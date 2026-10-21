[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })][string]$PriorRunJson,
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\correctness'),
    [string]$ProbePath = (Join-Path $PSScriptRoot 'MarkdownDeferredTouchSandboxProbe.ps1'),
    [string]$SharedProbePath = (Join-Path $PSScriptRoot 'MarkdownCorrectnessSandboxProbe.ps1'),
    [switch]$RunApprovedSandboxCases
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-DeferredTouchProperty {
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

function Assert-DeferredTouchPriorRun {
    param([Parameter(Mandatory)]$Receipt)
    $target = [string](Get-DeferredTouchProperty $Receipt 'target')
    $epoch = [string](Get-DeferredTouchProperty $Receipt 'targetEpoch')
    $architecture = [string](Get-DeferredTouchProperty $Receipt 'architecture')
    $app = Get-DeferredTouchProperty $Receipt 'app'
    $build = Get-DeferredTouchProperty $Receipt 'buildBinary'
    $payload = Get-DeferredTouchProperty $Receipt 'payloadIdentity'
    $appPid = 0
    $appHwnd = 0L
    if ($target -cne 'sandbox' -or [string]::IsNullOrWhiteSpace($epoch) -or $architecture -cne 'x64') {
        throw 'Prior run receipt must identify the same x64 Sandbox target and a nonempty target epoch.'
    }
    if ($null -eq $app -or [string](Get-DeferredTouchProperty $app 'target') -cne 'sandbox' -or
        [string](Get-DeferredTouchProperty $app 'epoch') -cne $epoch -or
        -not [int]::TryParse([string](Get-DeferredTouchProperty $app 'processId'), [ref]$appPid) -or $appPid -le 0 -or
        -not [long]::TryParse([string](Get-DeferredTouchProperty $app 'hwnd'), [ref]$appHwnd) -or $appHwnd -le 0 -or
        [string](Get-DeferredTouchProperty $app 'processName') -cne 'MarkdownRenderer.Sample' -or
        [string]::IsNullOrWhiteSpace([string](Get-DeferredTouchProperty $app 'title'))) {
        throw 'Prior run receipt does not contain the exact original Sandbox sample PID/HWND/title identity.'
    }
    if ($null -eq $build -or [string](Get-DeferredTouchProperty $build 'sha256') -notmatch '^[0-9a-fA-F]{64}$' -or
        [string]::IsNullOrWhiteSpace([string](Get-DeferredTouchProperty $build 'path')) -or
        [long](Get-DeferredTouchProperty $build 'length') -le 0) {
        throw 'Prior run receipt lacks the exact nonempty local executable build identity.'
    }
    if ($null -eq $payload -or [string](Get-DeferredTouchProperty $payload 'status') -cne 'MATCH') {
        throw 'Prior run receipt does not attest that the deployed sample/renderer payload matched the host build.'
    }
    $hostPayloads = @(Get-DeferredTouchProperty $payload 'host')
    $guestPayloads = @(Get-DeferredTouchProperty $payload 'guest')
    $requiredNames = @('MarkdownRenderer.Sample.exe', 'MarkdownRenderer.Sample.dll', 'MarkdownRenderer.dll')
    foreach ($name in $requiredNames) {
        $hostMatches = @($hostPayloads | Where-Object { [string](Get-DeferredTouchProperty $_ 'name') -ceq $name })
        $guestMatches = @($guestPayloads | Where-Object { [string](Get-DeferredTouchProperty $_ 'name') -ceq $name })
        if ($hostMatches.Count -ne 1 -or $guestMatches.Count -ne 1) {
            throw "Prior run receipt must contain exactly one host and guest '$name' payload."
        }
        $hostEntry = $hostMatches[0]
        $guest = $guestMatches[0]
        $hostHash = [string](Get-DeferredTouchProperty $hostEntry 'sha256')
        $guestHash = [string](Get-DeferredTouchProperty $guest 'sha256')
        $hostLength = 0L
        $guestLength = 0L
        if ($hostHash -notmatch '^[0-9a-fA-F]{64}$' -or $guestHash -notmatch '^[0-9a-fA-F]{64}$' -or
            -not [long]::TryParse([string](Get-DeferredTouchProperty $hostEntry 'length'), [ref]$hostLength) -or $hostLength -le 0 -or
            -not [long]::TryParse([string](Get-DeferredTouchProperty $guest 'length'), [ref]$guestLength) -or $guestLength -le 0 -or
            $hostHash -cne $guestHash -or $hostLength -ne $guestLength) {
            throw "Prior run host/guest payload identity is invalid or mismatched for '$name'."
        }
        if ([string](Get-DeferredTouchProperty $hostEntry 'path') -notmatch '^[A-Za-z]:\\' -or
            [string](Get-DeferredTouchProperty $guest 'path') -notmatch '^[A-Za-z]:\\') {
            throw "Prior run payload paths are not absolute for '$name'."
        }
        $hostDirectory = [IO.Path]::GetDirectoryName([string](Get-DeferredTouchProperty $hostEntry 'path'))
        $guestDirectory = [IO.Path]::GetDirectoryName([string](Get-DeferredTouchProperty $guest 'path'))
        if (-not [string]::Equals($hostDirectory, [IO.Path]::GetDirectoryName([string](Get-DeferredTouchProperty $build 'path')), [StringComparison]::OrdinalIgnoreCase) -or
            -not [string]::Equals($guestDirectory, [IO.Path]::GetDirectoryName([string](Get-DeferredTouchProperty $payload 'guestExecutablePath')), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Prior run payload '$name' was not recorded beside the exact approved host/guest executable."
        }
    }
    $guestExe = [string](Get-DeferredTouchProperty $payload 'guestExecutablePath')
    if ([string]::IsNullOrWhiteSpace($guestExe) -or
        [IO.Path]::GetFileName($guestExe) -cne 'MarkdownRenderer.Sample.exe' -or
        [int](Get-DeferredTouchProperty $payload 'guestProcessId') -ne $appPid) {
        throw 'Prior run does not bind its guest executable to the exact original sample PID.'
    }
    return [pscustomobject]@{
        target = $target; targetEpoch = $epoch; architecture = $architecture
        appProcessId = $appPid; appHwnd = $appHwnd; appProcessName = 'MarkdownRenderer.Sample'
        appTitle = [string](Get-DeferredTouchProperty $app 'title')
        buildBinary = $build; hostPayloads = @($hostPayloads); guestPayloads = @($guestPayloads)
        guestExecutablePath = $guestExe; host = $payload
    }
}

function Assert-DeferredTouchSnapshot {
    param([Parameter(Mandatory)]$Snapshot, [Parameter(Mandatory)][string]$ExpectedEpoch)
    $target = Get-DeferredTouchProperty $Snapshot 'executionTarget'
    $capabilities = Get-DeferredTouchProperty $Snapshot 'capabilities'
    $desktop = Get-DeferredTouchProperty $Snapshot 'desktop'
    $running = Get-DeferredTouchProperty $Snapshot 'running'
    $inputReady = Get-DeferredTouchProperty $desktop 'effectiveInputReady'
    $captureReady = Get-DeferredTouchProperty $desktop 'effectiveCaptureReady'
    if ($running -isnot [bool] -or $inputReady -isnot [bool] -or $captureReady -isnot [bool] -or
        -not $running -or
        [string](Get-DeferredTouchProperty $target 'epoch') -cne $ExpectedEpoch -or
        [string](Get-DeferredTouchProperty $capabilities 'architecture') -cne 'x64' -or
        -not $inputReady -or -not $captureReady) {
        throw 'Sandbox is not the same running x64 epoch with effective input and capture ready.'
    }
    return [string](Get-DeferredTouchProperty $target 'epoch')
}

function Assert-DeferredTouchPayloadMatch {
    param([Parameter(Mandatory)][object[]]$Expected, [Parameter(Mandatory)][object[]]$ActualFiles, [Parameter(Mandatory)][string]$ExpectedDirectory)
    $directory = [IO.Path]::GetFullPath($ExpectedDirectory)
    $comparisons = [Collections.Generic.List[object]]::new()
    foreach ($expectedFile in $Expected) {
        $name = [string](Get-DeferredTouchProperty $expectedFile 'name')
        $expectedHash = [string](Get-DeferredTouchProperty $expectedFile 'sha256')
        $expectedLength = 0L
        if ([string]::IsNullOrWhiteSpace($name) -or $expectedHash -notmatch '^[0-9a-fA-F]{64}$' -or
            -not [long]::TryParse([string](Get-DeferredTouchProperty $expectedFile 'length'), [ref]$expectedLength) -or $expectedLength -le 0) {
            throw "Expected payload manifest contains an invalid entry '$name'."
        }
        $sameName = @($ActualFiles | Where-Object { [string](Get-DeferredTouchProperty $_ 'name') -ceq $name })
        if ($sameName.Count -ne 1) { throw "Actual payload manifest must contain exactly one '$name'." }
        $actual = $sameName[0]
        $actualHash = [string](Get-DeferredTouchProperty $actual 'sha256')
        $actualLength = 0L
        $actualPath = [string](Get-DeferredTouchProperty $actual 'path')
        if ($actualHash -notmatch '^[0-9a-fA-F]{64}$' -or
            -not [long]::TryParse([string](Get-DeferredTouchProperty $actual 'length'), [ref]$actualLength) -or
            $actualLength -ne $expectedLength -or $actualHash -cne $expectedHash -or
            [IO.Path]::GetFileName($actualPath) -cne $name -or
            -not [string]::Equals([IO.Path]::GetFullPath((Split-Path -Parent $actualPath)), $directory, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Guest payload '$name' differs from the approved build or is not beside the exact guest executable (hash=$actualHash/$expectedHash, length=$actualLength/$expectedLength, path=$actualPath, directory=$directory)."
        }
        $comparisons.Add([pscustomobject]@{ name = $name; length = $actualLength; sha256 = $actualHash.ToLowerInvariant(); guestPath = [IO.Path]::GetFullPath($actualPath); status = 'MATCH' })
    }
    return [pscustomobject]@{ status = 'MATCH'; files = @($comparisons.ToArray()) }
}

function Assert-DeferredTouchProcessIdentity {
    param([Parameter(Mandatory)]$Actual, [Parameter(Mandatory)]$Expected)
    $actualPid = 0
    $expectedPid = 0
    $actualStart = 0L
    $expectedStart = 0L
    $actualName = [string](Get-DeferredTouchProperty $Actual 'processName')
    $expectedName = [string](Get-DeferredTouchProperty $Expected 'processName')
    $actualPath = [string](Get-DeferredTouchProperty $Actual 'executablePath')
    $expectedPath = [string](Get-DeferredTouchProperty $Expected 'executablePath')
    if (-not [int]::TryParse([string](Get-DeferredTouchProperty $Actual 'processId'), [ref]$actualPid) -or $actualPid -le 0 -or
        -not [int]::TryParse([string](Get-DeferredTouchProperty $Expected 'processId'), [ref]$expectedPid) -or $expectedPid -le 0 -or
        -not [long]::TryParse([string](Get-DeferredTouchProperty $Actual 'startTicks'), [ref]$actualStart) -or $actualStart -le 0 -or
        -not [long]::TryParse([string](Get-DeferredTouchProperty $Expected 'startTicks'), [ref]$expectedStart) -or $expectedStart -le 0 -or
        $actualName -cne $expectedName -or [string]::IsNullOrWhiteSpace($actualPath) -or [string]::IsNullOrWhiteSpace($expectedPath) -or
        -not [IO.Path]::IsPathRooted($actualPath) -or -not [IO.Path]::IsPathRooted($expectedPath) -or
        [string](Get-DeferredTouchProperty $Actual 'exeSha256') -cne [string](Get-DeferredTouchProperty $Expected 'exeSha256')) {
        throw 'Process identity is incomplete or differs in PID, name, exact start ticks, executable path, or SHA-256.'
    }
    if ($actualPid -ne $expectedPid -or $actualStart -ne $expectedStart -or
        -not [string]::Equals([IO.Path]::GetFullPath($actualPath), [IO.Path]::GetFullPath($expectedPath), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Process identity changed or PID was reused (expected $expectedPid/$expectedStart, actual $actualPid/$actualStart)."
    }
    $expectedHwnd = Get-DeferredTouchProperty $Expected 'hwnd'
    if ($null -ne $expectedHwnd -and [long](Get-DeferredTouchProperty $Actual 'hwnd') -ne [long]$expectedHwnd) {
        throw 'Process identity HWND differs from the exact approved window.'
    }
    $expectedTitle = Get-DeferredTouchProperty $Expected 'title'
    if ($null -ne $expectedTitle -and [string](Get-DeferredTouchProperty $Actual 'title') -cne [string]$expectedTitle) {
        throw 'Process identity window title differs from the exact approved title.'
    }
    return [pscustomobject]@{
        processId = $actualPid; processName = $actualName; executablePath = [IO.Path]::GetFullPath($actualPath)
        startTicks = $actualStart; exeSha256 = ([string](Get-DeferredTouchProperty $Actual 'exeSha256')).ToLowerInvariant()
        hwnd = Get-DeferredTouchProperty $Actual 'hwnd'; title = Get-DeferredTouchProperty $Actual 'title'
    }
}

function Assert-DeferredTouchLaunchReceipt {
    param([Parameter(Mandatory)]$Receipt, [Parameter(Mandatory)]$OriginalBefore, [Parameter(Mandatory)]$OriginalAfter, [Parameter(Mandatory)][string]$GuestName, [Parameter(Mandatory)][string]$HostName)
    if ([int](Get-DeferredTouchProperty $Receipt 'schemaVersion') -ne 1 -or
        [string](Get-DeferredTouchProperty $Receipt 'status') -cne 'LAUNCHED' -or
        [string](Get-DeferredTouchProperty $Receipt 'launchMethod') -cne 'guest-Win32_Process-Create' -or
        [string](Get-DeferredTouchProperty $Receipt 'argument') -cne '--renderer-deferred-touch-probe' -or
        [string](Get-DeferredTouchProperty $Receipt 'guestComputerName') -cne $GuestName -or
        [string](Get-DeferredTouchProperty $Receipt 'hostComputerName') -cne $HostName -or
        -not [bool](Get-DeferredTouchProperty $Receipt 'sourceRetained')) {
        throw 'Direct guest launch receipt is missing exact guest/host provenance or fixed launch arguments.'
    }
    $sourceBefore = Get-DeferredTouchProperty $Receipt 'sourceBefore'
    $sourceAfter = Get-DeferredTouchProperty $Receipt 'sourceAfter'
    $originalProcess = [pscustomobject]@{
        processId = [int](Get-DeferredTouchProperty $OriginalBefore 'processId')
        processName = [string](Get-DeferredTouchProperty $OriginalBefore 'processName')
        executablePath = [string](Get-DeferredTouchProperty $OriginalBefore 'executablePath')
        startTicks = [long](Get-DeferredTouchProperty $OriginalBefore 'startTicks')
        exeSha256 = [string](Get-DeferredTouchProperty $OriginalBefore 'exeSha256')
    }
    $null = Assert-DeferredTouchProcessIdentity -Actual $sourceBefore -Expected $originalProcess
    $null = Assert-DeferredTouchProcessIdentity -Actual $sourceAfter -Expected $originalProcess
    $null = Assert-DeferredTouchProcessIdentity -Actual $OriginalAfter -Expected $originalProcess
    $child = Get-DeferredTouchProperty $Receipt 'process'
    $expectedPath = [string](Get-DeferredTouchProperty $OriginalBefore 'executablePath')
    if ([string](Get-DeferredTouchProperty $child 'processName') -cne 'MarkdownRenderer.Sample' -or
        -not [string]::Equals([string](Get-DeferredTouchProperty $child 'executablePath'), $expectedPath, [StringComparison]::OrdinalIgnoreCase) -or
        [string](Get-DeferredTouchProperty $child 'exeSha256') -cne [string](Get-DeferredTouchProperty $OriginalBefore 'exeSha256') -or
        [int](Get-DeferredTouchProperty $child 'processId') -eq [int](Get-DeferredTouchProperty $OriginalBefore 'processId')) {
        throw 'Directly launched probe is not a distinct process using the original exact approved executable.'
    }
    $childStart = 0L
    $childPid = 0
    if (-not [long]::TryParse([string](Get-DeferredTouchProperty $child 'startTicks'), [ref]$childStart) -or $childStart -le 0 -or
        -not [int]::TryParse([string](Get-DeferredTouchProperty $child 'processId'), [ref]$childPid) -or $childPid -le 0) {
        throw 'Direct launch receipt omitted a valid new PID/start-time identity.'
    }
    return [pscustomobject]@{
        processId = $childPid; processName = 'MarkdownRenderer.Sample'; executablePath = [IO.Path]::GetFullPath($expectedPath)
        startTicks = $childStart; exeSha256 = ([string](Get-DeferredTouchProperty $child 'exeSha256')).ToLowerInvariant()
        startTimeUtc = [string](Get-DeferredTouchProperty $child 'startTimeUtc')
    }
}

function Find-DeferredTouchProbeWindow {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Windows, [Parameter(Mandatory)]$LaunchIdentity, [Parameter(Mandatory)]$OriginalIdentity)
    $exactTitle = @($Windows | Where-Object { [string](Get-DeferredTouchProperty $_ 'title') -ceq 'MarkdownRenderer deferred-touch probe' })
    if ($exactTitle.Count -gt 1) { throw "Expected one new deferred-touch probe window, found $($exactTitle.Count)." }
    if ($exactTitle.Count -eq 0) { return $null }
    $window = $exactTitle[0]
    $candidateProcessId = 0
    $hwnd = 0L
    if (-not [int]::TryParse([string](Get-DeferredTouchProperty $window 'processId'), [ref]$candidateProcessId) -or $candidateProcessId -le 0 -or
        -not [long]::TryParse([string](Get-DeferredTouchProperty $window 'hwnd'), [ref]$hwnd) -or $hwnd -le 0 -or
        $candidateProcessId -eq [int](Get-DeferredTouchProperty $OriginalIdentity 'processId') -or
        $candidateProcessId -ne [int](Get-DeferredTouchProperty $LaunchIdentity 'processId') -or
        [string](Get-DeferredTouchProperty $window 'processName') -cne 'MarkdownRenderer.Sample') {
        throw 'The exact-title probe window is not owned by the unique new directly launched sample process.'
    }
    return [pscustomobject]@{ processId = $candidateProcessId; hwnd = $hwnd; processName = 'MarkdownRenderer.Sample'; title = 'MarkdownRenderer deferred-touch probe' }
}

function Get-DeferredTouchDriverStatus {
    param([Parameter(Mandatory)][int]$ExitCode, [AllowNull()][object]$Report)
    if ($null -eq $Report -or [string]::IsNullOrWhiteSpace([string](Get-DeferredTouchProperty $Report 'scenario')) -or
        [string]::IsNullOrWhiteSpace([string](Get-DeferredTouchProperty $Report 'viewport'))) { return 'UNVERIFIED' }
    $status = [string](Get-DeferredTouchProperty $Report 'status')
    if ($status -ceq 'PASS' -and $ExitCode -eq 0) { return 'PASS' }
    if ($status -ceq 'FAIL' -and $ExitCode -ne 0 -and
        -not [string]::IsNullOrWhiteSpace([string](Get-DeferredTouchProperty $Report 'failure'))) { return 'FAIL' }
    return 'UNVERIFIED'
}

function Assert-DeferredTouchDriverReport {
    param(
        [Parameter(Mandatory)]$Report,
        [Parameter(Mandatory)][string]$Scenario,
        [Parameter(Mandatory)][string]$Viewport,
        [Parameter(Mandatory)]$ExpectedIdentity,
        [Parameter(Mandatory)][object[]]$ExpectedPayloads,
        [Parameter(Mandatory)][string]$ExpectedGuestName,
        [Parameter(Mandatory)][string]$ExpectedDirectory
    )
    if ([string](Get-DeferredTouchProperty $Report 'scenario') -cne $Scenario -or
        [string](Get-DeferredTouchProperty $Report 'viewport') -cne $Viewport) {
        throw 'Guest driver report does not describe the exact requested scenario and viewport.'
    }
    $reportedStatus = [string](Get-DeferredTouchProperty $Report 'status')
    if ($reportedStatus -cnotin @('PASS', 'FAIL')) { throw 'Guest driver report has no recognized final status.' }
    if ($reportedStatus -ceq 'FAIL' -and [string]::IsNullOrWhiteSpace([string](Get-DeferredTouchProperty $Report 'failure'))) {
        throw 'Guest driver failure report omitted the concrete assertion or cleanup failure.'
    }
    $identityChecks = 0
    $foregroundChecks = 0
    if (-not [int]::TryParse([string](Get-DeferredTouchProperty $Report 'identityChecks'), [ref]$identityChecks) -or $identityChecks -lt 1 -or
        -not [int]::TryParse([string](Get-DeferredTouchProperty $Report 'foregroundChecks'), [ref]$foregroundChecks) -or $foregroundChecks -lt 1) {
        throw 'Guest driver did not prove at least one exact process-identity check and exact foreground check before interaction.'
    }
    $cleanup = Get-DeferredTouchProperty $Report 'cleanup'
    $pumpStopped = Get-DeferredTouchProperty $cleanup 'pumpStopped'
    $deviceDestroyed = Get-DeferredTouchProperty $cleanup 'deviceDestroyed'
    $deviceRetained = Get-DeferredTouchProperty $cleanup 'deviceRetained'
    if ($null -eq $cleanup -or $pumpStopped -isnot [bool] -or $deviceDestroyed -isnot [bool] -or $deviceRetained -isnot [bool]) {
        throw 'Guest driver omitted typed pump/device lifetime cleanup evidence.'
    }
    if ($reportedStatus -ceq 'PASS' -and
        (-not $pumpStopped -or -not $deviceDestroyed -or $deviceRetained -or
         -not [string]::IsNullOrWhiteSpace([string](Get-DeferredTouchProperty $cleanup 'error')))) {
        throw 'Guest driver reported PASS without clean, joined touch-pump/device cleanup.'
    }
    $reportedIdentity = Get-DeferredTouchProperty $Report 'identity'
    if ([string](Get-DeferredTouchProperty $reportedIdentity 'computerName') -cne $ExpectedGuestName -or
        [long](Get-DeferredTouchProperty $reportedIdentity 'hwnd') -ne [long](Get-DeferredTouchProperty $ExpectedIdentity 'hwnd') -or
        [string](Get-DeferredTouchProperty $reportedIdentity 'title') -cne 'MarkdownRenderer deferred-touch probe') {
        throw 'Guest driver did not retain the exact approved guest machine and probe window identity.'
    }
    $validatedIdentity = Assert-DeferredTouchGuestIdentityShape -Value $reportedIdentity -Mode Identity `
        -ProcessId ([int](Get-DeferredTouchProperty $ExpectedIdentity 'processId')) `
        -WindowHandle ([long](Get-DeferredTouchProperty $ExpectedIdentity 'hwnd')) -GuestComputerName $ExpectedGuestName
    $normalizedIdentity = ConvertTo-DeferredTouchCanonicalProcessIdentity -ValidatedIdentity $validatedIdentity `
        -ExpectedExecutablePath ([string](Get-DeferredTouchProperty $ExpectedIdentity 'executablePath'))
    $null = Assert-DeferredTouchProcessIdentity -Actual $normalizedIdentity -Expected $ExpectedIdentity
    $reportedPayload = Get-DeferredTouchProperty $Report 'payloadHashes'
    if ([string](Get-DeferredTouchProperty $reportedPayload 'mode') -cne 'PayloadHashes' -or
        [int](Get-DeferredTouchProperty $reportedPayload 'processId') -ne [int](Get-DeferredTouchProperty $ExpectedIdentity 'processId') -or
        -not [string]::Equals([string](Get-DeferredTouchProperty $reportedPayload 'executablePath'), [string](Get-DeferredTouchProperty $ExpectedIdentity 'executablePath'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Guest driver payload evidence is not scoped to the exact approved probe executable/process.'
    }
    return Assert-DeferredTouchPayloadMatch -Expected $ExpectedPayloads -ActualFiles @(Get-DeferredTouchProperty $reportedPayload 'files') -ExpectedDirectory $ExpectedDirectory
}

function Get-DeferredTouchNormalCloseSafety {
    param([Parameter(Mandatory)][bool]$DriverInvoked, [Parameter(Mandatory)][bool]$ReportValidated, [AllowNull()][object]$Report)
    if (-not $DriverInvoked) { return [pscustomobject]@{ safe = $true; reason = 'No touch driver was invoked; no synthetic contact could have started.' } }
    if (-not $ReportValidated) { return [pscustomobject]@{ safe = $false; reason = 'The guest report did not validate against the exact app and payload; withholding UI close input.' } }
    if ($null -eq $Report) { return [pscustomobject]@{ safe = $false; reason = 'No guest report proves that the touch worker joined and its device was not retained.' } }
    $cleanup = Get-DeferredTouchProperty $Report 'cleanup'
    $pumpStopped = Get-DeferredTouchProperty $cleanup 'pumpStopped'
    $deviceRetained = Get-DeferredTouchProperty $cleanup 'deviceRetained'
    if ($pumpStopped -isnot [bool] -or $deviceRetained -isnot [bool]) {
        return [pscustomobject]@{ safe = $false; reason = 'Guest report lacks typed worker-join or retained-device evidence.' }
    }
    if (-not $pumpStopped -or $deviceRetained -or
        -not [string]::IsNullOrWhiteSpace([string](Get-DeferredTouchProperty $cleanup 'error'))) {
        return [pscustomobject]@{ safe = $false; reason = 'Guest touch worker did not prove clean join/device cleanup; withholding UI close input.' }
    }
    return [pscustomobject]@{ safe = $true; reason = 'Guest report proves the touch worker joined and no device/contact was retained.' }
}

function Assert-DeferredTouchExactProcessExited {
    param([Parameter(Mandatory)]$ProcessState, [Parameter(Mandatory)]$ExpectedIdentity)
    $targetProcessId = 0
    if ([string](Get-DeferredTouchProperty $ProcessState 'mode') -cne 'ProcessState' -or
        -not [int]::TryParse([string](Get-DeferredTouchProperty $ProcessState 'processId'), [ref]$targetProcessId) -or
        $targetProcessId -ne [int](Get-DeferredTouchProperty $ExpectedIdentity 'processId')) {
        throw 'Guest process-state report does not describe the exact probe PID.'
    }
    $exists = Get-DeferredTouchProperty $ProcessState 'exists'
    if ($exists -isnot [bool]) { throw 'Guest process-state report omitted a boolean exists value.' }
    if (-not $exists) { return $true }
    $currentStart = 0L
    $currentName = [string](Get-DeferredTouchProperty $ProcessState 'processName')
    $currentPath = [string](Get-DeferredTouchProperty $ProcessState 'executablePath')
    if ([string]::IsNullOrWhiteSpace($currentName) -or [string]::IsNullOrWhiteSpace($currentPath) -or
        -not [IO.Path]::IsPathRooted($currentPath) -or
        -not [long]::TryParse([string](Get-DeferredTouchProperty $ProcessState 'startTicks'), [ref]$currentStart) -or $currentStart -le 0) {
        throw 'Guest process-state reported an existing PID without complete path/name/start-time identity.'
    }
    if ($currentName -ceq [string](Get-DeferredTouchProperty $ExpectedIdentity 'processName') -and
        [string]::Equals([IO.Path]::GetFullPath($currentPath), [IO.Path]::GetFullPath([string](Get-DeferredTouchProperty $ExpectedIdentity 'executablePath')), [StringComparison]::OrdinalIgnoreCase) -and
        $currentStart -eq [long](Get-DeferredTouchProperty $ExpectedIdentity 'startTicks')) {
        throw 'The exact deferred-touch probe process is still running after its normal close request.'
    }
    return $true
}

function Get-DeferredTouchCasePlan {
    $cases = [Collections.Generic.List[object]]::new()
    foreach ($viewport in @('owned', 'ancestor')) {
        foreach ($scenario in @('Release', 'CancelNew', 'CancelExisting')) {
            $cases.Add([pscustomobject]@{ id = "P1-005-006-$scenario-$viewport"; scenario = $scenario; viewport = $viewport })
        }
    }
    return @($cases.ToArray())
}

function Get-DeferredTouchCounts {
    param([Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Results, [AllowNull()][object]$SetupFailure)
    $counts = [ordered]@{ pass = 0; fail = 0; unverified = 0 }
    $allResults = [Collections.Generic.List[object]]::new()
    foreach ($item in $Results) { $allResults.Add($item) }
    if ($null -ne $SetupFailure) { $allResults.Add($SetupFailure) }
    foreach ($item in $allResults) {
        switch ([string](Get-DeferredTouchProperty $item 'status')) {
            'PASS' { $counts.pass++ }
            'FAIL' { $counts.fail++ }
            'UNVERIFIED' { $counts.unverified++ }
            default { throw 'A deferred-touch result has an unknown status.' }
        }
    }
    return [pscustomobject]$counts
}

function Write-DeferredTouchJsonExclusive {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)
    $json = (ConvertTo-Json -InputObject $Value -Depth 40) + [Environment]::NewLine
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

function ConvertTo-DeferredTouchPsLiteral {
    param([Parameter(Mandatory)][string]$Value)
    return "'" + $Value.Replace("'", "''") + "'"
}

function ConvertTo-DeferredTouchCommandArgument {
    param([AllowEmptyString()][string]$Value)
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') { return $Value }
    $builder = [Text.StringBuilder]::new()
    $null = $builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            $null = $builder.Append(('\' * (($slashes * 2) + 1))).Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) { $null = $builder.Append(('\' * $slashes)); $slashes = 0 }
        $null = $builder.Append($character)
    }
    if ($slashes -gt 0) { $null = $builder.Append(('\' * ($slashes * 2))) }
    $null = $builder.Append('"')
    return $builder.ToString()
}

function Invoke-DeferredTouchRedirectedProcess {
    param([Parameter(Mandatory)][string]$FileName, [Parameter(Mandatory)][string[]]$Arguments)
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FileName
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $info.StandardOutputEncoding = $strictUtf8
    $info.StandardErrorEncoding = $strictUtf8
    $argumentListProperty = $info.GetType().GetProperty('ArgumentList')
    if ($null -ne $argumentListProperty) {
        $list = $argumentListProperty.GetValue($info, $null)
        foreach ($argument in $Arguments) { $null = $list.Add([string]$argument) }
    }
    else {
        $info.Arguments = (@($Arguments | ForEach-Object { ConvertTo-DeferredTouchCommandArgument ([string]$_) }) -join ' ')
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw "Could not start '$FileName'." }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{ exitCode = $process.ExitCode; stdout = $stdoutTask.GetAwaiter().GetResult(); stderr = $stderrTask.GetAwaiter().GetResult() }
    }
    finally { $process.Dispose() }
}

function Save-DeferredTouchCommand {
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][int]$ExitCode, [Parameter(Mandatory)][string]$Output, [Parameter(Mandatory)][string]$CaseDirectory)
    $script:CommandIndex++
    $label = ($Arguments -join ' ') -replace '[^A-Za-z0-9._-]+', '-'
    if ($label.Length -gt 96) { $label = $label.Substring(0, 96) }
    $path = Join-Path $CaseDirectory ('commands\{0:D4}-{1}.txt' -f $script:CommandIndex, $label)
    [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
    Write-DeferredTouchJsonExclusive -Path $path -Value ([pscustomobject]@{ exitCode = $ExitCode; output = $Output; arguments = $Arguments })
    return $path
}

function Invoke-DeferredTouchNative {
    param([Parameter(Mandatory)][string[]]$Arguments, [Parameter(Mandatory)][string]$CaseDirectory, [switch]$Json, [switch]$AllowNonZero)
    $argv = @($Arguments)
    if ($Json -and $argv -notcontains '--json' -and $argv -notcontains '--') { $argv += '--json' }
    $command = Get-Command -Name 'winapp' -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $fileName = if (-not [string]::IsNullOrWhiteSpace([string]$command.Source)) { [string]$command.Source } else { [string]$command.Path }
    if ([string]::IsNullOrWhiteSpace($fileName)) { throw 'Could not resolve the winapp executable path.' }
    $process = Invoke-DeferredTouchRedirectedProcess -FileName $fileName -Arguments $argv
    $output = [string]$process.stdout
    if (-not [string]::IsNullOrEmpty([string]$process.stderr)) { $output += [Environment]::NewLine + '[stderr]' + [Environment]::NewLine + [string]$process.stderr }
    $evidence = Save-DeferredTouchCommand -Arguments $argv -ExitCode ([int]$process.exitCode) -Output $output -CaseDirectory $CaseDirectory
    if ([int]$process.exitCode -ne 0 -and -not $AllowNonZero) { throw "winapp exited $($process.exitCode) (evidence $evidence): $output" }
    if (-not $Json) { return [pscustomobject]@{ exitCode = [int]$process.exitCode; output = $output; evidence = $evidence } }
    $trimmed = [string]$process.stdout
    $match = [regex]::Match($trimmed, '(?s)(\{|\[)')
    if (-not $match.Success) { throw "winapp returned no JSON (evidence $evidence): $output" }
    try { $value = ConvertFrom-Json -InputObject $trimmed.Substring($match.Index) }
    catch { throw "winapp returned malformed JSON (evidence $evidence): $_`n$output" }
    return [pscustomobject]@{ exitCode = [int]$process.exitCode; value = $value; stdout = [string]$process.stdout; stderr = [string]$process.stderr; output = $output; evidence = $evidence }
}

function Assert-DeferredTouchEpoch {
    param([Parameter(Mandatory)][string]$Phase, [Parameter(Mandatory)][string]$CaseDirectory)
    $snapshot = (Invoke-DeferredTouchNative @('target', 'snapshot', 'sandbox') -CaseDirectory $CaseDirectory -Json).value
    $null = Assert-DeferredTouchSnapshot -Snapshot $snapshot -ExpectedEpoch $script:Prior.targetEpoch
    return $snapshot
}

function ConvertFrom-DeferredTouchJsonObjectText {
    param([Parameter(Mandatory)][string]$Text, [Parameter(Mandatory)][string]$Source)
    if ([string]::IsNullOrWhiteSpace($Text)) { throw "Sandbox guest produced empty JSON text from $Source." }
    $trimmed = $Text.Trim()
    $parseErrors = [Collections.Generic.List[string]]::new()
    $candidateStarts = [Collections.Generic.List[int]]::new()
    $candidateStarts.Add(0)
    for ($index = 0; $index -lt $trimmed.Length; $index++) {
        if ($trimmed[$index] -eq '{' -or $trimmed[$index] -eq '[') { $candidateStarts.Add($index) }
    }
    foreach ($start in $candidateStarts) {
        $candidate = $trimmed.Substring($start).Trim()
        try {
            $parsed = ConvertFrom-Json -InputObject $candidate -ErrorAction Stop
            if ($null -eq $parsed -or $parsed -is [string] -or $parsed -is [ValueType] -or $parsed -is [array]) {
                throw 'Guest result must be one JSON object, not null, scalar, or array.'
            }
            return [pscustomobject]@{ value = $parsed; text = $Text; source = $Source }
        }
        catch { $parseErrors.Add($_.Exception.Message) }
    }
    $detail = if ($parseErrors.Count -gt 0) { $parseErrors[$parseErrors.Count - 1] } else { 'no JSON object start was found' }
    throw "Sandbox guest output from $Source did not contain one valid JSON object: $detail"
}

function ConvertFrom-DeferredTouchNativeGuestResult {
    param([Parameter(Mandatory)]$NativeResult)
    $value = Get-DeferredTouchProperty $NativeResult 'value'
    if ($null -eq $value) { throw "Sandbox target exec returned no JSON value: $((Get-DeferredTouchProperty $NativeResult 'output'))" }
    if ($value -is [string]) {
        return ConvertFrom-DeferredTouchJsonObjectText -Text $value -Source 'native.value string'
    }
    if ($value -is [array] -or $value -is [ValueType]) { throw 'Sandbox target exec returned a scalar/array instead of a guest JSON object.' }

    # Current winapp target-exec emits a direct JSON object in native.value; --json
    # formats CLI errors and does not wrap the child stdout. Keep only a bounded
    # compatibility path for older CLI wrappers with a string stdout/output/result.
    $guestMarkers = @('mode', 'scenario', 'computerName', 'directory', 'launchMethod')
    $isGuestObject = $false
    foreach ($marker in $guestMarkers) {
        if ($null -ne $value.PSObject.Properties[$marker]) { $isGuestObject = $true; break }
    }
    if ($isGuestObject) {
        $rawStdout = [string](Get-DeferredTouchProperty $NativeResult 'stdout')
        $text = if ([string]::IsNullOrWhiteSpace($rawStdout)) { ConvertTo-Json -InputObject $value -Depth 40 -Compress } else { $rawStdout }
        return [pscustomobject]@{ value = $value; text = $text; source = 'native.value direct JSON object' }
    }

    foreach ($name in @('stdout', 'output', 'result')) {
        $candidate = Get-DeferredTouchProperty $value $name
        if ($candidate -is [string] -and -not [string]::IsNullOrWhiteSpace($candidate)) {
            return ConvertFrom-DeferredTouchJsonObjectText -Text $candidate -Source "legacy native.value.$name wrapper"
        }
    }
    throw "Sandbox target exec value is neither a recognized guest JSON object nor a bounded stdout/output/result wrapper: $(ConvertTo-Json -InputObject $value -Depth 8 -Compress)"
}

function Assert-DeferredTouchGuestIdentityShape {
    param(
        [Parameter(Mandatory)]$Value,
        [Parameter(Mandatory)][ValidateSet('Identity', 'PayloadHashes')][string]$Mode,
        [Parameter(Mandatory)][int]$ProcessId,
        [Parameter(Mandatory)][long]$WindowHandle,
        [Parameter(Mandatory)][string]$GuestComputerName
    )
    if ([string](Get-DeferredTouchProperty $Value 'mode') -cne $Mode -or
        [int](Get-DeferredTouchProperty $Value 'processId') -ne $ProcessId) {
        throw "Shared guest identity helper result does not match requested mode/PID ($Mode/$ProcessId)."
    }
    if ($Mode -ceq 'Identity') {
        $reportedStartTicks = 0L
        if ([long](Get-DeferredTouchProperty $Value 'hwnd') -ne $WindowHandle -or
            [string](Get-DeferredTouchProperty $Value 'computerName') -cne $GuestComputerName -or
            -not [long]::TryParse([string](Get-DeferredTouchProperty $Value 'startTicks'), [ref]$reportedStartTicks) -or $reportedStartTicks -le 0 -or
            [string](Get-DeferredTouchProperty $Value 'exeSha256') -notmatch '^[0-9a-fA-F]{64}$' -or
            [string]::IsNullOrWhiteSpace([string](Get-DeferredTouchProperty $Value 'exePath')) -or
            -not [IO.Path]::IsPathRooted([string](Get-DeferredTouchProperty $Value 'exePath')) -or
            [string]::IsNullOrWhiteSpace([string](Get-DeferredTouchProperty $Value 'title'))) {
            throw 'Shared guest identity helper returned an incomplete process/HWND identity object.'
        }
    }
    else {
        $files = @(Get-DeferredTouchProperty $Value 'files')
        $executablePath = [string](Get-DeferredTouchProperty $Value 'executablePath')
        if ($files.Count -eq 0 -or [string]::IsNullOrWhiteSpace($executablePath) -or -not [IO.Path]::IsPathRooted($executablePath)) {
            throw 'Shared guest payload helper returned an incomplete file manifest.'
        }
    }
    return $Value
}

function ConvertTo-DeferredTouchCanonicalProcessIdentity {
    param(
        [Parameter(Mandatory)]$ValidatedIdentity,
        [Parameter(Mandatory)][string]$ExpectedExecutablePath
    )
    if ([string](Get-DeferredTouchProperty $ValidatedIdentity 'mode') -cne 'Identity') {
        throw 'Canonical process identity requires an Identity report that passed the shared guest shape checks.'
    }
    $reportedPath = [string](Get-DeferredTouchProperty $ValidatedIdentity 'exePath')
    if ([string]::IsNullOrWhiteSpace($reportedPath) -or -not [IO.Path]::IsPathRooted($reportedPath) -or
        [string]::IsNullOrWhiteSpace($ExpectedExecutablePath) -or -not [IO.Path]::IsPathRooted($ExpectedExecutablePath)) {
        throw 'Canonical process identity requires rooted reported and expected executable paths.'
    }
    $actualPath = [IO.Path]::GetFullPath($reportedPath)
    $expectedPath = [IO.Path]::GetFullPath($ExpectedExecutablePath)
    if (-not [string]::Equals($actualPath, $expectedPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Canonical process identity executable path differs from the approved deployed executable.'
    }
    if ([IO.Path]::GetExtension($actualPath) -ine '.exe') {
        throw 'Canonical process identity path is not an executable file.'
    }
    $derivedName = [IO.Path]::GetFileNameWithoutExtension($actualPath)
    if ([string]::IsNullOrWhiteSpace($derivedName)) { throw 'Canonical process identity could not derive a process name from the verified executable path.' }
    $reportedName = [string](Get-DeferredTouchProperty $ValidatedIdentity 'processName')
    if (-not [string]::IsNullOrWhiteSpace($reportedName) -and $reportedName -cne $derivedName) {
        throw 'Canonical process identity includes a process name inconsistent with its verified executable path.'
    }

    $canonical = [ordered]@{}
    if ($ValidatedIdentity -is [System.Collections.IDictionary]) {
        foreach ($key in $ValidatedIdentity.Keys) { $canonical[[string]$key] = $ValidatedIdentity[$key] }
    }
    else {
        foreach ($property in $ValidatedIdentity.PSObject.Properties) { $canonical[$property.Name] = $property.Value }
    }
    $canonical['processName'] = $derivedName
    $canonical['executablePath'] = $actualPath
    return [pscustomobject]$canonical
}

function Invoke-DeferredTouchGuestScript {
    param([Parameter(Mandatory)][string]$Code, [Parameter(Mandatory)][string]$CaseDirectory, [switch]$AllowNonZero)
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Code))
    $native = Invoke-DeferredTouchNative @('target', 'exec', 'sandbox', '--json', '--', 'powershell.exe', '-WindowStyle', 'Hidden', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-STA', '-EncodedCommand', $encoded) -CaseDirectory $CaseDirectory -Json -AllowNonZero:$AllowNonZero
    $parsed = ConvertFrom-DeferredTouchNativeGuestResult -NativeResult $native
    return [pscustomobject]@{ exitCode = [int]$native.exitCode; value = $native.value; output = [string]$native.output; evidence = [string]$native.evidence; guestText = [string]$parsed.text; guestValue = $parsed.value; guestResultSource = $parsed.source }
}

function Invoke-DeferredTouchGuestIdentity {
    param([Parameter(Mandatory)][ValidateSet('Identity', 'PayloadHashes')][string]$Mode, [Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][long]$Hwnd, [long]$StartTicks = 0, [Parameter(Mandatory)][string]$CaseDirectory)
    $args = @('target', 'exec', 'sandbox', '--json', '--', 'powershell.exe', '-WindowStyle', 'Hidden', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-STA', '-File', $script:GuestSharedProbePath,
        '-Mode', $Mode, '-ProcessId', [string]$ProcessId, '-WindowHandle', [string]$Hwnd,
        '-ExpectedExeSha256', [string]$script:Prior.buildBinary.sha256,
        '-ExpectedGuestComputerName', [string]$script:GuestComputerName,
        '-ExpectedHostComputerName', [string]$script:HostComputerName)
    if ($StartTicks -gt 0) { $args += @('-ExpectedStartTicks', [string]$StartTicks) }
    $native = Invoke-DeferredTouchNative -Arguments $args -CaseDirectory $CaseDirectory -Json -AllowNonZero
    $parsed = ConvertFrom-DeferredTouchNativeGuestResult -NativeResult $native
    $value = Assert-DeferredTouchGuestIdentityShape -Value $parsed.value -Mode $Mode -ProcessId $ProcessId -WindowHandle $Hwnd -GuestComputerName $script:GuestComputerName
    if ([int]$native.exitCode -ne 0) { throw "Shared guest identity helper failed (exit $($native.exitCode)): $($parsed.text)" }
    if ($Mode -ceq 'Identity') {
        $expectedPath = [string]$script:OriginalExecutablePath
        if ([string]::IsNullOrWhiteSpace($expectedPath)) { $expectedPath = [string]$script:Prior.guestExecutablePath }
        return ConvertTo-DeferredTouchCanonicalProcessIdentity -ValidatedIdentity $value -ExpectedExecutablePath $expectedPath
    }
    return $value
}

function Assert-DeferredTouchOriginalApp {
    param([Parameter(Mandatory)][string]$Phase, [Parameter(Mandatory)][string]$CaseDirectory)
    $null = Assert-DeferredTouchEpoch -Phase $Phase -CaseDirectory $CaseDirectory
    $windowsResult = Invoke-DeferredTouchNative @('ui', 'list-windows', '--on', 'sandbox') -CaseDirectory $CaseDirectory -Json
    $windows = @($windowsResult.value)
    $matches = @($windows | Where-Object {
        [int](Get-DeferredTouchProperty $_ 'processId') -eq $script:Prior.appProcessId -and
        [long](Get-DeferredTouchProperty $_ 'hwnd') -eq $script:Prior.appHwnd -and
        [string](Get-DeferredTouchProperty $_ 'processName') -ceq $script:Prior.appProcessName -and
        [string](Get-DeferredTouchProperty $_ 'title') -ceq $script:Prior.appTitle
    })
    if ($matches.Count -ne 1) { throw "Original app window changed/disappeared during '$Phase'; exact PID/HWND/title matches=$($matches.Count)." }
    $actual = Invoke-DeferredTouchGuestIdentity -Mode Identity -ProcessId $script:Prior.appProcessId -Hwnd $script:Prior.appHwnd -StartTicks $script:OriginalStartTicks -CaseDirectory $CaseDirectory
    $expected = [pscustomobject]@{
        processId = $script:Prior.appProcessId; processName = $script:Prior.appProcessName
        executablePath = $script:OriginalExecutablePath; startTicks = $script:OriginalStartTicks
        exeSha256 = [string]$script:Prior.buildBinary.sha256; hwnd = $script:Prior.appHwnd; title = $script:Prior.appTitle
    }
    return Assert-DeferredTouchProcessIdentity -Actual $actual -Expected $expected
}

function Get-DeferredTouchPayload {
    param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][long]$Hwnd, [Parameter(Mandatory)][long]$StartTicks, [Parameter(Mandatory)][string]$CaseDirectory)
    $receipt = Invoke-DeferredTouchGuestIdentity -Mode PayloadHashes -ProcessId $ProcessId -Hwnd $Hwnd -StartTicks $StartTicks -CaseDirectory $CaseDirectory
    if ([string](Get-DeferredTouchProperty $receipt 'mode') -cne 'PayloadHashes' -or [int](Get-DeferredTouchProperty $receipt 'processId') -ne $ProcessId) {
        throw 'Shared guest helper payload report is not scoped to the exact approved process.'
    }
    $exe = [string](Get-DeferredTouchProperty $receipt 'executablePath')
    if (-not [string]::Equals([IO.Path]::GetFullPath($exe), [IO.Path]::GetFullPath($script:OriginalExecutablePath), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Guest payload report did not come from the exact already-deployed executable directory.'
    }
    $compared = Assert-DeferredTouchPayloadMatch -Expected @($script:Prior.hostPayloads | Where-Object { [bool](Get-DeferredTouchProperty $_ 'required') -or [string](Get-DeferredTouchProperty $_ 'name') -eq 'MarkdownRenderer.Sample.deps.json' }) `
        -Actual @(Get-DeferredTouchProperty $receipt 'files') -ExpectedDirectory (Split-Path -Parent $exe)
    return [pscustomobject]@{ receipt = $receipt; comparison = $compared }
}

function Get-DeferredTouchGuestComputerName {
    param([Parameter(Mandatory)][string]$CaseDirectory)
    $code = '$ErrorActionPreference=''Stop''; [Console]::Out.WriteLine((ConvertTo-Json -InputObject ([pscustomobject]@{computerName=[Environment]::MachineName}) -Compress))'
    $result = Invoke-DeferredTouchGuestScript -Code $code -CaseDirectory $CaseDirectory
    if ($result.exitCode -ne 0 -or $null -eq $result.guestValue -or [string]::IsNullOrWhiteSpace([string]$result.guestValue.computerName)) {
        throw 'Could not obtain an unambiguous Sandbox guest computer name.'
    }
    $name = [string]$result.guestValue.computerName
    if ([string]::Equals($name, $script:HostComputerName, [StringComparison]::OrdinalIgnoreCase)) { throw 'Sandbox target exec reported the host machine name.' }
    return $name
}

function Stage-DeferredTouchGuestScripts {
    param([Parameter(Mandatory)]$Snapshot, [Parameter(Mandatory)][string]$RunId, [Parameter(Mandatory)][string]$CaseDirectory)
    $workRoot = [string](Get-DeferredTouchProperty $Snapshot 'workRoot')
    if ([string]::IsNullOrWhiteSpace($workRoot) -or -not [IO.Path]::IsPathRooted($workRoot)) { throw 'Sandbox snapshot has no absolute workRoot; refusing to guess guest paths.' }
    $relativeDirectory = "correctness\$RunId\deferred-touch"
    $guestDirectory = Join-Path $workRoot $relativeDirectory
    $guestRunRoot = Join-Path $workRoot "correctness\$RunId"
    $script:GuestDriverPath = Join-Path $guestDirectory 'MarkdownDeferredTouchSandboxProbe.ps1'
    $script:GuestSharedProbePath = Join-Path $guestDirectory 'MarkdownCorrectnessSandboxProbe.ps1'
    $code = @"
`$ErrorActionPreference = 'Stop'
if ([Environment]::MachineName -cne $(ConvertTo-DeferredTouchPsLiteral $script:GuestComputerName)) { throw 'Guest machine identity changed before staging.' }
`$runRoot = $(ConvertTo-DeferredTouchPsLiteral $guestRunRoot)
`$directory = $(ConvertTo-DeferredTouchPsLiteral $guestDirectory)
if (Test-Path -LiteralPath `$runRoot) { throw 'Run-scoped deferred-touch staging directory already exists; refusing overwrite.' }
[IO.Directory]::CreateDirectory(`$directory) | Out-Null
[Console]::Out.WriteLine((ConvertTo-Json -InputObject ([pscustomobject]@{directory=`$directory;computerName=[Environment]::MachineName}) -Compress))
"@
    $mkdir = Invoke-DeferredTouchGuestScript -Code $code -CaseDirectory $CaseDirectory
    if ($mkdir.exitCode -ne 0 -or $null -eq $mkdir.guestValue -or [string]$mkdir.guestValue.directory -cne $guestDirectory -or [string]$mkdir.guestValue.computerName -cne $script:GuestComputerName) {
        throw 'Could not create a new unique run-scoped guest staging directory.'
    }
    $relativeDriver = "$relativeDirectory\MarkdownDeferredTouchSandboxProbe.ps1"
    $relativeShared = "$relativeDirectory\MarkdownCorrectnessSandboxProbe.ps1"
    $null = Assert-DeferredTouchEpoch -Phase 'before pushing the guest driver' -CaseDirectory $CaseDirectory
    $null = Invoke-DeferredTouchNative @('target', 'push', 'sandbox', [IO.Path]::GetFullPath($ProbePath), $relativeDriver) -CaseDirectory $CaseDirectory
    $null = Assert-DeferredTouchEpoch -Phase 'before pushing the shared identity helper' -CaseDirectory $CaseDirectory
    $null = Invoke-DeferredTouchNative @('target', 'push', 'sandbox', [IO.Path]::GetFullPath($SharedProbePath), $relativeShared) -CaseDirectory $CaseDirectory
    $null = Assert-DeferredTouchEpoch -Phase 'after pushing guest scripts' -CaseDirectory $CaseDirectory
    $expectedDriverHash = (Get-FileHash -LiteralPath $ProbePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $expectedSharedHash = (Get-FileHash -LiteralPath $SharedProbePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $verify = @"
`$ErrorActionPreference = 'Stop'
if ([Environment]::MachineName -cne $(ConvertTo-DeferredTouchPsLiteral $script:GuestComputerName)) { throw 'Guest machine identity changed while verifying staged files.' }
`$files = @(
    [pscustomobject]@{name='MarkdownDeferredTouchSandboxProbe.ps1';sha256=(Get-FileHash -LiteralPath $(ConvertTo-DeferredTouchPsLiteral $script:GuestDriverPath) -Algorithm SHA256).Hash.ToLowerInvariant()},
    [pscustomobject]@{name='MarkdownCorrectnessSandboxProbe.ps1';sha256=(Get-FileHash -LiteralPath $(ConvertTo-DeferredTouchPsLiteral $script:GuestSharedProbePath) -Algorithm SHA256).Hash.ToLowerInvariant()}
)
[Console]::Out.WriteLine((ConvertTo-Json -InputObject ([pscustomobject]@{computerName=[Environment]::MachineName;files=`$files}) -Compress))
"@
    $verified = Invoke-DeferredTouchGuestScript -Code $verify -CaseDirectory $CaseDirectory
    if ($verified.exitCode -ne 0 -or $null -eq $verified.guestValue -or [string]$verified.guestValue.computerName -cne $script:GuestComputerName) { throw 'Guest did not verify the staged helper scripts.' }
    foreach ($expectation in @(
        [pscustomobject]@{ name = 'MarkdownDeferredTouchSandboxProbe.ps1'; sha256 = $expectedDriverHash },
        [pscustomobject]@{ name = 'MarkdownCorrectnessSandboxProbe.ps1'; sha256 = $expectedSharedHash }
    )) {
        $matches = @($verified.guestValue.files | Where-Object { [string]$_.name -ceq $expectation.name })
        if ($matches.Count -ne 1 -or [string]$matches[0].sha256 -cne $expectation.sha256) { throw "Staged guest helper hash mismatch for '$($expectation.name)'." }
    }
    return [pscustomobject]@{ guestDirectory = $guestDirectory; driverPath = $script:GuestDriverPath; sharedProbePath = $script:GuestSharedProbePath; expectedHashes = @(@{name='MarkdownDeferredTouchSandboxProbe.ps1';sha256=$expectedDriverHash},@{name='MarkdownCorrectnessSandboxProbe.ps1';sha256=$expectedSharedHash}); guestReceipt = $verified.guestValue }
}

function New-DeferredTouchGuestLauncherCode {
    param([Parameter(Mandatory)]$OriginalIdentity)
    $exe = ConvertTo-DeferredTouchPsLiteral ([string]$script:OriginalExecutablePath)
    $guest = ConvertTo-DeferredTouchPsLiteral ([string]$script:GuestComputerName)
    $expectedHostNameLiteral = ConvertTo-DeferredTouchPsLiteral ([string]$script:HostComputerName)
    $expectedPid = [int]$OriginalIdentity.processId
    $expectedStart = [long]$OriginalIdentity.startTicks
    $expectedHash = ConvertTo-DeferredTouchPsLiteral ([string]$OriginalIdentity.exeSha256)
    return @"
`$ErrorActionPreference = 'Stop'
`$guest = $guest
`$expectedHostName = $expectedHostNameLiteral
`$exe = $exe
`$expectedPid = $expectedPid
`$expectedStart = $expectedStart
`$expectedHash = $expectedHash
function Get-ProcessReceipt([int]`$Id) {
    `$p = Get-Process -Id `$Id -ErrorAction Stop
    `$p.Refresh()
    `$path = [IO.Path]::GetFullPath([string]`$p.Path)
    `$start = `$p.StartTime.ToUniversalTime()
    `$hash = (Get-FileHash -LiteralPath `$path -Algorithm SHA256).Hash.ToLowerInvariant()
    return [pscustomobject]@{processId=`$p.Id;processName=`$p.ProcessName;executablePath=`$path;startTicks=`$start.Ticks;startTimeUtc=`$start.ToString('o');exeSha256=`$hash}
}
if ([Environment]::MachineName -cne `$guest -or [string]::IsNullOrWhiteSpace(`$expectedHostName) -or [string]::Equals(`$guest,`$expectedHostName,[StringComparison]::OrdinalIgnoreCase)) { throw 'Launcher is not executing in the exact Sandbox guest.' }
`$sourceBefore = Get-ProcessReceipt `$expectedPid
if (`$sourceBefore.processName -cne 'MarkdownRenderer.Sample' -or `$sourceBefore.executablePath -cne `$exe -or `$sourceBefore.startTicks -ne `$expectedStart -or `$sourceBefore.exeSha256 -cne `$expectedHash) { throw 'Original app identity changed before launching the probe.' }
`$fxr = @((Get-Process -Id `$expectedPid -ErrorAction Stop).Modules | Where-Object { `$_.ModuleName -ieq 'hostfxr.dll' } | ForEach-Object { [string]`$_.FileName })
if (`$fxr.Count -ne 1) { throw 'The original app must load exactly one hostfxr.dll; its runtime cannot be reused for the probe.' }
`$runtimeRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent `$fxr[0]) '..\..\..'))
# T1-012: the framework-dependent sample runs on winapp's private runtime, which a direct launch does not inherit.
`$env:DOTNET_ROOT = `$runtimeRoot
`$env:DOTNET_ROOT_X64 = `$runtimeRoot
# T1-017: winapp target exec closes its job when this launcher returns, terminating Start-Process descendants.
# Win32_Process.Create starts the probe outside that job; its environment block replaces the inherited one, so pass the full current block.
`$envBlock = [string[]]@(Get-ChildItem Env: | ForEach-Object { '{0}={1}' -f `$_.Name, `$_.Value })
`$startup = New-CimInstance -ClassName Win32_ProcessStartup -ClientOnly -Property @{ ShowWindow = [uint16]1; EnvironmentVariables = `$envBlock }
`$created = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{ CommandLine = ('"' + `$exe + '" --renderer-deferred-touch-probe'); CurrentDirectory = (Split-Path -Parent `$exe); ProcessStartupInformation = `$startup }
if ([int]`$created.ReturnValue -ne 0 -or [int]`$created.ProcessId -le 0) { throw "Win32_Process.Create failed with return value `$(`$created.ReturnValue)." }
`$childReceipt = Get-ProcessReceipt ([int]`$created.ProcessId)
if (`$childReceipt.processName -cne 'MarkdownRenderer.Sample' -or `$childReceipt.executablePath -cne `$exe -or `$childReceipt.exeSha256 -cne `$expectedHash -or `$childReceipt.processId -eq `$expectedPid) { throw 'Directly launched child did not retain the exact approved binary identity.' }
`$sourceAfter = Get-ProcessReceipt `$expectedPid
if (`$sourceAfter.processId -ne `$sourceBefore.processId -or `$sourceAfter.startTicks -ne `$sourceBefore.startTicks -or `$sourceAfter.executablePath -cne `$sourceBefore.executablePath -or `$sourceAfter.exeSha256 -cne `$sourceBefore.exeSha256) { throw 'Original app process was replaced or changed by direct launch.' }
`$result = [pscustomobject]@{schemaVersion=1;status='LAUNCHED';launchMethod='guest-Win32_Process-Create';argument='--renderer-deferred-touch-probe';guestComputerName=`$guest;hostComputerName=`$expectedHostName;sourceRetained=`$true;sourceBefore=`$sourceBefore;sourceAfter=`$sourceAfter;process=`$childReceipt}
[Console]::Out.WriteLine((ConvertTo-Json -InputObject `$result -Depth 8 -Compress))
"@
}

function Invoke-DeferredTouchProbeLaunch {
    param([Parameter(Mandatory)]$OriginalIdentity, [Parameter(Mandatory)][string]$CaseDirectory)
    $null = Assert-DeferredTouchOriginalApp -Phase 'immediately before direct guest launch' -CaseDirectory $CaseDirectory
    $code = New-DeferredTouchGuestLauncherCode -OriginalIdentity $OriginalIdentity
    $result = Invoke-DeferredTouchGuestScript -Code $code -CaseDirectory $CaseDirectory -AllowNonZero
    if ($null -eq $result.guestValue) { throw "Guest direct-launch receipt is missing or malformed (exit $($result.exitCode)): $($result.guestText)" }
    $identity = Assert-DeferredTouchLaunchReceipt -Receipt $result.guestValue -OriginalBefore $OriginalIdentity -OriginalAfter $result.guestValue.sourceAfter -GuestName $script:GuestComputerName -HostName $script:HostComputerName
    if ($result.exitCode -ne 0) { throw "Guest direct launch returned exit $($result.exitCode) despite a receipt: $($result.guestText)" }
    return [pscustomobject]@{ identity = $identity; receipt = $result.guestValue; commandEvidence = $result.evidence }
}

function Wait-DeferredTouchProbeWindow {
    param([Parameter(Mandatory)]$LaunchIdentity, [Parameter(Mandatory)]$OriginalIdentity, [Parameter(Mandatory)][string]$CaseDirectory)
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    do {
        $null = Assert-DeferredTouchOriginalApp -Phase 'probe window discovery' -CaseDirectory $CaseDirectory
        $windows = @((Invoke-DeferredTouchNative @('ui', 'list-windows', '--on', 'sandbox') -CaseDirectory $CaseDirectory -Json).value)
        $window = Find-DeferredTouchProbeWindow -Windows $windows -LaunchIdentity $LaunchIdentity -OriginalIdentity $OriginalIdentity
        if ($null -ne $window) { return $window }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The new process did not expose a unique exact-title deferred-touch window within 45 seconds.'
}

function Get-DeferredTouchProcessState {
    param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][string]$CaseDirectory)
    $code = @"
`$ErrorActionPreference = 'Stop'
`$id = $ProcessId
`$p = Get-Process -Id `$id -ErrorAction SilentlyContinue
if (`$null -eq `$p) { `$state = [pscustomobject]@{mode='ProcessState';processId=`$id;exists=`$false} }
else {
    `$p.Refresh()
    `$start = `$p.StartTime.ToUniversalTime()
    `$state = [pscustomobject]@{mode='ProcessState';processId=`$p.Id;exists=`$true;processName=`$p.ProcessName;executablePath=[IO.Path]::GetFullPath([string]`$p.Path);startTicks=`$start.Ticks;startTimeUtc=`$start.ToString('o')}
}
[Console]::Out.WriteLine((ConvertTo-Json -InputObject `$state -Compress))
"@
    $code = $code.Replace('$ProcessId', [string]$ProcessId)
    $result = Invoke-DeferredTouchGuestScript -Code $code -CaseDirectory $CaseDirectory
    if ($result.exitCode -ne 0 -or $null -eq $result.guestValue) { throw 'Guest process-state query failed or returned malformed JSON.' }
    return $result.guestValue
}

function Close-DeferredTouchProbeNormally {
    param([Parameter(Mandatory)]$Window, [Parameter(Mandatory)]$Identity, [Parameter(Mandatory)][string]$CaseDirectory)
    $null = Assert-DeferredTouchEpoch -Phase 'before normal-close' -CaseDirectory $CaseDirectory
    $null = Assert-DeferredTouchOriginalApp -Phase 'before normal-close of probe' -CaseDirectory $CaseDirectory
    $currentWindows = @((Invoke-DeferredTouchNative @('ui', 'list-windows', '--on', 'sandbox') -CaseDirectory $CaseDirectory -Json).value)
    $matches = @($currentWindows | Where-Object {
        [int](Get-DeferredTouchProperty $_ 'processId') -eq [int]$Window.processId -and
        [long](Get-DeferredTouchProperty $_ 'hwnd') -eq [long]$Window.hwnd -and
        [string](Get-DeferredTouchProperty $_ 'processName') -ceq 'MarkdownRenderer.Sample' -and
        [string](Get-DeferredTouchProperty $_ 'title') -ceq 'MarkdownRenderer deferred-touch probe'
    })
    if ($matches.Count -ne 1) { throw 'Refusing close input because exact target epoch/PID/HWND/title is not uniquely present.' }
    $currentIdentity = Invoke-DeferredTouchGuestIdentity -Mode Identity -ProcessId ([int]$Window.processId) -Hwnd ([long]$Window.hwnd) -StartTicks ([long]$Identity.startTicks) -CaseDirectory $CaseDirectory
    $null = Assert-DeferredTouchProcessIdentity -Actual $currentIdentity -Expected $Identity
    $payload = Get-DeferredTouchPayload -ProcessId ([int]$Window.processId) -Hwnd ([long]$Window.hwnd) -StartTicks ([long]$Identity.startTicks) -CaseDirectory $CaseDirectory
    $null = Assert-DeferredTouchEpoch -Phase 'immediately before normal close control' -CaseDirectory $CaseDirectory
    $invoke = Invoke-DeferredTouchNative @('ui', 'invoke', 'DeferredTouchCloseProbe', '--action', 'invoke', '--on', 'sandbox', '-a', [string]$Window.processId) -CaseDirectory $CaseDirectory -Json
    if ($invoke.exitCode -ne 0) { throw 'The exact probe close control invocation failed.' }

    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $lastState = $null
    do {
        $null = Assert-DeferredTouchEpoch -Phase 'verifying normal close' -CaseDirectory $CaseDirectory
        $windows = @((Invoke-DeferredTouchNative @('ui', 'list-windows', '--on', 'sandbox') -CaseDirectory $CaseDirectory -Json).value)
        $sameWindow = @($windows | Where-Object {
            [int](Get-DeferredTouchProperty $_ 'processId') -eq [int]$Window.processId -and
            [long](Get-DeferredTouchProperty $_ 'hwnd') -eq [long]$Window.hwnd -and
            [string](Get-DeferredTouchProperty $_ 'title') -ceq 'MarkdownRenderer deferred-touch probe'
        })
        if ($sameWindow.Count -gt 0) { Start-Sleep -Milliseconds 200; continue }
        $lastState = Get-DeferredTouchProcessState -ProcessId ([int]$Window.processId) -CaseDirectory $CaseDirectory
        try { $null = Assert-DeferredTouchExactProcessExited -ProcessState $lastState -ExpectedIdentity $Identity; break }
        catch { Start-Sleep -Milliseconds 200 }
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $lastState) { throw 'Normal close did not produce a process-state receipt.' }
    $null = Assert-DeferredTouchExactProcessExited -ProcessState $lastState -ExpectedIdentity $Identity
    $remainingTitle = @($windows | Where-Object { [string](Get-DeferredTouchProperty $_ 'title') -ceq 'MarkdownRenderer deferred-touch probe' })
    if ($remainingTitle.Count -gt 0) { throw 'A deferred-touch probe window remains after normal close.' }
    $mainAfter = Assert-DeferredTouchOriginalApp -Phase 'after normal-close cleanup' -CaseDirectory $CaseDirectory
    return [pscustomobject]@{ status = 'PASS'; method = 'UIA DeferredTouchCloseProbe invoke'; exactWindowGone = $true; exactProcessExited = $true; processState = $lastState; originalAppAfter = $mainAfter; payloadBeforeClose = $payload }
}

function Invoke-DeferredTouchCase {
    param([Parameter(Mandatory)]$Case, [Parameter(Mandatory)][string]$CaseDirectory)
    $started = [DateTime]::UtcNow.ToString('o')
    $result = [ordered]@{
        id = [string]$Case.id; scenario = [string]$Case.scenario; viewport = [string]$Case.viewport
        status = 'UNVERIFIED'; startedUtc = $started; finishedUtc = $null
        targetEpoch = $script:Prior.targetEpoch; workflowId = $script:WorkflowId
        originalAppBefore = $null; launch = $null; probeWindow = $null; probeIdentity = $null; payload = $null
        driverInvoked = $false; driverReportValidated = $false; driverExitCode = $null; driverReport = $null; driverResultSource = $null; driverCommandEvidence = $null
        cleanup = [pscustomobject]@{ status = 'NOT_STARTED'; error = $null }
        failure = $null
    }
    $launchAttempted = $false
    $driverInvoked = $false
    $driverReportValidated = $false
    $probeWindow = $null
    $probeIdentity = $null
    $caseDirectoryExists = $false
    try {
        if (Test-Path -LiteralPath $CaseDirectory) { throw "Case evidence directory already exists; refusing overwrite: $CaseDirectory" }
        [IO.Directory]::CreateDirectory($CaseDirectory) | Out-Null
        $caseDirectoryExists = $true
        $result.originalAppBefore = Assert-DeferredTouchOriginalApp -Phase "before $($Case.id)" -CaseDirectory $CaseDirectory
        $existingWindows = @((Invoke-DeferredTouchNative @('ui', 'list-windows', '--on', 'sandbox') -CaseDirectory $CaseDirectory -Json).value)
        $existingProbeTitles = @($existingWindows | Where-Object { [string](Get-DeferredTouchProperty $_ 'title') -ceq 'MarkdownRenderer deferred-touch probe' })
        if ($existingProbeTitles.Count -ne 0) { throw 'A deferred-touch probe window already exists; no new launch was attempted.' }
        $launchAttempted = $true
        $launch = Invoke-DeferredTouchProbeLaunch -OriginalIdentity $script:OriginalIdentity -CaseDirectory $CaseDirectory
        $result.launch = $launch.receipt
        $probeWindow = Wait-DeferredTouchProbeWindow -LaunchIdentity $launch.identity -OriginalIdentity $script:OriginalIdentity -CaseDirectory $CaseDirectory
        $result.probeWindow = $probeWindow
        $identityReceipt = Invoke-DeferredTouchGuestIdentity -Mode Identity -ProcessId ([int]$probeWindow.processId) -Hwnd ([long]$probeWindow.hwnd) -StartTicks ([long]$launch.identity.startTicks) -CaseDirectory $CaseDirectory
        $expectedIdentity = [pscustomobject]@{
            processId = $launch.identity.processId; processName = 'MarkdownRenderer.Sample'; executablePath = $script:OriginalExecutablePath
            startTicks = $launch.identity.startTicks; exeSha256 = [string]$script:Prior.buildBinary.sha256
            hwnd = [long]$probeWindow.hwnd; title = 'MarkdownRenderer deferred-touch probe'
        }
        $probeIdentity = Assert-DeferredTouchProcessIdentity -Actual $identityReceipt -Expected $expectedIdentity
        $result.probeIdentity = $probeIdentity
        $payload = Get-DeferredTouchPayload -ProcessId ([int]$probeWindow.processId) -Hwnd ([long]$probeWindow.hwnd) -StartTicks ([long]$probeIdentity.startTicks) -CaseDirectory $CaseDirectory
        $result.payload = $payload
        $result.status = 'UNVERIFIED'
        $null = Assert-DeferredTouchOriginalApp -Phase "immediately before guest driver $($Case.id)" -CaseDirectory $CaseDirectory
        $latestProbe = Invoke-DeferredTouchGuestIdentity -Mode Identity -ProcessId ([int]$probeWindow.processId) -Hwnd ([long]$probeWindow.hwnd) -StartTicks ([long]$probeIdentity.startTicks) -CaseDirectory $CaseDirectory
        $null = Assert-DeferredTouchProcessIdentity -Actual $latestProbe -Expected ([pscustomobject]@{
            processId = $probeIdentity.processId; processName = $probeIdentity.processName; executablePath = $probeIdentity.executablePath
            startTicks = $probeIdentity.startTicks; exeSha256 = $probeIdentity.exeSha256; hwnd = $probeWindow.hwnd; title = $probeWindow.title
        })
        $driverArgs = @('target', 'exec', 'sandbox', '--json', '--', 'powershell.exe', '-WindowStyle', 'Hidden', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-STA', '-File', $script:GuestDriverPath,
            '-Scenario', [string]$Case.scenario, '-Viewport', [string]$Case.viewport,
            '-ProcessId', [string]$probeWindow.processId, '-WindowHandle', [string]$probeWindow.hwnd,
            '-ExpectedExeSha256', [string]$script:Prior.buildBinary.sha256,
            '-ExpectedSampleDllSha256', [string]$script:PayloadByName['MarkdownRenderer.Sample.dll'].sha256,
            '-ExpectedRendererDllSha256', [string]$script:PayloadByName['MarkdownRenderer.dll'].sha256,
            '-ExpectedGuestComputerName', $script:GuestComputerName,
            '-ExpectedHostComputerName', $script:HostComputerName,
            '-ExpectedStartTicks', [string]$probeIdentity.startTicks)
        $driver = $null
        $driverInvoked = $true
        $result.driverInvoked = $true
        try { $driver = Invoke-DeferredTouchNative -Arguments $driverArgs -CaseDirectory $CaseDirectory -Json -AllowNonZero }
        catch {
            $result.status = 'UNVERIFIED'
            $result.failure = 'Guest driver command did not return parseable completion evidence: ' + $_.Exception.ToString()
        }
        if ($null -ne $driver) {
            $result.driverExitCode = [int]$driver.exitCode
            try {
                $parsedDriver = ConvertFrom-DeferredTouchNativeGuestResult -NativeResult $driver
                $result.driverReport = $parsedDriver.value
                $result.driverResultSource = $parsedDriver.source
            }
            catch {
                $result.status = 'UNVERIFIED'
                $result.failure = 'Guest driver stdout could not be parsed as a direct or bounded-wrapper JSON object: ' + $_.Exception.ToString()
            }
            $result.driverCommandEvidence = $driver.evidence
            try {
                if ($null -eq $result.driverReport) { throw 'Guest deferred-touch output is missing the evidence JSON object.' }
                $null = Assert-DeferredTouchDriverReport -Report $result.driverReport -Scenario ([string]$Case.scenario) -Viewport ([string]$Case.viewport) `
                    -ExpectedIdentity $expectedIdentity -ExpectedPayloads @($script:Prior.hostPayloads | Where-Object { [bool](Get-DeferredTouchProperty $_ 'required') -or [string](Get-DeferredTouchProperty $_ 'name') -eq 'MarkdownRenderer.Sample.deps.json' }) `
                    -ExpectedGuestName $script:GuestComputerName -ExpectedDirectory (Split-Path -Parent $script:OriginalExecutablePath)
                $driverReportValidated = $true
                $result.driverReportValidated = $true
                $result.status = Get-DeferredTouchDriverStatus -ExitCode ([int]$driver.exitCode) -Report $result.driverReport
                if ($result.status -eq 'UNVERIFIED') { $result.failure = 'Guest driver exit/report status was absent, malformed, or inconsistent.' }
                elseif ($result.status -eq 'FAIL') { $result.failure = [string](Get-DeferredTouchProperty $result.driverReport 'failure') }
            }
            catch {
                $result.status = 'UNVERIFIED'
                $result.failure = 'Guest driver evidence did not validate against the exact launched app: ' + $_.Exception.ToString()
            }
        }
    }
    catch {
        $result.status = 'UNVERIFIED'
        $result.failure = $_.Exception.ToString()
        if (-not $launchAttempted) { $script:StopAfterCase = $true }
    }
    finally {
        if ($launchAttempted) {
            try {
                if ($null -eq $probeWindow -or $null -eq $probeIdentity) {
                    throw 'A launch was attempted but exact new PID/HWND/start/path identity was not fully proven; cleanup input is unsafe.'
                }
                $closeSafety = Get-DeferredTouchNormalCloseSafety -DriverInvoked $driverInvoked -ReportValidated $driverReportValidated -Report $result.driverReport
                if (-not $closeSafety.safe) {
                    $result.cleanup = [pscustomobject]@{ status = 'UNVERIFIED'; method = $null; error = [string]$closeSafety.reason; closeInputWithheld = $true }
                    if ($result.status -eq 'PASS') { $result.status = 'UNVERIFIED'; $result.failure = [string]$closeSafety.reason }
                    $script:StopAfterCase = $true
                }
                else {
                    $result.cleanup = Close-DeferredTouchProbeNormally -Window $probeWindow -Identity $probeIdentity -CaseDirectory $CaseDirectory
                }
            }
            catch {
                $result.cleanup = [pscustomobject]@{ status = 'UNVERIFIED'; method = $null; error = $_.Exception.ToString() }
                if ($result.status -eq 'PASS') { $result.status = 'UNVERIFIED'; $result.failure = 'Driver passed, but normal-close cleanup could not be proven.' }
            }
        }
        if ($launchAttempted -and [string]$result.cleanup.status -cne 'PASS') { $script:StopAfterCase = $true }
        if ($caseDirectoryExists) {
            $result.finishedUtc = [DateTime]::UtcNow.ToString('o')
            try { Write-DeferredTouchJsonExclusive -Path (Join-Path $CaseDirectory 'case.json') -Value ([pscustomobject]$result) }
            catch {
                $result.status = 'UNVERIFIED'
                $result.failure = 'Case artifact write failed: ' + $_.Exception.Message
                $script:StopAfterCase = $true
            }
        }
    }
    if ($launchAttempted -and [string]$result.cleanup.status -cne 'PASS') { $script:StopAfterCase = $true }
    return [pscustomobject]$result
}

function Write-DeferredTouchRunReport {
    param([Parameter(Mandatory)][string]$RunDirectory, [Parameter(Mandatory)][object[]]$Results)
    $counts = Get-DeferredTouchCounts -Results $Results -SetupFailure $script:SetupFailure
    $report = [ordered]@{
        schemaVersion = 1; objective = 'MarkdownRenderer Phase 1 deferred touch correctness on the exact already-deployed Sandbox app'
        runId = $script:RunId; priorRunJson = $script:PriorRunPath; target = 'sandbox'; targetEpoch = $script:Prior.targetEpoch
        workflowId = $script:WorkflowId; originalApp = $script:OriginalIdentity; hostPayloads = @($script:Prior.hostPayloads)
        setupFailure = $script:SetupFailure; guestPayloadStatus = $script:InitialPayloadStatus; stagedScripts = $script:StagedScripts; casePlan = @(Get-DeferredTouchCasePlan)
        resultCounts = $counts; results = @($Results); complete = ($counts.fail -eq 0 -and $counts.unverified -eq 0); generatedUtc = [DateTime]::UtcNow.ToString('o')
    }
    Write-DeferredTouchJsonExclusive -Path (Join-Path $RunDirectory 'results.json') -Value $report
    Write-DeferredTouchJsonExclusive -Path (Join-Path $RunDirectory 'run.json') -Value ([ordered]@{
        runId = $script:RunId; priorRunJson = $script:PriorRunPath; target = 'sandbox'; targetEpoch = $script:Prior.targetEpoch
        workflowId = $script:WorkflowId; originalApp = $script:OriginalIdentity; hostPayloads = @($script:Prior.hostPayloads)
        setupFailure = $script:SetupFailure; guestComputerName = $script:GuestComputerName; hostComputerName = $script:HostComputerName
        stagedScripts = $script:StagedScripts; generatedUtc = [DateTime]::UtcNow.ToString('o')
    })
    return $counts
}

if (-not $RunApprovedSandboxCases) {
    throw 'No Sandbox actions were performed. To explicitly run the approved six-case batch, pass -RunApprovedSandboxCases after confirming the current Sandbox epoch and user approval.'
}

$script:PriorRunPath = [IO.Path]::GetFullPath($PriorRunJson)
$script:PriorReceipt = Get-Content -LiteralPath $script:PriorRunPath -Raw | ConvertFrom-Json
$script:Prior = Assert-DeferredTouchPriorRun -Receipt $script:PriorReceipt
$script:OutputRootPath = [IO.Path]::GetFullPath($OutputRoot)
$script:RunId = 'deferred-touch-{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'), [guid]::NewGuid().ToString('N')
$script:RunDirectory = Join-Path $script:OutputRootPath $script:RunId
$script:WorkflowId = "markdown-deferred-touch-$([guid]::NewGuid().ToString('N'))"
$script:PreviousWorkflowId = $env:WINAPP_UI_WORKFLOW_ID
$script:CommandIndex = 0
$script:OriginalStartTicks = 0L
$script:OriginalExecutablePath = ''
$script:HostComputerName = [Environment]::MachineName
$script:GuestComputerName = ''
$script:GuestDriverPath = ''
$script:GuestSharedProbePath = ''
$script:PayloadByName = @{}
$script:StagedScripts = $null
$script:InitialPayloadStatus = $null
$script:OriginalIdentity = $null
$script:SetupFailure = $null
$script:UiWorkflowStarted = $false
$script:StopAfterCase = $false
$script:Results = [Collections.Generic.List[object]]::new()
$plan = @(Get-DeferredTouchCasePlan)
foreach ($case in $plan) {
    $script:Results.Add([pscustomobject]@{ id = $case.id; scenario = $case.scenario; viewport = $case.viewport; status = 'UNVERIFIED'; requirement = 'Not run because setup or a prior case did not complete safely.'; cleanup = [pscustomobject]@{ status = 'NOT_STARTED' } })
}

try {
    if (Test-Path -LiteralPath $script:RunDirectory) { throw "Unique run output already exists; refusing overwrite: $script:RunDirectory" }
    [IO.Directory]::CreateDirectory($script:RunDirectory) | Out-Null
    if (-not (Test-Path -LiteralPath $ProbePath -PathType Leaf)) { throw "Guest deferred-touch driver is missing: $ProbePath" }
    if (-not (Test-Path -LiteralPath $SharedProbePath -PathType Leaf)) { throw "Shared Sandbox identity helper is missing: $SharedProbePath" }
    $script:ProbePath = [IO.Path]::GetFullPath($ProbePath)
    $script:SharedProbePath = [IO.Path]::GetFullPath($SharedProbePath)
    $revalidatedPrior = Assert-DeferredTouchPriorRun -Receipt $script:PriorReceipt
    $script:Prior = $revalidatedPrior
    foreach ($file in $script:Prior.hostPayloads) {
        $path = [string](Get-DeferredTouchProperty $file 'path')
        $name = [string](Get-DeferredTouchProperty $file 'name')
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Captured host build payload is missing: $path" }
        $item = Get-Item -LiteralPath $path
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        if ([long]$item.Length -ne [long]$file.length -or $hash -cne [string]$file.sha256) { throw "Current host payload '$name' differs from the prior run's exact build receipt." }
        $script:PayloadByName[$name] = [pscustomobject]@{ name = $name; path = $path; length = [long]$item.Length; sha256 = $hash; required = [bool]$file.required }
    }
    if (-not $script:PayloadByName.ContainsKey('MarkdownRenderer.Sample.exe') -or -not $script:PayloadByName.ContainsKey('MarkdownRenderer.Sample.dll') -or -not $script:PayloadByName.ContainsKey('MarkdownRenderer.dll')) {
        throw 'Current host build receipt is missing one of the required exact sample/renderer payloads.'
    }
    $exe = $script:PayloadByName['MarkdownRenderer.Sample.exe']
    if ([long]$exe.length -ne [long]$script:Prior.buildBinary.length -or [string]$exe.sha256 -cne [string]$script:Prior.buildBinary.sha256 -or
        -not [string]::Equals([IO.Path]::GetFullPath([string]$exe.path), [IO.Path]::GetFullPath([string]$script:Prior.buildBinary.path), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'buildBinary does not match the exact host sample executable payload.'
    }
    $env:WINAPP_UI_WORKFLOW_ID = $script:WorkflowId
    $script:UiWorkflowStarted = $true

    $initialDirectory = Join-Path $script:RunDirectory 'setup'
    [IO.Directory]::CreateDirectory($initialDirectory) | Out-Null
    $snapshot = (Invoke-DeferredTouchNative @('target', 'snapshot', 'sandbox') -CaseDirectory $initialDirectory -Json).value
    $null = Assert-DeferredTouchSnapshot -Snapshot $snapshot -ExpectedEpoch $script:Prior.targetEpoch
    $windowResult = Invoke-DeferredTouchNative @('ui', 'list-windows', '--on', 'sandbox') -CaseDirectory $initialDirectory -Json
    $originalWindows = @($windowResult.value | Where-Object {
        [int](Get-DeferredTouchProperty $_ 'processId') -eq $script:Prior.appProcessId -and
        [long](Get-DeferredTouchProperty $_ 'hwnd') -eq $script:Prior.appHwnd -and
        [string](Get-DeferredTouchProperty $_ 'processName') -ceq $script:Prior.appProcessName -and
        [string](Get-DeferredTouchProperty $_ 'title') -ceq $script:Prior.appTitle
    })
    if ($originalWindows.Count -ne 1) { throw 'The exact original sample window from the prior receipt is not uniquely present in the same Sandbox epoch.' }
    $preexistingProbe = @($windowResult.value | Where-Object { [string](Get-DeferredTouchProperty $_ 'title') -ceq 'MarkdownRenderer deferred-touch probe' })
    if ($preexistingProbe.Count -ne 0) { throw 'A deferred-touch probe window exists before this batch; refusing to adopt or close an unowned process.' }

    $script:GuestComputerName = Get-DeferredTouchGuestComputerName -CaseDirectory $initialDirectory
    # The identity and payload probes below run the staged shared probe, so it
    # must be staged first; otherwise its guest path is still empty.
    $script:StagedScripts = Stage-DeferredTouchGuestScripts -Snapshot $snapshot -RunId $script:RunId -CaseDirectory $initialDirectory
    $firstIdentity = Invoke-DeferredTouchGuestIdentity -Mode Identity -ProcessId $script:Prior.appProcessId -Hwnd $script:Prior.appHwnd -CaseDirectory $initialDirectory
    if ([string]$firstIdentity.computerName -cne $script:GuestComputerName -or
        [string]$firstIdentity.exeSha256 -cne [string]$script:Prior.buildBinary.sha256 -or
        [string]$firstIdentity.processName -cne [string]$script:Prior.appProcessName -or
        [IO.Path]::GetFileName([string]$firstIdentity.exePath) -cne 'MarkdownRenderer.Sample.exe' -or
        [string]$firstIdentity.title -cne [string]$script:Prior.appTitle -or
        [long]$firstIdentity.hwnd -ne [long]$script:Prior.appHwnd) {
        throw 'Live original app identity does not match the exact prior guest PID/HWND/title/executable receipt.'
    }
    $script:OriginalStartTicks = [long]$firstIdentity.startTicks
    if ($script:OriginalStartTicks -le 0) { throw 'Live original process start tick identity is invalid.' }
    $script:OriginalExecutablePath = [IO.Path]::GetFullPath([string]$firstIdentity.exePath)
    if (-not [string]::Equals($script:OriginalExecutablePath, [string]$script:Prior.guestExecutablePath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Live original app executable path differs from the prior guest deployment receipt.'
    }
    $script:OriginalIdentity = [pscustomobject]@{
        processId = [int]$firstIdentity.processId; processName = [string]$firstIdentity.processName; executablePath = $script:OriginalExecutablePath
        startTicks = $script:OriginalStartTicks; exeSha256 = [string]$firstIdentity.exeSha256
        hwnd = [long]$firstIdentity.hwnd; title = [string]$firstIdentity.title
    }
    $originalPayload = Get-DeferredTouchPayload -ProcessId $script:Prior.appProcessId -Hwnd $script:Prior.appHwnd -StartTicks $script:OriginalStartTicks -CaseDirectory $initialDirectory
    $script:InitialPayloadStatus = $originalPayload

    for ($index = 0; $index -lt $plan.Count; $index++) {
        if ($script:StopAfterCase) { break }
        $caseDirectory = Join-Path $script:RunDirectory ('cases\{0:D2}-{1}' -f ($index + 1), $plan[$index].id)
        $result = Invoke-DeferredTouchCase -Case $plan[$index] -CaseDirectory $caseDirectory
        $script:Results[$index] = $result
        if ($result.status -eq 'FAIL' -and $result.cleanup.status -eq 'PASS') {
            # The app and Sandbox are still proven safe; proceed to independent cases once each is freshly launched.
        }
    }
}
catch {
    $setupError = $_.Exception.ToString() + [Environment]::NewLine + $_.ScriptStackTrace
    $script:SetupFailure = [pscustomobject]@{ status = 'UNVERIFIED'; requirement = 'Preserve the original app, exact Sandbox epoch, and deployed binary identity before any probe.'; failure = $setupError; recordedUtc = [DateTime]::UtcNow.ToString('o') }
}
finally {
    if ($script:UiWorkflowStarted) {
        try {
            $yieldDirectory = if (Test-Path -LiteralPath $script:RunDirectory -PathType Container) { $script:RunDirectory } else { $env:TEMP }
            $yieldSnapshot = (Invoke-DeferredTouchNative @('target', 'snapshot', 'sandbox') -CaseDirectory $yieldDirectory -Json).value
            $null = Assert-DeferredTouchSnapshot -Snapshot $yieldSnapshot -ExpectedEpoch $script:Prior.targetEpoch
            $null = Invoke-DeferredTouchNative @('ui', 'yield', '--on', 'sandbox') -CaseDirectory $yieldDirectory
        }
        catch {
            $script:Results.Add([pscustomobject]@{ id = 'WORKFLOW'; scenario = $null; viewport = $null; status = 'FAIL'; requirement = 'Yield the single run-scoped Sandbox workflow.'; failure = $_.Exception.ToString(); cleanup = [pscustomobject]@{ status = 'FAIL' } })
        }
        $env:WINAPP_UI_WORKFLOW_ID = $script:PreviousWorkflowId
    }
}

if (-not (Test-Path -LiteralPath $script:RunDirectory -PathType Container)) {
    $setupDetail = Get-Variable -Name SetupFailure -Scope Script -ValueOnly -ErrorAction SilentlyContinue
    throw "No output directory was created; no run report can be written. Setup failure: $(if ($null -ne $setupDetail) { $setupDetail.failure } else { 'none recorded' })"
}
$counts = Write-DeferredTouchRunReport -RunDirectory $script:RunDirectory -Results @($script:Results.ToArray())
$counts | Format-List
Write-Host "Deferred-touch Sandbox evidence: $script:RunDirectory"
if ($counts.fail -gt 0) { exit 1 }
if ($counts.unverified -gt 0) { exit 2 }
exit 0
