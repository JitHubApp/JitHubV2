param(
    [Parameter(Mandatory)][int]$AppPid,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateSet('', 'x86', 'x64', 'arm64')][string]$ExpectedArchitecture = '',
    [ValidateRange(0, 8)][double]$ExpectedDpiScale = 0,
    [switch]$RequireSystemHighContrast
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$results = [System.Collections.Generic.List[object]]::new()

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
if (-not ('TouchSelectionNativeEnvironment' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class TouchSelectionNativeEnvironment
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HighContrastOn = 0x00000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct HighContrast
    {
        public uint Size;
        public uint Flags;
        public IntPtr DefaultScheme;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process2(
        IntPtr process,
        out ushort processMachine,
        out ushort nativeMachine);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref HighContrast value,
        uint flags);

    public static double GetWindowDpiScale(IntPtr hwnd)
        => Math.Max(1, GetDpiForWindow(hwnd)) / 96.0;

    public static string GetProcessArchitecture(IntPtr process)
    {
        if (!IsWow64Process2(process, out ushort processMachine, out ushort nativeMachine))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        ushort machine = processMachine == 0 ? nativeMachine : processMachine;
        return machine switch
        {
            0x014c => "x86",
            0x8664 => "x64",
            0xAA64 => "arm64",
            _ => $"unknown-0x{machine:X4}",
        };
    }

    public static bool IsSystemHighContrast()
    {
        var value = new HighContrast { Size = (uint)Marshal.SizeOf<HighContrast>() };
        if (!SystemParametersInfo(SpiGetHighContrast, value.Size, ref value, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return (value.Flags & HighContrastOn) != 0;
    }
}
'@
}

function Invoke-Control([string]$ControlId) {
    winapp ui invoke $ControlId -a $AppPid
    if ($LASTEXITCODE -ne 0) { throw "Could not invoke $ControlId." }
}

function Set-ToggleState([string]$ControlId, [bool]$Enabled) {
    $state = winapp ui get-property $ControlId -a $AppPid --property ToggleState --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Could not read $ControlId." }
    $isEnabled = [string]$state.properties.ToggleState -eq 'On'
    if ($isEnabled -ne $Enabled) { Invoke-Control $ControlId }
}

function Expand-Elements($Elements) {
    foreach ($element in $Elements) {
        $element
        Expand-Elements $element.children
    }
}

function Get-Elements {
    $json = winapp ui inspect -a $AppPid --depth 12 --interactive --json
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the sample.' }
    $tree = $json | ConvertFrom-Json
    return @(Expand-Elements $tree.windows.elements)
}

function Get-RendererPoint {
    $property = winapp ui get-property MarkdownRenderer -a $AppPid --property BoundingRectangle --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the renderer bounds.' }
    $bounds = @([string]$property.properties.BoundingRectangle -split ',' | ForEach-Object { [double]$_ })
    if ($bounds.Count -ne 4 -or $bounds[2] -le 0 -or $bounds[3] -le 0) {
        throw 'The renderer has no usable screen bounds.'
    }
    $x = [int]($bounds[0] + [Math]::Min($bounds[2] * 0.45, 320))
    $y = [int]($bounds[1] + [Math]::Min(112, $bounds[3] * 0.25))
    return "$x,$y"
}

function Get-ElementBounds([string]$ControlId) {
    $property = winapp ui get-property $ControlId -a $AppPid --property BoundingRectangle --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw "Could not read bounds for $ControlId." }
    $bounds = @([string]$property.properties.BoundingRectangle -split ',' | ForEach-Object { [double]$_ })
    if ($bounds.Count -ne 4 -or $bounds[2] -le 0 -or $bounds[3] -le 0) {
        throw "$ControlId has no usable screen bounds."
    }
    return $bounds
}

function Get-SelectedText {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $AppPid)
    $window = $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        $processCondition)
    if ($null -eq $window) { throw 'Could not find the sample window through UI Automation.' }

    $rendererCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        'MarkdownRenderer')
    $renderer = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $rendererCondition)
    if ($null -eq $renderer) { throw 'Could not find the renderer through UI Automation.' }

    $patternObject = $null
    if (-not $renderer.TryGetCurrentPattern(
            [System.Windows.Automation.TextPattern]::Pattern,
            [ref]$patternObject)) {
        throw 'MarkdownRenderer does not expose TextPattern.'
    }

    $ranges = @(([System.Windows.Automation.TextPattern]$patternObject).GetSelection())
    return ($ranges | ForEach-Object { $_.GetText(-1) }) -join ''
}

function Get-SelectedTextBounds {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $AppPid)
    $window = $root.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        $processCondition)
    if ($null -eq $window) { throw 'Could not find the sample window through UI Automation.' }

    $rendererCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        'MarkdownRenderer')
    $renderer = $window.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $rendererCondition)
    if ($null -eq $renderer) { throw 'Could not find the renderer through UI Automation.' }

    $patternObject = $null
    if (-not $renderer.TryGetCurrentPattern(
            [System.Windows.Automation.TextPattern]::Pattern,
            [ref]$patternObject)) {
        throw 'MarkdownRenderer does not expose TextPattern.'
    }

    $rectangles = [System.Collections.Generic.List[object]]::new()
    foreach ($range in ([System.Windows.Automation.TextPattern]$patternObject).GetSelection()) {
        $values = @($range.GetBoundingRectangles())
        if ($values.Count -gt 0 -and $values[0] -is [System.Windows.Rect]) {
            foreach ($value in $values) {
                $rectangles.Add([pscustomobject]@{
                    X = [double]$value.X
                    Y = [double]$value.Y
                    Width = [double]$value.Width
                    Height = [double]$value.Height
                })
            }
            continue
        }

        # Some UIA projections expose the native flattened double array rather
        # than System.Windows.Rect[]. Accept both shapes for Windows PowerShell
        # and PowerShell 7 release agents.
        for ($index = 0; $index + 3 -lt $values.Count; $index += 4) {
            $rectangles.Add([pscustomobject]@{
                X = [double]$values[$index]
                Y = [double]$values[$index + 1]
                Width = [double]$values[$index + 2]
                Height = [double]$values[$index + 3]
            })
        }
    }
    return @($rectangles)
}

function Assert-HandlesAdjacentToSelection {
    $selectionBounds = @(Get-SelectedTextBounds)
    if ($selectionBounds.Count -eq 0) {
        throw 'TextPattern returned no selection bounding rectangles.'
    }

    $dpiScale = [TouchSelectionNativeEnvironment]::GetWindowDpiScale((Get-AppWindowHandle))
    $tolerance = 56 * $dpiScale
    foreach ($handleId in @('MarkdownSelectionStartHandle', 'MarkdownSelectionEndHandle')) {
        $handle = Get-ElementBounds $handleId
        $centerX = $handle[0] + $handle[2] / 2
        $centerY = $handle[1] + $handle[3] / 2
        $adjacent = @($selectionBounds | Where-Object {
            $nearHorizontalEdge =
                [Math]::Abs($centerX - $_.X) -le $tolerance -or
                [Math]::Abs($centerX - ($_.X + $_.Width)) -le $tolerance
            $nearLine = $centerY -ge $_.Y -and $centerY -le ($_.Y + $_.Height + $tolerance)
            $nearHorizontalEdge -and $nearLine
        }).Count -gt 0
        if (-not $adjacent) {
            throw "$handleId is not adjacent to the active UIA selection."
        }
    }
}

function Get-RawAutomationName([string]$AutomationId) {
    $automationElement = [System.Windows.Automation.AutomationElement]
    $processCondition = [System.Windows.Automation.PropertyCondition]::new(
        $automationElement::ProcessIdProperty,
        $AppPid)
    $appRoot = $automationElement::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        $processCondition)
    if ($null -eq $appRoot) { return $null }

    $walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
    $pending = [System.Collections.Generic.Stack[System.Windows.Automation.AutomationElement]]::new()
    $pending.Push($appRoot)
    while ($pending.Count -gt 0) {
        $element = $pending.Pop()
        try {
            if ($element.Current.AutomationId -eq $AutomationId) {
                return $element.Current.Name
            }

            $child = $walker.GetFirstChild($element)
            while ($null -ne $child) {
                $pending.Push($child)
                $child = $walker.GetNextSibling($child)
            }
        }
        catch {
            # A XAML rebuild can retire an element while UIA is walking it.
        }
    }

    return $null
}

function Wait-ForRawAutomationName(
    [string]$AutomationId,
    [string]$ExpectedValue,
    [int]$TimeoutMilliseconds = 3000) {
    $deadline = [datetime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    do {
        $actual = Get-RawAutomationName $AutomationId
        if ($null -ne $actual -and $actual.Contains($ExpectedValue, [System.StringComparison]::Ordinal)) {
            return
        }
        Start-Sleep -Milliseconds 100
    } while ([datetime]::UtcNow -lt $deadline)

    throw "$AutomationId did not contain '$ExpectedValue' before the timeout."
}

function Get-AppWindowHandle {
    $windows = @(winapp ui list-windows -a $AppPid --json | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or $windows.Count -eq 0) { throw 'Could not resolve the sample window.' }
    $candidate = @($windows | Where-Object title -ne 'PopupHost' | Select-Object -First 1)
    if ($candidate.Count -eq 0) { throw 'Could not resolve the main sample window.' }
    $raw = [string]$candidate[0].hwnd
    if ($raw.StartsWith('0x', [System.StringComparison]::OrdinalIgnoreCase)) {
        return [IntPtr]([Convert]::ToInt64($raw.Substring(2), 16))
    }
    return [IntPtr]([long]$raw)
}

function Wait-ForHandles([bool]$Visible) {
    $deadline = [datetime]::UtcNow.AddSeconds(5)
    do {
        $handles = @(Get-Elements | Where-Object automationId -Like 'MarkdownSelection*Handle')
        $actual = $handles.Count -eq 2 -and @($handles | Where-Object { $_.width -lt 44 -or $_.height -lt 44 }).Count -eq 0
        if ($actual -eq $Visible) { return }
        Start-Sleep -Milliseconds 100
    } while ([datetime]::UtcNow -lt $deadline)
    throw "Selection handle visibility did not become $Visible."
}

function Test-State([string]$Name, [scriptblock]$Test) {
    try {
        & $Test
        $results.Add([pscustomobject]@{ name = $Name; status = 'PASS' })
    }
    catch {
        $results.Add([pscustomobject]@{ name = $Name; status = 'FAIL'; detail = $_.Exception.Message })
    }
}

Invoke-Control SampleNav_TouchSelection
winapp ui wait-for MarkdownRenderer -a $AppPid --value 'Touch selection' --contains -t 10000
if ($LASTEXITCODE -ne 0) { throw 'The Touch selection page did not render.' }
Set-ToggleState TouchViewportOwnershipToggle $false
Set-ToggleState ForcedHighContrastToggle $false
Set-ToggleState TextScaleToggle $false
Set-ToggleState ThemeToggle $false
Set-ToggleState RtlToggle $false
Set-ToggleState TaskEditingToggle $false

Test-State 'machine-configuration-contract' {
    $windowHandle = Get-AppWindowHandle
    $process = [System.Diagnostics.Process]::GetProcessById($AppPid)
    $observedArchitecture = [TouchSelectionNativeEnvironment]::GetProcessArchitecture($process.Handle)
    $observedDpiScale = [TouchSelectionNativeEnvironment]::GetWindowDpiScale($windowHandle)
    $observedHighContrast = [TouchSelectionNativeEnvironment]::IsSystemHighContrast()
    [pscustomobject]@{
        architecture = $observedArchitecture
        dpiScale = $observedDpiScale
        systemHighContrast = $observedHighContrast
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'environment.json')

    if ($ExpectedArchitecture -and $observedArchitecture -ne $ExpectedArchitecture) {
        throw "Expected $ExpectedArchitecture process, observed $observedArchitecture."
    }
    if ($ExpectedDpiScale -gt 0 -and [Math]::Abs($observedDpiScale - $ExpectedDpiScale) -gt 0.01) {
        throw "Expected DPI scale $ExpectedDpiScale, observed $observedDpiScale."
    }
    if ($RequireSystemHighContrast -and -not $observedHighContrast) {
        throw 'This matrix run requires Windows system High Contrast.'
    }
}

Test-State 'swipe-remains-scroll-only' {
    winapp ui touch MarkdownRenderer -a $AppPid --gesture swipe --direction up --distance 180 --duration-ms 350
    if ($LASTEXITCODE -ne 0) { throw 'Touch swipe injection failed.' }
    Wait-ForHandles $false
}

# Reload to restore a deterministic top-of-document hit point.
Invoke-Control SampleNav_Selection
Invoke-Control SampleNav_TouchSelection
winapp ui wait-for MarkdownRenderer -a $AppPid --value 'Press and hold a word' --contains -t 10000
if ($LASTEXITCODE -ne 0) { throw 'The touch document did not reset.' }

Test-State 'long-press-selects-and-opens-native-menu' {
    $point = Get-RendererPoint
    winapp ui touch MarkdownRenderer -a $AppPid --gesture long-press --at $point --hold-ms 700
    if ($LASTEXITCODE -ne 0) { throw 'Long-press injection failed.' }
    Wait-ForHandles $true
    $menu = @(Get-Elements | Where-Object automationId -in @(
        'MarkdownContextCopy', 'MarkdownContextCopyMarkdown', 'MarkdownContextSelectAll'))
    if ($menu.Count -ne 3) { throw "Expected three native selection commands, got $($menu.Count)." }
    winapp ui screenshot MarkdownRenderer -a $AppPid -o (Join-Path $OutputDirectory 'long-press-selection.png') --capture-screen --json
    if ($LASTEXITCODE -ne 0) { throw 'Long-press screenshot failed.' }
    winapp ui send-keys escape -a $AppPid --via post-message
}

Test-State 'double-tap-selects-without-capturing-pan' {
    $point = Get-RendererPoint
    winapp ui touch MarkdownRenderer -a $AppPid --gesture double-tap --at $point
    if ($LASTEXITCODE -ne 0) { throw 'Double-tap injection failed.' }
    Wait-ForHandles $true
    $selected = Get-SelectedText
    if ([string]::IsNullOrWhiteSpace($selected)) { throw 'UIA TextPattern returned no active selection.' }
}

Test-State 'touch-handle-drag-extends-selection-and-updates-uia' {
    $before = Get-SelectedText
    $rendererBounds = Get-ElementBounds MarkdownRenderer
    $targetX = [int]($rendererBounds[0] + [Math]::Min($rendererBounds[2] - 56, 520))
    $targetY = [int]($rendererBounds[1] + [Math]::Min($rendererBounds[3] - 72, 250))
    winapp ui touch MarkdownSelectionEndHandle -a $AppPid --gesture swipe --to-point "$targetX,$targetY" --duration-ms 500
    if ($LASTEXITCODE -ne 0) { throw 'End-handle touch drag failed.' }
    Wait-ForHandles $true
    $after = Get-SelectedText
    if ([string]::IsNullOrWhiteSpace($after) -or $after.Length -le $before.Length) {
        throw "Handle drag did not extend the UIA selection (before=$($before.Length), after=$($after.Length))."
    }
}

Test-State 'selection-copy-publishes-current-uia-range' {
    $selected = Get-SelectedText
    winapp ui send-keys 'ctrl+c' -a $AppPid --via send-input
    if ($LASTEXITCODE -ne 0) { throw 'Copy keyboard command failed.' }
    Start-Sleep -Milliseconds 200
    $clipboard = Get-Clipboard -Raw
    if ([string]::IsNullOrWhiteSpace($selected) -or $clipboard -ne $selected) {
        throw 'Clipboard text does not match TextPattern.GetSelection().'
    }
}

Test-State 'dark-and-high-contrast-selection-visuals' {
    try {
        if ($RequireSystemHighContrast) {
            $point = Get-RendererPoint
            winapp ui touch MarkdownRenderer -a $AppPid --gesture double-tap --at $point
            Wait-ForHandles $true
            $dpiLabel = ([TouchSelectionNativeEnvironment]::GetWindowDpiScale((Get-AppWindowHandle))).ToString(
                '0.##',
                [System.Globalization.CultureInfo]::InvariantCulture)
            winapp ui screenshot MarkdownRenderer -a $AppPid -o (Join-Path $OutputDirectory "selection-system-high-contrast-$dpiLabel.png") --json
            if ($LASTEXITCODE -ne 0) { throw 'System High Contrast selection screenshot failed.' }
        }

        Set-ToggleState ThemeToggle $true
        $point = Get-RendererPoint
        winapp ui touch MarkdownRenderer -a $AppPid --gesture double-tap --at $point
        Wait-ForHandles $true
        winapp ui screenshot MarkdownRenderer -a $AppPid -o (Join-Path $OutputDirectory 'selection-dark.png') --json
        if ($LASTEXITCODE -ne 0) { throw 'Dark selection screenshot failed.' }

        Set-ToggleState TextScaleToggle $true
        Set-ToggleState ForcedHighContrastToggle $true
        $contrast = winapp ui get-property ForcedHighContrastToggle -a $AppPid --property ToggleState --json | ConvertFrom-Json
        if ([string]$contrast.properties.ToggleState -ne 'On') { throw 'Forced High Contrast did not activate.' }
        $point = Get-RendererPoint
        winapp ui touch MarkdownRenderer -a $AppPid --gesture double-tap --at $point
        Wait-ForHandles $true
        winapp ui screenshot MarkdownRenderer -a $AppPid -o (Join-Path $OutputDirectory 'selection-high-contrast-200-text.png') --json
        if ($LASTEXITCODE -ne 0) { throw 'High Contrast selection screenshot failed.' }
    }
    finally {
        Set-ToggleState ForcedHighContrastToggle $false
        Set-ToggleState TextScaleToggle $false
        Set-ToggleState ThemeToggle $false
    }
}

Test-State 'rtl-relayout-cancels-touch-chrome-and-reselects-mixed-bidi' {
    try {
        $point = Get-RendererPoint
        winapp ui touch MarkdownRenderer -a $AppPid --gesture double-tap --at $point
        Wait-ForHandles $true
        Set-ToggleState RtlToggle $true
        Wait-ForHandles $false
        winapp ui touch MarkdownRenderer -a $AppPid --gesture double-tap --at $point
        Wait-ForHandles $true
        if ([string]::IsNullOrWhiteSpace((Get-SelectedText))) {
            throw 'Mixed-direction RTL selection was not exposed through TextPattern.'
        }
        Assert-HandlesAdjacentToSelection
        winapp ui screenshot MarkdownRenderer -a $AppPid -o (Join-Path $OutputDirectory 'selection-rtl.png') --json
        if ($LASTEXITCODE -ne 0) { throw 'RTL selection screenshot failed.' }
    }
    finally {
        Set-ToggleState RtlToggle $false
        Wait-ForHandles $false
    }
}

Test-State 'links-task-checkboxes-and-hosted-controls-remain-direct-targets' {
    try {
        Invoke-Control SampleNav_Selection
        Invoke-Control SampleNav_TouchSelection
        winapp ui wait-for MarkdownRenderer -a $AppPid --value 'Press and hold a word' --contains -t 10000

        winapp ui touch 'this link remains touchable' -a $AppPid --gesture tap
        if ($LASTEXITCODE -ne 0) { throw 'Touch link activation failed.' }
        Wait-ForRawAutomationName LinkActivationStatus 'link:Touch:https://example.invalid/touch-link'
        Wait-ForHandles $false

        Set-ToggleState TaskEditingToggle $true
        winapp ui scroll MarkdownRenderer -a $AppPid --direction down
        $taskCheckbox = @(Get-Elements | Where-Object {
            $_.type -eq 'CheckBox' -and $_.automationId -like 'MarkdownTask_*'
        } | Select-Object -First 1)
        if ($taskCheckbox.Count -ne 1) { throw 'Native task checkbox was not realized.' }
        $taskEnabled = winapp ui get-property $taskCheckbox[0].automationId -a $AppPid --property IsEnabled --json | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or [string]$taskEnabled.properties.IsEnabled -ne 'True') {
            throw 'Native task checkbox is disabled; its command source may not match the rendered source.'
        }
        winapp ui scroll-into-view $taskCheckbox[0].automationId -a $AppPid
        if ($LASTEXITCODE -ne 0) { throw 'Native task checkbox could not be scrolled into view.' }
        winapp ui touch $taskCheckbox[0].automationId -a $AppPid --gesture tap
        if ($LASTEXITCODE -ne 0) { throw 'Native task checkbox touch failed.' }

        # Toggling a task updates the document and realizes a replacement native
        # CheckBox. Refresh the automation tree before querying ToggleState so
        # validation never relies on the retired element's RuntimeId.
        $updatedTaskCheckbox = @(Get-Elements | Where-Object {
            $_.type -eq 'CheckBox' -and $_.automationId -like 'MarkdownTask_*'
        } | Select-Object -First 1)
        if ($updatedTaskCheckbox.Count -ne 1) { throw 'Updated native task checkbox was not realized.' }
        $taskState = winapp ui get-property $updatedTaskCheckbox[0].automationId -a $AppPid --property ToggleState --json | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or [string]$taskState.properties.ToggleState -ne 'On') {
            throw 'Native task checkbox did not toggle.'
        }
        Wait-ForHandles $false

        winapp ui wait-for 'Touch hosted action' -a $AppPid -t 5000
        if ($LASTEXITCODE -ne 0) { throw 'Hosted touch button was not realized.' }
        winapp ui touch 'Touch hosted action' -a $AppPid --gesture tap
        if ($LASTEXITCODE -ne 0) { throw 'Hosted button touch failed.' }
        winapp ui wait-for 'Touch hosted action activated' -a $AppPid -t 3000
        if ($LASTEXITCODE -ne 0) { throw 'Hosted button did not activate.' }
        Wait-ForHandles $false
    }
    finally {
        Set-ToggleState TaskEditingToggle $false
    }
}

Test-State 'ancestor-owned-viewport' {
    Set-ToggleState TouchViewportOwnershipToggle $false
    Invoke-Control TouchViewportOwnershipToggle
    winapp ui wait-for TouchSelectionAncestorViewport -a $AppPid -t 5000
    if ($LASTEXITCODE -ne 0) { throw 'The ancestor-owned viewport did not appear.' }
    winapp ui touch MarkdownRenderer -a $AppPid --gesture swipe --direction up --distance 140 --duration-ms 300
    if ($LASTEXITCODE -ne 0) { throw 'Ancestor viewport swipe failed.' }
    Wait-ForHandles $false
    Invoke-Control SampleNav_Selection
    Wait-ForHandles $false
}

Test-State 'navigation-unload-removes-selection-handles' {
    Invoke-Control SampleNav_TouchSelection
    $point = Get-RendererPoint
    winapp ui touch MarkdownRenderer -a $AppPid --gesture double-tap --at $point
    Wait-ForHandles $true
    Invoke-Control SampleNav_Selection
    Wait-ForHandles $false
}

$results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'results.json')
$results | Format-Table name, status, detail -AutoSize
if (@($results | Where-Object status -eq 'FAIL').Count) { exit 1 }
