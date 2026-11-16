<#
.SYNOPSIS
Installs CI-built resvg workers and records their source-bound identity.

.DESCRIPTION
Use this when a local toolchain cannot reproduce CI's worker bytes (for
example a different MSVC static CRT), so Build-ResvgWorker.ps1 -RefreshIdentity
cannot pass its local reproducible comparison. RebuildRoot is the
resvg-worker-rebuilds artifact uploaded by the "Rebuild and verify every pinned
resvg worker" CI step, from a run on the same worker sources:

    <RebuildRoot>\win-x86\MarkdownRenderer.Svg.Resvg.Worker.exe
    <RebuildRoot>\win-x64\MarkdownRenderer.Svg.Resvg.Worker.exe
    <RebuildRoot>\win-arm64\MarkdownRenderer.Svg.Resvg.Worker.exe

The script replaces all three runtime workers together and records each one as
its own build output under the current source fingerprint and pinned rustc
commit. It does not prove reproducibility itself: CI's rebuild-and-compare step
still rebuilds every worker from source and must match these bytes exactly.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $RebuildRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'ResvgWorkerIdentity.psm1') -Force

$providerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $RebuildRoot).Path)
& (Join-Path $PSScriptRoot 'Verify-ResvgSupplyChain.ps1') | Out-Null
$protocol = Get-ResvgWorkerProtocolVersions $providerRoot
if ($protocol.Host -ne $protocol.Native) {
    throw "Host and native worker protocol versions differ ($($protocol.Host) vs $($protocol.Native))."
}
$sourceFingerprint = Get-ResvgWorkerSourceFingerprint $providerRoot
$rustcCommit = Get-ResvgWorkerPinnedRustcCommit $providerRoot
$rids = Get-ResvgWorkerIdentityRids
$workerFileName = 'MarkdownRenderer.Svg.Resvg.Worker.exe'

# Validate the complete artifact before any runtime worker is replaced.
$entries = @([IO.Directory]::EnumerateFileSystemEntries($root) | ForEach-Object { [IO.Path]::GetFileName($_) })
$unexpected = @($entries | Where-Object { -not $rids.Contains([string]$_) })
if ($unexpected.Count -ne 0) {
    throw "Rebuild artifact contains unexpected entries: $($unexpected -join ', ')."
}
$builds = [ordered]@{}
foreach ($rid in $rids.Keys) {
    $ridDirectory = Join-Path $root $rid
    if (-not (Test-Path -LiteralPath $ridDirectory -PathType Container)) {
        throw "Rebuild artifact is missing the '$rid' worker directory; import all three RIDs together."
    }
    $files = @([IO.Directory]::EnumerateFileSystemEntries($ridDirectory))
    if ($files.Count -ne 1 -or [IO.Path]::GetFileName($files[0]) -cne $workerFileName) {
        throw "Rebuild artifact directory '$rid' must contain exactly '$workerFileName'."
    }
    $image = Get-ResvgWorkerImageInfo $files[0]
    if ($image.machine -cne [string]$rids[$rid].machine) {
        throw "Rebuilt '$rid' worker has PE architecture '$($image.machine)', expected '$($rids[$rid].machine)'."
    }
    $builds[$rid] = $image
}

$records = [Collections.Generic.List[object]]::new()
foreach ($rid in $rids.Keys) {
    $build = $builds[$rid]
    $runtimePath = Join-Path $providerRoot "runtimes\$rid\native\$workerFileName"
    Copy-Item -LiteralPath $build.path -Destination $runtimePath -Force
    $runtimeImage = Get-ResvgWorkerImageInfo $runtimePath
    if ($runtimeImage.bytes -ne $build.bytes -or $runtimeImage.sha256 -cne $build.sha256) {
        throw "The installed '$rid' worker differs from the rebuild artifact."
    }
    $records.Add((New-ResvgWorkerIdentityRecord $rid $sourceFingerprint $rustcCommit $protocol $runtimeImage $build))
}
if ((Get-ResvgWorkerSourceFingerprint $providerRoot) -cne $sourceFingerprint) {
    throw 'Worker source/build inputs changed during import; refusing to record an identity.'
}
Save-ResvgWorkerIdentity (Join-Path $providerRoot 'RESVG_WORKER_IDENTITY.json') @($records)

foreach ($rid in $rids.Keys) {
    & (Join-Path $PSScriptRoot 'Verify-ResvgWorkerIdentity.ps1') -WorkerRoot $providerRoot -Rid $rid | Out-Null
}
Write-Output "Installed CI-built workers for all three RIDs and recorded their identity. CI's rebuild-and-compare step remains the reproducibility gate."
