[CmdletBinding()]
param(
    [ValidateRange(1, 500)]
    [int]$Count = 500,

    [ValidateRange(1, 500)]
    [int]$StartRank = 1,

    [string]$OutputRoot = "",

    [string]$ManifestPath = "",

    [switch]$Resume,

    [switch]$ReuseBrowserEvidence,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $stamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss")
    $OutputRoot = Join-Path $repoRoot "artifacts\readme-top500\run-$stamp"
}
$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $OutputRoot "top-repositories.json"
}
$ManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
$requiredManifestCount = $StartRank + $Count - 1
if (-not (Test-Path -LiteralPath $ManifestPath)) {
    & (Join-Path $repoRoot "eng\readme-audit\New-TopReadmeManifest.ps1") `
        -Count $requiredManifestCount `
        -OutputPath $ManifestPath
    if ($LASTEXITCODE -ne 0) { throw "README manifest generation failed." }
}

$appProject = Join-Path $repoRoot "JitHub.WinUI\JitHub.WinUI.csproj"
$automationProject = Join-Path $repoRoot "JitHub.WinUI.Automation\JitHub.WinUI.Automation.csproj"
if (-not $SkipBuild) {
    $appBuildArguments = @(
        "build", $appProject,
        "-c", $Configuration,
        "-p:Platform=x64",
        "-p:GenerateAppxPackageOnBuild=false",
        "--disable-build-servers",
        "-m:1")
    if ($Configuration -eq "Release") {
        $appBuildArguments += "-p:SkipReleaseSecurityGate=true"
    }
    & dotnet @appBuildArguments
    if ($LASTEXITCODE -ne 0) { throw "JitHub build failed." }
    $automationBuildArguments = @(
        "build", $automationProject,
        "-c", $Configuration,
        "-p:Platform=x64",
        "--disable-build-servers",
        "-m:1")
    if ($Configuration -eq "Release") {
        $automationBuildArguments += @("-r", "win-x64")
    }
    & dotnet @automationBuildArguments
    if ($LASTEXITCODE -ne 0) { throw "README audit harness build failed." }
}

$appPath = Join-Path $repoRoot "JitHub.WinUI\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\JitHub.WinUI.exe"
$runnerRidSegment = if ($Configuration -eq "Release") { "\win-x64" } else { "" }
$runnerPath = Join-Path $repoRoot "JitHub.WinUI.Automation\bin\x64\$Configuration\net10.0-windows10.0.19041.0$runnerRidSegment\JitHub.WinUI.Automation.dll"
$browserScript = Join-Path $repoRoot "eng\readme-audit\browser-oracle.mjs"
foreach ($requiredPath in @($appPath, $runnerPath, $browserScript)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required audit artifact was not found: '$requiredPath'."
    }
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    throw "GitHub CLI is required for authenticated corpus rendering."
}
$auditToken = (& gh auth token).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($auditToken)) {
    throw "Could not obtain the authenticated GitHub token for the isolated audit processes."
}

$tokenBytes = [System.Text.Encoding]::UTF8.GetBytes($auditToken)
$tokenHashBytes = [System.Security.Cryptography.SHA256]::HashData($tokenBytes)
$tokenHash = [Convert]::ToHexString($tokenHashBytes)
# A 60-bit positive identifier is stable for the token, does not disclose it,
# and preserves the production cache's per-account isolation. Audit data roots
# are unique per case, so this identity never enters a user's normal cache.
$auditAccountId = [Convert]::ToInt64($tokenHash.Substring(0, 15), 16).ToString(
    [System.Globalization.CultureInfo]::InvariantCulture)
[Array]::Clear($tokenBytes, 0, $tokenBytes.Length)
[Array]::Clear($tokenHashBytes, 0, $tokenHashBytes.Length)

$previousToken = $env:JITHUB_README_AUDIT_GITHUB_TOKEN
$previousAccountId = $env:JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID
try {
    $env:JITHUB_README_AUDIT_GITHUB_TOKEN = $auditToken
    $env:JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID = $auditAccountId
    $arguments = @(
        $runnerPath,
        "--probe=readme-production-audit",
        "--app=$appPath",
        "--out=$OutputRoot",
        "--manifest=$ManifestPath",
        "--browser-script=$browserScript",
        "--start-rank=$StartRank",
        "--count=$Count")
    if ($Resume) { $arguments += "--resume" }
    if ($ReuseBrowserEvidence) { $arguments += "--reuse-browser-evidence" }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Top README audit failed. See '$OutputRoot\summary.md'."
    }
}
finally {
    $env:JITHUB_README_AUDIT_GITHUB_TOKEN = $previousToken
    $env:JITHUB_README_AUDIT_GITHUB_ACCOUNT_ID = $previousAccountId
    $auditToken = $null
    $auditAccountId = $null
}

Write-Host "Top README audit passed. Report: '$OutputRoot\summary.md'."
