param(
    [switch]$InstallMissing,
    [switch]$IncludeStoreDeveloperCli = $true,
    [switch]$IncludeWindowsAppCli = $true,
    [switch]$IncludeStoreClientCli = $true
)

$ErrorActionPreference = 'Stop'

function Test-CommandAvailable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    return $null -ne (Get-Command $Name -ErrorAction SilentlyContinue)
}

function Invoke-WingetInstall {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$DisplayName
    )

    if (-not (Test-CommandAvailable -Name 'winget')) {
        throw "winget is required to install $DisplayName automatically. Install it from App Installer or install $DisplayName manually."
    }

    Write-Host "Installing $DisplayName..."
    & winget @Arguments --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "winget failed to install $DisplayName."
    }
}

function Ensure-Tool {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,

        [Parameter(Mandatory = $true)]
        [string]$DisplayName,

        [string[]]$WingetArguments,

        [string]$InstallHint
    )

    if (Test-CommandAvailable -Name $Command) {
        $resolved = (Get-Command $Command).Source
        Write-Host "OK: $DisplayName is available at $resolved"
        return
    }

    if ($InstallMissing -and $WingetArguments) {
        Invoke-WingetInstall -Arguments $WingetArguments -DisplayName $DisplayName
        if (Test-CommandAvailable -Name $Command) {
            $resolved = (Get-Command $Command).Source
            Write-Host "OK: $DisplayName is available at $resolved"
            return
        }
    }

    $suffix = if ([string]::IsNullOrWhiteSpace($InstallHint)) { '' } else { " $InstallHint" }
    throw "$DisplayName is not available on PATH.$suffix"
}

if ($IncludeWindowsAppCli) {
    Ensure-Tool `
        -Command 'winapp' `
        -DisplayName 'Windows App CLI (winapp)' `
        -WingetArguments @('install', '--id', 'Microsoft.WinAppCli', '--source', 'winget') `
        -InstallHint 'Install with: winget install Microsoft.WinAppCli'
}

if ($IncludeStoreDeveloperCli) {
    if ($InstallMissing) {
        Invoke-WingetInstall `
            -Arguments @('install', '--id', 'Microsoft.DotNet.DesktopRuntime.9', '--source', 'winget') `
            -DisplayName '.NET 9 Desktop Runtime for Microsoft Store Developer CLI'
    }

    Ensure-Tool `
        -Command 'msstore' `
        -DisplayName 'Microsoft Store Developer CLI (msstore)' `
        -WingetArguments @('install', '--name', 'Microsoft Store Developer CLI', '--source', 'winget') `
        -InstallHint 'Install with: winget install "Microsoft Store Developer CLI". It also requires the .NET 9 Desktop Runtime.'
}

if ($IncludeStoreClientCli) {
    Ensure-Tool `
        -Command 'store' `
        -DisplayName 'Microsoft Store client CLI (store)' `
        -InstallHint 'It ships with the Microsoft Store experience on supported Windows builds. Update Microsoft Store if it is missing.'
}

Write-Host 'Windows CLI tooling check complete.'
