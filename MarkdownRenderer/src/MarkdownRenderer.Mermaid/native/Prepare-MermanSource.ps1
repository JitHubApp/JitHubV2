[CmdletBinding()]
param(
    [string] $PinnedRepositoryPath,
    [string] $PreparationRoot
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$script:MermanUtf8Strict = [System.Text.UTF8Encoding]::new($false, $true)
$script:MermanUtf8NoBom = [System.Text.UTF8Encoding]::new($false)
$script:MermanGitExecutable = (Get-Command git.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$script:MermanCargoRepositoryUrl = 'https://github.com/Latias94/merman.git'

function Get-MermanProperty {
    param(
        [Parameter(Mandatory)] [object] $Object,
        [Parameter(Mandatory)] [string] $Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        throw "Required Merman manifest property '$Name' is missing."
    }

    return $property.Value
}

function Get-MermanRawSha256FromBytes {
    param(
        [Parameter(Mandatory)] [byte[]] $Bytes,
        [Parameter(Mandatory)] [string] $Description
    )

    try {
        $text = $script:MermanUtf8Strict.GetString($Bytes)
    }
    catch {
        throw "$Description is not valid UTF-8 text: $($_.Exception.Message)"
    }

    $hash = [System.Security.Cryptography.SHA256]::Create()
    try {
        $digest = $hash.ComputeHash($Bytes)
        return [System.Convert]::ToHexString($digest).ToLowerInvariant()
    }
    finally {
        $hash.Dispose()
    }
}

function Get-MermanFileRawSha256 {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Description
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description is missing: $Path"
    }

    return Get-MermanRawSha256FromBytes -Bytes ([System.IO.File]::ReadAllBytes($Path)) -Description $Description
}

function Get-MermanPatchSetSha256 {
    param([Parameter(Mandatory)] [object[]] $Patches)

    if ($Patches.Count -eq 0) {
        throw 'The Merman patch set must contain at least one patch.'
    }
    if ($Patches.Count -eq 1) {
        return [string]$Patches[0].Sha256
    }

    $identity = (($Patches | ForEach-Object { "$($_.RelativePath)`n$($_.Sha256)`n" }) -join '')
    return Get-MermanRawSha256FromBytes -Bytes $script:MermanUtf8NoBom.GetBytes($identity) -Description 'Merman patch-set identity'
}

function Invoke-MermanGitText {
    param(
        [Parameter(Mandatory)] [string] $Repository,
        [Parameter(Mandatory)] [string[]] $Arguments
    )

    $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = $script:MermanGitExecutable
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    foreach ($argument in @('-c', 'core.autocrlf=false', '-c', 'core.longpaths=true', '-C', $Repository) + $Arguments) {
        $processInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $processInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start git $($Arguments -join ' ')."
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "git $($Arguments -join ' ') failed in '$Repository' with exit code $($process.ExitCode). $stderr$stdout"
        }

        if ([string]::IsNullOrEmpty($stdout)) {
            return @()
        }
        $lines = @($stdout -split '\r?\n')
        if ($lines.Count -gt 0 -and $lines[-1] -ceq '') {
            $lines = @($lines | Select-Object -First ($lines.Count - 1))
        }
        return $lines
    }
    finally {
        $process.Dispose()
    }
}

function Get-MermanGitBlobRawSha256 {
    param(
        [Parameter(Mandatory)] [string] $Repository,
        [Parameter(Mandatory)] [string] $Commit,
        [Parameter(Mandatory)] [string] $RelativePath
    )

    $processInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $processInfo.FileName = $script:MermanGitExecutable
    $processInfo.UseShellExecute = $false
    $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    foreach ($argument in @(
        '-c', 'core.autocrlf=false', '-c', 'core.longpaths=true', '-C', $Repository,
        'cat-file', 'blob', "$Commit`:$RelativePath")) {
        $processInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $processInfo
    $memory = [System.IO.MemoryStream]::new()
    try {
        if (-not $process.Start()) {
            throw 'Could not start git to inspect a pinned Merman blob.'
        }

        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.StandardOutput.BaseStream.CopyTo($memory)
        $process.WaitForExit()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw "git cat-file failed for '$RelativePath' at '$Commit' with exit code $($process.ExitCode). $stderr"
        }

        return Get-MermanRawSha256FromBytes -Bytes $memory.ToArray() -Description "Pinned Merman source '$RelativePath'"
    }
    finally {
        $memory.Dispose()
        $process.Dispose()
    }
}

function Get-MermanTrackedFileMode {
    param(
        [Parameter(Mandatory)] [string] $Repository,
        [Parameter(Mandatory)] [string] $Commit,
        [Parameter(Mandatory)] [string] $RelativePath
    )

    $entries = @(Invoke-MermanGitText -Repository $Repository -Arguments @('ls-tree', $Commit, '--', $RelativePath) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($entries.Count -ne 1 -or $entries[0] -notmatch '^(?<mode>[0-9]{6}) blob [0-9a-f]{40}\t') {
        throw "Merman source path '$RelativePath' is not one regular tracked blob at '$Commit'. Symlinks, submodules, and missing paths are rejected."
    }

    $mode = $Matches.mode
    if ($mode -notin @('100644', '100755')) {
        throw "Merman source path '$RelativePath' has unsupported Git mode '$mode'."
    }

    return $mode
}

function Assert-MermanRelativeSourcePath {
    param([Parameter(Mandatory)] [string] $Path)

    if ($Path -notmatch '^crates/[A-Za-z0-9._-]+(?:/[A-Za-z0-9._-]+)*$' -or
        $Path.Contains('..') -or $Path.Split('/') -ccontains '.' -or $Path.Contains('\') -or $Path.Contains(':')) {
        throw "Unsafe or unsupported Merman source path '$Path'."
    }
}

function Assert-MermanDirectoryIsOrdinary {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [switch] $RequireExisting
    )

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    if ($null -eq $item) {
        if ($RequireExisting) {
            throw "Merman source parent must already be an ordinary directory: $Path"
        }
        return
    }

    if (-not $item.PSIsContainer -or
        (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Merman preparation path must be an ordinary directory, not a file or reparse point: $Path"
    }
}

function Assert-MermanFileIsOrdinary {
    param([Parameter(Mandatory)] [string] $Path)

    $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
    if ($item.PSIsContainer -or
        (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
        throw "Merman source or patch path must be an ordinary file, not a directory or reparse point: $Path"
    }
}

function Get-MermanOrdinarySourcePath {
    param(
        [Parameter(Mandatory)] [string] $SourceRoot,
        [Parameter(Mandatory)] [string] $RelativePath
    )

    Assert-MermanRelativeSourcePath -Path $RelativePath
    Assert-MermanDirectoryIsOrdinary -Path $SourceRoot -RequireExisting
    $segments = $RelativePath.Split('/')
    $sourcePath = $SourceRoot
    for ($index = 0; $index -lt $segments.Length - 1; $index++) {
        $sourcePath = Join-Path $sourcePath $segments[$index]
        Assert-MermanDirectoryIsOrdinary -Path $sourcePath -RequireExisting
    }
    return Join-Path $sourcePath $segments[-1]
}

function Assert-MermanAddedSourceAbsentAtBase {
    param(
        [Parameter(Mandatory)] [string] $Repository,
        [Parameter(Mandatory)] [string] $Commit,
        [Parameter(Mandatory)] [string] $RelativePath
    )

    $entries = @(Invoke-MermanGitText -Repository $Repository -Arguments @('ls-tree', $Commit, '--', $RelativePath) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($entries.Count -ne 0) {
        throw "Added Merman source '$RelativePath' must be absent from the pinned base tree; additions cannot overwrite upstream files or directories."
    }
}

function Read-MermanPreparationInputs {
    param(
        [Parameter(Mandatory)] [string] $ManifestPath,
        [Parameter(Mandatory)] [string] $PatchDirectory,
        [Parameter(Mandatory)] [string] $ProvenancePath,
        [Parameter(Mandatory)] [string] $CargoTomlPath
    )

    foreach ($path in @($ManifestPath, $ProvenancePath, $CargoTomlPath)) {
        Assert-MermanFileIsOrdinary -Path $path
    }
    Assert-MermanDirectoryIsOrdinary -Path $PatchDirectory

    try {
        $provenance = [System.IO.File]::ReadAllText($ProvenancePath, $script:MermanUtf8Strict) | ConvertFrom-Json -ErrorAction Stop
        $manifestBytes = [System.IO.File]::ReadAllBytes($ManifestPath)
        $manifest = $script:MermanUtf8Strict.GetString($manifestBytes) | ConvertFrom-Json -ErrorAction Stop
        $cargoToml = [System.IO.File]::ReadAllText($CargoTomlPath, $script:MermanUtf8Strict)
    }
    catch {
        throw "Could not read Merman provenance/patch manifest/Cargo.toml: $($_.Exception.Message)"
    }

    if ((Get-MermanProperty $provenance 'component') -ne 'Merman') {
        throw 'MERMAN_PROVENANCE.json does not identify the Merman component.'
    }
    $sourcePatches = Get-MermanProperty $provenance 'sourcePatches'
    $manifestProvenancePath = [string](Get-MermanProperty $sourcePatches 'manifest')
    $expectedManifestSha256 = [string](Get-MermanProperty $sourcePatches 'manifestSha256')
    $expectedPatchSetSha256 = [string](Get-MermanProperty $sourcePatches 'patchSetSha256')
    if ($manifestProvenancePath -cne 'native/patches/MERMAN_SOURCE_PATCHES.json' -or
        $expectedManifestSha256 -notmatch '^[0-9a-f]{64}$' -or
        $expectedPatchSetSha256 -notmatch '^[0-9a-f]{64}$') {
        throw 'MERMAN_PROVENANCE.json must pin the canonical source-patch manifest path, raw manifest digest, and patch-set digest.'
    }
    $actualManifestSha256 = Get-MermanRawSha256FromBytes -Bytes $manifestBytes -Description 'MERMAN_SOURCE_PATCHES.json'
    if ($actualManifestSha256 -cne $expectedManifestSha256) {
        throw "MERMAN_SOURCE_PATCHES.json raw SHA-256 mismatch: provenance pins $expectedManifestSha256, got $actualManifestSha256."
    }
    $provenanceRepository = [string](Get-MermanProperty $provenance 'repository')
    $normalizedProvenanceRepository = $provenanceRepository.TrimEnd('/')
    if ($normalizedProvenanceRepository.EndsWith('.git', [System.StringComparison]::OrdinalIgnoreCase)) {
        $normalizedProvenanceRepository = $normalizedProvenanceRepository.Substring(0, $normalizedProvenanceRepository.Length - 4)
    }
    $revision = [string](Get-MermanProperty $provenance 'revision')
    $tree = [string](Get-MermanProperty $provenance 'tree')
    if ($normalizedProvenanceRepository -cne 'https://github.com/Latias94/merman' -or
        $revision -notmatch '^[0-9a-f]{40}$' -or $tree -notmatch '^[0-9a-f]{40}$') {
        throw 'MERMAN_PROVENANCE.json must contain full lowercase Git commit and tree IDs.'
    }

    $dependencyMatch = [regex]::Match(
        $cargoToml,
        '(?m)^\s*merman\s*=\s*\{[^\r\n]*git\s*=\s*"(?<url>https://github\.com/Latias94/merman\.git)"[^\r\n]*rev\s*=\s*"(?<rev>[0-9a-f]{40})"')
    if (-not $dependencyMatch.Success -or
        $dependencyMatch.Groups['rev'].Value -ne $revision -or
        $dependencyMatch.Groups['url'].Value -ne $script:MermanCargoRepositoryUrl) {
        throw 'native/Cargo.toml Merman git dependency does not match the fixed repository URL and MERMAN_PROVENANCE revision.'
    }

    $schemaVersion = Get-MermanProperty $manifest 'schemaVersion'
    if (($schemaVersion -isnot [int] -and $schemaVersion -isnot [long]) -or
        $schemaVersion -notin @(1, 2) -or
        [string](Get-MermanProperty $manifest 'repository') -cne $script:MermanCargoRepositoryUrl -or
        [string](Get-MermanProperty $manifest 'baseCommit') -cne $revision -or
        [string](Get-MermanProperty $manifest 'baseTree') -cne $tree) {
        throw 'MERMAN_SOURCE_PATCHES.json must use schema 1 or 2 and match the exact Cargo repository, provenance commit, and provenance tree.'
    }

    $patchEntries = @(Get-MermanProperty $manifest 'patches')
    $fileEntries = @(Get-MermanProperty $manifest 'files')
    if ($patchEntries.Count -eq 0 -or $fileEntries.Count -eq 0) {
        throw 'The Merman source patch manifest must declare at least one patch and one changed source file.'
    }

    $patches = [System.Collections.Generic.List[object]]::new()
    $seenPatchPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $patchEntries) {
        $relativePath = [string](Get-MermanProperty $entry 'path')
        $expectedHash = [string](Get-MermanProperty $entry 'sha256')
        if ($relativePath -notmatch '^[A-Za-z0-9._-]+\.patch$' -or
            $relativePath.Contains('..') -or
            -not $seenPatchPaths.Add($relativePath)) {
            throw "Unsafe, duplicate, or unsupported patch path '$relativePath'. Patch paths must be filenames directly inside native/patches."
        }
        if ($expectedHash -notmatch '^[0-9a-f]{64}$') {
            throw "Patch '$relativePath' must declare a lowercase SHA-256 digest."
        }

        $patchPath = [System.IO.Path]::GetFullPath((Join-Path $PatchDirectory $relativePath))
        if (-not $patchPath.StartsWith([System.IO.Path]::GetFullPath($PatchDirectory).TrimEnd('\') + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Patch '$relativePath' resolves outside native/patches."
        }
        Assert-MermanFileIsOrdinary -Path $patchPath
        $actualHash = Get-MermanFileRawSha256 -Path $patchPath -Description "Merman patch '$relativePath'"
        if ($actualHash -cne $expectedHash) {
            throw "Merman patch SHA-256 mismatch for '$relativePath': expected $expectedHash, got $actualHash."
        }

        $patches.Add([pscustomobject]@{
            RelativePath = $relativePath
            FullPath = $patchPath
            Sha256 = $expectedHash
        })
    }

    $files = [System.Collections.Generic.List[object]]::new()
    $seenFilePaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $fileEntries) {
        $relativePath = [string](Get-MermanProperty $entry 'path')
        $upstreamValue = Get-MermanProperty $entry 'upstreamSha256'
        $upstreamHash = [string]$upstreamValue
        $patchedHash = [string](Get-MermanProperty $entry 'patchedSha256')
        $changeKind = if ($schemaVersion -eq 1) { 'modify' } else { [string](Get-MermanProperty $entry 'changeKind') }
        Assert-MermanRelativeSourcePath -Path $relativePath
        if (-not $seenFilePaths.Add($relativePath)) {
            throw "Duplicate Merman changed-file path '$relativePath'."
        }
        if (@('modify', 'add') -cnotcontains $changeKind) {
            throw "Merman changed file '$relativePath' must declare changeKind 'modify' or 'add'."
        }
        if ($patchedHash -notmatch '^[0-9a-f]{64}$' -or
            ($changeKind -ceq 'modify' -and ($upstreamHash -notmatch '^[0-9a-f]{64}$' -or $upstreamHash -ceq $patchedHash))) {
            throw "Merman changed file '$relativePath' must have distinct lowercase upstreamSha256 and patchedSha256 values."
        }
        if ($changeKind -ceq 'add' -and $null -ne $upstreamValue) {
            throw "Added Merman source '$relativePath' must declare upstreamSha256 as null, not an invented base digest."
        }

        $files.Add([pscustomobject]@{
            RelativePath = $relativePath
            ChangeKind = $changeKind
            UpstreamSha256 = $upstreamHash
            PatchedSha256 = $patchedHash
        })
    }

    # A new file must be introduced as ordinary source, not as a symlink or
    # executable. Git's patch summary exposes modes even on Windows checkouts
    # that materialize symlinks as text files. Check this on cache reuse too.
    $addedPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    foreach ($file in $files) {
        if ($file.ChangeKind -ceq 'add') { $null = $addedPaths.Add($file.RelativePath) }
    }
    $createdPaths = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
    $patchInspectionDirectory = $PatchDirectory
    try {
        $repositoryRoots = @(Invoke-MermanGitText -Repository $PatchDirectory -Arguments @('rev-parse', '--show-toplevel'))
        if ($repositoryRoots.Count -ne 1) { throw 'Git did not return exactly one patch-inspection repository root.' }
        $patchInspectionDirectory = [System.IO.Path]::GetFullPath($repositoryRoots[0])
        Assert-MermanDirectoryIsOrdinary -Path $patchInspectionDirectory -RequireExisting
    }
    catch {
        # Standalone exported patches need no Git repository. Inside a checkout,
        # however, `git apply --summary` must run at its root: Git otherwise
        # silently ignores diff paths outside the current subdirectory prefix.
        if ($_.Exception.Message -notlike '*not a git repository*') { throw }
    }
    foreach ($patch in $patches) {
        $summary = @(Invoke-MermanGitText -Repository $patchInspectionDirectory -Arguments @('apply', '--summary', '--', $patch.FullPath) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        foreach ($line in $summary) {
            if ($line -cnotmatch '^ create mode 100644 (?<path>crates/[A-Za-z0-9._/-]+)$' -or
                -not $addedPaths.Contains($Matches.path) -or -not $createdPaths.Add($Matches.path)) {
                throw "Merman patch contains an undeclared addition or unsupported mode/type change: '$line'."
            }
        }
    }
    if (-not $createdPaths.SetEquals($addedPaths)) {
        throw 'Every added Merman source must be introduced by exactly one reviewed ordinary-file patch.'
    }

    $patchSetHash = Get-MermanPatchSetSha256 -Patches $patches.ToArray()
    if ($patchSetHash -cne $expectedPatchSetSha256) {
        throw "Merman patch-set SHA-256 mismatch: provenance pins $expectedPatchSetSha256, manifest declares $patchSetHash."
    }

    return [pscustomobject]@{
        Revision = $revision
        Tree = $tree
        Repository = $script:MermanCargoRepositoryUrl
        Patches = $patches.ToArray()
        Files = $files.ToArray()
        PatchSetSha256 = $patchSetHash
    }
}

function Assert-MermanSourceRoot {
    param(
        [Parameter(Mandatory)] [string] $SourceRoot,
        [Parameter(Mandatory)] [object] $Inputs
    )

    Assert-MermanDirectoryIsOrdinary -Path $SourceRoot
    $resolvedRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
    $head = (Invoke-MermanGitText -Repository $resolvedRoot -Arguments @('rev-parse', 'HEAD') | Select-Object -First 1).Trim()
    $tree = (Invoke-MermanGitText -Repository $resolvedRoot -Arguments @('rev-parse', 'HEAD^{tree}') | Select-Object -First 1).Trim()
    if ($head -cne $Inputs.Revision -or $tree -cne $Inputs.Tree) {
        throw "Prepared Merman source has the wrong base identity: expected $($Inputs.Revision)/$($Inputs.Tree), got $head/$tree."
    }

    # Git's path order is ordinal, not the current Windows culture's punctuation-aware order.
    # Keep all three exact comparisons deterministic when siblings include '.', '/' and '_'.
    $expected = [string[]] @($Inputs.Files | Where-Object { $_.ChangeKind -ceq 'modify' } | ForEach-Object { $_.RelativePath })
    $changed = [string[]] @(Invoke-MermanGitText -Repository $resolvedRoot -Arguments @('diff', '--name-only', '--no-renames', 'HEAD', '--') |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    [Array]::Sort($expected, [StringComparer]::Ordinal)
    [Array]::Sort($changed, [StringComparer]::Ordinal)
    if ($changed.Count -ne $expected.Count) {
        throw "Prepared Merman source changed-file count is $($changed.Count); manifest requires $($expected.Count). Changed paths: $($changed -join ', ')."
    }
    for ($index = 0; $index -lt $expected.Count; $index++) {
        if (-not [string]::Equals($changed[$index], $expected[$index], [System.StringComparison]::Ordinal)) {
            throw "Prepared Merman source changed-file list differs from the manifest at index ${index}: expected '$($expected[$index])', got '$($changed[$index])'."
        }
    }

    $statusLines = [string[]] @(Invoke-MermanGitText -Repository $resolvedRoot -Arguments @('status', '--porcelain=v1', '--untracked-files=all', '--ignored=matching') |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $expectedStatus = [string[]] @($Inputs.Files | ForEach-Object {
        if ($_.ChangeKind -ceq 'add') { '?? ' + $_.RelativePath } else { ' M ' + $_.RelativePath }
    })
    [Array]::Sort($statusLines, [StringComparer]::Ordinal)
    [Array]::Sort($expectedStatus, [StringComparer]::Ordinal)
    if ($statusLines.Count -ne $expectedStatus.Count) {
        throw "Prepared Merman source has $($statusLines.Count) worktree/index/untracked/ignored entries; manifest permits only $($expectedStatus.Count) exact modified or added files."
    }
    for ($index = 0; $index -lt $statusLines.Count; $index++) {
        $line = $statusLines[$index]
        if (-not [string]::Equals($line, $expectedStatus[$index], [System.StringComparison]::Ordinal)) {
            throw "Prepared Merman source has an unexpected Git status entry: '$line'."
        }
    }

    $summary = @(Invoke-MermanGitText -Repository $resolvedRoot -Arguments @('diff', '--summary', 'HEAD', '--') |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($summary.Count -ne 0) {
        throw "Prepared Merman source contains a mode/type change outside the content-hash manifest: $($summary -join '; ')"
    }

    foreach ($file in $Inputs.Files) {
        $sourcePath = Get-MermanOrdinarySourcePath -SourceRoot $resolvedRoot -RelativePath $file.RelativePath
        Assert-MermanFileIsOrdinary -Path $sourcePath
        $worktreeHash = Get-MermanFileRawSha256 -Path $sourcePath -Description "Prepared Merman source '$($file.RelativePath)'"
        if ($worktreeHash -cne $file.PatchedSha256) {
            throw "Prepared Merman SHA-256 mismatch for '$($file.RelativePath)': expected $($file.PatchedSha256), got $worktreeHash."
        }

        if ($file.ChangeKind -ceq 'add') {
            Assert-MermanAddedSourceAbsentAtBase -Repository $resolvedRoot -Commit $Inputs.Revision -RelativePath $file.RelativePath
            continue
        }
        $mode = Get-MermanTrackedFileMode -Repository $resolvedRoot -Commit $Inputs.Revision -RelativePath $file.RelativePath
        $upstreamHash = Get-MermanGitBlobRawSha256 -Repository $resolvedRoot -Commit $Inputs.Revision -RelativePath $file.RelativePath
        if ($upstreamHash -cne $file.UpstreamSha256) {
            throw "Prepared Merman base-blob SHA-256 mismatch for '$($file.RelativePath)': expected $($file.UpstreamSha256), got $upstreamHash."
        }
        if ($mode -notin @('100644', '100755')) {
            throw "Prepared Merman source '$($file.RelativePath)' changed into unsupported mode '$mode'."
        }
    }

    return $resolvedRoot
}

function ConvertTo-MermanCargoTomlString {
    param([Parameter(Mandatory)] [string] $Value)

    return '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

function Invoke-MermanSourcePreparation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $ManifestPath,
        [Parameter(Mandatory)] [string] $PatchDirectory,
        [Parameter(Mandatory)] [string] $ProvenancePath,
        [Parameter(Mandatory)] [string] $CargoTomlPath,
        [Parameter(Mandatory)] [string] $PreparationRoot,
        [string] $PinnedRepositoryPath
    )

    $inputs = Read-MermanPreparationInputs `
        -ManifestPath $ManifestPath `
        -PatchDirectory $PatchDirectory `
        -ProvenancePath $ProvenancePath `
        -CargoTomlPath $CargoTomlPath

    $repositoryPath = if ([string]::IsNullOrWhiteSpace($PinnedRepositoryPath)) {
        $inputs.Repository
    }
    else {
        if (-not (Test-Path -LiteralPath $PinnedRepositoryPath -PathType Container)) {
            throw "Explicit pinned Merman source path does not exist: $PinnedRepositoryPath"
        }
        (Resolve-Path -LiteralPath $PinnedRepositoryPath).Path
    }

    $preparationRoot = [System.IO.Path]::GetFullPath($PreparationRoot)
    if (Test-Path -LiteralPath $preparationRoot) {
        Assert-MermanDirectoryIsOrdinary -Path $preparationRoot
    }

    $finalRoot = Join-Path $preparationRoot $inputs.PatchSetSha256
    if (Test-Path -LiteralPath $finalRoot) {
        $verifiedRoot = Assert-MermanSourceRoot -SourceRoot $finalRoot -Inputs $inputs
    }
    else {
        if (-not (Test-Path -LiteralPath $preparationRoot -PathType Container)) {
            $null = New-Item -ItemType Directory -Path $preparationRoot -Force
        }
        Assert-MermanDirectoryIsOrdinary -Path $preparationRoot

        $stageRoot = Join-Path $preparationRoot ('.staging-' + [guid]::NewGuid().ToString('N'))
        if (Test-Path -LiteralPath $stageRoot) {
            throw "Refusing to reuse an unexpected Merman staging path: $stageRoot"
        }

        $cloneOutput = & $script:MermanGitExecutable -c core.autocrlf=false -c core.longpaths=true clone --no-hardlinks --no-checkout $repositoryPath $stageRoot 2>&1
        $cloneExitCode = $LASTEXITCODE
        if ($cloneExitCode -ne 0) {
            $details = (@($cloneOutput | ForEach-Object { [string] $_ }) -join [Environment]::NewLine).Trim()
            throw "Could not clone the exact Merman source into an independent preparation directory (exit $cloneExitCode). $details"
        }

        Assert-MermanDirectoryIsOrdinary -Path $stageRoot
        $null = Invoke-MermanGitText -Repository $stageRoot -Arguments @('config', 'core.autocrlf', 'false')
        $null = Invoke-MermanGitText -Repository $stageRoot -Arguments @('checkout', '--detach', $inputs.Revision)
        $stageHead = (Invoke-MermanGitText -Repository $stageRoot -Arguments @('rev-parse', 'HEAD') | Select-Object -First 1).Trim()
        $stageTree = (Invoke-MermanGitText -Repository $stageRoot -Arguments @('rev-parse', 'HEAD^{tree}') | Select-Object -First 1).Trim()
        if ($stageHead -cne $inputs.Revision -or $stageTree -cne $inputs.Tree) {
            throw "Cloned Merman baseline identity mismatch: expected $($inputs.Revision)/$($inputs.Tree), got $stageHead/$stageTree."
        }

        $baselineStatus = @(Invoke-MermanGitText -Repository $stageRoot -Arguments @('status', '--porcelain=v1', '--untracked-files=all', '--ignored=matching') |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        if ($baselineStatus.Count -ne 0) {
            throw "The cloned Merman baseline is not clean: $($baselineStatus -join '; ')"
        }

        foreach ($file in $inputs.Files) {
            $sourcePath = Get-MermanOrdinarySourcePath -SourceRoot $stageRoot -RelativePath $file.RelativePath
            if ($file.ChangeKind -ceq 'add') {
                Assert-MermanAddedSourceAbsentAtBase -Repository $stageRoot -Commit $inputs.Revision -RelativePath $file.RelativePath
                if (Test-Path -LiteralPath $sourcePath) {
                    throw "Added Merman source already exists in the cloned baseline: $($file.RelativePath)"
                }
                continue
            }
            Assert-MermanFileIsOrdinary -Path $sourcePath
            $baselineHash = Get-MermanFileRawSha256 -Path $sourcePath -Description "Cloned Merman baseline '$($file.RelativePath)'"
            if ($baselineHash -cne $file.UpstreamSha256) {
                throw "Cloned Merman baseline file '$($file.RelativePath)' does not match its manifest upstream hash."
            }
        }

        foreach ($patch in $inputs.Patches) {
            $null = Invoke-MermanGitText -Repository $stageRoot -Arguments @('apply', '--check', '--whitespace=error', '--', $patch.FullPath)
            $null = Invoke-MermanGitText -Repository $stageRoot -Arguments @('apply', '--whitespace=error', '--', $patch.FullPath)
        }

        $null = Assert-MermanSourceRoot -SourceRoot $stageRoot -Inputs $inputs
        if (Test-Path -LiteralPath $finalRoot) {
            throw "Merman preparation destination appeared during the build; refusing to overwrite it: $finalRoot"
        }
        [System.IO.Directory]::Move($stageRoot, $finalRoot)
        $verifiedRoot = Assert-MermanSourceRoot -SourceRoot $finalRoot -Inputs $inputs
    }

    $corePath = Join-Path $verifiedRoot 'crates/merman-core'
    $renderPath = Join-Path $verifiedRoot 'crates/merman-render'
    foreach ($path in @($corePath, $renderPath)) {
        Assert-MermanDirectoryIsOrdinary -Path $path
        if (-not (Test-Path -LiteralPath $path -PathType Container)) {
            throw "Prepared Merman crate path is missing: $path"
        }
    }

    $escapedCore = ConvertTo-MermanCargoTomlString -Value $corePath
    $escapedRender = ConvertTo-MermanCargoTomlString -Value $renderPath
    $cargoArguments = [string[]] @(
        '--config', "patch.`"$($inputs.Repository)`".merman-core.path=$escapedCore",
        '--config', "patch.`"$($inputs.Repository)`".merman-render.path=$escapedRender"
    )

    return [pscustomobject]@{
        SourceRoot = $verifiedRoot
        PatchSetSha256 = $inputs.PatchSetSha256
        BaseCommit = $inputs.Revision
        BaseTree = $inputs.Tree
        CargoArguments = $cargoArguments
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    $nativeRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
    $mermaidProjectRoot = Split-Path -Parent $nativeRoot
    $manifestPath = Join-Path $nativeRoot 'patches/MERMAN_SOURCE_PATCHES.json'
    $patchDirectory = Join-Path $nativeRoot 'patches'
    $provenancePath = Join-Path $mermaidProjectRoot 'MERMAN_PROVENANCE.json'
    $cargoTomlPath = Join-Path $nativeRoot 'Cargo.toml'
    if ([string]::IsNullOrWhiteSpace($PreparationRoot)) {
        $PreparationRoot = Join-Path $nativeRoot 'target/merman-patched'
    }

    Invoke-MermanSourcePreparation `
        -ManifestPath $manifestPath `
        -PatchDirectory $patchDirectory `
        -ProvenancePath $provenancePath `
        -CargoTomlPath $cargoTomlPath `
        -PreparationRoot $PreparationRoot `
        -PinnedRepositoryPath $PinnedRepositoryPath
}
