param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\JitHub.WinUI\JitHub.WinUI.csproj'),
    [ValidateSet('x86', 'x64', 'ARM64')]
    [string]$Platform = 'x64',
    [switch]$SkipBuild,
    [switch]$SkipDebugIdentity,
    [switch]$KeepIdentity,
    [switch]$NoLaunch,
    [switch]$Wait,
    [string[]]$AppArguments = @()
)

$ErrorActionPreference = 'Stop'

function Require-Command {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name is not available on PATH. Run .\eng\Ensure-WindowsCliTools.ps1 -IncludeStoreDeveloperCli:`$false -IncludeStoreClientCli:`$false first."
    }
}

function Get-ProjectValue {
    param(
        [Parameter(Mandatory = $true)]
        [xml]$Project,
        [Parameter(Mandatory = $true)]
        [string]$ElementName
    )

    $node = $Project.Project.PropertyGroup.$ElementName | Select-Object -First 1
    return [string]$node
}

function Get-RuntimeIdentifier {
    param([Parameter(Mandatory = $true)][string]$RequestedPlatform)

    switch ($RequestedPlatform) {
        'x86' { 'win-x86' }
        'x64' { 'win-x64' }
        'ARM64' { 'win-arm64' }
        default { throw "Unsupported platform: $RequestedPlatform" }
    }
}

Require-Command -Name 'dotnet'
Require-Command -Name 'winapp'

$resolvedProjectPath = [System.IO.Path]::GetFullPath($ProjectPath)
if (-not (Test-Path -LiteralPath $resolvedProjectPath)) {
    throw "Project not found: $resolvedProjectPath"
}

$projectDirectory = Split-Path -Parent $resolvedProjectPath
$manifestPath = Join-Path $projectDirectory 'Package.appxmanifest'
if (-not (Test-Path -LiteralPath $manifestPath)) {
    throw "Package manifest not found: $manifestPath"
}

$editorAssetsIndexPath = Join-Path (Split-Path -Parent $projectDirectory) 'artifacts\EditorAssets\dist\index.html'
if (-not $SkipBuild -and -not (Test-Path -LiteralPath $editorAssetsIndexPath)) {
    throw "Embedded editor assets are missing at '$editorAssetsIndexPath'. Run .\sync-vscode-assets.ps1 before launching JitHub.WinUI."
}

if (-not $SkipBuild) {
    Write-Host "Building JitHub.WinUI Debug|$Platform..."
    & dotnet build $resolvedProjectPath -c Debug -p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet build failed.'
    }
}

[xml]$project = Get-Content -LiteralPath $resolvedProjectPath
$targetFramework = Get-ProjectValue -Project $project -ElementName 'TargetFramework'
$runtimeIdentifier = Get-RuntimeIdentifier -RequestedPlatform $Platform
$targetName = [System.IO.Path]::GetFileNameWithoutExtension($resolvedProjectPath)

$outputDirectory = Join-Path $projectDirectory "bin\$Platform\Debug\$targetFramework\$runtimeIdentifier"
$exePath = Join-Path $outputDirectory "$targetName.exe"
if (-not (Test-Path -LiteralPath $exePath)) {
    $exePath = Get-ChildItem -Path (Join-Path $projectDirectory 'bin') -Recurse -Filter "$targetName.exe" -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*\Debug\*' -and $_.FullName -like "*\$runtimeIdentifier\*" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}

if ([string]::IsNullOrWhiteSpace($exePath) -or -not (Test-Path -LiteralPath $exePath)) {
    throw "Unable to find $targetName.exe in the Debug output for $Platform. Build the WinUI project first."
}

Write-Host "Debug executable: $exePath"

if (-not $SkipDebugIdentity) {
    Write-Host 'Applying debug package identity with Windows App CLI...'
    $debugIdentityArguments = @(
        'create-debug-identity',
        $exePath,
        '--manifest',
        $manifestPath
    )

    if ($KeepIdentity) {
        $debugIdentityArguments += '--keep-identity'
    }

    & winapp @debugIdentityArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'winapp create-debug-identity failed.'
    }
}

if ($NoLaunch) {
    Write-Host 'Skipping launch because -NoLaunch was specified.'
    return
}

Write-Host 'Launching JitHub.WinUI...'
$startProcessArguments = @{
    FilePath = $exePath
    WorkingDirectory = Split-Path -Parent $exePath
}

if ($AppArguments.Count -gt 0) {
    $startProcessArguments.ArgumentList = $AppArguments
}

if ($Wait) {
    $startProcessArguments.Wait = $true
}

Start-Process @startProcessArguments
