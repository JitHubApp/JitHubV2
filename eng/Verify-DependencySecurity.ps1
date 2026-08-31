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

$projectFiles = Get-ChildItem -LiteralPath $repositoryRoot -Recurse -Filter *.csproj -File |
    Where-Object { $_.FullName -notmatch '[\\/](?:bin|obj|artifacts|\.codex-artifacts)[\\/]' }

$allowedPrereleasePackages = @{
    "CommunityToolkit.Labs.WinUI.TransitionHelper" = "0.1.251217-build.2433"
    "WinUIEdit" = "0.0.5-prerelease"
}

foreach ($projectFile in $projectFiles) {
    [xml] $project = Get-Content -LiteralPath $projectFile.FullName -Raw
    foreach ($reference in @($project.Project.ItemGroup.PackageReference)) {
        if ($null -eq $reference) {
            continue
        }

        $name = [string] $reference.Include
        $version = [string] $reference.Version
        if ([string]::IsNullOrWhiteSpace($name) -or [string]::IsNullOrWhiteSpace($version)) {
            throw "Every PackageReference must use an explicit version: $($projectFile.FullName)"
        }

        if ($version.Contains('*')) {
            throw "Floating package versions are forbidden: $name $version"
        }

        if ($version.Contains('-')) {
            if (-not $allowedPrereleasePackages.ContainsKey($name) -or
                $allowedPrereleasePackages[$name] -ne $version) {
                throw "Unapproved prerelease package: $name $version"
            }
        }
    }
}

[xml] $nugetConfig = Get-Content -LiteralPath (Join-Path $repositoryRoot "NuGet.config") -Raw
foreach ($source in @($nugetConfig.configuration.packageSources.add)) {
    $uri = [Uri] ([string] $source.value)
    if ($uri.Scheme -ne [Uri]::UriSchemeHttps) {
        throw "NuGet package source must use HTTPS: $uri"
    }
}

$restoreOutput = Invoke-CheckedDotNet @(
    "restore",
    $appProjectPath,
    "--locked-mode",
    "-p:Configuration=Release",
    "-p:Platform=x64",
    "-p:RuntimeIdentifier=win-x64",
    "-p:PublishAot=true",
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
