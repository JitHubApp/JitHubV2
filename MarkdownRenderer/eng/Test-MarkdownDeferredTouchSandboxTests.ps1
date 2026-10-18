[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$hostScriptPath = Join-Path $PSScriptRoot 'Invoke-MarkdownDeferredTouchSandboxTests.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($hostScriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw "Deferred-touch host orchestrator does not parse: $(($parseErrors | ForEach-Object { $_.Message }) -join '; ')"
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

function Get-FunctionAst {
    param([Parameter(Mandatory)][string]$Name)
    $functionAst = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name }, $true)
    if ($null -eq $functionAst) { throw "Host orchestrator is missing function '$Name'." }
    return $functionAst
}

foreach ($name in @(
    'Get-DeferredTouchProperty', 'Assert-DeferredTouchPriorRun', 'Assert-DeferredTouchSnapshot',
    'Assert-DeferredTouchPayloadMatch', 'Assert-DeferredTouchProcessIdentity', 'Assert-DeferredTouchLaunchReceipt',
    'Find-DeferredTouchProbeWindow', 'Get-DeferredTouchDriverStatus', 'Assert-DeferredTouchDriverReport', 'Get-DeferredTouchNormalCloseSafety',
    'Assert-DeferredTouchExactProcessExited', 'Get-DeferredTouchCasePlan', 'Get-DeferredTouchCounts',
    'ConvertFrom-DeferredTouchJsonObjectText', 'ConvertFrom-DeferredTouchNativeGuestResult', 'Assert-DeferredTouchGuestIdentityShape', 'ConvertTo-DeferredTouchCanonicalProcessIdentity', 'Invoke-DeferredTouchGuestIdentity',
    'ConvertTo-DeferredTouchPsLiteral', 'New-DeferredTouchGuestLauncherCode'
)) {
    . ([scriptblock]::Create((Get-FunctionAst $name).Extent.Text))
}

function New-TestPayload {
    param([string]$Name, [string]$Hash, [long]$Length = 128, [string]$Path = '')
    if ([string]::IsNullOrEmpty($Path)) { $Path = "C:\WinApp\deployments\fixture\$Name" }
    return [pscustomobject]@{ name = $Name; path = $Path; length = $Length; sha256 = $Hash; required = $true }
}

function New-TestPriorReceipt {
    $exeHash = 'a' * 64
    $sampleHash = 'b' * 64
    $rendererHash = 'c' * 64
    $hostFiles = @(
        (New-TestPayload 'MarkdownRenderer.Sample.exe' $exeHash 100 'E:\build\MarkdownRenderer.Sample.exe'),
        (New-TestPayload 'MarkdownRenderer.Sample.dll' $sampleHash 200 'E:\build\MarkdownRenderer.Sample.dll'),
        (New-TestPayload 'MarkdownRenderer.dll' $rendererHash 300 'E:\build\MarkdownRenderer.dll')
    )
    $guestFiles = @(
        (New-TestPayload 'MarkdownRenderer.Sample.exe' $exeHash 100),
        (New-TestPayload 'MarkdownRenderer.Sample.dll' $sampleHash 200),
        (New-TestPayload 'MarkdownRenderer.dll' $rendererHash 300)
    )
    return [pscustomobject]@{
        target = 'sandbox'; targetEpoch = 'guest-guid:session-guid'; architecture = 'x64'
        app = [pscustomobject]@{ target = 'sandbox'; epoch = 'guest-guid:session-guid'; processId = 501; hwnd = 8801; processName = 'MarkdownRenderer.Sample'; title = 'MarkdownRenderer — Feature Showcase' }
        buildBinary = [pscustomobject]@{ path = 'E:\build\MarkdownRenderer.Sample.exe'; sha256 = $exeHash; length = 100 }
        payloadIdentity = [pscustomobject]@{ status = 'MATCH'; guestProcessId = 501; guestExecutablePath = 'C:\WinApp\deployments\fixture\MarkdownRenderer.Sample.exe'; host = $hostFiles; guest = $guestFiles }
    }
}

function New-TestIdentity {
    param([int]$ProcessId = 612, [long]$StartTicks = 639220000000000000, [string]$Path = 'C:\WinApp\deployments\fixture\MarkdownRenderer.Sample.exe', [string]$Hash = ('a' * 64), [long]$Hwnd = 0, [string]$Title = '')
    return [pscustomobject]@{
        processId = $ProcessId; processName = 'MarkdownRenderer.Sample'; executablePath = $Path
        startTicks = $StartTicks; startTimeUtc = '2026-10-13T03:00:00.0000000Z'; exeSha256 = $Hash
        hwnd = $Hwnd; title = $Title
    }
}

function Invoke-DeferredTouchNative {
    param([string[]]$Arguments, [string]$CaseDirectory, [switch]$Json, [switch]$AllowNonZero)
    if ($null -eq $script:DeferredTouchNativeFixture) { throw 'The offline target-exec fixture was not configured.' }
    return $script:DeferredTouchNativeFixture
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

Invoke-Test 'prior receipt accepts exact deployed Sandbox epoch and three payload identities' {
    $validated = Assert-DeferredTouchPriorRun -Receipt (New-TestPriorReceipt)
    Assert-Condition ($validated.appProcessId -eq 501 -and $validated.appHwnd -eq 8801) 'Prior receipt lost the exact original app identity.'
}
Invoke-Test 'prior receipt rejects mismatched target epoch' {
    $r = New-TestPriorReceipt; $r.app.epoch = 'different-guest'
    Assert-Throws { Assert-DeferredTouchPriorRun -Receipt $r } 'app epoch mismatch'
}
Invoke-Test 'prior receipt rejects failed guest payload comparison' {
    $r = New-TestPriorReceipt; $r.payloadIdentity.guest[2].sha256 = 'd' * 64
    Assert-Throws { Assert-DeferredTouchPriorRun -Receipt $r } 'renderer payload mismatch'
}
Invoke-Test 'prior receipt rejects invalid original PID' {
    $r = New-TestPriorReceipt; $r.app.processId = 0
    Assert-Throws { Assert-DeferredTouchPriorRun -Receipt $r } 'invalid app PID'
}

Invoke-Test 'snapshot accepts only same running x64 input/capture epoch' {
    $snapshot = [pscustomobject]@{ running = $true; executionTarget = [pscustomobject]@{ epoch = 'epoch-1' }; capabilities = [pscustomobject]@{ architecture = 'x64' }; desktop = [pscustomobject]@{ effectiveInputReady = $true; effectiveCaptureReady = $true } }
    Assert-Condition ((Assert-DeferredTouchSnapshot -Snapshot $snapshot -ExpectedEpoch 'epoch-1') -ceq 'epoch-1') 'Snapshot did not return exact epoch.'
}
Invoke-Test 'snapshot rejects guest epoch replacement' {
    $snapshot = [pscustomobject]@{ running = $true; executionTarget = [pscustomobject]@{ epoch = 'epoch-2' }; capabilities = [pscustomobject]@{ architecture = 'x64' }; desktop = [pscustomobject]@{ effectiveInputReady = $true; effectiveCaptureReady = $true } }
    Assert-Throws { Assert-DeferredTouchSnapshot -Snapshot $snapshot -ExpectedEpoch 'epoch-1' } 'target epoch changed'
}
Invoke-Test 'snapshot rejects unavailable guest input' {
    $snapshot = [pscustomobject]@{ running = $true; executionTarget = [pscustomobject]@{ epoch = 'epoch-1' }; capabilities = [pscustomobject]@{ architecture = 'x64' }; desktop = [pscustomobject]@{ effectiveInputReady = $false; effectiveCaptureReady = $true } }
    Assert-Throws { Assert-DeferredTouchSnapshot -Snapshot $snapshot -ExpectedEpoch 'epoch-1' } 'input not ready'
}
Invoke-Test 'snapshot rejects string lookalikes instead of typed readiness booleans' {
    $snapshot = [pscustomobject]@{ running = 'false'; executionTarget = [pscustomobject]@{ epoch = 'epoch-1' }; capabilities = [pscustomobject]@{ architecture = 'x64' }; desktop = [pscustomobject]@{ effectiveInputReady = 'false'; effectiveCaptureReady = 'false' } }
    Assert-Throws { Assert-DeferredTouchSnapshot -Snapshot $snapshot -ExpectedEpoch 'epoch-1' } 'non-boolean readiness values'
}

$expectedPayloads = @(
    (New-TestPayload 'MarkdownRenderer.Sample.exe' ('a' * 64) 100),
    (New-TestPayload 'MarkdownRenderer.Sample.dll' ('b' * 64) 200),
    (New-TestPayload 'MarkdownRenderer.dll' ('c' * 64) 300)
)
$actualPayloads = @(
    (New-TestPayload 'MarkdownRenderer.Sample.exe' ('a' * 64) 100),
    (New-TestPayload 'MarkdownRenderer.Sample.dll' ('b' * 64) 200),
    (New-TestPayload 'MarkdownRenderer.dll' ('c' * 64) 300)
)
Invoke-Test 'payload matcher accepts same bytes only beside exact executable directory' {
    $result = Assert-DeferredTouchPayloadMatch -Expected $expectedPayloads -ActualFiles $actualPayloads -ExpectedDirectory 'C:\WinApp\deployments\fixture'
    Assert-Condition ($result.status -ceq 'MATCH' -and $result.files.Count -eq 3) 'Payload comparison did not capture all three required files.'
}
Invoke-Test 'payload matcher rejects changed renderer hash' {
    $changed = @($actualPayloads | ForEach-Object { $_.PSObject.Copy() }); $changed[2].sha256 = 'd' * 64
    Assert-Throws { Assert-DeferredTouchPayloadMatch -Expected $expectedPayloads -ActualFiles $changed -ExpectedDirectory 'C:\WinApp\deployments\fixture' } 'renderer hash changed'
}
Invoke-Test 'payload matcher rejects a file outside the exact guest executable directory' {
    $escaped = @($actualPayloads | ForEach-Object { $_.PSObject.Copy() }); $escaped[1].path = 'C:\WinApp\other\MarkdownRenderer.Sample.dll'
    Assert-Throws { Assert-DeferredTouchPayloadMatch -Expected $expectedPayloads -ActualFiles $escaped -ExpectedDirectory 'C:\WinApp\deployments\fixture' } 'payload directory changed'
}

$expectedIdentity = New-TestIdentity -Hwnd 9901 -Title 'MarkdownRenderer deferred-touch probe'
Invoke-Test 'process identity accepts exact PID/path/hash/start ticks' {
    $actual = New-TestIdentity -Hwnd 9901 -Title 'MarkdownRenderer deferred-touch probe'
    $null = Assert-DeferredTouchProcessIdentity -Actual $actual -Expected $expectedIdentity
}
Invoke-Test 'process identity rejects PID reuse with a new start time' {
    $actual = New-TestIdentity -ProcessId 612 -StartTicks 639220000000000001 -Hwnd 9901 -Title 'MarkdownRenderer deferred-touch probe'
    Assert-Throws { Assert-DeferredTouchProcessIdentity -Actual $actual -Expected $expectedIdentity } 'same PID reused'
}
Invoke-Test 'process identity rejects a different PID' {
    $actual = New-TestIdentity -ProcessId 613 -Hwnd 9901 -Title 'MarkdownRenderer deferred-touch probe'
    Assert-Throws { Assert-DeferredTouchProcessIdentity -Actual $actual -Expected $expectedIdentity } 'unexpected PID'
}
Invoke-Test 'process identity rejects a changed executable hash' {
    $actual = New-TestIdentity -Hash ('e' * 64) -Hwnd 9901 -Title 'MarkdownRenderer deferred-touch probe'
    Assert-Throws { Assert-DeferredTouchProcessIdentity -Actual $actual -Expected $expectedIdentity } 'binary hash changed'
}
Invoke-Test 'process identity rejects a changed executable path' {
    $actual = New-TestIdentity -Path 'C:\WinApp\other\MarkdownRenderer.Sample.exe' -Hwnd 9901 -Title 'MarkdownRenderer deferred-touch probe'
    Assert-Throws { Assert-DeferredTouchProcessIdentity -Actual $actual -Expected $expectedIdentity } 'deployment path changed'
}

$sourceProcess = New-TestIdentity -ProcessId 501 -StartTicks 639219000000000000 -Path 'C:\WinApp\deployments\fixture\MarkdownRenderer.Sample.exe'
$childProcess = New-TestIdentity -ProcessId 612 -StartTicks 639220000000000000
$launchReceipt = [pscustomobject]@{
    schemaVersion = 1; status = 'LAUNCHED'; launchMethod = 'guest-Start-Process'; argument = '--renderer-deferred-touch-probe'
    guestComputerName = 'SANDBOX-GUEST'; hostComputerName = 'HOST'; sourceRetained = $true
    sourceBefore = $sourceProcess; sourceAfter = $sourceProcess; process = $childProcess
}
Invoke-Test 'launch receipt proves distinct exact executable and retained source process' {
    $original = [pscustomobject]@{ processId = 501; processName = 'MarkdownRenderer.Sample'; executablePath = $sourceProcess.executablePath; startTicks = $sourceProcess.startTicks; exeSha256 = $sourceProcess.exeSha256; hwnd = 8801; title = 'MarkdownRenderer — Feature Showcase' }
    $probe = Assert-DeferredTouchLaunchReceipt -Receipt $launchReceipt -OriginalBefore $original -OriginalAfter $original -GuestName 'SANDBOX-GUEST' -HostName 'HOST'
    Assert-Condition ($probe.processId -eq 612 -and $probe.startTicks -eq $childProcess.startTicks) 'Launch receipt lost the exact child process identity.'
}
Invoke-Test 'launch receipt rejects original app replacement' {
    $original = [pscustomobject]@{ processId = 501; processName = 'MarkdownRenderer.Sample'; executablePath = $sourceProcess.executablePath; startTicks = $sourceProcess.startTicks; exeSha256 = $sourceProcess.exeSha256 }
    $changed = New-TestIdentity -ProcessId 501 -StartTicks ($sourceProcess.startTicks + 1) -Path $sourceProcess.executablePath
    Assert-Throws { Assert-DeferredTouchLaunchReceipt -Receipt $launchReceipt -OriginalBefore $original -OriginalAfter $changed -GuestName 'SANDBOX-GUEST' -HostName 'HOST' } 'source PID reuse during launch'
}
Invoke-Test 'launch receipt rejects wrong guest epoch provenance' {
    $original = [pscustomobject]@{ processId = 501; processName = 'MarkdownRenderer.Sample'; executablePath = $sourceProcess.executablePath; startTicks = $sourceProcess.startTicks; exeSha256 = $sourceProcess.exeSha256 }
    $wrong = $launchReceipt.PSObject.Copy(); $wrong.guestComputerName = 'HOST'
    Assert-Throws { Assert-DeferredTouchLaunchReceipt -Receipt $wrong -OriginalBefore $original -OriginalAfter $original -GuestName 'SANDBOX-GUEST' -HostName 'HOST' } 'wrong guest machine'
}
Invoke-Test 'launch receipt rejects unknown or changed child hash' {
    $original = [pscustomobject]@{ processId = 501; processName = 'MarkdownRenderer.Sample'; executablePath = $sourceProcess.executablePath; startTicks = $sourceProcess.startTicks; exeSha256 = $sourceProcess.exeSha256 }
    $wrong = $launchReceipt.PSObject.Copy(); $wrong.process = (New-TestIdentity -ProcessId 612 -Hash ('f' * 64))
    Assert-Throws { Assert-DeferredTouchLaunchReceipt -Receipt $wrong -OriginalBefore $original -OriginalAfter $original -GuestName 'SANDBOX-GUEST' -HostName 'HOST' } 'child executable hash changed'
}
Invoke-Test 'generated guest launcher is valid PowerShell, directly starts only the captured executable, and leaves its window visible' {
    $script:OriginalExecutablePath = 'C:\WinApp\deployments\fixture\MarkdownRenderer.Sample.exe'
    $script:GuestComputerName = 'SANDBOX-GUEST'
    $script:HostComputerName = 'HOST'
    $source = [pscustomobject]@{ processId = 501; startTicks = 639219000000000000; exeSha256 = 'a' * 64 }
    $code = New-DeferredTouchGuestLauncherCode -OriginalIdentity $source
    $generatedTokens = $null; $generatedErrors = $null
    [System.Management.Automation.Language.Parser]::ParseInput($code, [ref]$generatedTokens, [ref]$generatedErrors) | Out-Null
    Assert-Condition ($generatedErrors.Count -eq 0) 'Generated guest launch command is not valid Windows PowerShell.'
    Assert-Condition ($code -match 'Start-Process -FilePath \$exe .*--renderer-deferred-touch-probe.*-WindowStyle Normal') 'Generated guest launcher does not launch the exact visible sample process with the fixed argument.'
    Assert-Condition ($code -notmatch '\$host\s*=|MarkdownRenderer\.Sample\.exe\s+--') 'Generated guest launch command uses an unsafe host variable or a hard-coded executable.'
}

$launchIdentity = [pscustomobject]@{ processId = 612; processName = 'MarkdownRenderer.Sample' }
$originalIdentity = [pscustomobject]@{ processId = 501 }
Invoke-Test 'window discovery binds exact title to launch PID and distinct source PID' {
    $windows = @([pscustomobject]@{ processId = 612; hwnd = 9901; processName = 'MarkdownRenderer.Sample'; title = 'MarkdownRenderer deferred-touch probe' })
    $found = Find-DeferredTouchProbeWindow -Windows $windows -LaunchIdentity $launchIdentity -OriginalIdentity $originalIdentity
    Assert-Condition ($found.processId -eq 612 -and $found.hwnd -eq 9901) 'Window discovery returned the wrong identity.'
}
Invoke-Test 'window discovery rejects PID reuse/title on a different launch process' {
    $windows = @([pscustomobject]@{ processId = 613; hwnd = 9901; processName = 'MarkdownRenderer.Sample'; title = 'MarkdownRenderer deferred-touch probe' })
    Assert-Throws { Find-DeferredTouchProbeWindow -Windows $windows -LaunchIdentity $launchIdentity -OriginalIdentity $originalIdentity } 'wrong newly launched PID'
}
Invoke-Test 'window discovery rejects duplicate exact probe windows' {
    $windows = @(
        [pscustomobject]@{ processId = 612; hwnd = 9901; processName = 'MarkdownRenderer.Sample'; title = 'MarkdownRenderer deferred-touch probe' },
        [pscustomobject]@{ processId = 612; hwnd = 9902; processName = 'MarkdownRenderer.Sample'; title = 'MarkdownRenderer deferred-touch probe' }
    )
    Assert-Throws { Find-DeferredTouchProbeWindow -Windows $windows -LaunchIdentity $launchIdentity -OriginalIdentity $originalIdentity } 'duplicate title windows'
}
Invoke-Test 'window discovery returns null before the new window appears' {
    $found = Find-DeferredTouchProbeWindow -Windows @() -LaunchIdentity $launchIdentity -OriginalIdentity $originalIdentity
    Assert-Condition ($null -eq $found) 'Window discovery should allow bounded polling when no window is present yet.'
}

Invoke-Test 'driver result requires passing report and zero exit' {
    $report = [pscustomobject]@{ status = 'PASS'; scenario = 'Release'; viewport = 'owned' }
    Assert-Condition ((Get-DeferredTouchDriverStatus -ExitCode 0 -Report $report) -ceq 'PASS') 'Passing guest report was not accepted.'
}
Invoke-Test 'driver result preserves report failure with nonzero exit' {
    $report = [pscustomobject]@{ status = 'FAIL'; failure = 'Observed selection regression.'; scenario = 'Release'; viewport = 'owned' }
    Assert-Condition ((Get-DeferredTouchDriverStatus -ExitCode 1 -Report $report) -ceq 'FAIL') 'Guest failure was not preserved.'
}
Invoke-Test 'driver result rejects nonzero exit paired with PASS report' {
    $report = [pscustomobject]@{ status = 'PASS'; scenario = 'Release'; viewport = 'owned' }
    Assert-Condition ((Get-DeferredTouchDriverStatus -ExitCode 1 -Report $report) -ceq 'UNVERIFIED') 'Inconsistent exit/report was accepted.'
}
Invoke-Test 'driver result rejects exit zero paired with FAIL report' {
    $report = [pscustomobject]@{ status = 'FAIL'; scenario = 'Release'; viewport = 'owned' }
    Assert-Condition ((Get-DeferredTouchDriverStatus -ExitCode 0 -Report $report) -ceq 'UNVERIFIED') 'Inconsistent exit/report was accepted.'
}
Invoke-Test 'driver result rejects missing report' {
    Assert-Condition ((Get-DeferredTouchDriverStatus -ExitCode 0 -Report $null) -ceq 'UNVERIFIED') 'Missing report was treated as success.'
}
Invoke-Test 'driver result does not call an empty failure report a product failure' {
    $report = [pscustomobject]@{ status = 'FAIL'; failure = ''; scenario = 'Release'; viewport = 'owned' }
    Assert-Condition ((Get-DeferredTouchDriverStatus -ExitCode 1 -Report $report) -ceq 'UNVERIFIED') 'A report without a concrete failure was classified as a product failure.'
}
Invoke-Test 'guest result parser accepts the current direct native.value JSON object and preserves raw stdout' {
    $raw = '{"mode":"Identity","computerName":"SANDBOX-GUEST","processId":612,"hwnd":9901}'
    $value = [pscustomobject]@{ mode = 'Identity'; computerName = 'SANDBOX-GUEST'; processId = 612; hwnd = 9901 }
    $parsed = ConvertFrom-DeferredTouchNativeGuestResult -NativeResult ([pscustomobject]@{ value = $value; stdout = $raw; output = $raw })
    Assert-Condition ($parsed.source -ceq 'native.value direct JSON object' -and $parsed.value.processId -eq 612 -and $parsed.text -ceq $raw) 'Current direct-object target-exec output was not consumed as the guest result or raw stdout was lost.'
}
Invoke-Test 'guest result parser accepts only bounded legacy string wrappers' {
    $raw = '{"computerName":"SANDBOX-GUEST","directory":"C:\\WinApp\\work"}'
    $wrapper = [pscustomobject]@{ exitCode = 0; stdout = $raw; stderr = '' }
    $parsed = ConvertFrom-DeferredTouchNativeGuestResult -NativeResult ([pscustomobject]@{ value = $wrapper; stdout = (ConvertTo-Json -InputObject $wrapper -Compress) })
    Assert-Condition ($parsed.source -ceq 'legacy native.value.stdout wrapper' -and $parsed.value.directory -ceq 'C:\WinApp\work') 'Known wrapped child stdout was not parsed exactly.'
}
Invoke-Test 'guest result parser rejects CLI error objects instead of treating them as child reports' {
    $errorObject = [pscustomobject]@{ error = 'target exec failed'; message = 'process unavailable'; exitCode = 1 }
    Assert-Throws { ConvertFrom-DeferredTouchNativeGuestResult -NativeResult ([pscustomobject]@{ value = $errorObject; stdout = (ConvertTo-Json -InputObject $errorObject -Compress) }) } 'CLI error mistaken for child result'
}
Invoke-Test 'guest result parser rejects scalar arrays and malformed wrapped stdout' {
    Assert-Throws { ConvertFrom-DeferredTouchNativeGuestResult -NativeResult ([pscustomobject]@{ value = 'true'; stdout = 'true' }) } 'scalar JSON guest output'
    Assert-Throws { ConvertFrom-DeferredTouchNativeGuestResult -NativeResult ([pscustomobject]@{ value = @('one', 'two'); stdout = '[]' }) } 'array JSON guest output'
    $badWrapper = [pscustomobject]@{ stdout = 'not json'; exitCode = 0 }
    Assert-Throws { ConvertFrom-DeferredTouchNativeGuestResult -NativeResult ([pscustomobject]@{ value = $badWrapper; stdout = 'not json' }) } 'malformed wrapper output'
}
Invoke-Test 'shared identity helper output requires exact mode process HWND and complete identity' {
    $identityReport = [pscustomobject]@{
        mode = 'Identity'; computerName = 'SANDBOX-GUEST'; processId = 612; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe'
        exePath = 'C:\WinApp\deployments\fixture\MarkdownRenderer.Sample.exe'; exeSha256 = 'a' * 64; startTicks = 639220000000000000
    }
    $accepted = Assert-DeferredTouchGuestIdentityShape -Value $identityReport -Mode Identity -ProcessId 612 -WindowHandle 9901 -GuestComputerName 'SANDBOX-GUEST'
    Assert-Condition ($accepted.processId -eq 612) 'Exact identity report was rejected.'
    foreach ($mutation in @(
        @{ name = 'wrong guest'; update = { param($v) $v.computerName = 'HOST' } },
        @{ name = 'wrong HWND'; update = { param($v) $v.hwnd = 9902 } },
        @{ name = 'wrong start'; update = { param($v) $v.startTicks = 0 } },
        @{ name = 'wrong process hash'; update = { param($v) $v.exeSha256 = 'not-a-hash' } }
    )) {
        $changed = $identityReport.PSObject.Copy(); & $mutation.update $changed
        Assert-Throws { Assert-DeferredTouchGuestIdentityShape -Value $changed -Mode Identity -ProcessId 612 -WindowHandle 9901 -GuestComputerName 'SANDBOX-GUEST' } $mutation.name
    }
}
Invoke-Test 'real exePath-only identity JSON flows through parsing validation and canonical process checks for original and child' {
    $identityCases = @(
        [pscustomobject]@{ expected = (New-TestIdentity -ProcessId 501 -StartTicks 639219000000000000 -Hwnd 8801 -Title 'MarkdownRenderer — Feature Showcase'); title = 'MarkdownRenderer — Feature Showcase' },
        [pscustomobject]@{ expected = (New-TestIdentity -ProcessId 612 -StartTicks 639220000000000000 -Hwnd 9901 -Title 'MarkdownRenderer deferred-touch probe'); title = 'MarkdownRenderer deferred-touch probe' }
    )
    $script:GuestSharedProbePath = 'C:\fixture\MarkdownCorrectnessSandboxProbe.ps1'
    $script:GuestComputerName = 'SANDBOX-GUEST'
    $script:Prior = [pscustomobject]@{
        guestExecutablePath = $expectedIdentity.executablePath
        buildBinary = [pscustomobject]@{ sha256 = $expectedIdentity.exeSha256 }
    }
    foreach ($case in $identityCases) {
        $expected = $case.expected
        $rawIdentity = [pscustomobject]@{
            mode = 'Identity'; computerName = 'SANDBOX-GUEST'; processId = $expected.processId; hwnd = $expected.hwnd
            title = $case.title; exePath = $expected.executablePath; exeSha256 = $expected.exeSha256
            startTicks = $expected.startTicks; startTimeUtc = '2026-10-13T03:00:00.0000000Z'
            dpi = 96; highContrastFlags = 0; highContrastOn = $false; highContrastScheme = ''
        }
        $script:DeferredTouchNativeFixture = [pscustomobject]@{
            value = $rawIdentity
            stdout = ConvertTo-Json -InputObject $rawIdentity -Depth 16 -Compress
            exitCode = 0; output = ''
        }
        $script:OriginalExecutablePath = if ($expected.processId -eq 501) { '' } else { $expected.executablePath }
        $canonical = Invoke-DeferredTouchGuestIdentity -Mode Identity -ProcessId $expected.processId -Hwnd $expected.hwnd `
            -StartTicks $expected.startTicks -CaseDirectory 'C:\fixture\case'
        $matched = Assert-DeferredTouchProcessIdentity -Actual $canonical -Expected $expected
        Assert-Condition ($matched.processName -ceq 'MarkdownRenderer.Sample' -and
            $canonical.exePath -ceq $expected.executablePath -and $canonical.startTimeUtc -ceq '2026-10-13T03:00:00.0000000Z' -and
            $canonical.dpi -eq 96) 'Canonical identity lost the real executable path or diagnostic fields.'
    }
}
Invoke-Test 'canonical process name is derived from the verified executable path rather than the expected identity' {
    $otherPath = 'C:\WinApp\deployments\fixture\Unexpected.Sample.exe'
    $rawIdentity = [pscustomobject]@{
        mode = 'Identity'; computerName = 'SANDBOX-GUEST'; processId = 612; hwnd = 9901
        title = 'MarkdownRenderer deferred-touch probe'; exePath = $otherPath; exeSha256 = 'a' * 64
        startTicks = 639220000000000000
    }
    $validated = Assert-DeferredTouchGuestIdentityShape -Value $rawIdentity -Mode Identity -ProcessId 612 -WindowHandle 9901 -GuestComputerName 'SANDBOX-GUEST'
    Assert-Throws { ConvertTo-DeferredTouchCanonicalProcessIdentity -ValidatedIdentity $validated -ExpectedExecutablePath $expectedIdentity.executablePath } 'unapproved executable path'
    $canonical = ConvertTo-DeferredTouchCanonicalProcessIdentity -ValidatedIdentity $validated -ExpectedExecutablePath $otherPath
    Assert-Condition ($canonical.processName -ceq 'Unexpected.Sample') 'Process name was fabricated from a fixed expected name instead of the verified executable path.'
    Assert-Throws { Assert-DeferredTouchProcessIdentity -Actual $canonical -Expected $expectedIdentity } 'unexpected executable identity'
}
Invoke-Test 'payload helper shape does not require HWND absent from its documented report' {
    $payloadReport = [pscustomobject]@{
        mode = 'PayloadHashes'; processId = 612; executablePath = 'C:\WinApp\deployments\fixture\MarkdownRenderer.Sample.exe'
        files = @((New-TestPayload 'MarkdownRenderer.Sample.exe' ('a' * 64)), (New-TestPayload 'MarkdownRenderer.dll' ('b' * 64)))
    }
    $accepted = Assert-DeferredTouchGuestIdentityShape -Value $payloadReport -Mode PayloadHashes -ProcessId 612 -WindowHandle 9901 -GuestComputerName 'SANDBOX-GUEST'
    Assert-Condition ($accepted.files.Count -eq 2) 'Payload manifest with the real helper contract was rejected.'
    $bad = $payloadReport.PSObject.Copy(); $bad.files = @()
    Assert-Throws { Assert-DeferredTouchGuestIdentityShape -Value $bad -Mode PayloadHashes -ProcessId 612 -WindowHandle 9901 -GuestComputerName 'SANDBOX-GUEST' } 'empty payload manifest'
}

Invoke-Test 'driver report binds case, process identity and same deployed payload' {
    $prior = New-TestPriorReceipt
    $identity = [pscustomobject]@{ mode = 'Identity'; computerName = 'SANDBOX-GUEST'; processId = 612; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe'; exePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256 }
    $payload = [pscustomobject]@{ mode = 'PayloadHashes'; processId = 612; executablePath = $expectedIdentity.executablePath; files = $actualPayloads }
    $report = [pscustomobject]@{
        status = 'PASS'; scenario = 'Release'; viewport = 'owned'; identityChecks = 4; foregroundChecks = 3
        cleanup = [pscustomobject]@{ pumpStopped = $true; deviceDestroyed = $true; deviceRetained = $false; error = $null }
        identity = $identity; payloadHashes = $payload
    }
    $expected = [pscustomobject]@{ processId = 612; processName = 'MarkdownRenderer.Sample'; executablePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe' }
    $matched = Assert-DeferredTouchDriverReport -Report $report -Scenario 'Release' -Viewport 'owned' -ExpectedIdentity $expected -ExpectedPayloads $expectedPayloads -ExpectedGuestName 'SANDBOX-GUEST' -ExpectedDirectory 'C:\WinApp\deployments\fixture'
    Assert-Condition ($matched.status -ceq 'MATCH') 'Guest report payload was not verified.'
}
Invoke-Test 'driver report rejects an identity missing the emitted mode field' {
    $identity = [pscustomobject]@{
        computerName = 'SANDBOX-GUEST'; processId = 612; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe'
        exePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256
    }
    $payload = [pscustomobject]@{ mode = 'PayloadHashes'; processId = 612; executablePath = $expectedIdentity.executablePath; files = $actualPayloads }
    $report = [pscustomobject]@{
        status = 'PASS'; scenario = 'Release'; viewport = 'owned'; identityChecks = 4; foregroundChecks = 3
        cleanup = [pscustomobject]@{ pumpStopped = $true; deviceDestroyed = $true; deviceRetained = $false; error = $null }
        identity = $identity; payloadHashes = $payload
    }
    $expected = [pscustomobject]@{ processId = 612; processName = 'MarkdownRenderer.Sample'; executablePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe' }
    Assert-Throws { Assert-DeferredTouchDriverReport -Report $report -Scenario 'Release' -Viewport 'owned' -ExpectedIdentity $expected -ExpectedPayloads $expectedPayloads -ExpectedGuestName 'SANDBOX-GUEST' -ExpectedDirectory 'C:\WinApp\deployments\fixture' } 'missing nested identity mode'
}
Invoke-Test 'driver report rejects wrong case or viewport' {
    $identity = [pscustomobject]@{ mode = 'Identity'; computerName = 'SANDBOX-GUEST'; processId = 612; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe'; exePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256 }
    $report = [pscustomobject]@{ scenario = 'CancelNew'; viewport = 'ancestor'; identity = $identity; payloadHashes = $null }
    $expected = [pscustomobject]@{ processId = 612; processName = 'MarkdownRenderer.Sample'; executablePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe' }
    Assert-Throws { Assert-DeferredTouchDriverReport -Report $report -Scenario 'Release' -Viewport 'owned' -ExpectedIdentity $expected -ExpectedPayloads $expectedPayloads -ExpectedGuestName 'SANDBOX-GUEST' -ExpectedDirectory 'C:\WinApp\deployments\fixture' } 'wrong scenario and viewport'
}
Invoke-Test 'driver report rejects reused probe PID' {
    $identity = [pscustomobject]@{ mode = 'Identity'; computerName = 'SANDBOX-GUEST'; processId = 612; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe'; exePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks + 1; exeSha256 = $expectedIdentity.exeSha256 }
    $report = [pscustomobject]@{ scenario = 'Release'; viewport = 'owned'; identity = $identity; payloadHashes = $null }
    $expected = [pscustomobject]@{ processId = 612; processName = 'MarkdownRenderer.Sample'; executablePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe' }
    Assert-Throws { Assert-DeferredTouchDriverReport -Report $report -Scenario 'Release' -Viewport 'owned' -ExpectedIdentity $expected -ExpectedPayloads $expectedPayloads -ExpectedGuestName 'SANDBOX-GUEST' -ExpectedDirectory 'C:\WinApp\deployments\fixture' } 'reused guest PID in report'
}
Invoke-Test 'driver failure report requires a concrete failure detail' {
    $identity = [pscustomobject]@{ mode = 'Identity'; computerName = 'SANDBOX-GUEST'; processId = 612; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe'; exePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256 }
    $payload = [pscustomobject]@{ mode = 'PayloadHashes'; processId = 612; executablePath = $expectedIdentity.executablePath; files = $actualPayloads }
    $expected = [pscustomobject]@{ processId = 612; processName = 'MarkdownRenderer.Sample'; executablePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe' }
    $report = [pscustomobject]@{
        status = 'FAIL'; failure = ''; scenario = 'Release'; viewport = 'owned'; identityChecks = 4; foregroundChecks = 3
        cleanup = [pscustomobject]@{ pumpStopped = $true; deviceDestroyed = $true; deviceRetained = $false; error = $null }
        identity = $identity; payloadHashes = $payload
    }
    Assert-Throws { Assert-DeferredTouchDriverReport -Report $report -Scenario 'Release' -Viewport 'owned' -ExpectedIdentity $expected -ExpectedPayloads $expectedPayloads -ExpectedGuestName 'SANDBOX-GUEST' -ExpectedDirectory 'C:\WinApp\deployments\fixture' } 'failure report without details'
}
Invoke-Test 'driver PASS requires positive identity and foreground evidence' {
    $identity = [pscustomobject]@{ mode = 'Identity'; computerName = 'SANDBOX-GUEST'; processId = 612; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe'; exePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256 }
    $payload = [pscustomobject]@{ mode = 'PayloadHashes'; processId = 612; executablePath = $expectedIdentity.executablePath; files = $actualPayloads }
    $expected = [pscustomobject]@{ processId = 612; processName = 'MarkdownRenderer.Sample'; executablePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe' }
    foreach ($field in @('identityChecks', 'foregroundChecks')) {
        $report = [pscustomobject]@{
            status = 'PASS'; scenario = 'Release'; viewport = 'owned'; identityChecks = 1; foregroundChecks = 1
            cleanup = [pscustomobject]@{ pumpStopped = $true; deviceDestroyed = $true; deviceRetained = $false; error = $null }
            identity = $identity; payloadHashes = $payload
        }
        $report.$field = 0
        Assert-Throws { Assert-DeferredTouchDriverReport -Report $report -Scenario 'Release' -Viewport 'owned' -ExpectedIdentity $expected -ExpectedPayloads $expectedPayloads -ExpectedGuestName 'SANDBOX-GUEST' -ExpectedDirectory 'C:\WinApp\deployments\fixture' } "missing positive $field"
    }
}
Invoke-Test 'driver PASS requires joined worker and destroyed unretained device' {
    $identity = [pscustomobject]@{ mode = 'Identity'; computerName = 'SANDBOX-GUEST'; processId = 612; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe'; exePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256 }
    $payload = [pscustomobject]@{ mode = 'PayloadHashes'; processId = 612; executablePath = $expectedIdentity.executablePath; files = $actualPayloads }
    $expected = [pscustomobject]@{ processId = 612; processName = 'MarkdownRenderer.Sample'; executablePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe' }
    foreach ($cleanup in @(
        [pscustomobject]@{ pumpStopped = $false; deviceDestroyed = $true; deviceRetained = $false; error = $null },
        [pscustomobject]@{ pumpStopped = $true; deviceDestroyed = $false; deviceRetained = $true; error = 'worker may still be running' },
        [pscustomobject]@{ pumpStopped = $true; deviceDestroyed = $true; deviceRetained = $false; error = 'teardown warning' }
    )) {
        $report = [pscustomobject]@{ status = 'PASS'; scenario = 'Release'; viewport = 'owned'; identityChecks = 4; foregroundChecks = 3; cleanup = $cleanup; identity = $identity; payloadHashes = $payload }
        Assert-Throws { Assert-DeferredTouchDriverReport -Report $report -Scenario 'Release' -Viewport 'owned' -ExpectedIdentity $expected -ExpectedPayloads $expectedPayloads -ExpectedGuestName 'SANDBOX-GUEST' -ExpectedDirectory 'C:\WinApp\deployments\fixture' } 'unclean cleanup marked PASS'
    }
}
Invoke-Test 'a bound guest assertion failure remains FAIL when guest cleanup is also incomplete' {
    $identity = [pscustomobject]@{ mode = 'Identity'; computerName = 'SANDBOX-GUEST'; processId = 612; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe'; exePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256 }
    $payload = [pscustomobject]@{ mode = 'PayloadHashes'; processId = 612; executablePath = $expectedIdentity.executablePath; files = $actualPayloads }
    $expected = [pscustomobject]@{ processId = 612; processName = 'MarkdownRenderer.Sample'; executablePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks; exeSha256 = $expectedIdentity.exeSha256; hwnd = 9901; title = 'MarkdownRenderer deferred-touch probe' }
    $report = [pscustomobject]@{
        status = 'FAIL'; scenario = 'Release'; viewport = 'owned'; failure = 'Fresh contact did not select the required word.'
        identityChecks = 4; foregroundChecks = 3
        cleanup = [pscustomobject]@{ pumpStopped = $false; deviceDestroyed = $false; deviceRetained = $true; error = 'worker join timed out' }
        identity = $identity; payloadHashes = $payload
    }
    $null = Assert-DeferredTouchDriverReport -Report $report -Scenario 'Release' -Viewport 'owned' -ExpectedIdentity $expected -ExpectedPayloads $expectedPayloads -ExpectedGuestName 'SANDBOX-GUEST' -ExpectedDirectory 'C:\WinApp\deployments\fixture'
    Assert-Condition ((Get-DeferredTouchDriverStatus -ExitCode 1 -Report $report) -ceq 'FAIL') 'Real guest assertion failure was masked by its distinct cleanup failure.'
}
Invoke-Test 'normal close is allowed without invoking the touch driver' {
    $safety = Get-DeferredTouchNormalCloseSafety -DriverInvoked $false -ReportValidated $false -Report $null
    Assert-Condition ($safety.safe -and $safety.reason -match 'No touch driver') 'Pre-driver app launch could not be closed safely.'
}
Invoke-Test 'normal close requires validated report plus joined worker and unretained device' {
    $clean = [pscustomobject]@{ cleanup = [pscustomobject]@{ pumpStopped = $true; deviceRetained = $false; error = $null } }
    $safe = Get-DeferredTouchNormalCloseSafety -DriverInvoked $true -ReportValidated $true -Report $clean
    Assert-Condition $safe.safe 'Clean validated guest cleanup was not allowed to close the test window.'

    $unvalidated = Get-DeferredTouchNormalCloseSafety -DriverInvoked $true -ReportValidated $false -Report $clean
    Assert-Condition (-not $unvalidated.safe -and $unvalidated.reason -match 'did not validate') 'Unvalidated guest output was allowed to send close input.'
    $missing = Get-DeferredTouchNormalCloseSafety -DriverInvoked $true -ReportValidated $true -Report $null
    Assert-Condition (-not $missing.safe -and $missing.reason -match 'No guest report') 'Missing guest cleanup evidence was allowed to send close input.'

    foreach ($cleanup in @(
        [pscustomobject]@{ pumpStopped = $false; deviceRetained = $false; error = $null },
        [pscustomobject]@{ pumpStopped = $true; deviceRetained = $true; error = 'device retained' },
        [pscustomobject]@{ pumpStopped = $true; deviceRetained = $false; error = 'cleanup error' }
    )) {
        $unsafe = Get-DeferredTouchNormalCloseSafety -DriverInvoked $true -ReportValidated $true -Report ([pscustomobject]@{ cleanup = $cleanup })
        Assert-Condition (-not $unsafe.safe) 'Incomplete touch cleanup was allowed to send close input.'
    }
}

Invoke-Test 'normal-close verification accepts exact probe exit' {
    $state = [pscustomobject]@{ mode = 'ProcessState'; processId = 612; exists = $false }
    Assert-Condition (Assert-DeferredTouchExactProcessExited -ProcessState $state -ExpectedIdentity $expectedIdentity) 'Exact probe exit was rejected.'
}
Invoke-Test 'normal-close verification refuses when exact probe is still running' {
    $state = [pscustomobject]@{ mode = 'ProcessState'; processId = 612; exists = $true; processName = $expectedIdentity.processName; executablePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks }
    Assert-Throws { Assert-DeferredTouchExactProcessExited -ProcessState $state -ExpectedIdentity $expectedIdentity } 'app still alive after close'
}
Invoke-Test 'normal-close verification distinguishes reused PID by exact start ticks' {
    $state = [pscustomobject]@{ mode = 'ProcessState'; processId = 612; exists = $true; processName = 'MarkdownRenderer.Sample'; executablePath = $expectedIdentity.executablePath; startTicks = $expectedIdentity.startTicks + 1 }
    Assert-Condition (Assert-DeferredTouchExactProcessExited -ProcessState $state -ExpectedIdentity $expectedIdentity) 'Different process instance was confused with the probe.'
}
Invoke-Test 'normal-close verification rejects incomplete live PID state' {
    $state = [pscustomobject]@{ mode = 'ProcessState'; processId = 612; exists = $true; processName = ''; executablePath = ''; startTicks = 0 }
    Assert-Throws { Assert-DeferredTouchExactProcessExited -ProcessState $state -ExpectedIdentity $expectedIdentity } 'incomplete process state'
}
Invoke-Test 'normal-close verification rejects wrong process-state PID' {
    $state = [pscustomobject]@{ mode = 'ProcessState'; processId = 613; exists = $false }
    Assert-Throws { Assert-DeferredTouchExactProcessExited -ProcessState $state -ExpectedIdentity $expectedIdentity } 'wrong process-state PID'
}

Invoke-Test 'case plan includes exactly one of all six scenario and viewport pairs' {
    $plan = @(Get-DeferredTouchCasePlan)
    Assert-Condition ($plan.Count -eq 6) 'Expected exactly six bounded cases.'
    $keys = @($plan | ForEach-Object { "$($_.scenario)/$($_.viewport)" } | Sort-Object -Unique)
    Assert-Condition ($keys.Count -eq 6) 'Case plan contains duplicate or missing scenario/viewport pairs.'
    Assert-Condition ((@($plan | Where-Object { $_.scenario -eq 'Release' }).Count) -eq 2) 'Release cases are incomplete.'
    Assert-Condition ((@($plan | Where-Object { $_.scenario -eq 'CancelNew' }).Count) -eq 2) 'CancelNew cases are incomplete.'
    Assert-Condition ((@($plan | Where-Object { $_.scenario -eq 'CancelExisting' }).Count) -eq 2) 'CancelExisting cases are incomplete.'
}
Invoke-Test 'status counts reject unknown status rather than marking complete' {
    $items = @([pscustomobject]@{ status = 'PASS' }, [pscustomobject]@{ status = 'UNVERIFIED' })
    $counts = Get-DeferredTouchCounts -Results $items
    Assert-Condition ($counts.pass -eq 1 -and $counts.unverified -eq 1 -and $counts.fail -eq 0) 'Status counts are incorrect.'
    Assert-Throws { Get-DeferredTouchCounts -Results @([pscustomobject]@{ status = 'SKIPPED' }) } 'unknown result status'
}
Invoke-Test 'setup failures count as unverified rather than product failures' {
    $counts = Get-DeferredTouchCounts -Results @() -SetupFailure ([pscustomobject]@{ status = 'UNVERIFIED'; failure = 'Sandbox prerequisite was not met.' })
    Assert-Condition ($counts.pass -eq 0 -and $counts.fail -eq 0 -and $counts.unverified -eq 1) 'A setup block was incorrectly classified as a failed product assertion.'
}

Invoke-Test 'host entrypoint is opt-in and never deploys, resets, retries, or force-kills' {
    $source = Get-Content -LiteralPath $hostScriptPath -Raw
    Assert-Condition ($source -match 'if\s*\(\s*-not\s+\$RunApprovedSandboxCases\s*\)') 'Host entrypoint is not gated by the explicit run switch.'
    Assert-Condition ($source -notmatch '(?im)\bwinapp\s+run\b|\bStop-Process\b|\btaskkill\b|target\s+reset|--reset|Invoke-DeferredTouchProbeLaunch\s+-OriginalIdentity[^\r\n]*\r?\n[^\r\n]*Invoke-DeferredTouchProbeLaunch') 'Host entrypoint contains a redeploy/reset/force-kill/retry path.'
    Assert-Condition ($source -match "--renderer-deferred-touch-probe" -and $source -match "DeferredTouchCloseProbe") 'Direct launch argument or normal close control is missing.'
    Assert-Condition ($source -match 'WINAPP_UI_WORKFLOW_ID\s*=\s*\$script:WorkflowId') 'The batch does not pin one workflow identifier.'
    Assert-Condition ($source -match 'FileMode\]::CreateNew') 'Evidence writes are not create-new/immutable.'
    Assert-Condition ($source -match "SetupFailure = \[pscustomobject\]@\{ status = 'UNVERIFIED'") 'An unexecuted batch is incorrectly reported as a product failure.'
    Assert-Condition ($source -match 'Get-DeferredTouchNormalCloseSafety\s+-DriverInvoked\s+\$driverInvoked\s+-ReportValidated\s+\$driverReportValidated') 'Normal close is not gated by a validated guest report and affirmative touch-worker cleanup.'
    Assert-Condition ($source -match '\[Text\.UTF8Encoding\]::new\(\$false,\s*\$true\)') 'WinApp stdout/stderr decoding is not strict UTF-8.'
    Assert-Condition ($source -match 'ConvertFrom-DeferredTouchNativeGuestResult\s+-NativeResult\s+\$native') 'Guest stdout parsing is not centralized across guest script and identity calls.'
    Assert-Condition ($source -match 'ConvertFrom-DeferredTouchNativeGuestResult\s+-NativeResult\s+\$driver') 'Touch-driver output bypasses the common strict JSON-object parser.'
}

$failures = @($tests | Where-Object { $_.status -ne 'PASS' })
$tests | Format-Table -AutoSize
foreach ($failure in $failures) { Write-Host ("FAIL {0}: {1}" -f $failure.name, $failure.error) }
"Deferred-touch host offline tests: $(@($tests | Where-Object status -eq 'PASS').Count) passed, $($failures.Count) failed."
if ($failures.Count -gt 0) { exit 1 }
exit 0
