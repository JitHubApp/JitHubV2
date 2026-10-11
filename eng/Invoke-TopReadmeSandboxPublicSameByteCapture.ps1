[CmdletBinding()]
param(
    [ValidateRange(1, 500)]
    [int]$StartRank = 1,

    [ValidateRange(1, 500)]
    [int]$Count = 500,

    [ValidateRange(1, 25)]
    [int]$ShardSize = 10,

    [Parameter(Mandatory = $true)]
    [string]$AutomationRunnerDirectory,

    [Parameter(Mandatory = $true)]
    [string]$NodeExecutablePath,

    [string]$ManifestPath = '',

    [string]$EdgeExecutablePath = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',

    [string]$OutputRoot = '',

    [string]$WinAppPath = 'winapp',

    [switch]$Run
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$lastRank = $StartRank + $Count - 1
if ($lastRank -gt 500) { throw 'The public capture rank range must remain within 1..500.' }
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $repoRoot 'eng\readme-audit\pinned-top500.json'
}
$ManifestPath = [IO.Path]::GetFullPath($ManifestPath)
$AutomationRunnerDirectory = [IO.Path]::GetFullPath($AutomationRunnerDirectory)
$NodeExecutablePath = [IO.Path]::GetFullPath($NodeExecutablePath)
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot ('artifacts\readme-public-same-byte-capture\run-' +
        [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
}
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$captureRoot = Join-Path $OutputRoot 'capture'
$stageRoot = Join-Path $OutputRoot 'staging'
$script:MaximumSourceFileBytes = 64MB
$script:MaximumSourceSnapshotBytes = 256MB
$script:MaximumRunnerPayloadBytes = 2GB
$script:MaximumCaptureReportBytes = 4MB
$script:MaximumCorpusManifestBytes = 4MB
$script:MaximumCorpusFileBytes = 64MB
$script:MaximumCorpusFiles = 15016
$script:MaximumCorpusBytes = 308MB
# Keep corpus traversal bounded like ordinary staging traversal; this includes directories.
$script:MaximumCorpusEntries = 100000
$script:SensitiveExtensions = @('.pfx', '.p12', '.pem', '.key', '.env', '.netrc')
$script:CredentialFileNames = @('.npmrc', '.git-credentials', 'secrets.json', 'credentials.json', 'id_rsa', 'id_ed25519', 'known_hosts')
$script:GitHubCredentialEnvironmentNames = @(
    'JITHUB_README_AUDIT_GITHUB_TOKEN', 'JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID',
    'JITHUB_README_AUDIT_SEMANTIC_HMAC_KEY', 'GITHUB_TOKEN', 'GH_TOKEN',
    'GH_ENTERPRISE_TOKEN', 'GITHUB_ENTERPRISE_TOKEN', 'ACTIONS_ID_TOKEN_REQUEST_TOKEN',
    'ACTIONS_ID_TOKEN_REQUEST_URL', 'GITHUB_APP_TOKEN', 'GITHUB_ACCESS_TOKEN',
    'GITHUB_CLIENT_SECRET', 'GITHUB_APP_PRIVATE_KEY', 'GH_ACCESS_TOKEN', 'GH_CONFIG_DIR')

function Assert-OrdinaryPath {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Description)
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetPathRoot($full)
    $current = $root
    foreach ($segment in $full.Substring($root.Length).Split([char[]]@('\', '/'), [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $segment
        if (-not (Test-Path -LiteralPath $current)) { break }
        if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "$Description contains a redirected path component: '$current'."
        }
    }
    return $full
}

function Read-BoundedFileBytes {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$MaximumBytes,
        [Parameter(Mandatory = $true)][string]$Description)

    if ($MaximumBytes -lt 0 -or $MaximumBytes -gt [int]::MaxValue) {
        throw "$Description has an invalid read bound."
    }
    $fullPath = Assert-OrdinaryPath -Path $Path -Description $Description
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "$Description is missing: '$fullPath'." }

    $stream = [IO.FileStream]::new($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        # Recheck before consuming bytes, after the handle has been opened with sharing that
        # prevents concurrent writers/deleters from changing the file during this bounded read.
        [void](Assert-OrdinaryPath -Path $fullPath -Description $Description)
        $length = $stream.Length
        if ($length -gt $MaximumBytes -or $length -gt [int]::MaxValue) {
            throw "$Description exceeds its $MaximumBytes-byte read cap."
        }

        $bytes = [byte[]]::new([int]$length)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -le 0) { throw "$Description changed while it was being read." }
            $offset += $read
        }
        if ($stream.ReadByte() -ne -1 -or $stream.Length -ne $length) {
            throw "$Description changed or exceeded its read cap while it was being read."
        }
        return ,$bytes
    }
    finally { $stream.Dispose() }
}

function Get-BoundedFileHash {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$MaximumBytes,
        [Parameter(Mandatory = $true)][string]$Description)

    if ($MaximumBytes -lt 0) { throw "$Description has an invalid read bound." }
    $fullPath = Assert-OrdinaryPath -Path $Path -Description $Description
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { throw "$Description is missing: '$fullPath'." }

    $stream = [IO.FileStream]::new($fullPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        [void](Assert-OrdinaryPath -Path $fullPath -Description $Description)
        $length = $stream.Length
        if ($length -gt $MaximumBytes) { throw "$Description exceeds its $MaximumBytes-byte read cap." }

        $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
        try {
            $buffer = [byte[]]::new(64KB)
            $total = 0L
            while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                if ($total -gt $MaximumBytes - $read) { throw "$Description exceeded its read cap while it was being read." }
                $hash.AppendData($buffer, 0, $read)
                $total += $read
            }
            if ($total -ne $length -or $stream.Length -ne $length) { throw "$Description changed while it was being read." }
            return [pscustomobject]@{
                Length = $total
                Sha256 = [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
            }
        }
        finally { $hash.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Read-BoundedJsonFile {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][long]$MaximumBytes,
        [Parameter(Mandatory = $true)][string]$Description,
        [Parameter(Mandatory = $true)][int]$Depth)

    $bytes = Read-BoundedFileBytes -Path $Path -MaximumBytes $MaximumBytes -Description $Description
    $sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $json = $encoding.GetString($bytes)
    if ($json.Length -gt 0 -and $json[0] -eq [char]0xFEFF) { $json = $json.Substring(1) }
    return [pscustomobject]@{
        Value = ConvertFrom-Json -InputObject $json -Depth $Depth
        Sha256 = $sha256
    }
}

function Assert-CapturedCorpusTree {
    param([Parameter(Mandatory = $true)][string]$CorpusDirectory, [Parameter(Mandatory = $true)][int]$Rank)

    $root = Assert-OrdinaryPath -Path $CorpusDirectory -Description "Captured corpus at rank $Rank"
    if (-not (Test-Path -LiteralPath $root -PathType Container)) { throw "Captured corpus is missing at rank $Rank." }

    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($root)
    $entryCount = 0
    $fileCount = 0
    $totalBytes = 0L
    while ($pending.Count -gt 0) {
        $directory = $pending.Pop()
        if (([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Captured corpus contains a redirected directory at rank $Rank."
        }
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
            if (++$entryCount -gt $script:MaximumCorpusEntries) {
                throw "Captured corpus entry count exceeds cap at rank $Rank."
            }
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Captured corpus contains a redirected entry at rank $Rank."
            }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $pending.Push($entry)
                continue
            }
            if (++$fileCount -gt $script:MaximumCorpusFiles) { throw "Captured corpus file count exceeds cap at rank $Rank." }
            $info = [IO.FileInfo]::new($entry); $info.Refresh()
            if ($info.Length -gt $script:MaximumCorpusFileBytes -or $info.Length -gt $script:MaximumCorpusBytes - $totalBytes) {
                throw "Captured corpus bytes exceed cap at rank $Rank."
            }
            $totalBytes += $info.Length
        }
    }
}

function Assert-AvailableReadmeRendered {
    param([Parameter(Mandatory = $true)]$Repository, [Parameter(Mandatory = $true)]$Browser, [Parameter(Mandatory = $true)][int]$Rank)

    if ($Browser.readmeRendered -isnot [bool]) {
        throw "Edge browser report has no Boolean README-rendered status at rank $Rank."
    }
    if ($Repository.readme.available -eq $true -and -not $Browser.readmeRendered) {
        throw "Available pinned README was not rendered at rank $Rank; this capture requires verified rendered-page evidence."
    }
}

function Assert-CapturedRenderedHtml {
    param([Parameter(Mandatory = $true)]$Browser, [Parameter(Mandatory = $true)]$CorpusManifest, [Parameter(Mandatory = $true)][int]$Rank)

    $renderProperty = $CorpusManifest.PSObject.Properties['browserRender']
    $render = if ($null -ne $renderProperty) { $renderProperty.Value } else { $null }
    $statusProperty = if ($null -ne $render) { $render.PSObject.Properties['status'] } else { $null }
    $fileProperty = if ($null -ne $render) { $render.PSObject.Properties['file'] } else { $null }
    $sizeProperty = if ($null -ne $render) { $render.PSObject.Properties['byteSize'] } else { $null }
    $hashProperty = if ($null -ne $render) { $render.PSObject.Properties['sha256'] } else { $null }
    $size = if ($null -ne $sizeProperty) { $sizeProperty.Value } else { $null }
    if ($Browser.readmeRendered -isnot [bool] -or -not $Browser.readmeRendered -or
        $null -eq $statusProperty -or $statusProperty.Value -isnot [string] -or $statusProperty.Value -cne 'passed' -or
        $null -eq $fileProperty -or $fileProperty.Value -isnot [string] -or $fileProperty.Value -cne 'rendered.html' -or
        $null -eq $sizeProperty -or ($size -isnot [int] -and $size -isnot [long]) -or $size -le 0 -or $size -gt 32MB -or
        $null -eq $hashProperty -or $hashProperty.Value -isnot [string] -or $hashProperty.Value -notmatch '^[0-9a-fA-F]{64}$') {
        throw "Available README lacks verified rendered-HTML evidence at rank $Rank."
    }
}

function Get-OrdinaryFiles {
    param([Parameter(Mandatory = $true)][string]$Root, [switch]$ReadmeAuditSource)
    $fullRoot = Assert-OrdinaryPath -Path $Root -Description 'Staging source'
    if (-not (Test-Path -LiteralPath $fullRoot -PathType Container)) { throw "Staging source is missing: '$fullRoot'." }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($fullRoot)
    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    $entries = 0
    while ($pending.Count -gt 0) {
        foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($pending.Pop())) {
            if (++$entries -gt 100000) { throw "Staging source exceeded its bounded entry count: '$fullRoot'." }
            $attributes = [IO.File]::GetAttributes($entry)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Staging source contains a redirected entry: '$entry'." }
            if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                $name = [IO.Path]::GetFileName($entry)
                if ($ReadmeAuditSource -and $name -in @('node_modules', 'artifacts', 'profiles\browser-data')) { continue }
                $pending.Push($entry)
                continue
            }
            $file = [IO.FileInfo]::new($entry)
            if ($ReadmeAuditSource -and [IO.Path]::GetExtension($file.Name).ToLowerInvariant() -notin @('.mjs', '.js', '.json', '.css', '.license')) { continue }
            if ($script:SensitiveExtensions -contains $file.Extension.ToLowerInvariant() -or $script:CredentialFileNames -contains $file.Name) {
                throw "Refusing to stage credential-shaped file '$entry'."
            }
            $files.Add($file)
        }
    }
    return $files
}

function Get-PinnedManifest {
    $manifestPath = Assert-OrdinaryPath -Path $ManifestPath -Description 'Pinned manifest'
    $canonicalPath = Assert-OrdinaryPath -Path (Join-Path $repoRoot 'eng\readme-audit\pinned-top500.json') -Description 'Canonical pinned manifest'
    foreach ($path in @($manifestPath, $canonicalPath)) {
        $info = [IO.FileInfo]::new($path); $info.Refresh()
        if (-not $info.Exists -or $info.Length -le 0 -or $info.Length -gt 4MB) { throw "Pinned manifest is missing or oversized: '$path'." }
    }
    $bytes = Read-BoundedFileBytes -Path $manifestPath -MaximumBytes 4MB -Description 'Pinned manifest bytes'
    $canonicalBytes = Read-BoundedFileBytes -Path $canonicalPath -MaximumBytes 4MB -Description 'Canonical pinned manifest bytes'
    $sha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    $canonicalSha = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($canonicalBytes)).ToLowerInvariant()
    if ($sha -cne $canonicalSha) { throw 'Public capture requires the exact canonical pinned-top500.json bytes.' }
    $manifest = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json -Depth 32
    if ($manifest.schemaVersion -ne 2 -or @($manifest.repositories).Count -ne 500) { throw 'Public capture requires the complete pinned schema-v2 top-500 manifest.' }
    $repositories = @($manifest.repositories | Sort-Object { [int]$_.rank })
    for ($index = 0; $index -lt 500; $index++) {
        $repo = $repositories[$index]
        if ([int]$repo.rank -ne $index + 1 -or [string]$repo.fullName -notmatch '^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$' -or
            [string]$repo.commitSha -notmatch '^[0-9a-fA-F]{40}$' -or [string]$repo.url -notmatch '^https://github\.com/[^/\s]+/[^/\s]+/?$') {
            throw "Pinned manifest has an invalid repository identity at rank $($index + 1)."
        }
        if ($repo.readme.available -eq $true -and ([string]$repo.readme.sha -notmatch '^[0-9a-fA-F]{40}$' -or
            [long]$repo.readme.byteSize -le 0 -or [long]$repo.readme.byteSize -gt 16MB -or
            [string]::IsNullOrWhiteSpace([string]$repo.readme.path) -or [string]$repo.readme.path -match '(^/|\\|(^|/)\.\.?(/|$)|:)')) {
            throw "Pinned README identity is invalid at rank $($index + 1)."
        }
    }
    return [pscustomobject]@{ Bytes = $bytes; Sha256 = $sha; Manifest = $manifest; Repositories = $repositories }
}

function Copy-VerifiedFile {
    param([Parameter(Mandatory = $true)][string]$Source, [Parameter(Mandatory = $true)][string]$Destination, [Parameter(Mandatory = $true)][long]$MaximumBytes)
    $sourcePath = Assert-OrdinaryPath -Path $Source -Description 'Copy source'
    $sourceInfo = [IO.FileInfo]::new($sourcePath); $sourceInfo.Refresh()
    if (-not $sourceInfo.Exists -or $sourceInfo.Length -lt 0 -or $sourceInfo.Length -gt $MaximumBytes) { throw "Input file is missing or exceeds its byte bound: '$sourcePath'." }
    $dest = [IO.Path]::GetFullPath($Destination)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($dest)) | Out-Null
    if (Test-Path -LiteralPath $dest) { throw "Refusing to overwrite staged file '$dest'." }
    $sourceSha = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::Copy($sourcePath, $dest, $false)
    $destInfo = [IO.FileInfo]::new($dest); $destInfo.Refresh()
    $destSha = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourceInfo.Refresh()
    if ($destInfo.Length -ne $sourceInfo.Length -or $destSha -cne $sourceSha) { throw "Input changed while staging: '$sourcePath'." }
    return [pscustomobject]@{ relativePath = $null; byteSize = [long]$destInfo.Length; sha256 = $destSha }
}

function Get-PeMachine {
    param([Parameter(Mandatory = $true)][string]$Path)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $reader = [IO.BinaryReader]::new($stream)
        try {
            if ($stream.Length -lt 256 -or $reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid PE executable '$Path'." }
            $stream.Position = 0x3c; $offset = $reader.ReadInt32()
            if ($offset -lt 0 -or $offset + 6 -gt $stream.Length) { throw "Invalid PE header '$Path'." }
            $stream.Position = $offset
            if ($reader.ReadUInt32() -ne 0x00004550) { throw "Invalid PE signature '$Path'." }
            return [int]$reader.ReadUInt16()
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-GitBlobSha1 {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)
    $header = [Text.Encoding]::ASCII.GetBytes("blob $($Bytes.Length)`0")
    $payload = [byte[]]::new($header.Length + $Bytes.Length)
    [Array]::Copy($header, 0, $payload, 0, $header.Length)
    [Array]::Copy($Bytes, 0, $payload, $header.Length, $Bytes.Length)
    return [Convert]::ToHexString([Security.Cryptography.SHA1]::HashData($payload)).ToLowerInvariant()
}

function New-CaptureSnapshot {
    param([Parameter(Mandatory = $true)][string]$RunId)
    $snapshotRoot = Join-Path $stageRoot $RunId
    [IO.Directory]::CreateDirectory($snapshotRoot) | Out-Null
    $manifest = [Collections.Generic.List[object]]::new()
    $totalBytes = 0L
    $solution = Copy-VerifiedFile -Source (Join-Path $repoRoot 'JitHub.slnx') -Destination (Join-Path $snapshotRoot 'JitHub.slnx') -MaximumBytes 4MB
    $solution.relativePath = 'JitHub.slnx'; $manifest.Add($solution); $totalBytes += $solution.byteSize
    foreach ($source in Get-OrdinaryFiles -Root (Join-Path $repoRoot 'eng\readme-audit') -ReadmeAuditSource) {
        $relative = [IO.Path]::GetRelativePath((Join-Path $repoRoot 'eng\readme-audit'), $source.FullName)
        $dest = Join-Path $snapshotRoot (Join-Path 'eng\readme-audit' $relative)
        $record = Copy-VerifiedFile -Source $source.FullName -Destination $dest -MaximumBytes $script:MaximumSourceFileBytes
        $record.relativePath = ('eng/readme-audit/' + $relative.Replace('\', '/'))
        $manifest.Add($record)
        if ($record.byteSize -gt $script:MaximumSourceSnapshotBytes - $totalBytes) { throw 'Filtered readme-audit source exceeds its transfer bound.' }
        $totalBytes += $record.byteSize
    }
    foreach ($required in @('eng\readme-audit\pinned-top500.json', 'eng\readme-audit\browser-oracle.mjs',
        'eng\readme-audit\closed-details-snapshot.mjs',
        'eng\readme-audit\browser-environment.mjs', 'eng\readme-audit\same-byte-corpus.mjs',
        'eng\readme-audit\same-byte-edge-replay.mjs', 'eng\readme-audit\same-byte-edge-source.mjs',
        'eng\readme-audit\vendor\marked-18.0.5.umd.js', 'eng\readme-audit\vendor\github-emoji-shortcodes.mjs',
        'eng\readme-audit\profiles\github-primer-light-v4.css')) {
        if (-not (Test-Path -LiteralPath (Join-Path $snapshotRoot $required) -PathType Leaf)) { throw "Capture runtime dependency was not staged: '$required'." }
    }
    $runnerRoot = Join-Path $snapshotRoot 'tools\automation'
    foreach ($file in Get-OrdinaryFiles -Root $AutomationRunnerDirectory) {
        $relative = [IO.Path]::GetRelativePath($AutomationRunnerDirectory, $file.FullName)
        $record = Copy-VerifiedFile -Source $file.FullName -Destination (Join-Path $runnerRoot $relative) -MaximumBytes $script:MaximumRunnerPayloadBytes
        $record.relativePath = ('tools/automation/' + $relative.Replace('\', '/'))
        $manifest.Add($record)
        if ($record.byteSize -gt $script:MaximumRunnerPayloadBytes - $totalBytes) { throw 'Automation runner/source staging exceeds its transfer bound.' }
        $totalBytes += $record.byteSize
    }
    $runnerExe = Join-Path $runnerRoot 'JitHub.WinUI.Automation.exe'
    foreach ($required in @($runnerExe, (Join-Path $runnerRoot 'JitHub.WinUI.Automation.dll'),
        (Join-Path $runnerRoot 'JitHub.WinUI.Automation.deps.json'), (Join-Path $runnerRoot 'JitHub.WinUI.Automation.runtimeconfig.json'),
        (Join-Path $runnerRoot 'coreclr.dll'), (Join-Path $runnerRoot 'hostfxr.dll'),
        (Join-Path $runnerRoot 'hostpolicy.dll'), (Join-Path $runnerRoot 'System.Private.CoreLib.dll'))) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Automation runner must be a self-contained x64 publish; missing '$required'." }
    }
    if ((Get-PeMachine -Path $runnerExe) -ne 0x8664) { throw 'Automation runner is not x64.' }
    $nodeRecord = Copy-VerifiedFile -Source $NodeExecutablePath -Destination (Join-Path $snapshotRoot 'tools\node.exe') -MaximumBytes 128MB
    $nodeRecord.relativePath = 'tools/node.exe'; $manifest.Add($nodeRecord)
    if ($nodeRecord.byteSize -gt $script:MaximumRunnerPayloadBytes - $totalBytes) { throw 'Automation/source/Node staging exceeds its total transfer bound.' }
    $totalBytes += $nodeRecord.byteSize
    if ((Get-PeMachine -Path (Join-Path $snapshotRoot 'tools\node.exe')) -ne 0x8664) { throw 'Node executable is not x64.' }
    $nodeVersionOutput = & $NodeExecutablePath --version
    $nodeVersionExitCode = $LASTEXITCODE
    $nodeVersion = (($nodeVersionOutput | Out-String).Trim())
    if ($nodeVersionExitCode -ne 0 -or $nodeVersion -notmatch '^v\d+\.\d+\.\d+$') { throw 'NodeExecutablePath did not pass the standalone Node version probe.' }
    $manifestPath = Join-Path $snapshotRoot 'capture-source-manifest.json'
    $snapshot = [ordered]@{
        schemaVersion = 1
        scope = 'credential-free-public-github-same-byte-browser-capture-input'
        sourceCommitSha = ((& git -C $repoRoot rev-parse HEAD 2>$null | Out-String).Trim().ToLowerInvariant())
        pinnedManifestSha256 = $pinned.Sha256
        automationRunnerSha256 = (Get-FileHash -LiteralPath $runnerExe -Algorithm SHA256).Hash.ToLowerInvariant()
        nodeExecutableSha256 = (Get-FileHash -LiteralPath (Join-Path $snapshotRoot 'tools\node.exe') -Algorithm SHA256).Hash.ToLowerInvariant()
        nodeVersion = $nodeVersion
        stagedBytes = $totalBytes
        files = @($manifest | Sort-Object relativePath)
    }
    if ($snapshot.sourceCommitSha -notmatch '^[0-9a-f]{40}$') { throw 'Could not record source checkout commit.' }
    [IO.File]::WriteAllText($manifestPath, (($snapshot | ConvertTo-Json -Depth 8) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    return [pscustomobject]@{ Root = $snapshotRoot; Runner = $runnerExe; Node = (Join-Path $snapshotRoot 'tools\node.exe'); ManifestPath = $manifestPath; Data = $snapshot }
}

function Get-AndAssertSandboxSnapshot {
    $raw = & $WinAppPath target snapshot sandbox --json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the existing Windows Sandbox state; this script never creates or repairs a Sandbox.' }
    $snapshot = ($raw -join [Environment]::NewLine) | ConvertFrom-Json -Depth 32
    if ($snapshot.executionTarget.kind -cne 'sandbox' -or $snapshot.running -ne $true -or $snapshot.attached -ne $true -or
        [string]$snapshot.capabilities.architecture -cne 'x64' -or $snapshot.capabilities.supportsInteractiveDesktop -ne $true -or
        $snapshot.desktop.effectiveInputReady -ne $true -or [string]::IsNullOrWhiteSpace([string]$snapshot.workRoot)) {
        throw 'Public capture requires the already-running, attached x64 Sandbox; no host browser or fallback is permitted.'
    }
    $managedRoot = [string]$snapshot.capabilities.managedRoot
    if (-not [IO.Path]::IsPathFullyQualified($managedRoot) -or -not [IO.Path]::IsPathFullyQualified([string]$snapshot.workRoot)) {
        throw 'Public capture requires absolute Sandbox managed/work roots.'
    }
    $expectedWorkRoot = [IO.Path]::GetFullPath((Join-Path $managedRoot 'work')).TrimEnd('\', '/')
    $actualWorkRoot = [IO.Path]::GetFullPath([string]$snapshot.workRoot).TrimEnd('\', '/')
    if ($actualWorkRoot -cne $expectedWorkRoot) {
        throw 'Public capture Sandbox work root is not the expected child of its managed root.'
    }
    return $snapshot
}

function Invoke-WinApp {
    param([Parameter(Mandatory = $true)][string[]]$Arguments, [Parameter(Mandatory = $true)][string]$Description)
    Write-Host "[$Description] winapp $($Arguments -join ' ')"
    & $WinAppPath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "winapp $Description failed with exit code $LASTEXITCODE; no host fallback was attempted." }
}

function Initialize-SandboxWorkRoot {
    param([Parameter(Mandatory = $true)]$Snapshot)
    $managedRootLiteral = ([string]$Snapshot.capabilities.managedRoot).Replace("'", "''")
    $workRootLiteral = ([string]$Snapshot.workRoot).Replace("'", "''")
    $guestScript = @"
`$ErrorActionPreference = 'Stop'
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

function ConvertTo-WindowsProcessArgument {
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Argument)
    if ($Argument.IndexOf([char]0) -ge 0) { throw 'Process arguments cannot contain NUL.' }

    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append('"')
    $backslashes = 0
    foreach ($character in $Argument.ToCharArray()) {
        if ($character -eq [char]'\') {
            $backslashes++
            continue
        }

        if ($character -eq [char]'"') {
            for ($index = 0; $index -lt (2 * $backslashes + 1); $index++) { [void]$builder.Append([char]'\') }
            [void]$builder.Append([char]'"')
        }
        else {
            for ($index = 0; $index -lt $backslashes; $index++) { [void]$builder.Append([char]'\') }
            [void]$builder.Append($character)
        }
        $backslashes = 0
    }

    for ($index = 0; $index -lt (2 * $backslashes); $index++) { [void]$builder.Append([char]'\') }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Invoke-CapturedProcess {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][string]$StandardOutputPath,
        [Parameter(Mandatory = $true)][string]$StandardErrorPath)

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.Arguments = (@($Arguments | ForEach-Object { ConvertTo-WindowsProcessArgument -Argument $_ }) -join ' ')

    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    $started = $false
    try {
        if (-not $process.Start()) { throw "Failed to start captured process '$FilePath'." }
        $started = $true
        $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
        $standardErrorTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
        $standardError = $standardErrorTask.GetAwaiter().GetResult()
        $encoding = New-Object System.Text.UTF8Encoding($false)
        [IO.File]::WriteAllText($StandardOutputPath, $standardOutput, $encoding)
        [IO.File]::WriteAllText($StandardErrorPath, $standardError, $encoding)
        return $process.ExitCode
    }
    finally {
        if ($started -and $process -and -not $process.HasExited) {
            try { $process.Kill(); $process.WaitForExit() } catch { }
        }
        if ($process) { $process.Dispose() }
    }
}

function New-GuestBatchScript {
    param([Parameter(Mandatory = $true)][string]$GuestRepo, [Parameter(Mandatory = $true)][string]$GuestOutput, [Parameter(Mandatory = $true)][int]$FirstRank, [Parameter(Mandatory = $true)][int]$Amount, [Parameter(Mandatory = $true)][string]$ExpectedNodeVersion)
    $repo = Join-Path $sandbox.workRoot $GuestRepo
    $runnerArgs = @(
        '--probe=readme-public-same-byte-capture',
        "--manifest=$(Join-Path $repo 'eng\readme-audit\pinned-top500.json')",
        "--browser-script=$(Join-Path $repo 'eng\readme-audit\browser-oracle.mjs')",
        "--node=$(Join-Path $repo 'tools\node.exe')",
        "--edge=$EdgeExecutablePath",
        "--start-rank=$FirstRank",
        "--count=$Amount",
        '--capture-same-byte-corpus',
        "--out=$(Join-Path $repo $GuestOutput)")
    $argsLiteral = ($runnerArgs | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ', '
    $credentialsLiteral = ($script:GitHubCredentialEnvironmentNames | ForEach-Object { "'$_'" }) -join ', '
    $outAbsolute = Join-Path $repo $GuestOutput
    $expectedNodeVersionLiteral = $ExpectedNodeVersion.Replace("'", "''")
    $argumentQuoter = (Get-Command ConvertTo-WindowsProcessArgument -CommandType Function).Definition
    $processLauncher = (Get-Command Invoke-CapturedProcess -CommandType Function).Definition
    $guestProcessHelpers = @"
function ConvertTo-WindowsProcessArgument {
$argumentQuoter
}
function Invoke-CapturedProcess {
$processLauncher
}
"@
    return @"
`$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$guestProcessHelpers
`$credentialNames = @($credentialsLiteral)
foreach (`$name in `$credentialNames) { [Environment]::SetEnvironmentVariable(`$name, `$null, 'Process') }
foreach (`$name in `$credentialNames) { if (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable(`$name, 'Process'))) { throw 'Audit credential environment could not be cleared.' } }
`$repo = '$(($repo).Replace("'", "''"))'
`$out = '$(($outAbsolute).Replace("'", "''"))'
`$runner = Join-Path `$repo 'tools\automation\JitHub.WinUI.Automation.exe'
`$node = Join-Path `$repo 'tools\node.exe'
`$edge = '$(($EdgeExecutablePath).Replace("'", "''"))'
foreach (`$required in @(`$runner, `$node, `$edge, (Join-Path `$repo 'eng\readme-audit\pinned-top500.json'))) { if (-not (Test-Path -LiteralPath `$required -PathType Leaf)) { throw "Guest capture prerequisite is missing: `$required" } }
if (Test-Path -LiteralPath `$out) { throw 'Guest batch output already exists; refusing to reuse or overwrite browser evidence.' }
[IO.Directory]::CreateDirectory(`$out) | Out-Null
`$nodeVersionStdout = Join-Path `$out 'node-version-output.log'
`$nodeVersionStderr = Join-Path `$out 'node-version-error.log'
`$nodeVersionExitCode = Invoke-CapturedProcess -FilePath `$node -Arguments @('--version') -WorkingDirectory `$repo -StandardOutputPath `$nodeVersionStdout -StandardErrorPath `$nodeVersionStderr
`$nodeVersionOutput = (Get-Content -LiteralPath `$nodeVersionStdout -Raw).Trim()
`$nodeVersionError = Get-Content -LiteralPath `$nodeVersionStderr -Raw
if (`$nodeVersionExitCode -ne 0 -or `$nodeVersionOutput -cne '$expectedNodeVersionLiteral' -or -not [string]::IsNullOrWhiteSpace(`$nodeVersionError)) { throw 'Staged Node failed the expected-version probe before any repository capture.' }
`$runnerArgs = @($argsLiteral)
`$stdout = Join-Path `$out 'runner-output.log'
`$stderr = Join-Path `$out 'runner-error.log'
`$runnerExitCode = Invoke-CapturedProcess -FilePath `$runner -Arguments `$runnerArgs -WorkingDirectory `$repo -StandardOutputPath `$stdout -StandardErrorPath `$stderr
Write-Output "publicSameByteCaptureRunnerExitCode=`$runnerExitCode; ranks=$FirstRank..$($FirstRank + $Amount - 1); output=`$out"
exit `$runnerExitCode
"@
}

function Test-CapturedShard {
    param([Parameter(Mandatory = $true)][string]$ShardRoot, [Parameter(Mandatory = $true)]$Shard, [Parameter(Mandatory = $true)][string]$ExpectedManifestSha256)
    $batchPath = Join-Path $ShardRoot ('capture-batches\ranks-{0:D3}-{1:D3}.json' -f $Shard.Start, $Shard.End)
    $batch = (Read-BoundedJsonFile -Path $batchPath -MaximumBytes $script:MaximumCaptureReportBytes -Description 'Guest capture batch report' -Depth 32).Value
    if ($batch.schemaVersion -ne 1 -or $batch.scope -cne 'public-pinned-same-byte-browser-capture-in-existing-windows-sandbox' -or
        $batch.overallReleaseQualification -cne 'not-qualified' -or $batch.authenticatedLiveAuditEquivalent -ne $false -or
        $batch.nativeJitHubRenderComparison -cne 'not-run' -or $batch.sameByteEdgeFullPageReplay -cne 'not-run' -or
        $batch.pinnedManifestSha256 -ine $ExpectedManifestSha256 -or $batch.requestedStartRank -ne $Shard.Start -or $batch.requestedCount -ne $Shard.Count -or
        $batch.executionStatus -cne 'captured-within-browser-only-scope' -or $batch.capturedCaseCount -ne $Shard.Count -or $batch.failedCaseCount -ne 0) {
        throw "Guest batch report scope or pinned identity is invalid for ranks $($Shard.Start)..$($Shard.End)."
    }
    $expected = @($Shard.Repositories)
    foreach ($repository in $expected) {
        $caseId = '{0:D3}-{1}' -f [int]$repository.rank, [regex]::Replace([string]$repository.fullName, '[^A-Za-z0-9]', '-')
        $caseRoot = Join-Path $ShardRoot ('cases\' + $caseId)
        $caseReportPath = Join-Path $caseRoot 'capture.json'
        $case = (Read-BoundedJsonFile -Path $caseReportPath -MaximumBytes $script:MaximumCaptureReportBytes -Description "Capture report at rank $($repository.rank)" -Depth 32).Value
        if ([int]$case.rank -ne [int]$repository.rank -or [string]$case.fullName -ine [string]$repository.fullName -or
            [string]$case.commitSha -ine [string]$repository.commitSha -or
            [string]$case.readmeSha -ine $(if ($repository.readme.available -eq $true) { [string]$repository.readme.sha } else { 'absent' })) {
            throw "Capture identity differs from the pinned manifest at rank $($repository.rank)."
        }
        if ([string]$case.status -ne 'captured') {
            throw "Capture result is not successful for pinned rank $($repository.rank)."
        }
        $browserPath = Join-Path $caseRoot 'browser\browser.json'
        $browserRead = Read-BoundedJsonFile -Path $browserPath -MaximumBytes $script:MaximumCaptureReportBytes -Description "Sanitized Edge browser report at rank $($repository.rank)" -Depth 64
        if ($browserRead.Sha256 -ine [string]$case.browserReportSha256) {
            throw "Sanitized Edge browser report failed its recorded hash at rank $($repository.rank)."
        }
        $browser = $browserRead.Value
        if ($browser.schemaVersion -ne 5 -or [string]$browser.readmeSha -ine [string]$case.readmeSha) {
            throw "Edge browser report does not bind the pinned README at rank $($repository.rank)."
        }
        Assert-AvailableReadmeRendered -Repository $repository -Browser $browser -Rank ([int]$repository.rank)
        if ($repository.readme.available -eq $true) {
            $relative = [string]$browser.sameByteCorpus.manifest
            $corpusRoot = Resolve-SafeBrowserCorpusManifestPath -BrowserRoot (Join-Path $caseRoot 'browser') -RelativePath $relative -Rank ([int]$repository.rank)
            $corpusRoot = Assert-OrdinaryPath -Path $corpusRoot -Description "Same-byte corpus manifest at rank $($repository.rank)"
            $corpusDirectory = Split-Path -Parent $corpusRoot
            # Scan all entries before opening the guest manifest or any file it names.
            # This rejects reparse points and bounds both files and directory-only fanout.
            Assert-CapturedCorpusTree -CorpusDirectory $corpusDirectory -Rank ([int]$repository.rank)
            $manifestRead = Read-BoundedJsonFile -Path $corpusRoot -MaximumBytes $script:MaximumCorpusManifestBytes -Description "Same-byte corpus manifest at rank $($repository.rank)" -Depth 64
            $corpusManifest = $manifestRead.Value
            $manifestSha = $manifestRead.Sha256
            if ($manifestSha -ine [string]$case.corpusManifestSha256 -or $manifestSha -ine [string]$browser.sameByteCorpus.manifestSha256 -or
                $corpusManifest.schemaVersion -ne 2 -or $corpusManifest.complete -ne $true -or
                [string]$corpusManifest.repository.fullName -ine [string]$repository.fullName -or
                [string]$corpusManifest.repository.commitSha -ine [string]$repository.commitSha -or
                [string]$corpusManifest.repository.readmePath -cne [string]$repository.readme.path -or
                [string]$corpusManifest.repository.readmeGitBlobSha1 -ine [string]$repository.readme.sha -or
                [string]$corpusManifest.readme.file -cne 'readme.md' -or
                [long]$corpusManifest.readme.byteSize -ne [long]$repository.readme.byteSize) {
                throw "Same-byte corpus manifest identity failed at rank $($repository.rank)."
            }
            Assert-CapturedRenderedHtml -Browser $browser -CorpusManifest $corpusManifest -Rank ([int]$repository.rank)
            $readmePath = Join-Path (Split-Path -Parent $corpusRoot) 'readme.md'
            $readmeBytes = Read-BoundedFileBytes -Path $readmePath -MaximumBytes ([long]$repository.readme.byteSize) -Description "Captured README at rank $($repository.rank)"
            $readmeSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($readmeBytes)).ToLowerInvariant()
            if ($readmeBytes.LongLength -ne [long]$repository.readme.byteSize -or $readmeSha256 -ine [string]$corpusManifest.readme.sha256) {
                throw "Captured README bytes do not match the corpus manifest at rank $($repository.rank)."
            }
            if ((Get-GitBlobSha1 -Bytes $readmeBytes) -ine [string]$repository.readme.sha) {
                throw "Captured README bytes do not match the pinned Git blob at rank $($repository.rank)."
            }
            if ($corpusManifest.assets -isnot [array] -or $corpusManifest.assets.Count -gt 100000) {
                throw "Captured corpus asset list is missing or oversized at rank $($repository.rank)."
            }
            foreach ($asset in $corpusManifest.assets) {
                $assetSha = [string]$asset.sha256
                if ($assetSha -notmatch '^[0-9a-fA-F]{64}$' -or [long]$asset.byteSize -le 0 -or [long]$asset.byteSize -gt $script:MaximumCorpusFileBytes) {
                    throw "Captured asset metadata is invalid at rank $($repository.rank)."
                }
                $assetPath = Join-Path (Join-Path (Split-Path -Parent $corpusRoot) 'assets') $assetSha.ToLowerInvariant()
                $assetIdentity = Get-BoundedFileHash -Path $assetPath -MaximumBytes ([long]$asset.byteSize) -Description "Captured image asset at rank $($repository.rank)"
                if ($assetIdentity.Length -ne [long]$asset.byteSize -or $assetIdentity.Sha256 -ine $assetSha) {
                    throw "Captured image asset bytes do not match the corpus manifest at rank $($repository.rank)."
                }
            }
            $renderedPath = Join-Path (Split-Path -Parent $corpusRoot) 'rendered.html'
            $renderedIdentity = Get-BoundedFileHash -Path $renderedPath -MaximumBytes 32MB -Description "Captured rendered HTML at rank $($repository.rank)"
            if ($renderedIdentity.Length -ne [long]$corpusManifest.browserRender.byteSize -or
                $renderedIdentity.Sha256 -ine [string]$corpusManifest.browserRender.sha256) {
                throw "Captured rendered HTML bytes failed the manifest hash at rank $($repository.rank)."
            }
        }
        elseif ($browser.readmeRendered -ne $false -or [string]$browser.readmeSha -cne 'absent') {
            throw "Edge capture did not preserve the pinned absent-README state at rank $($repository.rank)."
        }
    }
}

function Resolve-SafeBrowserCorpusManifestPath {
    param(
        [Parameter(Mandatory = $true)][string]$BrowserRoot,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$RelativePath,
        [Parameter(Mandatory = $true)][int]$Rank)

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or $RelativePath.IndexOf([char]0) -ge 0 -or
        [IO.Path]::IsPathRooted($RelativePath) -or $RelativePath.Contains(':')) {
        throw "Same-byte corpus path is unsafe at rank $Rank."
    }

    # Node's path.relative uses the guest platform separator. Accept either separator, but
    # constrain the result to the producer's GUID-named corpus directory and manifest leaf.
    # This rejects traversal, Windows aliases (trailing dots/spaces), device names, and controls.
    $portableRelative = $RelativePath.Replace('\', '/')
    if ($portableRelative -cnotmatch '\Asame-byte-corpus-[0-9a-fA-F]{32}/manifest\.json\z') {
        throw "Same-byte corpus path is unsafe at rank $Rank."
    }

    $browserRootFull = [IO.Path]::GetFullPath($BrowserRoot)
    $relativePath = $portableRelative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $manifestPath = [IO.Path]::GetFullPath((Join-Path $browserRootFull $relativePath))
    $browserPrefix = $browserRootFull.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $manifestPath.StartsWith($browserPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Same-byte corpus escaped browser evidence at rank $Rank."
    }
    return $manifestPath
}

$pinned = Get-PinnedManifest
$selected = @($pinned.Repositories | Where-Object { [int]$_.rank -ge $StartRank -and [int]$_.rank -le $lastRank })
if ($selected.Count -ne $Count) { throw 'Pinned manifest does not contain every requested rank.' }
$shards = [Collections.Generic.List[object]]::new()
for ($offset = 0; $offset -lt $Count; $offset += $ShardSize) {
    $amount = [Math]::Min($ShardSize, $Count - $offset)
    $first = $StartRank + $offset
    $shards.Add([pscustomobject]@{ Start = $first; End = ($first + $amount - 1); Count = $amount; Repositories = @($selected | Where-Object { [int]$_.rank -ge $first -and [int]$_.rank -lt ($first + $amount) }) })
}
if (Test-Path -LiteralPath $OutputRoot) { throw "Refusing to reuse existing capture output root '$OutputRoot'. Choose a fresh run path." }
if ((Test-Path -LiteralPath $captureRoot) -or (Test-Path -LiteralPath $stageRoot)) { throw 'Capture output overlaps prior evidence.' }
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$snapshotData = New-CaptureSnapshot -RunId ([Guid]::NewGuid().ToString('N'))
$plan = [ordered]@{
    scope = 'public-pinned-same-byte-browser-capture-in-existing-windows-sandbox'
    overallReleaseQualification = 'not-qualified'
    authenticatedLiveAuditEquivalent = $false
    liveTop500RankingFreshness = 'not-checked'
    nativeJitHubRenderComparison = 'not-run'
    sameByteEdgeFullPageReplay = 'not-run'
    pinnedManifestSha256 = $pinned.Sha256
    startRank = $StartRank
    count = $Count
    shardSize = $ShardSize
    shardCount = $shards.Count
    sourceSnapshot = $snapshotData.Data
    runnerDirectory = $AutomationRunnerDirectory
    nodeExecutablePath = $NodeExecutablePath
    edgeGuestPath = $EdgeExecutablePath
    outputRoot = $OutputRoot
}
Write-Host ($plan | ConvertTo-Json -Depth 8)
if (-not $Run) {
    Write-Host 'Plan-only: no Sandbox commands were issued. No host browser or UI automation were performed. Re-run with -Run after the candidate paths are reviewed.'
    return
}

if ($script:GitHubCredentialEnvironmentNames | Where-Object { -not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($_, 'Process')) }) {
    throw 'Host process has GitHub audit credentials; this capture wrapper refuses to copy or stage credentials.'
}
$runnerExe = Join-Path $AutomationRunnerDirectory 'JitHub.WinUI.Automation.exe'
if ((Get-PeMachine -Path $runnerExe) -ne 0x8664 -or (Get-PeMachine -Path $NodeExecutablePath) -ne 0x8664) { throw 'Sandbox capture requires x64 Automation and Node executables.' }

$sandbox = Get-AndAssertSandboxSnapshot
$sandboxEpoch = [string]$sandbox.executionTarget.epoch
$runId = [Guid]::NewGuid().ToString('N')
$guestRoot = "readme-public-same-byte/$runId"
$guestRepo = "$guestRoot/repo"
$guestRepoAbsolute = Join-Path ([string]$sandbox.workRoot) ($guestRepo -replace '/', [IO.Path]::DirectorySeparatorChar)
$hostCaptureRoot = $captureRoot
[IO.Directory]::CreateDirectory($hostCaptureRoot) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $hostCaptureRoot 'cases')) | Out-Null
[IO.Directory]::CreateDirectory((Join-Path $hostCaptureRoot 'capture-batches')) | Out-Null
$shardFailures = [Collections.Generic.List[string]]::new()

try {
    $snapshotNow = Get-AndAssertSandboxSnapshot
    if ([string]$snapshotNow.executionTarget.epoch -cne $sandboxEpoch -or [string]$snapshotNow.workRoot -cne [string]$sandbox.workRoot) { throw 'The attached Sandbox changed after planning; refusing to stage in a different guest.' }
    Initialize-SandboxWorkRoot -Snapshot $snapshotNow
    $preflight = "if (Test-Path -LiteralPath '$($guestRepoAbsolute.Replace("'", "''"))') { exit 17 }; exit 0"
    Invoke-WinApp -Arguments @('target', 'exec', 'sandbox', '--cwd', [string]$sandbox.workRoot, '--', 'powershell.exe', '-NoProfile', '-NonInteractive', '-Command', $preflight) -Description 'unique Sandbox staging path preflight'
    Invoke-WinApp -Arguments @('target', 'push', 'sandbox', $snapshotData.Root, $guestRepo) -Description 'pinned readme-audit code, manifest, x64 runner and Node staging'

    foreach ($shard in $shards) {
        $batchName = 'ranks-{0:D3}-{1:D3}' -f $shard.Start, $shard.End
        $guestOutput = "artifacts/public-same-byte-capture/$batchName"
        $guestScript = New-GuestBatchScript -GuestRepo $guestRepo -GuestOutput $guestOutput -FirstRank $shard.Start -Amount $shard.Count -ExpectedNodeVersion ([string]$snapshotData.Data.nodeVersion)
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($guestScript))
        $before = Get-AndAssertSandboxSnapshot
        if ([string]$before.executionTarget.epoch -cne $sandboxEpoch -or [string]$before.workRoot -cne [string]$sandbox.workRoot) { throw 'Sandbox epoch or work root changed before capture batch.' }
        $guestCwd = Join-Path ([string]$sandbox.workRoot) ($guestRepo -replace '/', [IO.Path]::DirectorySeparatorChar)
        $execArgs = @('target', 'exec', 'sandbox', '--cwd', $guestCwd, '--', 'powershell.exe', '-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded)
        $guestExitCode = 0
        try { Invoke-WinApp -Arguments $execArgs -Description "credential-free public Edge capture ranks $($shard.Start)..$($shard.End)" }
        catch { $guestExitCode = 1; $shardFailures.Add("Capture runner failed ranks $($shard.Start)..$($shard.End): $($_.Exception.Message)") }

        $guestOutputRelative = "$guestRepo/$guestOutput"
        $pullRoot = Join-Path $stageRoot ('pulled\' + $batchName)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($pullRoot)) | Out-Null
        try {
            Invoke-WinApp -Arguments @('target', 'pull', 'sandbox', $guestOutputRelative, $pullRoot) -Description "capture evidence pull ranks $($shard.Start)..$($shard.End)"
            Test-CapturedShard -ShardRoot $pullRoot -Shard $shard -ExpectedManifestSha256 $pinned.Sha256
            foreach ($repo in $shard.Repositories) {
                $caseId = '{0:D3}-{1}' -f [int]$repo.rank, [regex]::Replace([string]$repo.fullName, '[^A-Za-z0-9]', '-')
                $sourceCase = Join-Path $pullRoot ('cases\' + $caseId)
                $destCase = Join-Path $hostCaptureRoot ('cases\' + $caseId)
                if (Test-Path -LiteralPath $destCase) { throw "Refusing to overwrite prior host capture case '$destCase'." }
                if (Test-Path -LiteralPath $sourceCase -PathType Container) {
                    $sourceCaseFull = Assert-OrdinaryPath -Path $sourceCase -Description 'Validated capture case'
                    $sourcePrefix = $sourceCaseFull.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
                    $sourceFiles = Get-OrdinaryFiles -Root $sourceCase
                    foreach ($sourceFile in $sourceFiles) {
                        $sourceFileFull = Assert-OrdinaryPath -Path $sourceFile.FullName -Description 'Validated capture file'
                        if (-not $sourceFileFull.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
                            throw "Capture file escaped its case directory: '$sourceFileFull'."
                        }
                        $relativeFile = $sourceFileFull.Substring($sourcePrefix.Length)
                        $destinationFile = Join-Path $destCase $relativeFile
                        Copy-VerifiedFile -Source $sourceFileFull -Destination $destinationFile -MaximumBytes $script:MaximumCorpusFileBytes | Out-Null
                    }
                }
            }
            $sourceBatch = Join-Path $pullRoot ('capture-batches\ranks-{0:D3}-{1:D3}.json' -f $shard.Start, $shard.End)
            Copy-VerifiedFile -Source $sourceBatch -Destination (Join-Path $hostCaptureRoot ('capture-batches\' + $batchName + '.json')) -MaximumBytes $script:MaximumCaptureReportBytes | Out-Null
            if ($guestExitCode -ne 0) { $shardFailures.Add("Capture batch returned nonzero after preserving rank evidence: $($shard.Start)..$($shard.End).") }
        }
        catch { $shardFailures.Add("Capture evidence validation/pull failed ranks $($shard.Start)..$($shard.End): $($_.Exception.Message)") }
        Write-Host "Public same-byte capture batch $batchName complete; candidate status is not-qualified."
    }
}
catch { $shardFailures.Add($_.Exception.Message) }

$runReport = [ordered]@{
    schemaVersion = 1
    scope = 'public-pinned-same-byte-browser-capture-in-existing-windows-sandbox'
    executionStatus = if ($shardFailures.Count -eq 0) { 'captured-within-browser-only-scope' } else { 'incomplete-or-failed' }
    overallReleaseQualification = 'not-qualified'
    authenticatedLiveAuditEquivalent = $false
    liveTop500RankingFreshness = 'not-checked'
    nativeJitHubRenderComparison = 'not-run'
    sameByteEdgeFullPageReplay = 'not-run'
    executionTargetEpoch = $sandboxEpoch
    sandboxArchitecture = [string]$sandbox.capabilities.architecture
    pinnedManifestSha256 = $pinned.Sha256
    sourceSnapshotManifestSha256 = (Get-FileHash -LiteralPath $snapshotData.ManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    capturedStartRank = $StartRank
    requestedCount = $Count
    shardSize = $ShardSize
    completedBatchCount = @(Get-ChildItem -LiteralPath (Join-Path $hostCaptureRoot 'capture-batches') -Filter 'ranks-*.json' -File).Count
    failures = @($shardFailures)
    completedAtUtc = [DateTimeOffset]::UtcNow
}
$reportPath = Join-Path $OutputRoot 'public-same-byte-capture-run.json'
[IO.File]::WriteAllText($reportPath, (($runReport | ConvertTo-Json -Depth 12) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
Write-Host "Public capture report: '$reportPath'. Scope remains unqualified and browser-only."
if ($shardFailures.Count -gt 0) { throw "Public pinned capture has $($shardFailures.Count) failed batch gate(s); evidence is preserved at '$OutputRoot'." }
Write-Host 'Credential-free public pinned README/asset capture completed. Run the existing Sandbox offline replay separately for native comparison; this report does not satisfy the authenticated live top-500 release gate.'
