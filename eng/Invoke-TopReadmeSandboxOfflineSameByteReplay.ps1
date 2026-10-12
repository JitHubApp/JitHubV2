[CmdletBinding()]
param(
    [ValidateRange(1, 500)]
    [int]$StartRank = 1,

    [ValidateRange(1, 500)]
    [int]$Count = 500,

    [ValidateRange(1, 25)]
    [int]$ShardSize = 25,

    [ValidateRange(64, 2048)]
    [int]$MaximumShardMiB = 1024,

    [Parameter(Mandatory = $true)]
    [string]$AppDirectory,

    [Parameter(Mandatory = $true)]
    [string]$AutomationRunnerDirectory,

    [Parameter(Mandatory = $true)]
    [string]$CaptureRoot,

    [string]$ManifestPath = '',

    [Parameter(Mandatory = $true)]
    [string]$NodeExecutablePath,

    [string]$EdgeExecutablePath = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',

    [string]$OutputRoot = '',

    [string]$WinAppPath = 'winapp',

    # Diagnostic attribution only; never usable as quiet-machine timing evidence.
    [switch]$SourceAdmissionTrace,

    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resolvedStartRank = $StartRank
$resolvedCount = $Count
if ($resolvedStartRank + $resolvedCount - 1 -gt 500) {
    throw 'The offline replay rank range must stay within 1..500.'
}
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $repoRoot 'eng\readme-audit\pinned-top500.json'
}
$ManifestPath = [IO.Path]::GetFullPath($ManifestPath)
$AppDirectory = [IO.Path]::GetFullPath($AppDirectory)
$AutomationRunnerDirectory = [IO.Path]::GetFullPath($AutomationRunnerDirectory)
$CaptureRoot = [IO.Path]::GetFullPath($CaptureRoot)
$NodeExecutablePath = [IO.Path]::GetFullPath($NodeExecutablePath)
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot ('artifacts\offline-same-byte-sandbox\run-' +
        [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)

$script:MaximumSourceFileBytes = 256MB
$script:MaximumSourceSnapshotBytes = 4GB
$script:MaximumAppPayloadBytes = 2GB
$script:MaximumRunnerPayloadBytes = 2GB
$script:MaximumCorpusFileBytes = 64MB
$script:MaximumCorpusFiles = 15016
$script:MaximumCorpusEntries = 16032
$script:MaximumCorpusBytes = 308MB
$script:ExcludedDirectoryNames = @(
    '.git', '.vs', '.codex', '.idea', 'bin', 'obj', 'artifacts',
    'node_modules', 'target', 'TestResults', 'packages', '.nuget')
$script:SensitiveExtensions = @('.pfx', '.p12', '.pem', '.key', '.env', '.netrc')
$script:CredentialFileNames = @(
    '.npmrc', '.git-credentials', 'secrets.json', 'credentials.json',
    'id_rsa', 'id_ed25519', 'known_hosts')

function Invoke-NativeAotArtifactVerifier {
    param(
        [Parameter(Mandatory = $true)][string]$VerifierPath,
        [Parameter(Mandatory = $true)][string]$InputPath
    )

    if (-not (Test-Path -LiteralPath $VerifierPath -PathType Leaf)) {
        throw "Native AOT artifact verifier is missing: '$VerifierPath'."
    }
    & $VerifierPath -InputPath $InputPath -Architecture x64
}

function Add-BoundedByteCount {
    param(
        [Parameter(Mandatory = $true)][long]$Current,
        [Parameter(Mandatory = $true)][long]$Additional,
        [Parameter(Mandatory = $true)][long]$Maximum,
        [Parameter(Mandatory = $true)][string]$Description
    )

    if ($Current -lt 0 -or $Additional -lt 0 -or $Current -gt $Maximum -or
        $Additional -gt ($Maximum - $Current)) {
        throw "$Description exceeds its $Maximum-byte bound."
    }
    return [long]($Current + $Additional)
}

function Assert-OrdinaryPath {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Description)

    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $current = $root
    $suffix = $full.Substring($root.Length)
    foreach ($segment in $suffix.Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) { break }
        $attributes = [IO.File]::GetAttributes($current)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description contains a redirected path component: '$current'."
        }
    }
    return $full
}

function Get-ReadmeFileDigests {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][long]$ExpectedByteSize)

    if ($ExpectedByteSize -le 0 -or $ExpectedByteSize -gt $script:MaximumCorpusFileBytes) {
        throw 'Pinned README byte size is outside the bounded same-byte corpus limit.'
    }
    $filePath = Assert-OrdinaryPath -Path $Path -Description 'Same-byte README'
    $fileInfo = [IO.FileInfo]::new($filePath)
    $fileInfo.Refresh()
    if (-not $fileInfo.Exists -or $fileInfo.Length -ne $ExpectedByteSize) {
        throw 'Same-byte README is missing or its length differs from the pinned manifest.'
    }

    $stream = [IO.File]::Open($filePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $sha256 = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    $gitSha1 = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA1)
    $buffer = [System.Buffers.ArrayPool[byte]]::Shared.Rent(64KB)
    try {
        $header = [Text.Encoding]::ASCII.GetBytes("blob $ExpectedByteSize`0")
        $gitSha1.AppendData($header)
        $readTotal = 0L
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $readTotal += $read
            if ($readTotal -gt $ExpectedByteSize) { throw 'Same-byte README grew while its identity was being verified.' }
            $sha256.AppendData($buffer, 0, $read)
            $gitSha1.AppendData($buffer, 0, $read)
        }
        if ($readTotal -ne $ExpectedByteSize) { throw 'Same-byte README changed while its identity was being verified.' }
        $fileInfo.Refresh()
        if ($fileInfo.Length -ne $ExpectedByteSize) { throw 'Same-byte README changed while its identity was being verified.' }
        return [pscustomobject]@{
            ByteSize = $readTotal
            Sha256 = [Convert]::ToHexString($sha256.GetHashAndReset()).ToLowerInvariant()
            GitBlobSha1 = [Convert]::ToHexString($gitSha1.GetHashAndReset()).ToLowerInvariant()
        }
    }
    finally {
        [System.Buffers.ArrayPool[byte]]::Shared.Return($buffer, $true)
        $gitSha1.Dispose()
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Assert-SameByteReadmeIdentity {
    param(
        [Parameter(Mandatory = $true)]$Repository,
        [Parameter(Mandatory = $true)]$BrowserCorpus,
        [Parameter(Mandatory = $true)]$CorpusManifest,
        [Parameter(Mandatory = $true)][string]$CorpusDirectory
    )

    $readmeSha1 = [string]$Repository.readme.sha
    $browserSha256 = [string]$BrowserCorpus.readmeSha256
    $manifestSha256 = [string]$CorpusManifest.readme.sha256
    $pinnedByteSize = [long]$Repository.readme.byteSize
    if ($readmeSha1 -notmatch '^[0-9a-fA-F]{40}$' -or
        $browserSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        $manifestSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        $browserSha256 -ine $manifestSha256 -or
        [long]$BrowserCorpus.readmeBytes -ne $pinnedByteSize -or
        [long]$CorpusManifest.readme.byteSize -ne $pinnedByteSize -or
        [string]$CorpusManifest.readme.file -cne 'readme.md' -or
        [string]$CorpusManifest.repository.readmeGitBlobSha1 -ine $readmeSha1) {
        throw 'Same-byte corpus metadata does not bind the pinned README identity.'
    }

    $readmePath = Join-Path $CorpusDirectory 'readme.md'
    $digests = Get-ReadmeFileDigests -Path $readmePath -ExpectedByteSize $pinnedByteSize
    if ($digests.Sha256 -ine $browserSha256 -or $digests.GitBlobSha1 -ine $readmeSha1) {
        throw 'Same-byte README bytes do not match the browser SHA-256 and pinned Git blob SHA-1.'
    }
}

function Get-OrdinaryTreeFiles {
    param([Parameter(Mandatory = $true)][string]$Root)

    $fullRoot = Assert-OrdinaryPath -Path $Root -Description 'Input tree'
    if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) {
        throw "Input directory is missing: '$fullRoot'."
    }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($fullRoot)
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    $entryCount = 0
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            $entryCount++
            if ($entryCount -gt 1000000) { throw "Input tree exceeded the one-million-entry safety limit: '$fullRoot'." }
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Input tree contains a redirected file or directory: '$entry'."
            }
            $name = [IO.Path]::GetFileName($entry)
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                if ($script:ExcludedDirectoryNames -contains $name) { continue }
                $pending.Push($entry)
            }
            else {
                $files.Add([IO.FileInfo]::new($entry))
            }
        }
    }
    return $files
}

function Test-SafeCredentialFilename {
    param([Parameter(Mandatory = $true)][string]$Path)

    $name = [IO.Path]::GetFileName($Path)
    return $script:CredentialFileNames -contains $name -or
        $script:SensitiveExtensions -contains [IO.Path]::GetExtension($name).ToLowerInvariant()
}

function Copy-VerifiedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][long]$MaximumBytes,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $sourcePath = Assert-OrdinaryPath -Path $Source -Description $Description
    $sourceInfo = [IO.FileInfo]::new($sourcePath)
    $sourceInfo.Refresh()
    if (-not $sourceInfo.Exists -or $sourceInfo.Length -lt 0 -or $sourceInfo.Length -gt $MaximumBytes) {
        throw "$Description is missing or exceeds its $MaximumBytes-byte transfer budget: '$sourcePath'."
    }
    if (Test-SafeCredentialFilename -Path $sourcePath) {
        throw "Refusing to stage a credential-shaped file: '$sourcePath'."
    }
    $destinationPath = [IO.Path]::GetFullPath($Destination)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
    if (Test-Path -LiteralPath $destinationPath) { throw "Staging destination already exists: '$destinationPath'." }

    $sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::Copy($sourcePath, $destinationPath, $false)
    [IO.File]::SetLastWriteTimeUtc($destinationPath, $sourceInfo.LastWriteTimeUtc)
    $destinationInfo = [IO.FileInfo]::new($destinationPath)
    $destinationInfo.Refresh()
    $destinationHash = (Get-FileHash -LiteralPath $destinationPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($destinationInfo.Length -ne $sourceInfo.Length -or $destinationHash -cne $sourceHash) {
        throw "$Description changed during staging: '$sourcePath'."
    }
    $sourceInfo.Refresh()
    if ($sourceInfo.Length -ne $destinationInfo.Length -or
        $sourceInfo.LastWriteTimeUtc -ne $destinationInfo.LastWriteTimeUtc) {
        throw "$Description changed while it was staged: '$sourcePath'."
    }
    return [pscustomobject]@{
        source = $sourcePath
        relativePath = $null
        byteSize = $destinationInfo.Length
        sha256 = $destinationHash
        lastWriteTimeUtc = $sourceInfo.LastWriteTimeUtc.ToString('O')
    }
}

function Get-CaseId {
    param([Parameter(Mandatory = $true)]$Repository)
    $safeName = [regex]::Replace([string]$Repository.fullName, '[^A-Za-z0-9]', '-')
    return ('{0:D3}-{1}' -f [int]$Repository.rank, $safeName)
}

function Get-PinnedRepositories {
    $manifestPath = Assert-OrdinaryPath -Path $ManifestPath -Description 'Pinned manifest'
    $manifestInfo = [IO.FileInfo]::new($manifestPath)
    $manifestInfo.Refresh()
    if (-not $manifestInfo.Exists -or $manifestInfo.Length -le 0 -or $manifestInfo.Length -gt 4MB) {
        throw 'Offline replay requires a bounded complete schema-v2 pinned manifest.'
    }
    $bytes = [IO.File]::ReadAllBytes($manifestPath)
    if ($bytes.Length -ne $manifestInfo.Length -or $bytes.Length -gt 4MB) {
        throw 'Pinned manifest changed while it was read.'
    }
    $sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    $canonicalManifestPath = Assert-OrdinaryPath `
        -Path (Join-Path $repoRoot 'eng\readme-audit\pinned-top500.json') `
        -Description 'Canonical pinned top-500 manifest'
    $canonicalInfo = [IO.FileInfo]::new($canonicalManifestPath)
    $canonicalInfo.Refresh()
    if (-not $canonicalInfo.Exists -or $canonicalInfo.Length -le 0 -or $canonicalInfo.Length -gt 4MB) {
        throw 'The repository-pinned top-500 manifest is missing or oversized.'
    }
    $canonicalBytes = [IO.File]::ReadAllBytes($canonicalManifestPath)
    if ($canonicalBytes.Length -ne $canonicalInfo.Length -or $canonicalBytes.Length -gt 4MB) {
        throw 'The repository-pinned top-500 manifest changed while it was read.'
    }
    $canonicalSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($canonicalBytes)).ToLowerInvariant()
    if ($sha256 -cne $canonicalSha256) {
        throw 'Offline replay requires the exact repository-pinned top-500 manifest bytes.'
    }
    $manifest = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -Depth 32
    if ($manifest.schemaVersion -ne 2 -or @($manifest.repositories).Count -ne 500 -or
        [string]::IsNullOrWhiteSpace([string]$manifest.query)) {
        throw 'Offline replay accepts only a complete schema-v2 pinned 500-repository manifest.'
    }
    $ordered = @($manifest.repositories | Sort-Object { [int]$_.rank })
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    for ($index = 0; $index -lt 500; $index++) {
        $repository = $ordered[$index]
        if ([int]$repository.rank -ne $index + 1 -or
            [string]$repository.fullName -notmatch '^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$' -or
            -not $names.Add([string]$repository.fullName) -or
            [string]$repository.commitSha -notmatch '^[0-9a-fA-F]{40}$' -or
            [string]$repository.url -notmatch '^https://github\.com/[^/\s]+/[^/\s]+/?$') {
            throw "Pinned manifest contains an invalid or duplicate repository identity at rank $($index + 1)."
        }
        if ($repository.readme.available -eq $true) {
            if ([string]$repository.readme.sha -notmatch '^[0-9a-fA-F]{40}$' -or
                [long]$repository.readme.byteSize -le 0 -or [long]$repository.readme.byteSize -gt 16MB -or
                [string]::IsNullOrWhiteSpace([string]$repository.readme.path) -or
                [string]$repository.readme.path -match '(^/|\\|(^|/)\.\.?(/|$)|:)') {
                throw "Pinned manifest contains invalid README identity at rank $($index + 1)."
            }
        }
        elseif ($repository.readme.byteSize -ne 0 -or $repository.readme.sha -or $repository.readme.path) {
            throw "Pinned manifest has inconsistent absent-README metadata at rank $($index + 1)."
        }
    }
    return [pscustomobject]@{ Manifest = $manifest; Bytes = $bytes; Sha256 = $sha256; Repositories = $ordered }
}

function Get-ValidatedCaptureCase {
    param([Parameter(Mandatory = $true)]$Repository)

    $caseId = Get-CaseId $Repository
    $browserDirectory = Join-Path $CaptureRoot ('cases\' + $caseId + '\browser')
    $browserDirectory = Assert-OrdinaryPath -Path $browserDirectory -Description "Captured browser evidence for rank $($Repository.rank)"
    if (-not (Test-Path -LiteralPath $browserDirectory -PathType Container)) {
        throw "Captured browser evidence is missing for rank $($Repository.rank): '$browserDirectory'."
    }
    $reportPath = Join-Path $browserDirectory 'browser.json'
    $reportPath = Assert-OrdinaryPath -Path $reportPath -Description 'Captured browser report'
    $reportInfo = [IO.FileInfo]::new($reportPath)
    $reportInfo.Refresh()
    if (-not $reportInfo.Exists -or $reportInfo.Length -le 0 -or $reportInfo.Length -gt 4MB) {
        throw "Captured browser report is missing or oversized at rank $($Repository.rank)."
    }
    $reportBytes = [IO.File]::ReadAllBytes($reportPath)
    if ($reportBytes.Length -ne $reportInfo.Length -or $reportBytes.Length -gt 4MB) {
        throw "Captured browser report changed while read at rank $($Repository.rank)."
    }
    $browser = [Text.Encoding]::UTF8.GetString($reportBytes) | ConvertFrom-Json -Depth 64
    if ($browser.schemaVersion -ne 5 -or $null -eq $browser.readmeRendered -or
        [string]$browser.readmeSha -ine [string]$Repository.readme.sha -or
        $null -eq $browser.sameByteCorpus -or $null -eq $browser.sameByteHtmlReplay) {
        throw "Captured browser evidence does not bind the pinned README at rank $($Repository.rank)."
    }
    $relativeManifest = [string]$browser.sameByteCorpus.manifest
    $normalizedRelativeManifest = $relativeManifest.Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($relativeManifest) -or [IO.Path]::IsPathRooted($relativeManifest) -or
        $relativeManifest.Contains(':') -or
        @($normalizedRelativeManifest.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -gt 0 -or
        [IO.Path]::GetFileName($normalizedRelativeManifest) -cne 'manifest.json') {
        throw "Captured corpus manifest path is invalid at rank $($Repository.rank)."
    }
    $browserPrefix = $browserDirectory.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $corpusManifestPath = [IO.Path]::GetFullPath((Join-Path $browserDirectory ($normalizedRelativeManifest -replace '/', [IO.Path]::DirectorySeparatorChar)))
    if (-not $corpusManifestPath.StartsWith($browserPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Captured corpus manifest escaped the browser case at rank $($Repository.rank)."
    }
    $corpusManifestPath = Assert-OrdinaryPath -Path $corpusManifestPath -Description 'Same-byte corpus manifest'
    $corpusDirectory = [IO.Path]::GetDirectoryName($corpusManifestPath)
    $manifestInfo = [IO.FileInfo]::new($corpusManifestPath)
    $manifestInfo.Refresh()
    if (-not $manifestInfo.Exists -or $manifestInfo.Length -le 0 -or $manifestInfo.Length -gt 4MB) {
        throw "Same-byte corpus manifest is missing or oversized at rank $($Repository.rank)."
    }
    $manifestBytes = [IO.File]::ReadAllBytes($corpusManifestPath)
    if ($manifestBytes.Length -ne $manifestInfo.Length -or $manifestBytes.Length -gt 4MB) {
        throw "Same-byte corpus manifest changed while read at rank $($Repository.rank)."
    }
    $manifestSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($manifestBytes)).ToLowerInvariant()
    if ($manifestSha -ine [string]$browser.sameByteCorpus.manifestSha256 -or
        [string]$browser.sameByteCorpus.readmeSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        [long]$browser.sameByteCorpus.readmeBytes -ne [long]$Repository.readme.byteSize) {
        throw "Same-byte corpus evidence does not match the pinned README at rank $($Repository.rank)."
    }
    $corpusManifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json -Depth 64
    if ($corpusManifest.schemaVersion -ne 2 -or $corpusManifest.complete -ne $true -or
        [string]$corpusManifest.repository.fullName -ine [string]$Repository.fullName -or
        [string]$corpusManifest.repository.commitSha -ine [string]$Repository.commitSha -or
        [string]$corpusManifest.repository.readmePath -cne [string]$Repository.readme.path -or
        [string]$corpusManifest.repository.readmeGitBlobSha1 -ine [string]$Repository.readme.sha -or
        [string]$corpusManifest.readme.file -cne 'readme.md' -or
        [string]$corpusManifest.readme.sha256 -ine [string]$browser.sameByteCorpus.readmeSha256 -or
        [long]$corpusManifest.readme.byteSize -ne [long]$browser.sameByteCorpus.readmeBytes) {
        throw "Same-byte corpus identity does not match the pinned commit and README at rank $($Repository.rank)."
    }
    Assert-SameByteReadmeIdentity -Repository $Repository -BrowserCorpus $browser.sameByteCorpus `
        -CorpusManifest $corpusManifest -CorpusDirectory $corpusDirectory
    $treeFiles = @(Get-OrdinaryTreeFiles -Root $corpusDirectory)
    if ($treeFiles.Count -gt $script:MaximumCorpusFiles) {
        throw "Same-byte corpus exceeds its file count bound at rank $($Repository.rank)."
    }
    $corpusBytes = 0L
    foreach ($file in $treeFiles) {
        $file.Refresh()
        if ($file.Length -lt 0 -or $file.Length -gt $script:MaximumCorpusFileBytes) {
            throw "Same-byte corpus contains an oversized file at rank $($Repository.rank)."
        }
        $corpusBytes = Add-BoundedByteCount -Current $corpusBytes -Additional ([long]$file.Length) `
            -Maximum $script:MaximumCorpusBytes -Description "Same-byte corpus at rank $($Repository.rank)"
    }
    if ($treeFiles.Count -gt $script:MaximumCorpusEntries -or $corpusBytes -gt $script:MaximumCorpusBytes) {
        throw "Same-byte corpus exceeds its aggregate transfer bound at rank $($Repository.rank)."
    }
    if ([string]$browser.readmeRendered -eq 'True') {
        if ([string]$corpusManifest.browserRender.status -cne 'passed' -or
            [string]$browser.sameByteHtmlReplay.status -cne 'passed') {
            throw "Rendered README lacks a qualified captured Edge corpus at rank $($Repository.rank)."
        }
    }
    elseif ([string]$corpusManifest.browserRender.status -cne 'not-applicable' -or
        [string]$corpusManifest.browserRender.reason -cne 'github-source-view' -or
        [string]$browser.sameByteHtmlReplay.status -cne 'not-applicable') {
        throw "Source-view README has an invalid not-applicable corpus at rank $($Repository.rank)."
    }
    return [pscustomobject]@{
        Rank = [int]$Repository.rank
        CaseId = $caseId
        BrowserDirectory = $browserDirectory
        BrowserReportPath = $reportPath
        BrowserReportSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($reportBytes)).ToLowerInvariant()
        CorpusDirectory = $corpusDirectory
        CorpusManifestRelativePath = $normalizedRelativeManifest
        CorpusManifestSha256 = $manifestSha
        CorpusBytes = $corpusBytes
        ReadmeRendered = [bool]$browser.readmeRendered
        Browser = $browser
    }
}

function Get-SelectedCapturePlan {
    param([Parameter(Mandatory = $true)]$Pinned)

    $selected = @($Pinned.Repositories | Where-Object {
        [int]$_.rank -ge $resolvedStartRank -and [int]$_.rank -lt ($resolvedStartRank + $resolvedCount)
    } | Sort-Object { [int]$_.rank })
    if ($selected.Count -ne $resolvedCount) { throw 'Pinned manifest does not contain every requested rank.' }
    $captures = @{}
    foreach ($repository in $selected) {
        if ($repository.readme.available -eq $true) {
            $captures[[int]$repository.rank] = Get-ValidatedCaptureCase -Repository $repository
        }
    }
    return [pscustomobject]@{ Repositories = $selected; Captures = $captures }
}

function New-Shards {
    param([Parameter(Mandatory = $true)]$Plan)

    $maxBytes = [long]$MaximumShardMiB * 1MB
    $shards = [Collections.Generic.List[object]]::new()
    $current = [Collections.Generic.List[object]]::new()
    $currentBytes = 0L
    foreach ($repository in $Plan.Repositories) {
        $capture = $Plan.Captures[[int]$repository.rank]
        $itemBytes = if ($null -eq $capture) { 0L } else {
            [long]$capture.CorpusBytes + [IO.FileInfo]::new($capture.BrowserReportPath).Length
        }
        if ($itemBytes -gt $maxBytes) { throw "Rank $($repository.rank) alone exceeds the maximum shard transfer budget." }
        if ($current.Count -gt 0 -and
            ($current.Count -ge $ShardSize -or $currentBytes + $itemBytes -gt $maxBytes)) {
            $shards.Add([pscustomobject]@{ Repositories = $current.ToArray(); Bytes = $currentBytes })
            $current = [Collections.Generic.List[object]]::new()
            $currentBytes = 0L
        }
        $current.Add($repository)
        $currentBytes = Add-BoundedByteCount -Current $currentBytes -Additional $itemBytes `
            -Maximum $maxBytes -Description 'Selected transfer shard'
    }
    if ($current.Count -gt 0) { $shards.Add([pscustomobject]@{ Repositories = $current.ToArray(); Bytes = $currentBytes }) }
    return $shards.ToArray()
}

function Test-SourceFileIncluded {
    param([Parameter(Mandatory = $true)][string]$RootName, [Parameter(Mandatory = $true)][IO.FileInfo]$File)

    if (Test-SafeCredentialFilename -Path $File.FullName) { return $false }
    $extension = $File.Extension.ToLowerInvariant()
    switch ($RootName) {
        'eng\readme-audit' { return $true }
        'JitHub.WinUI' {
            if ($extension -in @('.cs', '.xaml', '.csproj', '.props', '.targets', '.rs', '.toml', '.otf', '.resw')) { return $true }
            $relative = [IO.Path]::GetRelativePath((Join-Path $repoRoot 'JitHub.WinUI'), $File.FullName).Replace('/', '\')
            return $relative -in @('appsettings.json', 'Assets\CodeViewer\language-map.json')
        }
        'MarkdownRenderer' { return $extension -in @('.cs', '.xaml', '.csproj', '.props', '.targets', '.rs', '.toml', '.otf', '.json', '.lock', '.md') -or
            $File.Name -ceq 'MarkdownRenderer.Svg.Resvg.Worker.exe' }
        'JitHub.WinUI.Automation' { return $extension -in @('.cs', '.csproj', '.props', '.targets', '.manifest') }
        default { return $false }
    }
}

function Add-SourceTreeToSnapshot {
    param(
        [Parameter(Mandatory = $true)][string]$RootName,
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$SnapshotRoot,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][Collections.Generic.List[object]]$FileManifest,
        [Parameter(Mandatory = $true)][ref]$TotalBytes
    )

    $source = Assert-OrdinaryPath -Path $SourceRoot -Description "Source root $RootName"
    $files = Get-OrdinaryTreeFiles -Root $source
    foreach ($file in $files) {
        if (-not (Test-SourceFileIncluded -RootName $RootName -File $file)) { continue }
        $file.Refresh()
        if ($file.Length -gt $script:MaximumSourceFileBytes) { throw "Source file exceeds the transfer budget: '$($file.FullName)'." }
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        $destination = Join-Path (Join-Path $SnapshotRoot $RootName) $relative
        $record = Copy-VerifiedFile -Source $file.FullName -Destination $destination -MaximumBytes $script:MaximumSourceFileBytes -Description 'Source snapshot file'
        $record.relativePath = ($RootName.Replace('\', '/') + '/' + $relative.Replace('\', '/'))
        $FileManifest.Add($record)
        $TotalBytes.Value = Add-BoundedByteCount -Current ([long]$TotalBytes.Value) `
            -Additional ([long]$record.byteSize) -Maximum $script:MaximumSourceSnapshotBytes `
            -Description 'Filtered source snapshot'
    }
}

function New-SourceAndArtifactSnapshot {
    param([Parameter(Mandatory = $true)][string]$RunId)

    $snapshotRoot = Join-Path $OutputRoot ('staging\' + $RunId + '\repo')
    [IO.Directory]::CreateDirectory($snapshotRoot) | Out-Null
    $fileManifest = [Collections.Generic.List[object]]::new()
    $sourceBytes = 0L
    foreach ($rootName in @('JitHub.WinUI', 'MarkdownRenderer', 'JitHub.WinUI.Automation', 'eng\readme-audit')) {
        $sourceRoot = Join-Path $repoRoot $rootName
        if (-not (Test-Path -LiteralPath $sourceRoot -PathType Container)) { throw "Required source root is missing: '$sourceRoot'." }
        Add-SourceTreeToSnapshot -RootName $rootName -SourceRoot $sourceRoot -SnapshotRoot $snapshotRoot -FileManifest $fileManifest -TotalBytes ([ref]$sourceBytes)
    }
    foreach ($rootFile in @('JitHub.slnx', 'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json')) {
        $path = Join-Path $repoRoot $rootFile
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $record = Copy-VerifiedFile -Source $path -Destination (Join-Path $snapshotRoot $rootFile) -MaximumBytes 16MB -Description 'Repository build identity file'
            $record.relativePath = $rootFile
            $fileManifest.Add($record)
            $sourceBytes = Add-BoundedByteCount -Current $sourceBytes `
                -Additional ([long]$record.byteSize) -Maximum $script:MaximumSourceSnapshotBytes `
                -Description 'Filtered source snapshot including repository identity files'
        }
    }

    $artifactRoot = Join-Path $snapshotRoot 'artifacts\offline-same-byte-candidate'
    $artifactFiles = Get-OrdinaryTreeFiles -Root $AppDirectory
    if ($artifactFiles.Count -gt 8192) { throw 'NativeAOT application payload exceeds its entry-count bound.' }
    $artifactBytes = 0L
    foreach ($file in $artifactFiles) {
        $file.Refresh()
        $relative = [IO.Path]::GetRelativePath($AppDirectory, $file.FullName)
        $record = Copy-VerifiedFile -Source $file.FullName -Destination (Join-Path $artifactRoot $relative) -MaximumBytes $script:MaximumAppPayloadBytes -Description 'NativeAOT app payload'
        $record.relativePath = 'artifacts/offline-same-byte-candidate/' + $relative.Replace('\', '/')
        $fileManifest.Add($record)
        $artifactBytes = Add-BoundedByteCount -Current $artifactBytes -Additional ([long]$record.byteSize) `
            -Maximum $script:MaximumAppPayloadBytes -Description 'NativeAOT application payload'
    }
    if ($artifactBytes -gt $script:MaximumAppPayloadBytes) { throw 'NativeAOT application payload exceeds its 2-GiB transfer budget.' }

    $runnerFiles = Get-OrdinaryTreeFiles -Root $AutomationRunnerDirectory
    if ($runnerFiles.Count -gt 8192) { throw 'Self-contained Automation runner exceeds its entry-count bound.' }
    $runnerBytes = 0L
    $runnerDestinationRoot = Join-Path $snapshotRoot 'tools\automation'
    foreach ($file in $runnerFiles) {
        $file.Refresh()
        $relative = [IO.Path]::GetRelativePath($AutomationRunnerDirectory, $file.FullName)
        $record = Copy-VerifiedFile -Source $file.FullName -Destination (Join-Path $runnerDestinationRoot $relative) -MaximumBytes $script:MaximumRunnerPayloadBytes -Description 'Self-contained Automation runner'
        $record.relativePath = 'tools/automation/' + $relative.Replace('\', '/')
        $fileManifest.Add($record)
        $runnerBytes = Add-BoundedByteCount -Current $runnerBytes -Additional ([long]$record.byteSize) `
            -Maximum $script:MaximumRunnerPayloadBytes -Description 'Self-contained Automation runner'
    }
    if ($runnerBytes -gt $script:MaximumRunnerPayloadBytes) { throw 'Self-contained Automation runner exceeds its 2-GiB transfer budget.' }

    $nodeDestination = Join-Path $snapshotRoot 'tools\node.exe'
    $nodeRecord = Copy-VerifiedFile -Source $NodeExecutablePath -Destination $nodeDestination -MaximumBytes 128MB -Description 'Node.js executable'
    $nodeRecord.relativePath = 'tools/node.exe'
    $fileManifest.Add($nodeRecord)

    $runnerExe = Join-Path $runnerDestinationRoot 'JitHub.WinUI.Automation.exe'
    foreach ($required in @(
        $runnerExe,
        (Join-Path $runnerDestinationRoot 'JitHub.WinUI.Automation.dll'),
        (Join-Path $runnerDestinationRoot 'JitHub.WinUI.Automation.deps.json'),
        (Join-Path $runnerDestinationRoot 'JitHub.WinUI.Automation.runtimeconfig.json'),
        (Join-Path $runnerDestinationRoot 'coreclr.dll'),
        (Join-Path $runnerDestinationRoot 'hostfxr.dll'),
        (Join-Path $runnerDestinationRoot 'hostpolicy.dll'),
        (Join-Path $runnerDestinationRoot 'System.Private.CoreLib.dll'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Automation runner is not a self-contained Windows x64 publish; missing '$required'. Publish only JitHub.WinUI.Automation with -r win-x64 --self-contained true."
        }
    }
    $exeMachine = Get-PeMachine -Path $runnerExe
    if ($exeMachine -ne 0x8664) { throw 'Automation runner apphost is not x64.' }

    $manifest = [ordered]@{
        schemaVersion = 1
        scope = 'offline-same-byte-sandbox-source-and-artifact-snapshot'
        sourceCommitSha = Get-SourceCommitSha
        sourceWorkingTree = 'content-addressed-snapshot; dirty changes included only in allowlisted files'
        appArtifactSha256 = (Get-FileHash -LiteralPath (Join-Path $artifactRoot 'JitHub.WinUI.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        automationRunnerSha256 = (Get-FileHash -LiteralPath $runnerExe -Algorithm SHA256).Hash.ToLowerInvariant()
        nodeExecutableSha256 = (Get-FileHash -LiteralPath $nodeDestination -Algorithm SHA256).Hash.ToLowerInvariant()
        sourceSnapshotBytes = $sourceBytes
        appArtifactBytes = $artifactBytes
        automationRunnerBytes = $runnerBytes
        files = @($fileManifest | Sort-Object relativePath)
    }
    $snapshotManifestPath = Join-Path $OutputRoot ('source-snapshot-' + $RunId + '.json')
    $manifestJson = $manifest | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText($snapshotManifestPath, $manifestJson + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
    $manifestCopyPath = Join-Path $snapshotRoot 'artifacts\offline-same-byte-source-snapshot.json'
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($manifestCopyPath)) | Out-Null
    [IO.File]::Copy($snapshotManifestPath, $manifestCopyPath)
    return [pscustomobject]@{
        Root = $snapshotRoot
        RunnerRelativePath = 'tools/automation/JitHub.WinUI.Automation.exe'
        AppRelativePath = 'artifacts/offline-same-byte-candidate/JitHub.WinUI.exe'
        NodeRelativePath = 'tools/node.exe'
        Manifest = $manifest
        ManifestPath = $snapshotManifestPath
        ManifestSha256 = (Get-FileHash -LiteralPath $snapshotManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function Get-PeMachine {
    param([Parameter(Mandatory = $true)][string]$Path)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        try {
            if ($stream.Length -lt 256 -or $reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid PE executable: '$Path'." }
            $stream.Position = 0x3c
            $peOffset = $reader.ReadInt32()
            if ($peOffset -lt 0 -or $peOffset + 6 -gt $stream.Length) { throw "Invalid PE header in '$Path'." }
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) { throw "Invalid PE signature in '$Path'." }
            return [int]$reader.ReadUInt16()
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-SourceCommitSha {
    $commit = (& git -C $repoRoot rev-parse HEAD 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'Could not record the source checkout commit for the diagnostic run.'
    }
    return $commit.ToLowerInvariant()
}

function Invoke-WinApp {
    param([Parameter(Mandatory = $true)][string[]]$Arguments, [Parameter(Mandatory = $true)][string]$Description)
    Write-Host "[$Description] winapp $($Arguments -join ' ')"
    & $WinAppPath @Arguments
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "winapp $Description failed with exit code $exitCode; no host fallback was attempted." }
}

function Initialize-SandboxWorkRoot {
    param([Parameter(Mandatory = $true)]$Snapshot)
    $managedRootLiteral = ([string]$Snapshot.capabilities.managedRoot).Replace("'", "''")
    $workRootLiteral = ([string]$Snapshot.workRoot).Replace("'", "''")
    $guestScript = @"
`$ErrorActionPreference = 'Stop'
`$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
`$managedRoot = '$managedRootLiteral'
`$workRoot = '$workRootLiteral'
`$expectedWorkRoot = [IO.Path]::GetFullPath((Join-Path `$managedRoot 'work')).TrimEnd('\', '/')
if ([IO.Path]::GetFullPath(`$workRoot).TrimEnd('\', '/') -cne `$expectedWorkRoot) { throw 'Sandbox work root is not the expected managed-root child.' }
foreach (`$path in @(`$managedRoot, `$workRoot)) {
    `$item = Get-Item -LiteralPath `$path -Force -ErrorAction SilentlyContinue
    if (`$null -ne `$item -and -not `$item.PSIsContainer) { throw "Expected Sandbox directory path is occupied by a non-directory: `$path" }
    if (`$null -ne `$item -and (`$item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing a redirected Sandbox root: `$path" }
    [IO.Directory]::CreateDirectory(`$path) | Out-Null
    `$item = Get-Item -LiteralPath `$path -Force -ErrorAction Stop
    if (-not `$item.PSIsContainer -or (`$item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Sandbox directory initialization did not produce a plain directory: `$path" }
}
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($guestScript))
    # A fresh attached Sandbox advertises its managed work root before creating it.
    # Initialize only that expected root from winapp's default guest directory; use
    # --cwd only after this command verifies both roots are ordinary directories.
    Invoke-WinApp -Arguments @('target', 'exec', 'sandbox', '--', 'powershell.exe', '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded) -Description 'initialize expected Sandbox work root'
}

function New-GuestWindowsAppRuntimePreflightScript {
    param(
        [Parameter(Mandatory = $true)][string]$GuestRepoAbsolute,
        [Parameter(Mandatory = $true)][string]$ExpectedHelperSha256
    )

    if ($ExpectedHelperSha256 -notmatch '^[0-9a-fA-F]{64}$') {
        throw 'The staged Windows App Runtime helper must have a SHA-256 source-snapshot identity.'
    }
    $repoLiteral = $GuestRepoAbsolute.Replace("'", "''")
    $helperShaLiteral = $ExpectedHelperSha256.ToLowerInvariant()
    return @"
`$ErrorActionPreference = 'Stop'
`$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
`$repo = '$repoLiteral'
`$helperPath = Join-Path `$repo 'eng\readme-audit\Install-PinnedWindowsAppRuntime.ps1'
`$setupRoot = Join-Path `$repo 'artifacts\offline-same-byte-runtime-setup'
`$installerCache = Join-Path `$setupRoot 'installer-cache'
`$resultPath = Join-Path `$setupRoot 'windows-app-runtime.json'
`$expectedHelperSha256 = '$helperShaLiteral'
`$expectedInstallerSha256 = 'B8CDA840267AB72797F654F801F9A064AB6D9E508CEDEE3DF79F772F104DB6D6'
`$minimumVersion = [version]'8000.921.1539.0'
`$requiredFamilies = @(
    'Microsoft.WindowsAppRuntime.1.8_8wekyb3d8bbwe',
    'MicrosoftCorporationII.WinAppRuntime.Main.1.8_8wekyb3d8bbwe',
    'MicrosoftCorporationII.WinAppRuntime.Singleton_8wekyb3d8bbwe',
    'Microsoft.WinAppRuntime.DDLM.8000.921.1539.0-x6_8wekyb3d8bbwe')
`$result = [ordered]@{
    schemaVersion = 1
    scope = 'offline-same-byte-winui-runtime-prerequisite'
    requestedArchitecture = 'x64'
    minimumVersion = `$minimumVersion.ToString()
    requiredPackageFamilies = `$requiredFamilies
    helperSha256 = `$expectedHelperSha256
    installerSha256 = `$null
    action = 'not-started'
    status = 'failed'
    inventoryBefore = `$null
    inventoryAfter = `$null
    failure = `$null
    completedAtUtc = `$null
}

function Assert-PlainGuestPath {
    param([string]`$Root, [string]`$Path)
    `$rootFull = [IO.Path]::GetFullPath(`$Root).TrimEnd('\', '/')
    `$pathFull = [IO.Path]::GetFullPath(`$Path).TrimEnd('\', '/')
    if (-not `$pathFull.StartsWith(`$rootFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Windows App Runtime setup path escaped the unique staged repository.'
    }
    `$current = `$rootFull
    `$rootAttributes = [IO.File]::GetAttributes(`$current)
    if ((`$rootAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'The staged repository is a redirected path.' }
    foreach (`$segment in `$pathFull.Substring(`$rootFull.Length + 1).Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        `$current = Join-Path `$current `$segment
        if (-not (Test-Path -LiteralPath `$current)) { break }
        if (([IO.File]::GetAttributes(`$current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Windows App Runtime setup contains a redirected path: `$current"
        }
    }
    return `$pathFull
}

function Get-CurrentUserWindowsAppRuntimeInventory {
    `$packages = @(Get-AppxPackage)
    `$relevant = @(`$packages | Where-Object {
        `$_.Name -ceq 'Microsoft.WindowsAppRuntime.1.8' -or `$_.PackageFamilyName -in `$requiredFamilies
    } | ForEach-Object {
        [ordered]@{
            name = [string]`$_.Name
            version = [string]`$_.Version
            architecture = [string]`$_.Architecture
            packageFamilyName = [string]`$_.PackageFamilyName
            packageFullName = [string]`$_.PackageFullName
            status = [string]`$_.Status
        }
    })
    `$frameworks = @(`$packages | Where-Object {
        if (`$_.Name -cne 'Microsoft.WindowsAppRuntime.1.8' -or [string]`$_.Architecture -cne 'X64' -or [string]`$_.Status -cne 'Ok') { return `$false }
        try { return ([version]`$_.Version -ge `$minimumVersion) } catch { return `$false }
    })
    `$missingFamilies = @(`$requiredFamilies | Where-Object {
        `$family = `$_
        -not @(`$packages | Where-Object { `$_.PackageFamilyName -ceq `$family -and [string]`$_.Status -ceq 'Ok' }).Count
    })
    return [ordered]@{
        satisfied = (`$frameworks.Count -gt 0 -and `$missingFamilies.Count -eq 0)
        frameworkMatches = @(`$frameworks | ForEach-Object { [string]`$_.PackageFullName })
        missingPackageFamilies = `$missingFamilies
        packages = `$relevant
    }
}

try {
    if (-not [Environment]::Is64BitOperatingSystem -or -not [Environment]::Is64BitProcess) {
        throw 'The pinned x64 Windows App Runtime requires the existing x64 Sandbox process and operating system.'
    }
    `$setupRoot = Assert-PlainGuestPath -Root `$repo -Path `$setupRoot
    if (Test-Path -LiteralPath `$resultPath) { throw 'Runtime prerequisite evidence already exists in this unique run directory.' }
    [IO.Directory]::CreateDirectory(`$setupRoot) | Out-Null
    `$null = Assert-PlainGuestPath -Root `$repo -Path `$resultPath
    if (-not (Test-Path -LiteralPath `$helperPath -PathType Leaf)) { throw 'Pinned Windows App Runtime installer helper is missing from the staged source snapshot.' }
    `$actualHelperSha256 = (Get-FileHash -LiteralPath `$helperPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if (`$actualHelperSha256 -cne `$expectedHelperSha256) { throw 'Pinned Windows App Runtime helper differs from its source-snapshot SHA-256.' }

    `$before = Get-CurrentUserWindowsAppRuntimeInventory
    `$result.inventoryBefore = `$before
    if (`$before.satisfied) {
        `$result.action = 'reused-current-user-runtime'
        `$result.inventoryAfter = `$before
    }
    else {
        [IO.Directory]::CreateDirectory(`$installerCache) | Out-Null
        `$null = Assert-PlainGuestPath -Root `$repo -Path `$installerCache
        `$env:RUNNER_TEMP = `$installerCache
        & `$helperPath
        `$installerPath = Join-Path `$installerCache 'WindowsAppRuntimeInstall-1.8.260710003-x64.exe'
        if (-not (Test-Path -LiteralPath `$installerPath -PathType Leaf)) { throw 'Pinned Windows App Runtime helper did not leave its verified installer for evidence.' }
        `$installerSha256 = (Get-FileHash -LiteralPath `$installerPath -Algorithm SHA256).Hash.ToUpperInvariant()
        if (`$installerSha256 -cne `$expectedInstallerSha256) { throw 'Pinned Windows App Runtime installer hash differs after guest setup.' }
        `$result.installerSha256 = `$installerSha256.ToLowerInvariant()
        `$result.action = 'installed-pinned-current-user-runtime'
        `$after = Get-CurrentUserWindowsAppRuntimeInventory
        `$result.inventoryAfter = `$after
        if (-not `$after.satisfied) { throw 'Pinned Windows App Runtime installer completed without all required current-user x64 runtime packages.' }
    }
    `$result.status = 'ready'
}
catch {
    `$result.failure = `$_.Exception.Message
}
finally {
    `$result.completedAtUtc = [DateTimeOffset]::UtcNow
    [IO.Directory]::CreateDirectory(`$setupRoot) | Out-Null
    `$null = Assert-PlainGuestPath -Root `$repo -Path `$resultPath
    [IO.File]::WriteAllText(`$resultPath, ((`$result | ConvertTo-Json -Depth 12) + [Environment]::NewLine), [Text.UTF8Encoding]::new(`$false))
}
if (`$result.status -cne 'ready') {
    Write-Error -Message "Windows App Runtime preflight failed: `$(`$result.failure)" -ErrorAction Continue
    exit 1
}
"@
}

function Invoke-SandboxWindowsAppRuntimePreflight {
    param(
        [Parameter(Mandatory = $true)]$Snapshot,
        [Parameter(Mandatory = $true)][string]$GuestRepoAbsolute,
        [Parameter(Mandatory = $true)][string]$GuestRepoRelative,
        [Parameter(Mandatory = $true)]$SnapshotData,
        [Parameter(Mandatory = $true)][string]$HostEvidenceRoot,
        [Parameter(Mandatory = $true)][string]$GuestEpoch
    )

    try {
        $currentSnapshot = Get-AndAssertSandboxSnapshot
        if ([string]$currentSnapshot.executionTarget.epoch -cne $GuestEpoch -or
            [string]$currentSnapshot.workRoot -cne [string]$Snapshot.workRoot) {
            throw 'Sandbox epoch or work root changed before Windows App Runtime setup.'
        }
    }
    catch {
        return [pscustomobject]@{ Success = $false; Failure = $_.Exception.Message; EvidencePath = $null; EvidenceSha256 = $null; Report = $null }
    }

    $helperEntries = @($SnapshotData.Manifest.files | Where-Object {
        [string]$_.relativePath -ceq 'eng/readme-audit/Install-PinnedWindowsAppRuntime.ps1'
    })
    if ($helperEntries.Count -ne 1 -or [string]$helperEntries[0].sha256 -notmatch '^[0-9a-fA-F]{64}$') {
        return [pscustomobject]@{ Success = $false; Failure = 'The pinned Windows App Runtime helper is not uniquely bound by the source snapshot.'; EvidencePath = $null; EvidenceSha256 = $null; Report = $null }
    }
    $guestResultRelative = $GuestRepoRelative + '/artifacts/offline-same-byte-runtime-setup/windows-app-runtime.json'
    $hostResultPath = Join-Path $HostEvidenceRoot 'windows-app-runtime-preflight.json'
    $execFailure = $null
    $pullFailure = $null
    try {
        $guestScript = New-GuestWindowsAppRuntimePreflightScript -GuestRepoAbsolute $GuestRepoAbsolute -ExpectedHelperSha256 ([string]$helperEntries[0].sha256)
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($guestScript))
        Invoke-WinApp -Arguments @('target', 'exec', 'sandbox', '--cwd', $GuestRepoAbsolute, '--', 'powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', $encoded) -Description 'verify/install pinned x64 Windows App Runtime in Sandbox' | Out-Host
    }
    catch { $execFailure = $_.Exception.Message }
    try {
        if (Test-Path -LiteralPath $hostResultPath) { throw 'Refusing to overwrite runtime prerequisite evidence.' }
        Invoke-WinApp -Arguments @('target', 'pull', 'sandbox', $guestResultRelative, $hostResultPath) -Description 'pull Windows App Runtime setup inventory' | Out-Host
    }
    catch { $pullFailure = $_.Exception.Message }

    $report = $null
    $reportSha256 = $null
    $reportFailure = $null
    if (Test-Path -LiteralPath $hostResultPath -PathType Leaf) {
        try {
            $report = Get-Content -LiteralPath $hostResultPath -Raw | ConvertFrom-Json -Depth 16
            $reportSha256 = (Get-FileHash -LiteralPath $hostResultPath -Algorithm SHA256).Hash.ToLowerInvariant()
            if ([string]$report.helperSha256 -ine [string]$helperEntries[0].sha256 -or
                [string]$report.requestedArchitecture -cne 'x64' -or
                [string]$report.minimumVersion -cne '8000.921.1539.0' -or
                [string]$report.status -cne 'ready') {
                $reportFailure = 'Guest runtime prerequisite report did not verify the pinned helper, x64 architecture, minimum version, and ready state.'
            }
            else {
                $expectedFamilies = @(
                    'Microsoft.WindowsAppRuntime.1.8_8wekyb3d8bbwe',
                    'MicrosoftCorporationII.WinAppRuntime.Main.1.8_8wekyb3d8bbwe',
                    'MicrosoftCorporationII.WinAppRuntime.Singleton_8wekyb3d8bbwe',
                    'Microsoft.WinAppRuntime.DDLM.8000.921.1539.0-x6_8wekyb3d8bbwe')
                $reportedFamilies = @($report.requiredPackageFamilies | ForEach-Object { [string]$_ })
                $inventory = $report.inventoryAfter
                $runtimePackages = @($inventory.packages)
                $healthyFamilies = @($runtimePackages | Where-Object { [string]$_.status -ceq 'Ok' } |
                    ForEach-Object { [string]$_.packageFamilyName })
                $healthyFrameworks = @($runtimePackages | Where-Object {
                    [string]$_.name -ceq 'Microsoft.WindowsAppRuntime.1.8' -and
                    [string]$_.architecture -ceq 'X64' -and [string]$_.status -ceq 'Ok' -and
                    ([version]$_.version -ge [version]'8000.921.1539.0')
                })
                $requiredSetMatches = $reportedFamilies.Count -eq $expectedFamilies.Count
                if ($requiredSetMatches) {
                    foreach ($family in $expectedFamilies) {
                        if ($reportedFamilies -cnotcontains $family) { $requiredSetMatches = $false; break }
                    }
                }
                $missingHealthyFamilies = @($expectedFamilies | Where-Object { $healthyFamilies -cnotcontains $_ })
                if (-not $requiredSetMatches -or $null -eq $inventory -or
                    $inventory.satisfied -ne $true -or $healthyFrameworks.Count -eq 0 -or
                    $missingHealthyFamilies.Count -ne 0 -or @($inventory.missingPackageFamilies).Count -ne 0) {
                    $reportFailure = 'Guest runtime prerequisite report did not prove the complete healthy current-user x64 framework and package-family set.'
                }
            }
        }
        catch { $reportFailure = $_.Exception.Message }
    }
    if ($null -ne $execFailure -or $null -ne $pullFailure -or $null -ne $reportFailure -or $null -eq $report) {
        $failureParts = @($execFailure, $pullFailure, $reportFailure) | Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) }
        if ($null -eq $report) { $failureParts += 'Runtime prerequisite report was not delivered from the Sandbox.' }
        return [pscustomobject]@{ Success = $false; Failure = ($failureParts -join ' '); EvidencePath = $hostResultPath; EvidenceSha256 = $reportSha256; Report = $report }
    }
    return [pscustomobject]@{ Success = $true; Failure = $null; EvidencePath = $hostResultPath; EvidenceSha256 = $reportSha256; Report = $report }
}

function Get-AndAssertSandboxSnapshot {
    $raw = & $WinAppPath target snapshot sandbox --json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the existing Sandbox state. The script will not create or repair a Sandbox.' }
    $snapshot = ($raw -join [Environment]::NewLine) | ConvertFrom-Json -Depth 32
    if ($snapshot.executionTarget.kind -cne 'sandbox' -or
        $snapshot.running -ne $true -or $snapshot.attached -ne $true -or
        [string]$snapshot.capabilities.architecture -cne 'x64' -or
        $snapshot.capabilities.supportsInteractiveDesktop -ne $true -or
        $snapshot.desktop.effectiveInputReady -ne $true -or
        [string]::IsNullOrWhiteSpace([string]$snapshot.workRoot)) {
        throw 'Offline same-byte replay requires the already-running, attached x64 interactive Sandbox; no host/UI fallback is permitted.'
    }
    $managedRoot = [string]$snapshot.capabilities.managedRoot
    if (-not [IO.Path]::IsPathFullyQualified($managedRoot) -or -not [IO.Path]::IsPathFullyQualified([string]$snapshot.workRoot)) {
        throw 'Offline replay requires absolute Sandbox managed/work roots.'
    }
    $expectedWorkRoot = [IO.Path]::GetFullPath((Join-Path $managedRoot 'work')).TrimEnd('\', '/')
    $actualWorkRoot = [IO.Path]::GetFullPath([string]$snapshot.workRoot).TrimEnd('\', '/')
    if ($actualWorkRoot -cne $expectedWorkRoot) {
        throw 'Offline replay Sandbox work root is not the expected child of its managed root.'
    }
    return $snapshot
}

function Copy-ShardInput {
    param(
        [Parameter(Mandatory = $true)]$Shard,
        [Parameter(Mandatory = $true)]$Pinned,
        [Parameter(Mandatory = $true)]$Plan,
        [Parameter(Mandatory = $true)][string]$SnapshotRoot,
        [Parameter(Mandatory = $true)][string]$RunId,
        [Parameter(Mandatory = $true)][int]$ShardIndex
    )

    $firstRank = [int]$Shard.Repositories[0].rank
    $lastRank = [int]$Shard.Repositories[-1].rank
    $inputRoot = Join-Path $SnapshotRoot ('artifacts\offline-same-byte-input\ranks-{0:D3}-{1:D3}' -f $firstRank, $lastRank)
    [IO.Directory]::CreateDirectory((Join-Path $inputRoot 'cases')) | Out-Null
    $manifestDestination = Join-Path $inputRoot 'pinned-top500.json'
    [IO.File]::WriteAllBytes($manifestDestination, $Pinned.Bytes)
    foreach ($repository in $Shard.Repositories) {
        if ($repository.readme.available -ne $true) { continue }
        $capture = $Plan.Captures[[int]$repository.rank]
        $caseInput = Join-Path (Join-Path $inputRoot 'cases') $capture.CaseId
        $browserDestination = Join-Path $caseInput 'browser'
        [IO.Directory]::CreateDirectory($browserDestination) | Out-Null
        $browserReportDestination = Join-Path $browserDestination 'browser.json'
        $browserRecord = Copy-VerifiedFile -Source $capture.BrowserReportPath -Destination $browserReportDestination -MaximumBytes 4MB -Description 'Captured browser report'
        if ($browserRecord.sha256 -cne $capture.BrowserReportSha256) {
            throw "Captured browser evidence changed after validation at rank $($repository.rank)."
        }
        $manifestPath = Join-Path $capture.BrowserDirectory ($capture.CorpusManifestRelativePath -replace '/', [IO.Path]::DirectorySeparatorChar)
        $relativeCorpus = [IO.Path]::GetRelativePath($capture.BrowserDirectory, $capture.CorpusDirectory)
        $corpusDestination = Join-Path $browserDestination $relativeCorpus
        $files = Get-OrdinaryTreeFiles -Root $capture.CorpusDirectory
        foreach ($file in $files) {
            $relative = [IO.Path]::GetRelativePath($capture.CorpusDirectory, $file.FullName)
            $destination = Join-Path $corpusDestination $relative
            $null = Copy-VerifiedFile -Source $file.FullName -Destination $destination -MaximumBytes $script:MaximumCorpusFileBytes -Description 'Captured same-byte corpus file'
        }
        $copiedManifest = Join-Path $corpusDestination 'manifest.json'
        if (-not (Test-Path -LiteralPath $copiedManifest -PathType Leaf) -or
            (Get-FileHash -LiteralPath $copiedManifest -Algorithm SHA256).Hash -ine $capture.CorpusManifestSha256) {
            throw "Copied same-byte corpus manifest hash differs at rank $($repository.rank)."
        }
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw "Input corpus manifest disappeared at rank $($repository.rank)."
        }
    }
    return [pscustomobject]@{ Root = $inputRoot; FirstRank = $firstRank; LastRank = $lastRank }
}

function New-GuestBatchScript {
    param(
        [Parameter(Mandatory = $true)][string]$GuestRepo,
        [Parameter(Mandatory = $true)][string]$GuestWorkRoot,
        [Parameter(Mandatory = $true)][int]$Start,
        [Parameter(Mandatory = $true)][int]$Amount,
        [Parameter(Mandatory = $true)][string]$OutputRelativePath
    )

    $repo = Join-Path $GuestWorkRoot $GuestRepo
    $input = Join-Path $repo ('artifacts\offline-same-byte-input\ranks-{0:D3}-{1:D3}' -f $Start, ($Start + $Amount - 1))
    $output = Join-Path $repo $OutputRelativePath
    $runner = Join-Path $repo 'tools\automation\JitHub.WinUI.Automation.exe'
    $app = Join-Path $repo 'artifacts\offline-same-byte-candidate\JitHub.WinUI.exe'
    $manifest = Join-Path $input 'pinned-top500.json'
    $node = Join-Path $repo 'tools\node.exe'
    $browserScript = Join-Path $repo 'eng\readme-audit\same-byte-edge-source.mjs'
    $repoLiteral = $repo.Replace("'", "''")
    $inputLiteral = $input.Replace("'", "''")
    $outputLiteral = $output.Replace("'", "''")
    $runnerLiteral = $runner.Replace("'", "''")
    $appLiteral = $app.Replace("'", "''")
    $manifestLiteral = $manifest.Replace("'", "''")
    $nodeLiteral = $node.Replace("'", "''")
    $browserScriptLiteral = $browserScript.Replace("'", "''")
    $args = @(
        '--probe=readme-offline-same-byte',
        "--app=$app",
        "--out=$output",
        "--manifest=$manifest",
        "--offline-same-byte-corpus-root=$input",
        "--node=$node",
        "--browser-script=$browserScript",
        "--start-rank=$Start",
        "--count=$Amount")
    if (-not [string]::IsNullOrWhiteSpace($EdgeExecutablePath)) {
        $args += ('--edge=' + $EdgeExecutablePath)
    }
    $argumentLiteral = ($args | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ', '
    return @"
`$ErrorActionPreference = 'Stop'
`$repo = '$repoLiteral'
`$inputRoot = '$inputLiteral'
`$outputRoot = '$outputLiteral'
`$runner = '$runnerLiteral'
`$arguments = @($argumentLiteral)
`$required = @(`$runner, '$appLiteral', '$manifestLiteral', '$nodeLiteral', '$browserScriptLiteral')
foreach (`$path in `$required) { if (-not (Test-Path -LiteralPath `$path -PathType Leaf)) { throw "Required staged file is missing: `$path" } }
if (-not (Test-Path -LiteralPath `$inputRoot -PathType Container)) { throw 'Staged corpus input directory is missing.' }
function Read-CompletedLogLines {
    param(
        [Parameter(Mandatory = `$true)][string]`$Path,
        [Parameter(Mandatory = `$true)][ref]`$CommittedByteOffset,
        [Parameter(Mandatory = `$true)][ref]`$PendingBytes,
        [switch]`$FlushIncompleteLine
    )

    `$pending = [byte[]]`$PendingBytes.Value
    `$maximumBytesPerPoll = 1048576
    `$maximumLineBytes = 1048576
    if (-not (Test-Path -LiteralPath `$Path -PathType Leaf)) {
        if ([long]`$CommittedByteOffset.Value -ne 0 -or `$pending.Length -ne 0) {
            throw 'Offline same-byte runner stdout disappeared while being monitored.'
        }
        return
    }
    `$stream = [IO.File]::Open(`$Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        `$expectedPosition = [long]`$CommittedByteOffset.Value + [long]`$pending.Length
        `$snapshotLength = [long]`$stream.Length
        if (`$snapshotLength -lt `$expectedPosition) {
            throw 'Offline same-byte runner stdout was truncated while being monitored.'
        }
        `$stream.Position = `$expectedPosition
        `$remainingAtSnapshot = [long][Math]::Min([long]`$maximumBytesPerPoll, `$snapshotLength - `$expectedPosition)
        `$lineBytes = [Collections.Generic.List[byte]]::new()
        foreach (`$value in `$pending) { `$lineBytes.Add(`$value) }
        `$completed = [Collections.Generic.List[string]]::new()
        `$buffer = [byte[]]::new(65536)
        while (`$remainingAtSnapshot -gt 0) {
            `$request = [int][Math]::Min([long]`$buffer.Length, `$remainingAtSnapshot)
            `$read = `$stream.Read(`$buffer, 0, `$request)
            if (`$read -le 0) { throw 'Offline same-byte runner stdout changed or truncated during a bounded read.' }
            `$remainingAtSnapshot -= `$read
            for (`$index = 0; `$index -lt `$read; `$index++) {
                `$value = `$buffer[`$index]
                if (`$value -eq 10) {
                    `$line = `$lineBytes.ToArray()
                    `$lineLength = `$line.Length
                    if (`$lineLength -gt 0 -and `$line[`$lineLength - 1] -eq 13) { `$lineLength-- }
                    `$completed.Add([Text.Encoding]::UTF8.GetString(`$line, 0, `$lineLength))
                    `$CommittedByteOffset.Value = [long]`$CommittedByteOffset.Value + `$lineBytes.Count + 1
                    `$lineBytes.Clear()
                }
                else {
                    if (`$lineBytes.Count -ge `$maximumLineBytes) {
                        throw 'Offline same-byte runner stdout contained a line larger than its 1 MiB progress bound.'
                    }
                    `$lineBytes.Add(`$value)
                }
            }
        }

        if (`$FlushIncompleteLine -and `$lineBytes.Count -gt 0) {
            `$line = `$lineBytes.ToArray()
            `$completed.Add([Text.Encoding]::UTF8.GetString(`$line))
            `$CommittedByteOffset.Value = [long]`$CommittedByteOffset.Value + `$lineBytes.Count
            `$lineBytes.Clear()
        }
        `$PendingBytes.Value = `$lineBytes.ToArray()
        return `$completed.ToArray()
    }
    finally { `$stream.Dispose() }
}

function Update-RunnerProgress {
    param([Parameter(Mandatory = `$true)][AllowEmptyString()][string]`$Line)
    if (`$Line -match '^Offline same-byte README (\d+)/500: (.+) \(') {
        `$script:currentCase = "rank `$(`$Matches[1]): `$(`$Matches[2])"
        `$script:caseStarted = [DateTimeOffset]::UtcNow
        `$script:nativeActive = `$false
        `$script:lastStage = ''
    }
    if (`$Line -match '^README native audit stage: (.+)\.$' -or `$Line -match '^README native traversal (?:starting|viewport|revisit|movement|complete)') {
        `$script:nativeActive = `$true
        `$script:lastNativeProgress = [DateTimeOffset]::UtcNow
        `$script:lastStage = `$Line
    }
    elseif (`$Line -match '^README native audit complete\.$' -or `$Line -match '^Offline same-byte README \d+/500: .+ \((?:passed|failed|not-applicable-[^)]+)\)\.$') {
        `$script:nativeActive = `$false
        `$script:lastStage = ''
    }
}
function ConvertTo-WindowsCommandLineArgument {
    param([Parameter(Mandatory = `$true)][AllowEmptyString()][string]`$Argument)

    # Start-Process joins ArgumentList elements with spaces; it does not quote
    # embedded whitespace. Build the Windows argv string explicitly so paths
    # with spaces, quotes, or trailing backslashes remain one argument.
    `$builder = [Text.StringBuilder]::new()
    [void]`$builder.Append([char]'"')
    `$backslashCount = 0
    foreach (`$character in `$Argument.ToCharArray()) {
        if (`$character -eq [char]'\') { `$backslashCount++; continue }
        if (`$character -eq [char]'"') {
            if (`$backslashCount -gt 0) { [void]`$builder.Append([char]'\', 2 * `$backslashCount) }
            [void]`$builder.Append([char]'\')
            [void]`$builder.Append([char]'"')
            `$backslashCount = 0
            continue
        }
        if (`$backslashCount -gt 0) { [void]`$builder.Append([char]'\', `$backslashCount) }
        [void]`$builder.Append(`$character)
        `$backslashCount = 0
    }
    if (`$backslashCount -gt 0) { [void]`$builder.Append([char]'\', 2 * `$backslashCount) }
    [void]`$builder.Append([char]'"')
    return `$builder.ToString()
}
function Get-BoundedLogTail {
    param([Parameter(Mandatory = `$true)][string]`$Path)

    `$maximumBytes = 32768
    if (-not (Test-Path -LiteralPath `$Path -PathType Leaf)) {
        return [pscustomobject]@{ TotalBytes = 0L; BytesRead = 0; Text = '' }
    }
    `$stream = [IO.File]::Open(`$Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        `$totalBytes = [long]`$stream.Length
        `$start = [Math]::Max(0L, `$totalBytes - `$maximumBytes)
        `$stream.Position = `$start
        `$bytesToRead = [int](`$totalBytes - `$start)
        `$buffer = [byte[]]::new(`$bytesToRead)
        `$readTotal = 0
        while (`$readTotal -lt `$bytesToRead) {
            `$read = `$stream.Read(`$buffer, `$readTotal, `$bytesToRead - `$readTotal)
            if (`$read -le 0) { throw 'Runner log changed while reading its bounded diagnostic tail.' }
            `$readTotal += `$read
        }
        `$offset = 0
        `$partialLine = `$false
        if (`$start -gt 0) {
            `$newline = [Array]::IndexOf(`$buffer, [byte]10)
            if (`$newline -ge 0) { `$offset = `$newline + 1 }
            else { `$partialLine = `$true }
        }
        `$text = [Text.Encoding]::UTF8.GetString(`$buffer, `$offset, `$buffer.Length - `$offset)
        if (`$partialLine) { `$text = '[partial line tail] ' + `$text }
        return [pscustomobject]@{ TotalBytes = `$totalBytes; BytesRead = `$readTotal; Text = `$text }
    }
    finally { `$stream.Dispose() }
}
`$credentialNames = @('JITHUB_README_AUDIT_GITHUB_TOKEN','JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID','JITHUB_README_AUDIT_SEMANTIC_HMAC_KEY','GITHUB_TOKEN','GH_TOKEN','GH_ENTERPRISE_TOKEN','GITHUB_ENTERPRISE_TOKEN','ACTIONS_ID_TOKEN_REQUEST_TOKEN','ACTIONS_ID_TOKEN_REQUEST_URL','GITHUB_APP_TOKEN','GITHUB_ACCESS_TOKEN','GITHUB_CLIENT_SECRET','GITHUB_APP_PRIVATE_KEY','GH_ACCESS_TOKEN','GH_CONFIG_DIR')
foreach (`$name in `$credentialNames) { Remove-Item -LiteralPath ('Env:' + `$name) -ErrorAction SilentlyContinue }
foreach (`$name in `$credentialNames) { if (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable(`$name, 'Process'))) { throw 'Audit credential environment could not be cleared.' } }
`$edge = '$($EdgeExecutablePath.Replace("'", "''"))'
if (-not [string]::IsNullOrWhiteSpace(`$edge) -and -not (Test-Path -LiteralPath `$edge -PathType Leaf)) { throw "Edge is not installed at the required guest path: `$edge" }
`$version = if ([string]::IsNullOrWhiteSpace(`$edge)) { 'auto-detect' } else { (Get-Item -LiteralPath `$edge).VersionInfo.FileVersion }
`$env:JITHUB_README_AUDIT_RASTER_DIAGNOSTICS = `$null
`$env:JITHUB_README_AUDIT_IMAGE_TRANSPORT_DIAGNOSTICS = `$null
`$env:JITHUB_README_AUDIT_SVG_STAGE_DIAGNOSTICS = `$null
`$env:JITHUB_MARKDOWN_SOURCE_ADMISSION_TRACE = $(if ($SourceAdmissionTrace) { "'1'" } else { '$null' })
`$env:JITHUB_AUTOMATION_MARKDOWN_TARGET = `$null
`$env:JITHUB_AUTOMATION_MARKDOWN_RESUME = `$null
`$env:JITHUB_AUTOMATION_MARKDOWN_SUPPLEMENTAL = `$null
`$env:GITHUB_ACTIONS = `$null
`$env:GITHUB_SHA = `$null
`$env:GITHUB_RUN_ID = `$null
`$env:GITHUB_REPOSITORY = `$null
`$env:GITHUB_REF = `$null
`$env:GITHUB_WORKFLOW = `$null
`$env:GITHUB_JOB = `$null
`$env:GITHUB_EVENT_PATH = `$null
`$env:GITHUB_ENV = `$null
`$env:GITHUB_OUTPUT = `$null
`$env:GITHUB_PATH = `$null
`$env:GITHUB_STEP_SUMMARY = `$null
`$env:GH_TOKEN = `$null
`$env:GH_ENTERPRISE_TOKEN = `$null
`$env:GITHUB_TOKEN = `$null
`$env:GITHUB_ENTERPRISE_TOKEN = `$null
`$env:ACTIONS_ID_TOKEN_REQUEST_TOKEN = `$null
`$env:ACTIONS_ID_TOKEN_REQUEST_URL = `$null
`$env:JITHUB_README_AUDIT_GITHUB_TOKEN = `$null
`$env:JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID = `$null
`$env:JITHUB_README_AUDIT_SEMANTIC_HMAC_KEY = `$null
`$env:GH_CONFIG_DIR = `$null
New-Item -ItemType Directory -Force -Path `$outputRoot | Out-Null
`$stdout = Join-Path `$outputRoot 'runner.stdout.log'
`$stderr = Join-Path `$outputRoot 'runner.stderr.log'
`$runnerArgumentLine = (`$arguments | ForEach-Object { ConvertTo-WindowsCommandLineArgument -Argument `$_ }) -join ' '
`$runnerCommandLineLength = 1 + `$runner.Length + 3 + `$runnerArgumentLine.Length
if (`$runnerCommandLineLength -ge 32767) { throw "Offline same-byte runner command line exceeds the Windows 32767-character limit (`$runnerCommandLineLength characters)." }
`$process = Start-Process -FilePath `$runner -ArgumentList `$runnerArgumentLine -WorkingDirectory `$repo -NoNewWindow -PassThru -RedirectStandardOutput `$stdout -RedirectStandardError `$stderr
function Stop-OwnedRunnerTree {
    param([Parameter(Mandatory = `$true)][Diagnostics.Process]`$OwnedProcess)
    try {
        `$OwnedProcess.Refresh()
        if (-not `$OwnedProcess.HasExited) {
            & taskkill.exe /PID `$OwnedProcess.Id /T /F | Out-Null
            `$null = `$OwnedProcess.WaitForExit(5000)
            `$OwnedProcess.Refresh()
        }
        return `$OwnedProcess.HasExited
    }
    catch { return `$false }
}
`$currentCase = 'starting'
`$caseStarted = [DateTimeOffset]::UtcNow
`$nativeActive = `$false
`$lastNativeProgress = [DateTimeOffset]::UtcNow
`$lastStage = ''
`$processedOutputLength = 0L
`$pendingOutputBytes = [byte[]]::new(0)
try {
    # Windows PowerShell 5.1 can lose ExitCode/ExitTime from a redirected
    # Start-Process result unless its native process handle is materialized
    # while the process is still running.
    `$runnerProcessHandle = `$process.SafeHandle
    if (`$null -eq `$runnerProcessHandle -or `$runnerProcessHandle.IsInvalid) {
        throw 'Could not retain the owned offline same-byte runner process handle.'
    }
    while (-not `$process.HasExited) {
        Start-Sleep -Milliseconds 500
        `$process.Refresh()
        if (Test-Path -LiteralPath `$stdout -PathType Leaf) {
            `$info = [IO.FileInfo]::new(`$stdout)
            `$info.Refresh()
            if (`$info.Length -lt (`$processedOutputLength + `$pendingOutputBytes.Length)) {
                throw 'Offline same-byte runner stdout length moved backwards.'
            }
            `$lines = @(Read-CompletedLogLines -Path `$stdout -CommittedByteOffset ([ref]`$processedOutputLength) -PendingBytes ([ref]`$pendingOutputBytes))
            foreach (`$line in `$lines) { Update-RunnerProgress -Line `$line }
        }
        `$now = [DateTimeOffset]::UtcNow
        if ((`$now - `$script:caseStarted).TotalMinutes -ge 30) {
            throw "Offline same-byte case exceeded its 30-minute deadline at `$script:currentCase."
        }
        if (`$script:nativeActive -and (`$now - `$script:lastNativeProgress).TotalSeconds -ge 90) {
            throw "Offline same-byte native traversal made no progress for 90 seconds at `$script:currentCase; last stage `$script:lastStage."
        }
    }
    `$process.WaitForExit()
    `$process.Refresh()
    while (`$true) {
        `$consumedBeforeRead = [long]`$processedOutputLength + [long]`$pendingOutputBytes.Length
        foreach (`$line in @(Read-CompletedLogLines -Path `$stdout -CommittedByteOffset ([ref]`$processedOutputLength) -PendingBytes ([ref]`$pendingOutputBytes))) { Update-RunnerProgress -Line `$line }
        `$finalInfo = [IO.FileInfo]::new(`$stdout)
        `$finalInfo.Refresh()
        `$consumedAfterRead = [long]`$processedOutputLength + [long]`$pendingOutputBytes.Length
        if (-not `$finalInfo.Exists -or `$consumedAfterRead -eq `$finalInfo.Length) { break }
        if (`$consumedAfterRead -le `$consumedBeforeRead -or `$consumedAfterRead -gt `$finalInfo.Length) {
            throw 'Offline same-byte runner stdout changed during final bounded drain.'
        }
    }
    foreach (`$line in @(Read-CompletedLogLines -Path `$stdout -CommittedByteOffset ([ref]`$processedOutputLength) -PendingBytes ([ref]`$pendingOutputBytes) -FlushIncompleteLine)) { Update-RunnerProgress -Line `$line }
    `$rawExitCode = `$process.ExitCode
    if (`$null -eq `$rawExitCode) { throw 'Offline same-byte runner exit code was unavailable after the process exited.' }
    `$exitCode = [int]`$rawExitCode
}
catch {
    `$runnerFailure = `$_
    if (-not (Stop-OwnedRunnerTree -OwnedProcess `$process)) {
        throw "Offline same-byte monitoring failed and owned runner PID `$(`$process.Id) could not be confirmed stopped. Original error: `$(`$runnerFailure.Exception.Message)"
    }
    throw
}
finally {
    if (`$null -ne `$process) { `$process.Dispose() }
}
foreach (`$log in @(@{ Name = 'stdout'; Path = `$stdout }, @{ Name = 'stderr'; Path = `$stderr })) {
    `$tail = Get-BoundedLogTail -Path `$log.Path
    if (`$tail.TotalBytes -gt 0) {
        `$truncated = if (`$tail.TotalBytes -gt `$tail.BytesRead) { ' (tail only)' } else { '' }
        Write-Output ("runner `$(`$log.Name) log`$truncated; `$(`$tail.TotalBytes) bytes retained:")
        if (-not [string]::IsNullOrEmpty([string]`$tail.Text)) { Write-Output `$tail.Text }
    }
}
Write-Output "offlineSameByteGuestRunnerExitCode=`$exitCode; edgeVersion=`$version; requestedRanks=$Start..$($Start + $Amount - 1); reportRoot=`$outputRoot"
exit `$exitCode
"@
}

function New-GuestBatchExecArguments {
    param(
        [Parameter(Mandatory = $true)][string]$GuestCwd,
        [Parameter(Mandatory = $true)][string]$GuestScriptPath
    )

    if ([string]::IsNullOrWhiteSpace($GuestCwd) -or [string]::IsNullOrWhiteSpace($GuestScriptPath)) {
        throw 'The offline guest runner requires a working directory and a staged script path.'
    }
    $guestProcessArguments = @('powershell.exe', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $GuestScriptPath)
    $quotedGuestArguments = @($guestProcessArguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
    })
    $guestCommandLineLength = (($quotedGuestArguments -join ' ').Length + 1)
    if ($guestCommandLineLength -ge 32767) {
        throw "The offline guest PowerShell command line exceeds the Windows 32767-character process limit ($guestCommandLineLength characters)."
    }
    return @('target', 'exec', 'sandbox', '--cwd', $GuestCwd, '--') + $guestProcessArguments
}

function ConvertTo-GuestPowerShellSingleQuotedLiteral {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

function Get-Percentile {
    param([double[]]$Sorted, [double]$Percentile)
    if ($Sorted.Count -eq 0) { return 0.0 }
    $position = [Math]::Clamp($Percentile, 0.0, 1.0) * ($Sorted.Count - 1)
    $lower = [int][Math]::Floor($position)
    $upper = [int][Math]::Ceiling($position)
    if ($lower -eq $upper) { return $Sorted[$lower] }
    return $Sorted[$lower] + (($Sorted[$upper] - $Sorted[$lower]) * ($position - $lower))
}

function Get-OfflineAggregate {
    param([Parameter(Mandatory = $true)]$Pinned, [Parameter(Mandatory = $true)]$Plan, [Parameter(Mandatory = $true)]$Shards, [Parameter(Mandatory = $true)][string]$EvidenceRoot, [Parameter(Mandatory = $true)][int]$CompletedShardCount, [Parameter(Mandatory = $true)][AllowEmptyCollection()][Collections.Generic.List[string]]$Failures)

    $expectedCases = @($Plan.Repositories)
    $caseResults = [Collections.Generic.List[object]]::new()
    foreach ($repository in $expectedCases) {
        $caseId = Get-CaseId $repository
        $resultFiles = @(Get-ChildItem -LiteralPath $EvidenceRoot -Recurse -File -Filter 'offline-case-result.json' -ErrorAction SilentlyContinue | Where-Object {
            $_.FullName -match ('[\\/]cases[\\/]' + [regex]::Escape($caseId) + '[\\/]offline-case-result\.json$')
        })
        if ($resultFiles.Count -ne 1) {
            $Failures.Add("Rank $($repository.rank) has $($resultFiles.Count) offline case result records.")
            continue
        }
        $result = Get-Content -LiteralPath $resultFiles[0].FullName -Raw | ConvertFrom-Json -Depth 64
        if ([int]$result.rank -ne [int]$repository.rank -or [string]$result.fullName -ine [string]$repository.fullName -or
            [string]$result.commitSha -ine [string]$repository.commitSha -or
            [string]$result.readmeSha -ine $(if ($repository.readme.available -eq $true) { [string]$repository.readme.sha } else { 'absent' })) {
            $Failures.Add("Rank $($repository.rank) result identity differs from the pinned manifest.")
        }
        if ([string]$result.status -eq 'failed' -or @($result.failures).Count -gt 0) {
            $Failures.Add("Rank $($repository.rank) failed its offline per-case gates.")
        }
        if ($repository.readme.available -eq $true -and $result.browser -and $result.browser.readmeRendered -ne $false) {
            $comparison = $result.comparison
            if ($null -eq $comparison -or
                $null -eq $comparison.nativeToSameByteFirstRenderRatio -or
                $null -eq $comparison.nativeToSameByteFullPageRatio -or
                [double]$comparison.textTokenCoverage -lt 0.985 -or
                [double]$comparison.visualStructureScore -lt 0.95 -or
                $null -eq $comparison.sourceBoundLayoutExtentRatio -or
                [double]$comparison.sourceBoundLayoutExtentRatio -lt 0.90 -or
                [double]$comparison.sourceBoundLayoutExtentRatio -gt 1.10 -or
                [double]$comparison.sameByteTileCoverage -lt 1.0) {
                $Failures.Add("Rank $($repository.rank) lacks complete full-page source-bound fidelity or timing evidence.")
            }
        }
        $result | Add-Member -NotePropertyName hostEvidencePath -NotePropertyValue $resultFiles[0].FullName -Force
        $caseResults.Add($result)
    }

    $fullTop500 = $resolvedStartRank -eq 1 -and $resolvedCount -eq 500
    $rendered = @($caseResults | Where-Object {
        $_.browser -and $_.browser.readmeRendered -ne $false -and $_.comparison
    })
    $expectedRendered = @($expectedCases | Where-Object {
        $_.readme.available -eq $true -and $Plan.Captures[[int]$_.rank].ReadmeRendered -ne $false
    }).Count
    $first = @($rendered | ForEach-Object { $_.comparison.nativeToSameByteFirstRenderRatio } |
        Where-Object { $null -ne $_ -and [double]::IsFinite([double]$_) -and [double]$_ -gt 0 } |
        ForEach-Object { [double]$_ } | Sort-Object)
    $full = @($rendered | ForEach-Object { $_.comparison.nativeToSameByteFullPageRatio } |
        Where-Object { $null -ne $_ -and [double]::IsFinite([double]$_) -and [double]$_ -gt 0 } |
        ForEach-Object { [double]$_ } | Sort-Object)
    $aggregateFailures = [Collections.Generic.List[string]]::new()
    if ($fullTop500 -and ($first.Count -ne $expectedRendered -or $full.Count -ne $expectedRendered)) {
        $aggregateFailures.Add("Full top-500 same-byte timing set is incomplete: first $($first.Count)/$expectedRendered, full $($full.Count)/$expectedRendered.")
    }
    $firstP95 = Get-Percentile -Sorted ([double[]]$first) -Percentile 0.95
    $fullP95 = Get-Percentile -Sorted ([double[]]$full) -Percentile 0.95
    if ($fullTop500 -and $first.Count -eq $expectedRendered -and $firstP95 -gt 1.10) {
        $aggregateFailures.Add("Native first-viewport p95 against same-byte Edge was $firstP95, above 1.10.")
    }
    if ($fullTop500 -and $full.Count -eq $expectedRendered -and $fullP95 -gt 1.10) {
        $aggregateFailures.Add("Native full-traversal p95 against same-byte Edge was $fullP95, above 1.10.")
    }
    if ($fullTop500) {
        foreach ($rank in @(2, 5, 8, 22, 26, 60, 102, 149, 153, 201, 203, 295, 395, 476)) {
            $case = $caseResults | Where-Object { [int]$_.rank -eq $rank } | Select-Object -First 1
            foreach ($metric in @('nativeToSameByteFirstRenderRatio', 'nativeToSameByteFullPageRatio')) {
                $value = if ($case -and $case.comparison) { $case.comparison.$metric } else { $null }
                if ($null -eq $value -or -not [double]::IsFinite([double]$value) -or [double]$value -le 0 -or [double]$value -gt 1.10) {
                    $aggregateFailures.Add("Same-byte performance sentinel rank $rank $metric was missing or above 1.10.")
                }
            }
        }
    }
    foreach ($failure in $aggregateFailures) { $Failures.Add($failure) }
    return [pscustomobject]@{
        schemaVersion = 1
        scope = 'offline-same-byte-pinned-corpus-in-existing-windows-sandbox'
        executionStatus = if ($Failures.Count -eq 0 -and $CompletedShardCount -eq $Shards.Count) { 'completed-within-offline-scope' } else { 'failed' }
        overallReleaseQualification = 'not-qualified'
        liveGitHubArticleSemanticComparison = 'not-run'
        liveTop500RankingFreshness = 'not-checked'
        authenticatedLiveAuditEquivalent = $false
        pinnedManifestSha256 = $Pinned.Sha256
        requestedStartRank = $resolvedStartRank
        requestedCount = $resolvedCount
        completedShardCount = $CompletedShardCount
        requestedShardCount = $Shards.Count
        completedCaseCount = $caseResults.Count
        failedCaseCount = @($caseResults | Where-Object { $_.status -eq 'failed' }).Count
        sourceViewNotApplicableCount = @($caseResults | Where-Object { $_.status -eq 'not-applicable-source-view' }).Count
        noReadmeNotApplicableCount = @($caseResults | Where-Object { $_.status -eq 'not-applicable-no-readme' }).Count
        performanceAggregate = @{
            status = if (-not $fullTop500) { 'not-evaluated-partial-selection' } elseif ($aggregateFailures.Count -eq 0) { 'passed-within-offline-scope' } else { 'failed' }
            evaluated = $fullTop500
            ratioReference = 'same-byte Edge'
            expectedRenderedReadmeCount = $expectedRendered
            firstViewportRatioCount = $first.Count
            fullTraversalRatioCount = $full.Count
            firstViewportP95 = $firstP95
            fullTraversalP95 = $fullP95
            maximumRatio = 1.10
            sentinelRanks = @(2, 5, 8, 22, 26, 60, 102, 149, 153, 201, 203, 295, 395, 476)
            failures = @($aggregateFailures)
        }
        failures = @($Failures)
        cases = @($caseResults)
        completedAtUtc = [DateTimeOffset]::UtcNow
    }
}

$pinned = Get-PinnedRepositories
$plan = Get-SelectedCapturePlan -Pinned $pinned
$shards = @(New-Shards -Plan $plan)
$planSummary = [ordered]@{
    scope = 'offline-same-byte-pinned-corpus-in-existing-windows-sandbox'
    executionMode = if ($Run) { 'run-requested' } else { 'plan-only-no-sandbox-files-changed' }
    overallReleaseQualification = 'not-qualified'
    liveGitHubArticleSemanticComparison = 'not-run'
    liveTop500RankingFreshness = 'not-checked'
    authenticatedLiveAuditEquivalent = $false
    pinnedManifestPath = $ManifestPath
    pinnedManifestSha256 = $pinned.Sha256
    captureRoot = $CaptureRoot
    sourceRoot = $repoRoot
    appDirectory = $AppDirectory
    automationRunnerDirectory = $AutomationRunnerDirectory
    sourceAdmissionTraceEnabled = [bool]$SourceAdmissionTrace
    nodeExecutablePath = $NodeExecutablePath
    startRank = $resolvedStartRank
    count = $resolvedCount
    shardSize = $ShardSize
    shardCount = $shards.Count
    shards = @($shards | ForEach-Object {
        [ordered]@{ startRank = [int]$_.Repositories[0].rank; endRank = [int]$_.Repositories[-1].rank; stagedCorpusBytes = $_.Bytes }
    })
}
Write-Host ($planSummary | ConvertTo-Json -Depth 8)
if (-not $Run) {
    Write-Host 'Plan only: no Sandbox push, exec, pull, build, install, or UI launch was performed. Re-run with -Run after reviewing the explicit inputs.'
    return
}

$appPath = Join-Path $AppDirectory 'JitHub.WinUI.exe'
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) { throw "NativeAOT app is missing: '$appPath'." }
$OutputRoot = Assert-OrdinaryPath -Path $OutputRoot -Description 'Offline audit output root'
foreach ($inputPath in @($AppDirectory, $AutomationRunnerDirectory, $CaptureRoot, $NodeExecutablePath, $ManifestPath)) {
    $inputFullPath = [IO.Path]::GetFullPath($inputPath).TrimEnd('\', '/')
    $outputFullPath = $OutputRoot.TrimEnd('\', '/')
    $separator = [IO.Path]::DirectorySeparatorChar
    if ($outputFullPath.Equals($inputFullPath, [StringComparison]::OrdinalIgnoreCase) -or
        $outputFullPath.StartsWith($inputFullPath + $separator, [StringComparison]::OrdinalIgnoreCase) -or
        $inputFullPath.StartsWith($outputFullPath + $separator, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Output root overlaps a required input path: '$inputFullPath'."
    }
}
foreach ($existingReport in @(
    (Join-Path $OutputRoot 'offline-same-byte-sandbox-execution.json'),
    (Join-Path $OutputRoot 'offline-same-byte-sandbox-summary.json'))) {
    if (Test-Path -LiteralPath $existingReport) {
        throw "Refusing to overwrite prior audit evidence: '$existingReport'. Choose a fresh OutputRoot."
    }
}
Invoke-NativeAotArtifactVerifier -VerifierPath (Join-Path $repoRoot 'eng\Verify-NativeAotArtifact.ps1') -InputPath $AppDirectory
foreach ($requiredAuditFile in @(
    'eng\readme-audit\profiles\github-primer-light-v6.css',
    'eng\readme-audit\closed-details-snapshot.mjs',
    'eng\readme-audit\same-byte-edge-source.mjs',
    'eng\readme-audit\Install-PinnedWindowsAppRuntime.ps1',
    'eng\readme-audit\vendor\marked-18.0.5.umd.js',
    'eng\readme-audit\vendor\github-emoji-shortcodes.json',
    'eng\readme-audit\vendor\github-emoji-shortcodes.js',
    'eng\readme-audit\vendor\GITHUB-GEMOJI.LICENSE')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot $requiredAuditFile) -PathType Leaf)) {
        throw "Required pinned source replay file is missing: '$requiredAuditFile'."
    }
}

$snapshot = Get-AndAssertSandboxSnapshot
$runId = [Guid]::NewGuid().ToString('N')
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$snapshotData = New-SourceAndArtifactSnapshot -RunId $runId
$guestRelativeRoot = 'offline-same-byte/' + $runId
$guestRepoRelative = $guestRelativeRoot + '/repo'
$guestRepoAbsolute = Join-Path ([string]$snapshot.workRoot) ($guestRepoRelative -replace '/', [IO.Path]::DirectorySeparatorChar)
$guestEpoch = [string]$snapshot.executionTarget.epoch
$hostEvidenceRoot = Join-Path $OutputRoot ('evidence-' + $runId)
[IO.Directory]::CreateDirectory($hostEvidenceRoot) | Out-Null
$runFailures = [Collections.Generic.List[string]]::new()
$completedShardCount = 0
$runtimePrerequisite = $null
$guestControlScripts = [Collections.Generic.List[object]]::new()

try {
    $snapshotNow = Get-AndAssertSandboxSnapshot
    if ([string]$snapshotNow.executionTarget.epoch -cne $guestEpoch -or
        [string]$snapshotNow.workRoot -cne [string]$snapshot.workRoot) {
        throw 'The existing Sandbox changed after planning; refusing to continue in a different guest.'
    }
    Initialize-SandboxWorkRoot -Snapshot $snapshotNow
    $guestRepoAbsoluteLiteral = ConvertTo-GuestPowerShellSingleQuotedLiteral -Value $guestRepoAbsolute
    $relativeProbe = @(
        'target', 'exec', 'sandbox', '--cwd', [string]$snapshot.workRoot,
        '--', 'powershell.exe', '-NoProfile', '-NonInteractive', '-Command',
        "if (Test-Path -LiteralPath $guestRepoAbsoluteLiteral) { exit 17 }; exit 0")
    Invoke-WinApp -Arguments $relativeProbe -Description 'unique guest staging-path preflight'
    Invoke-WinApp -Arguments @('target', 'push', 'sandbox', $snapshotData.Root, $guestRepoRelative) -Description 'source, x64 AOT app, self-contained runner, and Node staging'
    $runtimePrerequisite = Invoke-SandboxWindowsAppRuntimePreflight -Snapshot $snapshotNow `
        -GuestRepoAbsolute $guestRepoAbsolute -GuestRepoRelative $guestRepoRelative `
        -SnapshotData $snapshotData -HostEvidenceRoot $hostEvidenceRoot -GuestEpoch $guestEpoch
    if (-not $runtimePrerequisite.Success) {
        throw "Windows App Runtime prerequisite did not pass: $($runtimePrerequisite.Failure)"
    }

    for ($shardIndex = 0; $shardIndex -lt $shards.Count; $shardIndex++) {
        $shard = $shards[$shardIndex]
        $first = [int]$shard.Repositories[0].rank
        $last = [int]$shard.Repositories[-1].rank
        $batch = Copy-ShardInput -Shard $shard -Pinned $pinned -Plan $plan -SnapshotRoot $snapshotData.Root -RunId $runId -ShardIndex ($shardIndex + 1)
        $batchDestination = $guestRepoRelative + '/artifacts/offline-same-byte-input/ranks-{0:D3}-{1:D3}' -f $first, $last
        Invoke-WinApp -Arguments @('target', 'push', 'sandbox', $batch.Root, $batchDestination) -Description "pinned corpus stage ranks $first..$last"
        $outputRelative = 'artifacts/offline-same-byte-output/ranks-{0:D3}-{1:D3}' -f $first, $last
        $guestScript = New-GuestBatchScript -GuestRepo $guestRepoRelative -GuestWorkRoot ([string]$snapshot.workRoot) -Start $first -Amount ($last - $first + 1) -OutputRelativePath $outputRelative
        $guestScriptFileName = 'offline-same-byte-ranks-{0:D3}-{1:D3}-{2}.ps1' -f $first, $last, $runId
        $hostControlRoot = Join-Path $hostEvidenceRoot 'guest-control'
        [IO.Directory]::CreateDirectory($hostControlRoot) | Out-Null
        $hostGuestScriptPath = Join-Path $hostControlRoot $guestScriptFileName
        [IO.File]::WriteAllText($hostGuestScriptPath, $guestScript, [Text.UTF8Encoding]::new($false))
        $guestControlRelativePath = $guestRepoRelative + '/' + $guestScriptFileName
        Invoke-WinApp -Arguments @('target', 'push', 'sandbox', $hostGuestScriptPath, $guestControlRelativePath) -Description "bounded guest script stage ranks $first..$last"
        $guestControlScripts.Add([ordered]@{
            firstRank = $first
            lastRank = $last
            guestPath = $guestControlRelativePath
            hostPath = $hostGuestScriptPath
            sha256 = (Get-FileHash -LiteralPath $hostGuestScriptPath -Algorithm SHA256).Hash.ToLowerInvariant()
            byteSize = (Get-Item -LiteralPath $hostGuestScriptPath).Length
        })
        $snapshotBeforeBatch = Get-AndAssertSandboxSnapshot
        if ([string]$snapshotBeforeBatch.executionTarget.epoch -cne $guestEpoch -or
            [string]$snapshotBeforeBatch.workRoot -cne [string]$snapshot.workRoot) {
            throw 'Sandbox epoch or work root changed before a shard; refusing host fallback.'
        }
        $guestCwd = Join-Path ([string]$snapshot.workRoot) ($guestRepoRelative -replace '/', [IO.Path]::DirectorySeparatorChar)
        $guestScriptAbsolute = Join-Path $guestCwd $guestScriptFileName
        $execArgs = New-GuestBatchExecArguments -GuestCwd $guestCwd -GuestScriptPath $guestScriptAbsolute
        $exitCode = 0
        try { Invoke-WinApp -Arguments $execArgs -Description "offline same-byte app/Edge replay ranks $first..$last" }
        catch {
            $exitCode = 1
            $runFailures.Add("Sandbox shard ranks $first..$last returned a failure: $($_.Exception.Message)")
        }

        $hostShard = Join-Path $hostEvidenceRoot ('ranks-{0:D3}-{1:D3}' -f $first, $last)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($hostShard)) | Out-Null
        $guestOutputRelative = $guestRepoRelative + '/' + $outputRelative
        try {
            Invoke-WinApp -Arguments @('target', 'pull', 'sandbox', $guestOutputRelative, $hostShard) -Description "case evidence pull ranks $first..$last"
            $completedShardCount++
        }
        catch { $runFailures.Add("Could not pull shard evidence ranks $first..${last}: $($_.Exception.Message)") }

        if ($exitCode -ne 0) { Write-Warning "Shard ranks $first..$last failed; later shards will still execute and keep separate evidence." }
        Write-Host "Offline same-byte Sandbox shard $($shardIndex + 1)/$($shards.Count) completed for ranks $first..$last."
    }
}
catch {
    $runFailures.Add($_.Exception.Message)
}

$executionManifest = [ordered]@{
    schemaVersion = 1
    scope = 'offline-same-byte-pinned-corpus-in-existing-windows-sandbox'
    overallReleaseQualification = 'not-qualified'
    liveGitHubArticleSemanticComparison = 'not-run'
    liveTop500RankingFreshness = 'not-checked'
    authenticatedLiveAuditEquivalent = $false
    executionTargetEpoch = $guestEpoch
    sandboxArchitecture = [string]$snapshot.capabilities.architecture
    supportsInteractiveDesktop = [bool]$snapshot.capabilities.supportsInteractiveDesktop
    sourceSnapshotManifestSha256 = $snapshotData.ManifestSha256
    sourceSnapshotManifestPath = $snapshotData.ManifestPath
    pinnedManifestSha256 = $pinned.Sha256
    capturedCorpusRoot = $CaptureRoot
    appArtifactDirectory = $AppDirectory
    automationRunnerDirectory = $AutomationRunnerDirectory
    nodeExecutablePath = $NodeExecutablePath
    edgeGuestPath = $EdgeExecutablePath
    windowsAppRuntimePreflight = if ($null -eq $runtimePrerequisite) {
        [ordered]@{ status = 'not-completed'; reason = 'Sandbox setup did not reach the runtime prerequisite.' }
    }
    else {
        [ordered]@{
            status = if ($runtimePrerequisite.Success) { 'ready' } else { 'failed' }
            action = if ($runtimePrerequisite.Report) { [string]$runtimePrerequisite.Report.action } else { $null }
            requestedArchitecture = if ($runtimePrerequisite.Report) { [string]$runtimePrerequisite.Report.requestedArchitecture } else { 'x64' }
            minimumVersion = if ($runtimePrerequisite.Report) { [string]$runtimePrerequisite.Report.minimumVersion } else { '8000.921.1539.0' }
            evidencePath = $runtimePrerequisite.EvidencePath
            evidenceSha256 = $runtimePrerequisite.EvidenceSha256
            failure = $runtimePrerequisite.Failure
        }
    }
    guestControlScripts = @($guestControlScripts)
    failures = @($runFailures)
    completedAtUtc = [DateTimeOffset]::UtcNow
}
$executionManifestPath = Join-Path $OutputRoot 'offline-same-byte-sandbox-execution.json'
[IO.File]::WriteAllText($executionManifestPath, (($executionManifest | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))

$aggregate = Get-OfflineAggregate -Pinned $pinned -Plan $plan -Shards $shards -EvidenceRoot $hostEvidenceRoot -CompletedShardCount $completedShardCount -Failures $runFailures
$aggregate | Add-Member -NotePropertyName executionTargetEpoch -NotePropertyValue $guestEpoch -Force
$aggregate | Add-Member -NotePropertyName sourceSnapshotManifestSha256 -NotePropertyValue $snapshotData.ManifestSha256 -Force
$aggregatePath = Join-Path $OutputRoot 'offline-same-byte-sandbox-summary.json'
[IO.File]::WriteAllText($aggregatePath, (($aggregate | ConvertTo-Json -Depth 64) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
Write-Host "Offline same-byte Sandbox report: '$aggregatePath'."
Write-Host 'This report does not qualify the authenticated live top-500 release gate.'
if ($aggregate.executionStatus -ne 'completed-within-offline-scope') {
    throw "Offline same-byte Sandbox replay failed $($runFailures.Count) aggregate gate(s); preserved evidence: '$hostEvidenceRoot'."
}
Write-Host 'Offline same-byte Sandbox replay completed within its explicitly limited scope.'
