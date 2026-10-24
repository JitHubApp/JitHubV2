$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'MarkdownWinUIForegroundCoordinator.psm1') -Force

$referencePath = 'C:\Pinned\Reference\MarkdownRenderer.PerformanceHarness.exe'
$candidatePath = 'C:\Pinned\Candidate\MarkdownRenderer.PerformanceHarness.exe'

function Assert-Equal {
    param(
        [Parameter(Mandatory)][AllowNull()][object] $Expected,
        [Parameter(Mandatory)][AllowNull()][object] $Actual,
        [Parameter(Mandatory)][string] $Name
    )
    if ($Expected -cne $Actual) {
        throw "$Name failed: expected '$Expected'; observed '$Actual'."
    }
}

function Assert-True {
    param([Parameter(Mandatory)][bool] $Value, [Parameter(Mandatory)][string] $Name)
    if (-not $Value) {
        throw "$Name failed."
    }
}

function Assert-False {
    param([Parameter(Mandatory)][bool] $Value, [Parameter(Mandatory)][string] $Name)
    if ($Value) {
        throw "$Name failed."
    }
}

$assertionCount = 0
Assert-Equal -Expected 'Reference' -Actual (Get-MarkdownHarnessRole `
    -ExecutablePath $referencePath.ToLowerInvariant() `
    -ReferenceExecutablePath $referencePath `
    -CandidateExecutablePath $candidatePath) -Name 'case-insensitive exact reference path'
$assertionCount++

Assert-Equal -Expected 'Candidate' -Actual (Get-MarkdownHarnessRole `
    -ExecutablePath $candidatePath `
    -ReferenceExecutablePath $referencePath `
    -CandidateExecutablePath $candidatePath) -Name 'candidate exact path'
$assertionCount++

Assert-Equal -Expected $null -Actual (Get-MarkdownHarnessRole `
    -ExecutablePath 'C:\Pinned\Reference-copy\MarkdownRenderer.PerformanceHarness.exe' `
    -ReferenceExecutablePath $referencePath `
    -CandidateExecutablePath $candidatePath) -Name 'path-prefix near miss'
$assertionCount++

$sequence = 0..3 | ForEach-Object { Get-MarkdownExpectedHarnessRole -LaunchIndex $_ }
Assert-Equal -Expected 'Reference,Candidate,Candidate,Reference' -Actual ($sequence -join ',') -Name 'frozen launch sequence'
$assertionCount++

$launchInstant = [DateTimeOffset]::UtcNow.AddSeconds(-3)
$eventCreatedFileTime = [UInt64]$launchInstant.UtcDateTime.ToFileTimeUtc()
$preflightDeadline = Get-MarkdownHarnessPreflightDeadlineUtc `
    -EventCreatedFileTime $eventCreatedFileTime `
    -TimeoutSeconds 30
$expectedDeadline = $launchInstant.AddSeconds(30)
Assert-Equal `
    -Expected $expectedDeadline.UtcDateTime.ToString('o') `
    -Actual $preflightDeadline.UtcDateTime.ToString('o') `
    -Name 'preflight deadline anchored to process-start event time'
$assertionCount++

$invalidPreflightDeadlineRejected = $false
try {
    Get-MarkdownHarnessPreflightDeadlineUtc -EventCreatedFileTime 0 -TimeoutSeconds 30 | Out-Null
}
catch {
    $invalidPreflightDeadlineRejected = $true
}
Assert-True -Value $invalidPreflightDeadlineRejected -Name 'invalid process-start timestamp rejected'
$assertionCount++

$clickArguments = New-MarkdownWinAppTitleBarClickArguments -ProcessId 1234
Assert-Equal -Expected 'ui,click,TitleBar,-a,1234' -Actual ($clickArguments -join ',') -Name 'single title-bar click arguments'
$assertionCount++

Assert-True -Value (Test-MarkdownForegroundProcessId -ForegroundProcessId 1234 -TargetProcessId 1234) -Name 'foreground PID match'
$assertionCount++

Assert-False -Value (Test-MarkdownForegroundProcessId -ForegroundProcessId 1 -TargetProcessId 1234) -Name 'foreground PID mismatch'
$assertionCount++

Assert-True -Value (Test-MarkdownWinAppCliVersion -VersionText '0.7.0') -Name 'WinApp CLI 0.7 accepted'
$assertionCount++

Assert-False -Value (Test-MarkdownWinAppCliVersion -VersionText '0.6.9') -Name 'old WinApp CLI rejected'
$assertionCount++

Assert-False -Value (Test-MarkdownWinAppCliVersion -VersionText '0.8.0') -Name 'unverified future WinApp CLI rejected'
$assertionCount++

Write-Output "Passed $assertionCount foreground coordinator assertions."
