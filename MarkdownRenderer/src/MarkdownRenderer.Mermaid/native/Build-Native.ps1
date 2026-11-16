[CmdletBinding()]
param(
    [ValidateSet('x86', 'x64', 'ARM64')]
    [string[]]$Platform = @('x86', 'x64', 'ARM64'),
    [switch]$SkipTests,
    [string]$PinnedRepositoryPath,
    [string]$PreparationRoot
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
$preparedSource = & (Join-Path $PSScriptRoot 'Prepare-MermanSource.ps1') -PinnedRepositoryPath $PinnedRepositoryPath -PreparationRoot $PreparationRoot
$cargoSourceArguments = [string[]]$preparedSource.CargoArguments
# Cargo can redirect its build outputs through CARGO_TARGET_DIR or local configuration.
# Ask Cargo for the effective path rather than copying a stale default-target DLL.
$cargoMetadataJson = & cargo @cargoSourceArguments metadata --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml') --locked --no-deps --format-version 1
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the native Cargo target directory.' }
$cargoMetadata = $cargoMetadataJson | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($cargoMetadata.target_directory)) { throw 'Cargo metadata omitted its target directory.' }
$effectiveCargoTargetRoot = [System.IO.Path]::GetFullPath($cargoMetadata.target_directory)
$tree = & cargo @cargoSourceArguments tree --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml') --locked --edges features 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw $tree }
foreach ($name in $forbidden) {
    if ($tree -match [regex]::Escape($name)) { throw "Forbidden ELK dependency resolved: $name" }
}
if ($tree -match 'merman feature "layout-elk"') { throw 'Forbidden Merman layout-elk feature resolved.' }

if (-not $SkipTests) {
    & cargo @cargoSourceArguments test --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml') --locked --release
    if ($LASTEXITCODE -ne 0) { throw 'Native unit tests failed.' }
}

$verboseVersion = @(& rustc -vV)
$sysroot = (& rustc --print sysroot).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sysroot)) { throw 'The Rust sysroot could not be resolved.' }
$rustCommitHash = @($verboseVersion | Where-Object { $_ -like 'commit-hash: *' } |
    ForEach-Object { $_.Substring('commit-hash: '.Length).Trim() })
if ($rustCommitHash.Count -ne 1 -or $rustCommitHash[0] -notmatch '^[0-9a-f]{40}$') { throw 'The Rust commit hash could not be resolved.' }
$cargoHome = if ([string]::IsNullOrWhiteSpace($env:CARGO_HOME)) { Join-Path $env:USERPROFILE '.cargo' } else { $env:CARGO_HOME }
$cargoHome = [System.IO.Path]::GetFullPath($cargoHome)
$preparedSourceRoot = [System.IO.Path]::GetFullPath([string]$preparedSource.SourceRoot)
$nativeRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)

$previousRustFlags = $env:RUSTFLAGS
$previousEncodedRustFlags = $env:CARGO_ENCODED_RUSTFLAGS
try {
    $flags = @(
        # Windows Sandbox and clean Store installations need not have the VC++
        # redistributable. Keep the Rust native engine self-contained instead of
        # relying on a machine-global VCRUNTIME140.dll.
        '-C'
        'target-feature=+crt-static'
        # Panic locations keep source paths in release builds. Map every
        # machine-specific root (checkout, prepared Merman source, Cargo cache,
        # local rust-src) to a fixed name so no build machine's paths ship.
        "--remap-path-prefix=$nativeRoot=/jithub/mermaid-native"
        "--remap-path-prefix=$preparedSourceRoot=/merman"
        "--remap-path-prefix=$cargoHome=/cargo"
        "--remap-path-prefix=$sysroot\lib/rustlib/src/rust\library=/rustc/$($rustCommitHash[0])/library"
    )
    # Cargo's encoded form preserves paths that contain spaces.
    $env:RUSTFLAGS = $null
    $env:CARGO_ENCODED_RUSTFLAGS = $flags -join [char]0x1F
    foreach ($name in $Platform) {
        $target = $targets[$name]
        & cargo @cargoSourceArguments build --manifest-path (Join-Path $PSScriptRoot 'Cargo.toml') --locked --release --target $target
        if ($LASTEXITCODE -ne 0) { throw "Native build failed for $name." }
        $source = Join-Path $effectiveCargoTargetRoot "$target/release/MarkdownRenderer_Mermaid_Native.dll"
        $destination = Join-Path (Split-Path -Parent $PSScriptRoot) "runtimes/$($rids[$name])/native/MarkdownRenderer.Mermaid.Native.dll"
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
}
finally {
    $env:RUSTFLAGS = $previousRustFlags
    $env:CARGO_ENCODED_RUSTFLAGS = $previousEncodedRustFlags
}
