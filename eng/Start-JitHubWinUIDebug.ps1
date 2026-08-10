param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\JitHub.WinUI\JitHub.WinUI.csproj'),
    [ValidateSet('x86', 'x64', 'ARM64')]
    [string]$Platform = 'x64',
    [switch]$SkipBuild,
    [switch]$SkipDebugIdentity,
    [switch]$SkipIdentityCleanup,
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

function Ensure-DebugIdentityAssetAliases {
    param([Parameter(Mandatory = $true)][string]$ManifestPath)

    $manifestDirectory = Split-Path -Parent $ManifestPath
    $assetsDirectory = Join-Path $manifestDirectory 'Assets'
    if (-not (Test-Path -LiteralPath $assetsDirectory)) {
        return
    }

    $assetAliases = @{
        'SplashScreen.png' = 'SplashScreen.scale-200.png'
        'Square150x150Logo.png' = 'Square150x150Logo.scale-200.png'
        'Square44x44Logo.png' = 'Square44x44Logo.scale-200.png'
        'Wide310x150Logo.png' = 'Wide310x150Logo.scale-200.png'
        'SmallTile.png' = 'SmallTile.scale-200.png'
        'LargeTile.png' = 'LargeTile.scale-200.png'
    }

    foreach ($alias in $assetAliases.GetEnumerator()) {
        $target = Join-Path $assetsDirectory $alias.Key
        if (Test-Path -LiteralPath $target) {
            continue
        }

        $source = Join-Path $assetsDirectory $alias.Value
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
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
    $looseLayoutAppHost = Join-Path $outputDirectory "AppX\$targetName.exe"
    $currentAssembly = Join-Path $outputDirectory "$targetName.dll"
    if ((Test-Path -LiteralPath $looseLayoutAppHost) -and (Test-Path -LiteralPath $currentAssembly)) {
        # MSIX tooling can leave the freshly built managed payload in the root
        # output while retaining the native apphost only in AppX. The apphost is
        # generic; place it beside the current DLLs so launch/debug never falls
        # back to an older loose-layout payload.
        Copy-Item -LiteralPath $looseLayoutAppHost -Destination $exePath -Force
    }
}
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
$generatedManifestPath = Join-Path $outputDirectory 'AppxManifest.xml'
$looseLayoutManifestPath = Join-Path $outputDirectory 'AppX\AppxManifest.xml'
if (Test-Path -LiteralPath $generatedManifestPath) {
    Ensure-DebugIdentityAssetAliases -ManifestPath $generatedManifestPath
    $manifestPath = $generatedManifestPath
} elseif (Test-Path -LiteralPath $looseLayoutManifestPath) {
    Ensure-DebugIdentityAssetAliases -ManifestPath $looseLayoutManifestPath
    $manifestPath = $looseLayoutManifestPath
}

Write-Host "Debug identity manifest: $manifestPath"

if (-not $SkipDebugIdentity) {
    if (-not $SkipIdentityCleanup) {
        Write-Host 'Removing stale JitHub development identities...'
        & (Join-Path $PSScriptRoot 'Reset-JitHubWinUIDebugIdentity.ps1') -ProjectPath $resolvedProjectPath
        if (-not $?) {
            throw 'Reset-JitHubWinUIDebugIdentity.ps1 failed.'
        }
    }

    Write-Host 'Applying debug package identity with Windows App CLI...'
    $debugIdentityArguments = @(
        'create-debug-identity',
        $exePath,
        '--manifest',
        $manifestPath,
        '--keep-identity'
    )

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
