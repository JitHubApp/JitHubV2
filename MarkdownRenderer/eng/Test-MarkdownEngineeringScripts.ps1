# Runs the offline self-tests of the MarkdownRenderer engineering scripts:
# the Windows Sandbox correctness runners, the Sandbox sample UI configuration
# and the resvg vendored-source provenance check. None of them launches an
# app, injects input or touches the desktop; each runs in its own PowerShell
# process.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$selfTests = @(
    'MarkdownRenderer\eng\Test-MarkdownCorrectnessUiHarness.ps1'
    'MarkdownRenderer\eng\Test-MarkdownCorrectnessSandboxFollowUp.ps1'
    'MarkdownRenderer\eng\Test-MarkdownCorrectnessSelectionTargets.ps1'
    'MarkdownRenderer\eng\Test-SandboxSampleUiConfiguration.ps1'
    'MarkdownRenderer\src\MarkdownRenderer.Svg.Resvg\eng\Test-ResvgVendoredSourceProvenance.ps1'
)

$pwsh = (Get-Process -Id $PID).Path
$failures = [Collections.Generic.List[string]]::new()
foreach ($relative in $selfTests) {
    $path = Join-Path $repositoryRoot $relative
    $output = @(& $pwsh -NoLogo -NoProfile -File $path 2>&1)
    if ($LASTEXITCODE -eq 0) {
        Write-Host "PASS $relative"
    }
    else {
        $failures.Add($relative)
        Write-Host "FAIL $relative"
        $output | Select-Object -Last 15 | ForEach-Object { Write-Host "    $_" }
    }
}

if ($failures.Count -gt 0) {
    throw "$($failures.Count) of $($selfTests.Count) engineering script self-tests failed."
}
Write-Host "$($selfTests.Count) engineering script self-tests passed."
