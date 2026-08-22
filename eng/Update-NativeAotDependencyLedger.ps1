param(
    [string]$AssetsPath = 'JitHub.WinUI\obj\project.assets.json',
    [string]$OutputPath = 'eng\native-aot-dependencies.json',
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

function Resolve-RepositoryPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }

    return [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

$resolvedAssetsPath = Resolve-RepositoryPath $AssetsPath
$resolvedOutputPath = Resolve-RepositoryPath $OutputPath
if (-not (Test-Path -LiteralPath $resolvedAssetsPath -PathType Leaf)) {
    throw "Assets file not found: $resolvedAssetsPath"
}

$assets = Get-Content -LiteralPath $resolvedAssetsPath -Raw | ConvertFrom-Json -AsHashtable
$directDependencies = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($framework in $assets.project.frameworks.Values) {
    if ($framework.dependencies) {
        foreach ($name in $framework.dependencies.Keys) {
            $null = $directDependencies.Add($name)
        }
    }
}

$packageMap = @{}
foreach ($targetName in ($assets.targets.Keys | Sort-Object)) {
    $target = $assets.targets[$targetName]
    foreach ($libraryKey in ($target.Keys | Sort-Object)) {
        $targetLibrary = $target[$libraryKey]
        $library = $assets.libraries[$libraryKey]
        if (-not $library -or $library.type -ne 'package') {
            continue
        }

        $separator = $libraryKey.LastIndexOf('/')
        if ($separator -le 0) {
            throw "Unexpected package key in assets file: $libraryKey"
        }

        $id = $libraryKey.Substring(0, $separator)
        $version = $libraryKey.Substring($separator + 1)
        if (-not $packageMap.ContainsKey($libraryKey)) {
            $packageMap[$libraryKey] = [ordered]@{
                id = $id
                version = $version
                direct = $directDependencies.Contains($id)
                roles = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
                targets = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
            }
        }

        $entry = $packageMap[$libraryKey]
        $null = $entry.targets.Add($targetName)
        foreach ($role in @('compile', 'runtime', 'native', 'runtimeTargets', 'build', 'buildTransitive', 'buildMultiTargeting')) {
            if ($targetLibrary.ContainsKey($role) -and $targetLibrary[$role]) {
                $null = $entry.roles.Add($role)
            }
        }

        foreach ($file in @($library.files)) {
            if ($file -match '(^|/)analyzers/') {
                $null = $entry.roles.Add('analyzer')
            }
            elseif ($file -match '(^|/)build(Transitive|MultiTargeting)?/') {
                $null = $entry.roles.Add('build')
            }
        }

        if ($entry.roles.Count -eq 0) {
            $null = $entry.roles.Add('metadata')
        }
    }
}

$packages = foreach ($libraryKey in ($packageMap.Keys | Sort-Object)) {
    $entry = $packageMap[$libraryKey]
    [ordered]@{
        id = $entry.id
        version = $entry.version
        direct = $entry.direct
        roles = @($entry.roles | Sort-Object)
        targets = @($entry.targets | Sort-Object)
    }
}

$document = [ordered]@{
    schemaVersion = 1
    project = 'JitHub.WinUI/JitHub.WinUI.csproj'
    runtimeIdentifiers = @('win-x86', 'win-x64', 'win-arm64')
    packageCount = @($packages).Count
    packages = @($packages)
}

$generated = (($document | ConvertTo-Json -Depth 12) -replace "`r`n", "`n") + "`n"
if ($Verify) {
    if (-not (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf)) {
        throw "Dependency ledger is missing: $resolvedOutputPath"
    }

    $existing = (Get-Content -LiteralPath $resolvedOutputPath -Raw) -replace "`r`n", "`n"
    if ($existing -ne $generated) {
        throw 'Native AOT dependency ledger is stale. Run eng\Update-NativeAotDependencyLedger.ps1 and review the package changes.'
    }

    Write-Host "Verified $(@($packages).Count) reviewed Native AOT package dependencies."
    return
}

$outputDirectory = Split-Path -Parent $resolvedOutputPath
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

[System.IO.File]::WriteAllText(
    $resolvedOutputPath,
    $generated,
    [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $(@($packages).Count) package dependencies to $resolvedOutputPath."
