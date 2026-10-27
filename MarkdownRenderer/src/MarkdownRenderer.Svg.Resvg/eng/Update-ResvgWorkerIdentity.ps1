[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('i686-pc-windows-msvc', 'x86_64-pc-windows-msvc', 'aarch64-pc-windows-msvc')]
    [string] $Target,

    [Parameter(Mandatory)]
    [string] $BuildReceipt
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ResvgWorkerIdentity.psm1') -Force

$providerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$ridMap = @{
    'i686-pc-windows-msvc' = 'win-x86'
    'x86_64-pc-windows-msvc' = 'win-x64'
    'aarch64-pc-windows-msvc' = 'win-arm64'
}
$rid = $ridMap[$Target]
$expected = (Get-ResvgWorkerIdentityRids)[$rid]
$protocol = Get-ResvgWorkerProtocolVersions $providerRoot
if ($protocol.Host -ne $protocol.Native) {
    throw "Host and native worker protocol versions differ ($($protocol.Host) vs $($protocol.Native))."
}
$sourceFingerprint = Get-ResvgWorkerSourceFingerprint $providerRoot
$receiptPath = [IO.Path]::GetFullPath($BuildReceipt)
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
    throw "Official worker build receipt is missing: '$receiptPath'."
}
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -Depth 8
if ([int]$receipt.schemaVersion -ne 1 -or
    [string]$receipt.target -cne $Target -or
    [string]$receipt.rid -cne $rid -or
    [string]$receipt.sourceFingerprintBefore -cne $sourceFingerprint -or
    [string]$receipt.sourceFingerprintAfter -cne $sourceFingerprint -or
    [int]$receipt.hostProtocolVersion -ne $protocol.Host -or
    [int]$receipt.nativeProtocolVersion -ne $protocol.Native -or
    [string]$receipt.rustcCommit -notmatch '^[0-9a-f]{40}$' -or
    [bool]$receipt.reproducibleComparisonPassed -ne $true) {
    throw 'Official worker build receipt is stale, incomplete, or does not record a passing reproducible comparison.'
}
$expectedBuildFingerprint = Get-ResvgWorkerBuildFingerprint $sourceFingerprint $Target ([string]$receipt.rustcCommit)
if ([string]$receipt.buildFingerprint -cne $expectedBuildFingerprint) {
    throw 'Official worker build receipt has an invalid build fingerprint.'
}

$binaryPath = [IO.Path]::GetFullPath([string]$receipt.outputPath)
$expectedOutputPath = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($receiptPath)) 'markdown_renderer_resvg_worker.exe'))
if ($binaryPath -cne $expectedOutputPath) {
    throw 'Official worker build receipt points outside its exact Cargo release output directory.'
}
$buildImage = Get-ResvgWorkerImageInfo $binaryPath
$runtimeRelativePath = "runtimes\$rid\native\MarkdownRenderer.Svg.Resvg.Worker.exe"
$runtimePath = Join-Path $providerRoot $runtimeRelativePath
if ($buildImage.machine -cne [string]$expected.machine -or
    $buildImage.bytes -ne [long]$receipt.outputBytes -or
    $buildImage.sha256 -cne [string]$receipt.outputSha256 -or
    $buildImage.normalizedSha256 -cne [string]$receipt.outputNormalizedSha256) {
    throw 'Build receipt no longer matches the fresh official worker output.'
}
& (Join-Path $PSScriptRoot 'Compare-ResvgWorkerBinary.ps1') -Expected $runtimePath -Actual $binaryPath | Out-Null
$runtimeImage = Get-ResvgWorkerImageInfo $runtimePath
if ($runtimeImage.machine -cne [string]$expected.machine -or
    $runtimeImage.bytes -ne $buildImage.bytes -or
    $runtimeImage.normalizedSha256 -cne $buildImage.normalizedSha256) {
    throw 'Current runtime worker differs from the freshly built, reproducibly compared output.'
}

$identityPath = Join-Path $providerRoot 'RESVG_WORKER_IDENTITY.json'
$workers = [Collections.Generic.List[object]]::new()
$knownRids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($knownRid in (Get-ResvgWorkerIdentityRids).Keys) { $null = $knownRids.Add([string]$knownRid) }
if (Test-Path -LiteralPath $identityPath -PathType Leaf) {
    $prior = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json -Depth 16
    if ([int]$prior.schemaVersion -ne 1 -or
        [string]$prior.workerFileName -cne 'MarkdownRenderer.Svg.Resvg.Worker.exe') {
        throw 'Refusing to update an unsupported worker identity manifest.'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in @($prior.workers)) {
        $entryRid = [string]$entry.rid
        if (-not $knownRids.Contains($entryRid) -or -not $seen.Add($entryRid)) {
            throw "Existing worker identity contains an unknown or duplicate RID '$entryRid'."
        }
        if ($entryRid -cne $rid) { $workers.Add($entry) }
    }
}
$workers.Add([ordered]@{
    rid = $rid
    target = $Target
    machine = $runtimeImage.machine
    bytes = [long]$runtimeImage.bytes
    sha256 = $runtimeImage.sha256
    normalizedSha256 = $runtimeImage.normalizedSha256
    sourceFingerprint = $sourceFingerprint
    buildFingerprint = $expectedBuildFingerprint
    rustcCommit = ([string]$receipt.rustcCommit).ToLowerInvariant()
    hostProtocolVersion = $protocol.Host
    nativeProtocolVersion = $protocol.Native
    buildOutputBytes = [long]$buildImage.bytes
    buildOutputSha256 = $buildImage.sha256
    buildOutputNormalizedSha256 = $buildImage.normalizedSha256
})
$orderedRids = @('win-x86', 'win-x64', 'win-arm64')
$sortedWorkers = @($workers | Sort-Object { [Array]::IndexOf($orderedRids, [string]$_.rid) })
$manifest = [ordered]@{
    schemaVersion = 1
    workerFileName = 'MarkdownRenderer.Svg.Resvg.Worker.exe'
    workers = $sortedWorkers
}
$json = $manifest | ConvertTo-Json -Depth 12
$temporaryPath = "$identityPath.$([Guid]::NewGuid().ToString('N')).tmp"
try {
    [IO.File]::WriteAllText($temporaryPath, $json + "`n", [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporaryPath -Destination $identityPath -Force
}
finally {
    if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryPath -Force
    }
}
Write-Output "Recorded fresh reproducible identity for $rid. All other RID records retain their own build fingerprint and must be rebuilt if stale."
