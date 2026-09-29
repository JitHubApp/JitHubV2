[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

# The Release audit is framework-dependent, as is the production app. A hosted
# Windows image is not guaranteed to have this exact Windows App Runtime build.
$uri = 'https://aka.ms/windowsappsdk/1.8/1.8.260710003/windowsappruntimeinstall-x64.exe'
$expectedSha256 = 'B8CDA840267AB72797F654F801F9A064AB6D9E508CEDEE3DF79F772F104DB6D6'
$installer = Join-Path $env:RUNNER_TEMP 'WindowsAppRuntimeInstall-1.8.260710003-x64.exe'
Invoke-WebRequest -Uri $uri -OutFile $installer
$actualSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
if ($actualSha256 -ne $expectedSha256) {
    throw "Windows App Runtime installer hash mismatch: $actualSha256"
}

# --quiet is the documented silent deployment mode. Keep the helper hidden on
# unattended runners so installer UI cannot obscure or block the audit app.
$process = Start-Process -FilePath $installer -ArgumentList '--quiet' -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) {
    throw "Windows App Runtime installer failed with exit code $($process.ExitCode)."
}

$runtime = Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.1.8' |
    Where-Object Architecture -eq 'X64' |
    Sort-Object Version -Descending |
    Select-Object -First 1
if ($null -eq $runtime -or [version]$runtime.Version -lt [version]'8000.921.1539.0') {
    throw 'The pinned x64 Windows App Runtime 1.8.10 was not installed.'
}

$packageFamilies = @(Get-AppxPackage | Select-Object -ExpandProperty PackageFamilyName -Unique)
$requiredFamilies = @(
    'Microsoft.WindowsAppRuntime.1.8_8wekyb3d8bbwe',
    'MicrosoftCorporationII.WinAppRuntime.Main.1.8_8wekyb3d8bbwe',
    'MicrosoftCorporationII.WinAppRuntime.Singleton_8wekyb3d8bbwe',
    'Microsoft.WinAppRuntime.DDLM.8000.921.1539.0-x6_8wekyb3d8bbwe'
)
$missingFamilies = @($requiredFamilies | Where-Object { $_ -notin $packageFamilies })
if ($missingFamilies.Count -ne 0) {
    throw "Windows App Runtime installation is incomplete: $($missingFamilies -join ', ')"
}
Write-Host "Using Windows App Runtime $($runtime.Version) ($($runtime.Architecture))."
