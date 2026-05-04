param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\JitHub.WinUI\JitHub.WinUI.csproj'),
    [ValidateSet('x86', 'x64', 'ARM64')]
    [string]$Platform = 'x64',
    [switch]$SkipBuild,
    [switch]$SkipLaunch,
    [switch]$SkipScreenshot,
    [string]$AppName = 'JitHub',
    [string]$ScreenshotPath = (Join-Path $PSScriptRoot '..\artifacts\screenshots\winapp-cli\smoke.png')
)

$ErrorActionPreference = 'Stop'

function Require-Command {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name is not available on PATH. Run .\eng\Ensure-WindowsCliTools.ps1 first."
    }
}

Require-Command -Name 'winapp'

if ($SkipBuild -and $SkipLaunch) {
    Write-Host 'Skipping build and launch. Verifying Windows App CLI command surface only.'
    & winapp create-debug-identity --help | Out-Host
    & winapp ui --help | Out-Host
    return
}

$launchArguments = @(
    '-ProjectPath', $ProjectPath,
    '-Platform', $Platform
)

if ($SkipBuild) {
    $launchArguments += '-SkipBuild'
}

if ($SkipLaunch) {
    $launchArguments += '-NoLaunch'
}

& (Join-Path $PSScriptRoot 'Start-JitHubWinUIDebug.ps1') @launchArguments
if ($LASTEXITCODE -ne 0) {
    throw 'Start-JitHubWinUIDebug.ps1 failed.'
}

if (-not $SkipLaunch -and -not $SkipScreenshot) {
    $screenshotDirectory = Split-Path -Parent ([System.IO.Path]::GetFullPath($ScreenshotPath))
    New-Item -ItemType Directory -Path $screenshotDirectory -Force | Out-Null

    Write-Host "Waiting for $AppName and capturing screenshot..."
    & winapp ui wait-for $AppName --app $AppName --timeout 15000
    if ($LASTEXITCODE -ne 0) {
        throw "winapp ui wait-for failed for $AppName."
    }

    & winapp ui screenshot --app $AppName --output $ScreenshotPath
    if ($LASTEXITCODE -ne 0) {
        throw 'winapp ui screenshot failed.'
    }

    Write-Host "Screenshot written to $ScreenshotPath"
}
elseif ($SkipLaunch) {
    Write-Host 'Skipping launch. Verifying Windows App CLI command surface only.'
    & winapp create-debug-identity --help | Out-Host
    & winapp ui --help | Out-Host
}
