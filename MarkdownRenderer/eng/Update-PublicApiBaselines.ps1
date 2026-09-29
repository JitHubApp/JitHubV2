[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [switch] $Update,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateSet('x64', 'x86', 'ARM64')]
    [string] $Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
if (-not $Update) {
    throw 'Baseline updates require the explicit -Update switch.'
}

$rendererRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $rendererRoot 'MarkdownRenderer.GitHub.Tests\MarkdownRenderer.GitHub.Tests.csproj'
$baselineDirectory = Join-Path $rendererRoot 'MarkdownRenderer.GitHub.Tests\PublicApiBaselines'
$intermediatePath = 'obj-public-api\'
$outputPath = 'bin-public-api\'
$lockFilePath = 'obj-public-api\packages.lock.json'
$filter = 'FullyQualifiedName~MarkdownRenderer.GitHub.Tests.PublicApiBaselineTests.PublicApiBaselines_MatchBuiltAssemblies'
$globalPackagesLine = dotnet nuget locals global-packages --list
$globalPackagesMatch = [regex]::Match(($globalPackagesLine -join [Environment]::NewLine), '(?m)^global-packages:\s*(.+)$')
if ($LASTEXITCODE -ne 0 -or -not $globalPackagesMatch.Success) {
    throw 'Unable to resolve the NuGet global-packages directory.'
}
$globalPackages = $globalPackagesMatch.Groups[1].Value.Trim()
$textMateGrammarPackage = Join-Path $globalPackages 'textmatesharp.grammars\2.0.3'
if (-not (Test-Path -LiteralPath $textMateGrammarPackage -PathType Container)) {
    throw "The pinned TextMateSharp.Grammars 2.0.3 package is missing from '$textMateGrammarPackage'."
}

$commonProperties = @(
    "-p:Platform=$Platform",
    '-p:RestoreLockedMode=false',
    "-p:BaseIntermediateOutputPath=$intermediatePath",
    "-p:BaseOutputPath=$outputPath",
    "-p:NuGetLockFilePath=$lockFilePath",
    "-p:PkgTextMateSharp_Grammars=$textMateGrammarPackage",
    '-p:UseSharedCompilation=false',
    '-nr:false',
    '--disable-build-servers'
)

dotnet restore $testProject --force-evaluate @commonProperties
if ($LASTEXITCODE -ne 0) {
    throw "Public API baseline restore failed with exit code $LASTEXITCODE."
}

$priorUpdate = $env:MARKDOWNRENDERER_UPDATE_PUBLIC_API_BASELINES
$priorDirectory = $env:MARKDOWNRENDERER_PUBLIC_API_BASELINE_DIRECTORY
try {
    $env:MARKDOWNRENDERER_UPDATE_PUBLIC_API_BASELINES = '1'
    $env:MARKDOWNRENDERER_PUBLIC_API_BASELINE_DIRECTORY = $baselineDirectory
    dotnet test $testProject --no-restore --configuration $Configuration --filter $filter @commonProperties
    if ($LASTEXITCODE -ne 0) {
        throw "Public API baseline generation failed with exit code $LASTEXITCODE."
    }

    # Rebuild so the generated text files are embedded, then verify with update
    # mode forcibly disabled. This prevents an inherited environment variable
    # from masking a serializer or resource-wiring failure.
    Remove-Item Env:\MARKDOWNRENDERER_UPDATE_PUBLIC_API_BASELINES -ErrorAction SilentlyContinue
    Remove-Item Env:\MARKDOWNRENDERER_PUBLIC_API_BASELINE_DIRECTORY -ErrorAction SilentlyContinue
    dotnet test $testProject --no-restore --configuration $Configuration --filter $filter @commonProperties
    if ($LASTEXITCODE -ne 0) {
        throw "Generated public API baselines failed verification with exit code $LASTEXITCODE."
    }
}
finally {
    $env:MARKDOWNRENDERER_UPDATE_PUBLIC_API_BASELINES = $priorUpdate
    $env:MARKDOWNRENDERER_PUBLIC_API_BASELINE_DIRECTORY = $priorDirectory
}

Write-Host "Updated and verified public API baselines in '$baselineDirectory'. Review every diff before commit."
