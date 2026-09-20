[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $PackageDirectory = (Join-Path $PSScriptRoot '..\artifacts\packages')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-LatestPackage([string] $packageId) {
    $matches = Get-ChildItem -LiteralPath $PackageDirectory -Filter "$packageId.*.nupkg" -File |
        Where-Object { $_.Name -notlike '*.symbols.nupkg' } |
        Sort-Object LastWriteTimeUtc -Descending
    if (-not $matches) {
        throw "Required package '$packageId' was not found in '$PackageDirectory'."
    }
    return $matches[0]
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

$core = Get-LatestPackage 'MarkdownRenderer.Core'
$winui = Get-LatestPackage 'MarkdownRenderer.WinUI'
$coreEntries = Get-PackageEntries $core
$winuiEntries = Get-PackageEntries $winui

$leanCompressedBytes = $core.Length + $winui.Length
$leanManagedBytes = @($coreEntries + $winuiEntries |
    Where-Object { $_.Name -match '^lib/.+\.dll$' } |
    Measure-Object -Property Length -Sum).Sum
$leanNativeEntries = @($coreEntries + $winuiEntries |
    Where-Object {
        $_.Name -match '(^|/)runtimes/.+/native/' -or
        ($_.Name -notmatch '^lib/' -and $_.Name -match '\.(dll|exe|so|dylib)$')
    })

# The 1.0 surface includes unified selection, touch, accessibility, and lazy
# layout infrastructure. Keep measured headroom without allowing native code
# to leak into the lean pair (checked separately immediately below).
Assert-Max $leanCompressedBytes (525KB) 'Core + WinUI compressed size'
Assert-Max $leanManagedBytes (1.2MB) 'Core + WinUI renderer-owned managed code'
if ($leanNativeEntries.Count -ne 0) {
    throw "Core + WinUI contain native payload: $($leanNativeEntries.Name -join ', ')."
}

$optionalCaps = [ordered]@{
    'MarkdownRenderer.Svg.Resvg' = 5MB
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
        foreach ($asset in $nativeAssets) {
            if ($isResvg) {
                Assert-Max $asset.Length (3.5MB) "$displayName selected deployment asset '$($asset.Name)'"
            }
            else {
                Assert-Max $asset.CompressedLength $entry.Value "$displayName selected-RID asset '$($asset.Name)'"
            }
        }
        if ($isResvg) {
            Assert-Max $package.Length $entry.Value 'resvg all-RID compressed package'
        }
    }
    else {
        Assert-Max $package.Length $entry.Value "$($entry.Key) compressed package"
    }
}

Write-Host "Package size gates passed. Lean compressed: $leanCompressedBytes bytes; managed: $leanManagedBytes bytes."
