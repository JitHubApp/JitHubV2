[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$AppPid,
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][long]$AppHwnd,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedAppExePath,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedAppStartTimeUtc,
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$SettingsPid,
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][long]$SettingsHwnd,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedSettingsExePath,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedSettingsStartTimeUtc,
    [string]$ExpectedAppTitle = 'MarkdownRenderer',
    [string]$ExpectedSettingsTitle = 'Settings',
    [string]$ScaleComboAutomationId = 'SystemSettings_Display_Scaling_ItemSizeOverride_ComboBox',
    [string]$OriginalScaleValue = '100% (Recommended)',
    [ValidateSet(96, 120, 144, 168, 192, 216, 288, 384)][int]$OriginalDpi = 96,
    [ValidateRange(640, 1440)][int]$NarrowWidthDip = 760,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\correctness\dpi')
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'The host DPI evidence runner requires PowerShell 7; guest helper compatibility is checked separately.' }

$script:RepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$script:RunId = '{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'), [guid]::NewGuid().ToString('N')
$script:RunDirectory = Join-Path $script:OutputRoot $script:RunId
$script:WorkflowId = "markdown-dpi-$([guid]::NewGuid().ToString('N'))"
$script:Results = [Collections.Generic.List[object]]::new()
$script:Commands = [Collections.Generic.List[object]]::new()
$script:Screenshots = [Collections.Generic.List[string]]::new()
$script:PreviousWorkflowId = $env:WINAPP_UI_WORKFLOW_ID
$script:UiStarted = $false
$script:ScaleMutationAttempted = $false
$script:OriginalBounds = $null
$script:OriginalDpiContext = [IntPtr]::Zero
$script:OriginalTheme = $null
$script:OriginalForcedContrast = $null
$script:OriginalAncestorViewport = $null
$script:OriginalSamplePage = $null
$script:SettingsMonitor = [IntPtr]::Zero
$script:AppMonitor = [IntPtr]::Zero
$script:SettingsContentIdentity = $null

if (-not ('MarkdownCorrectnessDpiNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class MarkdownCorrectnessDpiNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    private struct MonitorInfo { public uint Size; public Rect Monitor; public Rect Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast { public uint Size; public uint Flags; public IntPtr DefaultScheme; }

    [DllImport("user32.dll", SetLastError=true)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError=true)] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", SetLastError=true)] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SystemParametersInfo(uint action, uint parameter, ref HighContrast value, uint flags);
    [DllImport("user32.dll", SetLastError=true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; public Point(int x, int y) { X=x; Y=y; } }
    public static bool IsValidWindow(IntPtr hwnd) { return hwnd != IntPtr.Zero && IsWindow(hwnd); }
    public static bool IsMaximized(IntPtr hwnd) { return IsZoomed(hwnd); }
    public static bool IsMinimized(IntPtr hwnd) { return IsIconic(hwnd); }
    public static int DisplayMonitorCount() { return GetSystemMetrics(80); } // SM_CMONITORS, not virtual desktop dimensions.
    public static uint GetProcessId(IntPtr hwnd) { uint pid; if (GetWindowThreadProcessId(hwnd, out pid) == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); return pid; }
    public static string GetTitle(IntPtr hwnd) { var text = new StringBuilder(1024); GetWindowText(hwnd, text, text.Capacity); return text.ToString(); }
    public static Rect GetBounds(IntPtr hwnd) { Rect r; if (!GetWindowRect(hwnd, out r)) throw new Win32Exception(Marshal.GetLastWin32Error()); return r; }
    public static Rect MoveResize(IntPtr hwnd, int x, int y, int width, int height) { if (!SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, 0x0004 | 0x0010)) throw new Win32Exception(Marshal.GetLastWin32Error()); return GetBounds(hwnd); }
    public static uint GetDpi(IntPtr hwnd) { uint dpi=GetDpiForWindow(hwnd); if (dpi == 0) throw new Win32Exception(Marshal.GetLastWin32Error()); return dpi; }
    public static IntPtr GetMonitor(IntPtr hwnd) { IntPtr h=MonitorFromWindow(hwnd, 2); if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error()); return h; }
    public static IntPtr GetMonitorAt(int x, int y) { IntPtr h=MonitorFromPoint(new Point(x,y), 2); if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error()); return h; }
    public static Rect GetWorkArea(IntPtr monitor) { var i=new MonitorInfo(); i.Size=(uint)Marshal.SizeOf<MonitorInfo>(); if (!GetMonitorInfo(monitor, ref i)) throw new Win32Exception(Marshal.GetLastWin32Error()); return i.Work; }
    public static bool SystemHighContrast() { var v=new HighContrast(); v.Size=(uint)Marshal.SizeOf(typeof(HighContrast)); if (!SystemParametersInfo(0x0042, v.Size, ref v, 0)) throw new Win32Exception(Marshal.GetLastWin32Error()); return (v.Flags & 1) != 0; }
    public static IntPtr EnterPerMonitorV2() { IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4)); if (previous == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error()); return previous; }
    public static void RestoreDpiContext(IntPtr context) { if (context != IntPtr.Zero && SetThreadDpiAwarenessContext(context) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error()); }
}
'@
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Write-JsonFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 16) + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
}

function Get-WindowInfo {
    param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][long]$WindowHandle, [Parameter(Mandatory)][string]$ExpectedExePath, [Parameter(Mandatory)][string]$ExpectedStartTimeUtc, [string]$ExpectedTitle)
    $hwnd = [IntPtr]$WindowHandle
    if (-not [MarkdownCorrectnessDpiNative]::IsValidWindow($hwnd)) { throw "HWND $WindowHandle is not a live window." }
    $actualPid = [MarkdownCorrectnessDpiNative]::GetProcessId($hwnd)
    if ($actualPid -ne $ProcessId) { throw "HWND $WindowHandle belongs to PID $actualPid, not approved PID $ProcessId." }
    $process = Get-Process -Id $ProcessId -ErrorAction Stop
    $actualExePath = [IO.Path]::GetFullPath($process.Path)
    $expectedPath = [IO.Path]::GetFullPath($ExpectedExePath)
    if (-not [string]::Equals($actualExePath, $expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "PID $ProcessId executable '$actualExePath' differs from approved '$expectedPath'."
    }
    $expectedStartTicks=[DateTimeOffset]::Parse($ExpectedStartTimeUtc,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind).UtcDateTime.Ticks
    $actualStartTime=$process.StartTime.ToUniversalTime()
    if ($actualStartTime.Ticks -ne $expectedStartTicks) { throw "PID $ProcessId started at $($actualStartTime.ToString('o')), not approved $ExpectedStartTimeUtc." }
    $title = [MarkdownCorrectnessDpiNative]::GetTitle($hwnd)
    if ($ExpectedTitle -and $title.IndexOf($ExpectedTitle, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "HWND $WindowHandle title '$title' does not contain expected '$ExpectedTitle'."
    }
    return [pscustomobject]@{
        processId=$ProcessId; hwnd=$WindowHandle; processName=$process.ProcessName; exePath=$actualExePath
        exeSha256=(Get-FileHash -LiteralPath $actualExePath -Algorithm SHA256).Hash
        startTime=$actualStartTime.ToString('o'); title=$title
        dpi=[MarkdownCorrectnessDpiNative]::GetDpi($hwnd); monitor=([MarkdownCorrectnessDpiNative]::GetMonitor($hwnd).ToInt64())
    }
}

function Save-CommandEvidence {
    param([string[]]$Arguments, [int]$ExitCode, [string]$Output)
    $safeName = ($Arguments -join ' ') -replace '[^A-Za-z0-9._-]+', '-'
    if ($safeName.Length -gt 80) { $safeName=$safeName.Substring(0,80) }
    $path=Join-Path $script:RunDirectory ('commands\{0:D3}-{1}.txt' -f ($script:Commands.Count+1),$safeName)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
    [IO.File]::WriteAllText($path,"exitCode=$ExitCode`r`n$Output",[Text.UTF8Encoding]::new($false))
    $script:Commands.Add([pscustomobject]@{arguments=@($Arguments);exitCode=$ExitCode;path=$path})
    return $path
}

function Invoke-WinAppChecked {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$Json)
    if (@($Arguments | Where-Object { [string]::IsNullOrEmpty($_) }).Count -gt 0) { throw 'Refusing to send an empty native argument to winapp.' }
    $argv=@($Arguments)
    if ($Json -and $argv -notcontains '--json') { $argv += '--json' }
    # Explicit pipes avoid PowerShell native-redirection races and preserve
    # Unicode in UIA/JSON output. Drain both streams concurrently.
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=(Get-Command winapp -CommandType Application -ErrorAction Stop).Source
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
    foreach($argument in $argv){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::new()
    try {
        $process.StartInfo=$start
        if(-not $process.Start()){throw 'Could not start winapp.'}
        $stdout=$process.StandardOutput.ReadToEndAsync()
        $stderr=$process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $code=$process.ExitCode
        $standardOutput=$stdout.GetAwaiter().GetResult()
        $output=$standardOutput
        $errorOutput=$stderr.GetAwaiter().GetResult()
        if(-not [string]::IsNullOrWhiteSpace($errorOutput)){$output+=[Environment]::NewLine+$errorOutput}
    } finally {$process.Dispose()}
    $evidence=Save-CommandEvidence -Arguments $argv -ExitCode ([int]$code) -Output $output
    if ($null -eq $code -or $code -ne 0) { throw "winapp failed ($code); evidence $evidence. $output" }
    if (-not $Json) { return [pscustomobject]@{output=$output;evidence=$evidence} }
    $match=[regex]::Match($standardOutput,'(?s)(\{|\[)')
    if (-not $match.Success) { throw "winapp returned no JSON; evidence $evidence. $output" }
    try { $value=ConvertFrom-Json -InputObject $standardOutput.Substring($match.Index) }
    catch { throw "winapp returned malformed JSON; evidence $evidence. $_`n$output" }
    return [pscustomobject]@{value=$value;output=$output;evidence=$evidence}
}

function Invoke-AppUi {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$Json)
    $null=Get-WindowInfo -ProcessId $AppPid -WindowHandle $AppHwnd -ExpectedExePath $ExpectedAppExePath -ExpectedStartTimeUtc $ExpectedAppStartTimeUtc -ExpectedTitle $ExpectedAppTitle
    $args=@('ui')+$Arguments+@('--on','local','-w',[string]$AppHwnd)
    return Invoke-WinAppChecked -Arguments $args -Json:$Json
}

function Invoke-SettingsUi {
    param([Parameter(Mandatory)][string[]]$Arguments, [switch]$Json)
    $null=Get-WindowInfo -ProcessId $SettingsPid -WindowHandle $SettingsHwnd -ExpectedExePath $ExpectedSettingsExePath -ExpectedStartTimeUtc $ExpectedSettingsStartTimeUtc -ExpectedTitle $ExpectedSettingsTitle
    $args=@('ui')+$Arguments+@('--on','local','-w',[string]$SettingsHwnd)
    return Invoke-WinAppChecked -Arguments $args -Json:$Json
}

function Get-HwndElement {
    param([Parameter(Mandatory)][long]$WindowHandle)
    $element=[System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$WindowHandle)
    if ($null -eq $element -or $element.Current.NativeWindowHandle -ne [int]$WindowHandle) { throw "UIA root does not match HWND $WindowHandle." }
    return $element
}

function Find-UniqueAutomationId {
    param([Parameter(Mandatory)][System.Windows.Automation.AutomationElement]$Root, [Parameter(Mandatory)][string]$AutomationId)
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$AutomationId)
    # The sample's test-state indicators intentionally live only in Raw view.
    # FindAll otherwise searches Control view even for an AutomationId condition.
    $cache=[System.Windows.Automation.CacheRequest]::new()
    $cache.TreeFilter=[System.Windows.Automation.Automation]::RawViewCondition
    $rawScope=$cache.Activate()
    try { $items=$Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition) }
    finally { $rawScope.Dispose() }
    if ($items.Count -ne 1) { throw "Expected one AutomationId '$AutomationId' below HWND $($Root.Current.NativeWindowHandle); found $($items.Count)." }
    return $items.Item(0)
}

function Get-CurrentToggle {
    param([Parameter(Mandatory)][string]$AutomationId)
    $root=Get-HwndElement -WindowHandle $AppHwnd
    $element=Find-UniqueAutomationId -Root $root -AutomationId $AutomationId
    $pattern=$null
    if (-not $element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern,[ref]$pattern)) { throw "'$AutomationId' lacks TogglePattern." }
    return $pattern.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
}

function Set-AppToggle {
    param([Parameter(Mandatory)][string]$AutomationId, [Parameter(Mandatory)][bool]$Enabled)
    $desired=if($Enabled){'On'}else{'Off'}
    $action=if($Enabled){'toggle-on'}else{'toggle-off'}
    $deadline=[DateTime]::UtcNow.AddSeconds(8)
    do {
        if ((Get-CurrentToggle -AutomationId $AutomationId) -eq $Enabled) { return }
        $null=Invoke-AppUi @('invoke',$AutomationId,'--action',$action)
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "App toggle '$AutomationId' did not reach '$desired'."
}

function Get-ScaleCombo {
    $root=Get-HwndElement -WindowHandle $SettingsHwnd
    $combo=Find-UniqueAutomationId -Root $root -AutomationId $ScaleComboAutomationId
    # UWP Settings can expose its content through SystemSettings.exe below an
    # ApplicationFrameHost window. Do not mistake the frame PID for the combo
    # provider's PID, and never discover percentage items across the desktop.
    $contentPid=[int]$combo.Current.ProcessId
    $contentProcess=Get-Process -Id $contentPid -ErrorAction Stop
    $contentPath=[IO.Path]::GetFullPath($contentProcess.MainModule.FileName)
    $systemSettingsPath=[IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('Windows')) 'ImmersiveControlPanel\SystemSettings.exe'))
    $isApprovedFrame=$contentPid -eq $SettingsPid -and [String]::Equals($contentPath,[IO.Path]::GetFullPath($ExpectedSettingsExePath),[StringComparison]::OrdinalIgnoreCase)
    if (-not $isApprovedFrame -and -not [String]::Equals($contentPath,$systemSettingsPath,[StringComparison]::OrdinalIgnoreCase)) {
        throw "The scale combo belongs to unverified Settings content PID $contentPid ('$contentPath')."
    }
    $contentIdentity=[pscustomobject]@{processId=$contentPid;exePath=$contentPath;startTicks=$contentProcess.StartTime.ToUniversalTime().Ticks}
    if ($null -eq $script:SettingsContentIdentity) {
        $script:SettingsContentIdentity=$contentIdentity
    } elseif ($script:SettingsContentIdentity.processId -ne $contentIdentity.processId -or
        $script:SettingsContentIdentity.startTicks -ne $contentIdentity.startTicks -or
        -not [String]::Equals($script:SettingsContentIdentity.exePath,$contentIdentity.exePath,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'The captured Windows Settings content process changed; refusing to select a display scale.'
    }
    $bounds=$combo.Current.BoundingRectangle
    if ($bounds.IsEmpty -or $bounds.Width -le 0 -or $bounds.Height -le 0) { throw 'The Windows display-scale combo has empty UIA bounds.' }
    $monitor=[MarkdownCorrectnessDpiNative]::GetMonitorAt([int][Math]::Round($bounds.X+$bounds.Width/2),[int][Math]::Round($bounds.Y+$bounds.Height/2))
    if ($monitor -ne $script:SettingsMonitor) { throw 'The Settings scale selector is not on the approved Settings monitor.' }
    return $combo
}

function Get-ScaleValue {
    $result=Invoke-SettingsUi @('get-value',$ScaleComboAutomationId) -Json
    $value=$result.value
    foreach ($name in @('text','value','name')) {
        $property=$value.PSObject.Properties[$name]
        if ($null -ne $property -and $null -ne $property.Value) { return [string]$property.Value }
    }
    throw "Settings scale combo get-value returned no text/value/name: $($result.output)"
}

function Find-ScaleOption {
    param([Parameter(Mandatory)][ValidateSet(100,150,200)][int]$Percent)
    $null=Get-ScaleCombo
    $root=Get-HwndElement -WindowHandle $SettingsHwnd
    $condition=[System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,[int]$script:SettingsContentIdentity.processId),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem))
    $items=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition)
    $found=[Collections.Generic.List[System.Windows.Automation.AutomationElement]]::new()
    for($i=0;$i -lt $items.Count;$i++) {
        $element=$items.Item($i)
        try {
            if ($element.Current.ProcessId -ne $script:SettingsContentIdentity.processId) { continue }
            if ($element.Current.ControlType -ne [System.Windows.Automation.ControlType]::ListItem) { continue }
            if ($element.Current.Name -notmatch "^\s*$Percent%(?:\s+\(Recommended\))?\s*$") { continue }
            $found.Add($element)
        } catch { }
    }
    if ($found.Count -ne 1) { throw "Expected one fresh Settings ListItem named '$Percent%' (or its Recommended form); found $($found.Count)." }
    $item=$found[0]
    $itemPattern=$null
    if (-not $item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern,[ref]$itemPattern)) { throw "Fresh Settings item '$($item.Current.Name)' lacks SelectionItemPattern." }
    $itemBounds=$item.Current.BoundingRectangle
    if ($itemBounds.IsEmpty -or $itemBounds.Width -le 0 -or $itemBounds.Height -le 0) { throw 'The freshly discovered display-scale item has empty bounds.' }
    $itemMonitor=[MarkdownCorrectnessDpiNative]::GetMonitorAt([int][Math]::Round($itemBounds.X+$itemBounds.Width/2),[int][Math]::Round($itemBounds.Y+$itemBounds.Height/2))
    if ($itemMonitor -ne $script:SettingsMonitor) { throw 'The freshly discovered scale item is not on the approved Settings monitor.' }
    return [pscustomobject]@{element=$item;pattern=$itemPattern;name=[string]$item.Current.Name}
}

function Set-DisplayScale {
    param([Parameter(Mandatory)][ValidateSet(100,150,200)][int]$Percent,[switch]$Restoring)
    # The Settings window's monitor is not evidence of the page's selected
    # display on a multi-monitor desktop. Refuse ambiguous targeting, including
    # a topology change during the run, instead of changing another display.
    if ([MarkdownCorrectnessDpiNative]::DisplayMonitorCount() -ne 1) {
        throw 'The bounded DPI runner requires one active desktop monitor; the Settings selected display is not independently mapped on multi-monitor desktops.'
    }
    # During normal execution, both approved windows must still be the captured
    # processes and remain on the same monitor. Cleanup is deliberately allowed
    # to restore Settings even if the app crashed or its HWND was destroyed.
    if (-not $Restoring) {
        $null=Get-WindowInfo -ProcessId $AppPid -WindowHandle $AppHwnd -ExpectedExePath $ExpectedAppExePath -ExpectedStartTimeUtc $ExpectedAppStartTimeUtc -ExpectedTitle $ExpectedAppTitle
    }
    $null=Get-WindowInfo -ProcessId $SettingsPid -WindowHandle $SettingsHwnd -ExpectedExePath $ExpectedSettingsExePath -ExpectedStartTimeUtc $ExpectedSettingsStartTimeUtc -ExpectedTitle $ExpectedSettingsTitle
    if ([MarkdownCorrectnessDpiNative]::GetMonitor([IntPtr]$SettingsHwnd) -ne $script:SettingsMonitor) {
        throw 'The approved Settings window moved off the captured monitor; refusing to change display scale.'
    }
    if (-not $Restoring -and [MarkdownCorrectnessDpiNative]::GetMonitor([IntPtr]$AppHwnd) -ne $script:SettingsMonitor) {
        throw 'The approved app moved off the captured Settings monitor; refusing to change display scale.'
    }
    $script:ScaleMutationAttempted=$true
    $null=Get-ScaleCombo
    $null=Invoke-SettingsUi @('invoke',$ScaleComboAutomationId,'--action','expand')
    $option=Find-ScaleOption -Percent $Percent
    $option.pattern.Select()
    Start-Sleep -Milliseconds 500
    $combo=Get-ScaleCombo
    $expandPattern=$null
    if ($combo.TryGetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern,[ref]$expandPattern) -and $expandPattern.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
        $expandPattern.Collapse()
    }
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    do {
        $actual=Get-ScaleValue
        if ($actual -match "^\s*$Percent%(?:\s+\(Recommended\))?\s*$") { break }
        Start-Sleep -Milliseconds 300
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($actual -notmatch "^\s*$Percent%(?:\s+\(Recommended\))?\s*$") { throw "Windows did not report the requested $Percent% scale; it reports '$actual'." }
    if ($Restoring) {
        # Restore the user's Settings value even if the test app has crashed.
        # App identity and actual app DPI are checked separately by Restore-Scale.
        return [pscustomobject]@{percent=$Percent;settingsValue=$actual;monitor=$script:SettingsMonitor.ToInt64();selectedItem=$option.name}
    }
    $expectedDpi=[int]($Percent*96/100)
    $dpiDeadline=[DateTime]::UtcNow.AddSeconds(15)
    do {
        $dpi=[MarkdownCorrectnessDpiNative]::GetDpi([IntPtr]$AppHwnd)
        if ($dpi -eq $expectedDpi) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $dpiDeadline)
    if ($dpi -ne $expectedDpi) { throw "App HWND reports $dpi DPI at Windows $Percent% scale; expected $expectedDpi." }
    if ([MarkdownCorrectnessDpiNative]::GetMonitor([IntPtr]$AppHwnd) -ne $script:SettingsMonitor) { throw 'The app moved away from the Settings monitor during the scale change.' }
    return [pscustomobject]@{percent=$Percent;settingsValue=$actual;windowDpi=$dpi;monitor=$script:SettingsMonitor.ToInt64();selectedItem=$option.name}
}

function Set-TestWindowBounds {
    param([Parameter(Mandatory)][int]$Dpi)
    $work=[MarkdownCorrectnessDpiNative]::GetWorkArea($script:SettingsMonitor)
    $scale=$Dpi/96.0
    $margin=16
    $availableWidth=$work.Right-$work.Left-(2*$margin)
    $availableHeight=$work.Bottom-$work.Top-(2*$margin)
    $width=[Math]::Min([int][Math]::Round($NarrowWidthDip*$scale),$availableWidth)
    $height=[Math]::Min([int][Math]::Round(860*$scale),$availableHeight)
    if (($width/$scale) -lt $NarrowWidthDip) { throw "Settings monitor work area cannot fit the required $NarrowWidthDip-DIP narrow sample window at $Dpi DPI." }
    if ($width -lt 320 -or $height -lt 320) { throw 'Settings monitor work area is too small for the bounded sample test window.' }
    $rect=[MarkdownCorrectnessDpiNative]::MoveResize([IntPtr]$AppHwnd,$work.Left+$margin,$work.Top+$margin,$width,$height)
    if ($rect.Left -lt $work.Left -or $rect.Top -lt $work.Top -or $rect.Right -gt $work.Right -or $rect.Bottom -gt $work.Bottom) { throw 'The narrow test window escaped the Settings monitor work area.' }
    return [pscustomobject]@{left=$rect.Left;top=$rect.Top;right=$rect.Right;bottom=$rect.Bottom;width=$rect.Right-$rect.Left;height=$rect.Bottom-$rect.Top;widthDip=($rect.Right-$rect.Left)/$scale;heightDip=($rect.Bottom-$rect.Top)/$scale;dpi=$Dpi}
}

function Convert-ManagedTextRectangles {
    param([AllowEmptyCollection()][System.Windows.Rect[]]$Rectangles)
    # The managed UIA wrapper returns Rect[], not the native flat SAFEARRAY.
    foreach($rect in $Rectangles){
        if(-not $rect.IsEmpty -and $rect.Width -gt 0 -and $rect.Height -gt 0){
            [pscustomobject]@{x=$rect.X;y=$rect.Y;width=$rect.Width;height=$rect.Height}
        }
    }
}

function Find-TextRange {
    param([Parameter(Mandatory)][string]$Text)
    $renderer=Find-UniqueAutomationId -Root (Get-HwndElement -WindowHandle $AppHwnd) -AutomationId 'MarkdownRenderer'
    $pattern=$null
    if (-not $renderer.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern,[ref]$pattern)) { throw 'MarkdownRenderer does not expose UIA TextPattern.' }
    $range=$pattern.DocumentRange.FindText($Text,$false,$false)
    if ($null -eq $range) { throw "UIA TextPattern cannot find expected sample text '$Text'." }
    $range.ScrollIntoView($true)
    $target=[pscustomobject]@{renderer=$renderer;pattern=$pattern;range=$range;text=$Text;rect=$null;geometrySamples=@()}
    $deadline=[DateTime]::UtcNow.AddSeconds(5)
    $samples=[Collections.Generic.List[object]]::new()
    $previous=$null;$stable=0
    do {
        $sample=Get-TouchGeometrySnapshot -Target $target
        $samples.Add($sample)
        if ($sample.visible -and $null -ne $previous -and (Test-TouchGeometryStable -Before $previous -After $sample)) { $stable++ } else { $stable=0 }
        if ($stable -ge 4) {
            $target.rect=$sample.rect
            $target.geometrySamples=@($samples.ToArray())
            return $target
        }
        $previous=$sample
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Write-JsonFile (Join-Path $script:RunDirectory 'unsettled-touch-target.json') @($samples.ToArray())
    throw "Expected one visible, stable text rectangle for '$Text' inside the actual scrolling viewport; captured $($samples.Count) geometry samples."
}

function Get-TouchGeometrySnapshot {
    param([Parameter(Mandatory)]$Target)
    $root=Get-HwndElement -WindowHandle $AppHwnd
    $renderer=Find-UniqueAutomationId -Root $root -AutomationId 'MarkdownRenderer'
    $rendererRect=@(Convert-ManagedTextRectangles -Rectangles @($renderer.Current.BoundingRectangle))
    $viewport=$renderer
    if (Get-CurrentToggle 'TouchViewportOwnershipToggle') { $viewport=Find-UniqueAutomationId -Root $root -AutomationId 'TouchSelectionAncestorViewport' }
    $viewportRect=@(Convert-ManagedTextRectangles -Rectangles @($viewport.Current.BoundingRectangle))
    $rects=@(Convert-ManagedTextRectangles -Rectangles $Target.range.GetBoundingRectangles())
    $rect=if($rects.Count -eq 1){$rects[0]}else{$null}
    $clip=if($viewportRect.Count -eq 1){$viewportRect[0]}else{$null}
    $visible=$null -ne $rect -and $null -ne $clip -and -not $renderer.Current.IsOffscreen -and -not $viewport.Current.IsOffscreen
    if($visible){
        $visible=$rect.x -ge $clip.x -and $rect.y -ge $clip.y -and ($rect.x+$rect.width) -le ($clip.x+$clip.width+0.5) -and ($rect.y+$rect.height) -le ($clip.y+$clip.height+0.5)
    }
    $scroll=$null;$scrollState=$null
    if($viewport.TryGetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern,[ref]$scroll)){
        $scrollState=[pscustomobject]@{verticalPercent=$scroll.Current.VerticalScrollPercent;verticalViewSize=$scroll.Current.VerticalViewSize}
    }
    return [pscustomobject]@{timestampUtc=[DateTime]::UtcNow.ToString('o');rect=$rect;rectangles=$rects;renderer=$rendererRect;viewport=$clip;visible=[bool]$visible;scroll=$scrollState}
}

function Test-TouchGeometryStable {
    param([Parameter(Mandatory)]$Before,[Parameter(Mandatory)]$After)
    if(-not $Before.visible -or -not $After.visible){return $false}
    foreach($member in @('rect','viewport')){
        foreach($coordinate in @('x','y','width','height')){
            if([Math]::Abs($Before.$member.$coordinate-$After.$member.$coordinate) -gt 0.5){return $false}
        }
    }
    if(($null -eq $Before.scroll) -ne ($null -eq $After.scroll)){return $false}
    if($null -ne $Before.scroll){
        foreach($member in @('verticalPercent','verticalViewSize')){if([Math]::Abs($Before.scroll.$member-$After.scroll.$member) -gt 0.01){return $false}}
    }
    return $true
}

function Get-SelectionText {
    $renderer=Find-UniqueAutomationId -Root (Get-HwndElement -WindowHandle $AppHwnd) -AutomationId 'MarkdownRenderer'
    $pattern=$null
    if (-not $renderer.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern,[ref]$pattern)) { throw 'MarkdownRenderer does not expose UIA TextPattern.' }
    $ranges=@($pattern.GetSelection())
    return [pscustomobject]@{ranges=$ranges;text=(($ranges | ForEach-Object { $_.GetText(-1) }) -join '')}
}

function Get-SelectionHandles {
    $window=Get-HwndElement -WindowHandle $AppHwnd
    $walker=[System.Windows.Automation.TreeWalker]::RawViewWalker
    $pending=[Collections.Generic.Stack[System.Windows.Automation.AutomationElement]]::new()
    $pending.Push($window)
    $found=[Collections.Generic.List[object]]::new()
    while($pending.Count -gt 0) {
        $element=$pending.Pop()
        try {
            $id=[string]$element.Current.AutomationId
            if ($id -in @('MarkdownSelectionStartHandle','MarkdownSelectionEndHandle')) {
                $r=$element.Current.BoundingRectangle
                $found.Add([pscustomobject]@{automationId=$id;name=$element.Current.Name;x=$r.X;y=$r.Y;width=$r.Width;height=$r.Height;offscreen=$element.Current.IsOffscreen})
            }
            $child=$walker.GetFirstChild($element)
            while($null -ne $child) { $pending.Push($child);$child=$walker.GetNextSibling($child) }
        } catch { }
    }
    $start=@($found | Where-Object automationId -eq 'MarkdownSelectionStartHandle')
    $end=@($found | Where-Object automationId -eq 'MarkdownSelectionEndHandle')
    if ($start.Count -ne 1 -or $end.Count -ne 1) { throw "Expected exactly two selection handles (start=$($start.Count), end=$($end.Count))." }
    return @($start[0],$end[0])
}

function Wait-ForExactSelection {
    param([Parameter(Mandatory)][string]$Expected)
    $deadline=[DateTime]::UtcNow.AddSeconds(5)
    do {
        $selection=Get-SelectionText
        if ($selection.ranges.Count -eq 1 -and $selection.text -ceq $Expected) { return $selection }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Touch selection does not exactly equal '$Expected'; actual='$($selection.text)' ranges=$($selection.ranges.Count)."
}

function Assert-TestSelectionCleared {
    $deadline=[DateTime]::UtcNow.AddSeconds(3)
    do {
        $null=Get-WindowInfo -ProcessId $AppPid -WindowHandle $AppHwnd -ExpectedExePath $ExpectedAppExePath -ExpectedStartTimeUtc $ExpectedAppStartTimeUtc -ExpectedTitle $ExpectedAppTitle
        $selection=Get-SelectionText
        $root=Get-HwndElement -WindowHandle $AppHwnd
        # WinUI flyouts are separate PopupHost HWNDs. Include only this exact
        # app process, not unrelated desktop menus, when verifying cleanup.
        $processCondition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$AppPid)
        $roots=@($root)+@([System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$processCondition))
        $ids=@('MarkdownSelectionStartHandle','MarkdownSelectionEndHandle','MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll')
        $visible=0
        foreach($id in $ids){
            $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
            foreach($appRoot in $roots){
                $items=$appRoot.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition)
                foreach($item in $items){if(-not $item.Current.IsOffscreen){$visible++}}
            }
        }
        if($selection.text.Length -eq 0 -and $visible -eq 0){return}
        Start-Sleep -Milliseconds 100
    }while([DateTime]::UtcNow -lt $deadline)
    throw "Test selection/menu cleanup did not complete: selected text='$($selection.text)', visible handles/menu commands=$visible."
}

function Assert-SelectionGeometry {
    param([Parameter(Mandatory)]$Target,[Parameter(Mandatory)][int]$Dpi)
    $deadline=[DateTime]::UtcNow.AddSeconds(3)
    $handles=$null
    $lastError=$null
    do {
        try { $handles=@(Get-SelectionHandles); break }
        catch { $lastError=$_;Start-Sleep -Milliseconds 100 }
    } while([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $handles -or $handles.Count -ne 2) { throw "Selection handle UIA exposure did not stabilize: $lastError" }
    $scale=$Dpi/96.0
    foreach($handle in $handles) {
        if ($handle.offscreen -or $handle.width -le 0 -or $handle.height -le 0) { throw "Selection handle '$($handle.automationId)' is not visibly exposed." }
        if (($handle.width/$scale) -lt 44 -or ($handle.height/$scale) -lt 44) { throw "Selection handle '$($handle.automationId)' is smaller than 44 DIPs ($($handle.width/$scale) × $($handle.height/$scale))." }
    }
    $left=$Target.rect.x; $right=$Target.rect.x+$Target.rect.width; $midY=$Target.rect.y+$Target.rect.height/2
    $distance = { param($h,$x) [Math]::Sqrt([Math]::Pow(($h.x+$h.width/2)-$x,2)+[Math]::Pow(($h.y+$h.height/2)-$midY,2))/$scale }
    $startLeft=&$distance $handles[0] $left; $endRight=&$distance $handles[1] $right
    $startRight=&$distance $handles[0] $right; $endLeft=&$distance $handles[1] $left
    $best=[Math]::Min([Math]::Max($startLeft,$endRight),[Math]::Max($startRight,$endLeft))
    if ($best -gt 48) { throw "Selection handle centers are $best DIPs from the selected range endpoints (limit 48)." }
    return [pscustomobject]@{handles=$handles;endpointMaxDistanceDip=$best;minimumHitSizeDip=44;range=$Target.rect}
}

function Wait-RawName {
    param([Parameter(Mandatory)][string]$AutomationId,[Parameter(Mandatory)][string]$Expected)
    $deadline=[DateTime]::UtcNow.AddSeconds(8)
    do {
        $element=Find-UniqueAutomationId -Root (Get-HwndElement -WindowHandle $AppHwnd) -AutomationId $AutomationId
        if ([string]$element.Current.Name -eq $Expected) { return }
        Start-Sleep -Milliseconds 100
    } while([DateTime]::UtcNow -lt $deadline)
    throw "Raw UIA status '$AutomationId' did not become '$Expected'."
}

function Set-ViewportMode {
    param([Parameter(Mandatory)][ValidateSet('owned','ancestor')][string]$Mode)
    # A preceding long press leaves a modal MenuFlyout open. Dismiss it and
    # the prior range before querying or toggling controls behind that popup.
    Clear-TouchTestSelection
    $isAncestor=($Mode -eq 'ancestor')
    Set-AppToggle -AutomationId 'TouchViewportOwnershipToggle' -Enabled $isAncestor
    if ($isAncestor) {
        $present=$null
        try { $present=Find-UniqueAutomationId -Root (Get-HwndElement -WindowHandle $AppHwnd) -AutomationId 'TouchSelectionAncestorViewport' } catch { }
        if ($null -eq $present) { throw 'Ancestor-owned viewport toggle did not create TouchSelectionAncestorViewport.' }
    } else {
        $matches=@()
        $root=Get-HwndElement -WindowHandle $AppHwnd
        $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'TouchSelectionAncestorViewport')
        $matches=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition)
        if ($matches.Count -ne 0) { throw 'Owned viewport mode still exposes the ancestor ScrollViewer.' }
    }
}

function Set-TestAppearance {
    param([Parameter(Mandatory)][ValidateSet('light','dark','forced-hc-preview')][string]$Appearance)
    Clear-TouchTestSelection
    if ($Appearance -eq 'light') { Set-AppToggle -AutomationId 'ThemeToggle' -Enabled $false; Set-AppToggle -AutomationId 'ForcedHighContrastToggle' -Enabled $false; Wait-RawName 'ThemeStatus' 'theme:light'; Wait-RawName 'HighContrastStatus' 'hc:off' }
    elseif ($Appearance -eq 'dark') { Set-AppToggle -AutomationId 'ForcedHighContrastToggle' -Enabled $false; Set-AppToggle -AutomationId 'ThemeToggle' -Enabled $true; Wait-RawName 'ThemeStatus' 'theme:dark'; Wait-RawName 'HighContrastStatus' 'hc:off' }
    else { Set-AppToggle -AutomationId 'ThemeToggle' -Enabled $false; Set-AppToggle -AutomationId 'ForcedHighContrastToggle' -Enabled $true; Wait-RawName 'ThemeStatus' 'theme:high-contrast'; Wait-RawName 'HighContrastStatus' 'hc:on;fg:#FFFFFF;bg:#000000;hotlight:#00FFFF;highlight:#FFFF00;highlightText:#000000' }
    if ([MarkdownCorrectnessDpiNative]::SystemHighContrast()) { throw 'Windows High Contrast became active; forced-HC preview cannot be mislabeled as an OS High Contrast run.' }
}

function Navigate-ToTouchSample {
    $null=Invoke-AppUi @('invoke','SampleNav_TouchSelection','--action','select')
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    do {
        $page=Find-UniqueAutomationId -Root (Get-HwndElement -WindowHandle $AppHwnd) -AutomationId 'CurrentSamplePage'
        if ([string]$page.Current.Name -eq 'page:TouchSelection') { break }
        Start-Sleep -Milliseconds 100
    } while([DateTime]::UtcNow -lt $deadline)
    if ([string]$page.Current.Name -ne 'page:TouchSelection') { throw 'Sample navigation did not reach TouchSelection.' }
    $renderer=Find-UniqueAutomationId -Root (Get-HwndElement -WindowHandle $AppHwnd) -AutomationId 'MarkdownRenderer'
    $pattern=$null
    if (-not $renderer.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern,[ref]$pattern)) { throw 'TouchSelection renderer has no UIA TextPattern.' }
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    do {
        $range=$pattern.DocumentRange.FindText('Touch selection',$false,$false)
        if ($null -ne $range) { break }
        Start-Sleep -Milliseconds 100
    } while([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $range) { throw 'TouchSelection sample did not publish its document text.' }
    Wait-RawName -AutomationId 'CurrentSamplePage' -Expected 'page:TouchSelection'
}

function Save-TestScreenshot {
    param([Parameter(Mandatory)][string]$Name)
    $path=Join-Path $script:RunDirectory $Name
    if (Test-Path -LiteralPath $path) { throw "Refusing to overwrite DPI evidence '$path'." }
    $result=Invoke-AppUi @('screenshot','--output',$path) -Json
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -le 0) { throw "winapp did not deliver screenshot '$path'." }
    $script:Screenshots.Add($path)
    return [pscustomobject]@{path=$path;pixels=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash;winapp=$result.value}
}

function Clear-TouchTestSelection {
    # A prior handle can overlap the next text target. Dismiss the flyout,
    # then clear the range on the document before injecting a fresh gesture.
    $null=Invoke-AppUi @('send-keys','Escape','--via','send-input')
    $null=Invoke-AppUi @('send-keys','Escape','--target','MarkdownRenderer','--via','send-input')
    Assert-TestSelectionCleared
}

function Invoke-TouchSelectionCheck {
    param([Parameter(Mandatory)][ValidateSet('family','👩🏽‍💻')][string]$TargetText,[Parameter(Mandatory)][ValidateSet('long-press','double-tap')][string]$Gesture,[Parameter(Mandatory)][int]$Dpi,[Parameter(Mandatory)][string]$ScreenshotName)
    Clear-TouchTestSelection
    # The user may have just changed scaling in Settings. Focus a real native
    # sample control and require CLI foreground verification before scrolling
    # and sampling the renderer's target; focusing a virtual text child can
    # itself bring different document content into view.
    $null=Invoke-AppUi @('focus','MarkdownEditor')
    $target=Find-TextRange -Text $TargetText
    $beforeShot=Save-TestScreenshot -Name ("target-"+$ScreenshotName)
    # Capture and recheck after the screenshot as well: bringing an ancestor
    # viewport into view is asynchronous, and CLI startup adds another delay.
    $ready=Get-TouchGeometrySnapshot -Target $target
    if(-not(Test-TouchGeometryStable -Before $target.geometrySamples[-1] -After $ready)){throw 'The text target moved after its preparation capture; no touch was injected.'}
    $target.rect=$ready.rect
    $bounds=$target.rect
    $rendererBounds=(Find-UniqueAutomationId -Root (Get-HwndElement -WindowHandle $AppHwnd) -AutomationId 'MarkdownRenderer').Current.BoundingRectangle
    if ($bounds.x -lt $rendererBounds.X -or $bounds.y -lt $rendererBounds.Y -or ($bounds.x+$bounds.width) -gt ($rendererBounds.X+$rendererBounds.Width+2) -or ($bounds.y+$bounds.height) -gt ($rendererBounds.Y+$rendererBounds.Height+2)) {
        throw "Target '$TargetText' is outside renderer bounds after TextPatternRange.ScrollIntoView."
    }
    $x=[int][Math]::Round($bounds.x+$bounds.width/2); $y=[int][Math]::Round($bounds.y+$bounds.height/2)
    $touchArgs=@('touch','MarkdownRenderer','--gesture',$Gesture,'--at',"$x,$y")
    if ($Gesture -eq 'long-press') { $touchArgs+=@('--hold-ms','650') }
    $pointDiagnostic=$null
    try{
        $pointRange=$target.pattern.RangeFromPoint([System.Windows.Point]::new($x,$y))
        $pointRange.ExpandToEnclosingUnit([System.Windows.Automation.Text.TextUnit]::Word)
        $pointDiagnostic=[pscustomobject]@{status='RETURNED';word=$pointRange.GetText(-1)}
    }catch{
        # This additional diagnostic is not the gesture oracle. Preserve the
        # failure for investigation; selection text and geometry still must pass.
        $pointDiagnostic=[pscustomobject]@{status='FAILED';error=$_.Exception.ToString()}
    }
    $preflight=[pscustomobject]@{target=$TargetText;gesture=$Gesture;point=@{x=$x;y=$y};pointDiagnostic=$pointDiagnostic;geometry=$ready;samples=$target.geometrySamples;screenshot=$beforeShot}
    Write-JsonFile (Join-Path $script:RunDirectory ("target-"+[IO.Path]::GetFileNameWithoutExtension($ScreenshotName)+'.json')) $preflight
    try{
        $null=Invoke-AppUi $touchArgs
        $selection=Wait-ForExactSelection -Expected $TargetText
        $geometry=Assert-SelectionGeometry -Target $target -Dpi $Dpi
        $shot=Save-TestScreenshot -Name $ScreenshotName
    }catch{
        $originalError=$_
        $failureShot=Save-TestScreenshot -Name ("failure-"+$ScreenshotName)
        Write-JsonFile (Join-Path $script:RunDirectory ("failure-"+[IO.Path]::GetFileNameWithoutExtension($ScreenshotName)+'.json')) ([pscustomobject]@{preflight=$preflight;after=Get-TouchGeometrySnapshot -Target $target;selection=(Get-SelectionText).text;screenshot=$failureShot;error=$originalError.Exception.ToString()})
        throw $originalError
    }
    return [pscustomobject]@{
        expectedText=$TargetText;actualText=$selection.text;gesture=$Gesture;point=@{x=$x;y=$y};textRect=$bounds
        geometry=$geometry;screenshot=$shot;preflight=$preflight;dpi=$Dpi;actualDpi=[MarkdownCorrectnessDpiNative]::GetDpi([IntPtr]$AppHwnd)
    }
}

function Get-SourceFingerprint {
    $paths=@(
        (Join-Path $script:RepoRoot 'MarkdownRenderer\MarkdownRenderer.Sample\Samples\SampleDocuments.Interaction.cs'),
        (Join-Path $script:RepoRoot 'MarkdownRenderer\MarkdownRenderer.Sample\MainWindow.xaml.cs'),
        (Join-Path $script:RepoRoot 'MarkdownRenderer\MarkdownRenderer.Sample\TouchContactObservation.cs'),
        (Join-Path $script:RepoRoot 'MarkdownRenderer\MarkdownRenderer\Controls\MarkdownRendererControl.PointerInput.cs'),
        (Join-Path $script:RepoRoot 'MarkdownRenderer\MarkdownRenderer\Controls\MarkdownTouchSelectionSession.cs'),
        (Join-Path $script:RepoRoot 'MarkdownRenderer\MarkdownRenderer\Controls\MarkdownRendererControl.cs'),
        (Join-Path $script:RepoRoot 'MarkdownRenderer\MarkdownRenderer\Accessibility\MarkdownAutomationPeer.cs'),
        (Join-Path $script:RepoRoot 'MarkdownRenderer\MarkdownRenderer\Controls\TouchTapSuppressionPolicy.cs'),
        (Join-Path $script:RepoRoot 'MarkdownRenderer\MarkdownRenderer\Controls\DeferredTouchGestureOwnerResolver.cs'),
        (Join-Path $script:RepoRoot 'MarkdownRenderer\MarkdownRenderer\Layout\TextBoundaryHelper.cs'),
        (Join-Path $script:RepoRoot 'MarkdownRenderer\MarkdownRenderer\Layout\Boxes\InlineContainerBox.cs'),
        (Join-Path $script:RepoRoot 'MarkdownRenderer\MarkdownRenderer\Layout\Boxes\ScriptBaselineGeometry.cs'),
        (Join-Path $script:RepoRoot 'MarkdownRenderer\MarkdownRenderer\Layout\Boxes\ScriptBaselineTextRenderer.cs'),
        (Join-Path $script:RepoRoot 'MarkdownRenderer\MarkdownRenderer\Layout\Boxes\ScriptBaselineNativeColorGlyphRenderer.cs'),
        (Join-Path $script:RepoRoot 'MarkdownRenderer\eng\Invoke-MarkdownCorrectnessDpiTests.ps1')
    )
    $fixture=Get-Content -LiteralPath $paths[0] -Raw
    foreach($literal in @('TouchSelectionSample','👩🏽‍💻 family 👨‍👩‍👧‍👦')) {
    if ($fixture.IndexOf($literal,[StringComparison]::Ordinal) -lt 0) { throw "The live DPI fixture source is missing expected selectable token '$literal'." }
    }
    $files=foreach($path in $paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Source evidence file missing: $path" }
        [pscustomobject]@{path=[IO.Path]::GetFullPath($path);sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
    }
    $rev=(& git -C $script:RepoRoot rev-parse HEAD 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $rev -notmatch '^[0-9a-fA-F]{40}$') { throw 'Could not capture a full git source revision.' }
    $status=(& git -C $script:RepoRoot status --short 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not capture worktree status.' }
    $diff=(& git -C $script:RepoRoot diff --binary HEAD 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw 'Could not capture the uncommitted tracked diff.' }
    $diffBytes=[Text.UTF8Encoding]::new($false).GetBytes($diff)
    $diffSha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($diffBytes))
    $untracked=@(& git -C $script:RepoRoot ls-files --others --exclude-standard 2>&1)
    if ($LASTEXITCODE -ne 0) { throw 'Could not capture untracked source identities.' }
    $untrackedFiles=foreach($relative in $untracked) {
        $path=Join-Path $script:RepoRoot $relative
        if (Test-Path -LiteralPath $path -PathType Leaf) { [pscustomobject]@{path=$relative;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash} }
    }
    return [pscustomobject]@{revision=$rev;worktreeStatus=$status;trackedDiffSha256=$diffSha;files=@($files);untrackedFiles=@($untrackedFiles)}
}

function Restore-Scale {
    $percent=[int]([regex]::Match($OriginalScaleValue,'\d+').Value)
    if ($percent -notin @(100,150,200)) { throw "Original Windows scale '$OriginalScaleValue' is not in the validated 100/150/200 set." }
    $restore=Set-DisplayScale -Percent $percent -Restoring
    $actual=Get-ScaleValue
    if ($actual -cne $OriginalScaleValue) { throw "Windows scale restore reports '$actual', expected exact '$OriginalScaleValue'." }
    $appVerified=$false
    $appDpi=$null
    $appVerificationError=$null
    try {
        $deadline=[DateTime]::UtcNow.AddSeconds(15)
        do {
            $appInfo=Get-WindowInfo -ProcessId $AppPid -WindowHandle $AppHwnd -ExpectedExePath $ExpectedAppExePath -ExpectedStartTimeUtc $ExpectedAppStartTimeUtc -ExpectedTitle $ExpectedAppTitle
            if ($appInfo.dpi -eq $OriginalDpi) { break }
            Start-Sleep -Milliseconds 250
        } while ([DateTime]::UtcNow -lt $deadline)
        $appDpi=$appInfo.dpi
        if ($appDpi -ne $OriginalDpi) { throw "Restored app DPI is $appDpi, not captured $OriginalDpi DPI." }
        $appVerified=$true
    } catch { $appVerificationError=$_.Exception.Message }
    return [pscustomobject]@{settingsRestore=$restore;actualScale=$actual;appWindowVerified=$appVerified;appDpi=$appDpi;appVerificationError=$appVerificationError}
}

try {
    [IO.Directory]::CreateDirectory($script:RunDirectory) | Out-Null
    $script:OriginalDpiContext=[MarkdownCorrectnessDpiNative]::EnterPerMonitorV2()
    $appInfo=Get-WindowInfo -ProcessId $AppPid -WindowHandle $AppHwnd -ExpectedExePath $ExpectedAppExePath -ExpectedStartTimeUtc $ExpectedAppStartTimeUtc -ExpectedTitle $ExpectedAppTitle
    $settingsInfo=Get-WindowInfo -ProcessId $SettingsPid -WindowHandle $SettingsHwnd -ExpectedExePath $ExpectedSettingsExePath -ExpectedStartTimeUtc $ExpectedSettingsStartTimeUtc -ExpectedTitle $ExpectedSettingsTitle
    $script:AppMonitor=[IntPtr]$appInfo.monitor
    $script:SettingsMonitor=[IntPtr]$settingsInfo.monitor
    if ([MarkdownCorrectnessDpiNative]::DisplayMonitorCount() -ne 1) { throw 'This bounded DPI run cannot safely identify the Settings selected display on a multi-monitor desktop. No scale change was attempted.' }
    if ($script:AppMonitor -ne $script:SettingsMonitor) { throw 'App and Windows Settings are on different monitors. Refusing to change display scaling; move only the test app onto the Settings monitor and retry.' }
    if ($appInfo.dpi -ne $OriginalDpi) { throw "App starts at $($appInfo.dpi) DPI, not the recorded original $OriginalDpi DPI." }
    if ([MarkdownCorrectnessDpiNative]::SystemHighContrast()) { throw 'OS High Contrast is active. This run must keep it off and label forced app colors only as preview.' }
    $script:OriginalBounds=[MarkdownCorrectnessDpiNative]::GetBounds([IntPtr]$AppHwnd)
    if ([MarkdownCorrectnessDpiNative]::IsMaximized([IntPtr]$AppHwnd) -or [MarkdownCorrectnessDpiNative]::IsMinimized([IntPtr]$AppHwnd)) {
        throw 'The test app must start in its normal window state so the script can restore its exact original bounds without changing maximize/minimize state.'
    }
    $initialScale=Get-ScaleValue
    if ($initialScale -cne $OriginalScaleValue) { throw "Windows currently reports scale '$initialScale', not approved original '$OriginalScaleValue'." }
    $null=Get-ScaleCombo
    $script:OriginalTheme=Get-CurrentToggle 'ThemeToggle'
    $script:OriginalForcedContrast=Get-CurrentToggle 'ForcedHighContrastToggle'
    $samplePage=Find-UniqueAutomationId -Root (Get-HwndElement -WindowHandle $AppHwnd) -AutomationId 'CurrentSamplePage'
    $script:OriginalSamplePage=[string]$samplePage.Current.Name
    $source=Get-SourceFingerprint
    $exeIdentity=[pscustomobject]@{app=$appInfo;settings=$settingsInfo;settingsContent=$script:SettingsContentIdentity}
    $env:WINAPP_UI_WORKFLOW_ID=$script:WorkflowId
    $script:UiStarted=$true
    Navigate-ToTouchSample
    $script:OriginalAncestorViewport=Get-CurrentToggle 'TouchViewportOwnershipToggle'
    $runMetadata=[ordered]@{
        runId=$script:RunId;objective='Phase 1 correctness: actual Windows display DPI, touch selection, handle hit geometry, narrow reading layout, viewport parity and app appearance preview.'
        target='local physical desktop';workflowId=$script:WorkflowId;source=$source;build=$exeIdentity
        approvedOriginal=@{scale=$OriginalScaleValue;dpi=$OriginalDpi;appBounds=@{left=$script:OriginalBounds.Left;top=$script:OriginalBounds.Top;right=$script:OriginalBounds.Right;bottom=$script:OriginalBounds.Bottom}}
        monitor=@{settings=$script:SettingsMonitor.ToInt64();app=$script:AppMonitor.ToInt64();activeDesktopMonitorCount=([MarkdownCorrectnessDpiNative]::DisplayMonitorCount());workArea=([MarkdownCorrectnessDpiNative]::GetWorkArea($script:SettingsMonitor))}
        operatingSystemHighContrast=$false;scales=@(100,150,200);viewportModes=@('owned','ancestor');appearances=@('light','dark','forced-hc-preview');screenshots=@()
        checks=@();unverified=@('Windows OS High Contrast visual mode: deliberately not enabled; forced app HC is preview only.','Human visual inspection of each PNG remains required.')
    }
    Write-JsonFile (Join-Path $script:RunDirectory 'run-metadata.json') $runMetadata

    # The display scale is changed only through the live Windows Settings UIA SelectionItemPattern.
    foreach($percent in @(100,150,200)) {
        $scaleReceipt=Set-DisplayScale -Percent $percent
        if ($percent -eq 150 -and $scaleReceipt.windowDpi -ne 144) { throw 'The actual 150% run did not reach GetDpiForWindow=144.' }
        if ($percent -eq 200 -and $scaleReceipt.windowDpi -ne 192) { throw 'The actual 200% run did not reach GetDpiForWindow=192.' }
        $boundsReceipt=Set-TestWindowBounds -Dpi $scaleReceipt.windowDpi
        Navigate-ToTouchSample
        Set-TestAppearance -Appearance 'light'
        foreach($mode in @('owned','ancestor')) {
            Set-ViewportMode -Mode $mode
            $gesture=if($mode -eq 'owned'){'long-press'}else{'double-tap'}
            $name="scale-$percent-light-$mode.png"
            $check=Invoke-TouchSelectionCheck -TargetText 'family' -Gesture $gesture -Dpi $scaleReceipt.windowDpi -ScreenshotName $name
            $script:Results.Add([pscustomobject]@{id="dpi-$percent-$mode-light";status='PASS';scale=$scaleReceipt;window=$boundsReceipt;viewport=$mode;appearance='light';selection=$check})
        }
        if ($percent -eq 150) {
            foreach($mode in @('owned','ancestor')) {
                Set-ViewportMode -Mode $mode
                Set-TestAppearance -Appearance 'dark'
                $check=Invoke-TouchSelectionCheck -TargetText 'family' -Gesture 'long-press' -Dpi $scaleReceipt.windowDpi -ScreenshotName "scale-150-dark-$mode.png"
                $script:Results.Add([pscustomobject]@{id="dpi-150-$mode-dark";status='PASS';scale=$scaleReceipt;window=$boundsReceipt;viewport=$mode;appearance='dark';selection=$check})
                Set-TestAppearance -Appearance 'forced-hc-preview'
                $check=Invoke-TouchSelectionCheck -TargetText 'family' -Gesture 'long-press' -Dpi $scaleReceipt.windowDpi -ScreenshotName "scale-150-forced-hc-preview-$mode.png"
                $script:Results.Add([pscustomobject]@{id="dpi-150-$mode-forced-hc-preview";status='PASS';scale=$scaleReceipt;window=$boundsReceipt;viewport=$mode;appearance='forced-hc-preview-os-hc-off';selection=$check})
                Set-TestAppearance -Appearance 'light'
            }
            Set-ViewportMode -Mode 'owned'
            $emoji=Invoke-TouchSelectionCheck -TargetText '👩🏽‍💻' -Gesture 'long-press' -Dpi $scaleReceipt.windowDpi -ScreenshotName 'scale-150-light-owned-emoji.png'
            $script:Results.Add([pscustomobject]@{id='dpi-150-owned-light-emoji-grapheme';status='PASS';scale=$scaleReceipt;window=$boundsReceipt;viewport='owned';appearance='light';selection=$emoji})
        }
    }
}
catch {
    $script:Results.Add([pscustomobject]@{id='HARNESS';status='FAIL';detail=$_.Exception.ToString();timestampUtc=[DateTime]::UtcNow.ToString('o')})
}
finally {
    # Restoration is unconditional once a physical scale change is attempted.
    if ($script:ScaleMutationAttempted) {
        try {
            $restoreReceipt=Restore-Scale
            $script:Results.Add([pscustomobject]@{id='RESTORE-SCALE';status='PASS';receipt=$restoreReceipt;actualScale=Get-ScaleValue;actualDpi=$restoreReceipt.appDpi})
        } catch { $script:Results.Add([pscustomobject]@{id='RESTORE-SCALE';status='FAIL';detail=$_.Exception.ToString()}) }
    }
    if ($script:UiStarted) {
        foreach($restoreAction in @(
            @{id='RESTORE-THEME';script={if($null -ne $script:OriginalTheme){Set-AppToggle -AutomationId 'ThemeToggle' -Enabled ([bool]$script:OriginalTheme)}}},
            @{id='RESTORE-FORCED-CONTRAST-PREVIEW';script={if($null -ne $script:OriginalForcedContrast){Set-AppToggle -AutomationId 'ForcedHighContrastToggle' -Enabled ([bool]$script:OriginalForcedContrast)}}},
            @{id='RESTORE-VIEWPORT-OWNERSHIP';script={if($null -ne $script:OriginalAncestorViewport){Set-AppToggle -AutomationId 'TouchViewportOwnershipToggle' -Enabled ([bool]$script:OriginalAncestorViewport)}}},
            @{id='RESTORE-SAMPLE-PAGE';script={if($script:OriginalSamplePage -match '^page:(?<page>[A-Za-z0-9]+)$'){$null=Invoke-AppUi @('invoke',"SampleNav_$($Matches.page)",'--action','select');Wait-RawName -AutomationId 'CurrentSamplePage' -Expected $script:OriginalSamplePage}}},
            @{id='CLEAR-TEST-SELECTION';script={$null=Invoke-AppUi @('send-keys','Escape','--via','send-input');Assert-TestSelectionCleared}}
        )) {
            try { & $restoreAction.script; $script:Results.Add([pscustomobject]@{id=$restoreAction.id;status='PASS'}) }
            catch { $script:Results.Add([pscustomobject]@{id=$restoreAction.id;status='FAIL';detail=$_.Exception.ToString()}) }
        }
        try { $null=Invoke-WinAppChecked @('ui','yield','--on','local') }
        catch { $script:Results.Add([pscustomobject]@{id='YIELD-UI-WORKFLOW';status='FAIL';detail=$_.Exception.ToString()}) }
    }
    if ($null -ne $script:OriginalBounds -and [MarkdownCorrectnessDpiNative]::IsValidWindow([IntPtr]$AppHwnd)) {
        try {
            $null=Get-WindowInfo -ProcessId $AppPid -WindowHandle $AppHwnd -ExpectedExePath $ExpectedAppExePath -ExpectedStartTimeUtc $ExpectedAppStartTimeUtc -ExpectedTitle $ExpectedAppTitle
            $restored=[MarkdownCorrectnessDpiNative]::MoveResize([IntPtr]$AppHwnd,$script:OriginalBounds.Left,$script:OriginalBounds.Top,$script:OriginalBounds.Right-$script:OriginalBounds.Left,$script:OriginalBounds.Bottom-$script:OriginalBounds.Top)
            if ($restored.Left -ne $script:OriginalBounds.Left -or $restored.Top -ne $script:OriginalBounds.Top -or $restored.Right -ne $script:OriginalBounds.Right -or $restored.Bottom -ne $script:OriginalBounds.Bottom) { throw 'Test app window bounds did not return exactly to the captured rectangle.' }
            $script:Results.Add([pscustomobject]@{id='RESTORE-APP-BOUNDS';status='PASS';bounds=@{left=$restored.Left;top=$restored.Top;right=$restored.Right;bottom=$restored.Bottom}})
        } catch { $script:Results.Add([pscustomobject]@{id='RESTORE-APP-BOUNDS';status='FAIL';detail=$_.Exception.ToString()}) }
    }
    if ($script:OriginalDpiContext -ne [IntPtr]::Zero) {
        try { [MarkdownCorrectnessDpiNative]::RestoreDpiContext($script:OriginalDpiContext) }
        catch { $script:Results.Add([pscustomobject]@{id='RESTORE-THREAD-DPI-CONTEXT';status='FAIL';detail=$_.Exception.ToString()}) }
    }
    $env:WINAPP_UI_WORKFLOW_ID=$script:PreviousWorkflowId
}

$failed=@($script:Results | Where-Object status -eq 'FAIL').Count
$passed=@($script:Results | Where-Object status -eq 'PASS').Count
$interactionPasses=@($script:Results | Where-Object { $_.status -eq 'PASS' -and $_.id -like 'dpi-*' }).Count
$expectedInteractionIds=[Collections.Generic.List[string]]::new()
foreach($percent in @(100,150,200)) { foreach($mode in @('owned','ancestor')) { $expectedInteractionIds.Add("dpi-$percent-$mode-light") } }
foreach($mode in @('owned','ancestor')) { $expectedInteractionIds.Add("dpi-150-$mode-dark");$expectedInteractionIds.Add("dpi-150-$mode-forced-hc-preview") }
$expectedInteractionIds.Add('dpi-150-owned-light-emoji-grapheme')
$actualInteractionIds=@($script:Results | Where-Object { $_.status -eq 'PASS' -and $_.id -like 'dpi-*' } | ForEach-Object { [string]$_.id })
$missingInteractions=@($expectedInteractionIds | Where-Object { $_ -notin $actualInteractionIds })
$restoreFailures=@($script:Results | Where-Object { $_.status -eq 'FAIL' -and $_.id -like 'RESTORE-*' }).Count
$finalScale=$(try{Get-ScaleValue}catch{$null})
$finalDpi=$(try{[MarkdownCorrectnessDpiNative]::GetDpi([IntPtr]$AppHwnd)}catch{$null})
$operatingSystemHighContrast=$(try{[MarkdownCorrectnessDpiNative]::SystemHighContrast()}catch{$null})
$report=[ordered]@{
    schemaVersion=1;objective='Phase 1 actual display-DPI correctness';runId=$script:RunId;target='local physical desktop'
    appPid=$AppPid;appHwnd=$AppHwnd;settingsPid=$SettingsPid;settingsHwnd=$SettingsHwnd
    source=if(Test-Path -LiteralPath (Join-Path $script:RunDirectory 'run-metadata.json')){(Get-Content (Join-Path $script:RunDirectory 'run-metadata.json') -Raw | ConvertFrom-Json).source}else{$null}
    originalScale=$OriginalScaleValue;originalDpi=$OriginalDpi;finalScale=$finalScale;finalDpi=$finalDpi
    operatingSystemHighContrast=$operatingSystemHighContrast
    passed=$passed;failed=$failed;unverified=@('OS High Contrast display mode; intentionally not changed.','Human review of the recorded screenshots.','Stationary edge auto-scroll and endpoint crossing remain in the separate Sandbox selection test.')
    screenshots=@($script:Screenshots.ToArray());commands=@($script:Commands.ToArray());results=@($script:Results.ToArray());generatedUtc=[DateTime]::UtcNow.ToString('o')
    interactionPasses=$interactionPasses;missingInteractions=$missingInteractions;restoreFailures=$restoreFailures
    complete=($failed -eq 0 -and $interactionPasses -eq 11 -and $missingInteractions.Count -eq 0 -and $script:Screenshots.Count -eq 22 -and $restoreFailures -eq 0 -and $finalScale -ceq $OriginalScaleValue -and $finalDpi -eq $OriginalDpi -and $operatingSystemHighContrast -eq $false)
}
Write-JsonFile (Join-Path $script:RunDirectory 'results.json') $report
Write-Host "DPI correctness evidence: $script:RunDirectory"
Write-Host "Passed: $passed | Failed: $failed | Screenshots: $($script:Screenshots.Count)"
if ($failed -gt 0 -or -not $report.complete) { exit 1 }
exit 0
