[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Identity','PayloadHashes','ResizeWindow','Range','PointRange','Bounds','ScrollSemantic','State','ExpectedRange','StationaryEdge','ScrollToTop','RawName','HighContrastRead','HighContrastSet')][string]$Mode,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$ProcessId,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][long]$WindowHandle,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedExeSha256,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedGuestComputerName,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ExpectedHostComputerName,
    [long]$ExpectedStartTicks = 0,
    [string]$ViewportMode = 'owned',
    [string]$Needle = '',
    [string]$AutomationId = '',
    [string]$StartNeedle = '',
    [ValidateSet('Start','End')][string]$StartEndpoint = 'Start',
    [string]$EndNeedle = '',
    [ValidateSet('Start','End')][string]$EndEndpoint = 'End',
    [uint32]$PointerId = 0,
    [ValidateRange(0,8192)][int]$WindowWidthPixels = 0,
    [ValidateRange(0,8192)][int]$WindowHeightPixels = 0,
    [uint32]$HighContrastFlags = 0,
    [AllowEmptyString()][string]$HighContrastScheme = ''
)

$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

$currentComputerName=[Environment]::MachineName
if([String]::IsNullOrWhiteSpace($ExpectedGuestComputerName) -or [String]::IsNullOrWhiteSpace($ExpectedHostComputerName)){
    throw 'Both the pre-captured Sandbox and host computer names are required.'
}
if([String]::Equals($ExpectedGuestComputerName,$ExpectedHostComputerName,[StringComparison]::OrdinalIgnoreCase)){
    throw 'Sandbox and host computer names are indistinguishable; refusing guest-only probes.'
}
if([String]::Equals($currentComputerName,$ExpectedHostComputerName,[StringComparison]::OrdinalIgnoreCase)){
    throw 'This guest probe is running on the host; refusing native/UIA/SystemParametersInfo access.'
}
if(-not [String]::Equals($currentComputerName,$ExpectedGuestComputerName,[StringComparison]::OrdinalIgnoreCase)){
    throw "This process is not in the approved Sandbox (expected '$ExpectedGuestComputerName', found '$currentComputerName')."
}

if (-not ('MarkdownCorrectnessSandboxNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

[StructLayout(LayoutKind.Sequential)] public struct McPoint { public int X; public int Y; public McPoint(int x,int y){X=x;Y=y;} }
[StructLayout(LayoutKind.Sequential)] public struct McRect { public int Left; public int Top; public int Right; public int Bottom; }
[StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] public struct McHighContrast { public uint Size; public uint Flags; public IntPtr DefaultScheme; }

[Flags] public enum McPointerFlags : uint { InRange=0x00000002,InContact=0x00000004,Canceled=0x00008000,Primary=0x00002000,Down=0x00010000,Update=0x00020000,Up=0x00040000 }
[Flags] public enum McTouchMask : uint { ContactArea=1,Orientation=2,Pressure=4 }
[StructLayout(LayoutKind.Sequential)] public struct McPointerInfo {
    public uint PointerType; public uint PointerId; public uint FrameId; public McPointerFlags PointerFlags;
    public IntPtr SourceDevice; public IntPtr HwndTarget; public McPoint Pixel; public McPoint Himetric;
    public McPoint PixelRaw; public McPoint HimetricRaw; public uint Time; public uint HistoryCount;
    public int InputData; public uint KeyStates; public ulong PerformanceCount; public int ButtonChangeType;
}
[StructLayout(LayoutKind.Sequential)] public struct McPointerTouchInfo {
    public McPointerInfo PointerInfo; public uint TouchFlags; public McTouchMask TouchMask;
    public McRect Contact; public McRect ContactRaw; public uint Orientation; public uint Pressure;
}
[StructLayout(LayoutKind.Sequential)] public struct McPointerTypeInfo {
    public uint Type; public McPointerTypeInfoUnion Anonymous;
}
[StructLayout(LayoutKind.Explicit)] public struct McPointerTypeInfoUnion {
    [FieldOffset(0)] public McPointerTouchInfo TouchInfo;
}

public static class MarkdownCorrectnessSandboxNative {
    private const uint PointerFeedbackNone=3;
    [DllImport("user32.dll",EntryPoint="CreateSyntheticPointerDevice",SetLastError=true)] private static extern IntPtr CreateSyntheticPointerDevice(uint pointerType,uint maxCount,uint feedbackMode);
    [DllImport("user32.dll",EntryPoint="InjectSyntheticPointerInput",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool InjectSyntheticPointerInput(IntPtr device,[In] McPointerTypeInfo[] contacts,uint count);
    [DllImport("user32.dll",EntryPoint="DestroySyntheticPointerDevice")] private static extern void DestroySyntheticPointerDevice(IntPtr device);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll",SetLastError=true)] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll",SetLastError=true)] private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int maxCount);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool GetWindowRect(IntPtr hwnd,out McRect rect);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool ClientToScreen(IntPtr hwnd,ref McPoint point);
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
    [DllImport("user32.dll",SetLastError=true)] private static extern bool SetWindowPos(IntPtr hwnd,IntPtr after,int x,int y,int width,int height,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern bool SystemParametersInfo(uint action,uint parameter,ref McHighContrast value,uint flags);
    public static void VerifyWindow(IntPtr hwnd,int expectedPid) {
        if(hwnd==IntPtr.Zero||!IsWindow(hwnd)) throw new InvalidOperationException("Approved app HWND is not live.");
        uint pid; if(GetWindowThreadProcessId(hwnd,out pid)==0) throw LastError("GetWindowThreadProcessId");
        if(pid!=(uint)expectedPid) throw new InvalidOperationException("Approved app HWND process changed.");
    }
    public static uint Dpi(IntPtr hwnd) { uint dpi=GetDpiForWindow(hwnd); if(dpi==0) throw LastError("GetDpiForWindow"); return dpi; }
    public static McPoint ClientOriginOnScreen(IntPtr hwnd) {
        IntPtr previous=SetThreadDpiAwarenessContext(new IntPtr(-4));
        if(previous==IntPtr.Zero) throw LastError("SetThreadDpiAwarenessContext(PER_MONITOR_AWARE_V2)");
        try { var point=new McPoint(0,0); if(!ClientToScreen(hwnd,ref point)) throw LastError("ClientToScreen"); return point; }
        finally { if(SetThreadDpiAwarenessContext(previous)==IntPtr.Zero) throw LastError("Restore caller DPI-awareness context"); }
    }
    public static string Title(IntPtr hwnd) { var b=new StringBuilder(1024); GetWindowText(hwnd,b,b.Capacity); return b.ToString(); }
    public static McRect Bounds(IntPtr hwnd) { McRect r; if(!GetWindowRect(hwnd,out r)) throw LastError("GetWindowRect"); return r; }
    public static void Resize(IntPtr hwnd,int width,int height) {
        if(width<=0||height<=0) throw new ArgumentOutOfRangeException("size");
        // Retain position, stacking, and activation; resize only the verified app.
        if(!SetWindowPos(hwnd,IntPtr.Zero,0,0,width,height,0x0002|0x0004|0x0010)) throw LastError("SetWindowPos");
    }
    public static int PointerInfoSize() { return Marshal.SizeOf(typeof(McPointerInfo)); }
    public static int PointerTouchInfoSize() { return Marshal.SizeOf(typeof(McPointerTouchInfo)); }
    public static McHighContrast ReadHighContrast() {
        var value=new McHighContrast(); value.Size=(uint)Marshal.SizeOf<McHighContrast>();
        if(!SystemParametersInfo(0x0042,value.Size,ref value,0)) throw LastError("SystemParametersInfo(SPI_GETHIGHCONTRAST)");
        return value;
    }
    public static string HighContrastScheme(McHighContrast value) { return value.DefaultScheme==IntPtr.Zero ? null : Marshal.PtrToStringUni(value.DefaultScheme); }
    public static void SetHighContrast(uint flags,string scheme) {
        IntPtr text=IntPtr.Zero;
        try {
            if(!String.IsNullOrEmpty(scheme)) text=Marshal.StringToHGlobalUni(scheme);
            var value=new McHighContrast { Size=(uint)Marshal.SizeOf<McHighContrast>(),Flags=flags,DefaultScheme=text };
            if(!SystemParametersInfo(0x0043,value.Size,ref value,0x0002)) throw LastError("SystemParametersInfo(SPI_SETHIGHCONTRAST)");
        } finally { if(text!=IntPtr.Zero) Marshal.FreeHGlobal(text); }
    }
    private static Win32Exception LastError(string operation) {
        int error=Marshal.GetLastWin32Error();
        return LastError(operation,error);
    }
    private static Win32Exception LastError(string operation,int error) {
        string detail=operation+" failed (Win32 error "+error+" / 0x"+error.ToString("X8")+"): "+new Win32Exception(error).Message;
        return new Win32Exception(error,detail);
    }
    private static McPointerTypeInfo Frame(uint pointerId,int x,int y,McPointerFlags flags) {
        flags|=McPointerFlags.Primary;
        var info=new McPointerInfo { PointerType=2,PointerId=pointerId,PointerFlags=flags,Pixel=new McPoint(x,y) };
        var rect=new McRect { Left=x-2,Top=y-2,Right=x+2,Bottom=y+2 };
        var touch=new McPointerTouchInfo { PointerInfo=info,TouchFlags=0,TouchMask=McTouchMask.ContactArea,Contact=rect };
        return new McPointerTypeInfo { Type=2,Anonymous=new McPointerTypeInfoUnion { TouchInfo=touch } };
    }
    public static IntPtr CreateTouchDevice() {
        IntPtr device=CreateSyntheticPointerDevice(2,1,PointerFeedbackNone);
        if(device==IntPtr.Zero) throw LastError("CreateSyntheticPointerDevice(PT_TOUCH)");
        return device;
    }
    public static void DestroyTouchDevice(IntPtr device) { if(device!=IntPtr.Zero) DestroySyntheticPointerDevice(device); }
    public static void Send(IntPtr device,uint pointerId,int x,int y,McPointerFlags flags) {
        if(device==IntPtr.Zero) throw new ArgumentException("A live synthetic touch-device handle is required.","device");
        if(pointerId!=0) throw new ArgumentOutOfRangeException("pointerId","A one-contact synthetic device uses local contact index 0.");
        var one=new[] { Frame(pointerId,x,y,flags) };
        // ERROR_NOT_READY is transient while Windows is consuming the prior
        // frame. Retry only this exact frame, with a small bounded allowance.
        for(int attempt=0;attempt<=10;attempt++) {
            if(InjectSyntheticPointerInput(device,one,1)) return;
            int error=Marshal.GetLastWin32Error();
            if(error!=21||attempt==10) throw LastError("InjectSyntheticPointerInput(PT_TOUCH)",error);
            System.Threading.Thread.Sleep(1);
        }
    }
}
'@
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Get-Root {
    [MarkdownCorrectnessSandboxNative]::VerifyWindow([IntPtr]$WindowHandle,$ProcessId)
    return [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$WindowHandle)
}
function Find-Unique {
    param([System.Windows.Automation.AutomationElement]$Root,[string]$AutomationId)
    $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$AutomationId)
    $all=$Root.FindAll([System.Windows.Automation.TreeScope]::Descendants,$condition)
    if($all.Count -ne 1){throw "Expected exactly one AutomationId '$AutomationId'; found $($all.Count)."}
    return $all.Item(0)
}
function Get-ProcessWindowRoots {
    param([int]$TargetProcessId)
    if($TargetProcessId -ne $ProcessId){throw "Only the captured sample PID $ProcessId may be traversed; refusing PID $TargetProcessId."}

    # Keep the exact captured HWND even if the UIA desktop omits it. A raw-view
    # cache request is also required when discovering same-process popup roots;
    # ordinary FindAll enumeration otherwise silently uses Control view.
    $captured=Get-Root
    $roots=[Collections.Generic.List[System.Windows.Automation.AutomationElement]]::new()
    $seenHandles=[Collections.Generic.HashSet[long]]::new()
    $capturedHandle=[long]$captured.Current.NativeWindowHandle
    if($capturedHandle -ne [long]$WindowHandle -or [int]$captured.Current.ProcessId -ne $TargetProcessId){
        throw 'The captured UIA root no longer identifies the approved sample HWND and PID.'
    }
    $roots.Add($captured)
    [void]$seenHandles.Add($capturedHandle)

    $desktop=[System.Windows.Automation.AutomationElement]::RootElement
    $pidCondition=[System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,$TargetProcessId)
    $cache=[System.Windows.Automation.CacheRequest]::new()
    $cache.TreeFilter=[System.Windows.Automation.Automation]::RawViewCondition
    $rawScope=$cache.Activate()
    try{$topLevelWindows=$desktop.FindAll([System.Windows.Automation.TreeScope]::Children,$pidCondition)}
    finally{$rawScope.Dispose()}
    for($i=0;$i -lt $topLevelWindows.Count;$i++){
        $candidate=$topLevelWindows.Item($i)
        $current=$candidate.Current
        if([int]$current.ProcessId -ne $TargetProcessId){continue}
        $handle=[long]$current.NativeWindowHandle
        if($handle -le 0){continue}
        if($seenHandles.Add($handle)){$roots.Add($candidate)}
    }
    return $roots.ToArray()
}
function Get-ProcessElements {
    param([int]$TargetProcessId)
    # Entries are provenance envelopes containing both the UIA element and
    # the verified HWND/PID that authorized traversing it. They are not raw
    # AutomationElement instances, so the collection must accept objects.
    $elements=[Collections.Generic.List[object]]::new()
    $seenRuntimeIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $cache=[System.Windows.Automation.CacheRequest]::new()
    $cache.TreeFilter=[System.Windows.Automation.Automation]::RawViewCondition
    $rawScope=$cache.Activate()
    try{
        foreach($root in @(Get-ProcessWindowRoots $TargetProcessId)){
            $rootCurrent=$root.Current
            $rootHandle=[long]$rootCurrent.NativeWindowHandle
            if([int]$rootCurrent.ProcessId -ne $TargetProcessId -or $rootHandle -le 0){
                throw 'A raw UIA traversal root is not a verified same-process HWND.'
            }
            $candidates=[Collections.Generic.List[System.Windows.Automation.AutomationElement]]::new()
            if([int]$rootCurrent.ProcessId -eq $TargetProcessId){$candidates.Add($root)}
            # A normal FindAll call follows Control view even when the query is
            # rooted at the app HWND. The active RawView cache exposes shell-only
            # peers such as CurrentSamplePage and MarkdownEditor.
            $descendants=$root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            for($i=0;$i -lt $descendants.Count;$i++){$candidates.Add($descendants.Item($i))}
            foreach($element in $candidates){
                $current=$element.Current
                $elementProcessId=[int]$current.ProcessId
                # WinUI raw XAML peers often report ProcessId=0 and HWND=0.
                # They are accepted only with explicit provenance from this
                # already-verified same-process window root. A different
                # nonzero process is never admitted through that root.
                if($elementProcessId -ne $TargetProcessId -and $elementProcessId -ne 0){continue}
                $runtimeId=@($element.GetRuntimeId())
                if($runtimeId.Count -eq 0){throw 'A same-process raw UIA element returned an empty runtime ID.'}
                $runtimeKey=($runtimeId -join ',')
                if($seenRuntimeIds.Add($runtimeKey)){
                    $elements.Add([pscustomobject]@{element=$element;ownerProcessId=$TargetProcessId;ownerWindowHandle=$rootHandle;reportedProcessId=$elementProcessId})
                }
            }
        }
    }finally{$rawScope.Dispose()}
    return $elements.ToArray()
}
function Resolve-UniqueProcessAutomationId {
    param([object[]]$Elements,[int]$TargetProcessId,[string]$AutomationId)
    $matchedElements=[Collections.Generic.List[object]]::new()
    foreach($candidate in $Elements){
        if($null -eq $candidate -or $null -eq $candidate.element){continue}
        $ownerProcessId=[int]$candidate.ownerProcessId
        $ownerWindowHandle=[long]$candidate.ownerWindowHandle
        if($ownerProcessId -ne $TargetProcessId -or $ownerWindowHandle -le 0){continue}
        $current=$candidate.element.Current
        $reportedProcessId=[int]$current.ProcessId
        if(($reportedProcessId -eq $TargetProcessId -or $reportedProcessId -eq 0) -and
            [int]$candidate.reportedProcessId -eq $reportedProcessId -and
            [string]$current.AutomationId -ceq $AutomationId){$matchedElements.Add($candidate.element)}
    }
    if($matchedElements.Count -ne 1){throw "Expected exactly one raw AutomationId '$AutomationId' rooted in verified PID $TargetProcessId HWNDs; found $($matchedElements.Count)."}
    return $matchedElements[0]
}
function Find-UniqueProcessElement {
    param([int]$TargetProcessId,[string]$AutomationId)
    $elements=@(Get-ProcessElements $TargetProcessId)
    return Resolve-UniqueProcessAutomationId -Elements $elements -TargetProcessId $TargetProcessId -AutomationId $AutomationId
}
function Get-Pattern {
    param([System.Windows.Automation.AutomationElement]$Element,[System.Windows.Automation.AutomationPattern]$Pattern)
    $value=$null
    if(-not $Element.TryGetCurrentPattern($Pattern,[ref]$value)){return $null}
    return $value
}
function Get-TextPattern {
    $renderer=Find-Unique (Get-Root) 'MarkdownRenderer'
    $pattern=Get-Pattern $renderer ([System.Windows.Automation.TextPattern]::Pattern)
    if($null -eq $pattern){throw 'MarkdownRenderer does not expose UIA TextPattern.'}
    return [pscustomobject]@{renderer=$renderer;pattern=$pattern}
}
function ConvertTo-RangeRectangles {
    param([Parameter(Mandatory)]$Range)
    $values=@($Range.GetBoundingRectangles())
    $rectangles=[Collections.Generic.List[System.Windows.Rect]]::new()
    $typedRectangles=$false
    foreach($value in $values){
        if($value -is [System.Windows.Rect]){
            $typedRectangles=$true
            $rect=[System.Windows.Rect]$value
            if(-not $rect.IsEmpty -and $rect.Width -gt 0 -and $rect.Height -gt 0){$rectangles.Add($rect)}
        }
    }
    if($typedRectangles){return @($rectangles.ToArray())}
    if($values.Count -eq 0){return @()}
    if($values.Count -lt 4 -or ($values.Count%4)-ne 0){throw 'UIA returned an incomplete bounding-rectangle value array.'}
    for($i=0;$i+3 -lt $values.Count;$i+=4){
        $rect=[System.Windows.Rect]::new([double]$values[$i],[double]$values[$i+1],[double]$values[$i+2],[double]$values[$i+3])
        if(-not $rect.IsEmpty -and $rect.Width -gt 0 -and $rect.Height -gt 0){$rectangles.Add($rect)}
    }
    return @($rectangles.ToArray())
}
function Get-Range {
    param([string]$Text)
    if([string]::IsNullOrWhiteSpace($Text)){throw 'A semantic text needle is required.'}
    $context=Get-TextPattern
    $range=$context.pattern.DocumentRange.FindText($Text,$false,$false)
    if($null -eq $range){throw "UIA TextPattern cannot find '$Text'."}
    $range.ScrollIntoView($true)
    Start-Sleep -Milliseconds 100
    $rects=@(ConvertTo-RangeRectangles $range)
    if($rects.Count -ne 1){throw "Text '$Text' should have one visible rectangle, got $($rects.Count)."}
    $rect=[System.Windows.Rect]$rects[0]
    if($rect.IsEmpty -or $rect.Width -le 0 -or $rect.Height -le 0){throw "Text '$Text' has empty UIA geometry."}
    return [pscustomobject]@{range=$range;text=$range.GetText(-1);rect=$rect}
}
function Get-PointRange {
    param([Parameter(Mandatory)][string]$Text)
    $reference=Get-Range $Text
    $context=Get-TextPattern
    $point=[System.Windows.Point]::new(
        $reference.rect.X+$reference.rect.Width/2,
        $reference.rect.Y+$reference.rect.Height/2)
    $range=$context.pattern.RangeFromPoint($point)
    if($null -eq $range){throw "UIA RangeFromPoint returned no range for '$Text'."}
    $range.ExpandToEnclosingUnit([System.Windows.Automation.Text.TextUnit]::Word)
    $actualText=$range.GetText(-1)
    if($actualText -cne $Text -or $reference.text -cne $Text){throw "UIA point range resolved '$actualText', expected '$Text'."}
    $rectangles=@(ConvertTo-RangeRectangles $range)
    if($rectangles.Count -ne 1){throw "UIA point range '$Text' must have exactly one visible word rectangle."}
    $rect=$rectangles[0]
    if(-not $rect.Contains($point)){throw "UIA point range '$Text' does not contain its visible painted-word center."}
    return [pscustomobject]@{
        text=$actualText
        referenceText=$reference.text
        rendererRect=$context.renderer.Current.BoundingRectangle
        point=@{x=$point.X;y=$point.Y}
        rect=@{x=$rect.X;y=$rect.Y;width=$rect.Width;height=$rect.Height}
    }
}
function Get-Handles {
    $window=Get-Root; $walker=[System.Windows.Automation.TreeWalker]::RawViewWalker
    $pending=[Collections.Generic.Stack[System.Windows.Automation.AutomationElement]]::new();$pending.Push($window)
    $handles=[Collections.Generic.List[object]]::new()
    while($pending.Count -gt 0){
        $element=$pending.Pop()
        try{
            $id=[string]$element.Current.AutomationId
            if($id -in @('MarkdownSelectionStartHandle','MarkdownSelectionEndHandle')){
                $r=$element.Current.BoundingRectangle
                $handles.Add([pscustomobject]@{automationId=$id;name=$element.Current.Name;x=$r.X;y=$r.Y;width=$r.Width;height=$r.Height;offscreen=$element.Current.IsOffscreen})
            }
            $child=$walker.GetFirstChild($element);while($null -ne $child){$pending.Push($child);$child=$walker.GetNextSibling($child)}
        }catch{}
    }
    return @($handles.ToArray())
}
function Get-ScrollElement {
    param([ValidateSet('owned','ancestor')][string]$Mode)
    $root=Get-Root
    if($Mode -eq 'ancestor'){
        $element=Find-Unique $root 'TouchSelectionAncestorViewport'
        $pattern=Get-Pattern $element ([System.Windows.Automation.ScrollPattern]::Pattern)
        if($null -eq $pattern -or -not $pattern.Current.VerticallyScrollable){throw 'Ancestor viewport has no vertical ScrollPattern.'}
        return [pscustomobject]@{element=$element;pattern=$pattern}
    }
    $renderer=Find-Unique $root 'MarkdownRenderer';$walker=[System.Windows.Automation.TreeWalker]::ControlViewWalker
    $pending=[Collections.Generic.Stack[System.Windows.Automation.AutomationElement]]::new();$pending.Push($renderer)
    $candidates=[Collections.Generic.List[object]]::new()
    while($pending.Count -gt 0){
        $element=$pending.Pop()
        $pattern=Get-Pattern $element ([System.Windows.Automation.ScrollPattern]::Pattern)
        if($null -ne $pattern -and $pattern.Current.VerticallyScrollable){
            $r=$element.Current.BoundingRectangle
            if(-not $r.IsEmpty -and $r.Width -gt 100 -and $r.Height -gt 100){$candidates.Add([pscustomobject]@{element=$element;pattern=$pattern;area=($r.Width*$r.Height);className=$element.Current.ClassName;name=$element.Current.Name})}
        }
        $child=$walker.GetFirstChild($element);while($null -ne $child){$pending.Push($child);$child=$walker.GetNextSibling($child)}
    }
    if($candidates.Count -ne 1){throw "Owned MarkdownScrollView must expose exactly one vertically scrollable owner below MarkdownRenderer; found $($candidates.Count)."}
    return $candidates[0]
}
function Get-State {
    param([ValidateSet('owned','ancestor')][string]$Viewport)
    $context=Get-TextPattern;$ranges=@($context.pattern.GetSelection())
    $selection=(($ranges|ForEach-Object{$_.GetText(-1)}) -join '')
    $selectionRects=[Collections.Generic.List[object]]::new()
    foreach($range in $ranges){
        foreach($rect in @(ConvertTo-RangeRectangles $range)){
            $selectionRects.Add(@{x=$rect.X;y=$rect.Y;width=$rect.Width;height=$rect.Height})
        }
    }
    $scroll=Get-ScrollElement $Viewport;$scrollPattern=$scroll.pattern
    $rendererRect=$context.renderer.Current.BoundingRectangle
    return [pscustomobject]@{
        selection=$selection;rangeCount=$ranges.Count;verticalPercent=$scrollPattern.Current.VerticalScrollPercent
        verticallyScrollable=$scrollPattern.Current.VerticallyScrollable;viewportAutomationId=$scroll.element.Current.AutomationId
        viewportName=$scroll.element.Current.Name;viewportClass=$scroll.element.Current.ClassName
        rendererRect=@{x=$rendererRect.X;y=$rendererRect.Y;width=$rendererRect.Width;height=$rendererRect.Height}
        selectionRects=@($selectionRects.ToArray())
        handles=@(Get-Handles);dpi=[MarkdownCorrectnessSandboxNative]::Dpi([IntPtr]$WindowHandle)
    }
}
function Get-TouchContactObservation {
    # The sample status peer is intentionally a raw-tree leaf; use the same
    # verified process-root traversal as shell/editor peers rather than the
    # app window's narrower Control-view descendants.
    $element=Find-UniqueProcessElement $ProcessId 'TouchContactObservation'
    $json=[string]$element.Current.Name
    if([string]::IsNullOrWhiteSpace($json)){throw 'The sample touch-contact observer exposed no UIA snapshot.'}
    try{$observation=ConvertFrom-Json -InputObject $json}
    catch{throw "The sample touch-contact observer returned malformed JSON: $($_.Exception.Message)"}
    if([int]$observation.version -ne 1){throw "Unsupported sample touch-contact observation version '$($observation.version)'."}
    if([string]$observation.observationStatus -cne 'ok' -or [int]$observation.observationErrors -ne 0){
        throw "The sample touch-contact observer is degraded (status='$($observation.observationStatus)', errors=$($observation.observationErrors))."
    }
    if($null -eq $observation.contacts -or $null -eq $observation.active){throw 'The sample touch-contact snapshot omitted contacts/active arrays.'}
    return $observation
}
function Get-TouchContactRecord {
    param([Parameter(Mandatory)]$Observation,[Parameter(Mandatory)][uint32]$PointerId)
    $matches=@($Observation.contacts|Where-Object{[uint32]$_.pointerId -eq $PointerId})
    if($matches.Count -ne 1){throw "The sample observer must retain exactly one record for routed pointer $PointerId; found $($matches.Count)."}
    return $matches[0]
}
function Get-ExpectedScreenTouchPoint {
    param([Parameter(Mandatory)][double]$ScreenX,[Parameter(Mandatory)][double]$ScreenY)
    # This is intentionally only the exact screen-pixel injection target. The
    # observer validates it in XamlRoot-content DIPs, never against the clipped
    # UIA viewport rectangle.
    $origin=[MarkdownCorrectnessSandboxNative]::ClientOriginOnScreen([IntPtr]$WindowHandle)
    $dpi=[MarkdownCorrectnessSandboxNative]::Dpi([IntPtr]$WindowHandle)
    return [pscustomobject]@{screenX=$ScreenX;screenY=$ScreenY;windowDpi=$dpi;clientOriginScreenPx=@{x=$origin.X;y=$origin.Y}}
}
function ConvertTo-ExpectedRootClientPoint {
    param([Parameter(Mandatory)]$ExpectedScreenPoint,[Parameter(Mandatory)]$ObservedContact)
    $scale=[double]$ObservedContact.eventRasterizationScale
    if($null -eq $ObservedContact.eventRasterizationScale -or [double]::IsNaN($scale) -or [double]::IsInfinity($scale) -or $scale -le 0){throw 'The routed contact did not report a valid event XamlRoot rasterization scale.'}
    if($null -eq $ObservedContact.lastPositionRootDipX -or $null -eq $ObservedContact.lastPositionRootDipY){throw 'The routed contact observer omitted the XamlRoot-content point.'}
    $origin=$ExpectedScreenPoint.clientOriginScreenPx
    if($null -eq $origin -or $null -eq $origin.x -or $null -eq $origin.y){throw 'The expected input point omitted the captured native HWND client origin.'}
    $windowDpi=[double]$ExpectedScreenPoint.windowDpi
    if($windowDpi -le 0 -or [Math]::Abs(($windowDpi/96.0)-$scale) -gt 0.01){throw "The routed XamlRoot rasterization scale ($scale) disagrees with GetDpiForWindow ($windowDpi)."}
    return [pscustomobject]@{x=(([double]$ExpectedScreenPoint.screenX-[double]$origin.x)/$scale);y=(([double]$ExpectedScreenPoint.screenY-[double]$origin.y)/$scale);scale=$scale;clientOriginScreenPx=@{x=$origin.x;y=$origin.y}}
}
function Find-NewTouchContact {
    param([Parameter(Mandatory)]$Before,[Parameter(Mandatory)]$After,[Parameter(Mandatory)]$ExpectedPoint)
    if([int]$Before.version -ne 1 -or [int]$After.version -ne 1 -or
        [string]$Before.observationStatus -cne 'ok' -or [string]$After.observationStatus -cne 'ok' -or
        [int]$Before.observationErrors -ne 0 -or [int]$After.observationErrors -ne 0 -or
        [long]$Before.rendererGeneration -le 0 -or [long]$Before.rendererGeneration -ne [long]$After.rendererGeneration){
        throw 'The sample observer was degraded or changed renderer generation during synthetic touch delivery.'
    }
    $beforeById=@{};foreach($prior in $Before.contacts){$beforeById[[string][uint32]$prior.pointerId]=$prior}
    $newContacts=[Collections.Generic.List[object]]::new()
    foreach($record in $After.contacts){
        $key=[string][uint32]$record.pointerId
        if($beforeById.ContainsKey($key)){
            $prior=$beforeById[$key]
            if([int]$record.pressCount -eq ([int]$prior.pressCount+1) -and
                [long]$record.contactSerial -gt [long]$prior.contactSerial -and
                [long]$record.lastEventSequence -gt [long]$prior.lastEventSequence){$newContacts.Add($record)}
        }elseif([int]$record.pressCount -eq 1 -and [long]$record.contactSerial -gt 0 -and [long]$record.lastEventSequence -gt 0){$newContacts.Add($record)}
    }
    if($newContacts.Count -ne 1){throw "Expected exactly one newly routed contact using OS pointer ID, contact serial, event phase, and renderer generation; found $($newContacts.Count). Injected ID is not treated as routed identity."}
    $record=$newContacts[0]
    $expectedRootPoint=ConvertTo-ExpectedRootClientPoint -ExpectedScreenPoint $ExpectedPoint -ObservedContact $record
    $errorDip=[Math]::Sqrt([Math]::Pow(([double]$record.lastPositionRootDipX-[double]$expectedRootPoint.x),2)+[Math]::Pow(([double]$record.lastPositionRootDipY-[double]$expectedRootPoint.y),2))
    if($errorDip -gt 12.0){throw "The new routed pointer $($record.pointerId)/$($record.contactSerial) was at the wrong XamlRoot-content coordinate (error=$errorDip DIPs)."}
    return $record
}
function Assert-ActiveTouchHandleContact {
    param([Parameter(Mandatory)]$Observation,[Parameter(Mandatory)][uint32]$PointerId,[Parameter(Mandatory)][long]$ContactSerial,[Parameter(Mandatory)][string]$ExpectedHandle,[Parameter(Mandatory)]$ExpectedPoint,[Parameter(Mandatory)]$Before)
    $record=Get-TouchContactRecord -Observation $Observation -PointerId $PointerId
    $active=@($Observation.active|Where-Object{[uint32]$_.pointerId -eq $PointerId -and [long]$_.contactSerial -eq $ContactSerial})
    $expectedRootPoint=ConvertTo-ExpectedRootClientPoint -ExpectedScreenPoint $ExpectedPoint -ObservedContact $record
    $errorDip=[Math]::Sqrt([Math]::Pow(([double]$record.lastPositionRootDipX-[double]$expectedRootPoint.x),2)+[Math]::Pow(([double]$record.lastPositionRootDipY-[double]$expectedRootPoint.y),2))
    $prior=@($Before.contacts|Where-Object{[uint32]$_.pointerId -eq $PointerId})
    if($prior.Count -gt 1){throw "The baseline observer contained duplicate records for routed pointer $PointerId."}
    $baseline=if($prior.Count -eq 1){$prior[0]}else{[pscustomobject]@{pressCount=0;releaseCount=0;cancelCount=0;captureLostCount=0}}
    if($active.Count -ne 1 -or [long]$record.contactSerial -ne $ContactSerial -or
        -not [bool]$record.inContact -or [string]$record.lastEvent -cne 'Moved' -or
        [long]$record.contactSerial -le 0 -or [string]$record.handle -cne $ExpectedHandle -or
        [int]$record.pressCount -ne ([int]$baseline.pressCount+1) -or
        [int]$record.releaseCount -ne [int]$baseline.releaseCount -or
        [int]$record.cancelCount -ne [int]$baseline.cancelCount -or
        [int]$record.captureLostCount -ne [int]$baseline.captureLostCount -or $errorDip -gt 12.0){
        throw "Routed pointer $PointerId/$ContactSerial is not a live contact on $ExpectedHandle at the expected position according to actual routed events."
    }
    return $record
}

$process=$null;$exeHash=$null;$startTicks=$null;$title=$null
if($Mode -notin @('HighContrastRead','HighContrastSet')){
    [MarkdownCorrectnessSandboxNative]::VerifyWindow([IntPtr]$WindowHandle,$ProcessId)
    $process=Get-Process -Id $ProcessId -ErrorAction Stop
    $exeHash=(Get-FileHash -LiteralPath $process.Path -Algorithm SHA256).Hash.ToLowerInvariant()
    if($exeHash -cne $ExpectedExeSha256.ToLowerInvariant()){throw "Guest app binary SHA256 '$exeHash' differs from the build receipt."}
    $startTicks=$process.StartTime.ToUniversalTime().Ticks
    if($ExpectedStartTicks -ne 0 -and $startTicks -ne $ExpectedStartTicks){throw 'Guest app PID was reused or process identity changed.'}
    $title=[MarkdownCorrectnessSandboxNative]::Title([IntPtr]$WindowHandle)
    if($title -notmatch 'MarkdownRenderer'){throw "The approved app HWND title '$title' is unexpected."}
}

$result=$null
switch($Mode){
    'Identity' {
        $hc=[MarkdownCorrectnessSandboxNative]::ReadHighContrast()
        $result=@{mode=$Mode;computerName=$currentComputerName;processId=$ProcessId;hwnd=$WindowHandle;title=$title;exePath=$process.Path;exeSha256=$exeHash;startTicks=$startTicks;startTimeUtc=$process.StartTime.ToUniversalTime().ToString('o');dpi=[MarkdownCorrectnessSandboxNative]::Dpi([IntPtr]$WindowHandle);highContrastFlags=$hc.Flags;highContrastOn=(($hc.Flags -band 1)-ne 0);highContrastScheme=[MarkdownCorrectnessSandboxNative]::HighContrastScheme($hc)}
        $bounds=[MarkdownCorrectnessSandboxNative]::Bounds([IntPtr]$WindowHandle)
        $result.windowBounds=@{x=$bounds.Left;y=$bounds.Top;width=($bounds.Right-$bounds.Left);height=($bounds.Bottom-$bounds.Top)}
    }
    'ResizeWindow' {
        if($WindowWidthPixels -le 0 -or $WindowHeightPixels -le 0){throw 'ResizeWindow requires positive physical dimensions.'}
        [MarkdownCorrectnessSandboxNative]::Resize([IntPtr]$WindowHandle,$WindowWidthPixels,$WindowHeightPixels)
        Start-Sleep -Milliseconds 250
        $bounds=[MarkdownCorrectnessSandboxNative]::Bounds([IntPtr]$WindowHandle)
        $result=@{mode=$Mode;windowBounds=@{x=$bounds.Left;y=$bounds.Top;width=($bounds.Right-$bounds.Left);height=($bounds.Bottom-$bounds.Top)}}
        if($result.windowBounds.width -ne $WindowWidthPixels -or $result.windowBounds.height -ne $WindowHeightPixels){throw 'The verified app did not reach the requested physical size.'}
    }
    'PayloadHashes' {
        $requiredNames=@('MarkdownRenderer.Sample.exe','MarkdownRenderer.Sample.dll','MarkdownRenderer.dll')
        $optionalNames=@('MarkdownRenderer.Sample.deps.json')
        $files=[Collections.Generic.List[object]]::new()
        foreach($name in $requiredNames){
            $path=Join-Path (Split-Path -Parent $process.Path) $name
            if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw "Required deployed payload '$name' is missing beside the approved guest executable."}
            $item=Get-Item -LiteralPath $path;$hash=Get-FileHash -LiteralPath $path -Algorithm SHA256
            $files.Add(@{name=$name;path=$path;length=$item.Length;sha256=$hash.Hash.ToLowerInvariant()})
        }
        foreach($name in $optionalNames){
            $path=Join-Path (Split-Path -Parent $process.Path) $name
            if(Test-Path -LiteralPath $path -PathType Leaf){
                $item=Get-Item -LiteralPath $path;$hash=Get-FileHash -LiteralPath $path -Algorithm SHA256
                $files.Add(@{name=$name;path=$path;length=$item.Length;sha256=$hash.Hash.ToLowerInvariant()})
            }
        }
        $result=@{mode=$Mode;processId=$ProcessId;executablePath=[string]$process.Path;files=@($files.ToArray())}
    }
    'Range' {
        $r=Get-Range $Needle;$result=@{mode=$Mode;needle=$Needle;text=$r.text;rect=@{x=$r.rect.X;y=$r.rect.Y;width=$r.rect.Width;height=$r.rect.Height}}
    }
    'PointRange' {
        $r=Get-PointRange $Needle
        if($r.text -cne $Needle -or $r.referenceText -cne $Needle){throw "UIA point range resolved '$($r.text)', expected '$Needle'."}
        $result=@{mode=$Mode;needle=$Needle;text=$r.text;referenceText=$r.referenceText;point=$r.point;rect=$r.rect;rendererRect=@{x=$r.rendererRect.X;y=$r.rendererRect.Y;width=$r.rendererRect.Width;height=$r.rendererRect.Height}}
    }
    'Bounds' {
        if([string]::IsNullOrWhiteSpace($Needle)){throw 'Bounds requires a semantic text needle.'}
        $context=Get-TextPattern
        $range=$context.pattern.DocumentRange.FindText($Needle,$false,$false)
        if($null -eq $range){throw "UIA TextPattern cannot find '$Needle'."}
        # Do not scroll this range: offscreen/clipped content must retain absent
        # bounding geometry, rather than inventing visible rectangles for it.
        $rectangles=@(ConvertTo-RangeRectangles $range)
        $result=@{mode=$Mode;needle=$Needle;text=$range.GetText(-1);rectangleCount=$rectangles.Count;rectangles=@($rectangles|ForEach-Object{@{x=$_.X;y=$_.Y;width=$_.Width;height=$_.Height}})}
    }
    'ScrollSemantic' {
        if([string]::IsNullOrWhiteSpace($Needle)){throw 'ScrollSemantic requires a semantic text needle.'}
        $context=Get-TextPattern
        $range=$context.pattern.DocumentRange.FindText($Needle,$false,$false)
        if($null -eq $range -or $range.GetText(-1) -cne $Needle){throw "UIA TextPattern cannot resolve the exact semantic text '$Needle'."}
        $before=Get-State $ViewportMode
        $range.ScrollIntoView($true)
        Start-Sleep -Milliseconds 200
        $result=@{mode=$Mode;needle=$Needle;text=$range.GetText(-1);before=$before;after=(Get-State $ViewportMode)}
    }
    'RawName' {
        if([string]::IsNullOrWhiteSpace($AutomationId)){throw 'RawName requires an AutomationId.'}
        $element=Find-UniqueProcessElement $ProcessId $AutomationId
        $current=$element.Current
        $result=@{mode=$Mode;automationId=$AutomationId;name=[string]$current.Name;isEnabled=[bool]$current.IsEnabled;className=[string]$current.ClassName;controlType=[string]$current.ControlType.ProgrammaticName}
    }
    'State' { $result=Get-State $ViewportMode }
    'ScrollToTop' {
        $scroll=Get-ScrollElement $ViewportMode
        $scroll.pattern.SetScrollPercent(-1.0,0.0)
        Start-Sleep -Milliseconds 250
        $result=Get-State $ViewportMode
    }
    'ExpectedRange' {
        $a=Get-Range $StartNeedle;$b=Get-Range $EndNeedle
        $endpointType=$a.range.GetType().GetMethod('CompareEndpoints').GetParameters()[0].ParameterType
        if(-not $endpointType.IsEnum){throw "UIA CompareEndpoints parameter is not an enum: $($endpointType.FullName)."}
        $startPoint=[System.Enum]::Parse($endpointType,$StartEndpoint)
        $endPoint=[System.Enum]::Parse($endpointType,$EndEndpoint)
        $context=Get-TextPattern
        $range=$context.pattern.DocumentRange.Clone()
        $comparison=$a.range.CompareEndpoints($startPoint,$b.range,$endPoint)
        if($comparison -le 0){
            $range.MoveEndpointByRange([System.Enum]::Parse($endpointType,'Start'),$a.range,$startPoint)
            $range.MoveEndpointByRange([System.Enum]::Parse($endpointType,'End'),$b.range,$endPoint)
        }else{
            $range.MoveEndpointByRange([System.Enum]::Parse($endpointType,'Start'),$b.range,$endPoint)
            $range.MoveEndpointByRange([System.Enum]::Parse($endpointType,'End'),$a.range,$startPoint)
        }
        $result=@{mode=$Mode;startNeedle=$StartNeedle;startEndpoint=$StartEndpoint;endNeedle=$EndNeedle;endEndpoint=$EndEndpoint;text=$range.GetText(-1)}
    }
    'HighContrastRead' {
        $hc=[MarkdownCorrectnessSandboxNative]::ReadHighContrast()
        $result=@{mode=$Mode;computerName=$currentComputerName;flags=$hc.Flags;enabled=(($hc.Flags -band 1)-ne 0);scheme=[MarkdownCorrectnessSandboxNative]::HighContrastScheme($hc)}
    }
    'HighContrastSet' {
        $scheme=if([string]::IsNullOrEmpty($HighContrastScheme)){$null}else{$HighContrastScheme}
        [MarkdownCorrectnessSandboxNative]::SetHighContrast($HighContrastFlags,$scheme)
        # The system High Contrast theme switch completes asynchronously; poll
        # for the committed state instead of reading once after a fixed delay.
        $hcDeadline=[DateTime]::UtcNow.AddSeconds(15)
        do{
            Start-Sleep -Milliseconds 250
            $hc=[MarkdownCorrectnessSandboxNative]::ReadHighContrast()
            $actualScheme=[MarkdownCorrectnessSandboxNative]::HighContrastScheme($hc)
            $committed=($hc.Flags -eq $HighContrastFlags -and [String]::Equals([string]$actualScheme,[string]$scheme,[StringComparison]::Ordinal))
        }while(-not $committed -and [DateTime]::UtcNow -lt $hcDeadline)
        if(-not $committed){throw "Sandbox SystemParametersInfo did not commit the requested High Contrast flags/scheme within 15 seconds: requested flags=$HighContrastFlags scheme='$scheme'; actual flags=$($hc.Flags) scheme='$actualScheme'."}
        $result=@{mode=$Mode;computerName=$currentComputerName;flags=$hc.Flags;enabled=(($hc.Flags -band 1)-ne 0);scheme=$actualScheme}
    }
    'StationaryEdge' {
        if($ViewportMode -notin @('owned','ancestor')){throw "Unknown viewport mode '$ViewportMode'."}
        $before=Get-State $ViewportMode
        if($PointerId -ne 0){throw 'A one-contact synthetic pointer device uses local contact index 0.'}
        if($before.rangeCount -ne 1 -or [string]::IsNullOrWhiteSpace($before.selection)){throw 'Stationary edge test requires one committed nonempty selection.'}
        if(@($before.handles).Count -ne 2){throw "Stationary edge test requires exactly two UIA selection handles; found $(@($before.handles).Count)."}
        $end=@($before.handles|Where-Object automationId -eq 'MarkdownSelectionEndHandle')
        if($end.Count -ne 1 -or $end[0].offscreen){throw 'Selection end handle is missing or offscreen.'}
        $dpi=[uint32]$before.dpi;$scale=$dpi/96.0
        $rect=$before.rendererRect
        $startX=[int][Math]::Round($end[0].x+$end[0].width/2);$startY=[int][Math]::Round($end[0].y+$end[0].height/2)
        $edgeX=[int][Math]::Round($rect.x+$rect.width-8*$scale);$edgeY=[int][Math]::Round($rect.y+$rect.height-8*$scale)
        if($startX -lt $rect.x -or $startX -gt ($rect.x+$rect.width) -or $startY -lt $rect.y -or $startY -gt ($rect.y+$rect.height)){throw 'Selection handle is outside the renderer bounds.'}
        $edgeExpectedPoint=Get-ExpectedScreenTouchPoint -ScreenX $edgeX -ScreenY $edgeY
        $observerBefore=Get-TouchContactObservation
        $touchDevice=[IntPtr]::Zero
        $downSent=$false;$upSent=$false;$releaseError=$null;$lastSentX=$startX;$lastSentY=$startY
        $stationaryWatch=$null
        try{
            $touchDevice=[MarkdownCorrectnessSandboxNative]::CreateTouchDevice()
            [MarkdownCorrectnessSandboxNative]::Send($touchDevice,$PointerId,$startX,$startY,[McPointerFlags]::InRange -bor [McPointerFlags]::InContact -bor [McPointerFlags]::Down)
            $downSent=$true
            for($i=1;$i -le 10;$i++){
                $x=[int][Math]::Round($startX+($edgeX-$startX)*($i/10.0));$y=[int][Math]::Round($startY+($edgeY-$startY)*($i/10.0))
                [MarkdownCorrectnessSandboxNative]::Send($touchDevice,$PointerId,$x,$y,[McPointerFlags]::InRange -bor [McPointerFlags]::InContact -bor [McPointerFlags]::Update)
                $lastSentX=$x;$lastSentY=$y
                Start-Sleep -Milliseconds 30
            }
            # No further pointer frames are sent during the hold: the finger is
            # stationary at the edge while UIA snapshots are taken.
            $stationaryWatch=[Diagnostics.Stopwatch]::StartNew()
            Start-Sleep -Milliseconds 100
            $contactEdge=Get-TouchContactObservation
            $edgeRecord=Find-NewTouchContact -Before $observerBefore -After $contactEdge -ExpectedPoint $edgeExpectedPoint
            $routedPointerId=[uint32]$edgeRecord.pointerId;$routedContactSerial=[long]$edgeRecord.contactSerial
            $edgeRecord=Assert-ActiveTouchHandleContact -Observation $contactEdge -PointerId $routedPointerId -ContactSerial $routedContactSerial -ExpectedHandle 'MarkdownSelectionEndHandle' -ExpectedPoint $edgeExpectedPoint -Before $observerBefore
            $atEdge=Get-State $ViewportMode
            Start-Sleep -Milliseconds 550
            $contactMid=Get-TouchContactObservation
            $midRecord=Assert-ActiveTouchHandleContact -Observation $contactMid -PointerId $routedPointerId -ContactSerial $routedContactSerial -ExpectedHandle 'MarkdownSelectionEndHandle' -ExpectedPoint $edgeExpectedPoint -Before $observerBefore
            $mid=Get-State $ViewportMode
            Start-Sleep -Milliseconds 650
            $contactEnd=Get-TouchContactObservation
            $endRecord=Assert-ActiveTouchHandleContact -Observation $contactEnd -PointerId $routedPointerId -ContactSerial $routedContactSerial -ExpectedHandle 'MarkdownSelectionEndHandle' -ExpectedPoint $edgeExpectedPoint -Before $observerBefore
            $endState=Get-State $ViewportMode
            if([long]$contactEdge.rendererGeneration -ne [long]$contactMid.rendererGeneration -or
                [long]$contactEdge.rendererGeneration -ne [long]$contactEnd.rendererGeneration -or
                [long]$edgeRecord.contactSerial -ne [long]$midRecord.contactSerial -or
                [long]$edgeRecord.contactSerial -ne [long]$endRecord.contactSerial -or
                [int]$edgeRecord.moveCount -ne [int]$midRecord.moveCount -or
                [int]$edgeRecord.moveCount -ne [int]$endRecord.moveCount -or
                [int]$edgeRecord.pressCount -ne [int]$midRecord.pressCount -or
                [int]$edgeRecord.pressCount -ne [int]$endRecord.pressCount -or
                [int]$edgeRecord.releaseCount -ne [int]$midRecord.releaseCount -or
                [int]$edgeRecord.releaseCount -ne [int]$endRecord.releaseCount -or
                [int]$edgeRecord.cancelCount -ne [int]$midRecord.cancelCount -or
                [int]$edgeRecord.cancelCount -ne [int]$endRecord.cancelCount -or
                [int]$edgeRecord.captureLostCount -ne [int]$midRecord.captureLostCount -or
                [int]$edgeRecord.captureLostCount -ne [int]$endRecord.captureLostCount){
                throw 'The sample observer did not preserve the same stationary handle contact and event counters across all three hold samples.'
            }
        } finally {
            if($null -ne $stationaryWatch){$stationaryWatch.Stop()}
            if($downSent -and -not $upSent){
                try{[MarkdownCorrectnessSandboxNative]::Send($touchDevice,$PointerId,$lastSentX,$lastSentY,[McPointerFlags]::Up);$upSent=$true}
                catch{$releaseError=$_.Exception.Message}
            }
            [MarkdownCorrectnessSandboxNative]::DestroyTouchDevice($touchDevice)
        }
        if($null -ne $releaseError){throw "Touch contact could not be released: $releaseError"}
        $releaseDeadline=[DateTime]::UtcNow.AddSeconds(2);$releaseObservation=$null;$releasedRecord=$null
        do{
            $releaseObservation=Get-TouchContactObservation
            $releasedRecord=Get-TouchContactRecord -Observation $releaseObservation -PointerId $routedPointerId
            if(-not [bool]$releasedRecord.inContact -and [int]$releasedRecord.releaseCount -ge ([int]$edgeRecord.releaseCount+1) -and
                [string]$releasedRecord.lastEvent -in @('Released','CaptureLost')){break}
            Start-Sleep -Milliseconds 40
        }while([DateTime]::UtcNow -lt $releaseDeadline)
        if($null -eq $releasedRecord){throw 'The sample observer did not retain the injected pointer record after the up frame.'}
        $releaseCaptureLostDelta=[int]$releasedRecord.captureLostCount-[int]$edgeRecord.captureLostCount
        $releaseActive=@($releaseObservation.active|Where-Object{[uint32]$_.pointerId -eq $routedPointerId -and [long]$_.contactSerial -eq $routedContactSerial})
        if([bool]$releasedRecord.inContact -or
            [int]$releasedRecord.releaseCount -ne ([int]$edgeRecord.releaseCount+1) -or
            [int]$releasedRecord.cancelCount -ne [int]$edgeRecord.cancelCount -or
            $releaseCaptureLostDelta -lt 0 -or $releaseCaptureLostDelta -gt 1 -or
            [long]$releaseObservation.rendererGeneration -ne [long]$contactEdge.rendererGeneration -or
            [long]$releasedRecord.contactSerial -ne $routedContactSerial -or
            [string]$releasedRecord.lastEvent -notin @('Released','CaptureLost') -or $releaseActive.Count -ne 0){
            throw 'The sample observer did not confirm one ordinary released-up transition for the same contact (allowing its one normal capture-loss cleanup event).'
        }
        $afterRelease=Get-State $ViewportMode
        $result=@{mode=$Mode;viewport=$ViewportMode;before=$before;atEdge=$atEdge;midHold=$mid;endHold=$endState;afterRelease=$afterRelease;contactBefore=$observerBefore;contactAtEdge=$contactEdge;contactMidHold=$contactMid;contactEndHold=$contactEnd;contactAfterRelease=$releaseObservation;start=@{x=$startX;y=$startY};edge=@{x=$edgeX;y=$edgeY};injectedPointerId=$PointerId;routedPointerId=$routedPointerId;holdMilliseconds=$stationaryWatch.Elapsed.TotalMilliseconds;holdDurationMeaning='measured stationary interval including observation roundtrips';stationaryFrameCountDuringHold=0;contactReleased=$upSent;observerVerified=$true;observationStatus='ok';observationErrors=0;handleAutomationId='MarkdownSelectionEndHandle';handleAttribution='contact originated on the handle; capture-loss counters remained unchanged';contactSerial=$routedContactSerial;moveCount=[int]$edgeRecord.moveCount;releaseCaptureLostDelta=$releaseCaptureLostDelta;releasedLastEvent=[string]$releasedRecord.lastEvent}
    }
}

function ConvertTo-AsciiJson {
    param([Parameter(Mandatory)]$Value)
    $json = ConvertTo-Json -InputObject $Value -Depth 24 -Compress
    $builder = [System.Text.StringBuilder]::new($json.Length)
    foreach ($character in $json.ToCharArray()) {
        $codeUnit = [int]$character
        if ($codeUnit -gt 0x7f) { $null = $builder.AppendFormat([System.Globalization.CultureInfo]::InvariantCulture, '\u{0:X4}', $codeUnit) }
        else { $null = $builder.Append($character) }
    }
    return $builder.ToString()
}
ConvertTo-AsciiJson -Value $result
