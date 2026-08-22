[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Container })]
    [string]$InputPath,

    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [ValidateSet('x86', 'x64', 'arm64')]
    [string]$Architecture = 'x64',

    [ValidateSet('light', 'dark')]
    [string]$Theme = 'dark',

    [string]$Repository = 'JitHubApp/JitHubV2',
    [string[]]$Routes,
    [ValidateRange(0, 7680)]
    [int]$ViewportWidth = 0,
    [ValidateRange(0, 4320)]
    [int]$ViewportHeight = 0,
    [switch]$HighContrast,
    [string]$Scenario,
    [string]$AutomationDataRoot,
    [ValidateRange(5, 120)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'

if (($ViewportWidth -eq 0) -xor ($ViewportHeight -eq 0)) {
    throw 'ViewportWidth and ViewportHeight must be supplied together.'
}
if ($ViewportWidth -ne 0 -and ($ViewportWidth -lt 480 -or $ViewportHeight -lt 480)) {
    throw 'Native AOT UI matrix viewports must be at least 480 by 480 pixels.'
}

if (-not ('JitHub.NativeAotVerification.NativeUi' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace JitHub.NativeAotVerification
{
    public sealed class HighContrastSnapshot
    {
        public uint Flags { get; set; }
        public string DefaultScheme { get; set; }
        public bool IsEnabled { get { return (Flags & 0x00000001u) != 0; } }
    }

    public static class NativeUi
    {
        private const uint SpiGetHighContrast = 0x0042;
        private const uint SpiSetHighContrast = 0x0043;
        private const uint HcfHighContrastOn = 0x00000001;
        private const uint HcfOptionNoThemeChange = 0x00001000;
        private const uint SpifUpdateIniFile = 0x0001;
        private const uint SpifSendChange = 0x0002;
        private const uint SwpNoMove = 0x0002;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const int SwRestore = 9;

        public static void ResizeProcessWindow(int processId, int width, int height)
        {
            IntPtr windowHandle = IntPtr.Zero;
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    process.Refresh();
                    windowHandle = process.MainWindowHandle;
                }
                if (windowHandle != IntPtr.Zero)
                {
                    break;
                }
                Thread.Sleep(100);
            }

            if (windowHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException("The packaged app did not expose a main window for viewport sizing.");
            }

            ShowWindow(windowHandle, SwRestore);
            Thread.Sleep(150);
            if (!SetWindowPos(
                    windowHandle,
                    IntPtr.Zero,
                    0,
                    0,
                    width,
                    height,
                    SwpNoMove | SwpNoZOrder | SwpNoActivate))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowPos failed.");
            }
            Thread.Sleep(500);
        }

        public static HighContrastSnapshot CaptureHighContrast()
        {
            return ReadHighContrast();
        }

        public static void EnableHighContrast()
        {
            HighContrastSnapshot current = ReadHighContrast();
            if (!current.IsEnabled)
            {
                WriteHighContrast(new HighContrastSnapshot
                {
                    Flags = current.Flags | HcfHighContrastOn,
                    DefaultScheme = current.DefaultScheme
                });
                WaitForHighContrast(true);
                Thread.Sleep(1_250);
            }
        }

        public static void RestoreHighContrast(HighContrastSnapshot prior)
        {
            HighContrastSnapshot current = ReadHighContrast();
            if (current.Flags != prior.Flags ||
                !string.Equals(current.DefaultScheme, prior.DefaultScheme, StringComparison.Ordinal))
            {
                WriteHighContrast(prior);
            }
            WaitForHighContrast(prior.IsEnabled);
        }

        private static HighContrastSnapshot ReadHighContrast()
        {
            NativeHighContrast native = new NativeHighContrast
            {
                Size = (uint)Marshal.SizeOf(typeof(NativeHighContrast))
            };
            if (!SystemParametersInfoW(SpiGetHighContrast, native.Size, ref native, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SPI_GETHIGHCONTRAST failed.");
            }
            return new HighContrastSnapshot
            {
                Flags = native.Flags,
                DefaultScheme = native.DefaultScheme == IntPtr.Zero
                    ? null
                    : Marshal.PtrToStringUni(native.DefaultScheme)
            };
        }

        private static void WriteHighContrast(HighContrastSnapshot target)
        {
            HighContrastSnapshot current = ReadHighContrast();
            uint targetFlags = target.Flags;
            if (current.IsEnabled != target.IsEnabled)
            {
                targetFlags &= ~HcfOptionNoThemeChange;
            }

            IntPtr scheme = target.DefaultScheme == null
                ? IntPtr.Zero
                : Marshal.StringToHGlobalUni(target.DefaultScheme);
            try
            {
                NativeHighContrast native = new NativeHighContrast
                {
                    Size = (uint)Marshal.SizeOf(typeof(NativeHighContrast)),
                    Flags = targetFlags,
                    DefaultScheme = scheme
                };
                if (!SystemParametersInfoW(
                        SpiSetHighContrast,
                        native.Size,
                        ref native,
                        SpifUpdateIniFile | SpifSendChange))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "SPI_SETHIGHCONTRAST failed.");
                }
            }
            finally
            {
                if (scheme != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(scheme);
                }
            }
        }

        private static void WaitForHighContrast(bool expected)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.Elapsed < TimeSpan.FromSeconds(15))
            {
                if (ReadHighContrast().IsEnabled == expected)
                {
                    return;
                }
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("Timed out waiting for the requested Windows High Contrast state.");
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr windowHandle, int command);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr windowHandle,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfoW(
            uint action,
            uint parameter,
            ref NativeHighContrast highContrast,
            uint updateFlags);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeHighContrast
        {
            public uint Size;
            public uint Flags;
            public IntPtr DefaultScheme;
        }
    }
}
'@
}

$visualMode = if ($HighContrast) { 'highcontrast' } else { $Theme }

function Invoke-WinAppCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = & winapp @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "winapp failed with exit code $LASTEXITCODE`: $($Arguments -join ' ')`n$($output -join [Environment]::NewLine)"
    }

    return $output
}

function Wait-ForElement {
    param(
        [Parameter(Mandatory = $true)]
        [int]$AppProcessId,

        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [int]$TimeoutMilliseconds = ($TimeoutSeconds * 1000)
    )

    Invoke-WinAppCommand -Arguments @(
        'ui', 'wait-for', $AutomationId,
        '-a', $AppProcessId.ToString([Globalization.CultureInfo]::InvariantCulture),
        '-t', $TimeoutMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    ) | Out-Null
}

function Wait-ForElementProperty {
    param(
        [Parameter(Mandatory = $true)]
        [int]$AppProcessId,

        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [Parameter(Mandatory = $true)]
        [string]$Property,

        [Parameter(Mandatory = $true)]
        [string]$Value,

        [switch]$Contains,

        [int]$TimeoutMilliseconds = ($TimeoutSeconds * 1000)
    )

    $arguments = @(
        'ui', 'wait-for', $AutomationId,
        '-a', $AppProcessId.ToString([Globalization.CultureInfo]::InvariantCulture),
        '-t', $TimeoutMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--property', $Property, '--value', $Value
    )
    if ($Contains) {
        $arguments += '--contains'
    }

    Invoke-WinAppCommand -Arguments $arguments | Out-Null
}

function Invoke-Element {
    param(
        [Parameter(Mandatory = $true)]
        [int]$AppProcessId,

        [Parameter(Mandatory = $true)]
        [string]$AutomationId,

        [ValidateSet('invoke', 'click')]
        [string]$Interaction = 'invoke'
    )

    Invoke-WinAppCommand -Arguments @(
        'ui', $Interaction, $AutomationId,
        '-a', $AppProcessId.ToString([Globalization.CultureInfo]::InvariantCulture)
    ) | Out-Null
}

function Test-VisibleElement {
    param(
        [Parameter(Mandatory = $true)]
        [int]$AppProcessId,

        [Parameter(Mandatory = $true)]
        [string]$AutomationId
    )

    $output = & winapp ui get-property $AutomationId `
        -a $AppProcessId.ToString([Globalization.CultureInfo]::InvariantCulture) `
        --property IsOffscreen --json 2>&1
    if ($LASTEXITCODE -ne 0) {
        return $false
    }

    try {
        $result = $output -join [Environment]::NewLine | ConvertFrom-Json
        return $null -eq $result.error -and $result.properties.IsOffscreen -eq 'False'
    }
    catch {
        return $false
    }
}

function Wait-ForAnyVisibleElement {
    param(
        [Parameter(Mandatory = $true)]
        [int]$AppProcessId,

        [Parameter(Mandatory = $true)]
        [string[]]$AutomationIds,

        [int]$TimeoutMilliseconds = ($TimeoutSeconds * 1000)
    )

    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.ElapsedMilliseconds -lt $TimeoutMilliseconds) {
        foreach ($automationId in $AutomationIds) {
            if (Test-VisibleElement -AppProcessId $AppProcessId -AutomationId $automationId) {
                return $automationId
            }
        }
        Start-Sleep -Milliseconds 100
    }

    throw "None of the expected UI elements became visible: $($AutomationIds -join ', ')"
}

function Invoke-SectionMatrix {
    param(
        [Parameter(Mandatory = $true)]
        [int]$AppProcessId,

        [Parameter(Mandatory = $true)]
        [object[]]$Sections
    )

    foreach ($section in $Sections) {
        if ($section.Scroll -ne $false) {
            Invoke-WinAppCommand -Arguments @(
                'ui', 'scroll-into-view', $section.Action,
                '-a', $AppProcessId.ToString([Globalization.CultureInfo]::InvariantCulture)
            ) | Out-Null
        }
        $interaction = if ($null -ne $section.Interaction) { $section.Interaction } else { 'invoke' }
        Invoke-Element -AppProcessId $AppProcessId -AutomationId $section.Action -Interaction $interaction
        Wait-ForElement -AppProcessId $AppProcessId -AutomationId $section.Target
    }
}

function Invoke-CompactSectionMatrix {
    param(
        [Parameter(Mandatory = $true)]
        [int]$AppProcessId,

        [Parameter(Mandatory = $true)]
        [string]$PickerAutomationId,

        [Parameter(Mandatory = $true)]
        [object[]]$Sections
    )

    foreach ($section in $Sections) {
        Invoke-Element -AppProcessId $AppProcessId -AutomationId $PickerAutomationId -Interaction 'click'
        Wait-ForElement -AppProcessId $AppProcessId -AutomationId $section.Action
        Invoke-Element -AppProcessId $AppProcessId -AutomationId $section.Action -Interaction 'click'
        Wait-ForElement -AppProcessId $AppProcessId -AutomationId $section.Target
    }
}

function Save-RouteEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [int]$AppProcessId,

        [Parameter(Mandatory = $true)]
        [string]$RouteName,

        [switch]$CaptureScreen
    )

    $treePath = Join-Path $resolvedOutputDirectory "$RouteName-uia.json"
    $tree = Invoke-WinAppCommand -Arguments @(
        'ui', 'inspect', 'JitHubMainWindowRoot',
        '-a', $AppProcessId.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--depth', '10', '--json'
    )
    $tree -join [Environment]::NewLine | Set-Content -LiteralPath $treePath -Encoding utf8

    $screenshotArguments = @(
        'ui', 'screenshot', 'JitHubMainWindowRoot',
        '-a', $AppProcessId.ToString([Globalization.CultureInfo]::InvariantCulture),
        '-o', (Join-Path $resolvedOutputDirectory "$RouteName-$visualMode.png")
    )
    if ($CaptureScreen) {
        $screenshotArguments += '--capture-screen'
    }

    Invoke-WinAppCommand -Arguments $screenshotArguments | Out-Null
}

$resolvedInputPath = [System.IO.Path]::GetFullPath($InputPath)
$resolvedManifestPath = [System.IO.Path]::GetFullPath($ManifestPath)
$resolvedOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$resolvedAutomationDataRoot = if ([string]::IsNullOrWhiteSpace($AutomationDataRoot)) {
    $null
} else {
    [System.IO.Path]::GetFullPath($AutomationDataRoot)
}
$manifestText = Get-Content -LiteralPath $resolvedManifestPath -Raw
if ($manifestText -notmatch '<PackageDependency[^>]+Name="Microsoft\.WindowsAppRuntime\.') {
    throw "Native AOT UI validation requires the generated Release AppxManifest.xml with a Windows App Runtime package dependency: $resolvedManifestPath"
}
if ($manifestText -notmatch 'ActivatableClassId="WinUIEditor\.CodeEditorControl"') {
    throw "Native AOT UI validation requires the generated Release AppxManifest.xml with WinUIEdit activation metadata: $resolvedManifestPath"
}
New-Item -ItemType Directory -Path $resolvedOutputDirectory -Force | Out-Null
if ($null -ne $resolvedAutomationDataRoot) {
    New-Item -ItemType Directory -Path $resolvedAutomationDataRoot -Force | Out-Null
}

$routeDefinitions = @(
    [pscustomobject]@{ Name = 'login'; Page = 'login'; Target = 'LoginRoot' }
    [pscustomobject]@{ Name = 'shell'; Page = 'shell'; Target = 'ShellRoot' }
    [pscustomobject]@{ Name = 'home'; Page = 'home'; Target = 'DashboardPageRoot' }
    [pscustomobject]@{ Name = 'my-issues'; Page = 'my-issues'; Target = 'MyIssuesPageRoot' }
    [pscustomobject]@{ Name = 'my-pull-requests'; Page = 'my-pull-requests'; Target = 'MyPullRequestsPageRoot' }
    [pscustomobject]@{ Name = 'profile'; Page = 'profile'; Target = 'ProfilePageRoot' }
    [pscustomobject]@{ Name = 'repositories'; Page = 'repositories'; Target = 'RepoManagePageRoot' }
    [pscustomobject]@{ Name = 'settings'; Page = 'settings'; Target = 'SettingsPageTitle' }
    [pscustomobject]@{ Name = 'notifications'; Page = 'notifications'; Target = 'NotificationsPageRoot' }
    [pscustomobject]@{ Name = 'stars'; Page = 'stars'; Target = 'StarsPageRoot' }
    [pscustomobject]@{ Name = 'gists'; Page = 'gists'; Target = 'GistsNew' }
    [pscustomobject]@{ Name = 'repo'; Page = 'repo'; Target = 'RepoDetailPageRoot' }
    [pscustomobject]@{ Name = 'repo-code'; Page = 'repo-code'; Target = 'RepoCodePageRoot' }
    [pscustomobject]@{ Name = 'repo-issues'; Page = 'repo-issues'; Target = 'RepoIssuesPageRoot' }
    [pscustomobject]@{ Name = 'repo-pull-requests'; Page = 'repo-pull-requests'; Target = 'RepoPullRequestsPageRoot' }
    [pscustomobject]@{ Name = 'repo-commits'; Page = 'repo-commits'; Target = 'RepoCommitsPageRoot' }
)

if ($Routes -and $Routes.Count -gt 0) {
    $unknownRoutes = @($Routes | Where-Object { $_ -notin $routeDefinitions.Name })
    if ($unknownRoutes.Count -gt 0) {
        throw "Unknown Native AOT UI routes: $($unknownRoutes -join ', ')"
    }

    $routeDefinitions = @($routeDefinitions | Where-Object { $_.Name -in $Routes })
}

$results = [System.Collections.Generic.List[object]]::new()
$failures = [System.Collections.Generic.List[string]]::new()
$highContrastSnapshot = if ($HighContrast) {
    [JitHub.NativeAotVerification.NativeUi]::CaptureHighContrast()
} else {
    $null
}
$priorAutomationDataRoot = [Environment]::GetEnvironmentVariable(
    'JITHUB_AUTOMATION_DATA_ROOT',
    [EnvironmentVariableTarget]::Process)

try {
if ($null -ne $resolvedAutomationDataRoot) {
    [Environment]::SetEnvironmentVariable(
        'JITHUB_AUTOMATION_DATA_ROOT',
        $resolvedAutomationDataRoot,
        [EnvironmentVariableTarget]::Process)
}
if ($HighContrast) {
    [JitHub.NativeAotVerification.NativeUi]::EnableHighContrast()
}
foreach ($route in $routeDefinitions) {
    $launch = $null
    $routeUsesCompactLayout = $false
    $routeReadyAutomationId = $null
    $routeStartedAt = [DateTimeOffset]::UtcNow
    try {
        $layoutDirectory = Join-Path $resolvedOutputDirectory "layouts\$($route.Name)"
        $appArguments = "--page=$($route.Page) --theme=$Theme --repo=$Repository"
        if (-not [string]::IsNullOrWhiteSpace($Scenario)) {
            $appArguments += " --scenario=$Scenario"
        }
        $launchOutput = Invoke-WinAppCommand -Arguments @(
            'run', $resolvedInputPath,
            '--manifest', $resolvedManifestPath,
            '--output-appx-directory', $layoutDirectory,
            '--exe', 'JitHub.WinUI.exe',
            '--args', $appArguments,
            '--clean', '--detach', '--json'
        )
        $launch = $launchOutput -join [Environment]::NewLine | ConvertFrom-Json
        $appProcessId = [int]$launch.ProcessId

        Wait-ForElement -AppProcessId $appProcessId -AutomationId 'JitHubMainWindowRoot'
        if ($ViewportWidth -ne 0) {
            [JitHub.NativeAotVerification.NativeUi]::ResizeProcessWindow(
                $appProcessId,
                $ViewportWidth,
                $ViewportHeight)
        }
        if ($route.Name -eq 'gists') {
            $routeReadyAutomationId = Wait-ForAnyVisibleElement `
                -AppProcessId $appProcessId `
                -AutomationIds @('GistsNew', 'GistsLeadingPaneButton')
        }
        else {
            Wait-ForElement -AppProcessId $appProcessId -AutomationId $route.Target
        }
        if ($null -eq $routeReadyAutomationId) {
            $routeReadyAutomationId = switch ($route.Name) {
                'home' {
                    Wait-ForAnyVisibleElement -AppProcessId $appProcessId `
                        -AutomationIds @('DashboardWidget_overview', 'DashboardOverviewDrawerButton')
                }
                'settings' {
                    Wait-ForAnyVisibleElement -AppProcessId $appProcessId `
                        -AutomationIds @('SettingsSection_appearance', 'SettingsCompactSectionPicker')
                }
                'stars' {
                    Wait-ForAnyVisibleElement -AppProcessId $appProcessId `
                        -AutomationIds @('StarsNewCategory', 'StarsOpenCategories')
                }
                'repo-code' {
                    Wait-ForAnyVisibleElement -AppProcessId $appProcessId `
                        -AutomationIds @('RepoCodeTreeItem_path_data_csv_c67a2ff6', 'RepoCodeOpenFileTreeButton')
                }
                'repo-pull-requests' {
                    Wait-ForAnyVisibleElement -AppProcessId $appProcessId `
                        -AutomationIds @('RepoPullRequestsSection_Conversation', 'RepoPullRequestsSectionComboBox')
                }
                default { $null }
            }
        }
        $routeUsesCompactLayout = switch ($route.Name) {
            'home' { $routeReadyAutomationId -eq 'DashboardOverviewDrawerButton' }
            'settings' { $routeReadyAutomationId -eq 'SettingsCompactSectionPicker' }
            'stars' { $routeReadyAutomationId -eq 'StarsOpenCategories' }
            'gists' { $routeReadyAutomationId -eq 'GistsLeadingPaneButton' }
            'repo-code' { $routeReadyAutomationId -eq 'RepoCodeOpenFileTreeButton' }
            'repo-pull-requests' { $routeReadyAutomationId -eq 'RepoPullRequestsSectionComboBox' }
            default { $false }
        }

        $captureScreen = $false
        switch ($route.Name) {
            'home' {
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'DashboardWidget_recent_activity'
                if ($routeUsesCompactLayout) {
                    Wait-ForElement -AppProcessId $appProcessId -AutomationId 'DashboardOverviewDrawerButton'
                    Invoke-Element -AppProcessId $appProcessId -AutomationId 'DashboardOverviewDrawerButton'
                    Wait-ForElement -AppProcessId $appProcessId -AutomationId 'DashboardSideDrawerCloseButton'
                    Wait-ForElement -AppProcessId $appProcessId -AutomationId 'DashboardWidget_overview'
                }
                else {
                    Wait-ForElement -AppProcessId $appProcessId -AutomationId 'DashboardWidget_overview'
                }
            }
            'settings' {
                for ($settingsCycle = 0; $settingsCycle -lt 2; $settingsCycle++) {
                    if ($routeUsesCompactLayout) {
                        Invoke-CompactSectionMatrix -AppProcessId $appProcessId -PickerAutomationId 'SettingsCompactSectionPicker' -Sections @(
                            [pscustomobject]@{ Action = 'SettingsSection_appearance_Compact'; Target = 'SettingsThemeSystem' }
                            [pscustomobject]@{ Action = 'SettingsSection_general_Compact'; Target = 'SettingsDeveloperModeToggle' }
                            [pscustomobject]@{ Action = 'SettingsSection_privacy_Compact'; Target = 'SettingsDiagnosticsToggle' }
                            [pscustomobject]@{ Action = 'SettingsSection_data-cache_Compact'; Target = 'SettingsClearQueryCacheButton' }
                            [pscustomobject]@{ Action = 'SettingsSection_diagnostics_Compact'; Target = 'SettingsExportDiagnosticsButton' }
                            [pscustomobject]@{ Action = 'SettingsSection_about_Compact'; Target = 'SettingsViewSourceButton' }
                        )
                    }
                    else {
                        Invoke-SectionMatrix -AppProcessId $appProcessId -Sections @(
                            [pscustomobject]@{ Action = 'SettingsSection_appearance'; Target = 'SettingsThemeSystem'; Interaction = 'invoke' }
                            [pscustomobject]@{ Action = 'SettingsSection_general'; Target = 'SettingsDeveloperModeToggle'; Interaction = 'invoke' }
                            [pscustomobject]@{ Action = 'SettingsSection_privacy'; Target = 'SettingsDiagnosticsToggle'; Interaction = 'invoke' }
                            [pscustomobject]@{ Action = 'SettingsSection_data-cache'; Target = 'SettingsClearQueryCacheButton'; Interaction = 'invoke' }
                            [pscustomobject]@{ Action = 'SettingsSection_diagnostics'; Target = 'SettingsExportDiagnosticsButton'; Interaction = 'invoke' }
                            [pscustomobject]@{ Action = 'SettingsSection_about'; Target = 'SettingsViewSourceButton'; Interaction = 'invoke' }
                        )
                    }
                }
            }
            'stars' {
                if ($routeUsesCompactLayout) {
                    Invoke-Element -AppProcessId $appProcessId -AutomationId 'StarsOpenCategories'
                }
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'StarsNewCategory'
                Invoke-Element -AppProcessId $appProcessId -AutomationId 'StarsNewCategory'
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'StarsCreateCategoryDialog'
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'StarsCategoryColorPicker'
                $captureScreen = $true
            }
            'gists' {
                if ($routeUsesCompactLayout) {
                    Invoke-Element -AppProcessId $appProcessId -AutomationId 'GistsLeadingPaneButton'
                }
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'GistsNew'
            }
            'repo-code' {
                $csvFile = 'RepoCodeTreeItem_path_data_csv_c67a2ff6'
                $svgFile = 'RepoCodeTreeItem_path_architecture_svg_94f839c7'
                $sourceFolder = 'RepoCodeTreeItem_path_src_b8ca8ed2'
                $sourceFile = 'RepoCodeTreeItem_path_src_App_cs_a0c9d202'

                if ($routeUsesCompactLayout) {
                    Wait-ForElement -AppProcessId $appProcessId -AutomationId 'RepoCodeOpenFileTreeButton'
                    Invoke-Element -AppProcessId $appProcessId -AutomationId 'RepoCodeOpenFileTreeButton'
                }
                Wait-ForElement -AppProcessId $appProcessId -AutomationId $csvFile
                Invoke-Element -AppProcessId $appProcessId -AutomationId $csvFile -Interaction 'click'
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'CsvPreviewDataTableRow_0'
                Invoke-Element -AppProcessId $appProcessId -AutomationId 'CsvPreviewDataTableSortColumn_0'
                Invoke-Element -AppProcessId $appProcessId -AutomationId 'CsvPreviewViewMode_Plain' -Interaction 'click'
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'RepoCodeEditor'
                Invoke-Element -AppProcessId $appProcessId -AutomationId 'CsvPreviewViewMode_Rich' -Interaction 'click'
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'CsvPreviewDataTableRow_0'
                Save-RouteEvidence -AppProcessId $appProcessId -RouteName 'repo-code-csv'

                if ($routeUsesCompactLayout) {
                    Invoke-Element -AppProcessId $appProcessId -AutomationId 'RepoCodeOpenFileTreeButton'
                }
                Wait-ForElement -AppProcessId $appProcessId -AutomationId $svgFile
                Invoke-Element -AppProcessId $appProcessId -AutomationId $svgFile -Interaction 'click'
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'SvgPreviewViewport'
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'SvgPreviewRenderedImage'
                Save-RouteEvidence -AppProcessId $appProcessId -RouteName 'repo-code-svg'

                if ($routeUsesCompactLayout) {
                    Invoke-Element -AppProcessId $appProcessId -AutomationId 'RepoCodeOpenFileTreeButton'
                }
                Wait-ForElement -AppProcessId $appProcessId -AutomationId $sourceFolder
                Invoke-Element -AppProcessId $appProcessId -AutomationId $sourceFolder -Interaction 'click'
                Wait-ForElement -AppProcessId $appProcessId -AutomationId $sourceFile
                Invoke-Element -AppProcessId $appProcessId -AutomationId $sourceFile -Interaction 'click'
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'RepoCodeEditor'
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'RepoCodeFindButton'
                Invoke-Element -AppProcessId $appProcessId -AutomationId 'RepoCodeFindButton'
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'RepoCodeFindTextBox'
                Invoke-WinAppCommand -Arguments @(
                    'ui', 'set-value', 'RepoCodeFindTextBox', 'Experience',
                    '-a', $appProcessId.ToString([Globalization.CultureInfo]::InvariantCulture)
                ) | Out-Null
                Wait-ForElementProperty -AppProcessId $appProcessId -AutomationId 'RepoCodeFindStatus' -Property 'Name' -Value '5' -Contains
                Save-RouteEvidence -AppProcessId $appProcessId -RouteName 'repo-code-editor'
            }
            'repo-pull-requests' {
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'RepoPullRequestsDetailTitle'
                if ($routeUsesCompactLayout) {
                    Invoke-CompactSectionMatrix -AppProcessId $appProcessId -PickerAutomationId 'RepoPullRequestsSectionComboBox' -Sections @(
                        [pscustomobject]@{ Action = 'RepoPullRequestsCompactSection_Conversation'; Target = 'RepoPullRequestsCommentsList' }
                        [pscustomobject]@{ Action = 'RepoPullRequestsCompactSection_Files'; Target = 'RepoPullRequestsFileFilter' }
                        [pscustomobject]@{ Action = 'RepoPullRequestsCompactSection_Commits'; Target = 'RepoPullRequestsCommitsList' }
                        [pscustomobject]@{ Action = 'RepoPullRequestsCompactSection_Reviews'; Target = 'RepoPullRequestsReviewsList' }
                        [pscustomobject]@{ Action = 'RepoPullRequestsCompactSection_Timeline'; Target = 'RepoPullRequestsTimelineList' }
                        [pscustomobject]@{ Action = 'RepoPullRequestsCompactSection_Conversation'; Target = 'RepoPullRequestsCommentsList' }
                    )
                }
                else {
                    Invoke-SectionMatrix -AppProcessId $appProcessId -Sections @(
                        [pscustomobject]@{ Action = 'RepoPullRequestsSection_Conversation'; Target = 'RepoPullRequestsCommentsList'; Interaction = 'click'; Scroll = $false }
                        [pscustomobject]@{ Action = 'RepoPullRequestsSection_Files'; Target = 'RepoPullRequestsFileFilter'; Interaction = 'click'; Scroll = $false }
                        [pscustomobject]@{ Action = 'RepoPullRequestsSection_Commits'; Target = 'RepoPullRequestsCommitsList'; Interaction = 'click'; Scroll = $false }
                        [pscustomobject]@{ Action = 'RepoPullRequestsSection_Reviews'; Target = 'RepoPullRequestsReviewsList'; Interaction = 'click'; Scroll = $false }
                        [pscustomobject]@{ Action = 'RepoPullRequestsSection_Timeline'; Target = 'RepoPullRequestsTimelineList'; Interaction = 'click'; Scroll = $false }
                        [pscustomobject]@{ Action = 'RepoPullRequestsSection_Conversation'; Target = 'RepoPullRequestsCommentsList'; Interaction = 'click'; Scroll = $false }
                    )
                }
            }
            'repo-commits' {
                Wait-ForElement -AppProcessId $appProcessId -AutomationId 'RepoCommitsDetailTitle'
                Invoke-SectionMatrix -AppProcessId $appProcessId -Sections @(
                    [pscustomobject]@{ Action = 'RepoCommitsSection_Diff'; Target = 'RepoCommitsDiffFileFilterBox'; Interaction = 'click'; Scroll = $false }
                    [pscustomobject]@{ Action = 'RepoCommitsSection_Comments'; Target = 'RepoCommitsCommentsViewport'; Interaction = 'click'; Scroll = $false }
                    [pscustomobject]@{ Action = 'RepoCommitsSection_Checks'; Target = 'RepoCommitsChecksViewport'; Interaction = 'click'; Scroll = $false }
                    [pscustomobject]@{ Action = 'RepoCommitsSection_Compare'; Target = 'RepoCommitsCompareBaseBox'; Interaction = 'click'; Scroll = $false }
                )
            }
        }

        Save-RouteEvidence -AppProcessId $appProcessId -RouteName $route.Name -CaptureScreen:$captureScreen
        $results.Add([pscustomobject]@{
            Route = $route.Name
            Architecture = $Architecture
            Theme = $visualMode
            Viewport = if ($ViewportWidth -eq 0) { 'default' } else { "$($ViewportWidth)x$($ViewportHeight)" }
            Layout = if ($routeUsesCompactLayout) { 'compact' } else { 'wide' }
            Status = 'PASS'
            DurationMilliseconds = [int]([DateTimeOffset]::UtcNow - $routeStartedAt).TotalMilliseconds
        })
    }
    catch {
        $message = $_.Exception.Message
        $failures.Add("$($route.Name): $message")
        $results.Add([pscustomobject]@{
            Route = $route.Name
            Architecture = $Architecture
            Theme = $visualMode
            Viewport = if ($ViewportWidth -eq 0) { 'default' } else { "$($ViewportWidth)x$($ViewportHeight)" }
            Layout = if ($routeUsesCompactLayout) { 'compact' } else { 'wide' }
            Status = 'FAIL'
            Detail = $message
            DurationMilliseconds = [int]([DateTimeOffset]::UtcNow - $routeStartedAt).TotalMilliseconds
        })
    }
    finally {
        if ($null -ne $launch -and $null -ne $launch.ProcessId) {
            Stop-Process -Id ([int]$launch.ProcessId) -Force -ErrorAction SilentlyContinue
            Wait-Process -Id ([int]$launch.ProcessId) -Timeout 10 -ErrorAction SilentlyContinue
        }
    }
}
}
finally {
    if ($null -ne $highContrastSnapshot) {
        [JitHub.NativeAotVerification.NativeUi]::RestoreHighContrast($highContrastSnapshot)
    }
    [Environment]::SetEnvironmentVariable(
        'JITHUB_AUTOMATION_DATA_ROOT',
        $priorAutomationDataRoot,
        [EnvironmentVariableTarget]::Process)
}

$resultPath = Join-Path $resolvedOutputDirectory "native-aot-ui-matrix-$Architecture-$visualMode.json"
$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $resultPath -Encoding utf8

if ($failures.Count -gt 0) {
    throw "Native AOT UI matrix failed:`n$($failures -join [Environment]::NewLine)"
}

Write-Host "Native AOT UI matrix passed $($results.Count) routes for $Architecture ($visualMode)."
