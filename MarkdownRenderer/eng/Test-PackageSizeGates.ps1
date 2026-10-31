[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $PackageDirectory = (Join-Path $PSScriptRoot '..\artifacts\packages')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
. (Join-Path $PSScriptRoot 'PackageSizeGates.Common.ps1')

function Get-LatestPackage([string] $packageId) {
    # The package ID must be followed by a numeric version. A
    # 'MarkdownRenderer.*' glob also matches every MarkdownRenderer.<pack>
    # package, so it could pick a feature pack instead of the renderer.
    $fileNamePattern = '^' + [regex]::Escape($packageId) + '\.[0-9][0-9A-Za-z.+-]*\.nupkg$'
    $candidates = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File |
        Where-Object { $_.Name -notlike '*.symbols.nupkg' -and $_.Name -match $fileNamePattern } |
        Sort-Object LastWriteTimeUtc -Descending)
    if ($candidates.Count -eq 0) {
        throw "Required package '$packageId' was not found in '$PackageDirectory'."
    }
    return $candidates[0]
}

function Get-PackageEntries([System.IO.FileInfo] $package) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        return @($archive.Entries | ForEach-Object {
            [pscustomobject]@{
                Name = $_.FullName
                Length = $_.Length
                CompressedLength = $_.CompressedLength
            }
        })
    }
    finally {
        $archive.Dispose()
    }
}

function Assert-Max([long] $actual, [long] $maximum, [string] $label) {
    if ($actual -gt $maximum) {
        throw "$label is $actual bytes; the gate is $maximum bytes."
    }
}

$renderer = Get-LatestPackage 'MarkdownRenderer'
$rendererEntries = Get-PackageEntries $renderer

$rendererCompressedBytes = $renderer.Length
$rendererManagedBytes = [long] (@($rendererEntries |
    Where-Object { $_.Name -match '^lib/.+\.dll$' } |
    Measure-Object -Property Length -Sum).Sum)
$rendererNativeEntries = @($rendererEntries |
    Where-Object {
        $_.Name -match '(^|/)runtimes/.+/native/' -or
        ($_.Name -notmatch '^lib/' -and $_.Name -match '\.(dll|exe|so|dylib)$')
    })

# Budgets are measured sizes plus about 10% headroom. The compressed budget is
# the sum of the earlier engine/viewer (900 KiB) and performance-session
# (128 KiB) budgets. The managed budget is the measured MarkdownRenderer.dll
# (2,302,464 bytes on 2026-10-27) plus about 10%: the GFM, GitHub, HTML and
# performance code it now holds had no managed budget of its own. See
# docs/markdown-renderer/packaging-and-distribution.md. Native code must not
# leak into the MarkdownRenderer package (checked separately immediately below).
Assert-Max $rendererCompressedBytes (1028KB) 'MarkdownRenderer compressed package'
Assert-Max $rendererManagedBytes (2475KB) 'MarkdownRenderer renderer-owned managed code'
if ($rendererNativeEntries.Count -ne 0) {
    throw "MarkdownRenderer contains native payload: $($rendererNativeEntries.Name -join ', ')."
}

$optionalCaps = [ordered]@{
    'MarkdownRenderer.Svg.Resvg' = 5950KB
    'MarkdownRenderer.Math' = 2.3MB
    'MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common' = 6MB
    'MarkdownRenderer.Mermaid' = 12MB
}

foreach ($entry in $optionalCaps.GetEnumerator()) {
    $package = Get-LatestPackage $entry.Key
    $entries = Get-PackageEntries $package
    if ($entry.Key -eq 'MarkdownRenderer.Svg.Resvg' -or $entry.Key -eq 'MarkdownRenderer.Mermaid') {
        $isResvg = $entry.Key -eq 'MarkdownRenderer.Svg.Resvg'
        $nativeName = if ($isResvg) { 'MarkdownRenderer\.Svg\.Resvg\.Worker\.exe' } else { 'MarkdownRenderer\.Mermaid\.Native\.dll' }
        $displayName = if ($isResvg) { 'resvg' } else { 'Mermaid' }
        $nativeAssets = @($entries | Where-Object { $_.Name -match "^runtimes/win-(x86|x64|arm64)/native/$nativeName$" })
        if ($nativeAssets.Count -ne 3) {
            throw "$displayName must contain exactly one native asset for x86, x64, and ARM64."
        }
        $providerAssemblyPath = 'lib/net10.0-windows10.0.26100/MarkdownRenderer.Svg.Resvg.dll'
        foreach ($asset in $nativeAssets) {
            if ($isResvg) {
                Assert-Max $asset.Length (4500KB) "$displayName selected-RID worker '$($asset.Name)'"
                $ridMatch = [regex]::Match($asset.Name, '^runtimes/(?<rid>win-(x86|x64|arm64))/native/')
                if (-not $ridMatch.Success) {
                    throw "The resvg native asset '$($asset.Name)' has no supported selected RID."
                }
                $deployment = Get-ResvgSelectedDeploymentPayload `
                    -Entries $entries `
                    -Rid $ridMatch.Groups['rid'].Value `
                    -ProviderAssemblyPath $providerAssemblyPath
                Assert-ResvgSelectedDeploymentWithinCap -Deployment $deployment -MaximumBytes (4500KB)
            }
            else {
                Assert-Max $asset.CompressedLength $entry.Value "$displayName selected-RID asset '$($asset.Name)'"
            }
        }
        if ($isResvg) {
            Assert-ResvgPackageContentBoundaries -Entries $entries
            Assert-Max $package.Length $entry.Value 'resvg all-RID compressed package'
        }
    }
    else {
        Assert-Max $package.Length $entry.Value "$($entry.Key) compressed package"
    }
}

Write-Host "Package size gates passed. MarkdownRenderer compressed: $rendererCompressedBytes bytes; managed: $rendererManagedBytes bytes."
