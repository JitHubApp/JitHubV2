[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$runnerPath = Join-Path $PSScriptRoot 'Invoke-MarkdownTouchLifecycleSandboxTests.ps1'
$tokens = $null
$parseErrors = $null
$runnerAst = [System.Management.Automation.Language.Parser]::ParseFile($runnerPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "Touch-lifecycle host runner does not parse: $(($parseErrors | ForEach-Object { $_.Message }) -join '; ')"
}

function Assert-Condition {
    param([Parameter(Mandatory)][bool]$Condition, [Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([Parameter(Mandatory)][scriptblock]$Action, [Parameter(Mandatory)][string]$Name)
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    if (-not $threw) { throw "Expected rejection: $Name" }
}

function Get-RunnerFunctionAst {
    param([Parameter(Mandatory)][string]$Name)
    $functionAst = $runnerAst.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name }, $true)
    if ($null -eq $functionAst) { throw "Runner is missing function '$Name'." }
    return $functionAst
}

foreach ($name in @(
    'Get-TouchLifecycleProperty', 'Test-TouchLifecycleProperty', 'ConvertFrom-TouchLifecycleNativeJson', 'ConvertTo-TouchLifecyclePsLiteral', 'New-TouchLifecycleGuestLauncherCode', 'Assert-TouchLifecyclePriorRun', 'Assert-TouchLifecycleSnapshot',
    'Assert-TouchLifecycleProcessIdentity', 'Assert-TouchLifecycleProcessCoreIdentity', 'Find-TouchLifecycleProbeWindow',
    'Get-TouchLifecycleCasePlan', 'Get-TouchLifecycleWindows', 'ConvertTo-TouchLifecycleRangeKey', 'Assert-TouchLifecycleCleanControl',
    'Assert-TouchLifecycleSnapshotHasNoNativeCaptures', 'Get-TouchLifecycleEventMatches', 'Assert-TouchLifecycleReport',
    'Test-TouchLifecycleSafeTerminal', 'Test-TouchLifecycleSafeIdle', 'Get-TouchLifecycleNormalCloseSafety', 'Get-TouchLifecycleCounts',
    'Wait-TouchLifecycleReport', 'Assert-TouchLifecycleFreshMenuReadyForDismissal', 'Get-TouchLifecycleUiText'
)) {
    . ([scriptblock]::Create((Get-RunnerFunctionAst $name).Extent.Text))
}

function Invoke-TouchLifecycleNative {
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$EvidenceDirectory, [switch]$Json)
    return $script:TouchLifecycleNativeFixture
}

function Get-TouchLifecycleReceipt {
    param([Parameter(Mandatory)][string]$EvidenceDirectory)
    $script:TouchLifecycleReceiptReadCount++
    return $script:TouchLifecycleReceiptFixture
}

$tests = [Collections.Generic.List[object]]::new()
function Invoke-Test {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][scriptblock]$Body)
    try {
        & $Body
        $tests.Add([pscustomobject]@{ name = $Name; status = 'PASS'; error = $null })
    }
    catch {
        $tests.Add([pscustomobject]@{ name = $Name; status = 'FAIL'; error = $_.Exception.ToString() })
    }
}

function New-TestPayload {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][string]$Hash, [Parameter(Mandatory)][long]$Length, [Parameter(Mandatory)][string]$Path)
    return [pscustomobject]@{ name = $Name; path = $Path; length = $Length; sha256 = $Hash; required = $true }
}

function New-TestPriorReceipt {
    $exeHash = 'a' * 64; $sampleHash = 'b' * 64; $rendererHash = 'c' * 64
    $hostFiles = @(
        (New-TestPayload 'MarkdownRenderer.Sample.exe' $exeHash 100 'E:\build\MarkdownRenderer.Sample.exe'),
        (New-TestPayload 'MarkdownRenderer.Sample.dll' $sampleHash 200 'E:\build\MarkdownRenderer.Sample.dll'),
        (New-TestPayload 'MarkdownRenderer.dll' $rendererHash 300 'E:\build\MarkdownRenderer.dll')
    )
    $guest = @(
        (New-TestPayload 'MarkdownRenderer.Sample.exe' $exeHash 100 'C:\WinApp\deployments\fixture\MarkdownRenderer.Sample.exe'),
        (New-TestPayload 'MarkdownRenderer.Sample.dll' $sampleHash 200 'C:\WinApp\deployments\fixture\MarkdownRenderer.Sample.dll'),
        (New-TestPayload 'MarkdownRenderer.dll' $rendererHash 300 'C:\WinApp\deployments\fixture\MarkdownRenderer.dll')
    )
    return [pscustomobject]@{
        runId = 'prior-run-1'; target = 'sandbox'; targetEpoch = 'guest-guid:session-guid'; architecture = 'x64'
        launch = [pscustomobject]@{ Sandbox=$true; ProcessScope='sandbox'; ExecutionTarget=[pscustomobject]@{ Kind='sandbox'; Id='default'; Architecture='x64'; Epoch='guest-guid:session-guid' } }
        app = [pscustomobject]@{ target = 'sandbox'; epoch = 'guest-guid:session-guid'; processId = 501; hwnd = 8801; processName = 'MarkdownRenderer.Sample'; title = 'MarkdownRenderer — Feature Showcase' }
        buildBinary = [pscustomobject]@{ path = 'E:\build\MarkdownRenderer.Sample.exe'; sha256 = $exeHash; length = 100 }
        payloadIdentity = [pscustomobject]@{ status = 'MATCH'; guestProcessId = 501; guestExecutablePath = 'C:\WinApp\deployments\fixture\MarkdownRenderer.Sample.exe'; host = $hostFiles; guest = $guest }
    }
}

function New-TestControl {
    param([int]$PointerId = 0, [long]$Epoch = 0, [bool]$Selected = $false, [string]$Range = '0:0',
        [bool]$Loaded = $true, [bool]$Unloaded = $false, [bool]$Disposed = $false,
        [bool]$Dragging = $false, [int]$HandleId = 0, [long]$HandleEpoch = 0,
        [string]$CaptureTarget = '', [bool]$StartVisible = $false, [bool]$EndVisible = $false)
    return [pscustomobject]@{
        isLoaded=$Loaded; isUnloaded=$Unloaded; isDisposed=$Disposed
        contactPointerId=$PointerId; contactGestureEpoch=$Epoch; observedPointerId=$PointerId; observedGestureEpoch=$Epoch
        touchState=$(if ($Selected) { 'Selected' } else { 'Idle' })
        handlePointerId=$HandleId; handleGestureEpoch=$HandleEpoch; handleCaptureTargetAutomationId=$CaptureTarget
        isHandleDragging=$Dragging; handleFrameClockSubscribed=$Dragging; handleFrameTimestamp=$(if ($Dragging) { 1 } else { 0 })
        queuedPointerInputCount=0; deferredPointerCaptureCount=0; pointerRetryTimerExists=$false; isContextMenuOpen=$false
        isStartHandleVisible=$StartVisible; isEndHandleVisible=$EndVisible; selectionIsActive=$Selected
        selectionRange=[pscustomobject]@{ value=$Range }
    }
}

function New-TestHandle {
    param([Parameter(Mandatory)][string]$AutomationId, [uint32[]]$PointerIds = @())
    return [pscustomobject]@{ automationId=$AutomationId; found=$true; visibility='Visible'; pointerCaptureListAvailable=$true; pointerIds=@($PointerIds) }
}

function New-TestSnapshot {
    param([Parameter(Mandatory)]$Control, [uint32[]]$StartPointerIds = @(), [uint32[]]$EndPointerIds = @())
    return [pscustomobject]@{ control=$Control; startHandle=(New-TestHandle 'MarkdownSelectionStartHandle' $StartPointerIds); endHandle=(New-TestHandle 'MarkdownSelectionEndHandle' $EndPointerIds) }
}

function New-TestEvent {
    param([int]$Sequence, [string]$Kind, [uint32]$PointerId, [string]$Device, [AllowNull()][Nullable[bool]]$Contact,
        [bool]$Internal=$true, [string]$Source='MarkdownRenderer', [long]$ObservedEpoch=0, [long]$CorrelatedEpoch=0,
        [string]$Observer='Root')
    return [pscustomobject]@{ sequence=$Sequence; kind=$Kind; pointerId=$PointerId; device=$Device; isInContact=$Contact
        internalStateAvailable=$Internal; sourceAutomationId=$Source; observer=$Observer; observedGestureEpoch=$ObservedEpoch
        correlatedGestureEpoch=$CorrelatedEpoch; stopwatchTimestamp=$Sequence; rendererId='MarkdownRenderer' }
}

function New-TestValidReport {
    param([ValidateSet('captureLoss','unload','dispose')][string]$Case, [ValidateSet('owned','ancestor')][string]$Viewport)
    $longId=[uint32]11; $longEpoch=101L; $handleId=[uint32]22; $handleEpoch=202L
    $baselineControl=New-TestControl -Range '0:0'
    $selectedControl=New-TestControl -PointerId $longId -Epoch $longEpoch -Selected $true -Range '1:10' -StartVisible $true -EndVisible $true
    $activeControl=New-TestControl -PointerId $handleId -Epoch $handleEpoch -Selected $true -Range '1:10' -Dragging $true -HandleId $handleId -HandleEpoch $handleEpoch -CaptureTarget 'MarkdownSelectionStartHandle' -StartVisible $true -EndVisible $true
    $terminalLoaded=$true; $terminalUnloaded=$false; $terminalDisposed=$false
    $terminalControl=New-TestControl -Selected $true -Range '1:10' -Loaded $terminalLoaded -Unloaded $terminalUnloaded -Disposed $terminalDisposed
    if ($Case -eq 'unload') { $terminalControl=New-TestControl -Selected $true -Range '1:10' -Loaded $false -Unloaded $true }
    if ($Case -eq 'dispose') { $terminalControl=New-TestControl -Selected $true -Range '1:10' -Loaded $false -Unloaded $false -Disposed $true }
    $before=New-TestSnapshot $activeControl -StartPointerIds @($handleId)
    $afterAction=New-TestSnapshot (New-TestControl -Selected $true -Range '1:10')
    $afterTerminal=New-TestSnapshot $terminalControl
    $events=[Collections.Generic.List[object]]::new()
    $events.Add((New-TestEvent 1 'PointerPressed' $longId 'Touch' $true -Source 'MarkdownRenderer' -ObservedEpoch $longEpoch -CorrelatedEpoch $longEpoch))
    $events.Add((New-TestEvent 2 'HoldingStarted' $longId 'Touch' $true -Source 'MarkdownRenderer' -ObservedEpoch $longEpoch -CorrelatedEpoch $longEpoch))
    $events.Add((New-TestEvent 3 'PointerReleased' $longId 'Touch' $false -Source 'MarkdownRenderer' -ObservedEpoch $longEpoch -CorrelatedEpoch $longEpoch))
    $events.Add((New-TestEvent 4 'PointerPressed' $handleId 'Touch' $true -Source 'MarkdownSelectionStartHandle' -ObservedEpoch $handleEpoch -CorrelatedEpoch $handleEpoch -Observer 'CapturedButton'))
    $events.Add((New-TestEvent 5 'PointerMoved' $handleId 'Touch' $true -Source 'MarkdownSelectionStartHandle' -ObservedEpoch $handleEpoch -CorrelatedEpoch $handleEpoch -Observer 'CapturedButton'))
    $events.Add((New-TestEvent 6 ('Action:'+$Case) $handleId 'ProbeAction' $true -Internal $true -Source 'TouchLifecycleProbe' -ObservedEpoch $handleEpoch -CorrelatedEpoch $handleEpoch))
    $extra=@{ freshPointerId=[uint32]0; freshGestureEpoch=[long]0; afterAction=$null; afterUnloaded=$null; afterReattach=$null; freshSelection=$null; freshContextMenuObserved=$false; freshContextMenuOpenSequence=0; freshMenuDismissed=$false; freshMenuDismissedSequence=0; freshContextMenuOpen=$null; afterFreshTerminal=$null }
    switch ($Case) {
        captureLoss {
            $events.Add((New-TestEvent 7 'PointerCaptureLost' $handleId 'Touch' $true -Source 'MarkdownSelectionStartHandle' -ObservedEpoch $handleEpoch -CorrelatedEpoch $handleEpoch -Observer 'CapturedButton'))
            $events.Add((New-TestEvent 8 'PointerReleased' $handleId 'Touch' $false -Source 'MarkdownSelectionStartHandle' -ObservedEpoch $handleEpoch -CorrelatedEpoch $handleEpoch -Observer 'CapturedButton'))
            $extra.afterAction=$afterAction
        }
        unload {
            $events.Add((New-TestEvent 7 'Unloaded' 0 'None' $null -Internal $true -Source 'MarkdownRenderer' -ObservedEpoch $handleEpoch -CorrelatedEpoch $handleEpoch -Observer 'Renderer'))
            $events.Add((New-TestEvent 8 'PointerReleased' $handleId 'Touch' $false -Source 'MarkdownSelectionStartHandle' -ObservedEpoch $handleEpoch -CorrelatedEpoch $handleEpoch -Observer 'CapturedButton'))
            $events.Add((New-TestEvent 9 'Loaded' 0 'None' $null -Internal $true -Source 'MarkdownRenderer' -ObservedEpoch 0 -CorrelatedEpoch 0 -Observer 'Renderer'))
            $freshId=[uint32]33; $freshEpoch=303L
            $events.Add((New-TestEvent 10 'PointerPressed' $freshId 'Touch' $true -Source 'MarkdownRenderer' -ObservedEpoch $freshEpoch -CorrelatedEpoch $freshEpoch))
            $events.Add((New-TestEvent 11 'HoldingStarted' $freshId 'Touch' $true -Source 'MarkdownRenderer' -ObservedEpoch $freshEpoch -CorrelatedEpoch $freshEpoch))
            $events.Add((New-TestEvent 12 'PointerReleased' $freshId 'Touch' $false -Source 'MarkdownRenderer' -ObservedEpoch $freshEpoch -CorrelatedEpoch $freshEpoch))
            $events.Add((New-TestEvent 13 'FreshContextMenuObserved' $freshId 'ProbeState' $false -Source 'TouchLifecycleProbeWindow' -ObservedEpoch 0 -CorrelatedEpoch $freshEpoch -Observer 'Probe'))
            $events.Add((New-TestEvent 14 'FreshMenuDismissed' $freshId 'ProbeState' $false -Source 'TouchLifecycleProbeWindow' -ObservedEpoch 0 -CorrelatedEpoch $freshEpoch -Observer 'Probe'))
            $extra.afterUnloaded=New-TestSnapshot (New-TestControl -Selected $true -Range '1:10' -Loaded $false -Unloaded $true)
            $extra.afterReattach=New-TestSnapshot (New-TestControl -Selected $true -Range '1:10')
            $extra.freshSelection=New-TestSnapshot (New-TestControl -PointerId $freshId -Epoch $freshEpoch -Selected $true -Range '20:29' -StartVisible $true -EndVisible $true)
            $menuOpenControl=New-TestControl -Selected $true -Range '20:29' -StartVisible $true -EndVisible $true
            $menuOpenControl.isContextMenuOpen=$true
            $extra.freshContextMenuOpen=New-TestSnapshot $menuOpenControl
            $extra.afterFreshTerminal=New-TestSnapshot (New-TestControl -Selected $true -Range '20:29')
            $extra.freshContextMenuObserved=$true; $extra.freshContextMenuOpenSequence=13; $extra.freshMenuDismissed=$true; $extra.freshMenuDismissedSequence=14
            $extra.freshPointerId=$freshId; $extra.freshGestureEpoch=$freshEpoch
        }
        dispose {
            $events.Add((New-TestEvent 7 'DisposalCompleted' 0 'None' $null -Internal $true -Source 'MarkdownRenderer' -ObservedEpoch $handleEpoch -CorrelatedEpoch $handleEpoch -Observer 'Renderer'))
            $events.Add((New-TestEvent 8 'PointerReleased' $handleId 'Touch' $false -Source 'MarkdownSelectionStartHandle' -ObservedEpoch $handleEpoch -CorrelatedEpoch $handleEpoch -Observer 'CapturedButton'))
            $extra.afterAction=New-TestSnapshot (New-TestControl -Selected $true -Range '1:10' -Loaded $false -Disposed $true)
        }
    }
    return [pscustomobject]@{
        version=2; run=1; phase='complete'; lifecycleCase=$Case; viewport=$Viewport; longPressPointerId=$longId; longPressGestureEpoch=$longEpoch
        handlePointerId=$handleId; handleGestureEpoch=$handleEpoch; freshPointerId=([uint32]$extra.freshPointerId); freshGestureEpoch=([long]$extra.freshGestureEpoch)
        eventTraceTruncated=$false; baseline=(New-TestSnapshot $baselineControl); longPressSelection=(New-TestSnapshot $selectedControl)
        beforeAction=$before; afterAction=$extra.afterAction; afterTerminal=$afterTerminal; afterUnloaded=$extra.afterUnloaded
        afterReattach=$extra.afterReattach; freshSelection=$extra.freshSelection
        freshContextMenuObserved=$extra.freshContextMenuObserved; freshContextMenuOpenSequence=$extra.freshContextMenuOpenSequence
        freshMenuDismissed=$extra.freshMenuDismissed; freshMenuDismissedSequence=$extra.freshMenuDismissedSequence
        freshContextMenuOpen=$extra.freshContextMenuOpen; afterFreshTerminal=$extra.afterFreshTerminal
        events=@($events.ToArray()); failure=$null
    }
}

Invoke-Test 'get-value text is read from the top-level and nested shapes, never from an empty value' {
    $top=Get-TouchLifecycleUiText ([pscustomobject]@{value=[pscustomobject]@{elementId='cmb-1';text='Capture loss'}})
    $nested=Get-TouchLifecycleUiText ([pscustomobject]@{value=[pscustomobject]@{value=[pscustomobject]@{text='Unload and reuse'}}})
    $plain=Get-TouchLifecycleUiText ([pscustomobject]@{value='{"version":1}'})
    $empty=Get-TouchLifecycleUiText ([pscustomobject]@{value=[pscustomobject]@{elementId='x';text=''}})
    Assert-Condition ($top -ceq 'Capture loss' -and $nested -ceq 'Unload and reuse' -and $plain -ceq '{"version":1}' -and $null -eq $empty) 'UI text extraction lost a supported get-value shape or accepted empty text.'
}

Invoke-Test 'case plan covers each lifecycle and viewport exactly once' {
    $plan=@(Get-TouchLifecycleCasePlan)
    Assert-Condition ($plan.Count -eq 6) 'Expected six isolated child runs.'
    Assert-Condition (@($plan | ForEach-Object { "$($_.lifecycleCase):$($_.viewport)" } | Sort-Object -Unique).Count -eq 6) 'Lifecycle/viewport pairs are not unique.'
    Assert-Condition (@($plan | Where-Object { $_.lifecycleCase -eq 'unload' }).Count -eq 2) 'Unload/reuse must run for both viewport owners.'
}

Invoke-Test 'prior run accepts exact x64 Sandbox epoch and matched payload identities' {
    $validated=Assert-TouchLifecyclePriorRun -Receipt (New-TestPriorReceipt)
    Assert-Condition ($validated.appProcessId -eq 501 -and $validated.appHwnd -eq 8801 -and $validated.payloads.Count -eq 3) 'Validated prior run lost app or payload identities.'
}
Invoke-Test 'prior run rejects epoch, PID, payload-hash, and guest-path mismatches' {
    $receipt=New-TestPriorReceipt; $receipt.app.epoch='other-epoch'; Assert-Throws { Assert-TouchLifecyclePriorRun $receipt } 'epoch mismatch'
    $receipt=New-TestPriorReceipt; $receipt.payloadIdentity.guest[2].sha256='d'*64; Assert-Throws { Assert-TouchLifecyclePriorRun $receipt } 'renderer hash mismatch'
    $receipt=New-TestPriorReceipt; $receipt.payloadIdentity.guestProcessId=999; Assert-Throws { Assert-TouchLifecyclePriorRun $receipt } 'guest PID mismatch'
    $receipt=New-TestPriorReceipt; $receipt.payloadIdentity.guest[0].path='C:\other\MarkdownRenderer.Sample.exe'; Assert-Throws { Assert-TouchLifecyclePriorRun $receipt } 'guest directory mismatch'
    $receipt=New-TestPriorReceipt; $receipt.launch.ExecutionTarget.Epoch='other-epoch'; Assert-Throws { Assert-TouchLifecyclePriorRun $receipt } 'launch epoch mismatch'
    $receipt=New-TestPriorReceipt; $receipt.launch.ProcessScope='host'; Assert-Throws { Assert-TouchLifecyclePriorRun $receipt } 'host process scope'
    $receipt=New-TestPriorReceipt; $receipt.PSObject.Properties.Remove('launch'); Assert-Throws { Assert-TouchLifecyclePriorRun $receipt } 'missing launch target metadata'
    $receipt=New-TestPriorReceipt; $receipt.payloadIdentity.guest += New-TestPayload 'Unexpected.dll' ('d'*64) 400 'C:\WinApp\deployments\fixture\Unexpected.dll'; Assert-Throws { Assert-TouchLifecyclePriorRun $receipt } 'extra guest payload'
    $receipt=New-TestPriorReceipt; $receipt.payloadIdentity.guest += $receipt.payloadIdentity.guest[0].PSObject.Copy(); Assert-Throws { Assert-TouchLifecyclePriorRun $receipt } 'duplicate guest payload name'
}

Invoke-Test 'launcher source-process identity is checked without fabricated HWND or title fields' {
    $hash='a'*64
    $expected=[pscustomobject]@{ processId=51; processName='MarkdownRenderer.Sample'; executablePath='C:\build\MarkdownRenderer.Sample.exe'; startTicks=639220000000000000L; exeSha256=$hash }
    $null=Assert-TouchLifecycleProcessCoreIdentity -Actual $expected.PSObject.Copy() -Expected $expected
    $actual=$expected.PSObject.Copy();$actual.executablePath='c:\BUILD\MARKDOWNRENDERER.SAMPLE.EXE'
    $null=Assert-TouchLifecycleProcessCoreIdentity -Actual $actual -Expected $expected
    $actual=$expected.PSObject.Copy();$actual.executablePath='C:\other\MarkdownRenderer.Sample.exe'
    Assert-Throws { Assert-TouchLifecycleProcessCoreIdentity $actual $expected } 'source executable changed'
    $actual=$expected.PSObject.Copy();$actual.startTicks++
    Assert-Throws { Assert-TouchLifecycleProcessCoreIdentity $actual $expected } 'source process start changed'
}

Invoke-Test 'guest launcher code parses and compares Windows executable paths ordinal-ignore-case' {
    $script:Prior=[pscustomobject]@{ guestExecutablePath='C:\WinApp\deployments\fixture\MarkdownRenderer.Sample.exe' }
    $script:GuestComputerName='SandboxGuest'; $script:HostComputerName='HostMachine'
    $identity=[pscustomobject]@{ processId=501; startTicks=639220000000000000L; exeSha256=('a'*64) }
    $code=New-TouchLifecycleGuestLauncherCode -OriginalIdentity $identity
    $tokens=$null; $errors=$null
    $null=[System.Management.Automation.Language.Parser]::ParseInput($code,[ref]$tokens,[ref]$errors)
    Assert-Condition ($errors.Count -eq 0) 'Generated guest launcher PowerShell is syntactically invalid.'
    Assert-Condition ([regex]::Matches($code,'StringComparison]::OrdinalIgnoreCase').Count -eq 4) 'Guest/host distinction plus source-before, child, and source-after identity comparisons must use ordinal-ignore-case on Windows.'
    Assert-Condition ($code.Contains('Invoke-CimMethod -ClassName Win32_Process -MethodName Create') -and $code.Contains("CommandLine=('`"' + `$exe + '`" --renderer-touch-lifecycle-probe')") -and $code.Contains('ShowWindow=[uint16]1')) 'Generated guest launcher does not launch the exact visible sample process with the fixed argument.'
    Assert-Condition ($code -notmatch 'Start-Process\s+-FilePath') 'Generated guest launcher starts the probe inside the target exec job, which terminates it when the launcher returns (T1-017).'
    Assert-Condition ($code.Contains('EnvironmentVariables=$envBlock') -and $code.Contains('Get-ChildItem Env:')) 'Generated guest launcher does not pass the full environment block, so the probe loses DOTNET_ROOT and system variables.'
}

Invoke-Test 'native JSON keeps empty arrays distinct from null and accepts window arrays of any size' {
    $parsed=ConvertFrom-TouchLifecycleNativeJson -JsonText '[]'
    Assert-Condition ($null -ne $parsed.value -and $parsed.value -is [array] -and $parsed.value.Count -eq 0) 'Top-level empty JSON array collapsed to null.'
    $single=ConvertFrom-TouchLifecycleNativeJson -JsonText '[{"processId":7}]'
    Assert-Condition (@($single.value).Count -eq 1 -and [int]$single.value[0].processId -eq 7) 'Single-window JSON array was not retained.'
    $script:TouchLifecycleNativeFixture=[pscustomobject]@{value=@()}
    Assert-Condition (@(Get-TouchLifecycleWindows -EvidenceDirectory $env:TEMP).Count -eq 0) 'Empty window list was not accepted as an empty list.'
    $script:TouchLifecycleNativeFixture=[pscustomobject]@{value=@([pscustomobject]@{processId=1})}
    Assert-Condition (@(Get-TouchLifecycleWindows -EvidenceDirectory $env:TEMP).Count -eq 1) 'Single window was not retained.'
    $script:TouchLifecycleNativeFixture=[pscustomobject]@{value=@([pscustomobject]@{processId=1},[pscustomobject]@{processId=2})}
    Assert-Condition (@(Get-TouchLifecycleWindows -EvidenceDirectory $env:TEMP).Count -eq 2) 'Two windows were not retained.'
    $script:TouchLifecycleNativeFixture=[pscustomobject]@{value=$null}
    Assert-Throws { Get-TouchLifecycleWindows -EvidenceDirectory $env:TEMP } 'null window list'
    $script:TouchLifecycleNativeFixture=[pscustomobject]@{items=@()}
    Assert-Throws { Get-TouchLifecycleWindows -EvidenceDirectory $env:TEMP } 'missing window-list value'
}

Invoke-Test 'report validator accepts all six complete actual-evidence case shapes' {
    foreach ($case in @('captureLoss','unload','dispose')) { foreach ($viewport in @('owned','ancestor')) { $null=Assert-TouchLifecycleReport -Report (New-TestValidReport $case $viewport) -ExpectedCase $case -ExpectedViewport $viewport } }
}
Invoke-Test 'fresh interaction accepts a retained complete receipt without observing transient terminal phase' {
    $script:TouchLifecycleReceiptFixture = New-TestValidReport unload ancestor
    $script:TouchLifecycleReceiptReadCount = 0
    $complete = Wait-TouchLifecycleReport -EvidenceDirectory $env:TEMP -AcceptPhase @('complete') -TimeoutSeconds 1
    Assert-Condition ($script:TouchLifecycleReceiptReadCount -eq 1) 'Completed receipt was not accepted immediately.'
    Assert-Condition ((Get-TouchLifecycleProperty $complete 'phase') -ceq 'complete' -and
        $null -ne (Get-TouchLifecycleProperty $complete 'freshSelection') -and
        $null -ne (Get-TouchLifecycleProperty $complete 'afterFreshTerminal') -and
        [bool](Get-TouchLifecycleProperty $complete 'freshContextMenuObserved') -and
        [bool](Get-TouchLifecycleProperty $complete 'freshMenuDismissed')) 'Completed receipt did not retain the fresh selection, menu ordering, and terminal evidence.'
    $menuReady = New-TestValidReport unload ancestor
    $menuReady.phase='awaitFreshMenuDismissal'; $menuReady.freshMenuDismissed=$false; $menuReady.freshMenuDismissedSequence=0
    $menuReady.events=@($menuReady.events | Where-Object { $_.kind -cne 'FreshMenuDismissed' })
    $null=Assert-TouchLifecycleFreshMenuReadyForDismissal $menuReady
    $caseBody = (Get-RunnerFunctionAst 'Invoke-TouchLifecycleCase').Extent.Text
    $coordinatorVariable = [regex]::Escape('$script:TouchLifecycleCoordinatorWaitSeconds')
    $handleWaits = [regex]::Matches($caseBody, "AcceptPhase @\('awaitHandleContact'\) -TimeoutSeconds $coordinatorVariable").Count
    $freshWaits = [regex]::Matches($caseBody, "AcceptPhase @\('awaitFreshLongPress'\) -TimeoutSeconds $coordinatorVariable").Count
    $menuWait = $caseBody.IndexOf("AcceptPhase @('awaitFreshMenuDismissal')", [StringComparison]::Ordinal)
    $escape = $caseBody.IndexOf("'send-keys','esc'", $menuWait, [StringComparison]::Ordinal)
    $completeWait = $caseBody.IndexOf("AcceptPhase @('complete') -TimeoutSeconds 12", $escape, [StringComparison]::Ordinal)
    Assert-Condition ($handleWaits -eq 2 -and $freshWaits -eq 1 -and
        -not $caseBody.Contains("AcceptPhase @('awaitFreshTerminal')") -and
        $menuWait -ge 0 -and $escape -gt $menuWait -and $completeWait -gt $escape -and
        [regex]::Matches($caseBody, "'send-keys','esc'").Count -eq 2 -and
        $caseBody.Contains("Get-TouchLifecycleProperty `$final 'afterFreshTerminal'")) 'Fresh-word flow does not observe the menu, dismiss it on the exact child, and accept final completion without a transient phase read.'
    $unloadStart = $caseBody.IndexOf("if ([string]`$Case.lifecycleCase -ceq 'unload')", [StringComparison]::Ordinal)
    if ($unloadStart -lt 0) { throw 'Could not locate unload/reuse branch in case runner.' }
    $unloadEnd = $caseBody.IndexOf('if ($null -eq $final)', $unloadStart, [StringComparison]::Ordinal)
    $unloadBody = $caseBody.Substring($unloadStart, $unloadEnd - $unloadStart)
    Assert-Condition ($unloadStart -ge 0 -and $unloadEnd -gt $unloadStart -and
        [regex]::Matches($unloadBody, "'send-keys','esc'").Count -eq 1 -and
        $unloadBody.Contains('Assert-TouchLifecycleFreshMenuReadyForDismissal')) 'Fresh flow must send exactly one guarded Escape only while the observed menu is open.'
}
Invoke-Test 'fresh menu dismissal fails closed on stale or misordered observations' {
    $report=New-TestValidReport unload owned; $report.phase='awaitFreshMenuDismissal'; $report.freshMenuDismissed=$false; $report.freshMenuDismissedSequence=0
    $report.events=@($report.events | Where-Object { $_.kind -cne 'FreshMenuDismissed' })
    $null=Assert-TouchLifecycleFreshMenuReadyForDismissal $report
    $report.phase='awaitFreshTerminalCleanup'
    Assert-Throws { Assert-TouchLifecycleFreshMenuReadyForDismissal $report } 'menu closed before host Escape'
    $report=New-TestValidReport unload owned; $report.freshContextMenuOpenSequence=11; $report.phase='awaitFreshMenuDismissal'; $report.freshMenuDismissed=$false; $report.freshMenuDismissedSequence=0
    $report.events=@($report.events | Where-Object { $_.kind -cne 'FreshMenuDismissed' })
    Assert-Throws { Assert-TouchLifecycleFreshMenuReadyForDismissal $report } 'menu observation before fresh terminal'
    $script:TouchLifecycleReceiptFixture=New-TestValidReport unload owned
    $script:TouchLifecycleReceiptFixture.phase='awaitFreshTerminalCleanup'
    Assert-Throws { Wait-TouchLifecycleReport -EvidenceDirectory $env:TEMP -AcceptPhase @('awaitFreshMenuDismissal') -RejectPhase @('awaitFreshTerminalCleanup','complete') -TimeoutSeconds 1 } 'menu closes before host-coordinated Escape'
}
Invoke-Test 'final report requires both ordered menu observations and a closed current terminal snapshot' {
    $report=New-TestValidReport unload ancestor
    $report.freshContextMenuObserved=$false
    Assert-Throws { Assert-TouchLifecycleReport $report unload ancestor } 'missing native menu-open latch'
    $report=New-TestValidReport unload ancestor
    $report.freshMenuDismissed=$false
    Assert-Throws { Assert-TouchLifecycleReport $report unload ancestor } 'missing native menu-dismiss latch'
    $report=New-TestValidReport unload ancestor
    $report.freshMenuDismissedSequence=$report.freshContextMenuOpenSequence
    Assert-Throws { Assert-TouchLifecycleReport $report unload ancestor } 'menu dismissal not ordered after open'
    $report=New-TestValidReport unload ancestor
    $report.freshContextMenuOpen.control.isContextMenuOpen=$false
    Assert-Throws { Assert-TouchLifecycleReport $report unload ancestor } 'open snapshot omitted actual open state'
    $report=New-TestValidReport unload ancestor
    $report.afterFreshTerminal.control.isContextMenuOpen=$true
    Assert-Throws { Assert-TouchLifecycleReport $report unload ancestor } 'final terminal retained menu state'
}
Invoke-Test 'report validator rejects truncation, action after contact terminal, and wrong captured Button' {
    $report=New-TestValidReport captureLoss owned; $report.eventTraceTruncated=$true
    Assert-Throws { Assert-TouchLifecycleReport $report captureLoss owned } 'truncated event evidence'
    $report=New-TestValidReport captureLoss owned; $report.events[5].isInContact=$false
    Assert-Throws { Assert-TouchLifecycleReport $report captureLoss owned } 'action after contact ended'
    $report=New-TestValidReport captureLoss owned; $report.events[4].sourceAutomationId='MarkdownSelectionEndHandle'
    Assert-Throws { Assert-TouchLifecycleReport $report captureLoss owned } 'move from wrong Button'
    $report=New-TestValidReport captureLoss owned; $report.events[4].correlatedGestureEpoch=999
    Assert-Throws { Assert-TouchLifecycleReport $report captureLoss owned } 'wrong handle-move epoch'
    $report=New-TestValidReport captureLoss owned; $report.events[4].sequence=3
    Assert-Throws { Assert-TouchLifecycleReport $report captureLoss owned } 'handle move before handle press'
    $report=New-TestValidReport captureLoss owned; $report.PSObject.Properties.Remove('failure')
    Assert-Throws { Assert-TouchLifecycleReport $report captureLoss owned } 'missing explicit failure field'
}
Invoke-Test 'unload/reuse report rejects same epoch and no fresh same-epoch release' {
    $report=New-TestValidReport unload ancestor; $report.freshGestureEpoch=$report.handleGestureEpoch
    Assert-Throws { Assert-TouchLifecycleReport $report unload ancestor } 'reused gesture epoch'
    $report=New-TestValidReport unload ancestor; $report.events=@($report.events | Where-Object { -not ($_.pointerId -eq 33 -and $_.kind -eq 'PointerReleased') })
    Assert-Throws { Assert-TouchLifecycleReport $report unload ancestor } 'fresh contact lacks actual release'
}
Invoke-Test 'close safety permits only clean terminal or clean no-touch baseline' {
    $idle=New-TestValidReport captureLoss owned; $idle.phase='idle'; $idle.lifecycleCase='captureLoss'; $idle.events=@(); $idle.beforeAction=$null; $idle.longPressSelection=$null; $idle.afterTerminal=$null
    $idle.baseline.control.selectionIsActive=$false
    Assert-Condition ((Get-TouchLifecycleNormalCloseSafety -TouchCommandIssued $false -Report $idle -ExpectedCase captureLoss -ExpectedViewport owned).safe) 'Clean untouched child should be closable.'
    $unsafe=Get-TouchLifecycleNormalCloseSafety -TouchCommandIssued $true -Report $null -ExpectedCase captureLoss -ExpectedViewport owned
    Assert-Condition (-not $unsafe.safe -and $unsafe.reason -match 'withholding') 'Unknown touch state must withhold close input.'
    $complete=New-TestValidReport captureLoss owned
    Assert-Condition ((Get-TouchLifecycleNormalCloseSafety -TouchCommandIssued $true -Report $complete -ExpectedCase captureLoss -ExpectedViewport owned).safe) 'Complete terminal-clean report should permit normal close.'
    $complete.afterTerminal.startHandle.pointerIds=@(22)
    Assert-Condition (-not (Get-TouchLifecycleNormalCloseSafety -TouchCommandIssued $true -Report $complete -ExpectedCase captureLoss -ExpectedViewport owned).safe) 'Retained native capture must withhold close input.'
}

Invoke-Test 'process and window guards require exact PID, start, path, hash, HWND, and title' {
    $hash='a'*64
    $expected=[pscustomobject]@{processId=42;processName='MarkdownRenderer.Sample';executablePath='C:\build\MarkdownRenderer.Sample.exe';startTicks=639220000000000000L;exeSha256=$hash;hwnd=700;title='MarkdownRenderer touch lifecycle probe'}
    $actual=$expected.PSObject.Copy()
    $null=Assert-TouchLifecycleProcessIdentity $actual $expected
    $actual=$expected.PSObject.Copy();$actual.startTicks++
    Assert-Throws { Assert-TouchLifecycleProcessIdentity $actual $expected } 'PID reuse/start change'
    $actual=$expected.PSObject.Copy();$actual.exeSha256='b'*64
    Assert-Throws { Assert-TouchLifecycleProcessIdentity $actual $expected } 'binary changed'
    $windows=@([pscustomobject]@{processId=42;processName='MarkdownRenderer.Sample';hwnd=700;title='MarkdownRenderer touch lifecycle probe'})
    $found=Find-TouchLifecycleProbeWindow $windows $expected ([pscustomobject]@{processId=8})
    Assert-Condition ($found.hwnd -eq 700) 'Exact child window was not accepted.'
    $windows += [pscustomobject]@{processId=42;processName='MarkdownRenderer.Sample';hwnd=701;title='MarkdownRenderer touch lifecycle probe'}
    Assert-Throws { Find-TouchLifecycleProbeWindow $windows $expected ([pscustomobject]@{processId=8}) } 'ambiguous title'
}

Invoke-Test 'all UI dispatch is epoch and process guarded and normal close verifies target exit' {
    $uiFunction=(Get-RunnerFunctionAst 'Invoke-TouchLifecycleUi').Extent.Text
    Assert-Condition ($uiFunction.IndexOf('Assert-TouchLifecycleActionTarget') -lt $uiFunction.IndexOf('Invoke-TouchLifecycleNative') -and
        $uiFunction.LastIndexOf('Assert-TouchLifecycleActionTarget') -gt $uiFunction.IndexOf('Invoke-TouchLifecycleNative') -and
        $uiFunction.Contains("'--on','sandbox','-a',[string]`$script:ProbeIdentity.processId")) 'Generic UI action lost pre/post identity guard or exact child targeting.'
    $closeFunction=(Get-RunnerFunctionAst 'Close-TouchLifecycleProbeNormally').Extent.Text
    Assert-Condition ($closeFunction.Contains('Assert-TouchLifecycleActionTarget') -and $closeFunction.Contains('Assert-TouchLifecycleProcessExited') -and $closeFunction.Contains('Assert-TouchLifecycleOriginalIdentity')) 'Normal close does not verify clean target exit and original-app survival.'
    $source=[IO.File]::ReadAllText($runnerPath)
    Assert-Condition ($source.Contains('if (-not $RunApprovedSandboxCases)') -and $source.Contains('--renderer-touch-lifecycle-probe') -and -not $source.Contains('Stop-Process') -and -not $source.Contains('taskkill')) 'Runner lost explicit authorization gate, fixed probe argument, or non-forcible cleanup policy.'
}

$failed=@($tests | Where-Object status -ne 'PASS')
$tests | Format-Table -AutoSize name,status
if ($failed.Count -gt 0) {
    $failed | ForEach-Object { Write-Host ("FAIL {0}: {1}" -f $_.name,$_.error) }
    exit 1
}
Write-Host "Touch-lifecycle offline harness tests passed: $($tests.Count)"
