param(
    [switch]$RequireConfigured
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command msstore -ErrorAction SilentlyContinue)) {
    throw 'Microsoft Store Developer CLI (msstore) is not available. Run .\eng\Ensure-WindowsCliTools.ps1 -InstallMissing or install it manually.'
}

Write-Host 'Microsoft Store Developer CLI is available.'
& msstore --help | Select-Object -First 80 | Out-Host

if ($RequireConfigured) {
    Write-Host 'Checking Microsoft Store Developer CLI configuration...'
    & msstore info
    if ($LASTEXITCODE -ne 0) {
        throw 'msstore info failed. Run msstore reconfigure with Partner Center credentials.'
    }
}
else {
    Write-Host 'Skipping credential check. Pass -RequireConfigured to run msstore info.'
}
