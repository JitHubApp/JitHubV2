$script:NativeAotProvenanceFileName = 'native-aot-provenance.json'

function Get-NativeAotPayloadFileSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$InputPath)

    $root = [IO.Path]::GetFullPath($InputPath).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $rootPath = $root.TrimEnd([IO.Path]::DirectorySeparatorChar)
    $rootItem = Get-Item -LiteralPath $rootPath -Force -ErrorAction Stop
    if (-not $rootItem.PSIsContainer -or
        ($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "NativeAOT provenance root must be a regular directory: $rootPath"
    }

    $entries = @(Get-ChildItem -LiteralPath $rootPath -Recurse -Force -ErrorAction Stop)
    if ($entries.Count -gt 8192) {
        throw 'NativeAOT provenance artifact has too many entries to verify safely.'
    }
    $reparseEntries = @($entries | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    })
    if ($reparseEntries.Count -ne 0) {
        throw "Reparse-point entries are forbidden in NativeAOT provenance: $($reparseEntries.FullName -join ', ')"
    }

    $filePaths = @($entries |
        Where-Object { -not $_.PSIsContainer } |
        ForEach-Object { $_.FullName } |
        Where-Object {
            -not [string]::Equals(
                $_,
                (Join-Path $rootPath $script:NativeAotProvenanceFileName),
                [StringComparison]::OrdinalIgnoreCase)
        })
    [Array]::Sort($filePaths, [StringComparer]::Ordinal)

    $files = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
    $canonicalPathSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($filePath in $filePaths) {
        $relativePath = $filePath.Substring($root.Length).Replace('\', '/')
        if ([string]::IsNullOrWhiteSpace($relativePath) -or
            $relativePath.StartsWith('/') -or
            $relativePath.Contains(':') -or
            @($relativePath.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
            throw "NativeAOT provenance found an unsafe artifact-relative path: '$relativePath'."
        }
        if (-not $canonicalPathSet.Add($relativePath)) {
            throw "NativeAOT provenance found case-ambiguous artifact paths: '$relativePath'."
        }
        $files.Add($relativePath, (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant())
    }

    if (-not $files.ContainsKey('JitHub.WinUI.exe')) {
        throw "NativeAOT provenance payload is missing JitHub.WinUI.exe: $rootPath"
    }

    return ,$files
}

function Get-NativeAotPayloadManifestSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][Collections.IDictionary]$Files)

    $sorted = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($name in $Files.Keys) {
        $path = [string]$name
        $hash = [string]$Files[$name]
        if ([string]::IsNullOrWhiteSpace($path) -or
            $path.StartsWith('/') -or $path.Contains('\') -or $path.Contains(':') -or
            @($path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0 -or
            $hash -notmatch '^[0-9a-fA-F]{64}$') {
            throw 'NativeAOT provenance contains an invalid payload path or SHA-256 value.'
        }
        $sorted.Add($path, $hash.ToLowerInvariant())
    }

    if (-not $sorted.ContainsKey('JitHub.WinUI.exe')) {
        throw 'NativeAOT provenance payload manifest has no JitHub.WinUI.exe.'
    }

    $lines = @($sorted.GetEnumerator() | ForEach-Object { "$($_.Key)`0$($_.Value)" })
    return [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($lines -join "`n"))).ToLowerInvariant()
}

function New-NativeAotProvenance {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$InputPath,
        [Parameter(Mandatory = $true)][string]$SourceCommitSha,
        [Parameter(Mandatory = $true)][string]$WorkflowRunId,
        [Parameter(Mandatory = $true)][string]$WorkflowRepository,
        [Parameter(Mandatory = $true)][string]$WorkflowRef
    )

    if ($SourceCommitSha -notmatch '^[0-9a-fA-F]{40}$' -or
        $WorkflowRunId -notmatch '^[1-9][0-9]{0,18}$' -or
        [string]::IsNullOrWhiteSpace($WorkflowRepository) -or
        [string]::IsNullOrWhiteSpace($WorkflowRef)) {
        throw 'NativeAOT provenance requires a full commit ID and nonempty GitHub workflow identity.'
    }

    $files = Get-NativeAotPayloadFileSha256 -InputPath $InputPath
    [ordered]@{
        schemaVersion = 1
        provenanceVerified = $true
        workingTreeClean = $true
        sourceCommitSha = $SourceCommitSha.ToLowerInvariant()
        configuration = 'Release'
        runtimeFlavor = 'native-aot'
        architecture = 'x64'
        runtimeIdentifier = 'win-x64'
        executableName = 'JitHub.WinUI.exe'
        executableSha256 = $files['JitHub.WinUI.exe']
        workflowRunId = $WorkflowRunId
        workflowRepository = $WorkflowRepository.Trim()
        workflowRef = $WorkflowRef.Trim()
        artifactFileSha256 = $files
        artifactManifestSha256 = Get-NativeAotPayloadManifestSha256 -Files $files
    }
}

function Test-NativeAotProvenance {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$InputPath,
        [Parameter(Mandatory = $true)][string]$ProvenancePath,
        [string]$ExpectedSourceCommitSha = '',
        [string]$ExpectedWorkflowRunId = '',
        [string]$ExpectedWorkflowRepository = '',
        [string]$ExpectedWorkflowRef = ''
    )

    $rootPath = [IO.Path]::GetFullPath($InputPath).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $sidecarPath = [IO.Path]::GetFullPath($ProvenancePath)
    $expectedSidecarPath = [IO.Path]::GetFullPath((Join-Path $rootPath $script:NativeAotProvenanceFileName))
    if (-not [string]::Equals($sidecarPath, $expectedSidecarPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'NativeAOT provenance sidecar must be at the artifact root under its canonical file name.'
    }

    $sidecarItem = Get-Item -LiteralPath $sidecarPath -Force -ErrorAction Stop
    if ($sidecarItem.PSIsContainer -or
        ($sidecarItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $sidecarItem.Length -le 0 -or $sidecarItem.Length -gt 4MB) {
        throw 'NativeAOT provenance sidecar is missing, oversized, or not a regular file.'
    }

    $sidecarBytes = [IO.File]::ReadAllBytes($sidecarPath)
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    $provenance = $utf8.GetString($sidecarBytes) | ConvertFrom-Json -Depth 16
    if ($provenance.schemaVersion -ne 1 -or
        $provenance.provenanceVerified -ne $true -or
        $provenance.workingTreeClean -ne $true -or
        [string]$provenance.sourceCommitSha -notmatch '^[0-9a-fA-F]{40}$' -or
        [string]$provenance.configuration -cne 'Release' -or
        [string]$provenance.runtimeFlavor -cne 'native-aot' -or
        [string]$provenance.architecture -cne 'x64' -or
        [string]$provenance.runtimeIdentifier -cne 'win-x64' -or
        [string]$provenance.executableName -cne 'JitHub.WinUI.exe' -or
        [string]$provenance.executableSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        [string]$provenance.workflowRunId -notmatch '^[1-9][0-9]{0,18}$' -or
        [string]::IsNullOrWhiteSpace([string]$provenance.workflowRepository) -or
        [string]::IsNullOrWhiteSpace([string]$provenance.workflowRef) -or
        [string]$provenance.artifactManifestSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        $null -eq $provenance.artifactFileSha256) {
        throw 'NativeAOT provenance sidecar has invalid or unqualified build identity.'
    }

    foreach ($expected in @(
        @{ Name = 'source commit'; Actual = [string]$provenance.sourceCommitSha; Expected = $ExpectedSourceCommitSha },
        @{ Name = 'workflow run'; Actual = [string]$provenance.workflowRunId; Expected = $ExpectedWorkflowRunId },
        @{ Name = 'workflow repository'; Actual = [string]$provenance.workflowRepository; Expected = $ExpectedWorkflowRepository },
        @{ Name = 'workflow ref'; Actual = [string]$provenance.workflowRef; Expected = $ExpectedWorkflowRef }
    )) {
        if (-not [string]::IsNullOrWhiteSpace([string]$expected.Expected) -and
            -not [string]::Equals($expected.Actual, [string]$expected.Expected,
                $(if ($expected.Name -in @('source commit', 'workflow run')) {
                    [StringComparison]::OrdinalIgnoreCase
                } else {
                    [StringComparison]::Ordinal
                }))) {
            throw "NativeAOT provenance $($expected.Name) does not match this audit run."
        }
    }

    $reportedFiles = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
    $reportedPathSet = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $reportedFileProperties = @($provenance.artifactFileSha256.PSObject.Properties)
    if ($reportedFileProperties.Count -lt 1 -or $reportedFileProperties.Count -gt 4096) {
        throw 'NativeAOT provenance payload manifest has an invalid file count.'
    }
    foreach ($property in $reportedFileProperties) {
        $path = [string]$property.Name
        $hash = [string]$property.Value
        if ([string]::IsNullOrWhiteSpace($path) -or
            $path.StartsWith('/') -or $path.Contains('\') -or $path.Contains(':') -or
            @($path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0 -or
            $hash -notmatch '^[0-9a-fA-F]{64}$') {
            throw 'NativeAOT provenance payload manifest contains an invalid path or hash.'
        }
        if (-not $reportedPathSet.Add($path)) {
            throw "NativeAOT provenance payload manifest contains case-ambiguous paths: '$path'."
        }
        $reportedFiles.Add($path, $hash.ToLowerInvariant())
    }

    $reportedManifestSha256 = Get-NativeAotPayloadManifestSha256 -Files $reportedFiles
    if (-not [string]::Equals($reportedManifestSha256, [string]$provenance.artifactManifestSha256,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($reportedFiles['JitHub.WinUI.exe'], [string]$provenance.executableSha256,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'NativeAOT provenance payload manifest digest or executable hash is inconsistent.'
    }

    $actualFiles = Get-NativeAotPayloadFileSha256 -InputPath $rootPath
    if ($actualFiles.Count -ne $reportedFiles.Count) {
        throw 'NativeAOT provenance payload file count does not match the published artifact.'
    }
    foreach ($path in $reportedFiles.Keys) {
        if (-not $actualFiles.ContainsKey($path) -or
            -not [string]::Equals($actualFiles[$path], $reportedFiles[$path], [StringComparison]::OrdinalIgnoreCase)) {
            throw "NativeAOT provenance payload file does not match: '$path'."
        }
    }

    [pscustomobject]@{
        Provenance = $provenance
        Sha256 = (Get-FileHash -LiteralPath $sidecarPath -Algorithm SHA256).Hash.ToLowerInvariant()
        ArtifactManifestSha256 = $reportedManifestSha256
        ArtifactFileSha256 = $reportedFiles
    }
}

Export-ModuleMember -Function Get-NativeAotPayloadFileSha256, Get-NativeAotPayloadManifestSha256, New-NativeAotProvenance, Test-NativeAotProvenance
