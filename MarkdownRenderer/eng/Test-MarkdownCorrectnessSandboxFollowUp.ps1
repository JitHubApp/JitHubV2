[CmdletBinding()]
param(
    [string]$FollowUpPath = '',
    [string]$ProbePath = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($MyInvocation.MyCommand.Path))
if ([string]::IsNullOrWhiteSpace($FollowUpPath)) { $FollowUpPath = Join-Path $scriptDirectory 'Invoke-MarkdownCorrectnessSandboxFollowUp.ps1' }
if ([string]::IsNullOrWhiteSpace($ProbePath)) { $ProbePath = Join-Path $scriptDirectory 'MarkdownCorrectnessSandboxProbe.ps1' }

function Assert-ParseClean {
    param([Parameter(Mandatory)][string]$Path)
    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($Path,[ref]$tokens,[ref]$errors) | Out-Null
    if ($errors.Count -gt 0) { throw "PowerShell parse errors in $Path`n$($errors | Out-String)" }
}

Assert-ParseClean $FollowUpPath
Assert-ParseClean $ProbePath

$probe = Get-Content -LiteralPath $ProbePath -Raw
$followUpTokens=$null
$followUpErrors=$null
$followUpAst=[System.Management.Automation.Language.Parser]::ParseFile($FollowUpPath,[ref]$followUpTokens,[ref]$followUpErrors)
$releaseValidatorAst=$followUpAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-StationaryEdgeContactRelease'
},$true)
if($null -eq $releaseValidatorAst){throw 'Could not find the stationary contact post-release validator.'}
Invoke-Expression $releaseValidatorAst.Extent.Text

$edgeObservation=[pscustomobject]@{version=1;rendererGeneration=12;observationStatus='ok';observationErrors=0}
$endContact=[pscustomobject]@{pointerId=73;contactSerial=91;releaseCount=0;cancelCount=0;captureLostCount=0}
$normalRelease=[pscustomobject]@{
    version=1;rendererGeneration=12;observationStatus='ok';observationErrors=0
    active=@()
    contacts=@([pscustomobject]@{pointerId=73;contactSerial=91;releaseCount=1;cancelCount=0;captureLostCount=0;inContact=$false;lastEvent='Released'})
}
$normalEvidence=Assert-StationaryEdgeContactRelease -PointerId 73 -EdgeObservation $edgeObservation -ExpectedContact $endContact -ReleaseSnapshot $normalRelease
if($normalEvidence.captureLostDelta -ne 0 -or $normalEvidence.terminalEvent -cne 'Released'){throw 'A plain released-up event was not accepted.'}

$captureCleanup=[pscustomobject]@{
    version=1;rendererGeneration=12;observationStatus='ok';observationErrors=0
    active=@()
    contacts=@([pscustomobject]@{pointerId=73;contactSerial=91;releaseCount=1;cancelCount=0;captureLostCount=1;inContact=$false;lastEvent='CaptureLost'})
}
$cleanupEvidence=Assert-StationaryEdgeContactRelease -PointerId 73 -EdgeObservation $edgeObservation -ExpectedContact $endContact -ReleaseSnapshot $captureCleanup
if($cleanupEvidence.captureLostDelta -ne 1 -or $cleanupEvidence.terminalEvent -cne 'CaptureLost'){throw 'One normal post-release capture-loss cleanup event was not accepted.'}

foreach($invalidRelease in @(
    [pscustomobject]@{Name='duplicate release';Snapshot=[pscustomobject]@{version=1;rendererGeneration=12;observationStatus='ok';observationErrors=0;active=@();contacts=@([pscustomobject]@{pointerId=73;contactSerial=91;releaseCount=2;cancelCount=0;captureLostCount=0;inContact=$false;lastEvent='Released'})}},
    [pscustomobject]@{Name='multiple capture loss';Snapshot=[pscustomobject]@{version=1;rendererGeneration=12;observationStatus='ok';observationErrors=0;active=@();contacts=@([pscustomobject]@{pointerId=73;contactSerial=91;releaseCount=1;cancelCount=0;captureLostCount=2;inContact=$false;lastEvent='CaptureLost'})}},
    [pscustomobject]@{Name='wrong renderer generation';Snapshot=[pscustomobject]@{version=1;rendererGeneration=13;observationStatus='ok';observationErrors=0;active=@();contacts=@([pscustomobject]@{pointerId=73;contactSerial=91;releaseCount=1;cancelCount=0;captureLostCount=0;inContact=$false;lastEvent='Released'})}},
    [pscustomobject]@{Name='still active';Snapshot=[pscustomobject]@{version=1;rendererGeneration=12;observationStatus='ok';observationErrors=0;active=@([pscustomobject]@{pointerId=73});contacts=@([pscustomobject]@{pointerId=73;contactSerial=91;releaseCount=1;cancelCount=0;captureLostCount=0;inContact=$false;lastEvent='Released'})}}
)){
    $rejected=$false
    try{Assert-StationaryEdgeContactRelease -PointerId 73 -EdgeObservation $edgeObservation -ExpectedContact $endContact -ReleaseSnapshot $invalidRelease.Snapshot | Out-Null}catch{$rejected=$true}
    if(-not $rejected){throw "Post-release validator incorrectly accepted $($invalidRelease.Name)."}
}

$scriptDirectory = Split-Path -Parent ([IO.Path]::GetFullPath($MyInvocation.MyCommand.Path))
$interopPaths = @(
    $ProbePath,
    (Join-Path $scriptDirectory 'Invoke-MarkdownCorrectnessUiTests.ps1'),
    (Join-Path $scriptDirectory 'Invoke-MarkdownCorrectnessDpiTests.ps1')
)
$interopSources = [Collections.Generic.List[object]]::new()
foreach ($interopPath in $interopPaths) {
    $source = Get-Content -LiteralPath $interopPath -Raw
    $match = [regex]::Match($source,'(?s)Add-Type\s+-TypeDefinition\s+@[''\"]\r?\n(?<code>.*?)\r?\n[''\"]@')
    if (-not $match.Success) { throw "Could not isolate generated interop C# in $interopPath." }
    $interopSources.Add([pscustomobject]@{Path=$interopPath;Code=$match.Groups['code'].Value})
}
$abiMeasurementSource = @'
public static class MarkdownTouchAbiMeasure
{
    private static Type GetType(string name) { if(name == "MarkdownPointerInfo") return typeof(MarkdownPointerInfo); if(name == "MarkdownPointerTouchInfo") return typeof(MarkdownPointerTouchInfo); if(name == "MarkdownPointerTypeInfo") return typeof(MarkdownPointerTypeInfo); throw new ArgumentException(name); }
    public static int Size(string name) { return Marshal.SizeOf(GetType(name)); }
    public static int Offset(string name, string field) { return Marshal.OffsetOf(GetType(name), field).ToInt32(); }
}
'@
foreach ($interop in $interopSources) {
    $code=$interop.Code
    if ($interop.Path -like '*Invoke-MarkdownCorrectnessUiTests.ps1') { $code += [Environment]::NewLine + $abiMeasurementSource }
    if ($code -match '\bout\s+var\b|=>') { throw "Generated interop C# in $($interop.Path) must remain compatible with the Windows PowerShell 5 C# compiler." }
    Add-Type -TypeDefinition $code -Language CSharp
}

$checks = [Collections.Generic.List[object]]::new()
$checks.Add([pscustomobject]@{name='post-release validator accepts a single normal capture-loss cleanup and rejects duplicate release, excess capture loss, generation change, and active contact';status='PASS'})
function Assert-Value {
    param([string]$Name,[long]$Actual,[long]$Expected)
    if ($Actual -ne $Expected) { throw "$Name is $Actual; Windows x64 ABI requires $Expected." }
    $checks.Add([pscustomobject]@{name=$Name;actual=$Actual;expected=$Expected;status='PASS'})
}

if (-not [Environment]::Is64BitProcess) { throw 'Run the offline ABI check in 64-bit PowerShell.' }
Assert-Value 'POINTER_INFO size' ([MarkdownCorrectnessSandboxNative]::PointerInfoSize()) 96
Assert-Value 'POINTER_TOUCH_INFO size' ([MarkdownCorrectnessSandboxNative]::PointerTouchInfoSize()) 144
Assert-Value 'POINTER_TYPE_INFO size' ([Runtime.InteropServices.Marshal]::SizeOf([type][McPointerTypeInfo])) 152
Assert-Value 'POINTER_TYPE_INFO union offset' ([Runtime.InteropServices.Marshal]::OffsetOf([type][McPointerTypeInfo],'Anonymous').ToInt64()) 8
$pointerOffsets = @{
    PointerFlags=12; SourceDevice=16; HwndTarget=24; Pixel=32; Himetric=40; PixelRaw=48; HimetricRaw=56;
    Time=64; HistoryCount=68; InputData=72; KeyStates=76; PerformanceCount=80; ButtonChangeType=88
}
foreach ($field in $pointerOffsets.Keys) {
    Assert-Value "POINTER_INFO.$field offset" ([Runtime.InteropServices.Marshal]::OffsetOf([type][McPointerInfo],$field).ToInt64()) $pointerOffsets[$field]
}
$touchOffsets = @{TouchFlags=96;TouchMask=100;Contact=104;ContactRaw=120;Orientation=136;Pressure=140}
foreach ($field in $touchOffsets.Keys) {
    Assert-Value "POINTER_TOUCH_INFO.$field offset" ([Runtime.InteropServices.Marshal]::OffsetOf([type][McPointerTouchInfo],$field).ToInt64()) $touchOffsets[$field]
}
Assert-Value 'HIGHCONTRAST size' ([Runtime.InteropServices.Marshal]::SizeOf([type][McHighContrast])) 16
Assert-Value 'POINTER_INFO sample ABI size' ([MarkdownTouchAbiMeasure]::Size('MarkdownPointerInfo')) 96
Assert-Value 'POINTER_TOUCH_INFO sample ABI size' ([MarkdownTouchAbiMeasure]::Size('MarkdownPointerTouchInfo')) 144
Assert-Value 'POINTER_TYPE_INFO sample ABI size' ([MarkdownTouchAbiMeasure]::Size('MarkdownPointerTypeInfo')) 152
Assert-Value 'POINTER_TYPE_INFO sample union offset' ([MarkdownTouchAbiMeasure]::Offset('MarkdownPointerTypeInfo','Anonymous')) 8
foreach ($field in $pointerOffsets.Keys) {
    Assert-Value "POINTER_INFO sample.$field offset" ([MarkdownTouchAbiMeasure]::Offset('MarkdownPointerInfo',$field)) $pointerOffsets[$field]
}
foreach ($field in $touchOffsets.Keys) {
    Assert-Value "POINTER_TOUCH_INFO sample.$field offset" ([MarkdownTouchAbiMeasure]::Offset('MarkdownPointerTouchInfo',$field)) $touchOffsets[$field]
}
Assert-Value 'DPI and sample C# interop compiled' 1 1

$holdStart = $probe.IndexOf('# No further pointer frames are sent during the hold:',[StringComparison]::Ordinal)
if ($holdStart -lt 0) { throw 'Stationary-edge helper lacks the explicit no-input hold boundary.' }
$holdEnd = $probe.IndexOf('ConvertTo-AsciiJson -Value $result',$holdStart,[StringComparison]::Ordinal)
if ($holdEnd -lt 0) { throw 'Stationary-edge helper lacks the JSON receipt boundary.' }
$probeTail = $probe.Substring($holdStart,$holdEnd-$holdStart)
if ($probeTail -match 'MarkdownCorrectnessSandboxNative\]::Send\([^\r\n]*::(?:Update|Down)') { throw 'Stationary hold injects a movement/down pointer frame after reaching the edge.' }
if ([regex]::Matches($probeTail,'Get-State \$ViewportMode').Count -lt 3) { throw 'Stationary hold does not sample UIA state at edge, mid-hold, and end-hold.' }
if ([regex]::Matches($probeTail,'Get-TouchContactObservation').Count -lt 4) { throw 'Stationary hold does not sample the routed-event observer at edge, mid-hold, end-hold, and after release.' }
foreach($field in @('contactAtEdge=','$contactEdge','contactMidHold=','$contactMid','contactEndHold=','$contactEnd','contactAfterRelease=','$releaseObservation','observationStatus=''ok''','observationErrors=0','contactSerial=','moveCount=','stationaryFrameCountDuringHold=$keepAliveFrames')) {
    if ($probeTail.IndexOf($field,[StringComparison]::Ordinal) -lt 0) { throw "Stationary-edge receipt omits '$field'." }
}
if ($probe -match 'GetPointerInfo|TryGetPointer|Get-ContactState') { throw 'Stationary contact proof still relies on GetPointerInfo outside the injected-pointer delivery thread.' }
if ($probe -notmatch 'Find-UniqueProcessElement \$ProcessId ''TouchContactObservation''' -or
    $probe.IndexOf('observationErrors -ne 0',[StringComparison]::Ordinal) -lt 0 -or
    $probe.IndexOf("observationStatus -cne 'ok'",[StringComparison]::Ordinal) -lt 0 -or
    $probe.IndexOf('handle -cne $ExpectedHandle',[StringComparison]::Ordinal) -lt 0 -or
    $probe.IndexOf('[int]$edgeRecord.moveCount -ne [int]$endRecord.moveCount',[StringComparison]::Ordinal) -lt 0) {
    throw 'Stationary contact proof must use a healthy sample UIA snapshot and preserve its same active handle/pointer counters.'
}
$checks.Add([pscustomobject]@{name='stationary interval has no injected movement frames and same routed-event contact is sampled through release';status='PASS'})

$followUp = Get-Content -LiteralPath $FollowUpPath -Raw
# T1-021: an idle injected contact is ended by the OS. The no-frame hold
# reports only that exact pattern as a delivery limit; the repeated-position
# variant re-sends one fixed point and must keep position and counters.
$keepAliveClass = $probe.Substring($probe.IndexOf('public sealed class McStationaryKeepAlive',[StringComparison]::Ordinal))
$keepAliveClass = $keepAliveClass.Substring(0,$keepAliveClass.IndexOf("'@",[StringComparison]::Ordinal))
if ($keepAliveClass -notmatch 'Send\(_device,_pointerId,_x,_y,McPointerFlags\.InRange\|McPointerFlags\.InContact\|McPointerFlags\.Update\)' -or
    [regex]::Matches($keepAliveClass,'_x\s*=').Count -ne 1 -or [regex]::Matches($keepAliveClass,'_y\s*=').Count -ne 1) {
    throw 'The keep-alive must re-send only the fixed in-contact edge frame.'
}
if ($probe.IndexOf('if($null -ne $keepAlive){$keepAlive.Dispose();',[StringComparison]::Ordinal) -lt 0 -or
    $probe.IndexOf('if($null -ne $keepAlive){$keepAlive.Dispose();',[StringComparison]::Ordinal) -gt $probe.IndexOf('McPointerFlags]::Up);$upSent=$true',[StringComparison]::Ordinal)) {
    throw 'The keep-alive must stop before the up frame is sent.'
}
if ($probe.IndexOf("(`$KeepAliveMilliseconds -eq 0 -and (",[StringComparison]::Ordinal) -lt 0 -or
    $probe.IndexOf('[int]$edgeRecord.moveCount -ne [int]$midRecord.moveCount',[StringComparison]::Ordinal) -lt 0) {
    throw 'The no-frame hold must keep its exact move-count equality.'
}
if ($probe.IndexOf("throw ('INJECTED-CONTACT-ENDED-BY-OS: ",[StringComparison]::Ordinal) -lt 0 -or
    $followUp.IndexOf("Contains('INJECTED-CONTACT-ENDED-BY-OS: ')",[StringComparison]::Ordinal) -lt 0 -or
    $followUp.IndexOf("throw 'UNVERIFIED: Tooling limit: Windows ended the motionless injected contact",[StringComparison]::Ordinal) -lt 0) {
    throw 'Only the guest OS-ended-contact marker may become UNVERIFIED.'
}
if ([regex]::Matches($followUp,"Invoke-StationaryEdgeCase -Viewport '(owned|ancestor)' -KeepAliveMilliseconds 33").Count -ne 2) {
    throw 'Both viewports must run the repeated-position stationary variant.'
}
$guestProbeFunction=$followUpAst.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-GuestProbe'},$true)
if($null -eq $guestProbeFunction){throw 'Follow-up runner lacks its host-side guest-probe argument builder.'}
$viewportForwarding=@(& {
    function Assert-SandboxEpoch { param([string]$Phase) return $null }
    function Get-Property {
        param([AllowNull()][object]$Object,[string]$Name)
        if($null -eq $Object){return $null}
        $property=$Object.PSObject.Properties[$Name]
        if($null -eq $property){return $null}
        return $property.Value
    }
    function Invoke-NativeChecked {
        param([string[]]$Arguments,[switch]$Json)
        $script:FollowUpMockNativeCalls++
        $script:FollowUpMockNativeArguments=@($Arguments)
        $viewportIndex=[Array]::IndexOf([string[]]$Arguments,'-ViewportMode')
        $resolvedViewport='owned'
        if($viewportIndex -ge 0 -and ($viewportIndex+1) -lt $Arguments.Length){$resolvedViewport=[string]$Arguments[$viewportIndex+1]}
        $payload=ConvertTo-Json -InputObject ([pscustomobject]@{mode='ScrollSemantic';viewportMode=$resolvedViewport}) -Compress
        return [pscustomobject]@{exitCode=0;value=[pscustomobject]@{stdout=$payload};output='offline mock'}
    }

    $script:AppProcessId=4101
    $script:AppWindowHandle=8222L
    $script:ExpectedExeSha256='a'*64
    $script:ExpectedGuestComputerName='offline-guest'
    $script:ExpectedHostComputerName='offline-host'
    $script:ExpectedStartTicks=314159L
    $script:GuestProbePath='C:\offline\MarkdownCorrectnessSandboxProbe.ps1'
    $script:FollowUpMockNativeCalls=0
    $script:FollowUpMockNativeArguments=@()
    Invoke-Expression $guestProbeFunction.Extent.Text
    foreach($viewport in @('owned','ancestor')){
        $result=Invoke-GuestProbe -Mode 'ScrollSemantic' -Needle 'Native menu' -Viewport $viewport
        $viewportIndex=[Array]::IndexOf([string[]]$script:FollowUpMockNativeArguments,'-ViewportMode')
        if($script:FollowUpMockNativeCalls -ne $(if($viewport -eq 'owned'){1}else{2}) -or
           $viewportIndex -lt 0 -or
           $script:FollowUpMockNativeArguments[$viewportIndex+1] -cne $viewport -or
           $result.mode -cne 'ScrollSemantic' -or $result.viewportMode -cne $viewport){
            throw "Host guest-probe wrapper did not functionally forward the '$viewport' viewport for ScrollSemantic."
        }
    }
    [pscustomobject]@{status='PASS';viewports=@('owned','ancestor');nativeCalls=$script:FollowUpMockNativeCalls}
})
if($viewportForwarding.Count -ne 1 -or $viewportForwarding[0].status -cne 'PASS' -or $viewportForwarding[0].nativeCalls -ne 2){throw 'Functional ScrollSemantic viewport forwarding mock did not complete both viewport cases.'}
$checks.Add([pscustomobject]@{name='host ScrollSemantic probe functionally forwards the selected owned or ancestor viewport to the guest command';status='PASS';viewports=@('owned','ancestor')})
$nativeProcessFunction=$followUpAst.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-RedirectedUtf8Process'},$true)
if($null -eq $nativeProcessFunction){throw 'Follow-up runner lacks the redirected UTF-8 process helper.'}
if($nativeProcessFunction.Extent.Text -notmatch 'RedirectStandardOutput\s*=\s*\$true' -or
   $nativeProcessFunction.Extent.Text -notmatch 'RedirectStandardError\s*=\s*\$true' -or
   $nativeProcessFunction.Extent.Text -notmatch 'StandardOutputEncoding\s*=\s*\[System\.Text\.UTF8Encoding\]::new\(\$false\)' -or
   $nativeProcessFunction.Extent.Text -notmatch 'StandardErrorEncoding\s*=\s*\[System\.Text\.UTF8Encoding\]::new\(\$false\)' -or
   $nativeProcessFunction.Extent.Text -notmatch '\$startInfo\.ArgumentList\.Add\('){throw 'Follow-up native commands must use separate redirected UTF-8 streams and structured arguments.'}
if($PSVersionTable.PSEdition -eq 'Core'){
    Invoke-Expression $nativeProcessFunction.Extent.Text
    $childPath=(Get-Process -Id $PID -ErrorAction Stop).Path
    $child=Invoke-RedirectedUtf8Process -FileName $childPath -Arguments @('-NoProfile','-NonInteractive','-Command','[Console]::Out.WriteLine("stdout-sentinel"); [Console]::Error.WriteLine("stderr-sentinel")')
    if($child.ExitCode -ne 0 -or $child.StandardOutput -notmatch 'stdout-sentinel' -or $child.StandardError -notmatch 'stderr-sentinel'){
        throw 'Follow-up redirected process smoke did not preserve distinct stdout/stderr output and exit status.'
    }
}
$checks.Add([pscustomobject]@{name='follow-up command runner redirects UTF-8 stdout/stderr separately and preserves native process status';status='PASS'})
foreach ($required in @(
    'function Invoke-RedirectedUtf8Process',
    'RedirectStandardOutput = $true',
    'RedirectStandardError = $true',
    'StandardOutputEncoding = [System.Text.UTF8Encoding]::new($false)',
    'StandardErrorEncoding = [System.Text.UTF8Encoding]::new($false)',
    'Invoke-RedirectedUtf8Process -FileName $fileName -Arguments $argv',
    '$trimmed = $stdout.Trim()',
    "@('target','exec','sandbox','--json','--'",
    "@('ui') + `$Arguments + @('--on','sandbox'",
    "Invoke-GuestProbe -Mode 'HighContrastSet' -HighContrastFlags ([uint32]`$script:OriginalHighContrast.flags)",
    "Invoke-NativeChecked @('ui','yield','--on','sandbox')",
    'function Assert-AppIdentity',
    'function Assert-StationaryEdgeContactRelease',
    'function Get-GuestComputerName',
    '-ExpectedGuestComputerName',
    '-ExpectedHostComputerName',
    '$record.inContact',
    "-Mode 'PayloadHashes'",
    'function Assert-GuestPayloadIdentity',
    '$script:ExpectedBuildPayloads',
    'payloadIdentity=$script:PayloadIdentity',
    'releaseCaptureLostDelta=$releaseCaptureLostDelta'
)) {
    if ($followUp.IndexOf($required,[StringComparison]::Ordinal) -lt 0) { throw "Follow-up is missing required scope/cleanup contract '$required'." }
}
$probeAstTokens=$null;$probeAstErrors=$null
$probeAst=[System.Management.Automation.Language.Parser]::ParseFile($ProbePath,[ref]$probeAstTokens,[ref]$probeAstErrors)
$windowRootsAst=$probeAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ProcessWindowRoots'},$true)
$processElementsAst=$probeAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ProcessElements'},$true)
$uniqueResolverAst=$probeAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Resolve-UniqueProcessAutomationId'},$true)
$uniqueFinderAst=$probeAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Find-UniqueProcessElement'},$true)
if($null -eq $windowRootsAst -or $null -eq $processElementsAst -or $null -eq $uniqueResolverAst -or $null -eq $uniqueFinderAst){
    throw 'Guest probe must enumerate captured same-process windows and expose raw-tree process element/unique AutomationId helpers.'
}
$windowRootsText=$windowRootsAst.Extent.Text
$processElementsText=$processElementsAst.Extent.Text
$uniqueFinderText=$uniqueFinderAst.Extent.Text
if($windowRootsText.IndexOf('Get-Root',[StringComparison]::Ordinal) -lt 0 -or
   $windowRootsText.IndexOf('NativeWindowHandle',[StringComparison]::Ordinal) -lt 0 -or
   $windowRootsText.IndexOf('ProcessId -ne $TargetProcessId',[StringComparison]::Ordinal) -lt 0 -or
   $windowRootsText.IndexOf('TreeScope]::Children',[StringComparison]::Ordinal) -lt 0 -or
   $windowRootsText.IndexOf('CacheRequest]::new()',[StringComparison]::Ordinal) -lt 0 -or
   $windowRootsText.IndexOf('TreeFilter=[System.Windows.Automation.Automation]::RawViewCondition',[StringComparison]::Ordinal) -lt 0 -or
   $windowRootsText.IndexOf('$rawScope=$cache.Activate()',[StringComparison]::Ordinal) -lt 0 -or
   $windowRootsText.IndexOf('$rawScope.Dispose()',[StringComparison]::Ordinal) -lt 0 -or
   $processElementsText.IndexOf('CacheRequest]::new()',[StringComparison]::Ordinal) -lt 0 -or
   $processElementsText.IndexOf('TreeFilter=[System.Windows.Automation.Automation]::RawViewCondition',[StringComparison]::Ordinal) -lt 0 -or
   $processElementsText.IndexOf('$rawScope=$cache.Activate()',[StringComparison]::Ordinal) -lt 0 -or
   $processElementsText.IndexOf('FindAll([System.Windows.Automation.TreeScope]::Descendants',[StringComparison]::Ordinal) -lt 0 -or
   $processElementsText.IndexOf('Condition]::TrueCondition',[StringComparison]::Ordinal) -lt 0 -or
   $processElementsText.IndexOf('$rawScope.Dispose()',[StringComparison]::Ordinal) -lt 0 -or
   $processElementsText.IndexOf('$rootCurrent.ProcessId -ne $TargetProcessId',[StringComparison]::Ordinal) -lt 0 -or
   $processElementsText.IndexOf('$rootHandle -le 0',[StringComparison]::Ordinal) -lt 0 -or
   $processElementsText.IndexOf('$elementProcessId -ne $TargetProcessId -and $elementProcessId -ne 0',[StringComparison]::Ordinal) -lt 0 -or
   $processElementsText.IndexOf('ownerProcessId=$TargetProcessId',[StringComparison]::Ordinal) -lt 0 -or
   $processElementsText.IndexOf('ownerWindowHandle=$rootHandle',[StringComparison]::Ordinal) -lt 0 -or
   $processElementsText.IndexOf('reportedProcessId=$elementProcessId',[StringComparison]::Ordinal) -lt 0 -or
   $processElementsText.IndexOf('GetRuntimeId',[StringComparison]::Ordinal) -lt 0 -or
   $processElementsText -match 'TreeWalker\]::ControlViewWalker' -or
   $uniqueResolverAst.Extent.Text.IndexOf('$ownerProcessId -ne $TargetProcessId -or $ownerWindowHandle -le 0',[StringComparison]::Ordinal) -lt 0 -or
   $uniqueResolverAst.Extent.Text.IndexOf('$candidate.element.Current',[StringComparison]::Ordinal) -lt 0 -or
   $uniqueResolverAst.Extent.Text.IndexOf('$candidate.reportedProcessId -eq $reportedProcessId',[StringComparison]::Ordinal) -lt 0 -or
   $uniqueResolverAst.Extent.Text.IndexOf('$reportedProcessId -eq 0',[StringComparison]::Ordinal) -lt 0 -or
   $uniqueFinderText.IndexOf('Get-ProcessElements',[StringComparison]::Ordinal) -lt 0 -or
   $uniqueFinderText.IndexOf('Resolve-UniqueProcessAutomationId',[StringComparison]::Ordinal) -lt 0){
    throw 'Process-wide UIA lookup must verify exact same-PID HWND roots before raw traversal, preserve descendant provenance for PID-zero peers, exclude foreign processes/roots, de-duplicate elements, and resolve IDs uniquely.'
}
Invoke-Expression $uniqueResolverAst.Extent.Text
$currentPageElement=[pscustomobject]@{Current=[pscustomobject]@{ProcessId=0;AutomationId='CurrentSamplePage';Name='page:TouchSelection'}}
$foreignPageElement=[pscustomobject]@{Current=[pscustomobject]@{ProcessId=8381;AutomationId='CurrentSamplePage';Name='foreign-window'}}
$otherControlElement=[pscustomobject]@{Current=[pscustomobject]@{ProcessId=8380;AutomationId='MarkdownEditor';Name='MarkdownEditor'}}
$currentPage=[pscustomobject]@{element=$currentPageElement;ownerProcessId=8380;ownerWindowHandle=0x10001;reportedProcessId=0}
$foreignPage=[pscustomobject]@{element=$foreignPageElement;ownerProcessId=8380;ownerWindowHandle=0x10001;reportedProcessId=8381}
$otherControl=[pscustomobject]@{element=$otherControlElement;ownerProcessId=8380;ownerWindowHandle=0x10001;reportedProcessId=8380}
$foreignRootZero=[pscustomobject]@{element=[pscustomobject]@{Current=[pscustomobject]@{ProcessId=0;AutomationId='CurrentSamplePage';Name='foreign-root'}};ownerProcessId=8381;ownerWindowHandle=0x20002;reportedProcessId=0}
$resolvedPage=Resolve-UniqueProcessAutomationId -Elements @($foreignPage,$otherControl,$currentPage) -TargetProcessId 8380 -AutomationId 'CurrentSamplePage'
if(-not [object]::ReferenceEquals($resolvedPage,$currentPageElement)){throw 'Raw process resolver did not accept a PID-zero CurrentSamplePage descended from the exact verified app HWND.'}
foreach($invalidCandidates in @(
    [pscustomobject]@{Name='missing';Elements=@($foreignPage,$otherControl,$foreignRootZero);ExpectedCount=0},
    [pscustomobject]@{Name='duplicate same-root zero-PID ID';Elements=@($currentPage,[pscustomobject]@{element=[pscustomobject]@{Current=[pscustomobject]@{ProcessId=0;AutomationId='CurrentSamplePage';Name='duplicate'}};ownerProcessId=8380;ownerWindowHandle=0x10001;reportedProcessId=0});ExpectedCount=2},
    [pscustomobject]@{Name='foreign-root zero-PID element';Elements=@($foreignRootZero);ExpectedCount=0},
    [pscustomobject]@{Name='foreign nonzero descendant';Elements=@($foreignPage);ExpectedCount=0},
    [pscustomobject]@{Name='unverified zero-PID root handle';Elements=@([pscustomobject]@{element=$currentPageElement;ownerProcessId=8380;ownerWindowHandle=0;reportedProcessId=0});ExpectedCount=0}
)){
    $rejected=$false
    try{Resolve-UniqueProcessAutomationId -Elements $invalidCandidates.Elements -TargetProcessId 8380 -AutomationId 'CurrentSamplePage' | Out-Null}catch{$rejected=$_.Exception.Message.IndexOf("found $($invalidCandidates.ExpectedCount).",[StringComparison]::Ordinal) -ge 0}
    if(-not $rejected){throw "Raw process resolver did not reject the $($invalidCandidates.Name) CurrentSamplePage candidate set."}
}
$checks.Add([pscustomobject]@{name='raw UIA process scan accepts PID-zero shell peers only from exact verified app/popup HWND roots and rejects foreign roots, foreign processes, missing, or duplicate IDs';status='PASS'})

# Exercise the standalone guest probe's real collector, not only its source
# contract/resolver. The production outer list stores provenance envelopes,
# while the candidate list stores UIA elements. Mock only the latter's type so
# the exact production collector can accept these AutomationElement-shaped
# test doubles. This catches both a stale List[AutomationElement] envelope
# declaration and later consumer assumptions about raw elements versus wraps.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$outerEnvelopeDeclaration='$elements=[Collections.Generic.List[object]]::new()'
if([regex]::Matches($processElementsText,[regex]::Escape($outerEnvelopeDeclaration)).Count -ne 1){
    throw 'Standalone probe Get-ProcessElements must use List[object] for its provenance-envelope output.'
}
$candidateDeclaration='$candidates=[Collections.Generic.List[System.Windows.Automation.AutomationElement]]::new()'
if([regex]::Matches($processElementsText,[regex]::Escape($candidateDeclaration)).Count -ne 1){
    throw 'Standalone probe Get-ProcessElements candidate storage must remain typed as AutomationElement.'
}
$collectorSource=$processElementsText.Replace($candidateDeclaration,'$candidates=[Collections.Generic.List[object]]::new()')
Invoke-Expression $collectorSource
Invoke-Expression $uniqueFinderAst.Extent.Text
function New-ProbeMockAutomationElement {
    param([int]$ElementProcessId,[long]$NativeWindowHandle,[string]$AutomationId,[string]$Name,[int[]]$RuntimeId)
    $element=[pscustomobject]@{
        Current=[pscustomobject]@{ProcessId=$ElementProcessId;NativeWindowHandle=$NativeWindowHandle;AutomationId=$AutomationId;Name=$Name}
        RuntimeId=$RuntimeId
        Children=[Collections.ArrayList]::new()
    }
    $element | Add-Member -MemberType ScriptMethod -Name GetRuntimeId -Value { return ,([int[]]$this.RuntimeId) }
    $element | Add-Member -MemberType ScriptMethod -Name FindAll -Value { param($TreeScope,$Condition) return ,$this.Children }
    return $element
}
$probeCollectorRoot=New-ProbeMockAutomationElement -ElementProcessId 8380 -NativeWindowHandle 0x10001 -AutomationId 'VerifiedWindow' -Name 'Mock verified window' -RuntimeId @(21,1)
$probeLocalPage=New-ProbeMockAutomationElement -ElementProcessId 0 -NativeWindowHandle 0 -AutomationId 'CurrentSamplePage' -Name 'page:TouchSelection' -RuntimeId @(21,2)
$probeAppEditor=New-ProbeMockAutomationElement -ElementProcessId 8380 -NativeWindowHandle 0 -AutomationId 'MarkdownEditor' -Name 'MarkdownEditor' -RuntimeId @(21,3)
$probeForeignPeer=New-ProbeMockAutomationElement -ElementProcessId 8381 -NativeWindowHandle 0 -AutomationId 'ForeignPeer' -Name 'must be excluded' -RuntimeId @(21,4)
[void]$probeCollectorRoot.Children.Add($probeLocalPage)
[void]$probeCollectorRoot.Children.Add($probeAppEditor)
[void]$probeCollectorRoot.Children.Add($probeForeignPeer)
$script:MockProbeCollectorRoots=@($probeCollectorRoot)
$script:AllowUnverifiedProbeRootForNegativeTest=$false
function Get-ProcessWindowRoots {
    param([int]$TargetProcessId)
    if($TargetProcessId -ne 8380){throw "Standalone probe mock refused unapproved PID $TargetProcessId."}
    if(-not $script:AllowUnverifiedProbeRootForNegativeTest -and ($script:MockProbeCollectorRoots.Count -ne 1 -or
       [int]$script:MockProbeCollectorRoots[0].Current.ProcessId -ne 8380 -or
       [long]$script:MockProbeCollectorRoots[0].Current.NativeWindowHandle -ne 0x10001)){
        throw 'Standalone probe mock root is not the exact verified app PID/HWND.'
    }
    return ,$script:MockProbeCollectorRoots
}
$probeCollected=@(Get-ProcessElements -TargetProcessId 8380)
if($probeCollected.Count -ne 3){throw "Standalone probe collector should retain the verified root, PID-zero local peer, and same-process peer while excluding the foreign peer; got $($probeCollected.Count)."}
$probeResolved=Find-UniqueProcessElement -TargetProcessId 8380 -AutomationId 'CurrentSamplePage'
if(-not [object]::ReferenceEquals($probeResolved,$probeLocalPage)){
    throw 'Standalone probe Find-UniqueProcessElement must return the raw AutomationElement, not its provenance envelope.'
}
if(@($probeCollected|Where-Object{$_.element -eq $probeForeignPeer}).Count -ne 0){throw 'Standalone probe collector admitted an explicit foreign-process descendant.'}
if(@($probeCollected|Where-Object{$_.ownerProcessId -ne 8380 -or $_.ownerWindowHandle -ne 0x10001}).Count -ne 0){throw 'Standalone probe collector emitted a provenance envelope without verified owner identity.'}
$foreignCollectorRoot=New-ProbeMockAutomationElement -ElementProcessId 8381 -NativeWindowHandle 0x20002 -AutomationId 'ForeignWindow' -Name 'Must reject' -RuntimeId @(22,1)
$script:MockProbeCollectorRoots=@($foreignCollectorRoot)
$script:AllowUnverifiedProbeRootForNegativeTest=$true
$foreignRootRejected=$false
try{Get-ProcessElements -TargetProcessId 8380 | Out-Null}catch{$foreignRootRejected=$_.Exception.Message -like '*verified same-process HWND*'}
$script:AllowUnverifiedProbeRootForNegativeTest=$false
if(-not $foreignRootRejected){throw 'Standalone probe collector did not reject an unverified foreign root.'}
$checks.Add([pscustomobject]@{name='actual standalone guest collector emits object provenance envelopes, preserves raw-element lookup, accepts a PID-zero local peer, and excludes/rejects foreign process/root';status='PASS'})

$mainPath=Join-Path $scriptDirectory 'Invoke-MarkdownCorrectnessUiTests.ps1'
$mainSource=Get-Content -LiteralPath $mainPath -Raw
foreach($required in @(
    "Test-Case -Id 'C09' -Name 'mouse-selection-crosses-lines-formatting-lists-and-table-and-keyboard-adjusts-handle-endpoint'",
    "'MarkdownRenderer','--gesture','double-tap','--at'",
    'Assert-SelectionHandles',
    "'focus','MarkdownSelectionEndHandle'",
    "'right','--via','send-input'",
    "'left','--via','send-input'",
    "-Mode 'focus-state' -AutomationId 'MarkdownSelectionEndHandle'",
    'keyboardTarget,',
    "Test-Case -Id 'P1-003'",
    'function Get-TouchContactObservation',
    'function Assert-SyntheticTouchDelivery',
    '$observerBefore=Get-TouchContactObservation',
    '$delivery=Assert-SyntheticTouchDelivery',
    '$injection.delivery.cancelDelta',
    '$injection.delivery.activePointerCount'
)){
    if($mainSource.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "Main UI coverage lacks independently asserted keyboard/cancel-observer requirement '$required'."}
}
if($mainSource.IndexOf("'focus-state', 'scroll'",[StringComparison]::Ordinal) -lt 0){
    throw 'Invoke-GuestProbe does not admit the focus-state mode through its validated mode contract.'
}
$checks.Add([pscustomobject]@{name='main UI batch verifies keyboard endpoint extension/contraction and routed canceled-contact delivery separately from preserved selection';status='PASS'})
$rectanglesAst=$probeAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ConvertTo-RangeRectangles'},$true)
if($null -eq $rectanglesAst){throw 'Guest probe must normalize typed UIA Rect[] and flattened bounding-rectangle results.'}
Add-Type -AssemblyName WindowsBase
Invoke-Expression $rectanglesAst.Extent.Text
function New-TestRange {
    param([object[]]$Values)
    $range=[pscustomobject]@{Values=$Values}
    $range | Add-Member -MemberType ScriptMethod -Name GetBoundingRectangles -Value { return $this.Values }
    return $range
}
$singleRect=@(ConvertTo-RangeRectangles (New-TestRange @([System.Windows.Rect]::new(10,20,30,40))))
if($singleRect.Count -ne 1 -or $singleRect[0].X -ne 10 -or $singleRect[0].Y -ne 20 -or $singleRect[0].Width -ne 30 -or $singleRect[0].Height -ne 40){throw 'Typed UIA Rect[] single-range geometry was not preserved.'}
$multipleRects=@(ConvertTo-RangeRectangles (New-TestRange @([System.Windows.Rect]::new(10,20,30,40),[System.Windows.Rect]::new(50,60,70,80))))
if($multipleRects.Count -ne 2 -or $multipleRects[1].X -ne 50 -or $multipleRects[1].Y -ne 60){throw 'Typed UIA Rect[] multi-line geometry was not preserved.'}
$emptyRectangles=@(ConvertTo-RangeRectangles (New-TestRange @()))
if($emptyRectangles.Count -ne 0){throw 'Empty UIA rectangle results must remain empty so callers cannot manufacture geometry.'}
$flatRectangles=@(ConvertTo-RangeRectangles (New-TestRange @([double]10,[double]20,[double]30,[double]40,[double]50,[double]60,[double]70,[double]80)))
if($flatRectangles.Count -ne 2 -or $flatRectangles[1].X -ne 50 -or $flatRectangles[1].Y -ne 60){throw 'Legacy flattened UIA bounding-rectangle arrays must remain supported.'}
$checks.Add([pscustomobject]@{name='typed, multi-rectangle, empty, and flattened UIA text geometry normalization';status='PASS'})
$getRangeAst=$probeAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-Range'},$true)
$getStateAst=$probeAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-State'},$true)
if($null -eq $getRangeAst -or $getRangeAst.Extent.Text.IndexOf('ConvertTo-RangeRectangles $range',[StringComparison]::Ordinal) -lt 0 -or
   $null -eq $getStateAst -or $getStateAst.Extent.Text.IndexOf('ConvertTo-RangeRectangles $range',[StringComparison]::Ordinal) -lt 0){throw 'Guest range and selection-state geometry must both use the validated rectangle converter.'}
$pointRangeAst=$probeAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-PointRange'},$true)
if($null -eq $pointRangeAst -or $pointRangeAst.Extent.Text.IndexOf('.RangeFromPoint($point)',[StringComparison]::Ordinal) -lt 0){throw 'PointRange must call the real TextPattern.RangeFromPoint method, not substitute a FindText range.'}
Invoke-Expression $pointRangeAst.Extent.Text
$script:PointRangeCallCount=0
$script:PointRangeReferenceText='family'
$script:PointRangeResult=New-TestRange @([System.Windows.Rect]::new(10,20,30,40))
$script:PointRangeResult | Add-Member -MemberType NoteProperty -Name Text -Value 'family'
$script:PointRangeResult | Add-Member -MemberType NoteProperty -Name ExpandedToWord -Value $false
$script:PointRangeResult | Add-Member -MemberType ScriptMethod -Name GetText -Value {param($Length) return $this.Text}
$script:PointRangeResult | Add-Member -MemberType ScriptMethod -Name ExpandToEnclosingUnit -Value {param($Unit) $this.ExpandedToWord=($Unit -eq [System.Windows.Automation.Text.TextUnit]::Word)}
$script:PointRangePattern=[pscustomobject]@{}
$script:PointRangePattern | Add-Member -MemberType ScriptMethod -Name RangeFromPoint -Value {param($Point) $script:PointRangeCallCount++;return $script:PointRangeResult}
function Get-Range {param([string]$Text) return [pscustomobject]@{text=$script:PointRangeReferenceText;rect=[System.Windows.Rect]::new(10,20,30,40)}}
function Get-TextPattern {return [pscustomobject]@{pattern=$script:PointRangePattern;renderer=[pscustomobject]@{Current=[pscustomobject]@{BoundingRectangle=[System.Windows.Rect]::new(0,0,100,100)}}}}
$pointResult=Get-PointRange -Text 'family'
if($script:PointRangeCallCount -ne 1 -or -not $script:PointRangeResult.ExpandedToWord -or $pointResult.text -cne 'family' -or $pointResult.point.x -ne 25 -or $pointResult.point.y -ne 40){throw 'PointRange did not use the painted-word center and expand the returned point range to the exact word.'}
foreach($invalidPointRange in @('wrong text','wrong reference','empty geometry','multiple geometry','noncontaining geometry','null range')){
    $savedResult=$script:PointRangeResult
    $savedValues=$savedResult.Values
    try{
        switch($invalidPointRange){
            'wrong text' {$script:PointRangeResult.Text='other'}
            'wrong reference' {$script:PointRangeReferenceText='other'}
            'empty geometry' {$script:PointRangeResult.Values=@()}
            'multiple geometry' {$script:PointRangeResult.Values=@([System.Windows.Rect]::new(10,20,30,40),[System.Windows.Rect]::new(50,60,10,10))}
            'noncontaining geometry' {$script:PointRangeResult.Values=@([System.Windows.Rect]::new(50,60,10,10))}
            'null range' {$script:PointRangeResult=$null}
        }
        $rejected=$false
        try{Get-PointRange -Text 'family' | Out-Null}catch{$rejected=$true}
        if(-not $rejected){throw "PointRange accepted $invalidPointRange."}
    }
    finally{
        $script:PointRangeResult=$savedResult
        $script:PointRangeResult.Text='family'
        $script:PointRangeResult.Values=$savedValues
        $script:PointRangeReferenceText='family'
    }
}
$pointCaseAst=$followUpAst.Find({param($node)$node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-PointRangeCase'},$true)
if($null -eq $pointCaseAst -or $pointCaseAst.Extent.Text -notmatch "finally\s*\{\s*\`$null=Invoke-GuestProbe -Mode 'ResizeWindow'" -or
   $pointCaseAst.Extent.Text.IndexOf("-Mode 'Bounds' -Needle 'Native menu'",[StringComparison]::Ordinal) -lt 0 -or
   $pointCaseAst.Extent.Text.IndexOf('rectangleCount -ne 0',[StringComparison]::Ordinal) -lt 0){throw 'Point-range regression must check absent diagram geometry and restore the exact original window size even on failure.'}
foreach($mode in @('PointRange','Bounds','ScrollSemantic','ResizeWindow')){
    if($probeAst.ParamBlock.Extent.Text.IndexOf("'$mode'",[StringComparison]::Ordinal) -lt 0){throw "Guest validated modes omit $mode."}
}
if($pointCaseAst.Extent.Text.IndexOf("-Mode 'ScrollSemantic' -Needle 'Native menu'",[StringComparison]::Ordinal) -lt 0 -or
   $pointCaseAst.Extent.Text.IndexOf('$scroll.after.verticalPercent -le [double]$scroll.before.verticalPercent',[StringComparison]::Ordinal) -lt 0 -or
   $pointCaseAst.Extent.Text.IndexOf("-Mode 'Bounds' -Needle 'One-finger pan'",[StringComparison]::Ordinal) -lt 0 -or
   $pointCaseAst.Extent.Text.IndexOf('$topError -gt (64*',[StringComparison]::Ordinal) -lt 0){throw 'Clipped semantic ScrollIntoView must independently assert advancement to the block location and target-block label visibility near the top.'}
if($followUp.IndexOf('$restoredPage.name -cne [string]$script:OriginalAppState.page',[StringComparison]::Ordinal) -lt 0){throw 'Final sample-page restoration must verify its actual UIA state.'}
if($followUp.IndexOf('C11-uia-point-range-owned',[StringComparison]::Ordinal) -lt 0 -or $followUp.IndexOf('C11-uia-point-range-ancestor',[StringComparison]::Ordinal) -lt 0){throw 'Real COM point-query regression must cover both viewport ownership modes.'}
$checks.Add([pscustomobject]@{name='point-range query uses the visible center and exact word, rejects false geometry/text, covers both viewports, and restores original size';status='PASS'})
$asciiJsonFunction=$probeAst.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'ConvertTo-AsciiJson'},$true)
if($null -eq $asciiJsonFunction){throw 'Guest probe must ASCII-escape every non-ASCII UTF-16 code unit before returning JSON through target exec.'}
Invoke-Expression $asciiJsonFunction.Extent.Text
$emoji=[string]::new([char[]]@(0xD83D,0xDE00))
$technologist=[string]::new([char[]]@(0xD83D,0xDC69,0xD83C,0xDFFD,0x200D,0xD83D,0xDCBB))
$wireJson=ConvertTo-AsciiJson -Value @{smile=$emoji;technologist=$technologist}
if(@($wireJson.ToCharArray()|Where-Object{[int]$_ -gt 0x7f}).Count -gt 0){throw 'Guest probe JSON still contains non-ASCII code units that can be corrupted by Windows PowerShell stdout encoding.'}
$unicodeRoundTrip=ConvertFrom-Json -InputObject $wireJson
if($unicodeRoundTrip.smile -cne $emoji -or $unicodeRoundTrip.technologist -cne $technologist){throw 'ASCII-safe guest JSON did not preserve complete emoji/ZWJ grapheme UTF-16 sequences.'}
$checks.Add([pscustomobject]@{name='guest probe JSON preserves emoji/grapheme content through an ASCII-only target-exec stream';status='PASS'})
if ($followUp -match 'SystemParametersInfo|Set-ItemProperty|reg\.exe') { throw 'Host orchestrator must not mutate Windows display/contrast settings directly.' }
if($followUp.IndexOf("if (-not [string]::IsNullOrEmpty(`$HighContrastScheme))",[StringComparison]::Ordinal) -lt 0){throw 'The Sandbox restore path must omit empty optional High Contrast scheme arguments.'}
if ($probe.IndexOf('$currentComputerName=[Environment]::MachineName',[StringComparison]::Ordinal) -lt 0 -or
    $probe.IndexOf('This guest probe is running on the host',[StringComparison]::Ordinal) -lt 0 -or
    $probe.IndexOf('This process is not in the approved Sandbox',[StringComparison]::Ordinal) -lt 0) {
    throw 'Guest-only computer-name verification must run before all native/UI operations, including High Contrast restore.'
}
$guestGuard=$probe.IndexOf('$currentComputerName=[Environment]::MachineName',[StringComparison]::Ordinal)
$nativeInitialization=$probe.IndexOf("if (-not ('MarkdownCorrectnessSandboxNative' -as [type]))",[StringComparison]::Ordinal)
if($nativeInitialization -lt $guestGuard){throw 'Guest-only machine refusal must happen before compiling or calling native interop.'}
if($probe.IndexOf('$a.range.CompareEndpoints($startPoint,$b.range,$endPoint)',[StringComparison]::Ordinal) -lt 0){throw 'ExpectedRange must normalize endpoint order before moving UIA range boundaries.'}
foreach($required in @(
    "GetMethod('CompareEndpoints').GetParameters()[0].ParameterType",
    '[System.Enum]::Parse($endpointType,$StartEndpoint)',
    'Find-UniqueProcessElement $ProcessId $AutomationId',
    'function Get-ProcessWindowRoots {',
    'TreeFilter=[System.Windows.Automation.Automation]::RawViewCondition',
    'TreeWalker]::RawViewWalker',
    'Resolve-UniqueProcessAutomationId',
    'isEnabled=[bool]$current.IsEnabled',
    'private static McPointerTypeInfo Frame(uint pointerId,int x,int y,McPointerFlags flags)',
    'new McPointerInfo { PointerType=2,PointerId=pointerId,PointerFlags=flags,Pixel=new McPoint(x,y) }',
    'TouchMask=McTouchMask.ContactArea',
    'PointerFeedbackNone=3',
    'CreateSyntheticPointerDevice(2,1,PointerFeedbackNone)',
    'InjectSyntheticPointerInput(device,one,1)',
    'DestroyTouchDevice($touchDevice)',
    'Win32 error "+error+" / 0x"+error.ToString("X8")',
    'public static void Send(IntPtr device,uint pointerId,int x,int y,McPointerFlags flags)',
    'Send($touchDevice,$PointerId,$startX,$startY,[McPointerFlags]::InRange -bor [McPointerFlags]::InContact -bor [McPointerFlags]::Down',
    'Send($touchDevice,$PointerId,$x,$y,[McPointerFlags]::InRange -bor [McPointerFlags]::InContact -bor [McPointerFlags]::Update',
    'Send($touchDevice,$PointerId,'
)){
    if($probe.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "Probe is missing the canonical touch/UIA contract '$required'."}
}
if($probe -match 'McPointerFlags\.New|FrameId=|SourceDevice=|HwndTarget=|Himetric=|HistoryCount=|ContactRaw\s*='){throw 'Guest probe still explicitly sets noncanonical injected POINTER_INFO fields.'}
foreach($mode in @('HighContrastRead','HighContrastSet')){
    $guardFailure=$null
    try{
        & $ProbePath -Mode $mode -ProcessId 1 -WindowHandle 1 -ExpectedExeSha256 ('0'*64) -ExpectedGuestComputerName 'approved-sandbox-guest' -ExpectedHostComputerName ([Environment]::MachineName) -HighContrastFlags 1 -HighContrastScheme 'must-not-be-applied' | Out-Null
    }catch{$guardFailure=$_.Exception.Message}
    if($null -eq $guardFailure -or $guardFailure.IndexOf('This guest probe is running on the host',[StringComparison]::Ordinal) -lt 0){throw "Guest-only guard did not refuse host-side High Contrast $mode before native access."}
}
$checks.Add([pscustomobject]@{name='host refuses guest-only High Contrast read/set before native access';status='PASS'})
if ($probe -notmatch 'contactAtEdge=\$contactEdge;contactMidHold=\$contactMid;contactEndHold=\$contactEnd;contactAfterRelease=\$releaseObservation' -or
    $probe -match 'GetPointerInfo|TryGetPointer|Get-ContactState') {
    throw 'Stationary edge evidence must use the sample event observer instead of an invalid external GetPointerInfo query.'
}
$probeReleaseChecks=@(
    'releaseCount -ne ([int]$edgeRecord.releaseCount+1)',
    'releaseCaptureLostDelta -lt 0 -or $releaseCaptureLostDelta -gt 1',
    'lastEvent -notin',
    'rendererGeneration -ne [long]$contactEdge.rendererGeneration',
    '[int]$edgeRecord.captureLostCount -ne [int]$endRecord.captureLostCount'
)
foreach($required in $probeReleaseChecks){if($probe.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "Guest stationary probe is missing release/hold invariant '$required'."}}
$checks.Add([pscustomobject]@{name='guest stationary probe keeps capture stable during hold and accepts exactly one normal capture-loss cleanup after release';status='PASS'})
if ($followUp -notmatch "C10-stationary-edge-owned" -or $followUp -notmatch "C10-stationary-edge-ancestor") { throw 'Stationary edge coverage must include both viewport ownership modes.' }
if ($followUp -notmatch "C10-handle-crossing-owned" -or $followUp -notmatch "C10-handle-crossing-ancestor") { throw 'Handle crossing coverage must include both viewport ownership modes.' }
if($probe.IndexOf('selectionRects=@($selectionRects.ToArray())',[StringComparison]::Ordinal) -lt 0 -or
   $followUp.IndexOf('function Get-HandleDragDestination',[StringComparison]::Ordinal) -lt 0 -or
   $followUp.IndexOf('fingerToCaretOffsetDip',[StringComparison]::Ordinal) -lt 0){throw 'Crossing automation must calibrate the actual fingertip-to-caret offset, not drag raw range geometry.'}
$checks.Add([pscustomobject]@{name='guest-only probes, exact target scoping, finally restoration/yield, both viewport variants';status='PASS'})

# Execute the real coordinate helpers with unequal-height rows. Source-string
# presence alone cannot establish that their two baseline calculations agree.
foreach($functionName in @('Get-RangeBoundaryPoint','Get-HandleDragDestination')){
    $functionAst=$followUpAst.Find({param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName},$true)
    if($null -eq $functionAst){throw "Follow-up lacks coordinate helper '$functionName'."}
    Invoke-Expression $functionAst.Extent.Text
}
function Invoke-GuestProbe {
    param([string]$Mode,[string]$Needle)
    if($Mode -cne 'Range' -or $Needle -cne 'target'){throw 'Coordinate fixture requested an unexpected guest operation.'}
    return [pscustomobject]@{text='target';rect=[pscustomobject]@{x=90;y=110;width=50;height=27}}
}
$sourceState=[pscustomobject]@{
    rangeCount=1;dpi=96
    selectionRects=@([pscustomobject]@{x=20;y=30;width=40;height=18})
    handles=@(
        [pscustomobject]@{automationId='MarkdownSelectionStartHandle';offscreen=$false;x=-2;y=48;width=44;height=44},
        [pscustomobject]@{automationId='MarkdownSelectionEndHandle';offscreen=$false;x=38;y=48;width=44;height=44}
    )
}
foreach($endpoint in @('Start','End')){
    $caret=Get-RangeBoundaryPoint -Text 'target' -Endpoint $endpoint
    $destination=Get-HandleDragDestination -InitialState $sourceState -HandleAutomationId "MarkdownSelection${endpoint}Handle" -SourceEndpoint $endpoint -TargetCaret $caret
    $expectedX=if($endpoint -eq 'Start'){91}else{139}
    if($caret.y -ne 137 -or $destination.y -ne 159 -or $destination.x -ne $expectedX -or $destination.sourceCaret.y -ne 48){throw "The $endpoint handle destination does not preserve the renderer's bottom-edge caret anchor across unequal text heights."}
}
$checks.Add([pscustomobject]@{name='both crossing endpoints use bottom-edge caret anchors across unequal-height rows';status='PASS'})
$hcCaseStart=$followUp.IndexOf("Test-Case -Id 'C13-actual-system-high-contrast'",[StringComparison]::Ordinal)
$hcScroll=$followUp.IndexOf("Invoke-GuestProbe -Mode 'ScrollToTop' -Viewport 'owned'",$hcCaseStart,[StringComparison]::Ordinal)
if($hcCaseStart -lt 0 -or $hcScroll -lt 0 -or $followUp.Substring($hcCaseStart,$hcScroll-$hcCaseStart).IndexOf("Set-ToggleState -AutomationId 'TouchViewportOwnershipToggle' -Enabled `$false",[StringComparison]::Ordinal) -lt 0){throw 'Actual High Contrast must explicitly select its owned viewport before scrolling/input.'}
if($probe.IndexOf('holdMilliseconds=$stationaryWatch.Elapsed.TotalMilliseconds',[StringComparison]::Ordinal) -lt 0 -or $probe.Contains('holdMilliseconds=1200')){throw 'Stationary duration must report the measured interval, not an inaccurate hardcoded hold time.'}
$checks.Add([pscustomobject]@{name='High Contrast establishes owned viewport and stationary duration is measured';status='PASS'})

$mainPath=Join-Path $scriptDirectory 'Invoke-MarkdownCorrectnessUiTests.ps1'
$mainSource=Get-Content -LiteralPath $mainPath -Raw
foreach($required in @(
    "'payload-hashes'",
    "'image-state'",
    'Wait-ForImagePeerState -AltText ''Script-bearing SVG''',
    'Wait-ForImagePeerState -AltText ''Animated SVG''',
    'svg-script-and-animation-use-accessible-failure-placeholders',
    "'^Image unavailable:\s*'",
    '$heightRoundingError -gt 1.0',
    'Assert-GuestPayloadIdentity -BuildPayloads',
    'payloadIdentity = $payloadIdentity',
    'final-math-object-selection-owned-viewport',
    'final-math-object-selection-ancestor-viewport',
    'final-mermaid-object-selection-owned-viewport',
    'final-mermaid-object-selection-ancestor-viewport',
    'Test-FinalVectorObjectSelection -Kind Math -Viewport owned',
    'Test-FinalVectorObjectSelection -Kind Math -Viewport ancestor',
    'Test-FinalVectorObjectSelection -Kind Mermaid -Viewport owned',
    'Test-FinalVectorObjectSelection -Kind Mermaid -Viewport ancestor',
    'expectedSelection = "$boundary`n$semanticText"',
    'Copy as Markdown'
)) {
    if($mainSource.IndexOf($required,[StringComparison]::Ordinal) -lt 0){throw "Main correctness batch lacks required vector/payload assertion '$required'."}
}
$checks.Add([pscustomobject]@{name='main correctness batch explicitly verifies active SVG failure placeholders through UIA';status='PASS'})
$c12Start=$mainSource.IndexOf("Test-Case -Id 'C12' -Name 'final-math-object-selection-owned-viewport'",[StringComparison]::Ordinal)
$c14Start=if($c12Start -lt 0){-1}else{$mainSource.IndexOf("Test-Case -Id 'C14'",$c12Start,[StringComparison]::Ordinal)}
if($c12Start -lt 0 -or $c14Start -le $c12Start -or $mainSource.Substring($c12Start,$c14Start-$c12Start).Contains('GetNewClosure')){throw 'C12 Math/Mermaid cases must use explicit bodies rather than dynamically captured closure state.'}
if($mainSource.IndexOf('y = [double]$boundaryRect.y + [double]$boundaryRect.height',[StringComparison]::Ordinal) -lt 0){throw 'C12 source endpoint calibration must use the trailing caret at the text rectangle bottom.'}
$checks.Add([pscustomobject]@{name='main correctness batch uses explicit C12 bodies and trailing-caret calibration';status='PASS'})
$checks.Add([pscustomobject]@{name='main Sandbox batch qualifies managed payload hashes and Math/Mermaid final-atomic selection in both viewport modes';status='PASS'})

[pscustomobject]@{status='PASS';checks=@($checks.ToArray());liveUiExecuted=$false;details='Offline parser and ABI checks only; this script did not access winapp, inject input, launch an app, or change High Contrast.'} |
    ConvertTo-Json -Depth 8
