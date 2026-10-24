param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\JitHub.WinUI\JitHub.WinUI.csproj'),
    [ValidateSet('x86', 'x64', 'ARM64')]
    [string]$Platform = 'x64',
    [switch]$SkipBuild,
    [switch]$SkipLaunch,
    [switch]$SkipScreenshot,
    [switch]$Sandbox,
    [switch]$Aot,
    [string]$AppName = 'JitHub',
    [string]$ScreenshotPath = (Join-Path $PSScriptRoot '..\artifacts\screenshots\winapp-cli\smoke.png')
)

$ErrorActionPreference = 'Stop'

function Require-Command {
    param([Parameter(Mandatory = $true)][string]$Name)

    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "$Name is not available on PATH. Run .\eng\Ensure-WindowsCliTools.ps1 first."
    }
}

Require-Command -Name 'winapp'
if ($Aot -and -not $Sandbox) {
    throw '-Aot is supported only with -Sandbox in this smoke script.'
}

if ($SkipBuild -and $SkipLaunch) {
    Write-Host 'Skipping build and launch. Verifying Windows App CLI command surface only.'
    & winapp create-debug-identity --help | Out-Host
    & winapp ui --help | Out-Host
    return
}

if ($Sandbox) {
    if ($SkipLaunch) {
        throw '-Sandbox requires a launch. Use -SkipBuild -SkipLaunch for command-surface checks.'
    }
    if ($Platform -ne 'x64') {
        throw 'The JitHub sandbox smoke currently provisions and validates Debug x64 only.'
    }
    if ($AppName -ne 'JitHub') {
        throw '-AppName is a host-smoke selector; the sandbox smoke validates JitHub.WinUI only.'
    }
    if ($Aot -and $SkipBuild) {
        throw '-Aot publishes the current sources; do not combine it with -SkipBuild.'
    }
    $expectedProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\JitHub.WinUI\JitHub.WinUI.csproj'))
    if ([IO.Path]::GetFullPath($ProjectPath) -ine $expectedProject) {
        throw '-Sandbox validates the JitHub.WinUI project only; use the renderer sandbox script for its sample.'
    }

    $versionText = (& winapp --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $versionText -notmatch '(\d+)\.(\d+)\.(\d+)') {
        throw "Cannot determine WinApp CLI version: $versionText"
    }
    if ([version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3]) -lt [version]'0.7.0') {
        throw "WinApp CLI 0.7 or newer is required for sandbox smoke tests; found $versionText."
    }

    # The packaged Debug manifest needs Microsoft.VCLibs.140.00.Debug.
    # A clean Windows Sandbox has no SDK payload for winapp to provision.
    $guestProbe = '$p = Get-AppxPackage Microsoft.VCLibs.140.00.Debug | Where-Object Architecture -eq X64 | Select-Object -First 1; if ($p) { $p.Version.ToString() }'
    $guestVersionText = (& winapp target exec sandbox -- powershell -NoProfile -NonInteractive -Command $guestProbe | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not inspect the sandbox Debug VC framework.'
    }
    if ([string]::IsNullOrWhiteSpace($guestVersionText) -or [version]$guestVersionText -lt [version]'14.0.33519.0') {
        $sdkPackage = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft SDKs\Windows Kits\10\ExtensionSDKs\Microsoft.VCLibs\14.0\Appx\Debug\x64\Microsoft.VCLibs.x64.Debug.14.00.appx'
        if (-not (Test-Path -LiteralPath $sdkPackage -PathType Leaf)) {
            throw "The Windows SDK Debug VC framework package is missing: $sdkPackage"
        }
        & winapp target push sandbox $sdkPackage 'prerequisites/Microsoft.VCLibs.x64.Debug.14.00.appx' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Could not stage the Debug VC framework in the sandbox.' }
        & winapp target exec sandbox -- powershell -NoProfile -NonInteractive -Command "Add-AppxPackage -Path 'C:\WinApp\work\prerequisites\Microsoft.VCLibs.x64.Debug.14.00.appx' -ErrorAction Stop" | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Could not install the Debug VC framework in the sandbox.' }
    }

    $previousWorkflowId = $env:WINAPP_UI_WORKFLOW_ID
    $env:WINAPP_UI_WORKFLOW_ID = "jithub-sandbox-$([guid]::NewGuid().ToString('N'))"
    $launched = $false
    try {
        $configuration = if ($Aot) { 'AotDebug' } else { 'Debug' }
        if ($Aot) {
            Require-Command -Name 'dotnet'
            & dotnet restore $expectedProject -p:Configuration=AotDebug -p:Platform=x64 --locked-mode --verbosity quiet
            if ($LASTEXITCODE -ne 0) { throw 'The locked AotDebug project restore failed.' }
        }
        $runArguments = @('run', $expectedProject, '--on', 'sandbox', '--detach', '--json', '-c', $configuration, '--arch', 'x64')
        if ($Aot) { $runArguments += @('--aot', '--no-restore') }
        if ($SkipBuild) { $runArguments += '--no-build' }
        $runText = (& winapp @runArguments 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0) { throw "JitHub did not launch in Windows Sandbox: $runText" }
        # --aot also emits a project-property JSON document before the launch
        # document. The last top-level object is the scoped process result.
        $jsonStarts = [regex]::Matches($runText, '(?m)^\s*\{')
        if ($jsonStarts.Count -eq 0) { throw "The sandbox launch did not return JSON: $runText" }
        $run = $runText.Substring($jsonStarts[$jsonStarts.Count - 1].Index).Trim() | ConvertFrom-Json
        if (-not $run.Sandbox -or $run.ProcessScope -ne 'sandbox' -or $run.ProcessId -le 0) {
            throw 'WinApp CLI did not report a sandbox-scoped JitHub process.'
        }
        if ($Aot) {
            $nativeBinary = Join-Path $PSScriptRoot '..\JitHub.WinUI\bin\x64\AotDebug\net10.0-windows10.0.26100.0\win-x64\publish\JitHub.WinUI.exe'
            if (-not (Test-Path -LiteralPath $nativeBinary -PathType Leaf)) {
                throw 'WinApp CLI did not leave a Native AOT executable in the AotDebug publish output.'
            }
        }
        $launched = $true
        $appTarget = [string]$run.ProcessId
        Write-Host "JitHub started in Windows Sandbox (PID $appTarget)."
        if ($Aot) {
            $hostHash = (Get-FileHash -LiteralPath $nativeBinary -Algorithm SHA256).Hash
            $guestHash = (& winapp target exec sandbox -- powershell -NoProfile -NonInteractive -Command "(Get-FileHash -Path (Get-Process -Id $appTarget).Path -Algorithm SHA256).Hash" | Out-String).Trim()
            if ($LASTEXITCODE -ne 0 -or $guestHash -cne $hostHash) {
                throw 'The sandbox process is not running the Native AOT executable produced by this publish.'
            }
        }

        & winapp ui wait-for LoginSignInButton --on sandbox -a $appTarget --type Button -t 15000 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'The JitHub sign-in shell did not become accessible in the sandbox.' }

        if (-not $SkipScreenshot) {
            $screenshotDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($ScreenshotPath))
            New-Item -ItemType Directory -Path $screenshotDirectory -Force | Out-Null
            & winapp ui screenshot --on sandbox -a $appTarget -o $ScreenshotPath | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'Could not capture the sandbox JitHub window.' }
        }
    }
    finally {
        if ($launched) {
            & winapp ui yield --on sandbox --quiet | Out-Null
        }
        $env:WINAPP_UI_WORKFLOW_ID = $previousWorkflowId
    }
    return
}

$launchArguments = @(
    '-ProjectPath', $ProjectPath,
    '-Platform', $Platform
)

if ($SkipBuild) {
    $launchArguments += '-SkipBuild'
}

if ($SkipLaunch) {
    $launchArguments += '-NoLaunch'
}

& (Join-Path $PSScriptRoot 'Start-JitHubWinUIDebug.ps1') @launchArguments
if ($LASTEXITCODE -ne 0) {
    throw 'Start-JitHubWinUIDebug.ps1 failed.'
}

if (-not $SkipLaunch -and -not $SkipScreenshot) {
    $screenshotDirectory = Split-Path -Parent ([System.IO.Path]::GetFullPath($ScreenshotPath))
    New-Item -ItemType Directory -Path $screenshotDirectory -Force | Out-Null

    Write-Host "Waiting for $AppName and capturing screenshot..."
    & winapp ui wait-for $AppName --app $AppName --timeout 15000
    if ($LASTEXITCODE -ne 0) {
        throw "winapp ui wait-for failed for $AppName."
    }

    & winapp ui screenshot --app $AppName --output $ScreenshotPath
    if ($LASTEXITCODE -ne 0) {
        throw 'winapp ui screenshot failed.'
    }

    Write-Host "Screenshot written to $ScreenshotPath"
}
elseif ($SkipLaunch) {
    Write-Host 'Skipping launch. Verifying Windows App CLI command surface only.'
    & winapp create-debug-identity --help | Out-Host
    & winapp ui --help | Out-Host
}
