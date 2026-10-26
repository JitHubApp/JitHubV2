# Runs the offline self-tests of the MarkdownRenderer engineering scripts:
# the Windows Sandbox correctness runners, the README corpus acquisition
# scripts, the resvg vendored-source provenance check and the performance
# foreground coordinator. None of them launches an app, injects input or
# touches the desktop; each runs in its own PowerShell process.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$selfTests = @(
    'MarkdownRenderer\eng\Test-MarkdownCorrectnessUiHarness.ps1'
    'MarkdownRenderer\eng\Test-MarkdownCorrectnessManualDpiHarness.ps1'
    'MarkdownRenderer\eng\Test-MarkdownCorrectnessSandboxFollowUp.ps1'
    'MarkdownRenderer\eng\Test-MarkdownCorrectnessSelectionTargets.ps1'
    'MarkdownRenderer\eng\Test-MarkdownDeferredTouchSandboxProbe.ps1'
    'MarkdownRenderer\eng\Test-MarkdownDeferredTouchSandboxTests.ps1'
    'MarkdownRenderer\eng\Test-MarkdownTouchLifecycleSandboxTests.ps1'
    'MarkdownRenderer\eng\Test-MarkdownSelectionDisableLaunchDiagnostics.ps1'
    'MarkdownRenderer\eng\Test-MarkdownSandboxCrashDumpPolicy.ps1'
    'MarkdownRenderer\eng\Test-MarkdownSandboxCrashDumpSetup.ps1'
    'MarkdownRenderer\eng\Test-SandboxSampleUiConfiguration.ps1'
    'MarkdownRenderer\eng\Test-MarkdownWinUIForegroundCoordinator.ps1'
    'MarkdownRenderer\eng\tests\Invoke-MarkdownRendererCorrectnessHtmlAcquisition.Tests.ps1'
    'MarkdownRenderer\eng\tests\Invoke-MarkdownRendererTop500SourceAcquisition.Tests.ps1'
    'MarkdownRenderer\MarkdownRenderer.Svg.Resvg\eng\Test-ResvgVendoredSourceProvenance.ps1'
    'eng\tests\Invoke-TopReadmeSandboxOfflineSameByteReplay.Tests.ps1'
    'eng\tests\Invoke-TopReadmeSandboxPublicSameByteCapture.Tests.ps1'
)

# Self-tests that read locally acquired data, which is Git-ignored.
$prerequisites = @{
    'MarkdownRenderer\eng\tests\Invoke-MarkdownRendererCorrectnessHtmlAcquisition.Tests.ps1' = @{
        Path = 'artifacts\readme-top500\source-acquisition-615c56723fb1\source-acquisition.json'
        Hint = 'run MarkdownRenderer\eng\Invoke-MarkdownRendererTop500SourceAcquisition.ps1 first'
    }
}

$pwsh = (Get-Process -Id $PID).Path
$failures = [Collections.Generic.List[string]]::new()
$skipped = 0
foreach ($relative in $selfTests) {
    if ($prerequisites.ContainsKey($relative) -and
        -not (Test-Path -LiteralPath (Join-Path $repositoryRoot $prerequisites[$relative].Path))) {
        Write-Host "SKIP $relative (needs the acquired README corpus; $($prerequisites[$relative].Hint))"
        $skipped++
        continue
    }
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
Write-Host "$($selfTests.Count - $skipped) engineering script self-tests passed; $skipped skipped for missing local data."
