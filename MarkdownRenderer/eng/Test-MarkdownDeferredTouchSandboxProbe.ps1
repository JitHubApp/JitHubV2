[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$probePath = Join-Path $PSScriptRoot 'MarkdownDeferredTouchSandboxProbe.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($probePath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw "Deferred-touch probe does not parse: $($parseErrors | ForEach-Object Message -join '; ')" }

function Assert-Condition {
    param([Parameter(Mandatory)][bool]$Condition,[Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-Throws {
    param([Parameter(Mandatory)][scriptblock]$Action,[Parameter(Mandatory)][string]$Name)
    $didThrow = $false
    try { & $Action | Out-Null } catch { $didThrow = $true }
    if (-not $didThrow) { throw "Expected rejection: $Name" }
}

function Get-FunctionAst {
    param([Parameter(Mandatory)][string]$Name)
    $functionAst = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name }, $true)
    if ($null -eq $functionAst) { throw "Probe is missing required function '$Name'." }
    return $functionAst
}

# Load the real validation functions from the host driver without executing its
# guest-identity bootstrap, native setup, or any UI/input path.
foreach ($functionName in @('Get-DeferredRangeFingerprint','Test-DeferredTouchClearedSelection','Assert-DeferredTouchSelection','Assert-DeferredPayloadIdentity','Assert-DeferredTouchReceiptProperties','Assert-DeferredTouchStateProperties','Assert-DeferredTouchEvidence','Stop-DeferredTouchInputPump','Test-DeferredTouchPumpStopped')) {
    $functionAst = Get-FunctionAst $functionName
    . ([scriptblock]::Create($functionAst.Extent.Text))
}

function New-Range {
    param([int]$Start,[int]$End)
    [pscustomobject]@{
        start=[pscustomobject]@{blockIndex=0;inlineIndex=0;characterOffset=$Start}
        end=[pscustomobject]@{blockIndex=0;inlineIndex=0;characterOffset=$End}
    }
}

function New-State {
    param(
        [uint32]$PointerId=0,[long]$Epoch=0,[int]$Queued=0,[object[]]$Terminals=@(),
        [int]$Matching=0,[int]$Captures=0,[bool]$Retry=$false,[bool]$Dragging=$false,
        [int]$VisibleHandles=0,[bool]$Menu=$false,[bool]$Selected=$false,[object]$Range=(New-Range 0 0)
    )
    [pscustomobject]@{
        activePointerId=$PointerId;gestureEpoch=$Epoch;touchState='Idle';queuedCount=$Queued
        queuedTerminals=@($Terminals);matchingTerminalCount=$Matching;captureCount=$Captures
        retryTimerExists=$Retry;isHandleDragging=$Dragging;visibleHandleCount=$VisibleHandles
        selectionContextMenuOpen=$Menu;selectionIsActive=$Selected;selectionRange=$Range
    }
}

function New-Receipt {
    param(
        [string]$Phase,[int]$Sequence,[bool]$Canceled,[bool]$HadSelection,
        [uint32]$PointerId=0,[long]$Epoch=0,[bool]$Worker=$false,
        [object]$PreContact=$null,[object]$BeforeTerminal=$null,[object]$QueuedTerminal=$null,
        [object]$AfterDrop=$null,[object]$FreshContact=$null,[bool]$WorkerHeldAtDrop=$false
    )
    [pscustomobject]@{
        version=1;sequence=$Sequence;phase=$Phase;viewport='owned';canceled=$Canceled
        hadSelectionBeforeContact=$HadSelection;pointerId=$PointerId;gestureEpoch=$Epoch
        workerOwnsSnapshot=$Worker;preContact=$PreContact;beforeTerminal=$BeforeTerminal
        queuedTerminal=$QueuedTerminal;afterDrop=$AfterDrop;freshContact=$FreshContact
        failure='';workerHeldAtDrop=$WorkerHeldAtDrop
    }
}

function New-EvidenceFixture {
    param([ValidateSet('Release','CancelNew','CancelExisting')][string]$Scenario='CancelNew',[int]$CancelTerminalCount=1)
    $sequence = 9
    $canceled = $Scenario -ne 'Release'
    $hadSelection = $Scenario -ne 'CancelNew'
    $initialRange = if ($hadSelection) { New-Range 3 16 } else { New-Range 0 0 }
    $deferredRange = if ($hadSelection) { $initialRange } else { New-Range 30 42 }
    $freshRange = New-Range 60 69
    $pre = New-State -Selected $hadSelection -Range $initialRange
    $before = New-State -PointerId 21 -Epoch 44 -Selected $true -VisibleHandles 2 -Range $deferredRange
    $terminalKind = if ($canceled) { 'Cancel' } else { 'Release' }
    $terminals = @()
    for ($i=0; $i -lt $CancelTerminalCount; $i++) {
        $terminals += [pscustomobject]@{kind=$terminalKind;pointerId=[uint32]21;gestureEpoch=[long]44;isTouch=$true}
    }
    $queued = New-State -PointerId 21 -Epoch 44 -Queued $CancelTerminalCount -Terminals $terminals `
        -Matching $CancelTerminalCount -Selected $true -VisibleHandles 2 -Range $deferredRange
    $afterSelected = if ($Scenario -eq 'CancelNew') { $false } else { $true }
    $afterRange = if ($afterSelected) { $deferredRange } else { New-Range 0 0 }
    $after = New-State -Selected $afterSelected -Range $afterRange
    $awaiting = New-Receipt -Phase 'awaitingFreshContact' -Sequence $sequence -Canceled $canceled -HadSelection $hadSelection `
        -PointerId 21 -Epoch 44 -QueuedTerminal $queued -AfterDrop $after
    $armed = New-Receipt -Phase 'armed' -Sequence $sequence -Canceled $canceled -HadSelection $hadSelection -PreContact $pre
    $holding = New-Receipt -Phase 'holding' -Sequence $sequence -Canceled $canceled -HadSelection $hadSelection `
        -PointerId 21 -Epoch 44 -Worker $true -PreContact $pre -BeforeTerminal $before
    $dropPhase = if ($Scenario -eq 'Release') { 'releasing' } else { 'awaitingFreshContact' }
    $dropped = New-Receipt -Phase $dropPhase -Sequence $sequence -Canceled $canceled -HadSelection $hadSelection `
        -PointerId 21 -Epoch 44 -QueuedTerminal $queued -AfterDrop $after -WorkerHeldAtDrop $true
    $fresh = New-State -PointerId 22 -Epoch 45 -Selected $true -VisibleHandles 2 -Range $freshRange
    $freshReceipt = New-Receipt -Phase 'freshContactAccepted' -Sequence $sequence -Canceled $canceled -HadSelection $hadSelection `
        -PointerId 21 -Epoch 44 -FreshContact $fresh
    $complete = New-Receipt -Phase 'complete' -Sequence $sequence -Canceled $canceled -HadSelection $hadSelection `
        -PointerId 21 -Epoch 44 -AfterDrop $after -FreshContact $fresh
    $initialText = if ($hadSelection) { 'committedword' } else { '' }
    $beforeText = if ($Scenario -eq 'CancelNew') { 'deferredword' } elseif ($Scenario -eq 'Release') { 'committedword' } else { 'committedword' }
    $afterText = if ($Scenario -eq 'CancelNew') { '' } else { 'committedword' }
    $selectionRect = @(@{x=32.0;y=48.0;width=96.0;height=18.0})
    $initialUi = [pscustomobject]@{supportedTextSelection='Single';rangeCount=[int]([bool]$hadSelection);selection=$initialText;selectionRects=$(if($hadSelection){$selectionRect}else{@()});handles=@()}
    $beforeUi = [pscustomobject]@{supportedTextSelection='Single';rangeCount=1;selection=$beforeText;selectionRects=$selectionRect;handles=@()}
    $afterUi = [pscustomobject]@{supportedTextSelection='Single';rangeCount=[int]([bool]$afterSelected);selection=$afterText;selectionRects=$(if($afterSelected){$selectionRect}else{@()});handles=@()}
    $finalUi = [pscustomobject]@{
        supportedTextSelection='Single';rangeCount=1;selection='freshword';selectionRects=@(@{x=40.0;y=80.0;width=55.0;height=18.0});handles=@(
            [pscustomobject]@{automationId='MarkdownSelectionStartHandle';offscreen=$false;width=8.0;height=16.0},
            [pscustomobject]@{automationId='MarkdownSelectionEndHandle';offscreen=$false;width=8.0;height=16.0})
    }
    [pscustomobject]@{
        scenario=$Scenario;sequence=$sequence;armed=$armed;holding=$holding;dropped=$dropped;awaiting=$awaiting
        fresh=$freshReceipt;complete=$complete;initialUi=$initialUi;beforeUi=$beforeUi;afterUi=$afterUi;finalUi=$finalUi
    }
}

function New-BusySelectionFixture {
    $heldSnapshot = New-State -PointerId 21 -Epoch 44 -Selected $true -Range (New-Range 30 42)
    $receipt = New-Receipt -Phase 'holding' -Sequence 9 -Canceled $true -HadSelection $false `
        -PointerId 21 -Epoch 44 -Worker $true -BeforeTerminal $heldSnapshot
    $uiState = [pscustomobject]@{
        supportedTextSelection='Single';rangeCount=1;selection='deferredword';selectionRects=@();handles=@()
    }
    return [pscustomobject]@{uiState=$uiState;receipt=$receipt}
}

function Invoke-EvidenceFixture {
    param([Parameter(Mandatory)]$Fixture)
    Assert-DeferredTouchEvidence -ExpectedScenario $Fixture.scenario -ExpectedViewport owned -ExpectedSequence $Fixture.sequence `
        -ArmedReceipt $Fixture.armed -HoldingReceipt $Fixture.holding -DroppedReceipt $Fixture.dropped `
        -AwaitingReceipt $Fixture.awaiting -FreshReceipt $Fixture.fresh -CompleteReceipt $Fixture.complete `
        -InitialUiState $Fixture.initialUi -BeforeTerminalUiState $Fixture.beforeUi -AfterDropUiState $Fixture.afterUi `
        -FinalUiState $Fixture.finalUi -FirstRoutedPointerId 21 -FirstContactSerial 1001 `
        -FreshRoutedPointerId 22 -FreshContactSerial 1002
}

function Invoke-SharedGuestProbe {
    param([Parameter(Mandatory)][string]$Mode)
    if ($Mode -cne 'PayloadHashes') { throw "Unexpected payload test mode '$Mode'." }
    return $script:MockPayload
}

# Guard the safety-critical orchestration as well as its real validators.
$source = Get-Content -LiteralPath $probePath -Raw
$identityBootstrap = $source.IndexOf('$bootstrapOutput = @(. $script:DeferredProbePath @bootstrapParameters)',[StringComparison]::Ordinal)
$nativeTypeSetup = $source.IndexOf("Add-Type -TypeDefinition @'",[StringComparison]::Ordinal)
Assert-Condition ($identityBootstrap -ge 0 -and $nativeTypeSetup -gt $identityBootstrap) 'Guest identity must be validated before native helper initialization.'
Assert-Condition (-not $source.Contains('SetForegroundWindow')) 'The driver must never force the target foreground.'
Assert-Condition (-not $source.Contains('InRange -bor [McPointerFlags]::Up -bor [McPointerFlags]::Canceled')) 'Canceled contact must use exact Up|Canceled flags without InRange.'
Assert-Condition ($source.Contains('[Diagnostics.Stopwatch]::GetTimestamp()') -and $source.Contains('20L * $stopwatchFrequency')) 'The pump needs a monotonic independent hard contact deadline.'
Assert-Condition ($source.Contains('$script:TouchMayBeInContact = $true') -and $source.Contains('$pump.powerShell.Stop()') -and $source.Contains('$pump.async.AsyncWaitHandle.WaitOne(3000)')) 'The parent must track possible contact and bound worker stop/join.'
$payloadBootstrap = $source.IndexOf('$script:PayloadReceipt = Assert-DeferredPayloadIdentity',[StringComparison]::Ordinal)
Assert-Condition ($source.Contains('$ExpectedSampleDllSha256') -and $source.Contains('$ExpectedRendererDllSha256') -and $source.Contains("Invoke-SharedGuestProbe -Mode PayloadHashes") -and $payloadBootstrap -gt $identityBootstrap -and $payloadBootstrap -lt $nativeTypeSetup) 'Approved sample/renderer payload hashes must be verified from the validated guest executable directory before native setup or interaction.'
Assert-Condition ($source.LastIndexOf('$script:PayloadReceipt = Assert-DeferredPayloadIdentity',[StringComparison]::Ordinal) -gt $payloadBootstrap) 'The exact deployed payload hashes must be rechecked during final cleanup/completion.'
$uiStateFunction = Get-FunctionAst 'Get-DeferredTouchUiState'
Assert-Condition ($source.Contains('$textPattern.pattern.SupportedTextSelection') -and $source.Contains("'TouchSelectionAncestorViewport'") -and $source.Contains('[System.Windows.Automation.ScrollPattern]::Pattern') -and -not $uiStateFunction.Extent.Text.Contains('Get-State')) 'The short fixture state reader must verify supported-selection and viewport contracts without requiring long-document scroll overflow.'
foreach ($functionName in @('Invoke-DeferredTouchPattern','Set-DeferredTouchViewport','Get-DeferredTouchReceipt')) {
    $functionText = (Get-FunctionAst $functionName).Extent.Text
    Assert-Condition ($functionText.Contains('Find-UniqueProcessElement') -and $functionText.Contains('ProcessId -ne 0')) "$functionName must allow raw WinUI PID zero only after the shared verified-window provenance resolver."
}
$menuFunction = (Get-FunctionAst 'Get-DeferredTouchSelectionMenuCommands').Extent.Text
Assert-Condition ($menuFunction.Contains('ownerProcessId') -and $menuFunction.Contains('ownerWindowHandle') -and $menuFunction.Contains('reportedProcessId')) 'PID-zero flyout elements require an exact verified-root envelope and matching reported PID.'
Assert-Condition ($source.Contains('firstContactPumpMetrics = $null') -and
    $source.Contains('$script:Evidence.firstContactPumpMetrics = Send-DeferredTouchFrame') -and
    $source.Contains('freshContactPumpMetrics = $null') -and
    $source.Contains('$script:Evidence.freshContactPumpMetrics = Send-DeferredTouchFrame')) 'The actual first and fresh touch pump update/gap/terminal receipts must be serialized in final evidence.'
$finallyIndex = $source.LastIndexOf('finally {',[StringComparison]::Ordinal)
$finallySource = $source.Substring($finallyIndex)
$pumpStop = $finallySource.IndexOf('Stop-DeferredTouchInputPump',[StringComparison]::Ordinal)
$nativeSend = $finallySource.IndexOf('[MarkdownCorrectnessSandboxNative]::Send',[StringComparison]::Ordinal)
$destroy = $finallySource.IndexOf('[MarkdownCorrectnessSandboxNative]::DestroyTouchDevice',[StringComparison]::Ordinal)
Assert-Condition ($pumpStop -ge 0 -and ($nativeSend -lt 0 -or $pumpStop -lt $nativeSend) -and ($destroy -lt 0 -or $pumpStop -lt $destroy)) 'Final cleanup must stop/join the worker before fallback input or device destruction.'
Assert-Condition ($finallySource.Contains('if ($null -ne $script:TouchPump)') -and $finallySource.Contains('$cleanup.deviceRetained')) 'Failed worker join must retain its device rather than race destruction.'
Assert-Condition ($finallySource.Contains('pumpStopped=(Test-DeferredTouchPumpStopped)') -and
    $finallySource.Contains('if (-not (Test-DeferredTouchPumpStopped))')) 'Final cleanup must reuse positive join evidence from ordinary terminal completion and retain devices when join is uncertain.'
$startPumpSource = (Get-FunctionAst 'Start-DeferredTouchInputPump').Extent.Text
Assert-Condition ($startPumpSource.IndexOf('$script:TouchPumpJoined = $false',[StringComparison]::Ordinal) -lt
    $startPumpSource.IndexOf('$powerShell.BeginInvoke()',[StringComparison]::Ordinal)) 'Pump startup must invalidate join proof before invoking a worker.'

# Execute the real stop/join code with in-memory worker stand-ins only. No
# shared-probe bootstrap, native methods, app, or guest input is executed.
$script:TouchPump = $null
$script:TouchPumpJoined = $true
Assert-Condition (Test-DeferredTouchPumpStopped) 'No started pump is a safe stopped state.'
$script:TouchPumpJoined = $false
Assert-Condition (-not (Test-DeferredTouchPumpStopped)) 'A missing reference after uncertain startup is not affirmative join evidence.'
$script:TouchPump = [pscustomobject]@{}
Assert-Condition (-not (Test-DeferredTouchPumpStopped)) 'An unjoined pump remains unsafe.'
$script:TouchPumpJoined = $true
Assert-Condition (-not (Test-DeferredTouchPumpStopped)) 'A live pump reference remains unsafe even with stale join proof.'

$completedEvents = [Collections.Concurrent.ConcurrentQueue[object]]::new()
$completedEvents.Enqueue([pscustomobject]@{kind='complete';terminal='release';updates=3;maxGapMilliseconds=40.0})
$completedPowerShell = [pscustomobject]@{ended=$false;disposed=$false}
$completedPowerShell | Add-Member ScriptMethod EndInvoke { param($async) $this.ended=$true }
$completedPowerShell | Add-Member ScriptMethod Dispose { $this.disposed=$true }
$completedRunspace = [pscustomobject]@{disposed=$false}
$completedRunspace | Add-Member ScriptMethod Dispose { $this.disposed=$true }
$completedWait = [pscustomobject]@{}
$completedWait | Add-Member ScriptMethod WaitOne { param($milliseconds) return $true }
$completedSignal = [pscustomobject]@{disposed=$false}
$completedSignal | Add-Member ScriptMethod Set { return $true }
$completedSignal | Add-Member ScriptMethod Dispose { $this.disposed=$true }
$script:TouchPump = [pscustomobject]@{
    commands=[Collections.Concurrent.ConcurrentQueue[object]]::new();events=$completedEvents
    signal=$completedSignal;runspace=$completedRunspace;powerShell=$completedPowerShell
    async=[pscustomobject]@{AsyncWaitHandle=$completedWait}
}
$script:TouchPumpJoined = $false
$script:TouchMayBeInContact = $true
$script:TouchInContact = $true
$completionOutput = @(Stop-DeferredTouchInputPump -Terminal release)
Assert-Condition ($completionOutput.Count -eq 1) 'Stop/join must return only the terminal metrics receipt, not AutoResetEvent.Set boolean output.'
$completed = $completionOutput[0]
Assert-Condition ($completed.terminal -ceq 'release' -and $completedPowerShell.ended -and
    $completedPowerShell.disposed -and $completedRunspace.disposed -and $completedSignal.disposed) 'Ordinary completion must actually join/end and dispose the worker.'
Assert-Condition ((Test-DeferredTouchPumpStopped) -and -not $script:TouchMayBeInContact -and
    -not $script:TouchInContact) 'An already-joined ordinary terminal must retain positive stopped evidence for final cleanup.'

$unjoinedWait = [pscustomobject]@{}
$unjoinedWait | Add-Member ScriptMethod WaitOne { param($milliseconds) return $false }
$unjoinedPowerShell = [pscustomobject]@{stopAttempted=$false}
$unjoinedPowerShell | Add-Member ScriptMethod Stop { $this.stopAttempted=$true }
$script:TouchPump = [pscustomobject]@{
    commands=[Collections.Concurrent.ConcurrentQueue[object]]::new();signal=$completedSignal
    async=[pscustomobject]@{AsyncWaitHandle=$unjoinedWait};powerShell=$unjoinedPowerShell
}
$script:TouchPumpJoined = $false
Assert-Throws { Stop-DeferredTouchInputPump -Terminal cancel } 'unjoined worker must not publish stopped evidence'
Assert-Condition ($unjoinedPowerShell.stopAttempted -and $null -ne $script:TouchPump -and
    -not (Test-DeferredTouchPumpStopped)) 'Failed bounded join must retain the worker and unsafe cleanup state.'

foreach ($scenario in @('Release','CancelNew','CancelExisting')) {
    $fixture = New-EvidenceFixture -Scenario $scenario
    $result = Invoke-EvidenceFixture $fixture
    Assert-Condition ($result.status -ceq 'PASS' -and $result.scenario -ceq $scenario) "Valid $scenario evidence fixture was rejected."
}

$multipleCancel = New-EvidenceFixture -Scenario CancelExisting -CancelTerminalCount 2
$multipleResult = Invoke-EvidenceFixture $multipleCancel
Assert-Condition ($multipleResult.status -ceq 'PASS') 'Multiple exact canceled terminal callbacks should remain valid for one owned contact.'

$fixture = New-EvidenceFixture -Scenario CancelExisting
$fixture.holding.beforeTerminal.queuedCount = 3
Assert-Condition ((Invoke-EvidenceFixture $fixture).status -ceq 'PASS') 'BeforeTerminal may contain nonterminal queued pointer moves when no matching terminal is queued.'

$fixture = New-EvidenceFixture -Scenario CancelExisting
$fixture.dropped.workerHeldAtDrop = $false
Assert-Throws { Invoke-EvidenceFixture $fixture } 'worker lease must still be held at terminal processing'

$fixture = New-EvidenceFixture -Scenario CancelNew
$fixture.holding.beforeTerminal.matchingTerminalCount = 1
Assert-Throws { Invoke-EvidenceFixture $fixture } 'before-terminal queue must be empty'

$fixture = New-EvidenceFixture -Scenario CancelExisting
$fixture.dropped.afterDrop.selectionRange = New-Range 4 17
Assert-Throws { Invoke-EvidenceFixture $fixture } 'CancelExisting must preserve the exact pre-contact range'

$fixture = New-EvidenceFixture -Scenario CancelExisting
$fixture.dropped.afterDrop.selectionContextMenuOpen = $true
Assert-Throws { Invoke-EvidenceFixture $fixture } 'cancellation must close the selection flyout'

$fixture = New-EvidenceFixture -Scenario CancelNew
$fixture.dropped.afterDrop.selectionIsActive = $true
Assert-Throws { Invoke-EvidenceFixture $fixture } 'CancelNew must retire the temporary selection'

$fixture = New-EvidenceFixture -Scenario Release -CancelTerminalCount 2
Assert-Throws { Invoke-EvidenceFixture $fixture } 'release must contain exactly one terminal'

$fixture = New-EvidenceFixture -Scenario CancelNew
$fixture.dropped.queuedTerminal.queuedTerminals[0].isTouch = $false
Assert-Throws { Invoke-EvidenceFixture $fixture } 'terminal provenance must identify touch input'

$fixture = New-EvidenceFixture -Scenario CancelNew
$fixture.fresh.freshContact.activePointerId = 21
Assert-Throws { Invoke-EvidenceFixture $fixture } 'fresh contact must have independent pointer provenance'

$fixture = New-EvidenceFixture -Scenario CancelNew
$fixture.fresh.freshContact.gestureEpoch = 44
Assert-Throws { Invoke-EvidenceFixture $fixture } 'fresh contact must have a new gesture epoch'

$fixture = New-EvidenceFixture -Scenario CancelExisting
$fixture.complete.pointerId = 22
Assert-Throws { Invoke-EvidenceFixture $fixture } 'complete top-level receipt must retain the original pointer owner'

$fixture = New-EvidenceFixture -Scenario CancelExisting
$fixture.complete.gestureEpoch = 45
Assert-Throws { Invoke-EvidenceFixture $fixture } 'complete top-level receipt must retain the original gesture epoch'

foreach ($phaseName in @('dropped','awaiting','fresh','complete')) {
    $fixture = New-EvidenceFixture -Scenario CancelExisting
    $fixture.$phaseName.pointerId = 22
    Assert-Throws { Invoke-EvidenceFixture $fixture } "$phaseName top-level receipt must retain the original pointer owner"
    $fixture = New-EvidenceFixture -Scenario CancelExisting
    $fixture.$phaseName.gestureEpoch = 45
    Assert-Throws { Invoke-EvidenceFixture $fixture } "$phaseName top-level receipt must retain the original gesture epoch"
}

$fixture = New-EvidenceFixture -Scenario CancelExisting
$fixture.armed.pointerId = 21
Assert-Throws { Invoke-EvidenceFixture $fixture } 'armed top-level receipt must not claim a pointer owner'
$fixture = New-EvidenceFixture -Scenario CancelExisting
$fixture.armed.gestureEpoch = 44
Assert-Throws { Invoke-EvidenceFixture $fixture } 'armed top-level receipt must not claim a gesture epoch'

$fixture = New-EvidenceFixture -Scenario CancelExisting
$fixture.awaiting.queuedTerminal.gestureEpoch = 43
Assert-Throws { Invoke-EvidenceFixture $fixture } 'awaiting receipt must preserve the exact queued-terminal owner epoch'

$fixture = New-EvidenceFixture -Scenario CancelNew
$fixture.dropped.queuedTerminal.queuedTerminals[0].gestureEpoch = 43
Assert-Throws { Invoke-EvidenceFixture $fixture } 'queued terminal must match the held gesture epoch'

$fixture = New-EvidenceFixture -Scenario CancelNew
$fixture.finalUi.selection = 'stale word'
Assert-Throws { Invoke-EvidenceFixture $fixture } 'final UIA selection must be the fresh range'

$fixture = New-EvidenceFixture -Scenario CancelNew
$fixture.finalUi.handles = @($fixture.finalUi.handles[0])
Assert-Throws { Invoke-EvidenceFixture $fixture } 'final real selection must expose both handles'

$singleCaret = [pscustomobject]@{supportedTextSelection='Single';rangeCount=1;selection='';selectionRects=@();handles=@()}
Assert-Condition (Test-DeferredTouchClearedSelection -State $singleCaret) 'A single collapsed caret without rectangles or handles is not selected content.'
$noneRanges = [pscustomobject]@{supportedTextSelection='None';rangeCount=0;selection='';selectionRects=@();handles=@()}
Assert-Condition (Test-DeferredTouchClearedSelection -State $noneRanges) 'SupportedTextSelection.None with zero ranges is a cleared selection.'
$noneCaret = [pscustomobject]@{supportedTextSelection='None';rangeCount=1;selection='';selectionRects=@();handles=@()}
Assert-Condition (-not (Test-DeferredTouchClearedSelection -State $noneCaret)) 'SupportedTextSelection.None must not admit a caret range.'
$visibleCaret = [pscustomobject]@{supportedTextSelection='Single';rangeCount=1;selection='';selectionRects=@(@{x=10;y=20;width=0;height=17});handles=@()}
Assert-Condition (-not (Test-DeferredTouchClearedSelection -State $visibleCaret)) 'A geometry-bearing caret is not an empty selection.'
$textSelection = [pscustomobject]@{supportedTextSelection='Single';rangeCount=1;selection='content';selectionRects=@();handles=@()}
Assert-Condition (-not (Test-DeferredTouchClearedSelection -State $textSelection)) 'Nonempty selected text is never considered cleared.'
$validSelection = [pscustomobject]@{supportedTextSelection='Single';rangeCount=1;selection='word';selectionRects=@(@{x=10.0;y=20.0;width=44.0;height=18.0});handles=@()}
$selectedState = Assert-DeferredTouchSelection -State $validSelection -ExpectedSelected $true -ExpectedText 'word' -Stage 'offline visible selection'
Assert-Condition ($selectedState.selection -ceq 'word') 'A real text selection with finite visible geometry should validate.'
$hiddenSelection = [pscustomobject]@{supportedTextSelection='Single';rangeCount=1;selection='word';selectionRects=@();handles=@()}
Assert-Throws { Assert-DeferredTouchSelection -State $hiddenSelection -ExpectedSelected $true -ExpectedText 'word' -Stage 'offline hidden selection' } 'text selection without visible range geometry'
$busySelection = New-BusySelectionFixture
$busyValidated = Assert-DeferredTouchSelection -State $busySelection.uiState -ExpectedSelected $true -ExpectedText 'deferredword' `
    -Stage 'BeforeTerminal UIA state' -AllowUnavailableGeometry -HeldReceipt $busySelection.receipt -ExpectedSequence 9
Assert-Condition ($busyValidated.selection -ceq 'deferredword') 'BeforeTerminal may preserve exact selected text while its snapshot lease makes geometry unavailable.'
$busySelection = New-BusySelectionFixture
Assert-Throws { Assert-DeferredTouchSelection -State $busySelection.uiState -ExpectedSelected $true -ExpectedText 'deferredword' `
    -Stage 'BeforeTerminal UIA state' -AllowUnavailableGeometry -HeldReceipt $busySelection.receipt -ExpectedSequence 8 } 'stale held receipt sequence'
$busySelection = New-BusySelectionFixture
Assert-Throws { Assert-DeferredTouchSelection -State $busySelection.uiState -ExpectedSelected $true -ExpectedText 'deferredword' `
    -Stage 'Final UIA state' -AllowUnavailableGeometry -HeldReceipt $busySelection.receipt -ExpectedSequence 9 } 'geometry exception outside BeforeTerminal stage'
$busySelection = New-BusySelectionFixture
$busySelection.receipt.workerOwnsSnapshot = $false
Assert-Throws { Assert-DeferredTouchSelection -State $busySelection.uiState -ExpectedSelected $true -ExpectedText 'deferredword' `
    -Stage 'BeforeTerminal UIA state' -AllowUnavailableGeometry -HeldReceipt $busySelection.receipt -ExpectedSequence 9 } 'missing held snapshot lease proof'
$busySelection = New-BusySelectionFixture
$busySelection.receipt.pointerId = 0
Assert-Throws { Assert-DeferredTouchSelection -State $busySelection.uiState -ExpectedSelected $true -ExpectedText 'deferredword' `
    -Stage 'BeforeTerminal UIA state' -AllowUnavailableGeometry -HeldReceipt $busySelection.receipt -ExpectedSequence 9 } 'missing held pointer owner'
$busySelection = New-BusySelectionFixture
$busySelection.receipt.beforeTerminal.gestureEpoch++
Assert-Throws { Assert-DeferredTouchSelection -State $busySelection.uiState -ExpectedSelected $true -ExpectedText 'deferredword' `
    -Stage 'BeforeTerminal UIA state' -AllowUnavailableGeometry -HeldReceipt $busySelection.receipt -ExpectedSequence 9 } 'mismatched held gesture epoch'
$busySelection = New-BusySelectionFixture
$busySelection.receipt.beforeTerminal.matchingTerminalCount = 1
Assert-Throws { Assert-DeferredTouchSelection -State $busySelection.uiState -ExpectedSelected $true -ExpectedText 'deferredword' `
    -Stage 'BeforeTerminal UIA state' -AllowUnavailableGeometry -HeldReceipt $busySelection.receipt -ExpectedSequence 9 } 'terminal already queued before geometry exception'
$busySelection = New-BusySelectionFixture
$busySelection.receipt.beforeTerminal.queuedTerminals = @([pscustomobject]@{kind='Cancel'})
Assert-Throws { Assert-DeferredTouchSelection -State $busySelection.uiState -ExpectedSelected $true -ExpectedText 'deferredword' `
    -Stage 'BeforeTerminal UIA state' -AllowUnavailableGeometry -HeldReceipt $busySelection.receipt -ExpectedSequence 9 } 'queued terminal before geometry exception'
$busySelection = New-BusySelectionFixture
$busySelection.receipt.beforeTerminal.selectionIsActive = $false
Assert-Throws { Assert-DeferredTouchSelection -State $busySelection.uiState -ExpectedSelected $true -ExpectedText 'deferredword' `
    -Stage 'BeforeTerminal UIA state' -AllowUnavailableGeometry -HeldReceipt $busySelection.receipt -ExpectedSequence 9 } 'inactive logical selection before geometry exception'
$busySelection = New-BusySelectionFixture
$busySelection.receipt.phase = 'awaitingFreshContact'
Assert-Throws { Assert-DeferredTouchSelection -State $busySelection.uiState -ExpectedSelected $true -ExpectedText 'deferredword' `
    -Stage 'BeforeTerminal UIA state' -AllowUnavailableGeometry -HeldReceipt $busySelection.receipt -ExpectedSequence 9 } 'wrong receipt phase for geometry exception'
$busySelection = New-BusySelectionFixture
$busySelection.uiState.selectionRects = @(@{x=10.0;y=20.0;width=44.0;height=18.0})
$busySelection.receipt.sequence = 8
Assert-Throws { Assert-DeferredTouchSelection -State $busySelection.uiState -ExpectedSelected $true -ExpectedText 'deferredword' `
    -Stage 'BeforeTerminal UIA state' -AllowUnavailableGeometry -HeldReceipt $busySelection.receipt -ExpectedSequence 9 } 'allowance flag must always require exact held evidence'
$orphanGeometry = [pscustomobject]@{supportedTextSelection='Single';rangeCount=0;selection='';selectionRects=@(@{x=10.0;y=20.0;width=44.0;height=18.0});handles=@()}
Assert-Throws { Assert-DeferredTouchSelection -State $orphanGeometry -ExpectedSelected $false -Stage 'offline orphan rectangle' } 'empty range list with leftover selected geometry'

$badRange = [pscustomobject]@{start=[pscustomobject]@{blockIndex=0;inlineIndex=0};end=[pscustomobject]@{blockIndex=0;inlineIndex=0;characterOffset=0}}
Assert-Throws { Get-DeferredRangeFingerprint $badRange } 'range fingerprint must reject incomplete logical positions'

$script:ExpectedSampleDllSha256 = 'A' * 64
$script:ExpectedRendererDllSha256 = 'B' * 64
$sampleDirectory = Join-Path ([IO.Path]::GetTempPath()) ('DeferredTouchPayload-' + [guid]::NewGuid().ToString('N'))
$script:Identity = [pscustomobject]@{exePath=(Join-Path $sampleDirectory 'MarkdownRenderer.Sample.exe')}
$script:MockPayload = [pscustomobject]@{
    executablePath=$script:Identity.exePath
    files=@(
        [pscustomobject]@{name='MarkdownRenderer.Sample.dll';path=(Join-Path $sampleDirectory 'MarkdownRenderer.Sample.dll');length=1024;sha256=$script:ExpectedSampleDllSha256.ToLowerInvariant()},
        [pscustomobject]@{name='MarkdownRenderer.dll';path=(Join-Path $sampleDirectory 'MarkdownRenderer.dll');length=2048;sha256=$script:ExpectedRendererDllSha256.ToLowerInvariant()})
}
$payloadResult = Assert-DeferredPayloadIdentity
Assert-Condition (@($payloadResult.files).Count -eq 2) 'Exact payload hashes beside the captured executable should validate.'
$script:MockPayload.files[0].sha256 = 'C' * 64
Assert-Throws { Assert-DeferredPayloadIdentity } 'changed sample DLL payload hash'
$script:MockPayload.files[0].sha256 = $script:ExpectedSampleDllSha256.ToLowerInvariant()
$script:MockPayload.files[1].sha256 = 'D' * 64
Assert-Throws { Assert-DeferredPayloadIdentity } 'changed renderer DLL payload hash'
$script:MockPayload.files[1].sha256 = $script:ExpectedRendererDllSha256.ToLowerInvariant()
$script:MockPayload.files[1].path = Join-Path $sampleDirectory 'unrelated\MarkdownRenderer.dll'
Assert-Throws { Assert-DeferredPayloadIdentity } 'renderer DLL outside the validated executable directory'

Write-Output 'PASS: deferred-touch driver parses; payload provenance, receipt owner continuity, pump-metric evidence, selection/caret semantics, narrowly evidenced held-snapshot geometry allowance, positive ordinary-terminal join proof, single terminal metrics result, bounded worker cleanup guards and 63 offline cases passed.'
