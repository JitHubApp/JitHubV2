[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string] $OutputPath = (Join-Path $PSScriptRoot 'textmate\GRAMMAR-PROVENANCE.v2.0.3.json'),

    [switch] $Verify
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$packageRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget\packages'
}
else {
    $env:NUGET_PACKAGES
}

$lockedPackages = @(
    [pscustomobject][ordered]@{
        id = 'TextMateSharp'
        version = '2.0.3'
        repository = 'https://github.com/danipen/TextMateSharp'
        commit = '4532112f6ee96c8d7847ee66a79719dfe58e9f43'
        nupkgSha256 = '99864f7815add9e93723ba124625dc3aa72940c8c46c3eb04790ad1c81d22e4a'
    },
    [pscustomobject][ordered]@{
        id = 'TextMateSharp.Grammars'
        version = '2.0.3'
        repository = 'https://github.com/danipen/TextMateSharp'
        commit = '4532112f6ee96c8d7847ee66a79719dfe58e9f43'
        nupkgSha256 = '0e263108f060d233dc76585db9332509638e63d31cefb42dfda08610f1201444'
    },
    [pscustomobject][ordered]@{
        id = 'Onigwrap'
        version = '1.0.10'
        repository = 'https://github.com/aikawayataro/Onigwrap'
        commit = 'f8a040b6ceede60530b9572e73a5cce5e867daba'
        nupkgSha256 = 'c754c978e74036258377080df86b05ce71cc80f527abfea248dd01948b744093'
    }
)

foreach ($package in $lockedPackages) {
    $idPath = $package.id.ToLowerInvariant()
    $nupkg = Join-Path $packageRoot "$idPath\$($package.version)\$idPath.$($package.version).nupkg"
    if (-not (Test-Path -LiteralPath $nupkg -PathType Leaf)) {
        throw "The locked upstream package '$($package.id) $($package.version)' is not restored."
    }

    $actual = (Get-FileHash -LiteralPath $nupkg -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $package.nupkgSha256) {
        throw "SHA-256 mismatch for '$nupkg': expected $($package.nupkgSha256), found $actual."
    }
}

$grammarAssemblyPath = Join-Path $packageRoot `
    'textmatesharp.grammars\2.0.3\lib\netstandard2.0\TextMateSharp.Grammars.dll'
$grammarAssembly = [Reflection.Assembly]::LoadFrom($grammarAssemblyPath)
$resourceNames = [string[]] @($grammarAssembly.GetManifestResourceNames() | Sort-Object -CaseSensitive)
if ($resourceNames.Length -ne 317) {
    throw "The locked grammar assembly exposes $($resourceNames.Length) resources; expected 317."
}

$commonProject = [xml] (Get-Content -LiteralPath `
    (Join-Path $PSScriptRoot '..\MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common\MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common.csproj') `
    -Raw)
$commonResourceNames = [string[]] @(
    $commonProject.SelectNodes("//*[local-name()='_CommonTextMateResource']") |
        ForEach-Object { [string] $_.Include } |
        Sort-Object -CaseSensitive)
if ($commonResourceNames.Length -ne 44) {
    throw "The Common pack declares $($commonResourceNames.Length) resources; expected 44."
}

function Get-ResourceBytes {
    param([Parameter(Mandatory)][string] $Name)

    $stream = $grammarAssembly.GetManifestResourceStream($Name)
    if ($null -eq $stream) { throw "Upstream resource '$Name' is missing." }
    try {
        $memory = [IO.MemoryStream]::new()
        try {
            $stream.CopyTo($memory)
            return $memory.ToArray()
        }
        finally { $memory.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Get-ResourceInventory {
    param([Parameter(Mandatory)][string[]] $Names)

    return @($Names | ForEach-Object {
        $bytes = Get-ResourceBytes $_
        [pscustomobject][ordered]@{
            name = $_
            bytes = $bytes.LongLength
            sha256 = [Convert]::ToHexString(
                [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        }
    })
}

function Get-AggregateSha256 {
    param([Parameter(Mandatory)][object[]] $Inventory)

    $hash = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        foreach ($resource in $Inventory) {
            $nameBytes = [Text.Encoding]::UTF8.GetBytes([string] $resource.name)
            $content = Get-ResourceBytes ([string] $resource.name)
            $hash.AppendData([BitConverter]::GetBytes([uint32] $nameBytes.Length))
            $hash.AppendData($nameBytes)
            $hash.AppendData([BitConverter]::GetBytes([uint64] $content.LongLength))
            $hash.AppendData($content)
        }
        return [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    }
    finally { $hash.Dispose() }
}

$registrations = [Collections.Generic.List[object]]::new()
foreach ($name in $resourceNames | Where-Object { $_ -like '*.cgmanifest.json' }) {
    $bytes = Get-ResourceBytes $name
    $manifest = [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    $grammar = [regex]::Match(
        $name,
        '^TextMateSharp\.Grammars\.Resources\.Grammars\.([^.]+)\.cgmanifest\.json$').Groups[1].Value
    foreach ($registration in $manifest.registrations) {
        $versionProperty = $registration.PSObject.Properties['version']
        $descriptionProperty = $registration.PSObject.Properties['description']
        $licenseDetailProperty = $registration.PSObject.Properties['licenseDetail']
        $registrations.Add([pscustomobject][ordered]@{
            grammar = $grammar
            name = [string] $registration.component.git.name
            repositoryUrl = ([string] $registration.component.git.repositoryUrl).Trim()
            commitHash = [string] $registration.component.git.commitHash
            version = if ($null -eq $versionProperty) { '' } else { [string] $versionProperty.Value }
            license = [string] $registration.license
            licenseDetail = if ($null -eq $licenseDetailProperty) {
                [string[]] @()
            }
            else {
                [string[]] @($licenseDetailProperty.Value | ForEach-Object { [string] $_ })
            }
            description = if ($null -eq $descriptionProperty) { '' } else { [string] $descriptionProperty.Value }
        })
    }
}
$orderedRegistrations = [object[]] @($registrations | Sort-Object grammar, name, commitHash)
if ($orderedRegistrations.Length -ne 57) {
    throw "The locked grammar manifests expose $($orderedRegistrations.Length) registrations; expected 57."
}
foreach ($registration in $orderedRegistrations) {
    if ($registration.commitHash -notmatch '^[0-9a-fA-F]{40}$') {
        throw "Grammar '$($registration.grammar)' has a non-commit source revision '$($registration.commitHash)'."
    }
    if ($registration.repositoryUrl -notmatch '^https://github\.com/') {
        throw "Grammar '$($registration.grammar)' has an unexpected source repository '$($registration.repositoryUrl)'."
    }
    if ([string]::IsNullOrWhiteSpace($registration.license)) {
        throw "Grammar '$($registration.grammar)' has no declared upstream license."
    }
}
$licenseIdentifiers = [string[]] @($orderedRegistrations.license | Sort-Object -Unique)
$expectedLicenseIdentifiers = [string[]] @(
    'Apache-2.0',
    'Apache2',
    'BSD',
    'GitHub License',
    'MIT',
    'TextMate Bundle License'
)
if (($licenseIdentifiers -join "`n") -ne ($expectedLicenseIdentifiers -join "`n")) {
    throw "The grammar license-identifier set changed: $($licenseIdentifiers -join ', ')."
}
$licenseDetailRegistrationCount = @($orderedRegistrations | Where-Object {
    @($_.licenseDetail).Count -gt 0
}).Count
if ($licenseDetailRegistrationCount -ne 22) {
    throw "The locked grammar manifests expose $licenseDetailRegistrationCount detailed license texts; expected 22."
}

$allInventory = [object[]] @(Get-ResourceInventory $resourceNames)
$commonInventory = [object[]] @(Get-ResourceInventory $commonResourceNames)
$commonFolders = [string[]] @($commonResourceNames |
    ForEach-Object {
        $match = [regex]::Match($_, '\.Resources\.Grammars\.([^.]+)\.')
        if ($match.Success) { $match.Groups[1].Value }
    } |
    Sort-Object -Unique -CaseSensitive)

$report = [pscustomobject][ordered]@{
    schemaVersion = 1
    generatedFrom = 'signed NuGet payloads; generation is deterministic'
    upstreamPackages = $lockedPackages
    allPack = [pscustomobject][ordered]@{
        resourceCount = $allInventory.Length
        aggregateSha256 = Get-AggregateSha256 $allInventory
        resources = $allInventory
    }
    commonPack = [pscustomobject][ordered]@{
        sourceFolders = $commonFolders
        resourceCount = $commonInventory.Length
        aggregateSha256 = Get-AggregateSha256 $commonInventory
        resources = $commonInventory
    }
    licenseIdentifiers = $licenseIdentifiers
    licenseDetailRegistrationCount = $licenseDetailRegistrationCount
    registrations = $orderedRegistrations
}

$json = ($report | ConvertTo-Json -Depth 20).Replace("`r`n", "`n").Replace("`r", "`n") + "`n"
if ($Verify) {
    if (-not (Test-Path -LiteralPath $OutputPath -PathType Leaf)) {
        throw "Checked-in provenance file '$OutputPath' is missing."
    }
    $expected = (Get-Content -LiteralPath $OutputPath -Raw).Replace("`r`n", "`n").Replace("`r", "`n")
    if ($expected -ne $json) {
        throw "Checked-in provenance file '$OutputPath' does not match the locked upstream payload."
    }
    Write-Host "TextMate grammar provenance verified: '$OutputPath'."
    return
}

$parent = Split-Path -Parent $OutputPath
[IO.Directory]::CreateDirectory($parent) | Out-Null
[IO.File]::WriteAllText($OutputPath, $json, [Text.UTF8Encoding]::new($false))
Write-Host "TextMate grammar provenance generated: '$OutputPath'."
