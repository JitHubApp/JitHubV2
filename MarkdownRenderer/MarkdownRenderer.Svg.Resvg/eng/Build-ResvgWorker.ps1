[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet(
        'i686-pc-windows-msvc',
        'x86_64-pc-windows-msvc',
        'aarch64-pc-windows-msvc')]
    [string]$Target,

    [string]$TargetDirectory = ''
)

$ErrorActionPreference = 'Stop'
$providerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$nativeRoot = [IO.Path]::GetFullPath((Join-Path $providerRoot 'native'))
$manifest = Join-Path $nativeRoot 'Cargo.toml'
if ([string]::IsNullOrWhiteSpace($TargetDirectory)) {
    $TargetDirectory = Join-Path $nativeRoot 'target'
}
$TargetDirectory = [IO.Path]::GetFullPath($TargetDirectory)

$verboseVersion = @(& rustc +1.96.0 -vV)
$sysroot = (& rustc +1.96.0 --print sysroot).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sysroot)) {
    throw 'The pinned Rust 1.96.0 sysroot could not be resolved.'
}
$hostTarget = @($verboseVersion | Where-Object { $_ -like 'host: *' } |
    ForEach-Object { $_.Substring('host: '.Length).Trim() })
if ($hostTarget.Count -ne 1 -or [string]::IsNullOrWhiteSpace($hostTarget[0])) {
    throw 'The pinned Rust 1.96.0 host target could not be resolved.'
}
$lldDirectory = Join-Path $sysroot "lib\rustlib\$($hostTarget[0])\bin"
$lld = Join-Path $lldDirectory 'rust-lld.exe'
if (-not (Test-Path -LiteralPath $lld -PathType Leaf)) {
    throw "The pinned Rust 1.96.0 rust-lld executable was not found: '$lld'."
}

$cargoHome = if ([string]::IsNullOrWhiteSpace($env:CARGO_HOME)) {
    Join-Path $env:USERPROFILE '.cargo'
}
else {
    $env:CARGO_HOME
}
$cargoHome = [IO.Path]::GetFullPath($cargoHome)

$previousPath = $env:PATH
$previousRustFlags = $env:RUSTFLAGS
$previousEncodedRustFlags = $env:CARGO_ENCODED_RUSTFLAGS
try {
    # rust-lld is part of the pinned Rust toolchain, so the PE linker does not
    # drift with the Visual Studio image installed on the build machine.
    $env:PATH = "$lldDirectory$([IO.Path]::PathSeparator)$previousPath"
    $flags = @(
        '-C'
        'linker=rust-lld.exe'
        '-C'
        'link-arg=/Brepro'
        "--remap-path-prefix=$nativeRoot=/jithub/svg-native"
        "--remap-path-prefix=$cargoHome=/cargo"
    )
    # Cargo's encoded form preserves checkout paths that contain spaces.
    $env:RUSTFLAGS = $null
    $env:CARGO_ENCODED_RUSTFLAGS = $flags -join [char]0x1F

    & cargo +1.96.0 build `
        --manifest-path $manifest `
        --release `
        --locked `
        --target $Target `
        --target-dir $TargetDirectory
    if ($LASTEXITCODE -ne 0) {
        throw "The pinned resvg worker build failed for $Target."
    }
}
finally {
    $env:PATH = $previousPath
    $env:RUSTFLAGS = $previousRustFlags
    $env:CARGO_ENCODED_RUSTFLAGS = $previousEncodedRustFlags
}

$binary = Join-Path $TargetDirectory "$Target\release\markdown_renderer_resvg_worker.exe"
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
    throw "The resvg worker build did not produce '$binary'."
}

Write-Output $binary
