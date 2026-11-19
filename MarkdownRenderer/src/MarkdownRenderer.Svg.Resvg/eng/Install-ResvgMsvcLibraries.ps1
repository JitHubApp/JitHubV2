<#
.SYNOPSIS
Installs the pinned MSVC and Windows SDK libraries the resvg workers link.

.DESCRIPTION
Downloads every payload in RESVG_MSVC_LIBRARIES.json, refuses any whose SHA-256
differs from the pin, and unpacks the libraries into

    <Root>\<pin key>\<arch>\msvc   static CRT, vcruntime and oldnames
    <Root>\<pin key>\<arch>\ucrt   Universal CRT
    <Root>\<pin key>\<arch>\um     Windows import libraries

for arch x86, x64 and arm64. The pin key is derived from the payload hashes,
so a changed pin installs beside, never over, an earlier one. An existing
complete install is reused without downloading.

Outputs the three library directories for -Target, in link search order.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Root,

    [Parameter(Mandatory)]
    [ValidateSet(
        'i686-pc-windows-msvc',
        'x86_64-pc-windows-msvc',
        'aarch64-pc-windows-msvc')]
    [string] $Target
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProgressPreference = 'SilentlyContinue'

$providerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pin = Get-Content -LiteralPath (Join-Path $providerRoot 'RESVG_MSVC_LIBRARIES.json') -Raw | ConvertFrom-Json
if ($pin.schemaVersion -ne 1 -or @($pin.payloads).Count -eq 0) {
    throw 'RESVG_MSVC_LIBRARIES.json is not a schema 1 pin with payloads.'
}
$arches = [ordered]@{ x86 = 'x86'; x64 = 'x64'; arm64 = 'ARM64' }
$targetArch = switch ($Target) {
    'i686-pc-windows-msvc' { 'x86' }
    'x86_64-pc-windows-msvc' { 'x64' }
    'aarch64-pc-windows-msvc' { 'arm64' }
}

$hashes = @($pin.payloads | ForEach-Object { ([string]$_.sha256).ToLowerInvariant() })
foreach ($hash in $hashes) {
    if ($hash -notmatch '^[0-9a-f]{64}$') { throw "Pinned payload hash '$hash' is not a SHA-256." }
}
$pinKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes("resvg-msvc-libraries-v1`n" + ($hashes -join "`n")))).Substring(0, 16).ToLowerInvariant()
$Root = [IO.Path]::GetFullPath($Root)
$installRoot = Join-Path $Root $pinKey
$marker = Join-Path $installRoot 'complete.json'

function Get-TargetLibraryDirectories([string] $InstallRoot) {
    foreach ($kind in @('msvc', 'ucrt', 'um')) {
        $directory = Join-Path $InstallRoot "$targetArch\$kind"
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
            throw "The pinned MSVC library install is missing '$directory'."
        }
        Write-Output $directory
    }
}

function Test-CompleteInstall {
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { return $false }
    $recorded = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    return [string]$recorded.pinKey -ceq $pinKey
}

if (Test-CompleteInstall) {
    Get-TargetLibraryDirectories $installRoot
    return
}

function Save-PinnedPayload([object] $Payload, [string] $Directory) {
    $path = Join-Path $Directory ([string]$Payload.fileName)
    $expected = ([string]$Payload.sha256).ToLowerInvariant()
    for ($attempt = 1; ; $attempt++) {
        try {
            Invoke-WebRequest -Uri ([string]$Payload.url) -OutFile $path
            $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($actual -cne $expected) {
                throw "'$($Payload.fileName)' has SHA-256 $actual; the pin requires $expected."
            }
            return $path
        }
        catch {
            if ($attempt -ge 3) { throw }
            Write-Warning "Download attempt $attempt of '$($Payload.fileName)' failed: $($_.Exception.Message)"
            Start-Sleep -Seconds (5 * $attempt)
        }
    }
}

# Copies the top-level .lib files of one source directory into a destination,
# refusing two different files under one name.
function Copy-Libraries([string] $Source, [string] $Destination) {
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "A pinned package did not contain the library directory '$Source'."
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $Source -File -Filter '*.lib') {
        $existing = Get-ChildItem -LiteralPath $Destination -File |
            Where-Object { $_.Name -ieq $file.Name } | Select-Object -First 1
        if ($null -ne $existing) {
            if ((Get-FileHash -LiteralPath $existing.FullName).Hash -cne (Get-FileHash -LiteralPath $file.FullName).Hash) {
                throw "Two pinned packages carry different '$($file.Name)' for '$Destination'."
            }
            continue
        }
        Copy-Item -LiteralPath $file.FullName -Destination $Destination
    }
}

function Get-SingleChildDirectory([string] $Path, [string] $Description) {
    $children = @(Get-ChildItem -LiteralPath $Path -Directory)
    if ($children.Count -ne 1) {
        throw "Expected one $Description under '$Path'; found $($children.Count)."
    }
    return $children[0].FullName
}

# msiexec writes deep SDK source paths, so unpack under a short temporary root.
$work = Join-Path ([IO.Path]::GetTempPath()) ('rmsvc-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$staging = "$installRoot.partial-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
try {
    $downloads = Join-Path $work 'd'
    New-Item -ItemType Directory -Path $downloads, $staging -Force | Out-Null

    # MSVC: each VSIX holds Contents\VC\Tools\MSVC\<toolset>\lib\<arch>.
    $vsixIndex = 0
    foreach ($payload in $pin.payloads | Where-Object { $_.kind -ceq 'vsix' }) {
        $vsix = Save-PinnedPayload $payload $downloads
        $vsixIndex++
        $unpacked = Join-Path $work "v$vsixIndex"
        Expand-Archive -LiteralPath $vsix -DestinationPath $unpacked
        $toolset = Get-SingleChildDirectory (Join-Path $unpacked 'Contents\VC\Tools\MSVC') 'MSVC toolset'
        $packageArch = @($arches.Keys | Where-Object { ([string]$payload.package) -like "*.CRT.$($arches[$_]).*" })
        if ($packageArch.Count -ne 1) { throw "Cannot tell the architecture of '$($payload.package)'." }
        Copy-Libraries (Join-Path $toolset "lib\$($packageArch[0])") (Join-Path $staging "$($packageArch[0])\msvc")
        Remove-Item -LiteralPath $unpacked -Recurse -Force
    }

    # Windows SDK: administrative installs of the pinned MSIs, from their cabinets.
    foreach ($payload in $pin.payloads | Where-Object { $_.kind -ceq 'cab' }) {
        $null = Save-PinnedPayload $payload $downloads
    }
    $sdkIndex = 0
    foreach ($payload in $pin.payloads | Where-Object { $_.kind -ceq 'msi' }) {
        $msi = Save-PinnedPayload $payload $downloads
        $sdkIndex++
        $unpacked = Join-Path $work "s$sdkIndex"
        for ($attempt = 1; ; $attempt++) {
            $process = Start-Process -FilePath 'msiexec.exe' -Wait -PassThru -NoNewWindow -ArgumentList @(
                '/a', "`"$msi`"", '/qn', "TARGETDIR=`"$unpacked`"")
            # 1618: another Windows Installer operation is running; wait and retry.
            if ($process.ExitCode -eq 1618 -and $attempt -lt 10) { Start-Sleep -Seconds 15; continue }
            if ($process.ExitCode -ne 0) {
                throw "Unpacking '$($payload.fileName)' failed with Windows Installer exit code $($process.ExitCode)."
            }
            break
        }
        $sdkLib = Join-Path $unpacked 'Windows Kits\10\Lib'
        if (Test-Path -LiteralPath $sdkLib -PathType Container) {
            $version = Get-SingleChildDirectory $sdkLib 'Windows SDK library version'
            foreach ($kind in @('ucrt', 'um')) {
                foreach ($arch in $arches.Keys) {
                    $source = Join-Path $version "$kind\$arch"
                    if (Test-Path -LiteralPath $source -PathType Container) {
                        Copy-Libraries $source (Join-Path $staging "$arch\$kind")
                    }
                }
            }
        }
        Remove-Item -LiteralPath $unpacked -Recurse -Force
    }

    foreach ($arch in $arches.Keys) {
        foreach ($kind in @('msvc', 'ucrt', 'um')) {
            if (@(Get-ChildItem -LiteralPath (Join-Path $staging "$arch\$kind") -File -Filter '*.lib' -ErrorAction SilentlyContinue).Count -eq 0) {
                throw "The pinned payloads produced no '$kind' libraries for $arch."
            }
        }
    }
    [IO.File]::WriteAllText(
        (Join-Path $staging 'complete.json'),
        ((@{ pinKey = $pinKey } | ConvertTo-Json) + "`n"),
        [Text.UTF8Encoding]::new($false))

    if (Test-Path -LiteralPath $installRoot) {
        if (Test-CompleteInstall) {
            # Another build finished the same pinned install first.
            Get-TargetLibraryDirectories $installRoot
            return
        }
        Remove-Item -LiteralPath $installRoot -Recurse -Force
    }
    Move-Item -LiteralPath $staging -Destination $installRoot
    Get-TargetLibraryDirectories $installRoot
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}
