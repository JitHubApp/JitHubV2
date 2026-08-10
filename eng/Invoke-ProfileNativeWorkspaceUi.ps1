param(
    [string]$AppPath = "artifacts\profile-debug\bin\JitHub.WinUI.exe",
    [string]$OutputDirectory = "artifacts\screenshots\winui-vnext-shell\profile-native-workspace"
)

$ErrorActionPreference = "Stop"

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ProfileWindowSizing
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
"@

function Invoke-WinApp {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & winapp @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "winapp failed: $($Arguments -join ' ')"
    }
}

function Set-ProfileWindowSize {
    param(
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][int]$Width,
        [Parameter(Mandatory)][int]$Height
    )

    $window = (winapp ui list-windows -a $ProcessId --json 2>$null | ConvertFrom-Json | Select-Object -First 1)
    if ($null -eq $window) {
        throw "JitHub profile window was not found."
    }

    $handle = [IntPtr]::new([Int64]$window.hwnd)
    if (-not [ProfileWindowSizing]::SetWindowPos($handle, [IntPtr]::Zero, 80, 80, $Width, $Height, 0x0040)) {
        throw "Could not resize the profile window to ${Width}x${Height}."
    }
    Start-Sleep -Milliseconds 450
}

$resolvedApp = (Resolve-Path $AppPath).Path
$resolvedOutput = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDirectory))
New-Item -ItemType Directory -Force -Path $resolvedOutput | Out-Null

# A unique apphost name keeps this focused run isolated from parallel JitHub probes.
$isolatedApp = Join-Path (Split-Path $resolvedApp) "JitHub.ProfileWorkspaceQA.exe"
Copy-Item -LiteralPath $resolvedApp -Destination $isolatedApp -Force
$process = Start-Process -FilePath $isolatedApp -ArgumentList @(
    "--page=profile",
    "--scenario=profile-native-workspace",
    "--theme=dark"
) -PassThru

try {
    Invoke-WinApp @("ui", "wait-for", "ProfileModeOverviewItem", "-a", "$($process.Id)", "-t", "15000")

    $sizes = @(
        @{ Width = 1366; Height = 900; Name = "1366x900" },
        @{ Width = 1180; Height = 800; Name = "1180x800" },
        @{ Width = 900; Height = 700; Name = "900x700" },
        @{ Width = 760; Height = 650; Name = "760x650" },
        @{ Width = 640; Height = 600; Name = "640x600" }
    )

    foreach ($size in $sizes) {
        Set-ProfileWindowSize -ProcessId $process.Id -Width $size.Width -Height $size.Height
        Invoke-WinApp @(
            "ui", "screenshot", "-a", "$($process.Id)",
            "-o", (Join-Path $resolvedOutput "$($size.Name)-overview.png")
        )

        Invoke-WinApp @("ui", "wait-for", "ProfileModeOverviewItem", "-a", "$($process.Id)", "-t", "3000")
        Invoke-WinApp @("ui", "wait-for", "ProfileModeRepositoriesItem", "-a", "$($process.Id)", "-t", "3000")
        Invoke-WinApp @("ui", "wait-for", "ProfileModeActivityItem", "-a", "$($process.Id)", "-t", "3000")
        Invoke-WinApp @("ui", "wait-for", "ProfileModeReadmeItem", "-a", "$($process.Id)", "-t", "3000")
    }

    Set-ProfileWindowSize -ProcessId $process.Id -Width 1180 -Height 800
    Invoke-WinApp @("ui", "invoke", "ProfileModeRepositoriesItem", "-a", "$($process.Id)")
    Invoke-WinApp @("ui", "wait-for", "ProfileRepositoriesList", "-a", "$($process.Id)", "-t", "5000")
    Start-Sleep -Milliseconds 350
    Invoke-WinApp @("ui", "screenshot", "-a", "$($process.Id)", "-o", (Join-Path $resolvedOutput "repositories-mode.png"))

    Invoke-WinApp @("ui", "invoke", "ProfileModeActivityItem", "-a", "$($process.Id)")
    Invoke-WinApp @("ui", "wait-for", "ProfileActivityList", "-a", "$($process.Id)", "-t", "5000")
    Start-Sleep -Milliseconds 350
    Invoke-WinApp @("ui", "screenshot", "-a", "$($process.Id)", "-o", (Join-Path $resolvedOutput "activity-mode.png"))

    Invoke-WinApp @("ui", "invoke", "ProfileFollowersStatTile", "-a", "$($process.Id)")
    Invoke-WinApp @("ui", "wait-for", "ProfilePeopleBackButton", "-a", "$($process.Id)", "-t", "5000")
    Invoke-WinApp @("ui", "invoke", "ProfilePeopleBackButton", "-a", "$($process.Id)")
    Invoke-WinApp @("ui", "wait-for", "ProfileModeOverviewItem", "-a", "$($process.Id)", "-t", "3000")

    Invoke-WinApp @("ui", "invoke", "ProfileModeReadmeItem", "-a", "$($process.Id)")
    Invoke-WinApp @("ui", "wait-for", "ProfileReadmeScrollViewer", "-a", "$($process.Id)", "-t", "5000")
    # Markdown realization is intentionally lazy and completes asynchronously.
    # Capture the settled workspace, not the renderer's first layout frame.
    Start-Sleep -Milliseconds 1800
    Invoke-WinApp @("ui", "screenshot", "-a", "$($process.Id)", "-o", (Join-Path $resolvedOutput "readme-mode.png"))

    $tree = winapp ui inspect -a $process.Id --interactive --json 2>$null | ConvertFrom-Json
    $profileControlsWithoutIds = @($tree.windows.elements | Where-Object {
        $_.name -match "Profile|Public repositories|Public activity|README|Followers|Following|Stars library" -and
        -not $_.automationId
    })
    if ($profileControlsWithoutIds.Count -gt 0) {
        throw "Profile interactive controls without AutomationId: $($profileControlsWithoutIds.name -join ', ')"
    }

    @{ Passed = $true; ProcessId = $process.Id; Screenshots = $sizes.Count + 3 } |
        ConvertTo-Json |
        Set-Content -Path (Join-Path $resolvedOutput "profile-native-workspace-results.json")
}
finally {
    Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
}
