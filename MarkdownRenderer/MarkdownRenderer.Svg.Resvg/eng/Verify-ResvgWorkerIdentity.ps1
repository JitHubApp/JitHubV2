[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $WorkerRoot,

    [Parameter(Mandatory)]
    [ValidateSet('win-x86', 'win-x64', 'win-arm64')]
    [string] $Rid,

    [string] $IdentityPath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ResvgWorkerIdentity.psm1') -Force

$root = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $WorkerRoot).Path)
if ([string]::IsNullOrWhiteSpace($IdentityPath)) {
    $IdentityPath = Join-Path $root 'RESVG_WORKER_IDENTITY.json'
}
$identityFile = [IO.Path]::GetFullPath($IdentityPath)
if (-not (Test-Path -LiteralPath $identityFile -PathType Leaf)) {
    throw "Resvg worker identity is missing: '$identityFile'. Rebuild and reproducibly compare all three workers before refreshing it."
}

$identity = Get-Content -LiteralPath $identityFile -Raw | ConvertFrom-Json -Depth 16
if ([int]$identity.schemaVersion -ne 1 -or
    [string]$identity.workerFileName -cne 'MarkdownRenderer.Svg.Resvg.Worker.exe') {
    throw 'Resvg worker identity has an unsupported schema or executable name.'
}
$protocol = Get-ResvgWorkerProtocolVersions ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')))
if ($protocol.Host -ne $protocol.Native) {
    throw "Host and native worker protocol versions differ ($($protocol.Host) vs $($protocol.Native))."
}
$sourceFingerprint = Get-ResvgWorkerSourceFingerprint ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')))
$rids = Get-ResvgWorkerIdentityRids
$knownRids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($knownRid in $rids.Keys) { $null = $knownRids.Add([string]$knownRid) }
$records = @($identity.workers)
if ($records.Count -ne $rids.Count) {
    throw "Resvg worker identity must contain exactly $($rids.Count) architecture records; found $($records.Count)."
}
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$rustcCommits = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$selectedRecord = $null
foreach ($record in $records) {
    $recordRid = [string]$record.rid
    if (-not $knownRids.Contains($recordRid) -or -not $seen.Add($recordRid)) {
        throw "Resvg worker identity contains an unknown or duplicate RID '$recordRid'."
    }
    $expected = $rids[$recordRid]
    $requiredHashes = @('sourceFingerprint', 'buildFingerprint', 'sha256', 'normalizedSha256', 'buildOutputSha256', 'buildOutputNormalizedSha256')
    foreach ($field in $requiredHashes) {
        if ([string]$record.$field -notmatch '^[0-9a-f]{64}$') {
            throw "Resvg worker identity field '$field' for '$recordRid' is missing or invalid."
        }
    }
    if ([string]$record.target -cne [string]$expected.target -or
        [string]$record.machine -cne [string]$expected.machine -or
        [long]$record.bytes -le 0 -or
        [long]$record.buildOutputBytes -ne [long]$record.bytes -or
        [string]$record.normalizedSha256 -cne [string]$record.buildOutputNormalizedSha256 -or
        [int]$record.hostProtocolVersion -ne $protocol.Host -or
        [int]$record.nativeProtocolVersion -ne $protocol.Native -or
        [string]$record.sourceFingerprint -cne $sourceFingerprint) {
        throw "Resvg worker identity for '$recordRid' is stale or inconsistent with current sources, protocol, architecture, or compared build output."
    }
    $relativePath = "runtimes\$recordRid\native\MarkdownRenderer.Svg.Resvg.Worker.exe"
    $workerPath = Join-Path $root $relativePath
    $image = Get-ResvgWorkerImageInfo $workerPath
    if ($image.machine -cne [string]$record.machine -or
        $image.bytes -ne [long]$record.bytes -or
        $image.sha256 -cne [string]$record.sha256 -or
        $image.normalizedSha256 -cne [string]$record.normalizedSha256) {
        throw "Resvg worker '$relativePath' does not match its source-bound identity."
    }
    $rustcCommit = [string]$record.rustcCommit
    if ($rustcCommit -notmatch '^[0-9a-f]{40}$' -or
        [string]$record.buildFingerprint -cne (Get-ResvgWorkerBuildFingerprint $sourceFingerprint ([string]$expected.target) $rustcCommit)) {
        throw "Resvg worker build fingerprint for '$recordRid' is invalid."
    }
    $null = $rustcCommits.Add($rustcCommit)
    if ($recordRid -ceq $Rid) { $selectedRecord = $record }
}
if ($seen.Count -ne $rids.Count -or $null -eq $selectedRecord -or $rustcCommits.Count -ne 1) {
    throw 'Resvg worker identity does not cover the complete RID set, use one Rust toolchain, or include the selected RID.'
}
Write-Output "Verified source-bound resvg worker identity for all three RIDs; selected $Rid."
