[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64', 'ARM64')]
    [string[]]$Platform = @('x86', 'x64', 'ARM64'),
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$targets = @{
    x86 = 'i686-pc-windows-msvc'
    x64 = 'x86_64-pc-windows-msvc'
    ARM64 = 'aarch64-pc-windows-msvc'
}
$rids = @{ x86 = 'win-x86'; x64 = 'win-x64'; ARM64 = 'win-arm64' }
$forbidden = @('merman-layout-elk', 'eclipse-elk', 'elkjs')

& (Join-Path $PSScriptRoot 'Verify-Bindings.ps1')
$tree = & cargo tree --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml') --locked --edges features 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw $tree }
foreach ($name in $forbidden) {
    if ($tree -match [regex]::Escape($name)) { throw "Forbidden ELK dependency resolved: $name" }
}
if ($tree -match 'merman feature "layout-elk"') { throw 'Forbidden Merman layout-elk feature resolved.' }

if (-not $SkipTests) {
    & cargo test --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml') --locked --release
    if ($LASTEXITCODE -ne 0) { throw 'Native unit tests failed.' }
}

foreach ($name in $Platform) {
    $target = $targets[$name]
    & cargo build --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml') --locked --release --target $target
    if ($LASTEXITCODE -ne 0) { throw "Native build failed for $name." }
    $source = Join-Path $PSScriptRoot "target/$target/release/MarkdownRenderer_Mermaid_Native.dll"
    $destination = Join-Path (Split-Path -Parent $PSScriptRoot) "runtimes/$($rids[$name])/native/MarkdownRenderer.Mermaid.Native.dll"
    Copy-Item -LiteralPath $source -Destination $destination -Force
}
