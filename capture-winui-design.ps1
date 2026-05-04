param(
    [string]$Configuration = 'Debug',
    [string]$TargetPlatform = 'x64',
    [string]$Themes = 'light,dark',
    [string]$Targets = 'buttons,inputs,navigation,settings,repo,conversation,empty,login,settings-page'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProcessName = 'JitHub.WinUI'
Get-Process $appProcessName -ErrorAction SilentlyContinue | Stop-Process -Force

$runtime = if ($TargetPlatform -eq 'x64') { 'win-x64' } elseif ($TargetPlatform -eq 'x86') { 'win-x86' } else { 'win-arm64' }

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $process = Start-Process -FilePath 'dotnet' -ArgumentList $Arguments -WorkingDirectory $repoRoot -NoNewWindow -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        exit $process.ExitCode
    }
}

$buildArgs = @(
    'build',
    '.\JitHub.WinUI\JitHub.WinUI.csproj',
    '-c', $Configuration,
    "-p:Platform=$TargetPlatform",
    "-p:RuntimeIdentifier=$runtime",
    '-nodeReuse:false',
    '-m:1'
)
Invoke-DotNet -Arguments $buildArgs

$automationBuildArgs = @(
    'build',
    '.\JitHub.WinUI.Automation\JitHub.WinUI.Automation.csproj',
    '-c', $Configuration,
    '-nodeReuse:false',
    '-m:1'
)
Invoke-DotNet -Arguments $automationBuildArgs

$appPath = Join-Path $repoRoot ("JitHub.WinUI\\bin\\$TargetPlatform\\$Configuration\\net10.0-windows10.0.26100.0\\$runtime\\JitHub.WinUI.exe")
if (-not (Test-Path $appPath)) {
    $appPath = Join-Path $repoRoot ("JitHub.WinUI\\bin\\$Configuration\\net10.0-windows10.0.26100.0\\$runtime\\JitHub.WinUI.exe")
}

$outDir = Join-Path $repoRoot 'artifacts\screenshots\winui'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$automationRunArgs = @(
    'run',
    '--project', '.\JitHub.WinUI.Automation\JitHub.WinUI.Automation.csproj',
    '--configuration', $Configuration,
    '--',
    "--app=$appPath",
    "--out=$outDir",
    "--themes=$Themes",
    "--targets=$Targets"
)
Invoke-DotNet -Arguments $automationRunArgs
Write-Host "Screenshot manifest: $outDir\index.html"
