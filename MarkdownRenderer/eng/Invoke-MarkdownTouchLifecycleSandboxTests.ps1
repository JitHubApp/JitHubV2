[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$PriorRunJson,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\correctness'),
    [string]$SharedProbePath = (Join-Path $PSScriptRoot 'MarkdownCorrectnessSandboxProbe.ps1'),
    [switch]$RunApprovedSandboxCases
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
$script:TouchLifecycleCoordinatorWaitSeconds = 150

function Get-TouchLifecycleProperty {
    param([AllowNull()][object]$Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $null }
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $null
    }
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Test-TouchLifecycleProperty {
    param([AllowNull()][object]$Object, [Parameter(Mandatory)][string]$Name)
    if ($null -eq $Object) { return $false }
    if ($Object -is [System.Collections.IDictionary]) { return $Object.Contains($Name) }
    return $null -ne $Object.PSObject.Properties[$Name]
}

function ConvertFrom-TouchLifecycleNativeJson {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$JsonText)
    $trimmed = $JsonText.TrimStart()
    if ([string]::IsNullOrWhiteSpace($trimmed)) { throw 'winapp returned empty JSON text.' }
    try {
        if ($trimmed.StartsWith('[', [StringComparison]::Ordinal)) {
            # Keep a top-level [] as an actual empty object[] instead of letting
            # PowerShell pipeline enumeration turn it into $null.
            if ($trimmed -match '^\[\s*\]$') { $value = [object[]]::new(0) }
            else { $value = @(ConvertFrom-Json -InputObject $JsonText -ErrorAction Stop) }
        }
        else { $value = ConvertFrom-Json -InputObject $JsonText -ErrorAction Stop }
    }
    catch { throw "winapp returned malformed JSON: $_" }
    return [pscustomobject]@{ value = $value }
}

function Assert-TouchLifecyclePriorRun {
    param([Parameter(Mandatory)]$Receipt)
    $target = [string](Get-TouchLifecycleProperty $Receipt 'target')
    $epoch = [string](Get-TouchLifecycleProperty $Receipt 'targetEpoch')
    $architecture = [string](Get-TouchLifecycleProperty $Receipt 'architecture')
    $launch = Get-TouchLifecycleProperty $Receipt 'launch'
    $executionTarget = Get-TouchLifecycleProperty $launch 'ExecutionTarget'
    $app = Get-TouchLifecycleProperty $Receipt 'app'
    $build = Get-TouchLifecycleProperty $Receipt 'buildBinary'
    $payload = Get-TouchLifecycleProperty $Receipt 'payloadIdentity'
    $appPid = 0
    $appHwnd = 0L
    if ($target -cne 'sandbox' -or [string]::IsNullOrWhiteSpace($epoch) -or $architecture -cne 'x64' -or
        (Get-TouchLifecycleProperty $launch 'Sandbox') -isnot [bool] -or -not [bool](Get-TouchLifecycleProperty $launch 'Sandbox') -or
        [string](Get-TouchLifecycleProperty $launch 'ProcessScope') -cne 'sandbox' -or
        [string](Get-TouchLifecycleProperty $executionTarget 'Kind') -cne 'sandbox' -or
        [string](Get-TouchLifecycleProperty $executionTarget 'Id') -cne 'default' -or
        [string](Get-TouchLifecycleProperty $executionTarget 'Architecture') -cne 'x64' -or
        [string](Get-TouchLifecycleProperty $executionTarget 'Epoch') -cne $epoch) {
        throw 'Prior run must identify the same x64 Sandbox target and a nonempty target epoch.'
    }
    if ($null -eq $app -or [string](Get-TouchLifecycleProperty $app 'target') -cne 'sandbox' -or
        [string](Get-TouchLifecycleProperty $app 'epoch') -cne $epoch -or
        -not [int]::TryParse([string](Get-TouchLifecycleProperty $app 'processId'), [ref]$appPid) -or $appPid -le 0 -or
        -not [long]::TryParse([string](Get-TouchLifecycleProperty $app 'hwnd'), [ref]$appHwnd) -or $appHwnd -le 0 -or
        [string](Get-TouchLifecycleProperty $app 'processName') -cne 'MarkdownRenderer.Sample' -or
        [string]::IsNullOrWhiteSpace([string](Get-TouchLifecycleProperty $app 'title'))) {
        throw 'Prior run lacks the exact original Sandbox sample PID/HWND/title identity.'
    }
    $buildPath = [string](Get-TouchLifecycleProperty $build 'path')
    $buildHash = [string](Get-TouchLifecycleProperty $build 'sha256')
    $buildLength = 0L
    if ($null -eq $build -or -not [IO.Path]::IsPathRooted($buildPath) -or
        [IO.Path]::GetFileName($buildPath) -cne 'MarkdownRenderer.Sample.exe' -or
        $buildHash -notmatch '^[0-9a-fA-F]{64}$' -or
        -not [long]::TryParse([string](Get-TouchLifecycleProperty $build 'length'), [ref]$buildLength) -or $buildLength -le 0) {
        throw 'Prior run has no valid exact sample executable build identity.'
    }
    if ($null -eq $payload -or [string](Get-TouchLifecycleProperty $payload 'status') -cne 'MATCH') {
        throw 'Prior run does not attest that its host and Sandbox payloads match.'
    }
    $hostPayloads = @(Get-TouchLifecycleProperty $payload 'host')
    $guestPayloads = @(Get-TouchLifecycleProperty $payload 'guest')
    $guestExe = [string](Get-TouchLifecycleProperty $payload 'guestExecutablePath')
    if (-not [IO.Path]::IsPathRooted($guestExe) -or
        [IO.Path]::GetFileName($guestExe) -cne 'MarkdownRenderer.Sample.exe' -or
        [int](Get-TouchLifecycleProperty $payload 'guestProcessId') -ne $appPid -or
        -not [string]::Equals([IO.Path]::GetFullPath($buildPath), [IO.Path]::GetFullPath([string](@($hostPayloads | Where-Object { [string](Get-TouchLifecycleProperty $_ 'name') -ceq 'MarkdownRenderer.Sample.exe' } | Select-Object -First 1 | ForEach-Object { Get-TouchLifecycleProperty $_ 'path' }))), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Prior run does not bind the exact Sandbox executable to its original PID and buildBinary.'
    }
    $expectedNames = @('MarkdownRenderer.Sample.exe', 'MarkdownRenderer.Sample.dll', 'MarkdownRenderer.dll')
    if ($hostPayloads.Count -ne $guestPayloads.Count) { throw 'Prior host and guest payload inventories have different entry counts.' }
    $hostNames = @($hostPayloads | ForEach-Object { [string](Get-TouchLifecycleProperty $_ 'name') })
    $guestNames = @($guestPayloads | ForEach-Object { [string](Get-TouchLifecycleProperty $_ 'name') })
    $payloadNameDifferences = @(Compare-Object -ReferenceObject @($hostNames | Sort-Object) -DifferenceObject @($guestNames | Sort-Object) -CaseSensitive)
    if (@($hostNames | Sort-Object -Unique).Count -ne $hostNames.Count -or
        @($guestNames | Sort-Object -Unique).Count -ne $guestNames.Count -or
        $payloadNameDifferences.Count -ne 0) {
        throw 'Prior host and guest payload inventories do not contain the same unique names.'
    }
    $validated = [Collections.Generic.List[object]]::new()
    foreach ($name in $expectedNames) {
        $hostMatches = @($hostPayloads | Where-Object { [string](Get-TouchLifecycleProperty $_ 'name') -ceq $name })
        $guestMatches = @($guestPayloads | Where-Object { [string](Get-TouchLifecycleProperty $_ 'name') -ceq $name })
        if ($hostMatches.Count -ne 1 -or $guestMatches.Count -ne 1) { throw "Prior run must contain exactly one host and guest '$name'." }
    }
    foreach ($hostEntry in $hostPayloads) {
        $name = [string](Get-TouchLifecycleProperty $hostEntry 'name')
        $guestMatches = @($guestPayloads | Where-Object { [string](Get-TouchLifecycleProperty $_ 'name') -ceq $name })
        $hostHash = [string](Get-TouchLifecycleProperty $hostEntry 'sha256')
        $guestHash = if ($guestMatches.Count -eq 1) { [string](Get-TouchLifecycleProperty $guestMatches[0] 'sha256') } else { '' }
        $hostLength = 0L
        $guestLength = 0L
        $hostFile = [string](Get-TouchLifecycleProperty $hostEntry 'path')
        $guestFile = if ($guestMatches.Count -eq 1) { [string](Get-TouchLifecycleProperty $guestMatches[0] 'path') } else { '' }
        if ($guestMatches.Count -ne 1 -or $hostHash -notmatch '^[0-9a-fA-F]{64}$' -or
            $guestHash -notmatch '^[0-9a-fA-F]{64}$' -or
            -not [long]::TryParse([string](Get-TouchLifecycleProperty $hostEntry 'length'), [ref]$hostLength) -or $hostLength -le 0 -or
            -not [long]::TryParse([string](Get-TouchLifecycleProperty $guestMatches[0] 'length'), [ref]$guestLength) -or
            $hostLength -ne $guestLength -or $hostHash -cne $guestHash -or
            [IO.Path]::GetFileName($hostFile) -cne $name -or [IO.Path]::GetFileName($guestFile) -cne $name -or
            -not [string]::Equals([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($hostFile)), [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($buildPath)), [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($guestFile)), [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($guestExe)), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Prior run has a missing, mismatched, or misplaced host/guest payload '$name'."
        }
        $validated.Add([pscustomobject]@{ name = $name; hostPath = [IO.Path]::GetFullPath($hostFile); guestPath = [IO.Path]::GetFullPath($guestFile); length = $hostLength; sha256 = $hostHash.ToLowerInvariant() })
    }
    if ($buildLength -ne [long](@($validated | Where-Object name -ceq 'MarkdownRenderer.Sample.exe')[0].length) -or
        $buildHash -cne [string](@($validated | Where-Object name -ceq 'MarkdownRenderer.Sample.exe')[0].sha256)) {
        throw 'Prior buildBinary differs from the exact matched sample executable payload.'
    }
    return [pscustomobject]@{
        target = 'sandbox'; targetEpoch = $epoch; architecture = 'x64'
        appProcessId = $appPid; appHwnd = $appHwnd; appProcessName = 'MarkdownRenderer.Sample'; appTitle = [string](Get-TouchLifecycleProperty $app 'title')
        hostExecutablePath = [IO.Path]::GetFullPath($buildPath); guestExecutablePath = [IO.Path]::GetFullPath($guestExe)
        exeSha256 = $buildHash.ToLowerInvariant(); buildBinary = $build; payloads = @($validated.ToArray())
        runId = [string](Get-TouchLifecycleProperty $Receipt 'runId')
    }
}

function Assert-TouchLifecycleSnapshot {
    param([Parameter(Mandatory)]$Snapshot, [Parameter(Mandatory)][string]$ExpectedEpoch)
    $target = Get-TouchLifecycleProperty $Snapshot 'executionTarget'
    $capabilities = Get-TouchLifecycleProperty $Snapshot 'capabilities'
    $desktop = Get-TouchLifecycleProperty $Snapshot 'desktop'
    if ((Get-TouchLifecycleProperty $Snapshot 'running') -isnot [bool] -or
        -not [bool](Get-TouchLifecycleProperty $Snapshot 'running') -or
        [string](Get-TouchLifecycleProperty $target 'epoch') -cne $ExpectedEpoch -or
        [string](Get-TouchLifecycleProperty $capabilities 'architecture') -cne 'x64' -or
        (Get-TouchLifecycleProperty $desktop 'effectiveInputReady') -isnot [bool] -or
        -not [bool](Get-TouchLifecycleProperty $desktop 'effectiveInputReady') -or
        (Get-TouchLifecycleProperty $desktop 'effectiveCaptureReady') -isnot [bool] -or
        -not [bool](Get-TouchLifecycleProperty $desktop 'effectiveCaptureReady')) {
        throw 'Sandbox is not the same running x64 epoch with effective input and capture ready.'
    }
    return $Snapshot
}

function Assert-TouchLifecycleProcessIdentity {
    param([Parameter(Mandatory)]$Actual, [Parameter(Mandatory)]$Expected)
    $actualPid = 0; $expectedPid = 0; $actualStart = 0L; $expectedStart = 0L; $actualHwnd = 0L; $expectedHwnd = 0L
    $actualPath = [string](Get-TouchLifecycleProperty $Actual 'executablePath')
    $expectedPath = [string](Get-TouchLifecycleProperty $Expected 'executablePath')
    $actualHash = [string](Get-TouchLifecycleProperty $Actual 'exeSha256')
    $expectedHash = [string](Get-TouchLifecycleProperty $Expected 'exeSha256')
    if (-not [int]::TryParse([string](Get-TouchLifecycleProperty $Actual 'processId'), [ref]$actualPid) -or $actualPid -le 0 -or
        -not [int]::TryParse([string](Get-TouchLifecycleProperty $Expected 'processId'), [ref]$expectedPid) -or $expectedPid -le 0 -or
        -not [long]::TryParse([string](Get-TouchLifecycleProperty $Actual 'startTicks'), [ref]$actualStart) -or $actualStart -le 0 -or
        -not [long]::TryParse([string](Get-TouchLifecycleProperty $Expected 'startTicks'), [ref]$expectedStart) -or $expectedStart -le 0 -or
        -not [long]::TryParse([string](Get-TouchLifecycleProperty $Actual 'hwnd'), [ref]$actualHwnd) -or $actualHwnd -le 0 -or
        -not [long]::TryParse([string](Get-TouchLifecycleProperty $Expected 'hwnd'), [ref]$expectedHwnd) -or $expectedHwnd -le 0 -or
        [string](Get-TouchLifecycleProperty $Actual 'processName') -cne [string](Get-TouchLifecycleProperty $Expected 'processName') -or
        -not [IO.Path]::IsPathRooted($actualPath) -or -not [IO.Path]::IsPathRooted($expectedPath) -or
        $actualHash -notmatch '^[0-9a-fA-F]{64}$' -or $expectedHash -notmatch '^[0-9a-fA-F]{64}$' -or
        $actualHash -cne $expectedHash -or
        [string](Get-TouchLifecycleProperty $Actual 'title') -cne [string](Get-TouchLifecycleProperty $Expected 'title') -or
        $actualPid -ne $expectedPid -or $actualStart -ne $expectedStart -or $actualHwnd -ne $expectedHwnd -or
        -not [string]::Equals([IO.Path]::GetFullPath($actualPath), [IO.Path]::GetFullPath($expectedPath), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Process identity differs in PID, exact start ticks, path, SHA-256, HWND, or window title.'
    }
    return [pscustomobject]@{
        processId = $actualPid; processName = [string](Get-TouchLifecycleProperty $Actual 'processName')
        executablePath = [IO.Path]::GetFullPath($actualPath); startTicks = $actualStart; exeSha256 = $actualHash.ToLowerInvariant()
        hwnd = $actualHwnd; title = [string](Get-TouchLifecycleProperty $Actual 'title')
    }
}

function Assert-TouchLifecycleProcessCoreIdentity {
    param([Parameter(Mandatory)]$Actual, [Parameter(Mandatory)]$Expected)
    $actualPid = 0; $expectedPid = 0; $actualStart = 0L; $expectedStart = 0L
    $actualPath = [string](Get-TouchLifecycleProperty $Actual 'executablePath')
    $expectedPath = [string](Get-TouchLifecycleProperty $Expected 'executablePath')
    $actualHash = [string](Get-TouchLifecycleProperty $Actual 'exeSha256')
    $expectedHash = [string](Get-TouchLifecycleProperty $Expected 'exeSha256')
    if (-not [int]::TryParse([string](Get-TouchLifecycleProperty $Actual 'processId'), [ref]$actualPid) -or $actualPid -le 0 -or
        -not [int]::TryParse([string](Get-TouchLifecycleProperty $Expected 'processId'), [ref]$expectedPid) -or $expectedPid -le 0 -or
        -not [long]::TryParse([string](Get-TouchLifecycleProperty $Actual 'startTicks'), [ref]$actualStart) -or $actualStart -le 0 -or
        -not [long]::TryParse([string](Get-TouchLifecycleProperty $Expected 'startTicks'), [ref]$expectedStart) -or $expectedStart -le 0 -or
        [string](Get-TouchLifecycleProperty $Actual 'processName') -cne [string](Get-TouchLifecycleProperty $Expected 'processName') -or
        -not [IO.Path]::IsPathRooted($actualPath) -or -not [IO.Path]::IsPathRooted($expectedPath) -or
        $actualHash -notmatch '^[0-9a-fA-F]{64}$' -or $expectedHash -notmatch '^[0-9a-fA-F]{64}$' -or
        $actualHash -cne $expectedHash -or $actualPid -ne $expectedPid -or $actualStart -ne $expectedStart -or
        -not [string]::Equals([IO.Path]::GetFullPath($actualPath), [IO.Path]::GetFullPath($expectedPath), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Process core identity differs in PID, exact start ticks, path, or SHA-256.'
    }
    return [pscustomobject]@{
        processId = $actualPid; processName = [string](Get-TouchLifecycleProperty $Actual 'processName')
        executablePath = [IO.Path]::GetFullPath($actualPath); startTicks = $actualStart; exeSha256 = $actualHash.ToLowerInvariant()
    }
}

function Find-TouchLifecycleProbeWindow {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Windows, [Parameter(Mandatory)]$LaunchIdentity, [Parameter(Mandatory)]$OriginalIdentity)
    $exact = @($Windows | Where-Object { [string](Get-TouchLifecycleProperty $_ 'title') -ceq 'MarkdownRenderer touch lifecycle probe' })
    if ($exact.Count -gt 1) { throw "Found multiple exact-title touch lifecycle probe windows ($($exact.Count))." }
    if ($exact.Count -eq 0) { return $null }
    $window = $exact[0]
    $probeProcessId = 0; $hwnd = 0L
    if (-not [int]::TryParse([string](Get-TouchLifecycleProperty $window 'processId'), [ref]$probeProcessId) -or $probeProcessId -le 0 -or
        -not [long]::TryParse([string](Get-TouchLifecycleProperty $window 'hwnd'), [ref]$hwnd) -or $hwnd -le 0 -or
        $probeProcessId -eq [int](Get-TouchLifecycleProperty $OriginalIdentity 'processId') -or
        $probeProcessId -ne [int](Get-TouchLifecycleProperty $LaunchIdentity 'processId') -or
        [string](Get-TouchLifecycleProperty $window 'processName') -cne 'MarkdownRenderer.Sample') {
        throw 'Exact-title probe window is not owned by the unique newly launched sample process.'
    }
    return [pscustomobject]@{ processId = $probeProcessId; processName = 'MarkdownRenderer.Sample'; hwnd = $hwnd; title = 'MarkdownRenderer touch lifecycle probe' }
}

function Get-TouchLifecycleCasePlan {
    $items = [Collections.Generic.List[object]]::new()
    foreach ($case in @(
        [pscustomobject]@{ id = 'CaptureLoss'; label = 'Capture loss'; value = 'captureLoss' },
        [pscustomobject]@{ id = 'UnloadReuse'; label = 'Unload and reuse'; value = 'unload' },
        [pscustomobject]@{ id = 'Dispose'; label = 'Dispose'; value = 'dispose' }
    )) {
        foreach ($viewport in @('owned', 'ancestor')) {
            $items.Add([pscustomobject]@{ id = "P1-TouchLifecycle-$($case.id)-$viewport"; label = $case.label; lifecycleCase = $case.value; viewport = $viewport })
        }
    }
    return @($items.ToArray())
}

function ConvertTo-TouchLifecycleRangeKey {
    param([Parameter(Mandatory)]$Range)
    if ($null -eq $Range) { throw 'Probe report omitted an exact selection range.' }
    return ConvertTo-Json -InputObject $Range -Depth 8 -Compress
}

function Assert-TouchLifecycleCleanControl {
    param([Parameter(Mandatory)]$Control, [switch]$RequireLoaded, [switch]$RequireUnloaded, [switch]$RequireDisposed, [switch]$RequireHiddenHandles)
    foreach ($name in @('isLoaded','isUnloaded','isDisposed','isHandleDragging','handleFrameClockSubscribed','pointerRetryTimerExists','isContextMenuOpen')) {
        if ((Get-TouchLifecycleProperty $Control $name) -isnot [bool]) { throw "Touch lifecycle control state omitted typed '$name'." }
    }
    if ([uint64](Get-TouchLifecycleProperty $Control 'contactPointerId') -ne 0 -or
        [uint64](Get-TouchLifecycleProperty $Control 'handlePointerId') -ne 0 -or
        [bool](Get-TouchLifecycleProperty $Control 'isHandleDragging') -or
        [bool](Get-TouchLifecycleProperty $Control 'handleFrameClockSubscribed') -or
        [int](Get-TouchLifecycleProperty $Control 'queuedPointerInputCount') -ne 0 -or
        [int](Get-TouchLifecycleProperty $Control 'deferredPointerCaptureCount') -ne 0 -or
        [bool](Get-TouchLifecycleProperty $Control 'pointerRetryTimerExists') -or
        [bool](Get-TouchLifecycleProperty $Control 'isContextMenuOpen')) {
        throw 'Touch lifecycle snapshot still has contact, capture, frame, queue, retry, or menu state.'
    }
    if ($RequireLoaded -and (-not [bool](Get-TouchLifecycleProperty $Control 'isLoaded') -or [bool](Get-TouchLifecycleProperty $Control 'isUnloaded') -or [bool](Get-TouchLifecycleProperty $Control 'isDisposed'))) {
        throw 'Touch lifecycle snapshot does not prove the renderer is loaded and live.'
    }
    if ($RequireUnloaded -and (-not [bool](Get-TouchLifecycleProperty $Control 'isUnloaded') -or [bool](Get-TouchLifecycleProperty $Control 'isLoaded'))) {
        throw 'Touch lifecycle snapshot does not prove the renderer is unloaded.'
    }
    if ($RequireDisposed -and -not [bool](Get-TouchLifecycleProperty $Control 'isDisposed')) { throw 'Touch lifecycle snapshot does not prove disposal completed.' }
    if ($RequireHiddenHandles -and ([bool](Get-TouchLifecycleProperty $Control 'isStartHandleVisible') -or [bool](Get-TouchLifecycleProperty $Control 'isEndHandleVisible'))) {
        throw 'Unloaded/disposed renderer retained a visible selection handle.'
    }
}

function Assert-TouchLifecycleSnapshotHasNoNativeCaptures {
    param([Parameter(Mandatory)]$Snapshot)
    foreach ($name in @('startHandle','endHandle')) {
        $handle = Get-TouchLifecycleProperty $Snapshot $name
        if ($null -eq $handle -or (Get-TouchLifecycleProperty $handle 'found') -isnot [bool] -or
            -not [bool](Get-TouchLifecycleProperty $handle 'found') -or
            (Get-TouchLifecycleProperty $handle 'pointerCaptureListAvailable') -isnot [bool] -or
            -not [bool](Get-TouchLifecycleProperty $handle 'pointerCaptureListAvailable')) {
            throw "Touch lifecycle snapshot lacks retained native Button capture evidence for '$name'."
        }
        if (@(Get-TouchLifecycleProperty $handle 'pointerIds').Count -ne 0) { throw "Retained native Button '$name' still owns a pointer capture." }
    }
}

function Get-TouchLifecycleEventMatches {
    param([Parameter(Mandatory)][object[]]$Events, [Parameter(Mandatory)][string]$Kind, [long]$AfterSequence = 0, [Nullable[long]]$PointerId = $null)
    return @($Events | Where-Object {
        [string](Get-TouchLifecycleProperty $_ 'kind') -ceq $Kind -and
        [long](Get-TouchLifecycleProperty $_ 'sequence') -gt $AfterSequence -and
        ($null -eq $PointerId -or [long](Get-TouchLifecycleProperty $_ 'pointerId') -eq [long]$PointerId)
    })
}

function Assert-TouchLifecycleReport {
    param([Parameter(Mandatory)]$Report, [Parameter(Mandatory)][ValidateSet('captureLoss','unload','dispose')][string]$ExpectedCase, [Parameter(Mandatory)][ValidateSet('owned','ancestor')][string]$ExpectedViewport)
    if ([int](Get-TouchLifecycleProperty $Report 'version') -ne 2 -or
        [string](Get-TouchLifecycleProperty $Report 'phase') -cne 'complete' -or
        [string](Get-TouchLifecycleProperty $Report 'lifecycleCase') -cne $ExpectedCase -or
        [string](Get-TouchLifecycleProperty $Report 'viewport') -cne $ExpectedViewport -or
        (Get-TouchLifecycleProperty $Report 'eventTraceTruncated') -isnot [bool] -or
        [bool](Get-TouchLifecycleProperty $Report 'eventTraceTruncated') -or
        -not (Test-TouchLifecycleProperty $Report 'failure') -or
        -not [string]::IsNullOrEmpty([string](Get-TouchLifecycleProperty $Report 'failure'))) {
        throw 'Touch lifecycle report is not a complete, untruncated, exact case/viewport receipt.'
    }
    if ([int](Get-TouchLifecycleProperty $Report 'run') -le 0) { throw 'Touch lifecycle report has no positive probe run number.' }
    foreach ($name in @('freshContextMenuObserved','freshMenuDismissed')) {
        $value = Get-TouchLifecycleProperty $Report $name
        if ($value -isnot [bool]) { throw "Touch lifecycle report omitted typed '$name' menu evidence." }
    }
    foreach ($name in @('freshContextMenuOpenSequence','freshMenuDismissedSequence')) {
        $value = Get-TouchLifecycleProperty $Report $name
        if ($null -eq $value -or [long]$value -lt 0) { throw "Touch lifecycle report omitted a valid '$name'." }
    }
    $events = @(Get-TouchLifecycleProperty $Report 'events')
    if ($events.Count -lt 7) { throw 'Touch lifecycle report has too few routed events for a real long-press/handle/lifecycle sequence.' }
    $longId = [long](Get-TouchLifecycleProperty $Report 'longPressPointerId')
    $longEpoch = [long](Get-TouchLifecycleProperty $Report 'longPressGestureEpoch')
    $handleId = [long](Get-TouchLifecycleProperty $Report 'handlePointerId')
    $handleEpoch = [long](Get-TouchLifecycleProperty $Report 'handleGestureEpoch')
    if ($longId -le 0 -or $longEpoch -le 0 -or $handleId -le 0 -or $handleEpoch -le 0) { throw 'Touch lifecycle report omitted positive OS pointer IDs or gesture epochs.' }
    $baseline = Get-TouchLifecycleProperty $Report 'baseline'
    $longSelection = Get-TouchLifecycleProperty $Report 'longPressSelection'
    $beforeAction = Get-TouchLifecycleProperty $Report 'beforeAction'
    $afterTerminal = Get-TouchLifecycleProperty $Report 'afterTerminal'
    if ($null -eq $baseline -or $null -eq $longSelection -or $null -eq $beforeAction -or $null -eq $afterTerminal) {
        throw 'Touch lifecycle report omitted a required baseline, long-press, pre-action, or terminal snapshot.'
    }
    $baselineControl = Get-TouchLifecycleProperty $baseline 'control'
    $longControl = Get-TouchLifecycleProperty $longSelection 'control'
    $beforeControl = Get-TouchLifecycleProperty $beforeAction 'control'
    $terminalControl = Get-TouchLifecycleProperty $afterTerminal 'control'
    Assert-TouchLifecycleCleanControl -Control $baselineControl -RequireLoaded
    if ([bool](Get-TouchLifecycleProperty $baselineControl 'selectionIsActive')) { throw 'Probe baseline unexpectedly began with an active selection.' }
    if ([uint64](Get-TouchLifecycleProperty $longControl 'contactPointerId') -ne [uint64]$longId -or
        [long](Get-TouchLifecycleProperty $longControl 'contactGestureEpoch') -ne $longEpoch -or
        [string](Get-TouchLifecycleProperty $longControl 'touchState') -cne 'Selected' -or
        -not [bool](Get-TouchLifecycleProperty $longControl 'selectionIsActive') -or
        (ConvertTo-TouchLifecycleRangeKey (Get-TouchLifecycleProperty $longControl 'selectionRange')) -ceq (ConvertTo-TouchLifecycleRangeKey (Get-TouchLifecycleProperty $baselineControl 'selectionRange'))) {
        throw 'Long-press snapshot does not prove a same-pointer/same-epoch real changed word selection.'
    }
    $holding = @(Get-TouchLifecycleEventMatches -Events $events -Kind 'HoldingStarted' -PointerId $longId)
    $longPress = @(Get-TouchLifecycleEventMatches -Events $events -Kind 'PointerPressed' -PointerId $longId)
    $longRelease = @($events | Where-Object {
        [long](Get-TouchLifecycleProperty $_ 'sequence') -gt 0 -and
        [long](Get-TouchLifecycleProperty $_ 'pointerId') -eq $longId -and
        [string](Get-TouchLifecycleProperty $_ 'kind') -ceq 'PointerReleased' -and
        [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'Touch' -and
        (Get-TouchLifecycleProperty $_ 'isInContact') -is [bool] -and
        -not [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and
        [long](Get-TouchLifecycleProperty $_ 'correlatedGestureEpoch') -eq $longEpoch
    })
    $validHolding = @($holding | Where-Object { [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'Touch' -and [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and [bool](Get-TouchLifecycleProperty $_ 'internalStateAvailable') -and [long](Get-TouchLifecycleProperty $_ 'observedGestureEpoch') -eq $longEpoch -and [long](Get-TouchLifecycleProperty $_ 'correlatedGestureEpoch') -eq $longEpoch })
    $validLongPress = @($longPress | Where-Object { [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'Touch' -and [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and [bool](Get-TouchLifecycleProperty $_ 'internalStateAvailable') -and [string](Get-TouchLifecycleProperty $_ 'sourceAutomationId') -notin @('MarkdownSelectionStartHandle','MarkdownSelectionEndHandle') })
    if ($validHolding.Count -ne 1 -or $validLongPress.Count -lt 1 -or $longRelease.Count -ne 1) { throw 'Long-press lacks actual routed touch press/holding/release evidence on the same gesture.' }
    $longPressSequence = [long](Get-TouchLifecycleProperty @($validLongPress | Sort-Object { [long](Get-TouchLifecycleProperty $_ 'sequence') })[0] 'sequence')
    $holdingSequence = [long](Get-TouchLifecycleProperty $validHolding[0] 'sequence')
    $longReleaseSequence = [long](Get-TouchLifecycleProperty $longRelease[0] 'sequence')
    if ($longPressSequence -ge $holdingSequence -or $holdingSequence -ge $longReleaseSequence) { throw 'Long-press press, holding, and no-contact terminal evidence is out of order.' }

    $captureTarget = [string](Get-TouchLifecycleProperty $beforeControl 'handleCaptureTargetAutomationId')
    if ($captureTarget -notin @('MarkdownSelectionStartHandle','MarkdownSelectionEndHandle') -or
        [uint64](Get-TouchLifecycleProperty $beforeControl 'handlePointerId') -ne [uint64]$handleId -or
        [uint64](Get-TouchLifecycleProperty $beforeControl 'contactPointerId') -ne [uint64]$handleId -or
        [long](Get-TouchLifecycleProperty $beforeControl 'handleGestureEpoch') -ne $handleEpoch -or
        [long](Get-TouchLifecycleProperty $beforeControl 'contactGestureEpoch') -ne $handleEpoch -or
        -not [bool](Get-TouchLifecycleProperty $beforeControl 'isHandleDragging')) {
        throw 'Pre-action snapshot does not prove active same-epoch touch ownership and handle drag.'
    }
    $capturedButton = Get-TouchLifecycleProperty $beforeAction $(if ($captureTarget -ceq 'MarkdownSelectionStartHandle') { 'startHandle' } else { 'endHandle' })
    $buttonPointers = @((Get-TouchLifecycleProperty $capturedButton 'pointerIds') | ForEach-Object { [uint64]$_ })
    if ($null -eq $capturedButton -or -not [bool](Get-TouchLifecycleProperty $capturedButton 'found') -or
        -not [bool](Get-TouchLifecycleProperty $capturedButton 'pointerCaptureListAvailable') -or $buttonPointers -notcontains [uint64]$handleId) {
        throw 'The exact retained native selection Button did not report capture of the routed touch pointer.'
    }
    $handlePress = @(Get-TouchLifecycleEventMatches -Events $events -Kind 'PointerPressed' -PointerId $handleId | Where-Object { [string](Get-TouchLifecycleProperty $_ 'sourceAutomationId') -ceq $captureTarget -and [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'Touch' -and [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and [bool](Get-TouchLifecycleProperty $_ 'internalStateAvailable') -and [long](Get-TouchLifecycleProperty $_ 'observedGestureEpoch') -eq $handleEpoch -and [long](Get-TouchLifecycleProperty $_ 'correlatedGestureEpoch') -eq $handleEpoch })
    $handleMove = @(Get-TouchLifecycleEventMatches -Events $events -Kind 'PointerMoved' -PointerId $handleId | Where-Object { [string](Get-TouchLifecycleProperty $_ 'sourceAutomationId') -ceq $captureTarget -and [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'Touch' -and [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and [bool](Get-TouchLifecycleProperty $_ 'internalStateAvailable') -and [long](Get-TouchLifecycleProperty $_ 'observedGestureEpoch') -eq $handleEpoch -and [long](Get-TouchLifecycleProperty $_ 'correlatedGestureEpoch') -eq $handleEpoch })
    if ($handlePress.Count -lt 1 -or $handleMove.Count -lt 1) { throw 'Handle lifecycle action lacks actual routed in-contact press and move on the captured Button.' }
    $actionName = 'Action:' + $ExpectedCase
    $action = @(Get-TouchLifecycleEventMatches -Events $events -Kind $actionName -PointerId $handleId | Where-Object { [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'ProbeAction' -and (Get-TouchLifecycleProperty $_ 'isInContact') -is [bool] -and [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and [bool](Get-TouchLifecycleProperty $_ 'internalStateAvailable') -and [long](Get-TouchLifecycleProperty $_ 'observedGestureEpoch') -eq $handleEpoch -and [long](Get-TouchLifecycleProperty $_ 'correlatedGestureEpoch') -eq $handleEpoch })
    if ($action.Count -ne 1) { throw 'Lifecycle action is not uniquely recorded while the exact handle contact remains in contact.' }
    $actionSequence = [long](Get-TouchLifecycleProperty $action[0] 'sequence')
    $handlePressSequence = [long](Get-TouchLifecycleProperty @($handlePress | Sort-Object { [long](Get-TouchLifecycleProperty $_ 'sequence') })[0] 'sequence')
    $handleMoveSequence = [long](Get-TouchLifecycleProperty @($handleMove | Sort-Object { [long](Get-TouchLifecycleProperty $_ 'sequence') })[0] 'sequence')
    if ($longReleaseSequence -ge $handlePressSequence -or $handlePressSequence -ge $handleMoveSequence -or $handleMoveSequence -ge $actionSequence) {
        throw 'Lifecycle action preceded the actual captured-button press/move evidence.'
    }
    Assert-TouchLifecycleCleanControl -Control $terminalControl
    Assert-TouchLifecycleSnapshotHasNoNativeCaptures -Snapshot $afterTerminal
    $terminalEvents = @($events | Where-Object {
        [long](Get-TouchLifecycleProperty $_ 'sequence') -gt $actionSequence -and
        [long](Get-TouchLifecycleProperty $_ 'pointerId') -eq $handleId -and
        [string](Get-TouchLifecycleProperty $_ 'kind') -in @('PointerReleased','PointerCanceled') -and
        [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'Touch' -and
        (Get-TouchLifecycleProperty $_ 'isInContact') -is [bool] -and
        -not [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and
        [long](Get-TouchLifecycleProperty $_ 'correlatedGestureEpoch') -eq $handleEpoch
    })
    if ($terminalEvents.Count -ne 1) { throw 'Lifecycle action lacks one actual same-pointer/same-epoch no-contact terminal event.' }

    switch ($ExpectedCase) {
        'captureLoss' {
            $afterAction = Get-TouchLifecycleProperty $Report 'afterAction'
            if ($null -eq $afterAction) { throw 'Capture-loss report omitted its post-capture-loss snapshot.' }
            $captureEvents = @(Get-TouchLifecycleEventMatches -Events $events -Kind 'PointerCaptureLost' -AfterSequence $actionSequence -PointerId $handleId | Where-Object { [string](Get-TouchLifecycleProperty $_ 'sourceAutomationId') -ceq $captureTarget -and [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'Touch' -and (Get-TouchLifecycleProperty $_ 'isInContact') -is [bool] -and [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and [bool](Get-TouchLifecycleProperty $_ 'internalStateAvailable') -and [long](Get-TouchLifecycleProperty $_ 'observedGestureEpoch') -eq $handleEpoch -and [long](Get-TouchLifecycleProperty $_ 'correlatedGestureEpoch') -eq $handleEpoch })
            if ($captureEvents.Count -ne 1) { throw 'Capture-loss case lacks exact captured-Button PointerCaptureLost evidence while contact remains down.' }
            $afterActionControl = Get-TouchLifecycleProperty $afterAction 'control'
            Assert-TouchLifecycleCleanControl -Control $afterActionControl -RequireLoaded
            if (-not [bool](Get-TouchLifecycleProperty $afterActionControl 'selectionIsActive') -or
                (ConvertTo-TouchLifecycleRangeKey (Get-TouchLifecycleProperty $afterActionControl 'selectionRange')) -cne (ConvertTo-TouchLifecycleRangeKey (Get-TouchLifecycleProperty $beforeControl 'selectionRange'))) {
                throw 'Capture loss did not preserve the committed pre-action selection.'
            }
            Assert-TouchLifecycleSnapshotHasNoNativeCaptures -Snapshot $afterAction
            if (-not [bool](Get-TouchLifecycleProperty $terminalControl 'selectionIsActive') -or
                (ConvertTo-TouchLifecycleRangeKey (Get-TouchLifecycleProperty $terminalControl 'selectionRange')) -cne (ConvertTo-TouchLifecycleRangeKey (Get-TouchLifecycleProperty $beforeControl 'selectionRange'))) {
                throw 'Capture-loss terminal cleanup did not retain the committed selection.'
            }
        }
        'unload' {
            $afterUnloaded = Get-TouchLifecycleProperty $Report 'afterUnloaded'
            $afterReattach = Get-TouchLifecycleProperty $Report 'afterReattach'
            $freshSelection = Get-TouchLifecycleProperty $Report 'freshSelection'
            $afterFreshTerminal = Get-TouchLifecycleProperty $Report 'afterFreshTerminal'
            foreach ($entry in @($afterUnloaded,$afterReattach,$freshSelection,$afterFreshTerminal)) { if ($null -eq $entry) { throw 'Unload/reuse report omitted a required unload, reattach, or fresh-selection snapshot.' } }
            $unloadEvent = @(Get-TouchLifecycleEventMatches -Events $events -Kind 'Unloaded' -AfterSequence $actionSequence | Where-Object { [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'None' -and [bool](Get-TouchLifecycleProperty $_ 'internalStateAvailable') -and [string](Get-TouchLifecycleProperty $_ 'observer') -ceq 'Renderer' -and [string](Get-TouchLifecycleProperty $_ 'sourceAutomationId') -ceq 'MarkdownRenderer' })
            if ($unloadEvent.Count -ne 1) { throw 'Unload/reuse report lacks one Unloaded lifecycle event after the action.' }
            $unloadedControl = Get-TouchLifecycleProperty $afterUnloaded 'control'
            Assert-TouchLifecycleCleanControl -Control $unloadedControl -RequireUnloaded -RequireHiddenHandles
            Assert-TouchLifecycleSnapshotHasNoNativeCaptures -Snapshot $afterUnloaded
            Assert-TouchLifecycleSnapshotHasNoNativeCaptures -Snapshot $afterTerminal
            $loadedSequence = [long](Get-TouchLifecycleProperty $unloadEvent[0] 'sequence')
            $loadedEvents = @(Get-TouchLifecycleEventMatches -Events $events -Kind 'Loaded' -AfterSequence $loadedSequence | Where-Object { [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'None' -and [bool](Get-TouchLifecycleProperty $_ 'internalStateAvailable') -and [string](Get-TouchLifecycleProperty $_ 'observer') -ceq 'Renderer' -and [string](Get-TouchLifecycleProperty $_ 'sourceAutomationId') -ceq 'MarkdownRenderer' })
            if ($loadedEvents.Count -ne 1) { throw 'Unload/reuse report lacks one later Loaded event after reattachment.' }
            $reattachControl = Get-TouchLifecycleProperty $afterReattach 'control'
            Assert-TouchLifecycleCleanControl -Control $reattachControl -RequireLoaded
            $freshControl = Get-TouchLifecycleProperty $freshSelection 'control'
            $freshId = [long](Get-TouchLifecycleProperty $Report 'freshPointerId')
            $freshEpoch = [long](Get-TouchLifecycleProperty $Report 'freshGestureEpoch')
            if ($freshId -le 0 -or $freshEpoch -le 0 -or $freshEpoch -eq $handleEpoch -or
                [uint64](Get-TouchLifecycleProperty $freshControl 'contactPointerId') -ne [uint64]$freshId -or
                [long](Get-TouchLifecycleProperty $freshControl 'contactGestureEpoch') -ne $freshEpoch -or
                [string](Get-TouchLifecycleProperty $freshControl 'touchState') -cne 'Selected' -or
                -not [bool](Get-TouchLifecycleProperty $freshControl 'selectionIsActive') -or
                (ConvertTo-TouchLifecycleRangeKey (Get-TouchLifecycleProperty $freshControl 'selectionRange')) -ceq (ConvertTo-TouchLifecycleRangeKey (Get-TouchLifecycleProperty $beforeControl 'selectionRange'))) {
                throw 'Fresh post-unload selection does not prove a new epoch and different selected range.'
            }
            $freshHolding = @(Get-TouchLifecycleEventMatches -Events $events -Kind 'HoldingStarted' -PointerId $freshId | Where-Object { [long](Get-TouchLifecycleProperty $_ 'observedGestureEpoch') -eq $freshEpoch -and [long](Get-TouchLifecycleProperty $_ 'correlatedGestureEpoch') -eq $freshEpoch -and [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and [bool](Get-TouchLifecycleProperty $_ 'internalStateAvailable') -and [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'Touch' })
            $freshPress = @(Get-TouchLifecycleEventMatches -Events $events -Kind 'PointerPressed' -PointerId $freshId | Where-Object { [long](Get-TouchLifecycleProperty $_ 'sequence') -gt [long](Get-TouchLifecycleProperty $loadedEvents[0] 'sequence') -and [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'Touch' })
            $freshTerminals = @($events | Where-Object { [long](Get-TouchLifecycleProperty $_ 'pointerId') -eq $freshId -and [string](Get-TouchLifecycleProperty $_ 'kind') -ceq 'PointerReleased' -and [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'Touch' -and (Get-TouchLifecycleProperty $_ 'isInContact') -is [bool] -and -not [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and [long](Get-TouchLifecycleProperty $_ 'correlatedGestureEpoch') -eq $freshEpoch })
            if ($freshHolding.Count -ne 1 -or $freshPress.Count -lt 1 -or $freshTerminals.Count -ne 1) { throw 'Fresh post-unload word lacks actual same-epoch press/hold/release evidence.' }
            $freshPressSequence = [long](Get-TouchLifecycleProperty @($freshPress | Sort-Object { [long](Get-TouchLifecycleProperty $_ 'sequence') })[0] 'sequence')
            $freshHoldingSequence = [long](Get-TouchLifecycleProperty $freshHolding[0] 'sequence')
            $freshTerminalSequence = [long](Get-TouchLifecycleProperty $freshTerminals[0] 'sequence')
            if ($freshPressSequence -ge $freshHoldingSequence -or $freshHoldingSequence -ge $freshTerminalSequence) { throw 'Fresh post-unload press, holding, and no-contact release evidence is out of order.' }
            $menuOpenSequence = [long](Get-TouchLifecycleProperty $Report 'freshContextMenuOpenSequence')
            $menuDismissedSequence = [long](Get-TouchLifecycleProperty $Report 'freshMenuDismissedSequence')
            $menuOpenSnapshot = Get-TouchLifecycleProperty $Report 'freshContextMenuOpen'
            $menuOpenEvents = @(Get-TouchLifecycleEventMatches -Events $events -Kind 'FreshContextMenuObserved' -AfterSequence $freshTerminalSequence -PointerId $freshId | Where-Object { [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'ProbeState' -and (Get-TouchLifecycleProperty $_ 'isInContact') -is [bool] -and -not [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and [bool](Get-TouchLifecycleProperty $_ 'internalStateAvailable') -and [long](Get-TouchLifecycleProperty $_ 'correlatedGestureEpoch') -eq $freshEpoch })
            $menuDismissedEvents = @(Get-TouchLifecycleEventMatches -Events $events -Kind 'FreshMenuDismissed' -AfterSequence $menuOpenSequence -PointerId $freshId | Where-Object { [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'ProbeState' -and (Get-TouchLifecycleProperty $_ 'isInContact') -is [bool] -and -not [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and [bool](Get-TouchLifecycleProperty $_ 'internalStateAvailable') -and [long](Get-TouchLifecycleProperty $_ 'correlatedGestureEpoch') -eq $freshEpoch })
            if (-not [bool](Get-TouchLifecycleProperty $Report 'freshContextMenuObserved') -or
                -not [bool](Get-TouchLifecycleProperty $Report 'freshMenuDismissed') -or
                $menuOpenEvents.Count -ne 1 -or $menuDismissedEvents.Count -ne 1 -or
                $menuOpenSequence -le $freshTerminalSequence -or $menuDismissedSequence -le $menuOpenSequence -or
                [long](Get-TouchLifecycleProperty $menuOpenEvents[0] 'sequence') -ne $menuOpenSequence -or
                [long](Get-TouchLifecycleProperty $menuDismissedEvents[0] 'sequence') -ne $menuDismissedSequence -or
                $null -eq $menuOpenSnapshot -or
                -not [bool](Get-TouchLifecycleProperty (Get-TouchLifecycleProperty $menuOpenSnapshot 'control') 'isContextMenuOpen')) {
                throw 'Fresh selection lacks ordered native context-menu-open and post-dismissal observations.'
            }
            $freshTerminalControl = Get-TouchLifecycleProperty $afterFreshTerminal 'control'
            Assert-TouchLifecycleCleanControl -Control $freshTerminalControl -RequireLoaded
            if (-not [bool](Get-TouchLifecycleProperty $freshTerminalControl 'selectionIsActive') -or
                (ConvertTo-TouchLifecycleRangeKey (Get-TouchLifecycleProperty $freshTerminalControl 'selectionRange')) -cne (ConvertTo-TouchLifecycleRangeKey (Get-TouchLifecycleProperty $freshControl 'selectionRange'))) {
                throw 'Fresh selection did not remain committed after its same-epoch terminal.'
            }
            Assert-TouchLifecycleSnapshotHasNoNativeCaptures -Snapshot $afterFreshTerminal
        }
        'dispose' {
            $afterAction = Get-TouchLifecycleProperty $Report 'afterAction'
            if ($null -eq $afterAction) { throw 'Dispose report omitted the post-disposal snapshot.' }
            $disposedEvents = @(Get-TouchLifecycleEventMatches -Events $events -Kind 'DisposalCompleted' -AfterSequence $actionSequence | Where-Object { [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'None' -and [bool](Get-TouchLifecycleProperty $_ 'internalStateAvailable') -and [string](Get-TouchLifecycleProperty $_ 'observer') -ceq 'Renderer' -and [string](Get-TouchLifecycleProperty $_ 'sourceAutomationId') -ceq 'MarkdownRenderer' -and [long](Get-TouchLifecycleProperty $_ 'observedGestureEpoch') -eq $handleEpoch -and [long](Get-TouchLifecycleProperty $_ 'correlatedGestureEpoch') -eq $handleEpoch })
            if ($disposedEvents.Count -ne 1) { throw 'Dispose report lacks one DisposalCompleted event after the action.' }
            Assert-TouchLifecycleCleanControl -Control (Get-TouchLifecycleProperty $afterAction 'control') -RequireDisposed -RequireHiddenHandles
            Assert-TouchLifecycleCleanControl -Control $terminalControl -RequireDisposed -RequireHiddenHandles
            Assert-TouchLifecycleSnapshotHasNoNativeCaptures -Snapshot $afterAction
            Assert-TouchLifecycleSnapshotHasNoNativeCaptures -Snapshot $afterTerminal
        }
    }
    if ($ExpectedCase -ne 'unload' -and
        ([bool](Get-TouchLifecycleProperty $Report 'freshContextMenuObserved') -or
         [bool](Get-TouchLifecycleProperty $Report 'freshMenuDismissed') -or
         [long](Get-TouchLifecycleProperty $Report 'freshContextMenuOpenSequence') -ne 0 -or
         [long](Get-TouchLifecycleProperty $Report 'freshMenuDismissedSequence') -ne 0 -or
         $null -ne (Get-TouchLifecycleProperty $Report 'freshContextMenuOpen'))) {
        throw 'A non-unload case unexpectedly contains post-reattachment context-menu evidence.'
    }
    return [pscustomobject]@{ status = 'PASS'; lifecycleCase = $ExpectedCase; viewport = $ExpectedViewport; run = [int](Get-TouchLifecycleProperty $Report 'run'); eventCount = $events.Count; handlePointerId = $handleId; handleGestureEpoch = $handleEpoch }
}

function Test-TouchLifecycleSafeTerminal {
    param([AllowNull()]$Report, [Parameter(Mandatory)][string]$ExpectedCase, [Parameter(Mandatory)][string]$ExpectedViewport)
    try {
        if ($null -eq $Report -or [string](Get-TouchLifecycleProperty $Report 'phase') -cne 'complete' -or
            [string](Get-TouchLifecycleProperty $Report 'lifecycleCase') -cne $ExpectedCase -or
            [string](Get-TouchLifecycleProperty $Report 'viewport') -cne $ExpectedViewport -or
            [bool](Get-TouchLifecycleProperty $Report 'eventTraceTruncated')) { return $false }
        $terminal = Get-TouchLifecycleProperty $Report 'afterTerminal'
        if ($null -eq $terminal) { return $false }
        $control = Get-TouchLifecycleProperty $terminal 'control'
        Assert-TouchLifecycleCleanControl -Control $control
        Assert-TouchLifecycleSnapshotHasNoNativeCaptures -Snapshot $terminal
        if ($ExpectedCase -eq 'unload') {
            if (-not [bool](Get-TouchLifecycleProperty $Report 'freshContextMenuObserved') -or
                -not [bool](Get-TouchLifecycleProperty $Report 'freshMenuDismissed') -or
                [long](Get-TouchLifecycleProperty $Report 'freshMenuDismissedSequence') -le [long](Get-TouchLifecycleProperty $Report 'freshContextMenuOpenSequence')) { return $false }
            $fresh = Get-TouchLifecycleProperty $Report 'afterFreshTerminal'
            if ($null -eq $fresh) { return $false }
            Assert-TouchLifecycleCleanControl -Control (Get-TouchLifecycleProperty $fresh 'control') -RequireLoaded
            Assert-TouchLifecycleSnapshotHasNoNativeCaptures -Snapshot $fresh
        }
        elseif ($ExpectedCase -eq 'dispose') {
            if (-not [bool](Get-TouchLifecycleProperty $control 'isDisposed')) { return $false }
        }
        $handleId = [long](Get-TouchLifecycleProperty $Report 'handlePointerId')
        $handleEpoch = [long](Get-TouchLifecycleProperty $Report 'handleGestureEpoch')
        $action = @(Get-TouchLifecycleEventMatches -Events @(Get-TouchLifecycleProperty $Report 'events') -Kind ('Action:' + $ExpectedCase) -PointerId $handleId)
        if ($action.Count -ne 1) { return $false }
        $actionSequence = [long](Get-TouchLifecycleProperty $action[0] 'sequence')
        $terminalEvents = @((Get-TouchLifecycleProperty $Report 'events') | Where-Object { [long](Get-TouchLifecycleProperty $_ 'sequence') -gt $actionSequence -and [long](Get-TouchLifecycleProperty $_ 'pointerId') -eq $handleId -and [string](Get-TouchLifecycleProperty $_ 'kind') -in @('PointerReleased','PointerCanceled') -and [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'Touch' -and (Get-TouchLifecycleProperty $_ 'isInContact') -is [bool] -and -not [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and [long](Get-TouchLifecycleProperty $_ 'correlatedGestureEpoch') -eq $handleEpoch })
        return $terminalEvents.Count -eq 1
    }
    catch { return $false }
}

function Test-TouchLifecycleSafeIdle {
    param([AllowNull()]$Report, [Parameter(Mandatory)][string]$ExpectedCase, [Parameter(Mandatory)][string]$ExpectedViewport)
    try {
        if ($null -eq $Report -or [string](Get-TouchLifecycleProperty $Report 'lifecycleCase') -notin @($ExpectedCase, '') -or
            [string](Get-TouchLifecycleProperty $Report 'viewport') -cne $ExpectedViewport -or
            [string](Get-TouchLifecycleProperty $Report 'phase') -notin @('idle','awaitLongPress') -or
            [bool](Get-TouchLifecycleProperty $Report 'eventTraceTruncated') -or
            -not [string]::IsNullOrEmpty([string](Get-TouchLifecycleProperty $Report 'failure'))) { return $false }
        $baseline = Get-TouchLifecycleProperty $Report 'baseline'
        if ($null -eq $baseline) { return $false }
        Assert-TouchLifecycleCleanControl -Control (Get-TouchLifecycleProperty $baseline 'control') -RequireLoaded
        if ([bool](Get-TouchLifecycleProperty (Get-TouchLifecycleProperty $baseline 'control') 'selectionIsActive')) { return $false }
        $events = @(Get-TouchLifecycleProperty $Report 'events')
        if (@($events | Where-Object { [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'Touch' }).Count -ne 0) { return $false }
        return $true
    }
    catch { return $false }
}

function Get-TouchLifecycleNormalCloseSafety {
    param([Parameter(Mandatory)][bool]$TouchCommandIssued, [AllowNull()]$Report, [Parameter(Mandatory)][string]$ExpectedCase, [Parameter(Mandatory)][string]$ExpectedViewport)
    if (-not $TouchCommandIssued) {
        if (Test-TouchLifecycleSafeIdle -Report $Report -ExpectedCase $ExpectedCase -ExpectedViewport $ExpectedViewport) {
            return [pscustomobject]@{ safe = $true; reason = 'No touch command ran and the exact probe reports a clean idle/armed baseline.' }
        }
        return [pscustomobject]@{ safe = $false; reason = 'No complete clean idle receipt proves that closing the child cannot interrupt contact or capture.' }
    }
    if (Test-TouchLifecycleSafeTerminal -Report $Report -ExpectedCase $ExpectedCase -ExpectedViewport $ExpectedViewport) {
        return [pscustomobject]@{ safe = $true; reason = 'Exact same-epoch no-contact terminal and empty retained native capture lists are present.' }
    }
    return [pscustomobject]@{ safe = $false; reason = 'The actual touch lifecycle terminal and native capture cleanup are not both proven; withholding close input.' }
}

function Get-TouchLifecycleCounts {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Results, [AllowNull()]$SetupFailure)
    $counts = [ordered]@{ pass = 0; fail = 0; unverified = 0 }
    foreach ($item in $Results) {
        switch ([string](Get-TouchLifecycleProperty $item 'status')) {
            'PASS' { $counts.pass++ }
            'FAIL' { $counts.fail++ }
            'UNVERIFIED' { $counts.unverified++ }
            default { throw 'Touch lifecycle case has an unknown status.' }
        }
    }
    if ($null -ne $SetupFailure) { $counts.unverified++ }
    return [pscustomobject]$counts
}

function ConvertTo-TouchLifecyclePsLiteral {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

function ConvertTo-TouchLifecycleCommandArgument {
    param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $builder = [Text.StringBuilder]::new(); $null = $builder.Append('"'); $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') { $null = $builder.Append(('\' * (($slashes * 2) + 1))).Append('"'); $slashes = 0; continue }
        if ($slashes -gt 0) { $null = $builder.Append(('\' * $slashes)); $slashes = 0 }
        $null = $builder.Append($character)
    }
    if ($slashes -gt 0) { $null = $builder.Append(('\' * ($slashes * 2))) }
    $null = $builder.Append('"')
    return $builder.ToString()
}

function Invoke-TouchLifecycleRedirectedProcess {
    param([Parameter(Mandatory)][string]$FileName, [Parameter(Mandatory)][string[]]$Arguments)
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FileName; $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    $info.StandardOutputEncoding = $encoding; $info.StandardErrorEncoding = $encoding
    $argumentList = $info.GetType().GetProperty('ArgumentList')
    if ($null -ne $argumentList) {
        foreach ($argument in $Arguments) { $null = $argumentList.GetValue($info, $null).Add([string]$argument) }
    }
    else { $info.Arguments = (@($Arguments | ForEach-Object { ConvertTo-TouchLifecycleCommandArgument ([string]$_) }) -join ' ') }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw "Could not start '$FileName'." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync(); $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{ exitCode = $process.ExitCode; stdout = $stdoutTask.GetAwaiter().GetResult(); stderr = $stderrTask.GetAwaiter().GetResult() }
    }
    finally { $process.Dispose() }
}

function Write-TouchLifecycleJsonExclusive {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)
    $json = (ConvertTo-Json -InputObject $Value -Depth 48) + [Environment]::NewLine
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

function Save-TouchLifecycleCommand {
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][int]$ExitCode, [Parameter(Mandatory)][string]$Output, [Parameter(Mandatory)][string]$CaseDirectory)
    $script:CommandIndex++
    $label = ($Arguments -join ' ') -replace '[^A-Za-z0-9._-]+', '-'
    if ($label.Length -gt 80) { $label = $label.Substring(0, 80) }
    $commandsDirectory = Join-Path $CaseDirectory 'commands'
    [IO.Directory]::CreateDirectory($commandsDirectory) | Out-Null
    $path = Join-Path $commandsDirectory ('{0:D4}-{1}.json' -f $script:CommandIndex, $label)
    Write-TouchLifecycleJsonExclusive -Path $path -Value ([pscustomobject]@{ arguments = $Arguments; exitCode = $ExitCode; output = $Output })
    return $path
}

function Invoke-TouchLifecycleNative {
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$EvidenceDirectory, [switch]$Json, [switch]$AllowNonZero)
    $argv = @($Arguments)
    if ($Json -and $argv -notcontains '--json' -and $argv -notcontains '--') { $argv += '--json' }
    $command = Get-Command -Name 'winapp' -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $fileName = if (-not [string]::IsNullOrWhiteSpace([string]$command.Source)) { [string]$command.Source } else { [string]$command.Path }
    if ([string]::IsNullOrWhiteSpace($fileName)) { throw 'Could not resolve the winapp executable path.' }
    $process = Invoke-TouchLifecycleRedirectedProcess -FileName $fileName -Arguments $argv
    $output = [string]$process.stdout
    if (-not [string]::IsNullOrEmpty([string]$process.stderr)) { $output += [Environment]::NewLine + '[stderr]' + [Environment]::NewLine + [string]$process.stderr }
    $evidence = Save-TouchLifecycleCommand -Arguments $argv -ExitCode ([int]$process.exitCode) -Output $output -CaseDirectory $EvidenceDirectory
    if ([int]$process.exitCode -ne 0 -and -not $AllowNonZero) { throw "winapp exited $($process.exitCode) (evidence $evidence): $output" }
    if (-not $Json) { return [pscustomobject]@{ exitCode = [int]$process.exitCode; output = $output; evidence = $evidence } }
    $match = [regex]::Match([string]$process.stdout, '(?s)(\{|\[)')
    if (-not $match.Success) { throw "winapp returned no JSON (evidence $evidence): $output" }
    try { $parsed = ConvertFrom-TouchLifecycleNativeJson -JsonText ([string]$process.stdout).Substring($match.Index) }
    catch { throw "winapp returned malformed JSON (evidence $evidence): $_`n$output" }
    $value = $parsed.value
    return [pscustomobject]@{ exitCode = [int]$process.exitCode; value = $value; stdout = [string]$process.stdout; stderr = [string]$process.stderr; output = $output; evidence = $evidence }
}

function Get-TouchLifecycleWindows {
    param([Parameter(Mandatory)][string]$EvidenceDirectory)
    $native = Invoke-TouchLifecycleNative -Arguments @('ui','list-windows','--on','sandbox') -EvidenceDirectory $EvidenceDirectory -Json
    $valueProperty = $native.PSObject.Properties['value']
    if ($null -eq $valueProperty -or $null -eq $valueProperty.Value -or
        $valueProperty.Value -is [string] -or $valueProperty.Value -isnot [System.Collections.IEnumerable]) {
        throw 'winapp list-windows did not return a non-null JSON window array.'
    }
    $windows = [Collections.Generic.List[object]]::new()
    foreach ($window in $valueProperty.Value) {
        if ($null -eq $window) { throw 'winapp list-windows returned a null window entry.' }
        $windows.Add($window)
    }
    return $windows.ToArray()
}

function ConvertFrom-TouchLifecycleGuestObject {
    param([Parameter(Mandatory)]$NativeResult)
    $value = Get-TouchLifecycleProperty $NativeResult 'value'
    if ($null -eq $value) { throw 'Sandbox target exec returned no guest JSON value.' }
    if ($value -is [string]) { return ConvertFrom-Json -InputObject $value -ErrorAction Stop }
    foreach ($name in @('stdout','output','result')) {
        $text = Get-TouchLifecycleProperty $value $name
        if ($text -is [string] -and -not [string]::IsNullOrWhiteSpace($text)) {
            $match = [regex]::Match($text.Trim(), '(?s)(\{.*\})\s*$')
            if (-not $match.Success) { throw "Sandbox guest '$name' did not contain a JSON object." }
            return ConvertFrom-Json -InputObject $match.Groups[1].Value -ErrorAction Stop
        }
    }
    if ($null -ne (Get-TouchLifecycleProperty $value 'mode') -or $null -ne (Get-TouchLifecycleProperty $value 'status')) { return $value }
    # WinApp CLI 0.7 `target exec --json` forwards the guest command's own JSON
    # object directly (T1-006); with no envelope field it is that object.
    if ($value -is [System.Management.Automation.PSCustomObject]) { return $value }
    throw 'Sandbox target exec result does not expose one unambiguous guest JSON object.'
}

function Invoke-TouchLifecycleGuestCode {
    param([Parameter(Mandatory)][string]$Code, [Parameter(Mandatory)][string]$EvidenceDirectory)
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Code))
    $native = Invoke-TouchLifecycleNative -Arguments @('target','exec','sandbox','--json','--','powershell.exe','-WindowStyle','Hidden','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-STA','-EncodedCommand',$encoded) -EvidenceDirectory $EvidenceDirectory -Json -AllowNonZero
    $guest = ConvertFrom-TouchLifecycleGuestObject -NativeResult $native
    return [pscustomobject]@{ exitCode = [int]$native.exitCode; value = $guest; native = $native }
}

function Invoke-TouchLifecycleGuestProbe {
    param([Parameter(Mandatory)][ValidateSet('Identity','PayloadHashes','Range','State')][string]$Mode, [Parameter(Mandatory)]$Identity, [Parameter(Mandatory)][string]$EvidenceDirectory, [string]$Needle = '', [string]$Viewport = 'owned')
    $args = @('target','exec','sandbox','--json','--','powershell.exe','-WindowStyle','Hidden','-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-STA','-File',$script:GuestProbePath,
        '-Mode',$Mode,'-ProcessId',[string]$Identity.processId,'-WindowHandle',[string]$Identity.hwnd,
        '-ExpectedExeSha256',[string]$Identity.exeSha256,'-ExpectedGuestComputerName',$script:GuestComputerName,
        '-ExpectedHostComputerName',$script:HostComputerName,'-ExpectedStartTicks',[string]$Identity.startTicks)
    if ($Mode -eq 'Range') { $args += @('-Needle',$Needle) }
    if ($Mode -eq 'State') { $args += @('-ViewportMode',$Viewport) }
    $native = Invoke-TouchLifecycleNative -Arguments $args -EvidenceDirectory $EvidenceDirectory -Json -AllowNonZero
    if ([int]$native.exitCode -ne 0) { throw "Guest '$Mode' probe failed: $($native.output)" }
    $value = ConvertFrom-TouchLifecycleGuestObject -NativeResult $native
    if ($Mode -eq 'Identity') {
        $reportedPath = [string](Get-TouchLifecycleProperty $value 'exePath')
        if ([string]::IsNullOrWhiteSpace($reportedPath)) { throw 'Guest identity omitted its executable path.' }
        if ($null -eq (Get-TouchLifecycleProperty $value 'executablePath')) {
            $value | Add-Member -NotePropertyName executablePath -NotePropertyValue $reportedPath
        }
        # The raw Identity mode reports exePath only (T1-006); the process
        # name is the image name of that exact, verified path.
        if ($null -eq (Get-TouchLifecycleProperty $value 'processName')) {
            $value | Add-Member -NotePropertyName processName -NotePropertyValue ([IO.Path]::GetFileNameWithoutExtension($reportedPath))
        }
    }
    return $value
}

function Assert-TouchLifecycleWindowSet {
    param([Parameter(Mandatory)]$Windows, [Parameter(Mandatory)]$OriginalIdentity, [AllowNull()]$ProbeIdentity)
    $windowsArray = @($Windows)
    $originalMatches = @($windowsArray | Where-Object { [int](Get-TouchLifecycleProperty $_ 'processId') -eq [int]$OriginalIdentity.processId -and [long](Get-TouchLifecycleProperty $_ 'hwnd') -eq [long]$OriginalIdentity.hwnd -and [string](Get-TouchLifecycleProperty $_ 'processName') -ceq [string]$OriginalIdentity.processName -and [string](Get-TouchLifecycleProperty $_ 'title') -ceq [string]$OriginalIdentity.title })
    if ($originalMatches.Count -ne 1) { throw 'The exact original sample window disappeared or changed identity.' }
    if ($null -ne $ProbeIdentity) {
        $probeMatches = @($windowsArray | Where-Object { [int](Get-TouchLifecycleProperty $_ 'processId') -eq [int]$ProbeIdentity.processId -and [long](Get-TouchLifecycleProperty $_ 'hwnd') -eq [long]$ProbeIdentity.hwnd -and [string](Get-TouchLifecycleProperty $_ 'processName') -ceq [string]$ProbeIdentity.processName -and [string](Get-TouchLifecycleProperty $_ 'title') -ceq [string]$ProbeIdentity.title })
        if ($probeMatches.Count -ne 1) { throw 'The exact child probe PID/HWND/title is not uniquely present.' }
        $otherProbeTitles = @($windowsArray | Where-Object { [string](Get-TouchLifecycleProperty $_ 'title') -ceq 'MarkdownRenderer touch lifecycle probe' -and [int](Get-TouchLifecycleProperty $_ 'processId') -ne [int]$ProbeIdentity.processId })
        if ($otherProbeTitles.Count -ne 0) { throw 'Another exact-title touch lifecycle window exists; refusing ambiguous actions.' }
    }
    return $true
}

function Assert-TouchLifecycleEpoch {
    param([Parameter(Mandatory)][string]$EvidenceDirectory)
    $snapshot = (Invoke-TouchLifecycleNative -Arguments @('target','snapshot','sandbox') -EvidenceDirectory $EvidenceDirectory -Json).value
    return Assert-TouchLifecycleSnapshot -Snapshot $snapshot -ExpectedEpoch $script:Prior.targetEpoch
}

function Assert-TouchLifecycleOriginalIdentity {
    param([Parameter(Mandatory)][string]$EvidenceDirectory)
    $windows = @(Get-TouchLifecycleWindows -EvidenceDirectory $EvidenceDirectory)
    $null = Assert-TouchLifecycleWindowSet -Windows $windows -OriginalIdentity $script:OriginalIdentity -ProbeIdentity $null
    $actual = Invoke-TouchLifecycleGuestProbe -Mode Identity -Identity $script:OriginalIdentity -EvidenceDirectory $EvidenceDirectory
    $null = Assert-TouchLifecycleProcessIdentity -Actual $actual -Expected $script:OriginalIdentity
    return $actual
}

function Assert-TouchLifecycleActionTarget {
    param([Parameter(Mandatory)][string]$EvidenceDirectory)
    $null = Assert-TouchLifecycleEpoch -EvidenceDirectory $EvidenceDirectory
    $windows = @(Get-TouchLifecycleWindows -EvidenceDirectory $EvidenceDirectory)
    $null = Assert-TouchLifecycleWindowSet -Windows $windows -OriginalIdentity $script:OriginalIdentity -ProbeIdentity $script:ProbeIdentity
    $original = Invoke-TouchLifecycleGuestProbe -Mode Identity -Identity $script:OriginalIdentity -EvidenceDirectory $EvidenceDirectory
    $null = Assert-TouchLifecycleProcessIdentity -Actual $original -Expected $script:OriginalIdentity
    $probe = Invoke-TouchLifecycleGuestProbe -Mode Identity -Identity $script:ProbeIdentity -EvidenceDirectory $EvidenceDirectory
    $null = Assert-TouchLifecycleProcessIdentity -Actual $probe -Expected $script:ProbeIdentity
    return [pscustomobject]@{ epoch = $script:Prior.targetEpoch; original = $original; probe = $probe; windowCount = $windows.Count }
}

function Invoke-TouchLifecycleUi {
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$EvidenceDirectory, [switch]$Json)
    $null = Assert-TouchLifecycleActionTarget -EvidenceDirectory $EvidenceDirectory
    $argv = @('ui') + $Arguments + @('--on','sandbox','-a',[string]$script:ProbeIdentity.processId)
    $result = Invoke-TouchLifecycleNative -Arguments $argv -EvidenceDirectory $EvidenceDirectory -Json:$Json
    if ([int]$result.exitCode -ne 0) { throw "Guarded Sandbox UI action failed: $($result.output)" }
    $null = Assert-TouchLifecycleActionTarget -EvidenceDirectory $EvidenceDirectory
    return $result
}

function Get-TouchLifecycleUiText {
    # T1-022: winapp get-value reports text at the top level ('text'); older
    # shapes nest it under 'value'. Accept either, never an empty string.
    param([AllowNull()]$Result)
    $outer = Get-TouchLifecycleProperty $Result 'value'
    foreach ($source in @($outer, (Get-TouchLifecycleProperty $outer 'value'))) {
        if ($source -is [string]) { if (-not [string]::IsNullOrWhiteSpace($source)) { return $source }; continue }
        if ($null -eq $source) { continue }
        foreach ($name in @('text','value','name')) {
            $candidate = Get-TouchLifecycleProperty $source $name
            if ($candidate -is [string] -and -not [string]::IsNullOrWhiteSpace($candidate)) { return $candidate }
        }
    }
    return $null
}

function Get-TouchLifecycleReceipt {
    param([Parameter(Mandatory)][string]$EvidenceDirectory)
    $result = Invoke-TouchLifecycleUi -Arguments @('get-value','TouchLifecycleReceipt') -EvidenceDirectory $EvidenceDirectory -Json
    $text = Get-TouchLifecycleUiText $result
    if ([string]::IsNullOrWhiteSpace([string]$text)) { throw 'UIA get-value returned no TouchLifecycleReceipt text.' }
    $report = ConvertFrom-Json -InputObject $text -ErrorAction Stop
    $script:ReceiptIndex++
    $receiptDirectory = Join-Path $EvidenceDirectory 'receipts'
    [IO.Directory]::CreateDirectory($receiptDirectory) | Out-Null
    $receiptPath = Join-Path $receiptDirectory ('receipt-{0:D4}.json' -f $script:ReceiptIndex)
    [IO.File]::WriteAllText($receiptPath, ([string]$text + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
    $script:LastReceipt = $report
    $script:LastReceiptPath = $receiptPath
    return $report
}

function Wait-TouchLifecycleReport {
    param([Parameter(Mandatory)][string]$EvidenceDirectory, [Parameter(Mandatory)][string[]]$AcceptPhase,
        [string[]]$RejectPhase = @(), [int]$TimeoutSeconds = 15)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        $report = Get-TouchLifecycleReceipt -EvidenceDirectory $EvidenceDirectory
        $phase = [string](Get-TouchLifecycleProperty $report 'phase')
        if ($phase -ceq 'failed') { throw "Probe failed: $([string](Get-TouchLifecycleProperty $report 'failure'))" }
        if ($phase -in $RejectPhase) { throw "Probe passed the required phase without host coordination (now '$phase')." }
        if ($phase -in $AcceptPhase) { return $report }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Probe did not reach expected phase(s) '$($AcceptPhase -join ', ')' within $TimeoutSeconds seconds."
}

function Assert-TouchLifecycleFreshMenuReadyForDismissal {
    param([Parameter(Mandatory)]$Report)
    if ([int](Get-TouchLifecycleProperty $Report 'version') -ne 2 -or
        [string](Get-TouchLifecycleProperty $Report 'phase') -cne 'awaitFreshMenuDismissal' -or
        -not [bool](Get-TouchLifecycleProperty $Report 'freshContextMenuObserved') -or
        (Get-TouchLifecycleProperty $Report 'freshMenuDismissed') -isnot [bool] -or
        [bool](Get-TouchLifecycleProperty $Report 'freshMenuDismissed')) {
        throw 'The exact probe is not stably waiting for host dismissal of an observed fresh context menu.'
    }
    $snapshot = Get-TouchLifecycleProperty $Report 'freshContextMenuOpen'
    $control = if ($null -eq $snapshot) { $null } else { Get-TouchLifecycleProperty $snapshot 'control' }
    if ($null -eq $control -or
        (Get-TouchLifecycleProperty $control 'isContextMenuOpen') -isnot [bool] -or
        -not [bool](Get-TouchLifecycleProperty $control 'isContextMenuOpen') -or
        [uint64](Get-TouchLifecycleProperty $control 'contactPointerId') -ne 0 -or
        -not [bool](Get-TouchLifecycleProperty $control 'selectionIsActive')) {
        throw 'The retained menu-open snapshot does not prove an open menu after the fresh committed contact.'
    }
    $freshId = [long](Get-TouchLifecycleProperty $Report 'freshPointerId')
    $freshEpoch = [long](Get-TouchLifecycleProperty $Report 'freshGestureEpoch')
    $events = @(Get-TouchLifecycleProperty $Report 'events')
    $freshTerminal = @($events | Where-Object {
        [long](Get-TouchLifecycleProperty $_ 'pointerId') -eq $freshId -and
        [string](Get-TouchLifecycleProperty $_ 'kind') -ceq 'PointerReleased' -and
        [string](Get-TouchLifecycleProperty $_ 'device') -ceq 'Touch' -and
        (Get-TouchLifecycleProperty $_ 'isInContact') -is [bool] -and
        -not [bool](Get-TouchLifecycleProperty $_ 'isInContact') -and
        [long](Get-TouchLifecycleProperty $_ 'correlatedGestureEpoch') -eq $freshEpoch
    })
    $openSequence = [long](Get-TouchLifecycleProperty $Report 'freshContextMenuOpenSequence')
    $openEvents = @(Get-TouchLifecycleEventMatches -Events $events -Kind 'FreshContextMenuObserved' -PointerId $freshId)
    if ($freshId -le 0 -or $freshEpoch -le 0 -or $freshTerminal.Count -ne 1 -or
        $openEvents.Count -ne 1 -or $openSequence -le [long](Get-TouchLifecycleProperty $freshTerminal[0] 'sequence') -or
        [long](Get-TouchLifecycleProperty $openEvents[0] 'sequence') -ne $openSequence -or
        [string](Get-TouchLifecycleProperty $openEvents[0] 'device') -cne 'ProbeState' -or
        (Get-TouchLifecycleProperty $openEvents[0] 'isInContact') -isnot [bool] -or
        [bool](Get-TouchLifecycleProperty $openEvents[0] 'isInContact') -or
        [long](Get-TouchLifecycleProperty $openEvents[0] 'correlatedGestureEpoch') -ne $freshEpoch) {
        throw 'The report does not prove a fresh same-epoch release before the observed open menu.'
    }
    return $Report
}

function Get-TouchLifecycleGuestComputerName {
    param([Parameter(Mandatory)][string]$EvidenceDirectory)
    $code = "`$ErrorActionPreference='Stop'; [Console]::Out.WriteLine((ConvertTo-Json -InputObject ([pscustomobject]@{computerName=[Environment]::MachineName}) -Compress))"
    $result = Invoke-TouchLifecycleGuestCode -Code $code -EvidenceDirectory $EvidenceDirectory
    $name = [string](Get-TouchLifecycleProperty $result.value 'computerName')
    if ($result.exitCode -ne 0 -or [string]::IsNullOrWhiteSpace($name) -or [string]::Equals($name,$script:HostComputerName,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Could not establish a distinct Sandbox guest computer name.'
    }
    return $name
}

function Stage-TouchLifecycleGuestProbe {
    param([Parameter(Mandatory)]$Snapshot, [Parameter(Mandatory)][string]$EvidenceDirectory)
    $workRoot = [string](Get-TouchLifecycleProperty $Snapshot 'workRoot')
    if (-not [IO.Path]::IsPathRooted($workRoot)) { throw 'Sandbox snapshot has no rooted workRoot; refusing to guess a guest staging path.' }
    $relativeDirectory = "correctness\$script:RunId\touch-lifecycle"
    $guestDirectory = Join-Path $workRoot $relativeDirectory
    $mkdirCode = "`$ErrorActionPreference='Stop'; if ([Environment]::MachineName -cne $(ConvertTo-TouchLifecyclePsLiteral $script:GuestComputerName)) { throw 'Guest identity changed.' }; `$d=$(ConvertTo-TouchLifecyclePsLiteral $guestDirectory); if (Test-Path -LiteralPath `$d) { throw 'Touch-lifecycle staging path already exists.' }; [IO.Directory]::CreateDirectory(`$d) | Out-Null; [Console]::Out.WriteLine((ConvertTo-Json -InputObject ([pscustomobject]@{directory=`$d;computerName=[Environment]::MachineName}) -Compress))"
    $created = Invoke-TouchLifecycleGuestCode -Code $mkdirCode -EvidenceDirectory $EvidenceDirectory
    if ($created.exitCode -ne 0 -or [string](Get-TouchLifecycleProperty $created.value 'directory') -cne $guestDirectory -or [string](Get-TouchLifecycleProperty $created.value 'computerName') -cne $script:GuestComputerName) {
        throw 'Could not create a unique run-scoped Sandbox helper directory.'
    }
    $probePath = [IO.Path]::GetFullPath($script:SharedProbePath)
    $expectedHash = (Get-FileHash -LiteralPath $probePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $relativeFile = "$relativeDirectory\MarkdownCorrectnessSandboxProbe.ps1"
    $null = Assert-TouchLifecycleEpoch -EvidenceDirectory $EvidenceDirectory
    $null = Invoke-TouchLifecycleNative -Arguments @('target','push','sandbox',$probePath,$relativeFile) -EvidenceDirectory $EvidenceDirectory
    $null = Assert-TouchLifecycleEpoch -EvidenceDirectory $EvidenceDirectory
    $guestProbePath = Join-Path $guestDirectory 'MarkdownCorrectnessSandboxProbe.ps1'
    $verifyCode = "`$ErrorActionPreference='Stop'; if ([Environment]::MachineName -cne $(ConvertTo-TouchLifecyclePsLiteral $script:GuestComputerName)) { throw 'Guest identity changed.' }; `$p=$(ConvertTo-TouchLifecyclePsLiteral $guestProbePath); `$h=(Get-FileHash -LiteralPath `$p -Algorithm SHA256).Hash.ToLowerInvariant(); [Console]::Out.WriteLine((ConvertTo-Json -InputObject ([pscustomobject]@{computerName=[Environment]::MachineName;path=`$p;sha256=`$h}) -Compress))"
    $verified = Invoke-TouchLifecycleGuestCode -Code $verifyCode -EvidenceDirectory $EvidenceDirectory
    if ($verified.exitCode -ne 0 -or [string](Get-TouchLifecycleProperty $verified.value 'computerName') -cne $script:GuestComputerName -or
        [string](Get-TouchLifecycleProperty $verified.value 'path') -cne $guestProbePath -or
        [string](Get-TouchLifecycleProperty $verified.value 'sha256') -cne $expectedHash) {
        throw 'Staged shared Sandbox identity/UIA probe hash did not match the exact host script.'
    }
    $script:GuestProbePath = $guestProbePath
    return [pscustomobject]@{ guestDirectory = $guestDirectory; guestProbePath = $guestProbePath; sha256 = $expectedHash; length = (Get-Item -LiteralPath $probePath).Length }
}

function New-TouchLifecycleGuestLauncherCode {
    param([Parameter(Mandatory)]$OriginalIdentity)
    $exe = ConvertTo-TouchLifecyclePsLiteral ([string]$script:Prior.guestExecutablePath)
    $guest = ConvertTo-TouchLifecyclePsLiteral $script:GuestComputerName
    $hostLiteral = ConvertTo-TouchLifecyclePsLiteral $script:HostComputerName
    $expectedPid = [int]$OriginalIdentity.processId; $expectedStart = [long]$OriginalIdentity.startTicks
    $expectedHash = ConvertTo-TouchLifecyclePsLiteral ([string]$OriginalIdentity.exeSha256)
    return @"
`$ErrorActionPreference='Stop'
`$exe=$exe; `$guest=$guest; `$expectedHostName=$hostLiteral; `$expectedPid=$expectedPid; `$expectedStart=$expectedStart; `$expectedHash=$expectedHash
function Get-ProcessReceipt([int]`$Id) {
    `$p=Get-Process -Id `$Id -ErrorAction Stop; `$p.Refresh(); `$path=[IO.Path]::GetFullPath([string]`$p.Path)
    `$started=`$p.StartTime.ToUniversalTime(); `$hash=(Get-FileHash -LiteralPath `$path -Algorithm SHA256).Hash.ToLowerInvariant()
    return [pscustomobject]@{processId=`$p.Id;processName=`$p.ProcessName;executablePath=`$path;startTicks=`$started.Ticks;startTimeUtc=`$started.ToString('o');exeSha256=`$hash}
}
if ([Environment]::MachineName -cne `$guest -or [string]::IsNullOrWhiteSpace(`$expectedHostName) -or [string]::Equals(`$guest,`$expectedHostName,[StringComparison]::OrdinalIgnoreCase)) { throw 'Launcher did not run in the exact distinct Sandbox guest.' }
`$sourceBefore=Get-ProcessReceipt `$expectedPid
if (`$sourceBefore.processName -cne 'MarkdownRenderer.Sample' -or -not [string]::Equals([IO.Path]::GetFullPath(`$sourceBefore.executablePath),[IO.Path]::GetFullPath(`$exe),[StringComparison]::OrdinalIgnoreCase) -or `$sourceBefore.startTicks -ne `$expectedStart -or `$sourceBefore.exeSha256 -cne `$expectedHash) { throw 'Original sample identity changed before isolated launch.' }
`$fxr=@((Get-Process -Id `$expectedPid -ErrorAction Stop).Modules | Where-Object { `$_.ModuleName -ieq 'hostfxr.dll' } | ForEach-Object { [string]`$_.FileName })
if (`$fxr.Count -ne 1) { throw 'The original sample must load exactly one hostfxr.dll; its runtime cannot be reused for the probe.' }
`$runtimeRoot=[IO.Path]::GetFullPath((Join-Path (Split-Path -Parent `$fxr[0]) '..\..\..'))
# T1-012: the framework-dependent sample runs on winapp's private runtime, which a direct launch does not inherit.
`$env:DOTNET_ROOT=`$runtimeRoot; `$env:DOTNET_ROOT_X64=`$runtimeRoot
# T1-017: winapp target exec closes its job when this launcher returns, terminating Start-Process descendants.
# Win32_Process.Create starts the probe outside that job; its environment block replaces the inherited one, so pass the full current block.
`$envBlock=[string[]]@(Get-ChildItem Env: | ForEach-Object { '{0}={1}' -f `$_.Name, `$_.Value })
`$startup=New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow=[uint16]1; EnvironmentVariables=`$envBlock }
`$created=Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine=('"' + `$exe + '" --renderer-touch-lifecycle-probe'); CurrentDirectory=(Split-Path -Parent `$exe); ProcessStartupInformation=`$startup }
if ([int]`$created.ReturnValue -ne 0 -or [int]`$created.ProcessId -le 0) { throw "Win32_Process.Create failed with return value `$(`$created.ReturnValue)." }
`$childReceipt=Get-ProcessReceipt ([int]`$created.ProcessId)
if (`$childReceipt.processName -cne 'MarkdownRenderer.Sample' -or -not [string]::Equals([IO.Path]::GetFullPath(`$childReceipt.executablePath),[IO.Path]::GetFullPath(`$exe),[StringComparison]::OrdinalIgnoreCase) -or `$childReceipt.exeSha256 -cne `$expectedHash -or `$childReceipt.processId -eq `$expectedPid) { throw 'Child did not use the exact approved executable as a distinct process.' }
`$sourceAfter=Get-ProcessReceipt `$expectedPid
if (`$sourceAfter.processId -ne `$sourceBefore.processId -or `$sourceAfter.startTicks -ne `$sourceBefore.startTicks -or -not [string]::Equals([IO.Path]::GetFullPath(`$sourceAfter.executablePath),[IO.Path]::GetFullPath(`$sourceBefore.executablePath),[StringComparison]::OrdinalIgnoreCase) -or `$sourceAfter.exeSha256 -cne `$sourceBefore.exeSha256) { throw 'Original sample identity changed during isolated launch.' }
`$receipt=[pscustomobject]@{schemaVersion=1;status='LAUNCHED';launchMethod='guest-Win32_Process-Create';argument='--renderer-touch-lifecycle-probe';guestComputerName=`$guest;hostComputerName=`$expectedHostName;sourceBefore=`$sourceBefore;sourceAfter=`$sourceAfter;process=`$childReceipt}
[Console]::Out.WriteLine((ConvertTo-Json -InputObject `$receipt -Depth 8 -Compress))
"@
}

function Assert-TouchLifecycleLaunchReceipt {
    param([Parameter(Mandatory)]$Receipt, [Parameter(Mandatory)]$OriginalIdentity)
    if ([int](Get-TouchLifecycleProperty $Receipt 'schemaVersion') -ne 1 -or
        [string](Get-TouchLifecycleProperty $Receipt 'status') -cne 'LAUNCHED' -or
        [string](Get-TouchLifecycleProperty $Receipt 'launchMethod') -cne 'guest-Win32_Process-Create' -or
        [string](Get-TouchLifecycleProperty $Receipt 'argument') -cne '--renderer-touch-lifecycle-probe' -or
        [string](Get-TouchLifecycleProperty $Receipt 'guestComputerName') -cne $script:GuestComputerName -or
        [string](Get-TouchLifecycleProperty $Receipt 'hostComputerName') -cne $script:HostComputerName) {
        throw 'Guest child-launch receipt does not attest the exact fixed probe argument, host, and guest.'
    }
    # The isolated guest launcher measures the source process tuple directly.
    # HWND/title are checked independently by the host's exact Sandbox window
    # listing and the full guest Identity probe before any UI action.
    $null = Assert-TouchLifecycleProcessCoreIdentity -Actual (Get-TouchLifecycleProperty $Receipt 'sourceBefore') -Expected $OriginalIdentity
    $null = Assert-TouchLifecycleProcessCoreIdentity -Actual (Get-TouchLifecycleProperty $Receipt 'sourceAfter') -Expected $OriginalIdentity
    $child = Get-TouchLifecycleProperty $Receipt 'process'
    if ([int](Get-TouchLifecycleProperty $child 'processId') -eq [int]$OriginalIdentity.processId -or
        [string](Get-TouchLifecycleProperty $child 'processName') -cne 'MarkdownRenderer.Sample') {
        throw 'Probe launch did not create a distinct MarkdownRenderer.Sample process.'
    }
    $expected = [pscustomobject]@{
        processId = [int](Get-TouchLifecycleProperty $child 'processId'); processName = 'MarkdownRenderer.Sample'
        executablePath = [string]$script:Prior.guestExecutablePath; startTicks = [long](Get-TouchLifecycleProperty $child 'startTicks')
        exeSha256 = [string]$script:Prior.exeSha256; hwnd = 1; title = 'MarkdownRenderer touch lifecycle probe'
    }
    if ([long](Get-TouchLifecycleProperty $child 'startTicks') -le 0 -or
        -not [string]::Equals([IO.Path]::GetFullPath([string](Get-TouchLifecycleProperty $child 'executablePath')), [IO.Path]::GetFullPath($expected.executablePath), [StringComparison]::OrdinalIgnoreCase) -or
        [string](Get-TouchLifecycleProperty $child 'exeSha256') -cne $expected.exeSha256) {
        throw 'Launch child PID/start/hash/path does not match the exact approved Sandbox executable.'
    }
    return [pscustomobject]@{ processId = $expected.processId; processName = $expected.processName; executablePath = $expected.executablePath; startTicks = $expected.startTicks; exeSha256 = $expected.exeSha256 }
}

function Wait-TouchLifecycleProbeWindow {
    param([Parameter(Mandatory)]$LaunchIdentity, [Parameter(Mandatory)][string]$EvidenceDirectory)
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $null = Assert-TouchLifecycleEpoch -EvidenceDirectory $EvidenceDirectory
        $windows = @(Get-TouchLifecycleWindows -EvidenceDirectory $EvidenceDirectory)
        $priorWindow = [pscustomobject]@{ processId = $script:OriginalIdentity.processId; processName = $script:OriginalIdentity.processName; hwnd = $script:OriginalIdentity.hwnd; title = $script:OriginalIdentity.title }
        $found = Find-TouchLifecycleProbeWindow -Windows $windows -LaunchIdentity $LaunchIdentity -OriginalIdentity $script:OriginalIdentity
        $null = Assert-TouchLifecycleWindowSet -Windows $windows -OriginalIdentity $script:OriginalIdentity -ProbeIdentity $null
        if ($null -ne $found) {
            return [pscustomobject]@{ processId = $found.processId; processName = $found.processName; hwnd = $found.hwnd; title = $found.title }
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'New exact-path child did not expose the unique lifecycle-probe window within 45 seconds.'
}

function Get-TouchLifecyclePayloadHashes {
    param([Parameter(Mandatory)]$Identity, [Parameter(Mandatory)][string]$EvidenceDirectory)
    $receipt = Invoke-TouchLifecycleGuestProbe -Mode PayloadHashes -Identity $Identity -EvidenceDirectory $EvidenceDirectory
    if ([string](Get-TouchLifecycleProperty $receipt 'mode') -cne 'PayloadHashes' -or [int](Get-TouchLifecycleProperty $receipt 'processId') -ne [int]$Identity.processId -or
        -not [string]::Equals([IO.Path]::GetFullPath([string](Get-TouchLifecycleProperty $receipt 'executablePath')), [IO.Path]::GetFullPath([string]$Identity.executablePath), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Guest payload report is not scoped to the exact child process and executable.'
    }
    $comparisons = [Collections.Generic.List[object]]::new()
    foreach ($expected in $script:Prior.payloads) {
        $matches = @((Get-TouchLifecycleProperty $receipt 'files') | Where-Object { [string](Get-TouchLifecycleProperty $_ 'name') -ceq [string]$expected.name })
        if ($matches.Count -ne 1 -or [string](Get-TouchLifecycleProperty $matches[0] 'sha256') -cne [string]$expected.sha256 -or
            [long](Get-TouchLifecycleProperty $matches[0] 'length') -ne [long]$expected.length -or
            -not [string]::Equals([IO.Path]::GetFullPath([string](Get-TouchLifecycleProperty $matches[0] 'path')), [IO.Path]::GetFullPath([string]$expected.guestPath), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Fresh child payload does not match the prior exact run receipt for '$($expected.name)'."
        }
        $comparisons.Add([pscustomobject]@{ name = $expected.name; length = $expected.length; sha256 = $expected.sha256; status = 'MATCH' })
    }
    return [pscustomobject]@{ status = 'MATCH'; processId = $Identity.processId; files = @($comparisons.ToArray()); raw = $receipt }
}

function Get-TouchLifecycleProcessState {
    param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][string]$EvidenceDirectory)
    $pidLiteral = [string]$ProcessId
    $code = @"
`$ErrorActionPreference='Stop'; `$id=$pidLiteral; `$p=Get-Process -Id `$id -ErrorAction SilentlyContinue
if (`$null -eq `$p) { `$r=[pscustomobject]@{mode='ProcessState';processId=`$id;exists=`$false} }
else { `$p.Refresh(); `$started=`$p.StartTime.ToUniversalTime(); `$r=[pscustomobject]@{mode='ProcessState';processId=`$p.Id;exists=`$true;processName=`$p.ProcessName;executablePath=[IO.Path]::GetFullPath([string]`$p.Path);startTicks=`$started.Ticks} }
[Console]::Out.WriteLine((ConvertTo-Json -InputObject `$r -Compress))
"@
    $result = Invoke-TouchLifecycleGuestCode -Code $code -EvidenceDirectory $EvidenceDirectory
    if ($result.exitCode -ne 0 -or [string](Get-TouchLifecycleProperty $result.value 'mode') -cne 'ProcessState' -or [int](Get-TouchLifecycleProperty $result.value 'processId') -ne $ProcessId) {
        throw 'Guest process-state query did not return the exact child PID.'
    }
    return $result.value
}

function Assert-TouchLifecycleProcessExited {
    param([Parameter(Mandatory)]$State, [Parameter(Mandatory)]$Identity)
    if ([string](Get-TouchLifecycleProperty $State 'mode') -cne 'ProcessState' -or [int](Get-TouchLifecycleProperty $State 'processId') -ne [int]$Identity.processId -or
        (Get-TouchLifecycleProperty $State 'exists') -isnot [bool]) { throw 'Guest process state is incomplete or scoped to a different child PID.' }
    if (-not [bool](Get-TouchLifecycleProperty $State 'exists')) { return $true }
    if ([string](Get-TouchLifecycleProperty $State 'processName') -cne [string]$Identity.processName -or
        [string](Get-TouchLifecycleProperty $State 'executablePath') -cne [string]$Identity.executablePath -or
        [long](Get-TouchLifecycleProperty $State 'startTicks') -eq [long]$Identity.startTicks) {
        throw 'The exact probe process is still alive, or the guest PID identity became ambiguous after close.'
    }
    return $true
}

function Close-TouchLifecycleProbeNormally {
    param([Parameter(Mandatory)]$Window, [Parameter(Mandatory)]$Identity, [Parameter(Mandatory)]$OriginalIdentity, [Parameter(Mandatory)]$Payload, [Parameter(Mandatory)][string]$EvidenceDirectory)
    $null = Assert-TouchLifecycleActionTarget -EvidenceDirectory $EvidenceDirectory
    $currentPayload = Get-TouchLifecyclePayloadHashes -Identity $Identity -EvidenceDirectory $EvidenceDirectory
    if ($currentPayload.status -cne 'MATCH') { throw 'Probe payload changed before close.' }
    # This is the one guarded UI action whose intended result is that its own
    # target disappears. Re-check all identities immediately before dispatch,
    # then verify exact window/process exit below instead of requiring the
    # closed window to remain present in the generic post-action guard.
    $null = Assert-TouchLifecycleActionTarget -EvidenceDirectory $EvidenceDirectory
    $close = Invoke-TouchLifecycleNative -Arguments @('ui','invoke','TouchLifecycleCloseProbe','--action','invoke','--on','sandbox','-a',[string]$Identity.processId) -EvidenceDirectory $EvidenceDirectory
    if ([int]$close.exitCode -ne 0) { throw "Guarded normal-close UI action failed: $($close.output)" }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $lastWindows = @(); $lastState = $null
    do {
        $null = Assert-TouchLifecycleEpoch -EvidenceDirectory $EvidenceDirectory
        $lastWindows = @(Get-TouchLifecycleWindows -EvidenceDirectory $EvidenceDirectory)
        $remaining = @($lastWindows | Where-Object { [int](Get-TouchLifecycleProperty $_ 'processId') -eq [int]$Window.processId -and [long](Get-TouchLifecycleProperty $_ 'hwnd') -eq [long]$Window.hwnd -and [string](Get-TouchLifecycleProperty $_ 'title') -ceq $Window.title })
        if ($remaining.Count -gt 0) { Start-Sleep -Milliseconds 200; continue }
        $lastState = Get-TouchLifecycleProcessState -ProcessId ([int]$Identity.processId) -EvidenceDirectory $EvidenceDirectory
        try { $null = Assert-TouchLifecycleProcessExited -State $lastState -Identity $Identity; break }
        catch { Start-Sleep -Milliseconds 200 }
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $lastState) { throw 'Normal close produced no exact guest process-state receipt.' }
    $null = Assert-TouchLifecycleProcessExited -State $lastState -Identity $Identity
    $probeWindows = @($lastWindows | Where-Object { [string](Get-TouchLifecycleProperty $_ 'title') -ceq 'MarkdownRenderer touch lifecycle probe' })
    if ($probeWindows.Count -gt 0) { throw 'A lifecycle probe window remains after normal close.' }
    $null = Assert-TouchLifecycleOriginalIdentity -EvidenceDirectory $EvidenceDirectory
    return [pscustomobject]@{ status = 'PASS'; method = 'UIA TouchLifecycleCloseProbe invoke'; exactWindowGone = $true; exactProcessExited = $true; processState = $lastState; originalAppStillExact = $true; payloadBeforeClose = $Payload }
}

function Get-TouchLifecycleRangePoint {
    param([Parameter(Mandatory)][string]$Needle, [Parameter(Mandatory)]$ProbeIdentity, [Parameter(Mandatory)][string]$Viewport, [Parameter(Mandatory)][string]$EvidenceDirectory)
    $null = Assert-TouchLifecycleActionTarget -EvidenceDirectory $EvidenceDirectory
    $range = Invoke-TouchLifecycleGuestProbe -Mode Range -Identity $ProbeIdentity -EvidenceDirectory $EvidenceDirectory -Needle $Needle -Viewport $Viewport
    $null = Assert-TouchLifecycleActionTarget -EvidenceDirectory $EvidenceDirectory
    $rect = Get-TouchLifecycleProperty $range 'rect'
    if ([string](Get-TouchLifecycleProperty $range 'text') -cne $Needle -or
        $null -eq $rect -or [double](Get-TouchLifecycleProperty $rect 'width') -le 2 -or [double](Get-TouchLifecycleProperty $rect 'height') -le 2) {
        throw "UIA could not resolve exact visible probe word '$Needle'."
    }
    return [pscustomobject]@{ x = [int][Math]::Round([double](Get-TouchLifecycleProperty $rect 'x') + [double](Get-TouchLifecycleProperty $rect 'width') / 2); y = [int][Math]::Round([double](Get-TouchLifecycleProperty $rect 'y') + [double](Get-TouchLifecycleProperty $rect 'height') / 2); text = $Needle; rect = $rect }
}

function Set-TouchLifecycleViewport {
    param([Parameter(Mandatory)][string]$Viewport, [Parameter(Mandatory)][string]$EvidenceDirectory)
    $expectedOn = $Viewport -ceq 'ancestor'
    $property = Invoke-TouchLifecycleUi -Arguments @('get-property','TouchLifecycleAncestorViewport','--property','ToggleState') -EvidenceDirectory $EvidenceDirectory -Json
    $props = Get-TouchLifecycleProperty $property.value 'properties'
    $state = [string](Get-TouchLifecycleProperty $props 'ToggleState')
    $isOn = $state -in @('On','1','true','True')
    if ($isOn -ne $expectedOn) {
        $action = if ($expectedOn) { 'toggle-on' } else { 'toggle-off' }
        $null = Invoke-TouchLifecycleUi -Arguments @('invoke','TouchLifecycleAncestorViewport','--action',$action) -EvidenceDirectory $EvidenceDirectory
    }
    $after = Invoke-TouchLifecycleUi -Arguments @('get-property','TouchLifecycleAncestorViewport','--property','ToggleState') -EvidenceDirectory $EvidenceDirectory -Json
    $afterProps = Get-TouchLifecycleProperty $after.value 'properties'
    $afterState = [string](Get-TouchLifecycleProperty $afterProps 'ToggleState')
    $afterOn = $afterState -in @('On','1','true','True')
    if ($afterOn -ne $expectedOn) { throw "Viewport toggle did not commit the requested '$Viewport' mode." }
}

function Set-TouchLifecycleCase {
    param([Parameter(Mandatory)]$Case, [Parameter(Mandatory)][string]$EvidenceDirectory)
    # T1-022: a non-editable WinUI ComboBox exposes no settable ValuePattern.
    # Select through UIA as a user would: expand it, then select the item.
    $null = Invoke-TouchLifecycleUi -Arguments @('invoke','TouchLifecycleCase','--action','expand') -EvidenceDirectory $EvidenceDirectory
    # The label also appears as the item's text child; select the one exact ListItem.
    $search = Invoke-TouchLifecycleUi -Arguments @('search',[string]$Case.label,'--type','ListItem') -EvidenceDirectory $EvidenceDirectory -Json
    # The open popup can be reached from both its own window and the owner's
    # tree, so one element may be reported twice under the same selector.
    $items = @(@(Get-TouchLifecycleProperty $search.value 'matches') |
        Where-Object { $null -ne $_ -and [string]$_.name -ceq [string]$Case.label -and [string]$_.type -ceq 'ListItem' } |
        Sort-Object -Property selector -Unique)
    if ($items.Count -ne 1) { throw "The expanded case picker exposed $($items.Count) exact '$($Case.label)' list items." }
    $null = Invoke-TouchLifecycleUi -Arguments @('invoke',[string]$items[0].selector,'--action','select') -EvidenceDirectory $EvidenceDirectory
    $result = Invoke-TouchLifecycleUi -Arguments @('get-value','TouchLifecycleCase') -EvidenceDirectory $EvidenceDirectory -Json
    $selected = Get-TouchLifecycleUiText $result
    if ([string]$selected -cne [string]$Case.label) { throw "Case picker did not select exactly '$($Case.label)'." }
}

function Invoke-TouchLifecycleGesture {
    param([Parameter(Mandatory)][string]$Target, [Parameter(Mandatory)][string]$Gesture, [Parameter(Mandatory)][string]$EvidenceDirectory, [string]$At = '', [string]$ToPoint = '', [int]$HoldMilliseconds = 0, [int]$DurationMilliseconds = 350)
    $arguments = @('touch',$Target,'--gesture',$Gesture)
    if (-not [string]::IsNullOrWhiteSpace($At)) { $arguments += @('--hold-ms',[string]$HoldMilliseconds,'--at',$At) }
    if (-not [string]::IsNullOrWhiteSpace($ToPoint)) { $arguments += @('--to-point',$ToPoint,'--duration-ms',[string]$DurationMilliseconds) }
    # Set before dispatch: a CLI timeout/error can occur after input reached
    # the guest, so the caller must treat any attempted touch as live contact
    # until a terminal receipt proves otherwise.
    $script:TouchCommandIssued = $true
    return Invoke-TouchLifecycleUi -Arguments $arguments -EvidenceDirectory $EvidenceDirectory
}

function Get-TouchLifecycleHandleNudge {
    param([Parameter(Mandatory)]$ProbeIdentity, [Parameter(Mandatory)][string]$Viewport, [Parameter(Mandatory)][string]$EvidenceDirectory)
    $null = Assert-TouchLifecycleActionTarget -EvidenceDirectory $EvidenceDirectory
    $state = Invoke-TouchLifecycleGuestProbe -Mode State -Identity $ProbeIdentity -EvidenceDirectory $EvidenceDirectory -Viewport $Viewport
    $null = Assert-TouchLifecycleActionTarget -EvidenceDirectory $EvidenceDirectory
    $handles = @(Get-TouchLifecycleProperty $state 'handles')
    $start = @($handles | Where-Object { [string](Get-TouchLifecycleProperty $_ 'automationId') -ceq 'MarkdownSelectionStartHandle' })
    if ($start.Count -ne 1 -or [bool](Get-TouchLifecycleProperty $start[0] 'offscreen')) { throw 'The start selection handle is not uniquely visible for the routed contact test.' }
    $x = [double](Get-TouchLifecycleProperty $start[0] 'x'); $y = [double](Get-TouchLifecycleProperty $start[0] 'y')
    $width = [double](Get-TouchLifecycleProperty $start[0] 'width'); $height = [double](Get-TouchLifecycleProperty $start[0] 'height')
    if ($width -le 4 -or $height -le 4 -or [double]::IsNaN($x) -or [double]::IsNaN($y) -or [double]::IsInfinity($x) -or [double]::IsInfinity($y)) {
        throw 'Start handle has invalid UIA geometry; refusing to guess a touch destination.'
    }
    # UIA swipe starts from the exact named handle and moves a small distance
    # inside its measured rectangle. This produces a routed move without
    # selecting a different word or crossing into an adjacent control.
    $toX = [int][Math]::Round($x + [Math]::Min($width * 0.65, ($width - 2)))
    $toY = [int][Math]::Round($y + [Math]::Min($height * 0.55, ($height - 2)))
    $centerX = [int][Math]::Round($x + $width / 2); $centerY = [int][Math]::Round($y + $height / 2)
    if ($toX -eq $centerX -and $toY -eq $centerY) { $toX = [int][Math]::Round($x + $width - 2) }
    return [pscustomobject]@{ automationId = 'MarkdownSelectionStartHandle'; toPoint = "$toX,$toY"; bounds = @{x=$x;y=$y;width=$width;height=$height} }
}

function Invoke-TouchLifecycleCase {
    param([Parameter(Mandatory)]$Case, [Parameter(Mandatory)][string]$CaseDirectory)
    $result = [ordered]@{
        id = [string]$Case.id; lifecycleCase = [string]$Case.lifecycleCase; viewport = [string]$Case.viewport
        status = 'UNVERIFIED'; failure = $null; childLaunchAttempted = $false; touchCommandIssued = $false
        freshMenuDismissalCommandAttempted = $false; freshMenuDismissalCommandIssued = $false
        launch = $null; probeWindow = $null; probeIdentity = $null; payload = $null; receipts = [Collections.Generic.List[string]]::new()
        finalReport = $null; reportValidation = $null; cleanup = [pscustomobject]@{ status = 'NOT_STARTED'; closeInputWithheld = $true }
    }
    $caseDirectoryExists = $false; $safeToContinue = $false
    $script:ProbeIdentity = $null; $script:TouchCommandIssued = $false; $script:LastReceipt = $null; $script:LastReceiptPath = $null; $script:ReceiptIndex = 0
    try {
        if (Test-Path -LiteralPath $CaseDirectory) { throw "Case evidence directory already exists; refusing overwrite: $CaseDirectory" }
        [IO.Directory]::CreateDirectory($CaseDirectory) | Out-Null
        $caseDirectoryExists = $true
        $null = Assert-TouchLifecycleOriginalIdentity -EvidenceDirectory $CaseDirectory
        $windows = @(Get-TouchLifecycleWindows -EvidenceDirectory $CaseDirectory)
        $existing = @($windows | Where-Object { [string](Get-TouchLifecycleProperty $_ 'title') -ceq 'MarkdownRenderer touch lifecycle probe' })
        if ($existing.Count -ne 0) { throw 'A lifecycle probe child exists before this case; refusing to adopt or close it.' }
        $result.childLaunchAttempted = $true
        $launchCode = New-TouchLifecycleGuestLauncherCode -OriginalIdentity $script:OriginalIdentity
        $launch = Invoke-TouchLifecycleGuestCode -Code $launchCode -EvidenceDirectory $CaseDirectory
        $result.launch = $launch.value
        if ($launch.exitCode -ne 0) { throw "Direct guest launch returned $($launch.exitCode)." }
        $launchIdentity = Assert-TouchLifecycleLaunchReceipt -Receipt $launch.value -OriginalIdentity $script:OriginalIdentity
        $window = Wait-TouchLifecycleProbeWindow -LaunchIdentity $launchIdentity -EvidenceDirectory $CaseDirectory
        $result.probeWindow = $window
        $identityExpected = [pscustomobject]@{
            processId = $launchIdentity.processId; processName = 'MarkdownRenderer.Sample'; executablePath = $script:Prior.guestExecutablePath
            startTicks = $launchIdentity.startTicks; exeSha256 = $script:Prior.exeSha256; hwnd = $window.hwnd; title = $window.title
        }
        $actual = Invoke-TouchLifecycleGuestProbe -Mode Identity -Identity ([pscustomobject]@{
            processId=$launchIdentity.processId; processName='MarkdownRenderer.Sample'; executablePath=$script:Prior.guestExecutablePath
            startTicks=$launchIdentity.startTicks; exeSha256=$script:Prior.exeSha256; hwnd=$window.hwnd; title=$window.title
        }) -EvidenceDirectory $CaseDirectory
        $script:ProbeIdentity = Assert-TouchLifecycleProcessIdentity -Actual $actual -Expected $identityExpected
        $result.probeIdentity = $script:ProbeIdentity
        $result.payload = Get-TouchLifecyclePayloadHashes -Identity $script:ProbeIdentity -EvidenceDirectory $CaseDirectory
        $null = Assert-TouchLifecycleOriginalIdentity -EvidenceDirectory $CaseDirectory

        Set-TouchLifecycleViewport -Viewport ([string]$Case.viewport) -EvidenceDirectory $CaseDirectory
        Set-TouchLifecycleCase -Case $Case -EvidenceDirectory $CaseDirectory
        $null = Wait-TouchLifecycleReport -EvidenceDirectory $CaseDirectory -AcceptPhase @('idle') -TimeoutSeconds 8
        $arm = Invoke-TouchLifecycleUi -Arguments @('invoke','TouchLifecycleArmCase','--action','invoke') -EvidenceDirectory $CaseDirectory
        $armed = Wait-TouchLifecycleReport -EvidenceDirectory $CaseDirectory -AcceptPhase @('awaitLongPress') -TimeoutSeconds 5
        if ([string](Get-TouchLifecycleProperty $armed 'lifecycleCase') -cne [string]$Case.lifecycleCase -or [string](Get-TouchLifecycleProperty $armed 'viewport') -cne [string]$Case.viewport) {
            throw 'Probe did not arm the exact requested case and viewport.'
        }

        $longPoint = Get-TouchLifecycleRangePoint -Needle 'firstword' -ProbeIdentity $script:ProbeIdentity -Viewport ([string]$Case.viewport) -EvidenceDirectory $CaseDirectory
        $result.touchCommandIssued = $true
        $null = Invoke-TouchLifecycleGesture -Target 'MarkdownRenderer' -Gesture 'long-press' -At "$($longPoint.x),$($longPoint.y)" -HoldMilliseconds 800 -EvidenceDirectory $CaseDirectory
        $afterLong = Wait-TouchLifecycleReport -EvidenceDirectory $CaseDirectory -AcceptPhase @('awaitHandleContact') -TimeoutSeconds $script:TouchLifecycleCoordinatorWaitSeconds
        $longSelection = Get-TouchLifecycleProperty $afterLong 'longPressSelection'
        if ($null -eq $longSelection) { throw 'Probe did not record the real long-press selection snapshot.' }
        $state = Invoke-TouchLifecycleGuestProbe -Mode State -Identity $script:ProbeIdentity -EvidenceDirectory $CaseDirectory -Viewport ([string]$Case.viewport)
        if ([string](Get-TouchLifecycleProperty $state 'selection') -cne 'firstword' -or [int](Get-TouchLifecycleProperty $state 'rangeCount') -ne 1) {
            throw 'Actual touch selection did not resolve to exactly the authored firstword.'
        }
        $null = Invoke-TouchLifecycleUi -Arguments @('send-keys','esc','--via','send-input') -EvidenceDirectory $CaseDirectory
        $null = Wait-TouchLifecycleReport -EvidenceDirectory $CaseDirectory -AcceptPhase @('awaitHandleContact') -TimeoutSeconds $script:TouchLifecycleCoordinatorWaitSeconds
        $nudge = Get-TouchLifecycleHandleNudge -ProbeIdentity $script:ProbeIdentity -Viewport ([string]$Case.viewport) -EvidenceDirectory $CaseDirectory
        $result.touchCommandIssued = $true
        $null = Invoke-TouchLifecycleGesture -Target $nudge.automationId -Gesture 'swipe' -ToPoint $nudge.toPoint -EvidenceDirectory $CaseDirectory

        $final = $null
        if ([string]$Case.lifecycleCase -ceq 'unload') {
            $null = Wait-TouchLifecycleReport -EvidenceDirectory $CaseDirectory -AcceptPhase @('awaitFreshLongPress') -TimeoutSeconds $script:TouchLifecycleCoordinatorWaitSeconds
            $freshPoint = Get-TouchLifecycleRangePoint -Needle 'freshword' -ProbeIdentity $script:ProbeIdentity -Viewport ([string]$Case.viewport) -EvidenceDirectory $CaseDirectory
            $result.touchCommandIssued = $true
            $null = Invoke-TouchLifecycleGesture -Target 'MarkdownRenderer' -Gesture 'long-press' -At "$($freshPoint.x),$($freshPoint.y)" -HoldMilliseconds 800 -EvidenceDirectory $CaseDirectory
            $menu = Wait-TouchLifecycleReport -EvidenceDirectory $CaseDirectory -AcceptPhase @('awaitFreshMenuDismissal') `
                -RejectPhase @('awaitFreshTerminalCleanup','complete') -TimeoutSeconds $script:TouchLifecycleCoordinatorWaitSeconds
            $null = Assert-TouchLifecycleFreshMenuReadyForDismissal -Report $menu
            # Re-read immediately before sending Escape. If the probe observed
            # that the menu has already closed, do not send input to the child.
            $menu = Get-TouchLifecycleReceipt -EvidenceDirectory $CaseDirectory
            $null = Assert-TouchLifecycleFreshMenuReadyForDismissal -Report $menu
            $result.freshMenuDismissalCommandAttempted = $true
            $null = Invoke-TouchLifecycleUi -Arguments @('send-keys','esc','--via','send-input') -EvidenceDirectory $CaseDirectory
            $result.freshMenuDismissalCommandIssued = $true
            # The probe remains alive in a 12-second cleanup phase after it
            # observes the menu close. A direct complete receipt is valid too:
            # the native phase can finish before this guarded host read.
            $final = Wait-TouchLifecycleReport -EvidenceDirectory $CaseDirectory -AcceptPhase @('complete') -TimeoutSeconds 12
            if ($null -eq (Get-TouchLifecycleProperty $final 'freshSelection') -or
                $null -eq (Get-TouchLifecycleProperty $final 'afterFreshTerminal')) {
                throw 'Probe omitted a retained fresh-selection or same-epoch terminal snapshot.'
            }
            $freshState = Invoke-TouchLifecycleGuestProbe -Mode State -Identity $script:ProbeIdentity -EvidenceDirectory $CaseDirectory -Viewport ([string]$Case.viewport)
            if ([string](Get-TouchLifecycleProperty $freshState 'selection') -cne 'freshword' -or [int](Get-TouchLifecycleProperty $freshState 'rangeCount') -ne 1) {
                throw 'Actual fresh post-reattach touch did not select exactly freshword.'
            }
        }
        if ($null -eq $final) {
            $final = Wait-TouchLifecycleReport -EvidenceDirectory $CaseDirectory -AcceptPhase @('complete') -TimeoutSeconds 8
        }
        $result.finalReport = $final
        $result.receipts = @($script:LastReceiptPath)
        $result.reportValidation = Assert-TouchLifecycleReport -Report $final -ExpectedCase ([string]$Case.lifecycleCase) -ExpectedViewport ([string]$Case.viewport)
        $result.status = 'PASS'
    }
    catch {
        $result.failure = $_.Exception.ToString()
        if ($null -ne $script:LastReceipt) { $result.finalReport = $script:LastReceipt; $result.receipts = @($script:LastReceiptPath) }
        if ($null -ne $script:LastReceipt -and [string](Get-TouchLifecycleProperty $script:LastReceipt 'phase') -ceq 'failed') { $result.status = 'FAIL' }
        elseif ($null -ne $result.finalReport -and [string](Get-TouchLifecycleProperty $result.finalReport 'phase') -ceq 'complete') { $result.status = 'FAIL' }
    }
    finally {
        if ($result.childLaunchAttempted) {
            try {
                if ($null -eq $result.probeWindow -or $null -eq $script:ProbeIdentity) { throw 'Child launch was attempted without a fully proven exact PID/HWND/start/path/hash/title; cleanup input is unsafe.' }
                $closeSafety = Get-TouchLifecycleNormalCloseSafety -TouchCommandIssued ([bool]$result.touchCommandIssued) -Report $result.finalReport -ExpectedCase ([string]$Case.lifecycleCase) -ExpectedViewport ([string]$Case.viewport)
                if (-not [bool]$closeSafety.safe) {
                    $result.cleanup = [pscustomobject]@{ status = 'UNVERIFIED'; method = $null; error = [string]$closeSafety.reason; closeInputWithheld = $true }
                    $script:StopAfterCase = $true
                }
                else {
                    $result.cleanup = Close-TouchLifecycleProbeNormally -Window $result.probeWindow -Identity $script:ProbeIdentity -OriginalIdentity $script:OriginalIdentity -Payload $result.payload -EvidenceDirectory $CaseDirectory
                    $script:ProbeIdentity = $null
                    $safeToContinue = $true
                }
            }
            catch {
                $result.cleanup = [pscustomobject]@{ status = 'UNVERIFIED'; method = $null; error = $_.Exception.ToString(); closeInputWithheld = $true }
                $script:StopAfterCase = $true
            }
        }
        if ($result.childLaunchAttempted -and [string](Get-TouchLifecycleProperty $result.cleanup 'status') -cne 'PASS') {
            if ([string]$result.status -ceq 'PASS') {
                $result.status = 'UNVERIFIED'
                if ([string]::IsNullOrWhiteSpace([string]$result.failure)) { $result.failure = 'Lifecycle evidence passed, but exact child close/original-app cleanup was not verified.' }
            }
            $script:StopAfterCase = $true
        }
        if ($script:TouchCommandIssued) { $result.touchCommandIssued = $true }
        if ($caseDirectoryExists) {
            $result.finishedUtc = [DateTime]::UtcNow.ToString('o')
            try { Write-TouchLifecycleJsonExclusive -Path (Join-Path $CaseDirectory 'case.json') -Value ([pscustomobject]$result) }
            catch { $script:StopAfterCase = $true; $result.status = 'UNVERIFIED'; $result.failure = 'Case evidence write failed: ' + $_.Exception.Message }
        }
    }
    return [pscustomobject]$result
}

function Write-TouchLifecycleRunReport {
    param([Parameter(Mandatory)][string]$RunDirectory, [Parameter(Mandatory)][object[]]$Results)
    $counts = Get-TouchLifecycleCounts -Results $Results -SetupFailure $script:SetupFailure
    $report = [ordered]@{
        schemaVersion = 1; objective = 'Phase 1 actual-touch lifecycle correctness on the exact already-deployed Sandbox build'
        runId = $script:RunId; priorRunJson = $script:PriorRunPath; priorRunSha256 = $script:PriorRunSha256
        target = 'sandbox'; targetEpoch = $script:Prior.targetEpoch; workflowId = $script:WorkflowId
        originalApp = $script:OriginalIdentity; hostPayloads = @($script:Prior.payloads); guestProbe = $script:StagedProbe
        setupFailure = $script:SetupFailure; casePlan = @(Get-TouchLifecycleCasePlan); resultCounts = $counts
        results = @($Results); complete = ($counts.pass -eq 6 -and $counts.fail -eq 0 -and $counts.unverified -eq 0)
        generatedUtc = [DateTime]::UtcNow.ToString('o')
    }
    Write-TouchLifecycleJsonExclusive -Path (Join-Path $RunDirectory 'results.json') -Value $report
    Write-TouchLifecycleJsonExclusive -Path (Join-Path $RunDirectory 'run.json') -Value ([ordered]@{
        runId = $script:RunId; priorRunJson = $script:PriorRunPath; priorRunSha256 = $script:PriorRunSha256
        target = 'sandbox'; targetEpoch = $script:Prior.targetEpoch; workflowId = $script:WorkflowId
        originalApp = $script:OriginalIdentity; hostPayloads = @($script:Prior.payloads); guestProbe = $script:StagedProbe
        generatedUtc = [DateTime]::UtcNow.ToString('o')
    })
    return $counts
}

if (-not $RunApprovedSandboxCases) {
    throw 'No Sandbox actions were performed. To explicitly run the approved six-case touch-lifecycle batch, pass -RunApprovedSandboxCases after confirming the current Sandbox epoch.'
}

$script:PriorRunPath = [IO.Path]::GetFullPath($PriorRunJson)
$script:PriorRunSha256 = (Get-FileHash -LiteralPath $script:PriorRunPath -Algorithm SHA256).Hash.ToLowerInvariant()
$script:PriorReceipt = Get-Content -LiteralPath $script:PriorRunPath -Raw | ConvertFrom-Json -ErrorAction Stop
$script:Prior = Assert-TouchLifecyclePriorRun -Receipt $script:PriorReceipt
$script:OutputRootPath = [IO.Path]::GetFullPath($OutputRoot)
$script:RunId = 'touch-lifecycle-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'), [guid]::NewGuid().ToString('N')
$script:RunDirectory = Join-Path $script:OutputRootPath $script:RunId
$script:WorkflowId = "markdown-touch-lifecycle-$([guid]::NewGuid().ToString('N'))"
$script:PreviousWorkflowId = $env:WINAPP_UI_WORKFLOW_ID
$script:HostComputerName = [Environment]::MachineName
$script:GuestComputerName = ''
$script:OriginalIdentity = $null
$script:ProbeIdentity = $null
$script:GuestProbePath = ''
$script:SharedProbePath = [IO.Path]::GetFullPath($SharedProbePath)
$script:StagedProbe = $null
$script:CommandIndex = 0
$script:ReceiptIndex = 0
$script:LastReceipt = $null
$script:LastReceiptPath = $null
$script:SetupFailure = $null
$script:UiWorkflowStarted = $false
$script:StopAfterCase = $false
$script:Results = [Collections.Generic.List[object]]::new()
$script:OriginalStartTicks = 0L
$script:TouchCommandIssued = $false
$plan = @(Get-TouchLifecycleCasePlan)
foreach ($case in $plan) { $script:Results.Add([pscustomobject]@{ id=$case.id; lifecycleCase=$case.lifecycleCase; viewport=$case.viewport; status='UNVERIFIED'; failure='Not run because setup or an earlier case did not complete safely.'; cleanup=[pscustomobject]@{status='NOT_STARTED';closeInputWithheld=$true} }) }

try {
    if (Test-Path -LiteralPath $script:RunDirectory) { throw "Unique run evidence directory already exists; refusing overwrite: $script:RunDirectory" }
    [IO.Directory]::CreateDirectory($script:RunDirectory) | Out-Null
    if (-not (Test-Path -LiteralPath $script:SharedProbePath -PathType Leaf)) { throw "Shared guest identity/UIA probe is missing: $script:SharedProbePath" }
    foreach ($payload in $script:Prior.payloads) {
        if (-not (Test-Path -LiteralPath $payload.hostPath -PathType Leaf)) { throw "Prior build payload is missing: $($payload.hostPath)" }
        $item = Get-Item -LiteralPath $payload.hostPath
        $hash = (Get-FileHash -LiteralPath $payload.hostPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ([long]$item.Length -ne [long]$payload.length -or $hash -cne [string]$payload.sha256) { throw "Current host payload '$($payload.name)' differs from the exact prior run receipt." }
    }
    $script:HostComputerName = [Environment]::MachineName
    $env:WINAPP_UI_WORKFLOW_ID = $script:WorkflowId
    $script:UiWorkflowStarted = $true
    $setupDirectory = Join-Path $script:RunDirectory 'setup'
    [IO.Directory]::CreateDirectory($setupDirectory) | Out-Null
    $snapshot = (Invoke-TouchLifecycleNative -Arguments @('target','snapshot','sandbox') -EvidenceDirectory $setupDirectory -Json).value
    $null = Assert-TouchLifecycleSnapshot -Snapshot $snapshot -ExpectedEpoch $script:Prior.targetEpoch
    $windows = @(Get-TouchLifecycleWindows -EvidenceDirectory $setupDirectory)
    $main = @($windows | Where-Object { [int](Get-TouchLifecycleProperty $_ 'processId') -eq $script:Prior.appProcessId -and [long](Get-TouchLifecycleProperty $_ 'hwnd') -eq $script:Prior.appHwnd -and [string](Get-TouchLifecycleProperty $_ 'processName') -ceq $script:Prior.appProcessName -and [string](Get-TouchLifecycleProperty $_ 'title') -ceq $script:Prior.appTitle })
    if ($main.Count -ne 1) { throw 'The exact original Sandbox window captured by PriorRunJson is not uniquely present.' }
    if (@($windows | Where-Object { [string](Get-TouchLifecycleProperty $_ 'title') -ceq 'MarkdownRenderer touch lifecycle probe' }).Count -ne 0) { throw 'A touch lifecycle probe window already exists; refusing to adopt it.' }
    $script:GuestComputerName = Get-TouchLifecycleGuestComputerName -EvidenceDirectory $setupDirectory
    # The identity and payload probes below run the staged guest probe, so it
    # must be staged first; otherwise its guest path is still empty.
    $script:StagedProbe = Stage-TouchLifecycleGuestProbe -Snapshot $snapshot -EvidenceDirectory $setupDirectory
    $script:OriginalStartTicks = 0L
    $originalIdentity = [pscustomobject]@{
        processId = [int]$script:Prior.appProcessId; processName = [string]$script:Prior.appProcessName
        executablePath = [string]$script:Prior.guestExecutablePath; startTicks = 0L; exeSha256 = [string]$script:Prior.exeSha256
        hwnd = [long]$script:Prior.appHwnd; title = [string]$script:Prior.appTitle
    }
    $liveOriginal = Invoke-TouchLifecycleGuestProbe -Mode Identity -Identity $originalIdentity -EvidenceDirectory $setupDirectory
    $script:OriginalStartTicks = [long](Get-TouchLifecycleProperty $liveOriginal 'startTicks')
    $script:OriginalIdentity = Assert-TouchLifecycleProcessIdentity -Actual $liveOriginal -Expected ([pscustomobject]@{
        processId = [int]$script:Prior.appProcessId; processName = [string]$script:Prior.appProcessName; executablePath = [string]$script:Prior.guestExecutablePath
        startTicks = $script:OriginalStartTicks; exeSha256 = [string]$script:Prior.exeSha256; hwnd = [long]$script:Prior.appHwnd; title = [string]$script:Prior.appTitle
    })
    $originalPayload = Get-TouchLifecyclePayloadHashes -Identity $script:OriginalIdentity -EvidenceDirectory $setupDirectory
    foreach ($index in 0..($plan.Count - 1)) {
        if ($script:StopAfterCase) { break }
        $directory = Join-Path $script:RunDirectory ('cases\{0:D2}-{1}' -f ($index + 1), $plan[$index].id)
        $caseResult = Invoke-TouchLifecycleCase -Case $plan[$index] -CaseDirectory $directory
        $script:Results[$index] = $caseResult
        if ($script:StopAfterCase) { break }
    }
}
catch {
    $script:SetupFailure = [pscustomobject]@{ status='UNVERIFIED'; requirement='Preserve the original app, exact Sandbox epoch, and exact deployed payload before any input.'; failure=($_.Exception.ToString() + [Environment]::NewLine + $_.ScriptStackTrace); recordedUtc=[DateTime]::UtcNow.ToString('o') }
}
finally {
    if ($script:UiWorkflowStarted) {
        try {
            $yieldDirectory = if (Test-Path -LiteralPath $script:RunDirectory -PathType Container) { $script:RunDirectory } else { $env:TEMP }
            $null = Assert-TouchLifecycleEpoch -EvidenceDirectory $yieldDirectory
            if ($null -ne $script:ProbeIdentity) { $null = Assert-TouchLifecycleActionTarget -EvidenceDirectory $yieldDirectory }
            elseif ($null -ne $script:OriginalIdentity) { $null = Assert-TouchLifecycleOriginalIdentity -EvidenceDirectory $yieldDirectory }
            $null = Invoke-TouchLifecycleNative -Arguments @('ui','yield','--on','sandbox') -EvidenceDirectory $yieldDirectory
        }
        catch {
            $script:Results.Add([pscustomobject]@{ id='WORKFLOW'; lifecycleCase=$null; viewport=$null; status='UNVERIFIED'; failure='Could not safely yield the run-scoped Sandbox workflow: ' + $_.Exception.ToString(); cleanup=[pscustomobject]@{status='UNVERIFIED';closeInputWithheld=$true} })
        }
        $env:WINAPP_UI_WORKFLOW_ID = $script:PreviousWorkflowId
    }
}

if (-not (Test-Path -LiteralPath $script:RunDirectory -PathType Container)) { throw 'No run output directory was created; no evidence report can be written.' }
$counts = Write-TouchLifecycleRunReport -RunDirectory $script:RunDirectory -Results @($script:Results.ToArray())
$counts | Format-List
Write-Host "Touch-lifecycle Sandbox evidence: $script:RunDirectory"
if ($counts.fail -gt 0) { exit 1 }
if ($counts.unverified -gt 0 -or $counts.pass -ne 6) { exit 2 }
exit 0
