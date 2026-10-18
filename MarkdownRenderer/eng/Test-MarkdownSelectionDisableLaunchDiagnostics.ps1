$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-True {
    param([Parameter(Mandatory)][bool]$Condition,[Parameter(Mandatory)][string]$Message)
    if (-not $Condition) { throw $Message }
}

function Get-FunctionAst {
    param([Parameter(Mandatory)]$Ast,[Parameter(Mandatory)][string]$Name)
    $function = $Ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name },$true)
    if ($null -eq $function) { throw "Required offline-test function '$Name' was not found in the source." }
    return $function
}

$harnessPath = Join-Path $PSScriptRoot 'Invoke-MarkdownCorrectnessUiTests.ps1'
$tokens = $null
$parseErrors = $null
$harnessAst = [System.Management.Automation.Language.Parser]::ParseFile($harnessPath,[ref]$tokens,[ref]$parseErrors)
Assert-True ($parseErrors.Count -eq 0) 'The UI harness does not parse.'

$guestAssignment = $harnessAst.Find({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -ceq '$source' -and
        $node.Right.Expression -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
        $node.Right.Expression.StringConstantType -eq 'SingleQuotedHereString' -and
        $node.Right.Expression.Value.Contains('function Start-SelectionDisableProbeProcess')
},$true)
if ($null -eq $guestAssignment) { throw 'Could not find the embedded guest probe script.' }
$guestSource = [string]$guestAssignment.Right.Expression.Value
$guestTokens = $null
$guestErrors = $null
$guestAst = [System.Management.Automation.Language.Parser]::ParseInput($guestSource,[ref]$guestTokens,[ref]$guestErrors)
Assert-True ($guestErrors.Count -eq 0) 'The embedded guest probe script does not parse.'

$pathFunction = Get-FunctionAst -Ast $guestAst -Name 'New-SelectionDisableLaunchDiagnostics'
$observationFunction = Get-FunctionAst -Ast $guestAst -Name 'Get-SelectionDisableLaunchObservation'
$launchFunction = Get-FunctionAst -Ast $guestAst -Name 'Start-SelectionDisableProbeProcess'
$guestDispatcher = $guestSource.IndexOf("if (`$Mode -eq 'launch-selection-disable-probe')",[StringComparison]::Ordinal)
Assert-True ($guestDispatcher -ge 0 -and $guestSource.IndexOf('if ([string]::IsNullOrWhiteSpace($RunId)) { throw',$guestDispatcher,[StringComparison]::Ordinal) -ge 0) 'The live guest dispatcher must reject a selection-disable launch without the active RunId.'
Assert-True ($harnessAst.Extent.Text.Contains("Invoke-GuestProbe -Mode 'launch-selection-disable-probe' -RunId `$script:RunId")) 'The host must pass the current RunId to the guest launch helper.'

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ('selection-disable-launch-diagnostics-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($tempRoot)
try {
    $runId = '20261014T120000Z-' + [guid]::NewGuid().ToString('N')
    $guestRunRoot = Join-Path (Join-Path $tempRoot 'correctness') $runId
    [void][IO.Directory]::CreateDirectory($guestRunRoot)
    Invoke-Expression $pathFunction.Extent.Text
    $paths = New-SelectionDisableLaunchDiagnostics -RunId $runId -GuestWorkRoot $tempRoot
    Assert-True ($paths.guestRelativeDirectory -ceq "correctness\$runId\selection-disable-launch") 'Guest diagnostic directory is not confined to the exact run-scoped work path.'
    Assert-True ($paths.guestStdoutRelativePath -ceq "$($paths.guestRelativeDirectory)\stdout.txt" -and $paths.guestStderrRelativePath -ceq "$($paths.guestRelativeDirectory)\stderr.txt") 'Guest stdout/stderr paths do not have their fixed run-scoped names.'
    Assert-True ((Test-Path -LiteralPath $paths.guestDirectory -PathType Container) -and -not (Test-Path -LiteralPath $paths.guestStdoutPath) -and -not (Test-Path -LiteralPath $paths.guestStderrPath)) 'Diagnostic path setup must create only a fresh directory, not synthetic output files.'

    $invalidRunRejected = $false
    try { New-SelectionDisableLaunchDiagnostics -RunId '..\outside' -GuestWorkRoot $tempRoot | Out-Null } catch { $invalidRunRejected = $true }
    Assert-True $invalidRunRejected 'The diagnostic path builder accepted a traversal-shaped run ID.'
    $existingDirectoryRejected = $false
    try { New-SelectionDisableLaunchDiagnostics -RunId $runId -GuestWorkRoot $tempRoot | Out-Null } catch { $existingDirectoryRejected = $true }
    Assert-True $existingDirectoryRejected 'The diagnostic path builder reused an existing evidence directory.'

    Invoke-Expression $observationFunction.Extent.Text
    $process = [pscustomobject]@{
        HasExited = $true
        ExitCode = 73
        MainWindowHandle = [IntPtr]::Zero
        MainWindowTitle = ''
    }
    $process | Add-Member -MemberType ScriptMethod -Name Refresh -Value { }
    $observation = Get-SelectionDisableLaunchObservation -Process $process
    Assert-True ($observation.status -ceq 'process-exited-before-window-observation' -and $observation.processExited -and $observation.exitCode -eq 73) 'An exited child did not preserve its exit code as a distinct pre-window outcome.'

    $process.HasExited = $false
    $process.MainWindowHandle = [IntPtr]1234
    $process.MainWindowTitle = 'MarkdownRenderer selection-disable probe'
    $observation = Get-SelectionDisableLaunchObservation -Process $process
    Assert-True ($observation.status -ceq 'window-candidate-observed' -and -not $observation.processExited -and $observation.mainWindowHandle -eq 1234) 'The exact probe title/window handle was not represented as a candidate-only observation.'

    $process.MainWindowTitle = 'MarkdownRenderer — Feature Showcase'
    $observation = Get-SelectionDisableLaunchObservation -Process $process
    Assert-True ($observation.status -ceq 'process-live-no-window-observed' -and -not $observation.processExited -and $null -eq $observation.exitCode) 'A different window title was treated as the selection-disable probe window.'

    $script:MockLaunchProcess = $null
    $script:MockLaunchCalls = [System.Collections.Generic.List[object]]::new()
    $script:MockSourceIdentity = [pscustomobject]@{
        processId=101;processName='MarkdownRenderer.Sample';executablePath='C:\fixture\MarkdownRenderer.Sample.exe';startTimeUtc='2026-10-14T12:00:00.0000000Z'
    }
    $script:MockChildIdentity = [pscustomobject]@{
        processId=202;processName='MarkdownRenderer.Sample';executablePath='C:\fixture\MarkdownRenderer.Sample.exe';startTimeUtc='2026-10-14T12:01:00.0000000Z'
    }
    $script:MockIdentityFailureForPid = 0
    function Get-GuestSampleProcessIdentity {
        param([Parameter(Mandatory)][int]$ExpectedProcessId)
        if ($ExpectedProcessId -eq 101) { return $script:MockSourceIdentity }
        if ($ExpectedProcessId -eq $script:MockIdentityFailureForPid) { throw 'mock PID-path query failed' }
        if ($ExpectedProcessId -in @(202,203,204)) { return $script:MockChildIdentity }
        throw "Unexpected process ID $ExpectedProcessId in the offline launcher test."
    }
    $script:MockOriginalProcess = [pscustomobject]@{
        Id=101;HasExited=$false;ProcessName='MarkdownRenderer.Sample';Path='C:\fixture\MarkdownRenderer.Sample.exe'
        StartTime=[DateTime]::Parse('2026-10-14T12:00:00.0000000Z').ToUniversalTime()
    }
    function Start-Process {
        [CmdletBinding()]
        param(
            [Parameter(Mandatory)][string]$FilePath,
            [Parameter(Mandatory)][string]$WorkingDirectory,
            [Parameter(Mandatory)][string[]]$ArgumentList,
            [switch]$PassThru,
            [ValidateSet('Normal','Hidden')][string]$WindowStyle,
            [string]$RedirectStandardOutput,
            [string]$RedirectStandardError
        )
        $script:MockLaunchCalls.Add([pscustomobject]@{
            filePath=$FilePath;workingDirectory=$WorkingDirectory;arguments=@($ArgumentList);passThru=[bool]$PassThru
            windowStyle=$WindowStyle;stdout=$RedirectStandardOutput;stderr=$RedirectStandardError
        })
        if (-not $RedirectStandardOutput -or -not $RedirectStandardError) { throw 'Live test launch did not provide both redirected diagnostic paths.' }
        [IO.File]::WriteAllText($RedirectStandardOutput,'mock stdout')
        [IO.File]::WriteAllText($RedirectStandardError,'mock stderr')
        return $script:MockLaunchProcess
    }
    function Get-Process {
        param([Parameter(Mandatory)][int]$Id)
        if ($Id -in @(202,203,204)) { return $script:MockLaunchProcess }
        if ($Id -eq 101) { return $script:MockOriginalProcess }
        return $null
    }
    $identityFunction = Get-FunctionAst -Ast $guestAst -Name 'Get-GuestSampleProcessIdentity'
    $handleIdentityFunction = Get-FunctionAst -Ast $guestAst -Name 'Get-SelectionDisableLaunchIdentityFromHandle'
    Invoke-Expression $identityFunction.Extent.Text
    Invoke-Expression $handleIdentityFunction.Extent.Text
    function Get-GuestSampleProcessIdentity {
        param([Parameter(Mandatory)][int]$ExpectedProcessId)
        if ($ExpectedProcessId -eq 101) { return $script:MockSourceIdentity }
        if ($ExpectedProcessId -eq $script:MockIdentityFailureForPid) { throw 'mock PID-path query failed' }
        if ($ExpectedProcessId -in @(202,203,204)) { return $script:MockChildIdentity }
        throw "Unexpected process ID $ExpectedProcessId in the offline launcher test."
    }
    Invoke-Expression $launchFunction.Extent.Text

    $launchRunId = '20261014T120004Z-' + [guid]::NewGuid().ToString('N')
    [void][IO.Directory]::CreateDirectory((Join-Path (Join-Path $tempRoot 'correctness') $launchRunId))
    $script:MockLaunchProcess = [pscustomobject]@{
        Id=202;HasExited=$false;ExitCode=0;MainWindowHandle=[IntPtr]::Zero;MainWindowTitle='';ProcessName='MarkdownRenderer.Sample';Path='C:\fixture\MarkdownRenderer.Sample.exe'
        StartTime=[DateTime]::Parse('2026-10-14T12:01:00.0000000Z').ToUniversalTime()
    }
    $script:MockLaunchProcess | Add-Member -MemberType ScriptMethod -Name Refresh -Value { }
    $launch = Start-SelectionDisableProbeProcess -OriginalProcessId 101 -RunId $launchRunId -GuestWorkRoot $tempRoot -StartupObservationMilliseconds 1
    Assert-True ($launch.processId -eq 202 -and $launch.sourceRetained -and $launch.startupObservation.status -ceq 'process-live-no-window-observed') 'A live child without an observed window was reported as an exit or identity failure.'
    Assert-True ($script:MockLaunchCalls.Count -eq 1 -and $script:MockLaunchCalls[0].arguments.Count -eq 1 -and $script:MockLaunchCalls[0].arguments[0] -ceq '--renderer-selection-disable-probe' -and $script:MockLaunchCalls[0].passThru) 'The offline launcher did not make exactly one fixed-argument child launch while retaining its process handle.'
    $expectedLaunchDirectory = Join-Path (Join-Path (Join-Path $tempRoot 'correctness') $launchRunId) 'selection-disable-launch'
    Assert-True ($script:MockLaunchCalls[0].stdout -ceq (Join-Path $expectedLaunchDirectory 'stdout.txt') -and $script:MockLaunchCalls[0].stderr -ceq (Join-Path $expectedLaunchDirectory 'stderr.txt') -and $launch.diagnostics.guestRelativeDirectory -ceq "correctness\$launchRunId\selection-disable-launch") 'The child stdout/stderr redirects do not point at the fresh run-scoped files.'

    $fallbackRunId = '20261014T120005Z-' + [guid]::NewGuid().ToString('N')
    [void][IO.Directory]::CreateDirectory((Join-Path (Join-Path $tempRoot 'correctness') $fallbackRunId))
    $script:MockLaunchProcess = [pscustomobject]@{
        Id=204;HasExited=$false;ExitCode=0;MainWindowHandle=[IntPtr]::Zero;MainWindowTitle='';ProcessName='MarkdownRenderer.Sample';Path='C:\fixture\MarkdownRenderer.Sample.exe'
        StartTime=[DateTime]::Parse('2026-10-14T12:01:30.0000000Z').ToUniversalTime()
    }
    $script:MockLaunchProcess | Add-Member -MemberType ScriptMethod -Name Refresh -Value { }
    $script:MockChildIdentity = [pscustomobject]@{
        processId=204;processName='MarkdownRenderer.Sample';executablePath='C:\fixture\MarkdownRenderer.Sample.exe';startTimeUtc='2026-10-14T12:01:30.0000000Z'
    }
    $script:MockIdentityFailureForPid = 204
    $handleFallback = Start-SelectionDisableProbeProcess -OriginalProcessId 101 -RunId $fallbackRunId -GuestWorkRoot $tempRoot -StartupObservationMilliseconds 1
    Assert-True ($handleFallback.processId -eq 204 -and $handleFallback.sourceRetained -and $handleFallback.processIdentitySource -ceq 'retained-start-process-handle') 'The Start-Process handle did not preserve the exact launch identity when the guest PID/path lookup failed.'
    $script:MockIdentityFailureForPid = 0

    $secondRunId = '20261014T120001Z-' + [guid]::NewGuid().ToString('N')
    [void][IO.Directory]::CreateDirectory((Join-Path (Join-Path $tempRoot 'correctness') $secondRunId))
    $script:MockLaunchProcess = [pscustomobject]@{
        Id=203;HasExited=$true;ExitCode=42;MainWindowHandle=[IntPtr]::Zero;MainWindowTitle='';ProcessName='MarkdownRenderer.Sample';Path='C:\fixture\MarkdownRenderer.Sample.exe'
        StartTime=[DateTime]::Parse('2026-10-14T12:02:00.0000000Z').ToUniversalTime()
    }
    $script:MockLaunchProcess | Add-Member -MemberType ScriptMethod -Name Refresh -Value { }
    $script:MockChildIdentity = [pscustomobject]@{
        processId=203;processName='MarkdownRenderer.Sample';executablePath='C:\fixture\MarkdownRenderer.Sample.exe';startTimeUtc='2026-10-14T12:02:00.0000000Z'
    }
    $script:MockLaunchCalls.Clear()
    $earlyExit = Start-SelectionDisableProbeProcess -OriginalProcessId 101 -RunId $secondRunId -GuestWorkRoot $tempRoot -StartupObservationMilliseconds 100
    Assert-True ($earlyExit.startupObservation.processExited -and $earlyExit.startupObservation.exitCode -eq 42 -and $earlyExit.startupObservation.status -ceq 'process-exited-before-window-observation') 'The retained process handle did not return the exact early child exit code.'
    Assert-True ($script:MockLaunchCalls.Count -eq 1 -and $earlyExit.diagnostics.guestRelativeDirectory -ceq "correctness\$secondRunId\selection-disable-launch") 'An early child exit changed the one-launch/run-scoped diagnostic contract.'

    $script:RunId = '20261014T120002Z-' + [guid]::NewGuid().ToString('N')
    $script:RunDirectory = Join-Path $tempRoot 'host-run'
    [void][IO.Directory]::CreateDirectory($script:RunDirectory)
    $sourceHashFunction = Get-FunctionAst -Ast $harnessAst -Name 'Get-JsonPropertyValue'
    $saveFunction = Get-FunctionAst -Ast $harnessAst -Name 'Save-SelectionDisableLaunchDiagnostics'
    Invoke-Expression $sourceHashFunction.Extent.Text
    $script:PullCalls = [System.Collections.Generic.List[object]]::new()
    function Invoke-NativeChecked {
        param([Parameter(Mandatory)][string[]]$Arguments)
        $script:PullCalls.Add(@($Arguments))
        if ($Arguments.Count -ne 5 -or $Arguments[0] -cne 'target' -or $Arguments[1] -cne 'pull' -or $Arguments[2] -cne 'sandbox') {
            throw 'Diagnostic export attempted a non-scoped target-pull command.'
        }
        [IO.File]::WriteAllText($Arguments[4],"mock capture for $($Arguments[3])")
        return [pscustomobject]@{exitCode=0}
    }
    Invoke-Expression $saveFunction.Extent.Text
    $launchDiagnostics = [pscustomobject]@{
        diagnostics=[pscustomobject]@{
            guestStdoutRelativePath="correctness\$script:RunId\selection-disable-launch\stdout.txt"
            guestStderrRelativePath="correctness\$script:RunId\selection-disable-launch\stderr.txt"
        }
    }
    $captured = Save-SelectionDisableLaunchDiagnostics -Launch $launchDiagnostics -CaptureReason 'process-exit'
    Assert-True ($captured.status -ceq 'CAPTURED' -and @($captured.files).Count -eq 2 -and $script:PullCalls.Count -eq 2) 'Valid run-confined guest logs were not exported as two distinct host artifacts.'
    Assert-True (@($captured.files | Where-Object { -not (Test-Path -LiteralPath $_.hostPath -PathType Leaf) }).Count -eq 0) 'The host diagnostic receipt named missing files.'
    $pullCountBefore=$script:PullCalls.Count
    $again=Save-SelectionDisableLaunchDiagnostics -Launch $launchDiagnostics -CaptureReason 'process-exit'
    Assert-True ($again.status -ceq 'UNAVAILABLE' -and $script:PullCalls.Count -eq $pullCountBefore) 'The host diagnostic export overwrote an existing run receipt.'

    $script:RunId = '20261014T120003Z-' + [guid]::NewGuid().ToString('N')
    $script:RunDirectory = Join-Path $tempRoot 'host-run-invalid-path'
    [void][IO.Directory]::CreateDirectory($script:RunDirectory)
    $badLaunch = [pscustomobject]@{
        diagnostics=[pscustomobject]@{
            guestStdoutRelativePath="correctness\$script:RunId\..\outside.txt"
            guestStderrRelativePath="correctness\$script:RunId\selection-disable-launch\stderr.txt"
        }
    }
    $pullCountBefore=$script:PullCalls.Count
    $partial=Save-SelectionDisableLaunchDiagnostics -Launch $badLaunch -CaptureReason 'window-discovery-timeout'
    Assert-True ($partial.status -ceq 'PARTIAL' -and @($script:PullCalls).Count -eq $pullCountBefore+1 -and @($partial.files | Where-Object status -eq 'REJECTED').Count -eq 1) 'A traversal-shaped guest output path was followed or not rejected.'

    $windowLoop = Get-FunctionAst -Ast $harnessAst -Name 'Run-SelectionDisableInvestigation'
    $windowLoopText = $windowLoop.Extent.Text
    $stateCheckIndex = $windowLoopText.IndexOf("Invoke-GuestProbe -Mode 'process-state' -TargetProcessId `$launchPid",[StringComparison]::Ordinal)
    $exitBranchIndex = $windowLoopText.IndexOf('if (-not [bool]$state.exists)',[StringComparison]::Ordinal)
    $windowPollSleepIndex = $windowLoopText.IndexOf('Start-Sleep -Milliseconds 250',$stateCheckIndex,[StringComparison]::Ordinal)
    Assert-True ($stateCheckIndex -ge 0 -and $exitBranchIndex -gt $stateCheckIndex -and $windowPollSleepIndex -gt $exitBranchIndex) 'Window discovery must verify exact process state and fail on child exit before continuing its poll.'

    'PASS: run-scoped logs, one-child process-handle/exitcode observation, exact-title candidate distinction, early-exit discovery, and no-overwrite/path-traversal guards.'
}
finally {
    $fullTempRoot = [IO.Path]::GetFullPath($tempRoot)
    $systemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($fullTempRoot.StartsWith($systemTemp,[StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($fullTempRoot).StartsWith('selection-disable-launch-diagnostics-',[StringComparison]::Ordinal)) {
        [IO.Directory]::Delete($fullTempRoot,$true)
    }
}
