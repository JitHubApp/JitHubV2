$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$appProjectPath = (Resolve-Path (Join-Path $repositoryRoot 'JitHub.WinUI\JitHub.WinUI.csproj')).Path

function Invoke-CheckedDotNet {
    param([Parameter(Mandatory = $true)][string[]] $Arguments)

    $output = & dotnet @Arguments 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed.`n$output"
    }

    return $output
}

function Get-ProjectFilesPruned {
    param([Parameter(Mandatory = $true)][string] $Root)

    $excludedDirectoryNames = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @('bin', 'obj', 'artifacts', '_scratch', '.codex-artifacts', '.git')) {
        [void] $excludedDirectoryNames.Add($name)
    }

    $pendingDirectories = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
    $pendingDirectories.Push([System.IO.DirectoryInfo]::new($Root))

    while ($pendingDirectories.Count -gt 0) {
        $directory = $pendingDirectories.Pop()
        foreach ($projectFile in $directory.EnumerateFiles('*.csproj', [System.IO.SearchOption]::TopDirectoryOnly)) {
            $projectFile
        }

        foreach ($packagesFile in $directory.EnumerateFiles('Directory.Packages.props', [System.IO.SearchOption]::TopDirectoryOnly)) {
            $packagesFile
        }

        foreach ($childDirectory in $directory.EnumerateDirectories('*', [System.IO.SearchOption]::TopDirectoryOnly)) {
            $isGeneratedDirectory =
                $excludedDirectoryNames.Contains($childDirectory.Name) -or
                $childDirectory.Name.StartsWith('bin-', [StringComparison]::OrdinalIgnoreCase) -or
                $childDirectory.Name.StartsWith('obj-', [StringComparison]::OrdinalIgnoreCase)
            $isReparsePoint = ($childDirectory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0
            if (-not $isGeneratedDirectory -and -not $isReparsePoint) {
                $pendingDirectories.Push($childDirectory)
            }
        }
    }
}

$dependencyFiles = @(Get-ProjectFilesPruned -Root $repositoryRoot)
$projectFiles = @($dependencyFiles | Where-Object { $_.Extension -eq '.csproj' })
$centralPackageFiles = @($dependencyFiles | Where-Object { $_.Name -eq 'Directory.Packages.props' })

$allowedPrereleasePackages = @{
    "CommunityToolkit.Labs.WinUI.TransitionHelper" = "0.1.251217-build.2433"
    "WinUIEdit" = "0.0.5-prerelease"
}

function Assert-ApprovedPackageVersion {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Name,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $Version,
        [Parameter(Mandatory = $true)][string] $Source
    )

    if ([string]::IsNullOrWhiteSpace($Name) -or [string]::IsNullOrWhiteSpace($Version)) {
        throw "Every package must use an explicit version: $Source"
    }

    if ($Version.Contains('*')) {
        throw "Floating package versions are forbidden: $Name $Version"
    }

    if ($Version.Contains('-')) {
        if (-not $allowedPrereleasePackages.ContainsKey($Name) -or
            $allowedPrereleasePackages[$Name] -ne $Version) {
            throw "Unapproved prerelease package: $Name $Version"
        }
    }
}

# Central package management: a Directory.Packages.props pins one version per
# package for the projects below it. Check every pinned version here; a
# project's PackageReference then carries no version of its own.
$centralPackageVersions = @{}
foreach ($packagesFile in $centralPackageFiles) {
    [xml] $packages = Get-Content -LiteralPath $packagesFile.FullName -Raw
    $versions = @{}
    foreach ($packageVersion in @($packages.Project.ItemGroup.PackageVersion)) {
        if ($null -eq $packageVersion) {
            continue
        }

        $name = [string] $packageVersion.Include
        Assert-ApprovedPackageVersion -Name $name -Version ([string] $packageVersion.Version) -Source $packagesFile.FullName
        $versions[$name] = [string] $packageVersion.Version
    }

    $centralPackageVersions[$packagesFile.DirectoryName] = $versions
}

# MSBuild imports the nearest Directory.Packages.props above the project.
function Get-CentralPackageVersions {
    param([Parameter(Mandatory = $true)][System.IO.DirectoryInfo] $ProjectDirectory)

    for ($directory = $ProjectDirectory; $null -ne $directory; $directory = $directory.Parent) {
        if ($centralPackageVersions.ContainsKey($directory.FullName)) {
            return $centralPackageVersions[$directory.FullName]
        }

        if ([string]::Equals($directory.FullName, $repositoryRoot, [StringComparison]::OrdinalIgnoreCase)) {
            break
        }
    }

    return $null
}

foreach ($projectFile in $projectFiles) {
    [xml] $project = Get-Content -LiteralPath $projectFile.FullName -Raw
    $optsOutOfCentralVersions = @($project.Project.PropertyGroup.ManagePackageVersionsCentrally) -contains 'false'
    foreach ($reference in @($project.Project.ItemGroup.PackageReference)) {
        if ($null -eq $reference) {
            continue
        }

        $name = [string] $reference.Include
        $version = [string] $reference.Version
        if ([string]::IsNullOrWhiteSpace($version)) {
            $version = [string] $reference.VersionOverride
        }

        if ([string]::IsNullOrWhiteSpace($version) -and -not [string]::IsNullOrWhiteSpace($name)) {
            $centralVersions = if ($optsOutOfCentralVersions) { $null } else { Get-CentralPackageVersions -ProjectDirectory $projectFile.Directory }
            if ($null -eq $centralVersions -or -not $centralVersions.ContainsKey($name)) {
                throw "Every PackageReference must use an explicit version, in the project or in Directory.Packages.props: $name in $($projectFile.FullName)"
            }

            # The central version was checked with its Directory.Packages.props.
            continue
        }

        Assert-ApprovedPackageVersion -Name $name -Version $version -Source $projectFile.FullName
    }
}

[xml] $nugetConfig = Get-Content -LiteralPath (Join-Path $repositoryRoot "NuGet.config") -Raw
foreach ($source in @($nugetConfig.configuration.packageSources.add)) {
    $uri = [Uri] ([string] $source.value)
    if ($uri.Scheme -ne [Uri]::UriSchemeHttps) {
        throw "NuGet package source must use HTTPS: $uri"
    }
}

# RuntimeIdentifier and PublishAot are intentionally not passed as global MSBuild
# properties. Shipping projects declare the complete supported RuntimeIdentifiers
# set in their canonical locks; this RID-less restore audits all architectures in
# one pass while managed outputs remain AnyCPU. Architecture-specific AOT execution
# remains in Restore-NativeAot.ps1 and the native-aot workflow.
$restoreOutput = Invoke-CheckedDotNet @(
    "restore",
    $appProjectPath,
    "--locked-mode",
    "-p:Configuration=Release",
    "-p:Platform=x64",
    "-p:NuGetAudit=true",
    "-p:NuGetAuditMode=all",
    "-warnaserror:NU1901;NU1902;NU1903;NU1904",
    "-p:SkipReleaseSecurityGate=true"
)

$vulnerabilityOutput = Invoke-CheckedDotNet @(
    "list",
    $appProjectPath,
    "package",
    "--vulnerable",
    "--include-transitive",
    "--no-restore"
)

if ($vulnerabilityOutput -match 'has the following vulnerable packages') {
    throw "The dependency graph contains vulnerable packages.`n$vulnerabilityOutput"
}

Write-Host "Locked restore, dependency policy, feed policy, and vulnerability checks passed."
