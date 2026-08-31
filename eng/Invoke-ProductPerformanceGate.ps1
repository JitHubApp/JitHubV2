[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(ParameterSetName = 'Run', Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$AppPath,

    [Parameter(ParameterSetName = 'Gate', Mandatory = $true)]
    [switch]$GateOnly,

    [Parameter(ParameterSetName = 'Plan', Mandatory = $true)]
    [switch]$PlanOnly,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateRange(10, 100)]
    [int]$Iterations = 10,

    [string[]]$Fixtures,

    [string[]]$Routes,

    [string]$OutputPath = (Join-Path $PSScriptRoot '..\artifacts\performance\product-performance-report.json'),

    [string]$ArtifactsPath = (Join-Path $PSScriptRoot '..\artifacts\performance\runs'),

    [string]$Repository = 'JitHubApp/JitHubV2',

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $root 'JitHub.WinUI.PerformanceGate\JitHub.WinUI.PerformanceGate.csproj'

if (-not $SkipBuild) {
    dotnet build $project -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        throw "Performance gate build failed with exit code $LASTEXITCODE."
    }
}

$framework = 'net10.0-windows10.0.19041.0'
$projectDirectory = Split-Path $project
$runner = @(
    (Join-Path $projectDirectory "bin\x64\$Configuration\$framework\JitHub.WinUI.PerformanceGate.dll")
    (Join-Path $projectDirectory "bin\$Configuration\$framework\JitHub.WinUI.PerformanceGate.dll")
) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $runner -or -not (Test-Path -LiteralPath $runner -PathType Leaf)) {
    throw "Performance gate runner was not found under '$projectDirectory\bin'."
}

$command = if ($GateOnly) { 'gate' } elseif ($PlanOnly) { 'plan' } else { 'run' }
$runnerArgs = @(
    $runner,
    $command,
    "--output=$([IO.Path]::GetFullPath($OutputPath))",
    "--artifacts=$([IO.Path]::GetFullPath($ArtifactsPath))",
    "--configuration=$Configuration",
    "--iterations=$Iterations",
    "--repo=$Repository"
)

if ($Fixtures -and $Fixtures.Count -gt 0) {
    $runnerArgs += "--fixtures=$($Fixtures -join ',')"
}

if ($Routes -and $Routes.Count -gt 0) {
    $runnerArgs += "--routes=$($Routes -join ',')"
}

if ($command -eq 'run') {
    if (-not [Environment]::UserInteractive) {
        throw 'The live product performance benchmark requires an interactive Windows desktop session.'
    }

    $runnerArgs += "--app=$([IO.Path]::GetFullPath($AppPath))"
}

& dotnet @runnerArgs
exit $LASTEXITCODE
