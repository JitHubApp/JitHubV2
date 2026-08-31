[CmdletBinding()]
param(
    [string]$AppPath = "",
    [string]$Destination = "",
    [string]$Repository = "JitHubApp/JitHubV2",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$appProject = Join-Path $repoRoot "JitHub.WinUI\JitHub.WinUI.csproj"
$automationProject = Join-Path $repoRoot "JitHub.WinUI.Automation\JitHub.WinUI.Automation.csproj"
$automationConfiguration = "Release"
$runnerAssembly = Join-Path $repoRoot "JitHub.WinUI.Automation\bin\Release\net10.0-windows10.0.19041.0\JitHub.WinUI.Automation.dll"
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $repoRoot "JitHub.Web\wwwroot\media\showcase"
}
$Destination = [System.IO.Path]::GetFullPath($Destination)

function Invoke-CheckedDotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

if ([string]::IsNullOrWhiteSpace($AppPath)) {
    Write-Host "Building the current JitHub app and website-showcase automation probe..."
    Invoke-CheckedDotNet @(
        "build", $appProject,
        "--configuration", $Configuration,
        "--property:Platform=x64",
        "--disable-build-servers",
        "--maxcpucount:1")
    $AppPath = Join-Path $repoRoot (
        "JitHub.WinUI\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\JitHub.WinUI.exe")
}
$AppPath = [System.IO.Path]::GetFullPath($AppPath)
if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) {
    throw "The current JitHub executable was not found at '$AppPath'."
}

Invoke-CheckedDotNet @(
    "build", $automationProject,
    "--configuration", $automationConfiguration,
    "--disable-build-servers",
    "--maxcpucount:1")
if (-not (Test-Path -LiteralPath $runnerAssembly -PathType Leaf)) {
    throw "The website-showcase runner was not produced at '$runnerAssembly'."
}

$stageRoot = Join-Path $repoRoot ("artifacts\website-showcase\" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null
Write-Host "Capturing deterministic Light and Dark product media to '$stageRoot'..."
Invoke-CheckedDotNet @(
    $runnerAssembly,
    "--probe=website-showcase",
    "--themes=light,dark",
    "--repo=$Repository",
    "--app=$AppPath",
    "--out=$stageRoot")

$manifestPath = Join-Path $stageRoot "media-manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "The website-showcase probe did not produce its media manifest."
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$assets = @($manifest.assets)
if ($manifest.schemaVersion -ne 2 -or
    $manifest.captureWidth -ne 3200 -or
    $manifest.captureHeight -ne 1800 -or
    $manifest.minimumLogicalWidth -ne 1200 -or
    $manifest.minimumLogicalHeight -ne 675 -or
    $manifest.source -ne "synthetic-public-preview" -or
    $manifest.networkPolicy -ne "blocked-loopback-proxy" -or
    $assets.Count -ne 16 -or
    @($assets.id | Sort-Object -Unique).Count -ne 8 -or
    @($assets.theme | Sort-Object -Unique).Count -ne 2 -or
    @($assets | Where-Object { $_.logicalWidth -lt 1200 -or $_.logicalHeight -lt 675 }).Count -ne 0) {
    throw "The staged website media manifest failed completeness or capture-contract validation."
}

foreach ($asset in $assets) {
    $path = Join-Path $stageRoot ([string]$asset.file)
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The manifest references missing staged media '$path'."
    }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne [string]$asset.sha256) {
        throw "The staged media hash does not match for '$path'."
    }
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
foreach ($asset in $assets) {
    Copy-Item -LiteralPath (Join-Path $stageRoot ([string]$asset.file)) -Destination $Destination -Force
}
Copy-Item -LiteralPath $manifestPath -Destination $Destination -Force

Write-Host "Website showcase media is complete and verified at '$Destination'."
