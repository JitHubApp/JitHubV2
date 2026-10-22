[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Release','CancelNew','CancelExisting')][string]$Scenario,
    [Parameter(Mandatory)][ValidateSet('owned','ancestor')][string]$Viewport,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$ProcessId,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][long]$WindowHandle,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedExeSha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedSampleDllSha256,
    [Parameter(Mandatory)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$ExpectedRendererDllSha256,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedGuestComputerName,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedHostComputerName,
    [Parameter(Mandatory)][ValidateRange(1,[long]::MaxValue)][long]$ExpectedStartTicks
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$script:DeferredProbePath = Join-Path $PSScriptRoot 'MarkdownCorrectnessSandboxProbe.ps1'
$script:TouchDevice = [IntPtr]::Zero
$script:TouchInContact = $false
$script:TouchMayBeInContact = $false
$script:TouchPump = $null
$script:TouchPumpJoined = $true
$script:LastTouchPoint = $null
$script:ForegroundChecks = 0
$script:IdentityChecks = 0
$script:Evidence = [ordered]@{
    status = 'UNVERIFIED'
    scenario = $Scenario
    viewport = $Viewport
    identity = $null
    payloadHashes = $null
    preRunReceipt = $null
    emptyUiBeforeSeed = $null
    selectionSeed = $null
    initialUiSelection = $null
    armedUiSelection = $null
    initialSelectionFlyout = $null
    armedReceipt = $null
    firstContactBaseline = $null
    firstContactObserved = $null
    firstContactHoldingReceipt = $null
    firstContactPumpMetrics = $null
    terminalObserver = $null
    queuedAndDroppedReceipt = $null
    awaitingFreshContactReceipt = $null
    afterDropUiSelection = $null
    afterDropSelectionFlyout = $null
    freshContactBaseline = $null
    freshContactObserved = $null
    freshContactAcceptedReceipt = $null
    freshSelectionBeforeRelease = $null
    freshContactPumpMetrics = $null
    freshTerminalObserver = $null
    finalSelectionFlyout = $null
    completeReceipt = $null
    finalUiSelection = $null
    cleanup = $null
    failure = $null
}

function ConvertFrom-DeferredProbeJson {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Output)
    $jsonText = [string]::Join([Environment]::NewLine, [string[]]$Output)
    if ([string]::IsNullOrWhiteSpace($jsonText)) { throw 'The shared Sandbox probe returned no JSON.' }
    try { return (ConvertFrom-Json -InputObject $jsonText -ErrorAction Stop) }
    catch { throw "The shared Sandbox probe returned malformed JSON: $($_.Exception.Message)" }
}

function Invoke-SharedGuestProbe {
    param([Parameter(Mandatory)][ValidateSet('Identity','PayloadHashes')][string]$Mode)
    $parameters = @{
        Mode = $Mode
        ProcessId = $ProcessId
        WindowHandle = $WindowHandle
        ExpectedExeSha256 = $ExpectedExeSha256
        ExpectedGuestComputerName = $ExpectedGuestComputerName
        ExpectedHostComputerName = $ExpectedHostComputerName
        ExpectedStartTicks = $ExpectedStartTicks
    }
    $output = @(. $script:DeferredProbePath @parameters)
    return ConvertFrom-DeferredProbeJson -Output $output
}

function Assert-DeferredPayloadIdentity {
    $payload = Invoke-SharedGuestProbe -Mode PayloadHashes
    $identityPath = [IO.Path]::GetFullPath([string]$script:Identity.exePath)
    $payloadExePath = [IO.Path]::GetFullPath([string]$payload.executablePath)
    if ([string]::Compare($identityPath,$payloadExePath,[StringComparison]::OrdinalIgnoreCase) -ne 0) {
        throw 'The payload hash receipt belongs to a different executable directory/process image.'
    }
    $expected = @{
        'MarkdownRenderer.Sample.dll' = $ExpectedSampleDllSha256.ToLowerInvariant()
        'MarkdownRenderer.dll' = $ExpectedRendererDllSha256.ToLowerInvariant()
    }
    $directory = [IO.Path]::GetDirectoryName($identityPath)
    foreach ($name in $expected.Keys) {
        $records = @($payload.files | Where-Object { [string]$_.name -ceq $name })
        if ($records.Count -ne 1) { throw "The validated guest payload receipt must contain exactly one '$name'." }
        $record = $records[0]
        $expectedPath = [IO.Path]::GetFullPath((Join-Path $directory $name))
        $reportedPath = [IO.Path]::GetFullPath([string]$record.path)
        if ([string]::Compare($expectedPath,$reportedPath,[StringComparison]::OrdinalIgnoreCase) -ne 0 -or
            [long]$record.length -le 0 -or [string]$record.sha256 -cne $expected[$name]) {
            throw "The live guest payload '$name' is not the exact approved, nonempty binary beside the captured executable."
        }
    }
    return $payload
}

# The shared probe enforces the guest machine and exact process/window/binary
# identity before it initializes any Windows native helpers. Keep that gate at
# script scope; helper declarations below use its verified pointer types.
if (-not (Test-Path -LiteralPath $script:DeferredProbePath -PathType Leaf)) {
    throw "Missing shared guest probe '$script:DeferredProbePath'."
}
$bootstrapParameters = @{
    Mode = 'Identity'
    ProcessId = $ProcessId
    WindowHandle = $WindowHandle
    ExpectedExeSha256 = $ExpectedExeSha256
    ExpectedGuestComputerName = $ExpectedGuestComputerName
    ExpectedHostComputerName = $ExpectedHostComputerName
    ExpectedStartTicks = $ExpectedStartTicks
}
$bootstrapOutput = @(. $script:DeferredProbePath @bootstrapParameters)
$script:Identity = ConvertFrom-DeferredProbeJson -Output $bootstrapOutput
if ([int]$script:Identity.processId -ne $ProcessId -or [long]$script:Identity.hwnd -ne $WindowHandle -or
    [string]$script:Identity.computerName -cne $ExpectedGuestComputerName -or
    [string]$script:Identity.exeSha256 -cne $ExpectedExeSha256.ToLowerInvariant() -or
    [long]$script:Identity.startTicks -ne $ExpectedStartTicks -or
    [string]$script:Identity.title -cne 'MarkdownRenderer deferred-touch probe') {
    throw 'The initial guest identity receipt does not match the exact approved deferred-touch target.'
}
$script:PayloadReceipt = Assert-DeferredPayloadIdentity
$script:Evidence.identity = $script:Identity
$script:Evidence.payloadHashes = $script:PayloadReceipt

if (-not ('MarkdownDeferredTouchForegroundNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class MarkdownDeferredTouchForegroundNative
{
    [DllImport("user32.dll", EntryPoint="GetForegroundWindow", SetLastError=true)]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint="GetWindowThreadProcessId", SetLastError=true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", EntryPoint="GetAncestor", SetLastError=true)]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint="SetWindowPos", SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    // HWND_TOPMOST without moving, resizing or activating the window.
    public static void PinTopmost(IntPtr hwnd)
    {
        if (!SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010))
            throw new InvalidOperationException("SetWindowPos(HWND_TOPMOST) failed with Win32 error " + Marshal.GetLastWin32Error() + ".");
    }

    public static object Read()
    {
        IntPtr raw = GetForegroundWindow();
        // T1-023: after UIA focus Windows can report WinUI's InputSite child
        // window as foreground. Its root (GA_ROOT) is the active top-level
        // window; the raw handle is kept as evidence.
        IntPtr hwnd = raw == IntPtr.Zero ? IntPtr.Zero : GetAncestor(raw, 2);
        if (hwnd == IntPtr.Zero)
            hwnd = raw;
        uint processId;
        if (hwnd == IntPtr.Zero || GetWindowThreadProcessId(hwnd, out processId) == 0)
            throw new InvalidOperationException("Could not observe the current foreground window and process.");
        return new { hwnd = hwnd.ToInt64(), processId = processId, rawHwnd = raw.ToInt64() };
    }
}
'@
}

function Invoke-SharedIdentityProbe {
    return Invoke-SharedGuestProbe -Mode Identity
}

function Assert-DeferredProbeIdentity {
    $identity = Invoke-SharedIdentityProbe
    if ([int]$identity.processId -ne $ProcessId -or [long]$identity.hwnd -ne $WindowHandle -or
        [string]$identity.computerName -cne $ExpectedGuestComputerName -or
        [string]$identity.exeSha256 -cne $ExpectedExeSha256.ToLowerInvariant() -or
        [long]$identity.startTicks -ne $ExpectedStartTicks -or
        [string]$identity.title -cne 'MarkdownRenderer deferred-touch probe') {
        throw 'The deferred-touch action is not attached to the exact captured guest process, HWND, binary, start time, machine, and title.'
    }
    $script:IdentityChecks++
    return $identity
}

function Assert-DeferredProbeForeground {
    [MarkdownCorrectnessSandboxNative]::VerifyWindow([IntPtr]$WindowHandle, $ProcessId)
    $foreground = [MarkdownDeferredTouchForegroundNative]::Read()
    if ([long]$foreground.hwnd -ne $WindowHandle -or [uint32]$foreground.processId -ne [uint32]$ProcessId) {
        throw "Refusing synthetic touch because foreground HWND/PID ($($foreground.hwnd)/$($foreground.processId)) is not the exact deferred-touch app ($WindowHandle/$ProcessId)."
    }
    $script:ForegroundChecks++
    return $foreground
}

function Invoke-DeferredTouchPattern {
    param([Parameter(Mandatory)][string]$AutomationId)
    $element = Find-UniqueProcessElement $ProcessId $AutomationId
    if (([int]$element.Current.ProcessId -ne $ProcessId -and [int]$element.Current.ProcessId -ne 0) -or
        [string]$element.Current.AutomationId -cne $AutomationId) {
        throw "AutomationId '$AutomationId' did not resolve to the exact captured sample process/control."
    }
    if (-not $element.Current.IsEnabled) { throw "Control '$AutomationId' is not enabled." }
    $pattern = Get-Pattern $element ([System.Windows.Automation.InvokePattern]::Pattern)
    if ($null -eq $pattern) { throw "Control '$AutomationId' does not expose InvokePattern." }
    $pattern.Invoke()
}

function Set-DeferredTouchViewport {
    param([Parameter(Mandatory)][ValidateSet('owned','ancestor')][string]$RequestedViewport)
    $element = Find-UniqueProcessElement $ProcessId 'DeferredTouchAncestorViewport'
    if (([int]$element.Current.ProcessId -ne $ProcessId -and [int]$element.Current.ProcessId -ne 0) -or
        [string]$element.Current.AutomationId -cne 'DeferredTouchAncestorViewport') {
        throw 'The viewport checkbox is not rooted in the exact captured app process.'
    }
    $pattern = Get-Pattern $element ([System.Windows.Automation.TogglePattern]::Pattern)
    if ($null -eq $pattern) { throw 'DeferredTouchAncestorViewport does not expose TogglePattern.' }
    $expected = if ($RequestedViewport -eq 'ancestor') { [System.Windows.Automation.ToggleState]::On } else { [System.Windows.Automation.ToggleState]::Off }
    if ($pattern.Current.ToggleState -ne $expected) {
        if (-not $element.Current.IsEnabled) { throw 'The viewport checkbox is disabled while changing renderer ownership.' }
        $pattern.Toggle()
    }
    $verified = Get-Pattern (Find-UniqueProcessElement $ProcessId 'DeferredTouchAncestorViewport') ([System.Windows.Automation.TogglePattern]::Pattern)
    if ($null -eq $verified -or $verified.Current.ToggleState -ne $expected) {
        throw "The real viewport control did not select '$RequestedViewport'."
    }
}

function Get-DeferredTouchReceipt {
    $element = Find-UniqueProcessElement $ProcessId 'DeferredTouchReceipt'
    if (([int]$element.Current.ProcessId -ne $ProcessId -and [int]$element.Current.ProcessId -ne 0) -or
        [string]$element.Current.AutomationId -cne 'DeferredTouchReceipt') {
        throw 'The receipt control is not rooted in the exact captured app process.'
    }
    $pattern = Get-Pattern $element ([System.Windows.Automation.ValuePattern]::Pattern)
    if ($null -eq $pattern) { throw 'DeferredTouchReceipt does not expose ValuePattern.' }
    $text = [string]$pattern.Current.Value
    if ([string]::IsNullOrWhiteSpace($text)) { throw 'The real deferred-touch receipt is empty.' }
    try { return (ConvertFrom-Json -InputObject $text -ErrorAction Stop) }
    catch { throw "The real deferred-touch receipt is invalid JSON: $($_.Exception.Message)" }
}

function Wait-DeferredTouchPhase {
    param(
        [Parameter(Mandatory)][ValidateSet('armed','holding','releasing','awaitingFreshContact','freshContactAccepted','complete')][string[]]$Phase,
        [Parameter(Mandatory)][int]$Sequence,
        [Parameter(Mandatory)][int]$TimeoutMilliseconds
    )
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    $last = $null
    do {
        $last = Get-DeferredTouchReceipt
        if ([int]$last.version -ne 1) { throw "Unsupported deferred-touch receipt version '$($last.version)'." }
        if ([int]$last.sequence -gt $Sequence) { throw "Deferred-touch receipt sequence advanced unexpectedly to $($last.sequence), expected $Sequence." }
        if ([int]$last.sequence -eq $Sequence) {
            if (-not [string]::IsNullOrWhiteSpace([string]$last.failure)) { throw "Deferred-touch probe entered failed state: $($last.failure)" }
            if ([string]$last.phase -ceq 'failed') { throw 'Deferred-touch probe entered failed state without a failure detail.' }
            if ([string]$last.phase -in $Phase) { return $last }
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    # PowerShell names are case-insensitive: never reuse $Phase for the last phase.
    $lastPhase = if ($null -eq $last) { '<no receipt>' } else { [string]$last.phase }
    # P1-005 diagnostics: the probe republishes live deferred-input state while holding.
    $live = if ($null -ne $last -and $null -ne $last.PSObject.Properties['live'] -and $null -ne $last.live) { ' Live: ' + (ConvertTo-Json -InputObject $last.live -Depth 6 -Compress) } else { '' }
    throw "Timed out waiting for deferred-touch phase [$($Phase -join ',')] at sequence $Sequence; last phase was '$lastPhase'.$live"
}

function Get-DeferredTouchUiState {
    param([Parameter(Mandatory)][ValidateSet('owned','ancestor')][string]$RequestedViewport)
    $textPattern = Get-TextPattern
    $ranges = @($textPattern.pattern.GetSelection())
    $selection = (($ranges | ForEach-Object { $_.GetText(-1) }) -join '')
    $selectionRects = [Collections.Generic.List[object]]::new()
    foreach ($range in $ranges) {
        foreach ($rect in @(ConvertTo-RangeRectangles $range)) {
            $selectionRects.Add(@{x=$rect.X;y=$rect.Y;width=$rect.Width;height=$rect.Height})
        }
    }
    if ($RequestedViewport -eq 'owned') {
        $viewportElement = $textPattern.renderer
        $viewportPattern = Get-Pattern $viewportElement ([System.Windows.Automation.ScrollPattern]::Pattern)
        if ($null -eq $viewportPattern) { throw 'The owned renderer does not expose its expected UIA ScrollPattern.' }
    }
    else {
        $viewportElement = Find-Unique (Get-Root) 'TouchSelectionAncestorViewport'
        $viewportPattern = Get-Pattern $viewportElement ([System.Windows.Automation.ScrollPattern]::Pattern)
        if ($null -eq $viewportPattern) { throw 'The expected page-owned ancestor does not expose its UIA ScrollPattern.' }
    }
    $viewport = $viewportElement.Current
    $rendererRect = $textPattern.renderer.Current.BoundingRectangle
    return [pscustomobject]@{
        selection=$selection;rangeCount=$ranges.Count
        supportedTextSelection=[string]$textPattern.pattern.SupportedTextSelection
        verticalPercent=$viewportPattern.Current.VerticalScrollPercent
        verticallyScrollable=[bool]$viewportPattern.Current.VerticallyScrollable
        viewportAutomationId=[string]$viewport.AutomationId;viewportName=[string]$viewport.Name
        viewportClass=[string]$viewport.ClassName
        rendererRect=@{x=$rendererRect.X;y=$rendererRect.Y;width=$rendererRect.Width;height=$rendererRect.Height}
        selectionRects=@($selectionRects.ToArray());handles=@(Get-Handles)
        dpi=[MarkdownCorrectnessSandboxNative]::Dpi([IntPtr]$WindowHandle)
    }
}

function Test-DeferredTouchClearedSelection {
    param([Parameter(Mandatory)]$State)
    foreach ($name in @('supportedTextSelection','rangeCount','selection','selectionRects','handles')) {
        if ($null -eq $State.PSObject.Properties[$name]) { throw "The UIA cleared-selection check is missing '$name'." }
    }
    $rangeCount = [int]$State.rangeCount
    $text = [string]$State.selection
    $rectangles = @($State.selectionRects)
    $handles = @($State.handles)
    if (-not [string]::IsNullOrEmpty($text) -or $rectangles.Count -ne 0 -or $handles.Count -ne 0) { return $false }
    switch ([string]$State.supportedTextSelection) {
        'None' { return ($rangeCount -eq 0) }
        'Single' { return ($rangeCount -eq 0 -or $rangeCount -eq 1) }
        default { return $false }
    }
}

function Get-DeferredTouchSelectionMenuCommands {
    $expected = @('MarkdownContextCopy','MarkdownContextCopyMarkdown','MarkdownContextSelectAll')
    $found = [Collections.Generic.List[object]]::new()
    foreach ($candidate in @(Get-ProcessElements $ProcessId)) {
        if ($null -eq $candidate -or $null -eq $candidate.element) { continue }
        $current = $candidate.element.Current
        if ([int]$candidate.ownerProcessId -ne $ProcessId -or [long]$candidate.ownerWindowHandle -le 0 -or
            [int]$candidate.reportedProcessId -ne [int]$current.ProcessId -or
            ([int]$current.ProcessId -ne 0 -and [int]$current.ProcessId -ne $ProcessId)) {
            continue
        }
        if ([string]$current.AutomationId -cin $expected) {
            $found.Add([pscustomobject]@{
                automationId = [string]$current.AutomationId
                processId = [int]$current.ProcessId
                isEnabled = [bool]$current.IsEnabled
                isOffscreen = [bool]$current.IsOffscreen
                ownerWindowHandle = [long]$candidate.ownerWindowHandle
            })
        }
    }
    $duplicates = @($found | Group-Object -Property automationId | Where-Object Count -gt 1)
    if ($duplicates.Count -gt 0) { throw "The exact app process exposed duplicate selection flyout commands: $($duplicates.Name -join ', ')." }
    return @($found.ToArray())
}

function Dismiss-DeferredTouchSelectionFlyout {
    param([Parameter(Mandatory)][ValidateSet('owned','ancestor')][string]$RequestedViewport,[Parameter(Mandatory)][string]$ExpectedSelection,[Parameter(Mandatory)][string]$Stage)
    $beforeState = Assert-DeferredTouchSelection -State (Get-DeferredTouchUiState -RequestedViewport $RequestedViewport) `
        -ExpectedSelected (-not [string]::IsNullOrEmpty($ExpectedSelection)) -ExpectedText $ExpectedSelection -Stage "$Stage before flyout dismissal"
    $beforeCommands = @(Get-DeferredTouchSelectionMenuCommands)
    if ($beforeCommands.Count -eq 0) {
        return [pscustomobject]@{stage=$Stage;action='none';commandsBefore=@();commandsAfter=@();selectionBefore=$beforeState.selection;selectionAfter=$beforeState.selection;verified=$true}
    }
    $copyCommands = @($beforeCommands | Where-Object { $_.automationId -ceq 'MarkdownContextCopy' })
    if ($copyCommands.Count -ne 1 -or -not [bool]$copyCommands[0].isEnabled -or [bool]$copyCommands[0].isOffscreen) {
        throw "$Stage found a selection flyout but not one enabled, visible real MarkdownContextCopy command."
    }
    Invoke-DeferredTouchPattern -AutomationId 'MarkdownContextCopy'
    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    $afterCommands = @(Get-DeferredTouchSelectionMenuCommands)
    while ($afterCommands.Count -gt 0 -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 40
        $afterCommands = @(Get-DeferredTouchSelectionMenuCommands)
    }
    if ($afterCommands.Count -ne 0) { throw "$Stage Copy invocation did not dismiss the real selection flyout." }
    $afterState = Assert-DeferredTouchSelection -State (Get-DeferredTouchUiState -RequestedViewport $RequestedViewport) `
        -ExpectedSelected (-not [string]::IsNullOrEmpty($ExpectedSelection)) -ExpectedText $ExpectedSelection -Stage "$Stage after flyout dismissal"
    if ([int]$afterState.rangeCount -ne [int]$beforeState.rangeCount -or
        [string]$afterState.selection -cne [string]$beforeState.selection) {
        throw "$Stage Copy invocation changed the actual UIA selection."
    }
    return [pscustomobject]@{stage=$Stage;action='invoke-copy';commandsBefore=$beforeCommands;commandsAfter=$afterCommands;selectionBefore=$beforeState.selection;selectionAfter=$afterState.selection;verified=$true}
}

function Assert-DeferredTouchUiSelectionUnchanged {
    param([Parameter(Mandatory)]$Before,[Parameter(Mandatory)]$After,[Parameter(Mandatory)][string]$Stage)
    if ([int]$Before.rangeCount -ne [int]$After.rangeCount -or [string]$Before.selection -cne [string]$After.selection) {
        throw "$Stage changed the real UIA selection while arming the gesture."
    }
    $beforeRects = @($Before.selectionRects)
    $afterRects = @($After.selectionRects)
    if ($beforeRects.Count -ne $afterRects.Count) { throw "$Stage changed the UIA selection's visible rectangle count." }
    for ($index = 0; $index -lt $beforeRects.Count; $index++) {
        foreach ($field in @('x','y','width','height')) {
            if ($null -eq $beforeRects[$index].$field -or $null -eq $afterRects[$index].$field -or
                [double]$beforeRects[$index].$field -ne [double]$afterRects[$index].$field) {
                throw "$Stage changed selection rectangle $index field '$field' between capture and arm."
            }
        }
    }
}

function Assert-DeferredTouchSelection {
    param(
        [Parameter(Mandatory)]$State,
        [Parameter(Mandatory)][bool]$ExpectedSelected,
        [string]$ExpectedText = '',
        [Parameter(Mandatory)][string]$Stage,
        [switch]$AllowUnavailableGeometry,
        [object]$HeldReceipt,
        [int]$ExpectedSequence = 0
    )
    $rangeCount = [int]$State.rangeCount
    $text = [string]$State.selection
    $handles = @($State.handles)
    $allowedBusySnapshot = $false
    if ($AllowUnavailableGeometry) {
        if ($Stage -cne 'BeforeTerminal UIA state' -or $null -eq $HeldReceipt -or
            [int]$HeldReceipt.version -ne 1 -or $ExpectedSequence -le 0 -or
            [int]$HeldReceipt.sequence -ne $ExpectedSequence -or
            [string]$HeldReceipt.phase -cne 'holding' -or -not [bool]$HeldReceipt.workerOwnsSnapshot -or
            [uint32]$HeldReceipt.pointerId -eq 0 -or [long]$HeldReceipt.gestureEpoch -le 0 -or
            $null -eq $HeldReceipt.beforeTerminal) {
            throw "$Stage may omit busy-snapshot geometry only with the exact-sequence holding receipt and worker lease proof."
        }
        $heldState = $HeldReceipt.beforeTerminal
        if ([uint32]$heldState.activePointerId -ne [uint32]$HeldReceipt.pointerId -or
            [long]$heldState.gestureEpoch -ne [long]$HeldReceipt.gestureEpoch -or
            [int]$heldState.matchingTerminalCount -ne 0 -or @($heldState.queuedTerminals).Count -ne 0 -or
            -not [bool]$heldState.selectionIsActive) {
            throw "$Stage busy-snapshot geometry exception is not backed by the same live owner before terminal delivery."
        }
        $allowedBusySnapshot = $true
    }
    if ($ExpectedSelected) {
        if ($rangeCount -ne 1 -or [string]::IsNullOrWhiteSpace($text)) {
            throw "$Stage did not expose exactly one real nonempty UIA text selection (ranges=$rangeCount, text='$text')."
        }
        if ([string]$State.supportedTextSelection -cne 'Single') {
            throw "$Stage did not report the expected single-selection TextPattern capability."
        }
        $rectangles = @($State.selectionRects)
        if ($rectangles.Count -eq 0) {
            if (-not $allowedBusySnapshot) { throw "$Stage exposed selected text but no actual UIA selection rectangle." }
        }
        foreach ($rect in $rectangles) {
            foreach ($field in @('x','y','width','height')) {
                $value = $rect.$field
                if ($null -eq $value -or [double]::IsNaN([double]$value) -or [double]::IsInfinity([double]$value)) {
                    throw "$Stage has a non-finite or missing UIA selection rectangle field '$field'."
                }
            }
            if ([double]$rect.width -le 0 -or [double]$rect.height -le 0) { throw "$Stage has a non-visible UIA selection rectangle." }
        }
        if (-not [string]::IsNullOrEmpty($ExpectedText) -and $text -cne $ExpectedText) {
            throw "$Stage selected '$text', expected the independently resolved UIA text '$ExpectedText'."
        }
    }
    else {
        if (-not (Test-DeferredTouchClearedSelection -State $State)) {
            throw "$Stage should have no selected text, selection geometry, or visible handles (support=$($State.supportedTextSelection), ranges=$rangeCount, text='$text', rectangles=$(@($State.selectionRects).Count), handles=$($handles.Count))."
        }
    }
    return $State
}

function Get-DeferredRangeFingerprint {
    param([Parameter(Mandatory)]$Range)
    foreach ($name in @('start','end')) {
        if ($null -eq $Range.PSObject.Properties[$name]) { throw "DeferredTouchReport range omitted '$name'." }
        $position = $Range.$name
        foreach ($part in @('blockIndex','inlineIndex','characterOffset')) {
            if ($null -eq $position.PSObject.Properties[$part]) { throw "DeferredTouchReport range position omitted '$part'." }
        }
    }
    return ('{0}:{1}:{2}/{3}:{4}:{5}' -f [int]$Range.start.blockIndex,[int]$Range.start.inlineIndex,[int]$Range.start.characterOffset,
        [int]$Range.end.blockIndex,[int]$Range.end.inlineIndex,[int]$Range.end.characterOffset)
}

function Assert-DeferredTouchReceiptProperties {
    param([Parameter(Mandatory)]$Receipt,[Parameter(Mandatory)][string[]]$Properties)
    foreach ($name in $Properties) {
        if ($null -eq $Receipt.PSObject.Properties[$name]) { throw "DeferredTouchReport omitted required property '$name'." }
    }
}

function Assert-DeferredTouchStateProperties {
    param([Parameter(Mandatory)]$State)
    Assert-DeferredTouchReceiptProperties -Receipt $State -Properties @(
        'activePointerId','gestureEpoch','touchState','queuedCount','queuedTerminals','matchingTerminalCount',
        'captureCount','retryTimerExists','isHandleDragging','visibleHandleCount','selectionContextMenuOpen',
        'selectionIsActive','selectionRange')
    $null = Get-DeferredRangeFingerprint -Range $State.selectionRange
}

function Assert-DeferredTouchEvidence {
    param(
        [Parameter(Mandatory)][ValidateSet('Release','CancelNew','CancelExisting')][string]$ExpectedScenario,
        [Parameter(Mandatory)][ValidateSet('owned','ancestor')][string]$ExpectedViewport,
        [Parameter(Mandatory)][int]$ExpectedSequence,
        [Parameter(Mandatory)]$ArmedReceipt,
        [Parameter(Mandatory)]$HoldingReceipt,
        [Parameter(Mandatory)]$DroppedReceipt,
        [Parameter(Mandatory)]$AwaitingReceipt,
        [Parameter(Mandatory)]$FreshReceipt,
        [Parameter(Mandatory)]$CompleteReceipt,
        [Parameter(Mandatory)]$InitialUiState,
        [Parameter(Mandatory)]$BeforeTerminalUiState,
        [Parameter(Mandatory)]$AfterDropUiState,
        [Parameter(Mandatory)]$FinalUiState,
        [Parameter(Mandatory)][uint32]$FirstRoutedPointerId,
        [Parameter(Mandatory)][long]$FirstContactSerial,
        [Parameter(Mandatory)][uint32]$FreshRoutedPointerId,
        [Parameter(Mandatory)][long]$FreshContactSerial
    )
    $canceled = $ExpectedScenario -ne 'Release'
    $hadSelection = $ExpectedScenario -ne 'CancelNew'
    $receipts = @($ArmedReceipt,$HoldingReceipt,$DroppedReceipt,$AwaitingReceipt,$FreshReceipt,$CompleteReceipt)
    $expectedPhases = @(@('armed'),@('holding'),@('releasing','awaitingFreshContact'),@('awaitingFreshContact'),@('freshContactAccepted'),@('complete'))
    for ($index = 0; $index -lt $receipts.Count; $index++) {
        $receipt = $receipts[$index]
        Assert-DeferredTouchReceiptProperties -Receipt $receipt -Properties @(
            'version','sequence','phase','viewport','canceled','hadSelectionBeforeContact','pointerId','gestureEpoch',
            'workerOwnsSnapshot','preContact','beforeTerminal','queuedTerminal','afterDrop','freshContact','failure')
        if ([int]$receipt.version -ne 1 -or [int]$receipt.sequence -ne $ExpectedSequence -or
            [string]$receipt.phase -cnotin $expectedPhases[$index] -or [string]$receipt.viewport -cne $ExpectedViewport -or
            [bool]$receipt.canceled -ne $canceled -or [bool]$receipt.hadSelectionBeforeContact -ne $hadSelection -or
            -not [string]::IsNullOrEmpty([string]$receipt.failure)) {
            throw "DeferredTouchReport $($expectedPhases[$index]) receipt has wrong version/sequence/scenario/viewport or reports failure."
        }
    }
    if ([uint32]$ArmedReceipt.pointerId -ne 0 -or [long]$ArmedReceipt.gestureEpoch -ne 0) {
        throw 'The armed receipt must begin without a pointer owner or gesture epoch.'
    }
    $initialSelected = ([int]$InitialUiState.rangeCount -eq 1 -and -not [string]::IsNullOrWhiteSpace([string]$InitialUiState.selection))
    if ($initialSelected -ne $hadSelection) { throw 'Initial real UIA selection does not match the scenario-required pre-contact selection; no programmatic selection clearing is allowed.' }
    if (-not $hadSelection -and -not (Test-DeferredTouchClearedSelection -State $InitialUiState)) {
        throw 'CancelNew requires no selected text, selection rectangles, or stale handles; a single collapsed UIA caret is permitted.'
    }

    if ($null -eq $ArmedReceipt.preContact) { throw 'The armed receipt did not capture the actual pre-contact renderer state.' }
    Assert-DeferredTouchStateProperties -State $ArmedReceipt.preContact
    if ((Get-DeferredRangeFingerprint $ArmedReceipt.preContact.selectionRange) -cne (Get-DeferredRangeFingerprint $HoldingReceipt.preContact.selectionRange)) {
        throw 'The real pre-contact logical selection range changed between Arm and the held-contact receipt.'
    }
    if ([uint32]$ArmedReceipt.preContact.activePointerId -ne 0 -or [long]$ArmedReceipt.preContact.gestureEpoch -ne 0 -or
        [int]$ArmedReceipt.preContact.queuedCount -ne 0 -or [int]$ArmedReceipt.preContact.captureCount -ne 0 -or
        [bool]$ArmedReceipt.preContact.retryTimerExists -or [bool]$ArmedReceipt.preContact.selectionContextMenuOpen -or
        [bool]$ArmedReceipt.preContact.selectionIsActive -ne $hadSelection) {
        throw 'PreContact did not prove a clean, context-menu-free owner state and the required actual prior selection.'
    }

    $holdingId = [uint32]$HoldingReceipt.pointerId
    $holdingEpoch = [long]$HoldingReceipt.gestureEpoch
    if ($holdingId -eq 0 -or $holdingEpoch -le 0 -or -not [bool]$HoldingReceipt.workerOwnsSnapshot -or
        [uint32]$HoldingReceipt.preContact.activePointerId -ne 0 -or $null -eq $HoldingReceipt.beforeTerminal -or
        $null -ne $HoldingReceipt.queuedTerminal -or $null -ne $HoldingReceipt.afterDrop -or $null -ne $HoldingReceipt.freshContact) {
        throw 'Holding receipt lacks the active real owner, worker-held snapshot, captured pre-contact state, or clean before-terminal boundary.'
    }
    foreach ($receipt in @($DroppedReceipt,$AwaitingReceipt,$FreshReceipt,$CompleteReceipt)) {
        if ([uint32]$receipt.pointerId -ne $holdingId -or [long]$receipt.gestureEpoch -ne $holdingEpoch) {
            throw "DeferredTouchReport '$($receipt.phase)' top-level owner must retain the original held pointer/gesture pair."
        }
    }
    Assert-DeferredTouchStateProperties -State $HoldingReceipt.beforeTerminal
    if ([uint32]$HoldingReceipt.beforeTerminal.activePointerId -ne $holdingId -or
        [long]$HoldingReceipt.beforeTerminal.gestureEpoch -ne $holdingEpoch -or
        [int]$HoldingReceipt.beforeTerminal.matchingTerminalCount -ne 0 -or
        @($HoldingReceipt.beforeTerminal.queuedTerminals).Count -ne 0 -or
        -not [bool]$HoldingReceipt.beforeTerminal.selectionIsActive) {
        throw 'BeforeTerminal did not prove the matching real pointer/gesture owner and zero matching/queued terminal events before Up/Cancel.'
    }
    if ($hadSelection -and (Get-DeferredRangeFingerprint $HoldingReceipt.beforeTerminal.selectionRange) -cne
        (Get-DeferredRangeFingerprint $ArmedReceipt.preContact.selectionRange)) {
        throw 'The real existing logical selection changed before terminal delivery.'
    }
    if ($holdingId -ne $FirstRoutedPointerId -or $FirstRoutedPointerId -eq 0 -or $FirstContactSerial -le 0) {
        throw 'The renderer owner does not match the independently routed OS pointer/contact serial; injected index zero is not accepted as OS provenance.'
    }
    if (-not $hadSelection -and [string]$BeforeTerminalUiState.selection -cne 'deferredword') {
        throw 'CancelNew did not create the expected real held-word UIA selection before terminal delivery.'
    }

    $terminalKind = if ($canceled) { 'Cancel' } else { 'Release' }
    if ($null -eq $DroppedReceipt.queuedTerminal -or $null -eq $DroppedReceipt.afterDrop) {
        throw 'QueuedTerminal or AfterDrop receipt is missing; deferred work was not independently observed.'
    }
    Assert-DeferredTouchStateProperties -State $DroppedReceipt.queuedTerminal
    Assert-DeferredTouchStateProperties -State $DroppedReceipt.afterDrop
    $terminals = @($DroppedReceipt.queuedTerminal.queuedTerminals)
    if ($terminals.Count -lt 1 -or ($terminalKind -eq 'Release' -and $terminals.Count -ne 1) -or
        [uint32]$DroppedReceipt.queuedTerminal.activePointerId -ne $holdingId -or
        [long]$DroppedReceipt.queuedTerminal.gestureEpoch -ne $holdingEpoch -or
        [int]$DroppedReceipt.queuedTerminal.matchingTerminalCount -ne $terminals.Count) {
        throw 'QueuedTerminal does not contain exactly one release or one-or-more exact cancels for the worker-owned gesture.'
    }
    if ($null -eq $DroppedReceipt.PSObject.Properties['workerHeldAtDrop'] -or -not [bool]$DroppedReceipt.workerHeldAtDrop) {
        throw 'The report did not prove that the worker still held the snapshot immediately before deferred-terminal processing.'
    }
    foreach ($terminal in $terminals) {
        Assert-DeferredTouchReceiptProperties -Receipt $terminal -Properties @('kind','pointerId','gestureEpoch','isTouch')
        if ([string]$terminal.kind -cne $terminalKind -or [uint32]$terminal.pointerId -ne $holdingId -or
            [long]$terminal.gestureEpoch -ne $holdingEpoch -or -not [bool]$terminal.isTouch) {
            throw 'QueuedTerminal contains a mixed-device, mixed-owner, or stale-epoch terminal.'
        }
    }
    if ([int]$AwaitingReceipt.sequence -ne $ExpectedSequence -or [string]$AwaitingReceipt.phase -cne 'awaitingFreshContact' -or
        [long]$AwaitingReceipt.gestureEpoch -ne $holdingEpoch -or [uint32]$AwaitingReceipt.pointerId -ne $holdingId -or
        [bool]$AwaitingReceipt.workerOwnsSnapshot -or
        $null -eq $AwaitingReceipt.queuedTerminal -or $null -eq $AwaitingReceipt.afterDrop -or
        (Get-DeferredRangeFingerprint $AwaitingReceipt.queuedTerminal.selectionRange) -cne (Get-DeferredRangeFingerprint $DroppedReceipt.queuedTerminal.selectionRange) -or
        (Get-DeferredRangeFingerprint $AwaitingReceipt.afterDrop.selectionRange) -cne (Get-DeferredRangeFingerprint $DroppedReceipt.afterDrop.selectionRange)) {
        throw 'The awaiting-fresh-contact receipt did not retain the exact same owner, sequence, queued terminal, and post-drop selection.'
    }
    $after = $DroppedReceipt.afterDrop
    if ([uint32]$after.activePointerId -ne 0 -or [long]$after.gestureEpoch -ne 0 -or
        [int]$after.queuedCount -ne 0 -or [int]$after.captureCount -ne 0 -or
        [bool]$after.retryTimerExists -or [bool]$after.isHandleDragging -or
        ($canceled -and [bool]$after.selectionContextMenuOpen)) {
        throw 'AfterDrop left a pointer owner, deferred queue, capture, retry timer, handle drag, or canceled selection flyout alive.'
    }
    if ($canceled -and $hadSelection) {
        if (-not [bool]$after.selectionIsActive -or
            (Get-DeferredRangeFingerprint $after.selectionRange) -cne (Get-DeferredRangeFingerprint $ArmedReceipt.preContact.selectionRange) -or
            [string]$AfterDropUiState.selection -cne [string]$InitialUiState.selection) {
            throw 'CancelExisting did not preserve the exact pre-contact logical range and real UIA selected text.'
        }
    }
    elseif (-not $canceled) {
        if (-not [bool]$after.selectionIsActive -or
            (Get-DeferredRangeFingerprint $after.selectionRange) -cne (Get-DeferredRangeFingerprint $HoldingReceipt.beforeTerminal.selectionRange) -or
            [string]$AfterDropUiState.selection -cne [string]$BeforeTerminalUiState.selection) {
            throw 'Release did not preserve the before-terminal logical range and real UIA selected text.'
        }
    }
    else {
        if ([bool]$after.selectionIsActive -or [int]$after.visibleHandleCount -ne 0 -or
            -not (Test-DeferredTouchClearedSelection -State $AfterDropUiState)) {
            throw 'CancelNew did not retire the temporary first-contact selection and its visible handles.'
        }
    }

    $freshState = $FreshReceipt.freshContact
    if ($null -eq $freshState) { throw 'FreshContact receipt is missing.' }
    Assert-DeferredTouchStateProperties -State $freshState
    if ([uint32]$freshState.activePointerId -eq 0 -or [uint32]$freshState.activePointerId -ne $FreshRoutedPointerId -or
        [uint32]$FreshRoutedPointerId -eq 0 -or [long]$FreshContactSerial -le 0 -or
        [long]$freshState.gestureEpoch -le 0 -or [long]$freshState.gestureEpoch -eq $holdingEpoch) {
        throw 'FreshContact lacks independently observed new routed-contact provenance or a distinct gesture epoch.'
    }
    if ($null -eq $CompleteReceipt.freshContact -or
        (Get-DeferredRangeFingerprint $CompleteReceipt.freshContact.selectionRange) -cne (Get-DeferredRangeFingerprint $freshState.selectionRange) -or
        [uint32]$CompleteReceipt.freshContact.activePointerId -ne $FreshRoutedPointerId -or
        [long]$CompleteReceipt.freshContact.gestureEpoch -ne [long]$freshState.gestureEpoch) {
        throw 'The complete receipt does not preserve the exact accepted fresh-contact owner/epoch captured earlier.'
    }
    if ([uint32]$CompleteReceipt.afterDrop.activePointerId -ne 0 -or [long]$CompleteReceipt.afterDrop.gestureEpoch -ne 0 -or
        [int]$CompleteReceipt.afterDrop.queuedCount -ne 0 -or [int]$CompleteReceipt.afterDrop.captureCount -ne 0 -or
        [bool]$CompleteReceipt.afterDrop.retryTimerExists) {
        throw 'The final completion receipt left the original deferred gesture unretired.'
    }
    if ([int]$FinalUiState.rangeCount -ne 1 -or [string]$FinalUiState.selection -cne 'freshword') {
        throw 'The independently observed final UIA selection is not exactly freshword.'
    }
    $handles = @($FinalUiState.handles)
    $handleIds = @($handles | ForEach-Object { [string]$_.automationId } | Sort-Object -Unique)
    if ($handles.Count -ne 2 -or $handleIds.Count -ne 2 -or
        $handleIds -notcontains 'MarkdownSelectionStartHandle' -or $handleIds -notcontains 'MarkdownSelectionEndHandle' -or
        @($handles | Where-Object { [bool]$_.offscreen -or [double]$_.width -le 0 -or [double]$_.height -le 0 }).Count -ne 0) {
        throw 'The final real UIA selection did not expose both unique, visible positive-size selection handles.'
    }
    return [pscustomobject]@{status='PASS';scenario=$ExpectedScenario;viewport=$ExpectedViewport;sequence=$ExpectedSequence;firstPointerId=$FirstRoutedPointerId;firstContactSerial=$FirstContactSerial;freshPointerId=$FreshRoutedPointerId;freshContactSerial=$FreshContactSerial;selectedText=[string]$FinalUiState.selection}
}

function Assert-DeferredTouchRoutedContact {
    param(
        [Parameter(Mandatory)]$Before,
        [Parameter(Mandatory)]$After,
        [Parameter(Mandatory)]$ExpectedPoint,
        [Parameter(Mandatory)][uint32]$PointerId,
        [Parameter(Mandatory)][long]$ContactSerial
    )
    if ([int]$Before.version -ne 1 -or [int]$After.version -ne 1 -or
        [string]$Before.observationStatus -cne 'ok' -or [string]$After.observationStatus -cne 'ok' -or
        [int]$Before.observationErrors -ne 0 -or [int]$After.observationErrors -ne 0 -or
        [long]$Before.rendererGeneration -le 0 -or [long]$Before.rendererGeneration -ne [long]$After.rendererGeneration -or
        $PointerId -eq 0 -or $ContactSerial -le 0) {
        throw 'The actual routed-contact observer was degraded, changed renderer generation, or omitted OS pointer provenance.'
    }
    $beforeRecords = @($Before.contacts | Where-Object { [uint32]$_.pointerId -eq $PointerId })
    if ($beforeRecords.Count -gt 1) { throw 'The baseline contact observer has duplicate OS pointer IDs.' }
    $record = Get-TouchContactRecord -Observation $After -PointerId $PointerId
    $active = @($After.active | Where-Object { [uint32]$_.pointerId -eq $PointerId -and [long]$_.contactSerial -eq $ContactSerial })
    $prior = if ($beforeRecords.Count -eq 1) { $beforeRecords[0] } else { [pscustomobject]@{pressCount=0;releaseCount=0;cancelCount=0;captureLostCount=0;lastEventSequence=0} }
    $expected = ConvertTo-ExpectedRootClientPoint -ExpectedScreenPoint $ExpectedPoint -ObservedContact $record
    $distance = [Math]::Sqrt([Math]::Pow(([double]$record.lastPositionRootDipX-[double]$expected.x),2)+[Math]::Pow(([double]$record.lastPositionRootDipY-[double]$expected.y),2))
    if ($active.Count -ne 1 -or [long]$record.contactSerial -ne $ContactSerial -or -not [bool]$record.inContact -or
        [string]$record.lastEvent -notin @('Pressed','Moved') -or [long]$record.lastEventSequence -le [long]$prior.lastEventSequence -or
        [int]$record.pressCount -ne ([int]$prior.pressCount+1) -or [int]$record.releaseCount -ne [int]$prior.releaseCount -or
        [int]$record.cancelCount -ne [int]$prior.cancelCount -or [int]$record.captureLostCount -ne [int]$prior.captureLostCount -or
        [double]::IsNaN($distance) -or [double]::IsInfinity($distance) -or $distance -gt 12.0) {
        throw "OS-routed touch $PointerId/$ContactSerial is not the same live, correctly positioned press/move contact (distance=$distance DIPs)."
    }
    return $record
}

function Find-DeferredContactCandidate {
    param([Parameter(Mandatory)]$Before,[Parameter(Mandatory)]$After)
    foreach ($record in @($After.contacts)) {
        $prior = @($Before.contacts | Where-Object { [uint32]$_.pointerId -eq [uint32]$record.pointerId })
        if ($prior.Count -gt 1) { throw "Observer baseline duplicated OS pointer $($record.pointerId)." }
        if (($prior.Count -eq 0 -and [int]$record.pressCount -eq 1 -and [long]$record.contactSerial -gt 0) -or
            ($prior.Count -eq 1 -and [int]$record.pressCount -eq ([int]$prior[0].pressCount+1) -and [long]$record.contactSerial -gt [long]$prior[0].contactSerial)) {
            return [pscustomobject]@{pointerId=[uint32]$record.pointerId;contactSerial=[long]$record.contactSerial}
        }
    }
    return $null
}

function Start-DeferredTouchInputPump {
    param([Parameter(Mandatory)][int]$X,[Parameter(Mandatory)][int]$Y)
    if ($null -ne $script:TouchPump) { throw 'A synthetic touch input pump is already active.' }
    $commands = [Collections.Concurrent.ConcurrentQueue[object]]::new()
    $events = [Collections.Concurrent.ConcurrentQueue[object]]::new()
    $signal = [Threading.AutoResetEvent]::new($false)
    $runspace = [System.Management.Automation.Runspaces.RunspaceFactory]::CreateRunspace()
    $runspace.Open()
    $powerShell = [PowerShell]::Create()
    $powerShell.Runspace = $runspace
    $worker = {
        param($Device,[int]$TargetPid,[long]$TargetHwnd,[int]$StartX,[int]$StartY,$Commands,$Signal,$Events)
        $inContact = $false
        $maxGapMs = 0.0
        $updateCount = 0
        $terminalKind = $null
        $stopwatchFrequency = [Diagnostics.Stopwatch]::Frequency
        $lastSentTick = 0L
        $contactDeadlineTick = 0L
        try {
            $window = [MarkdownDeferredTouchForegroundNative]::Read()
            [MarkdownCorrectnessSandboxNative]::VerifyWindow([IntPtr]$TargetHwnd,$TargetPid)
            if ([long]$window.hwnd -ne $TargetHwnd -or [uint32]$window.processId -ne [uint32]$TargetPid) {
                throw 'The exact deferred-touch app is not foreground at contact start.'
            }
            $lastSentTick = [Diagnostics.Stopwatch]::GetTimestamp()
            [MarkdownCorrectnessSandboxNative]::Send($Device,[uint32]0,$StartX,$StartY,
                ([McPointerFlags]::InRange -bor [McPointerFlags]::InContact -bor [McPointerFlags]::Down))
            $inContact = $true
            $contactDeadlineTick = [Diagnostics.Stopwatch]::GetTimestamp() + (20L * $stopwatchFrequency)
            $Events.Enqueue([pscustomobject]@{kind='started';utc=[DateTime]::UtcNow.ToString('o')})
            while ($inContact) {
                if ([Diagnostics.Stopwatch]::GetTimestamp() -ge $contactDeadlineTick) {
                    $window = [MarkdownDeferredTouchForegroundNative]::Read()
                    [MarkdownCorrectnessSandboxNative]::VerifyWindow([IntPtr]$TargetHwnd,$TargetPid)
                    if ([long]$window.hwnd -ne $TargetHwnd -or [uint32]$window.processId -ne [uint32]$TargetPid) {
                        throw 'The hard contact deadline elapsed after the exact app stopped being foreground; refusing background cancellation.'
                    }
                    $sentTick = [Diagnostics.Stopwatch]::GetTimestamp()
                    $gapMs = (($sentTick - $lastSentTick) * 1000.0) / $stopwatchFrequency
                    if ($gapMs -gt $maxGapMs) { $maxGapMs = $gapMs }
                    [MarkdownCorrectnessSandboxNative]::Send($Device,[uint32]0,$StartX,$StartY,([McPointerFlags]::Up -bor [McPointerFlags]::Canceled))
                    $lastSentTick = $sentTick
                    $inContact = $false
                    $terminalKind = 'hard-deadline-cancel'
                    throw "The touch client exceeded the independent 20-second contact deadline (maximum observed frame gap $([Math]::Round($maxGapMs,2)) ms)."
                }
                $command = $null
                if ($Commands.TryDequeue([ref]$command)) {
                    if ($null -eq $command -or [string]$command.kind -notin @('release','cancel')) {
                        throw 'The touch pump received an unknown terminal command.'
                    }
                    $window = [MarkdownDeferredTouchForegroundNative]::Read()
                    [MarkdownCorrectnessSandboxNative]::VerifyWindow([IntPtr]$TargetHwnd,$TargetPid)
                    if ([long]$window.hwnd -ne $TargetHwnd -or [uint32]$window.processId -ne [uint32]$TargetPid) {
                        throw 'Refusing terminal touch because the exact deferred-touch app is no longer foreground.'
                    }
                    $flags = if ([string]$command.kind -ceq 'cancel') {
                        [McPointerFlags]::Up -bor [McPointerFlags]::Canceled
                    } else { [McPointerFlags]::Up }
                    $sentTick = [Diagnostics.Stopwatch]::GetTimestamp()
                    $gapMs = (($sentTick - $lastSentTick) * 1000.0) / $stopwatchFrequency
                    if ($gapMs -gt $maxGapMs) { $maxGapMs = $gapMs }
                    [MarkdownCorrectnessSandboxNative]::Send($Device,[uint32]0,$StartX,$StartY,$flags)
                    $lastSentTick = $sentTick
                    $inContact = $false
                    $terminalKind = [string]$command.kind
                    if ($gapMs -gt 80.0) { throw "Synthetic touch update gap was $([Math]::Round($gapMs,2)) ms before terminal (maximum 80 ms)." }
                    break
                }
                if (-not $Signal.WaitOne(40)) {
                    $window = [MarkdownDeferredTouchForegroundNative]::Read()
                    [MarkdownCorrectnessSandboxNative]::VerifyWindow([IntPtr]$TargetHwnd,$TargetPid)
                    if ([long]$window.hwnd -ne $TargetHwnd -or [uint32]$window.processId -ne [uint32]$TargetPid) {
                        throw 'The exact deferred-touch app stopped being foreground during the contact heartbeat.'
                    }
                    $sentTick = [Diagnostics.Stopwatch]::GetTimestamp()
                    $gapMs = (($sentTick - $lastSentTick) * 1000.0) / $stopwatchFrequency
                    if ($gapMs -gt $maxGapMs) { $maxGapMs = $gapMs }
                    [MarkdownCorrectnessSandboxNative]::Send($Device,[uint32]0,$StartX,$StartY,
                        ([McPointerFlags]::InRange -bor [McPointerFlags]::InContact -bor [McPointerFlags]::Update))
                    $lastSentTick = $sentTick
                    $updateCount++
                    if ($gapMs -gt 80.0) { throw "Synthetic touch update gap was $([Math]::Round($gapMs,2)) ms (maximum 80 ms)." }
                }
            }
            $Events.Enqueue([pscustomobject]@{kind='complete';terminal=$terminalKind;updates=$updateCount;maxGapMilliseconds=$maxGapMs})
        }
        catch {
            $errorText = $_.Exception.ToString()
            if ($inContact) {
                try {
                    $window = [MarkdownDeferredTouchForegroundNative]::Read()
                    [MarkdownCorrectnessSandboxNative]::VerifyWindow([IntPtr]$TargetHwnd,$TargetPid)
                    if ([long]$window.hwnd -ne $TargetHwnd -or [uint32]$window.processId -ne [uint32]$TargetPid) {
                        throw 'The exact app is no longer foreground during worker failure cleanup.'
                    }
                    [MarkdownCorrectnessSandboxNative]::Send($Device,[uint32]0,$StartX,$StartY,([McPointerFlags]::Up -bor [McPointerFlags]::Canceled))
                    $inContact = $false
                    $terminalKind = 'safe-failure-cancel'
                }
                catch { $errorText += [Environment]::NewLine + 'Worker canceled-Up cleanup failed: ' + $_.Exception.ToString() }
            }
            $Events.Enqueue([pscustomobject]@{kind='error';message=$errorText;terminal=$terminalKind;updates=$updateCount;maxGapMilliseconds=$maxGapMs;inContact=$inContact})
        }
    }
    $null = $powerShell.AddScript($worker.ToString()).AddArgument($script:TouchDevice).AddArgument($ProcessId).AddArgument($WindowHandle).AddArgument($X).AddArgument($Y).AddArgument($commands).AddArgument($signal).AddArgument($events)
    # A null pump reference alone is not join evidence if startup throws.
    # Retain that uncertainty until the worker has positively been joined.
    $script:TouchPumpJoined = $false
    $async = $powerShell.BeginInvoke()
    $script:TouchPump = [pscustomobject]@{commands=$commands;events=$events;signal=$signal;runspace=$runspace;powerShell=$powerShell;async=$async}
    $script:TouchMayBeInContact = $true
    $script:LastTouchPoint = [pscustomobject]@{x=$X;y=$Y}
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    while ([DateTime]::UtcNow -lt $deadline) {
        $event = $null
        while ($script:TouchPump.events.TryDequeue([ref]$event)) {
            if ([string]$event.kind -ceq 'started') {
                $script:TouchInContact = $true
                return [pscustomobject]@{startedUtc=[string]$event.utc}
            }
            if ([string]$event.kind -ceq 'error') {
                $script:TouchPump.events.Enqueue($event)
                $script:TouchInContact = [bool]$event.inContact
                $cleanupError = $null
                try { $null = Stop-DeferredTouchInputPump -Terminal cancel }
                catch { $cleanupError = $_.Exception.ToString() }
                throw "Touch heartbeat failed before startup: $($event.message)$(if($cleanupError){' Cleanup: '+$cleanupError}else{''})"
            }
        }
        Start-Sleep -Milliseconds 10
    }
    $cleanupError = $null
    try { $null = Stop-DeferredTouchInputPump -Terminal cancel }
    catch { $cleanupError = $_.Exception.ToString() }
    throw "The guest touch heartbeat did not confirm its actual Down frame within three seconds.$(if($cleanupError){' Cleanup: '+$cleanupError}else{''})"
}

function Stop-DeferredTouchInputPump {
    param([Parameter(Mandatory)][ValidateSet('release','cancel')][string]$Terminal)
    $pump = $script:TouchPump
    if ($null -eq $pump) { throw 'No synthetic touch input pump is active.' }
    $pump.commands.Enqueue([pscustomobject]@{kind=$Terminal})
    $null = $pump.signal.Set()
    if (-not $pump.async.AsyncWaitHandle.WaitOne(3000)) {
        try { $pump.powerShell.Stop() } catch { }
        if (-not $pump.async.AsyncWaitHandle.WaitOne(3000)) {
            throw 'The touch worker could not be stopped and joined within the bounded cleanup window; its device is retained and will not be destroyed concurrently.'
        }
    }
    $pipelineError = $null
    try { $null = $pump.powerShell.EndInvoke($pump.async) }
    catch { $pipelineError = $_.Exception.ToString() }
    $complete = $null
    $errorEvent = $null
    $event = $null
    while ($pump.events.TryDequeue([ref]$event)) {
        if ([string]$event.kind -ceq 'complete') { $complete = $event }
        elseif ([string]$event.kind -ceq 'error') { $errorEvent = $event }
    }
    if ($null -ne $pipelineError -and $null -eq $errorEvent) {
        $errorEvent = [pscustomobject]@{kind='error';message=$pipelineError;inContact=([bool]$script:TouchMayBeInContact -or [bool]$script:TouchInContact)}
    }
    $pump.powerShell.Dispose()
    $pump.runspace.Dispose()
    $pump.signal.Dispose()
    $script:TouchPump = $null
    $script:TouchPumpJoined = $true
    if ($null -ne $errorEvent) {
        if ([bool]$errorEvent.inContact) {
            try {
                Assert-DeferredProbeIdentity | Out-Null
                Assert-DeferredProbeForeground | Out-Null
                [MarkdownCorrectnessSandboxNative]::Send($script:TouchDevice,[uint32]0,[int]$script:LastTouchPoint.x,[int]$script:LastTouchPoint.y,
                    ([McPointerFlags]::Up -bor [McPointerFlags]::Canceled))
                $script:TouchInContact = $false
                $script:TouchMayBeInContact = $false
            }
            catch { $errorEvent.message += [Environment]::NewLine + 'Safe canceled-Up cleanup failed: ' + $_.Exception.ToString() }
        }
        else { $script:TouchInContact = $false; $script:TouchMayBeInContact = $false }
        throw "The synthetic touch heartbeat failed: $($errorEvent.message)"
    }
    $script:TouchInContact = $false
    $script:TouchMayBeInContact = $false
    if ($null -eq $complete -or [string]$complete.terminal -cne $Terminal -or [int]$complete.updates -lt 1 -or
        [double]$complete.maxGapMilliseconds -gt 80.0) {
        throw 'The touch heartbeat did not provide bounded real frames and the exact requested terminal.'
    }
    return $complete
}

function Test-DeferredTouchPumpStopped {
    return ($null -eq $script:TouchPump -and $script:TouchPumpJoined)
}

function Send-DeferredTouchFrame {
    param([Parameter(Mandatory)][int]$X,[Parameter(Mandatory)][int]$Y,[Parameter(Mandatory)][McPointerFlags]$Flags)
    $downFlags = [McPointerFlags]::InRange -bor [McPointerFlags]::InContact -bor [McPointerFlags]::Down
    if ($Flags -eq $downFlags) {
        if ($script:TouchDevice -eq [IntPtr]::Zero) { throw 'The touch device has not been created.' }
        $result = Start-DeferredTouchInputPump -X $X -Y $Y
        $script:TouchInContact = $true
        return $result
    }
    if ($Flags -eq [McPointerFlags]::Up -or $Flags -eq ([McPointerFlags]::Up -bor [McPointerFlags]::Canceled)) {
        if (-not $script:TouchInContact) { throw 'Refusing to inject Up without the matching active touch contact.' }
        $terminal = if (($Flags -band [McPointerFlags]::Canceled) -ne 0) { 'cancel' } else { 'release' }
        $result = Stop-DeferredTouchInputPump -Terminal $terminal
        $script:LastTouchPoint = [pscustomobject]@{x=$X;y=$Y}
        return $result
    }
    throw 'Only real Down, ordinary Up alone, or Up|Canceled are accepted; the pump owns all stationary updates.'
}

function Select-DeferredTouchTextPoint {
    # A text contact must land on the word and not on a visible selection
    # handle: handle touch targets extend beyond the selected line, and a
    # press there starts a handle drag (HandleRelease), not a text contact.
    # Returns the first candidate inside the word and renderer that is outside
    # every visible handle (plus a margin), or throws when none exists.
    param(
        [Parameter(Mandatory)]$WordRect,
        [Parameter(Mandatory)]$RendererRect,
        [AllowEmptyCollection()][object[]]$Handles = @(),
        [double]$HandleMargin = 4
    )
    $wordX = [double]$WordRect.X; $wordY = [double]$WordRect.Y
    $wordWidth = [double]$WordRect.Width; $wordHeight = [double]$WordRect.Height
    if ($wordWidth -le 0 -or $wordHeight -le 0) { throw 'The text target has no visible geometry.' }
    $visibleHandles = @($Handles | Where-Object { -not [bool]$_.offscreen -and [double]$_.width -gt 0 -and [double]$_.height -gt 0 })
    foreach ($fy in @(0.5, 0.35, 0.65)) {
        foreach ($fx in @(0.5, 0.65, 0.35, 0.8, 0.2, 0.9, 0.1)) {
            $x = [int][Math]::Round($wordX + $wordWidth * $fx)
            $y = [int][Math]::Round($wordY + $wordHeight * $fy)
            if ($x -lt [double]$RendererRect.x -or $x -gt ([double]$RendererRect.x + [double]$RendererRect.width) -or
                $y -lt [double]$RendererRect.y -or $y -gt ([double]$RendererRect.y + [double]$RendererRect.height)) { continue }
            $onHandle = @($visibleHandles | Where-Object {
                $x -ge ([double]$_.x - $HandleMargin) -and $x -le ([double]$_.x + [double]$_.width + $HandleMargin) -and
                $y -ge ([double]$_.y - $HandleMargin) -and $y -le ([double]$_.y + [double]$_.height + $HandleMargin)
            }).Count -ne 0
            if (-not $onHandle) { return [pscustomobject]@{ x = $x; y = $y; fractionX = $fx; fractionY = $fy } }
        }
    }
    throw 'Every candidate point on the text target is covered by a visible selection handle or lies outside the renderer viewport.'
}

function Start-DeferredTouchAtText {
    param(
        [Parameter(Mandatory)][ValidateSet('deferredword','freshword')][string]$Text,
        [Parameter(Mandatory)][ValidateSet('holding','freshContactAccepted')][string]$RequiredPhase,
        [Parameter(Mandatory)][int]$Sequence
    )
    $range = Get-Range $Text
    if ($range.text -cne $Text -or $range.rect.IsEmpty -or $range.rect.Width -le 0 -or $range.rect.Height -le 0) {
        throw "The actual UIA TextPattern range for '$Text' was not exact and visibly hittable."
    }
    $state = Get-DeferredTouchUiState -RequestedViewport $Viewport
    # P1-005: a committed selection's start handle can cover the next line's
    # word center; touch the word where no visible handle is.
    $textPoint = Select-DeferredTouchTextPoint -WordRect $range.rect -RendererRect $state.rendererRect -Handles @($state.handles)
    $screenX = [int]$textPoint.x
    $screenY = [int]$textPoint.y
    $expectedPoint = Get-ExpectedScreenTouchPoint -ScreenX $screenX -ScreenY $screenY
    $expectedInitialPhase = if ($RequiredPhase -eq 'holding') { 'armed' } else { 'awaitingFreshContact' }
    $preContactReceipt = Get-DeferredTouchReceipt
    if ([int]$preContactReceipt.version -ne 1 -or [int]$preContactReceipt.sequence -ne $Sequence -or
        [string]$preContactReceipt.phase -cne $expectedInitialPhase -or
        -not [string]::IsNullOrEmpty([string]$preContactReceipt.failure) -or [bool]$preContactReceipt.workerOwnsSnapshot) {
        throw "A real touch on '$Text' requires the same-sequence '$expectedInitialPhase' state with no failure or active worker lease."
    }
    $baseline = Get-TouchContactObservation
    Assert-DeferredProbeIdentity | Out-Null
    Assert-DeferredProbeForeground | Out-Null
    $immediateReceipt = Get-DeferredTouchReceipt
    if ([int]$immediateReceipt.version -ne [int]$preContactReceipt.version -or
        [int]$immediateReceipt.sequence -ne [int]$preContactReceipt.sequence -or
        [string]$immediateReceipt.phase -cne $expectedInitialPhase -or [bool]$immediateReceipt.workerOwnsSnapshot) {
        throw "The deferred-touch phase changed while resolving '$Text'; refusing stale-coordinate input."
    }
    $null = Send-DeferredTouchFrame -X $screenX -Y $screenY -Flags ([McPointerFlags]::InRange -bor [McPointerFlags]::InContact -bor [McPointerFlags]::Down)
    $deadline = [DateTime]::UtcNow.AddSeconds(9)
    $routed = $null
    $lastObserver = $null
    $lastReceipt = $null
    $lastUiState = $null
    do {
        $lastObserver = Get-TouchContactObservation
        if ($null -eq $routed) {
            $candidate = Find-DeferredContactCandidate -Before $baseline -After $lastObserver
            if ($null -ne $candidate) {
                $routed = Find-NewTouchContact -Before $baseline -After $lastObserver -ExpectedPoint $expectedPoint
                if ([uint32]$routed.pointerId -ne [uint32]$candidate.pointerId -or [long]$routed.contactSerial -ne [long]$candidate.contactSerial) {
                    throw 'The shared contact oracle and candidate provenance disagreed.'
                }
            }
        }
        else {
            $null = Assert-DeferredTouchRoutedContact -Before $baseline -After $lastObserver -ExpectedPoint $expectedPoint -PointerId ([uint32]$routed.pointerId) -ContactSerial ([long]$routed.contactSerial)
        }
        $lastReceipt = Get-DeferredTouchReceipt
        if ([int]$lastReceipt.sequence -ne $Sequence -or [int]$lastReceipt.version -ne 1) {
            throw "The actual-control receipt changed sequence or schema while waiting for '$RequiredPhase'."
        }
        if ([string]$lastReceipt.phase -ceq 'failed' -or -not [string]::IsNullOrWhiteSpace([string]$lastReceipt.failure)) {
            throw "The actual-control deferred-touch probe failed while waiting for '$RequiredPhase': $($lastReceipt.failure)"
        }
        if ($RequiredPhase -eq 'holding' -and [string]$lastReceipt.phase -ceq 'holding' -and $null -ne $routed) {
            if ([uint32]$lastReceipt.pointerId -ne [uint32]$routed.pointerId -or [long]$lastReceipt.gestureEpoch -le 0) {
                throw 'The renderer-held owner differs from the OS-routed contact identity.'
            }
            return [pscustomobject]@{text=$Text;range=$range;screenPoint=@{x=$screenX;y=$screenY};expectedPoint=$expectedPoint;preContactReceipt=$preContactReceipt;baseline=$baseline;observer=$lastObserver;routed=$routed;receipt=$lastReceipt;uiState=$null}
        }
        if ($RequiredPhase -eq 'freshContactAccepted' -and [string]$lastReceipt.phase -ceq 'freshContactAccepted' -and $null -ne $routed) {
            $lastUiState = Get-DeferredTouchUiState -RequestedViewport $Viewport
            if ([uint32]$lastReceipt.freshContact.activePointerId -ne [uint32]$routed.pointerId -or
                [long]$lastReceipt.freshContact.gestureEpoch -le 0 -or
                [long]$lastReceipt.freshContact.gestureEpoch -eq [long]$lastReceipt.gestureEpoch) {
                throw 'FreshContact receipt did not identify the real new OS pointer and gesture epoch.'
            }
            if ([int]$lastUiState.rangeCount -eq 1 -and [string]$lastUiState.selection -ceq 'freshword') {
                return [pscustomobject]@{text=$Text;range=$range;screenPoint=@{x=$screenX;y=$screenY};expectedPoint=$expectedPoint;preContactReceipt=$preContactReceipt;baseline=$baseline;observer=$lastObserver;routed=$routed;receipt=$lastReceipt;uiState=$lastUiState}
            }
        }
        Start-Sleep -Milliseconds 30
    } while ([DateTime]::UtcNow -lt $deadline)
    $phase = if ($null -eq $lastReceipt) { '<none>' } else { [string]$lastReceipt.phase }
    $selection = if ($null -eq $lastUiState) { '<not sampled>' } else { [string]$lastUiState.selection }
    throw "Actual touch on '$Text' did not reach '$RequiredPhase' with the expected live UIA selection (phase='$phase', selection='$selection')."
}

function Start-DeferredTouchSelectionSeed {
    param([Parameter(Mandatory)][ValidateSet('committedword')][string]$Text)
    $range = Get-Range $Text
    if ($range.text -cne $Text -or $range.rect.IsEmpty -or $range.rect.Width -le 0 -or $range.rect.Height -le 0) {
        throw 'The actual UIA TextPattern range for the seed word is not exact and visibly hittable.'
    }
    $state = Get-DeferredTouchUiState -RequestedViewport $Viewport
    # T1-024: TextPattern may report one collapsed, rectangle-free caret range
    # when nothing is selected (T1-003); use the same cleared-selection proof.
    if (-not (Test-DeferredTouchClearedSelection -State $state)) {
        throw 'The seed gesture requires a genuinely empty initial UIA selection; refusing to replace an existing range.'
    }
    $screenX = [int][Math]::Round($range.rect.X + $range.rect.Width / 2.0)
    $screenY = [int][Math]::Round($range.rect.Y + $range.rect.Height / 2.0)
    if ($screenX -lt [double]$state.rendererRect.x -or $screenX -gt ([double]$state.rendererRect.x+[double]$state.rendererRect.width) -or
        $screenY -lt [double]$state.rendererRect.y -or $screenY -gt ([double]$state.rendererRect.y+[double]$state.rendererRect.height)) {
        throw 'The actual UIA seed-word center is outside the selected renderer viewport.'
    }
    $expectedPoint = Get-ExpectedScreenTouchPoint -ScreenX $screenX -ScreenY $screenY
    $baseline = Get-TouchContactObservation
    Assert-DeferredProbeIdentity | Out-Null
    Assert-DeferredProbeForeground | Out-Null
    $null = Send-DeferredTouchFrame -X $screenX -Y $screenY -Flags ([McPointerFlags]::InRange -bor [McPointerFlags]::InContact -bor [McPointerFlags]::Down)
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    $routed = $null
    $lastObserver = $null
    $lastUiState = $null
    do {
        $lastObserver = Get-TouchContactObservation
        if ($null -eq $routed) {
            $candidate = Find-DeferredContactCandidate -Before $baseline -After $lastObserver
            if ($null -ne $candidate) {
                $routed = Find-NewTouchContact -Before $baseline -After $lastObserver -ExpectedPoint $expectedPoint
                if ([uint32]$routed.pointerId -ne [uint32]$candidate.pointerId -or [long]$routed.contactSerial -ne [long]$candidate.contactSerial) {
                    throw 'The shared touch oracle and seed-contact provenance disagreed.'
                }
            }
        }
        else {
            $null = Assert-DeferredTouchRoutedContact -Before $baseline -After $lastObserver -ExpectedPoint $expectedPoint `
                -PointerId ([uint32]$routed.pointerId) -ContactSerial ([long]$routed.contactSerial)
        }
        $lastUiState = Get-DeferredTouchUiState -RequestedViewport $Viewport
        if ($null -ne $routed -and [int]$lastUiState.rangeCount -eq 1 -and [string]$lastUiState.selection -ceq $Text) {
            $holdingState = $lastUiState
            $terminal = Send-DeferredTouchFrame -X $screenX -Y $screenY -Flags ([McPointerFlags]::Up)
            $script:TouchInContact = $false
            $terminalObserver = Wait-DeferredTerminalObservation -Contact ([pscustomobject]@{observer=$lastObserver;routed=$routed}) `
                -Canceled $false -TimeoutMilliseconds 2500
            $after = Get-DeferredTouchUiState -RequestedViewport $Viewport
            $null = Assert-DeferredTouchSelection -State $after -ExpectedSelected $true -ExpectedText $Text -Stage 'Seed selection after ordinary Up'
            $flyout = Dismiss-DeferredTouchSelectionFlyout -RequestedViewport $Viewport -ExpectedSelection $Text -Stage 'Seed selection'
            $afterFlyout = Get-DeferredTouchUiState -RequestedViewport $Viewport
            $null = Assert-DeferredTouchSelection -State $afterFlyout -ExpectedSelected $true -ExpectedText $Text -Stage 'Seed selection after flyout handling'
            return [pscustomobject]@{
                text=$Text;range=$range;screenPoint=@{x=$screenX;y=$screenY};expectedPoint=$expectedPoint;baseline=$baseline
                observer=$lastObserver;routed=$routed;holdingState=$holdingState;terminal=$terminal;terminalObserver=$terminalObserver
                afterSelection=$after;flyout=$flyout;afterFlyout=$afterFlyout
            }
        }
        Start-Sleep -Milliseconds 30
    } while ([DateTime]::UtcNow -lt $deadline)
    $selection = if ($null -eq $lastUiState) { '<not sampled>' } else { [string]$lastUiState.selection }
    throw "A real routed touch on '$Text' did not commit the exact seed selection (selection='$selection')."
}

function Wait-DeferredTerminalObservation {
    param([Parameter(Mandatory)]$Contact,[Parameter(Mandatory)][bool]$Canceled,[Parameter(Mandatory)][int]$TimeoutMilliseconds)
    $recordBefore = Get-TouchContactRecord -Observation $Contact.observer -PointerId ([uint32]$Contact.routed.pointerId)
    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    $last = $null
    do {
        $last = Get-TouchContactObservation
        $record = Get-TouchContactRecord -Observation $last -PointerId ([uint32]$Contact.routed.pointerId)
        if ([int]$last.version -ne 1 -or [string]$last.observationStatus -cne 'ok' -or [int]$last.observationErrors -ne 0 -or
            [long]$last.rendererGeneration -le 0 -or [long]$last.rendererGeneration -ne [long]$Contact.observer.rendererGeneration -or
            [long]$record.contactSerial -ne [long]$Contact.routed.contactSerial -or
            [long]$record.lastEventSequence -le [long]$recordBefore.lastEventSequence) {
            throw 'The terminal observer became unhealthy, changed renderer/contact identity, or omitted a new routed event.'
        }
        $complete = if ($Canceled) {
            -not [bool]$record.inContact -and [int]$record.cancelCount -eq ([int]$recordBefore.cancelCount+1) -and
                [int]$record.releaseCount -eq [int]$recordBefore.releaseCount -and
                [int]$record.captureLostCount -ge [int]$recordBefore.captureLostCount -and
                [int]$record.captureLostCount -le ([int]$recordBefore.captureLostCount+1) -and
                [string]$record.lastEvent -in @('Canceled','CaptureLost')
        }
        else {
            -not [bool]$record.inContact -and [int]$record.releaseCount -eq ([int]$recordBefore.releaseCount+1) -and
                [int]$record.cancelCount -eq [int]$recordBefore.cancelCount -and
                [int]$record.captureLostCount -ge [int]$recordBefore.captureLostCount -and
                [int]$record.captureLostCount -le ([int]$recordBefore.captureLostCount+1) -and
                [string]$record.lastEvent -in @('Released','CaptureLost')
        }
        if ($complete) { return $last }
        # P1-031: an injected POINTER_FLAG_CANCELED|UP that reaches WinUI as one
        # ordinary release without a native canceled packet is a delivery limit.
        if ($Canceled -and -not [bool]$record.inContact -and [string]$record.lastEvent -ceq 'Released' -and
            [int]$record.releaseCount -eq ([int]$recordBefore.releaseCount+1) -and [int]$record.cancelCount -eq [int]$recordBefore.cancelCount -and
            $null -ne $record.PSObject.Properties['nativeCanceledPacketCount'] -and $null -ne $recordBefore.PSObject.Properties['nativeCanceledPacketCount'] -and
            [int]$record.nativeCanceledPacketCount -eq [int]$recordBefore.nativeCanceledPacketCount -and -not [bool]$record.lastNativeCanceled) {
            throw ('INJECTED-CANCEL-NOT-NATIVE: the injected canceled up arrived as one ordinary routed release with no native canceled packet. Record: ' + (ConvertTo-Json -InputObject $record -Depth 4 -Compress))
        }
        Start-Sleep -Milliseconds 40
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The sample's actual routed event observer did not confirm the requested $(if($Canceled){'canceled'}else{'ordinary released'}) terminal for the same OS pointer/contact serial."
}

function Request-DeferredProbeActivation {
    # T1-023: a probe started with Win32_Process.Create (T1-017) is not
    # granted foreground. Activate only the exact identity-verified window
    # through UIA SetFocus, once, before any interaction. Every later
    # foreground check is unchanged and never re-activates mid-contact.
    # The probe overlaps the main sample window. Keep the exact test-only
    # probe above it so every injected contact lands on the probe and cannot
    # activate the main window mid-contact. The probe closes after each case.
    [MarkdownDeferredTouchForegroundNative]::PinTopmost([IntPtr]$WindowHandle)
    $foreground = [MarkdownDeferredTouchForegroundNative]::Read()
    if ([long]$foreground.hwnd -eq $WindowHandle -and [uint32]$foreground.processId -eq [uint32]$ProcessId) { return }
    # The window root is not focusable. Focus the viewport checkbox: focusing
    # it neither toggles it nor touches the renderer or its selection state.
    $target = Find-UniqueProcessElement $ProcessId 'DeferredTouchAncestorViewport'
    if ($null -eq $target -or ([int]$target.Current.ProcessId -ne $ProcessId -and [int]$target.Current.ProcessId -ne 0) -or
        [string]$target.Current.AutomationId -cne 'DeferredTouchAncestorViewport') {
        throw 'The exact deferred-touch window could not be resolved for activation.'
    }
    $target.SetFocus()
    $deadline = [DateTime]::UtcNow.AddSeconds(3)
    do {
        Start-Sleep -Milliseconds 100
        $foreground = [MarkdownDeferredTouchForegroundNative]::Read()
    } while (([long]$foreground.hwnd -ne $WindowHandle -or [uint32]$foreground.processId -ne [uint32]$ProcessId) -and [DateTime]::UtcNow -lt $deadline)
}

function Invoke-DeferredTouchScenario {
    if (-not (Test-Path -LiteralPath $script:DeferredProbePath -PathType Leaf)) { throw "The shared guest probe is not present beside this driver: $script:DeferredProbePath" }
    $script:Identity = Assert-DeferredProbeIdentity
    Request-DeferredProbeActivation
    $null = Assert-DeferredProbeForeground
    $root = Get-Root
    if ([int]$root.Current.ProcessId -ne $ProcessId -or [long]$root.Current.NativeWindowHandle -ne $WindowHandle -or
        [string]$root.Current.Name -cne 'MarkdownRenderer deferred-touch probe') {
        throw 'The exact-title deferred-touch probe UIA root changed or could not be proved.'
    }

    Set-DeferredTouchViewport -RequestedViewport $Viewport
    $preRunReceipt = Get-DeferredTouchReceipt
    if ([int]$preRunReceipt.version -ne 1 -or [string]$preRunReceipt.phase -notin @('idle','complete') -or
        -not [string]::IsNullOrEmpty([string]$preRunReceipt.failure) -or [bool]$preRunReceipt.workerOwnsSnapshot) {
        throw 'The actual-control probe is not idle/complete with no failed or worker-owned gesture.'
    }
    $script:Evidence.preRunReceipt = $preRunReceipt
    $sequence = [int]$preRunReceipt.sequence + 1
    $emptyUiState = Assert-DeferredTouchSelection -State (Get-DeferredTouchUiState -RequestedViewport $Viewport) `
        -ExpectedSelected $false -Stage 'Fresh-window UIA state before any seed gesture'
    $script:Evidence.emptyUiBeforeSeed = $emptyUiState
    $emptyMenu = @(Get-DeferredTouchSelectionMenuCommands)
    if ($emptyMenu.Count -ne 0) { throw 'The newly created probe window unexpectedly contains a selection flyout before any touch.' }
    Assert-DeferredProbeIdentity | Out-Null
    $script:TouchDevice = [MarkdownCorrectnessSandboxNative]::CreateTouchDevice()
    $initialUiState = $emptyUiState
    if ($Scenario -ne 'CancelNew') {
        $seed = Start-DeferredTouchSelectionSeed -Text 'committedword'
        $script:Evidence.selectionSeed = $seed
        $initialUiState = $seed.afterFlyout
    }
    else {
        $script:Evidence.selectionSeed = [pscustomobject]@{performed=$false;reason='CancelNew requires the initial empty window and does not fabricate or seed a prior range.'}
    }
    $initialUiState = Assert-DeferredTouchSelection -State $initialUiState -ExpectedSelected ($Scenario -ne 'CancelNew') `
        -ExpectedText $(if($Scenario -ne 'CancelNew'){'committedword'}else{''}) -Stage 'Initial UIA state after permitted actual-touch seeding'
    $script:Evidence.initialUiSelection = $initialUiState

    $button = if ($Scenario -eq 'Release') { 'DeferredTouchArmRelease' } else { 'DeferredTouchArmCancel' }
    Invoke-DeferredTouchPattern -AutomationId $button
    $armed = Wait-DeferredTouchPhase -Phase @('armed') -Sequence $sequence -TimeoutMilliseconds 1500
    $script:Evidence.armedReceipt = $armed
    if ([bool]$armed.hadSelectionBeforeContact -ne ($Scenario -ne 'CancelNew')) {
        throw 'The actual Arm click captured a different pre-contact selection state than the verified UIA state.'
    }

    $held = Start-DeferredTouchAtText -Text 'deferredword' -RequiredPhase 'holding' -Sequence $sequence
    $script:TouchInContact = $true
    $script:Evidence.firstContactBaseline = $held.baseline
    $script:Evidence.firstContactObserved = $held
    $script:Evidence.firstContactHoldingReceipt = $held.receipt
    $beforeTerminalUi = Get-DeferredTouchUiState -RequestedViewport $Viewport
    if ($Scenario -eq 'CancelNew') {
        $beforeTerminalUi = Assert-DeferredTouchSelection -State $beforeTerminalUi -ExpectedSelected $true -ExpectedText 'deferredword' `
            -Stage 'BeforeTerminal UIA state' -AllowUnavailableGeometry -HeldReceipt $held.receipt -ExpectedSequence $sequence
    }
    else {
        $beforeTerminalUi = Assert-DeferredTouchSelection -State $beforeTerminalUi -ExpectedSelected $true -ExpectedText ([string]$initialUiState.selection) `
            -Stage 'BeforeTerminal UIA state' -AllowUnavailableGeometry -HeldReceipt $held.receipt -ExpectedSequence $sequence
    }
    if ([int]$held.receipt.beforeTerminal.matchingTerminalCount -ne 0 -or -not [bool]$held.receipt.workerOwnsSnapshot) {
        throw 'BeforeTerminal was not captured before terminal delivery while the background snapshot lease was held.'
    }
    $script:Identity = Assert-DeferredProbeIdentity
    Assert-DeferredProbeForeground | Out-Null

    $terminalFlags = if ($Scenario -eq 'Release') {
        [McPointerFlags]::Up
    }
    else {
        [McPointerFlags]::Up -bor [McPointerFlags]::Canceled
    }
    $script:Evidence.firstContactPumpMetrics = Send-DeferredTouchFrame -X ([int]$held.screenPoint.x) -Y ([int]$held.screenPoint.y) -Flags $terminalFlags
    $script:TouchInContact = $false
    $terminalObserver = Wait-DeferredTerminalObservation -Contact $held -Canceled ($Scenario -ne 'Release') -TimeoutMilliseconds 2500
    $script:Evidence.terminalObserver = $terminalObserver
    $dropped = Wait-DeferredTouchPhase -Phase @('releasing','awaitingFreshContact') -Sequence $sequence -TimeoutMilliseconds 2500
    $script:Evidence.queuedAndDroppedReceipt = $dropped
    $awaiting = Wait-DeferredTouchPhase -Phase @('awaitingFreshContact') -Sequence $sequence -TimeoutMilliseconds 2500
    $script:Evidence.awaitingFreshContactReceipt = $awaiting
    $afterDropUiState = Get-DeferredTouchUiState -RequestedViewport $Viewport
    $preserves = ($Scenario -eq 'Release' -or $Scenario -eq 'CancelExisting')
    $null = Assert-DeferredTouchSelection -State $afterDropUiState -ExpectedSelected $preserves `
        -ExpectedText $(if($preserves){if($Scenario -eq 'Release'){[string]$beforeTerminalUi.selection}else{[string]$initialUiState.selection}}else{''}) `
        -Stage 'AfterDrop UIA state'
    $script:Evidence.afterDropUiSelection = $afterDropUiState
    $afterDropMenu = @(Get-DeferredTouchSelectionMenuCommands)
    if ($Scenario -ne 'Release' -and $afterDropMenu.Count -ne 0) {
        throw 'The canceled deferred gesture left a real selection context menu open after the worker drop.'
    }
    $afterDropFlyout = Dismiss-DeferredTouchSelectionFlyout -RequestedViewport $Viewport `
        -ExpectedSelection $(if($preserves){[string]$afterDropUiState.selection}else{''}) -Stage 'AfterDrop before fresh contact'
    $script:Evidence.afterDropSelectionFlyout = $afterDropFlyout

    $fresh = Start-DeferredTouchAtText -Text 'freshword' -RequiredPhase 'freshContactAccepted' -Sequence $sequence
    $script:TouchInContact = $true
    $script:Evidence.freshContactBaseline = $fresh.baseline
    $script:Evidence.freshContactObserved = $fresh
    $script:Evidence.freshContactAcceptedReceipt = $fresh.receipt
    $script:Evidence.freshSelectionBeforeRelease = $fresh.uiState
    $null = Assert-DeferredTouchSelection -State $fresh.uiState -ExpectedSelected $true -ExpectedText 'freshword' -Stage 'Fresh-contact-before-Up UIA state'
    $script:Evidence.freshContactPumpMetrics = Send-DeferredTouchFrame -X ([int]$fresh.screenPoint.x) -Y ([int]$fresh.screenPoint.y) -Flags ([McPointerFlags]::Up)
    $script:TouchInContact = $false
    $script:Evidence.freshTerminalObserver = Wait-DeferredTerminalObservation -Contact $fresh -Canceled $false -TimeoutMilliseconds 2500
    $complete = Wait-DeferredTouchPhase -Phase @('complete') -Sequence $sequence -TimeoutMilliseconds 2500
    $script:Evidence.completeReceipt = $complete
    $script:Evidence.finalSelectionFlyout = Dismiss-DeferredTouchSelectionFlyout -RequestedViewport $Viewport `
        -ExpectedSelection 'freshword' -Stage 'Final selection after ordinary Up'
    $finalUiState = Get-DeferredTouchUiState -RequestedViewport $Viewport
    $script:Evidence.finalUiSelection = $finalUiState

    $script:Evidence.validation = Assert-DeferredTouchEvidence `
        -ExpectedScenario $Scenario -ExpectedViewport $Viewport -ExpectedSequence $sequence `
        -ArmedReceipt $armed -HoldingReceipt $held.receipt -DroppedReceipt $dropped -AwaitingReceipt $awaiting `
        -FreshReceipt $fresh.receipt -CompleteReceipt $complete `
        -InitialUiState $initialUiState -BeforeTerminalUiState $beforeTerminalUi -AfterDropUiState $afterDropUiState -FinalUiState $finalUiState `
        -FirstRoutedPointerId ([uint32]$held.routed.pointerId) -FirstContactSerial ([long]$held.routed.contactSerial) `
        -FreshRoutedPointerId ([uint32]$fresh.routed.pointerId) -FreshContactSerial ([long]$fresh.routed.contactSerial)
}

try {
    $script:Identity = Assert-DeferredProbeIdentity
    Invoke-DeferredTouchScenario
    $script:Evidence.status = 'PASS'
}
catch {
    # Only the exact P1-031 delivery-limit marker is UNVERIFIED; all else fails.
    $script:Evidence.status = if ($_.Exception.Message.StartsWith('INJECTED-CANCEL-NOT-NATIVE: ', [StringComparison]::Ordinal)) { 'UNVERIFIED' } else { 'FAIL' }
    $script:Evidence.failure = $_.Exception.ToString()
}
finally {
    $cleanup = [ordered]@{pumpStopped=(Test-DeferredTouchPumpStopped);cancelUpSent=$false;deviceDestroyed=$false;deviceRetained=$false;error=$null}
    try {
        $script:Identity = Assert-DeferredProbeIdentity
        $script:PayloadReceipt = Assert-DeferredPayloadIdentity
        $script:Evidence.identity = $script:Identity
        $script:Evidence.payloadHashes = $script:PayloadReceipt
    }
    catch { $cleanup.error = 'Final process/deployed-payload identity check failed: ' + $_.Exception.ToString() }

    # Stop and join the only injector before any cleanup input or device teardown.
    # A failed join deliberately retains the device: destroying it while another
    # runspace may still be sending would race native input against device lifetime.
    if ($null -ne $script:TouchPump) {
        try {
            $stopped = Stop-DeferredTouchInputPump -Terminal cancel
            $cleanup.pumpStopped = Test-DeferredTouchPumpStopped
            $cleanup.cancelUpSent = ($null -ne $stopped -and [string]$stopped.terminal -ceq 'cancel')
        }
        catch {
            if ($null -eq $cleanup.error) { $cleanup.error = 'Could not stop and join the touch input worker: ' + $_.Exception.ToString() }
            else { $cleanup.error += [Environment]::NewLine + 'Could not stop and join the touch input worker: ' + $_.Exception.ToString() }
        }
    }

    if (-not (Test-DeferredTouchPumpStopped)) {
        $cleanup.deviceRetained = ($script:TouchDevice -ne [IntPtr]::Zero)
        $reason = 'The touch worker may still be active; its device was retained and no concurrent cleanup input or destruction was attempted.'
        if ($null -eq $cleanup.error) { $cleanup.error = $reason } else { $cleanup.error += [Environment]::NewLine + $reason }
    }
    else {
        $possiblyActive = [bool]$script:TouchMayBeInContact -or [bool]$script:TouchInContact
        if ($possiblyActive -and $script:TouchDevice -ne [IntPtr]::Zero -and $null -ne $script:LastTouchPoint) {
            try {
                Assert-DeferredProbeIdentity | Out-Null
                Assert-DeferredProbeForeground | Out-Null
                [MarkdownCorrectnessSandboxNative]::Send($script:TouchDevice,[uint32]0,[int]$script:LastTouchPoint.x,[int]$script:LastTouchPoint.y,
                    ([McPointerFlags]::Up -bor [McPointerFlags]::Canceled))
                $cleanup.cancelUpSent = $true
                $script:TouchInContact = $false
                $script:TouchMayBeInContact = $false
            }
            catch {
                $cleanup.deviceRetained = $true
                if ($null -eq $cleanup.error) { $cleanup.error = 'Could not safely deliver canceled Up to the exact foreground app; the device was retained: ' + $_.Exception.ToString() }
                else { $cleanup.error += [Environment]::NewLine + 'Could not safely deliver canceled Up to the exact foreground app; the device was retained: ' + $_.Exception.ToString() }
            }
        }
        if ($script:TouchDevice -ne [IntPtr]::Zero -and -not $cleanup.deviceRetained) {
            try {
                [MarkdownCorrectnessSandboxNative]::DestroyTouchDevice($script:TouchDevice)
                $cleanup.deviceDestroyed = $true
                $script:TouchDevice = [IntPtr]::Zero
            }
            catch {
                $cleanup.deviceRetained = $true
                if ($null -eq $cleanup.error) { $cleanup.error = $_.Exception.ToString() }
                else { $cleanup.error += [Environment]::NewLine + $_.Exception.ToString() }
            }
        }
    }
    $script:Evidence.cleanup = $cleanup
    if ($cleanup.error -and $script:Evidence.status -eq 'PASS') {
        $script:Evidence.status = 'FAIL'
        $script:Evidence.failure = [string]$cleanup.error
    }
    $script:Evidence.identityChecks = $script:IdentityChecks
    $script:Evidence.foregroundChecks = $script:ForegroundChecks
}

$json = ConvertTo-Json -InputObject ([pscustomobject]$script:Evidence) -Depth 32 -Compress
$ascii = [System.Text.StringBuilder]::new($json.Length)
foreach ($character in $json.ToCharArray()) {
    $codeUnit = [int]$character
    if ($codeUnit -gt 0x7f) { $null = $ascii.AppendFormat([Globalization.CultureInfo]::InvariantCulture,'\u{0:X4}',$codeUnit) }
    else { $null = $ascii.Append($character) }
}
[Console]::Out.WriteLine($ascii.ToString())
if ($script:Evidence.status -ne 'PASS') { exit 1 }
