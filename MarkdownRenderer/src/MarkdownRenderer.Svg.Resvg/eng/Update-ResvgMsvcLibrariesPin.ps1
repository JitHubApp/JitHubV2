<#
.SYNOPSIS
Pins the exact MSVC and Windows SDK library payloads the resvg workers link.

.DESCRIPTION
The workers link the MSVC static CRT and Windows SDK import libraries. Those
files used to come from whatever Visual Studio was installed on the build
machine, so a hosted-runner image update changed the worker bytes and broke
the rebuild-and-compare gate. This script resolves one Visual Studio channel
manifest and records every payload Install-ResvgMsvcLibraries.ps1 downloads,
by URL and SHA-256, in RESVG_MSVC_LIBRARIES.json. Builds then link only
those files, on every machine.

The pinned payloads are:
  - Microsoft.VC.<MsvcToolset>.CRT.<arch>.Desktop.base and .Store.base for
    x86, x64 and ARM64 (static CRT, vcruntime and oldnames);
  - from <SdkPackageId>, the Universal CRT, Desktop Libs (x86, x64, arm64)
    and Store Apps Libs MSIs plus every cabinet each MSI references.

Changing the pin changes the worker source fingerprint, so the workers must be
rebuilt and their identity refreshed in the same change.
#>
[CmdletBinding()]
param(
    [string] $ChannelUrl = 'https://aka.ms/vs/17/release/channel',
    [string] $MsvcToolset = '14.44.17.14',
    [string] $SdkPackageId = 'Win11SDK_10.0.26100'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ProgressPreference = 'SilentlyContinue'

$providerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pinPath = Join-Path $providerRoot 'RESVG_MSVC_LIBRARIES.json'
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("resvg-msvc-pin-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $scratch | Out-Null

function Get-Sha256([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function New-PinnedPayload([string] $Kind, [string] $Package, [object] $Payload) {
    [ordered]@{
        kind = $Kind
        package = $Package
        fileName = [IO.Path]::GetFileName(([string]$Payload.fileName).Replace('\', '/'))
        url = [string]$Payload.url
        # The manifest's size field is an estimate, not the served length, so
        # only the SHA-256 is pinned.
        sha256 = ([string]$Payload.sha256).ToLowerInvariant()
    }
}

try {
    $channelPath = Join-Path $scratch 'channel.json'
    Invoke-WebRequest -Uri $ChannelUrl -OutFile $channelPath
    $channel = Get-Content -LiteralPath $channelPath -Raw | ConvertFrom-Json
    $manifestItem = @($channel.channelItems | Where-Object { $_.id -eq 'Microsoft.VisualStudio.Manifests.VisualStudio' })
    if ($manifestItem.Count -ne 1 -or @($manifestItem[0].payloads).Count -ne 1) {
        throw "Channel '$ChannelUrl' does not name exactly one Visual Studio package manifest."
    }
    $manifestPayload = $manifestItem[0].payloads[0]
    $manifestPath = Join-Path $scratch 'VisualStudio.vsman'
    Invoke-WebRequest -Uri $manifestPayload.url -OutFile $manifestPath
    # The channel's declared manifest hash does not cover the served bytes, so
    # record what was read. Each pinned payload below carries its own hash.
    $manifestSha256 = Get-Sha256 $manifestPath
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $packagesById = @{}
    foreach ($package in $manifest.packages) {
        if (-not $packagesById.ContainsKey([string]$package.id)) {
            $packagesById[[string]$package.id] = [Collections.Generic.List[object]]::new()
        }
        $packagesById[[string]$package.id].Add($package)
    }

    function Get-SinglePackage([string] $Id) {
        $found = if ($packagesById.ContainsKey($Id)) { @($packagesById[$Id]) } else { @() }
        if ($found.Count -ne 1) {
            throw "Expected exactly one '$Id' package in the manifest; found $($found.Count)."
        }
        return $found[0]
    }

    $payloads = [Collections.Generic.List[object]]::new()
    $msvcVersions = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($arch in @('x86', 'x64', 'ARM64')) {
        foreach ($flavor in @('Desktop', 'Store')) {
            $id = "Microsoft.VC.$MsvcToolset.CRT.$arch.$flavor.base"
            $package = Get-SinglePackage $id
            if (@($package.payloads).Count -ne 1) {
                throw "Package '$id' must have exactly one VSIX payload."
            }
            $null = $msvcVersions.Add([string]$package.version)
            $payloads.Add((New-PinnedPayload 'vsix' $id $package.payloads[0]))
        }
    }
    if ($msvcVersions.Count -ne 1) {
        throw "The pinned CRT packages disagree on their MSVC version: $($msvcVersions -join ', ')."
    }

    $sdk = Get-SinglePackage $SdkPackageId
    $sdkPayloads = @($sdk.payloads)
    $cabinets = @{}
    foreach ($payload in $sdkPayloads | Where-Object { ([string]$_.fileName) -like '*.cab' }) {
        $cabinets[[IO.Path]::GetFileName(([string]$payload.fileName).Replace('\', '/'))] = $payload
    }
    $msiNames = @(
        'Universal CRT Headers Libraries and Sources-x86_en-us.msi'
        'Windows SDK Desktop Libs x86-x86_en-us.msi'
        'Windows SDK Desktop Libs x64-x86_en-us.msi'
        'Windows SDK Desktop Libs arm64-x86_en-us.msi'
        'Windows SDK for Windows Store Apps Libs-x86_en-us.msi'
    )
    $pinnedCabinets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($msiName in $msiNames) {
        $msi = @($sdkPayloads | Where-Object { ([string]$_.fileName).Replace('\', '/') -ceq "Installers/$msiName" })
        if ($msi.Count -ne 1) {
            throw "Expected exactly one '$msiName' payload in '$SdkPackageId'; found $($msi.Count)."
        }
        $payloads.Add((New-PinnedPayload 'msi' $SdkPackageId $msi[0]))

        # An MSI names its cabinets in its Media table. Every cabinet name in the
        # SDK is a 32-digit hash, so a byte search of the MSI finds exactly the
        # cabinets it extracts from.
        $msiPath = Join-Path $scratch $msiName
        Invoke-WebRequest -Uri $msi[0].url -OutFile $msiPath
        if ((Get-Sha256 $msiPath) -cne ([string]$msi[0].sha256).ToLowerInvariant()) {
            throw "'$msiName' does not match the hash its manifest declares."
        }
        $msiText = [Text.Encoding]::Latin1.GetString([IO.File]::ReadAllBytes($msiPath))
        $referenced = @($cabinets.Keys | Where-Object { $msiText.Contains($_) } | Sort-Object)
        if ($referenced.Count -eq 0) {
            throw "'$msiName' references no cabinet in '$SdkPackageId'."
        }
        foreach ($cabinet in $referenced) {
            if ($pinnedCabinets.Add($cabinet)) {
                $payloads.Add((New-PinnedPayload 'cab' $SdkPackageId $cabinets[$cabinet]))
            }
        }
    }

    $pin = [ordered]@{
        schemaVersion = 1
        source = [ordered]@{
            channelUrl = $ChannelUrl
            productDisplayVersion = [string]$channel.info.productDisplayVersion
            buildVersion = [string]$channel.info.buildVersion
            manifestUrl = [string]$manifestPayload.url
            manifestSha256 = $manifestSha256
        }
        msvc = [ordered]@{
            toolset = $MsvcToolset
            packageVersion = @($msvcVersions)[0]
        }
        windowsSdk = [ordered]@{
            packageId = $SdkPackageId
            packageVersion = [string]$sdk.version
        }
        payloads = @($payloads)
    }
    [IO.File]::WriteAllText($pinPath, (($pin | ConvertTo-Json -Depth 8) + "`n"), [Text.UTF8Encoding]::new($false))
    Write-Output "Pinned $($payloads.Count) payloads from Visual Studio $($channel.info.productDisplayVersion) in '$pinPath'."
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
