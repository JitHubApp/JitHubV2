<#
.SYNOPSIS
Foregrounds each pinned Markdown performance harness process once during release preflight.

.DESCRIPTION
Run this on the physical host before starting the strict counterbalanced orchestrator.
It watches local harness process starts, resolves the process executable path, and
only handles the four frozen R1,C1,C2,R2 reference/candidate launches. For each match
it waits for the main HWND, invokes `winapp ui click TitleBar -a <PID>` exactly once,
and verifies the foreground process ID. It performs no UI CLI polling during trials.

The process watcher must be ready before the benchmark orchestrator starts.
Pass -ReadyFilePath and wait for that create-new marker before launching the run.
The coordinator fails closed on an unavailable/unsupported WinApp CLI, unresolved
process identity, focus refusal, foreground mismatch, missing expected launch, or
timeout. -StopFilePath lets a wrapper stop it if the strict orchestrator aborts early.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $ReferenceExecutablePath,
    [Parameter(Mandatory)][string] $CandidateExecutablePath,
    [ValidateRange(1, 30)][int] $PreflightTimeoutSeconds = 30,
    [ValidateRange(1, 240)][int] $CoordinatorTimeoutMinutes = 190,
    [string] $ReadyFilePath,
    [string] $StopFilePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'MarkdownWinUIForegroundCoordinator.psm1') -Force

function Invoke-LocalProcess {
    param(
        [Parameter(Mandatory)][string] $ExecutablePath,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][int] $TimeoutMilliseconds
    )

    if ($TimeoutMilliseconds -le 0) {
        throw 'A positive process timeout is required.'
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Could not start '$ExecutablePath'."
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutMilliseconds)) {
            try {
                $process.Kill($true)
            }
            catch {
                # Preserve the timeout result if the process exits concurrently.
            }
            $process.WaitForExit()
            throw "'$ExecutablePath' exceeded its $TimeoutMilliseconds ms timeout."
        }

        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StdOut = $stdout
            StdErr = $stderr
        }
    }
    finally {
        $process.Dispose()
    }
}

function Get-LocalForegroundProcessId {
    if (-not ('MarkdownWinUIForegroundCoordinatorNative' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class MarkdownWinUIForegroundCoordinatorNative
{
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint sourceThreadId, uint targetThreadId, bool attach);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    public static int GetForegroundProcessId()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return 0;

        GetWindowThreadProcessId(window, out uint processId);
        return unchecked((int)processId);
    }

    public static bool TryActivate(IntPtr targetWindow)
    {
        if (targetWindow == IntPtr.Zero)
            return false;

        IntPtr foregroundWindow = GetForegroundWindow();
        uint foregroundProcessId;
        uint foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out foregroundProcessId);
        uint currentThreadId = GetCurrentThreadId();
        bool attached = foregroundThreadId != 0 &&
            foregroundThreadId != currentThreadId &&
            AttachThreadInput(currentThreadId, foregroundThreadId, true);

        try
        {
            ShowWindowAsync(targetWindow, 9); // SW_RESTORE
            BringWindowToTop(targetWindow);
            return SetForegroundWindow(targetWindow);
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
        }
    }
}
'@
    }

    return [MarkdownWinUIForegroundCoordinatorNative]::GetForegroundProcessId()
}

function Get-ProcessExecutablePath {
    param(
        [Parameter(Mandatory)][int] $ProcessId,
        [Parameter(Mandatory)][DateTimeOffset] $ExpectedStartUtc,
        [Parameter(Mandatory)][DateTimeOffset] $DeadlineUtc
    )

    while ([DateTimeOffset]::UtcNow -lt $DeadlineUtc) {
        $process = Get-CimInstance -ClassName Win32_Process `
            -Filter "ProcessId = $ProcessId" `
            -ErrorAction SilentlyContinue
        if ($null -ne $process) {
            if (-not [string]::IsNullOrWhiteSpace([string]$process.CreationDate)) {
                $resolvedStartUtc = [DateTimeOffset]::new([DateTime]$process.CreationDate).ToUniversalTime()
                $startTimeDeltaSeconds = [Math]::Abs(($resolvedStartUtc - $ExpectedStartUtc).TotalSeconds)
                if ($startTimeDeltaSeconds -gt 2) {
                    throw "PID $ProcessId was reused or resolved to a different process instance (start-time delta ${startTimeDeltaSeconds}s)."
                }

                if (-not [string]::IsNullOrWhiteSpace([string]$process.ExecutablePath)) {
                    return [IO.Path]::GetFullPath([string]$process.ExecutablePath)
                }
            }
        }

        # Win32_ProcessStartTrace can be delivered before Win32_Process has
        # published the corresponding instance/path. Retry only this cheap
        # process lookup, bounded by the event's original 30-second deadline.
        Start-Sleep -Milliseconds 50
    }

    return $null
}

function Wait-ForHarnessMainWindow {
    param(
        [Parameter(Mandatory)][int] $ProcessId,
        [Parameter(Mandatory)][DateTimeOffset] $DeadlineUtc
    )

    while ([DateTimeOffset]::UtcNow -lt $DeadlineUtc) {
        $process = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($null -eq $process) {
            throw "Harness process $ProcessId exited before its main window became available."
        }

        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
            return
        }

        Start-Sleep -Milliseconds 50
    }

    throw "Harness process $ProcessId did not create a main window within its preflight window."
}

function Write-ReadyMarker {
    param([Parameter(Mandatory)][string] $Path)

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $stream = [IO.File]::Open(
        $resolvedPath,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(
            "foreground-coordinator-ready`n")
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally {
        $stream.Dispose()
    }
}

$referencePath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $ReferenceExecutablePath).Path)
$candidatePath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $CandidateExecutablePath).Path)
if ([string]::Equals($referencePath, $candidatePath, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Reference and candidate executable paths must be distinct.'
}
if ([IO.Path]::GetFileName($referencePath) -ine 'MarkdownRenderer.PerformanceHarness.exe' -or
    [IO.Path]::GetFileName($candidatePath) -ine 'MarkdownRenderer.PerformanceHarness.exe') {
    throw 'Both pinned paths must name MarkdownRenderer.PerformanceHarness.exe.'
}

$winappCommand = Get-Command -Name winapp -CommandType Application -ErrorAction Stop |
    Select-Object -First 1
$winappPath = $winappCommand.Source
if ([string]::IsNullOrWhiteSpace($winappPath) -or -not (Test-Path -LiteralPath $winappPath -PathType Leaf)) {
    throw 'WinApp CLI executable could not be resolved; refusing to start the coordinator.'
}

$versionResult = Invoke-LocalProcess -ExecutablePath $winappPath -Arguments @('--version') -TimeoutMilliseconds 10000
if ($versionResult.ExitCode -ne 0 -or
    -not (Test-MarkdownWinAppCliVersion -VersionText $versionResult.StdOut.Trim())) {
    throw "WinApp CLI 0.7 is required. Observed version output '$($versionResult.StdOut.Trim())'. $($versionResult.StdErr.Trim())"
}

# Compile the P/Invoke helper before publishing the ready marker. The first
# pinned process can arrive immediately after that marker becomes visible.
Get-LocalForegroundProcessId | Out-Null

$existingPinned = @(Get-Process -Name 'MarkdownRenderer.PerformanceHarness' -ErrorAction SilentlyContinue |
    Where-Object {
        -not [string]::IsNullOrWhiteSpace($_.Path) -and
        $null -ne (Get-MarkdownHarnessRole `
            -ExecutablePath $_.Path `
            -ReferenceExecutablePath $referencePath `
            -CandidateExecutablePath $candidatePath)
    })
if ($existingPinned.Count -ne 0) {
    throw 'A pinned harness process was already running before the coordinator started.'
}

if (-not [string]::IsNullOrWhiteSpace($ReadyFilePath)) {
    Write-ReadyMarker -Path $ReadyFilePath
}
Write-Host 'Foreground coordinator ready; waiting for the frozen R1,C1,C2,R2 process sequence.'

$focusedPids = [Collections.Generic.List[int]]::new()
$seenProcessStarts = [Collections.Generic.HashSet[string]]::new()
$coordinatorDeadlineUtc = [DateTimeOffset]::UtcNow.AddMinutes($CoordinatorTimeoutMinutes)

while ($focusedPids.Count -lt 4) {
    if (-not [string]::IsNullOrWhiteSpace($StopFilePath) -and
        (Test-Path -LiteralPath $StopFilePath)) {
        throw 'Foreground coordinator was stopped before observing all four harness launches.'
    }
    if ([DateTimeOffset]::UtcNow -ge $coordinatorDeadlineUtc) {
        throw "Coordinator timed out before observing all four harness launches after $CoordinatorTimeoutMinutes minutes."
    }

    $newProcess = $null
    foreach ($process in @(Get-Process -Name 'MarkdownRenderer.PerformanceHarness' -ErrorAction SilentlyContinue)) {
        try {
            $processStartUtc = [DateTimeOffset]$process.StartTime.ToUniversalTime()
            $processStartIdentity = "$($process.Id)|$($processStartUtc.UtcTicks)"
            if (-not $seenProcessStarts.Add($processStartIdentity)) {
                continue
            }
            $executablePath = $process.Path
            if ([string]::IsNullOrWhiteSpace($executablePath)) {
                throw "Could not resolve executable path for new performance harness PID $($process.Id)."
            }
            $role = Get-MarkdownHarnessRole `
                -ExecutablePath $executablePath `
                -ReferenceExecutablePath $referencePath `
                -CandidateExecutablePath $candidatePath
            if ($null -eq $role) {
                Write-Verbose "Ignoring non-pinned harness process PID $($process.Id) at '$executablePath'."
                continue
            }
            $newProcess = $process
            break
        }
        catch [System.InvalidOperationException] {
            # A non-pinned process may exit while its identity is being read.
            continue
        }
    }
    if ($null -eq $newProcess) {
        Start-Sleep -Milliseconds 200
        continue
    }

    $processId = $newProcess.Id
    $preflightDeadlineUtc = $processStartUtc.AddSeconds($PreflightTimeoutSeconds)
    if ([DateTimeOffset]::UtcNow -ge $preflightDeadlineUtc) {
        throw "Harness PID $processId reached its $PreflightTimeoutSeconds-second focus preflight deadline before polling observed it."
    }
    $expectedRole = Get-MarkdownExpectedHarnessRole -LaunchIndex $focusedPids.Count
    if ($role -cne $expectedRole) {
        throw "Observed $role harness PID $processId at sequence position $($focusedPids.Count + 1); expected $expectedRole."
    }

    Wait-ForHarnessMainWindow -ProcessId $processId -DeadlineUtc $preflightDeadlineUtc
    $newProcess.Refresh()
    if (-not [MarkdownWinUIForegroundCoordinatorNative]::TryActivate($newProcess.MainWindowHandle)) {
        throw "Windows refused to foreground pinned harness PID $processId before its UI verification."
    }
    if (-not (Test-MarkdownForegroundProcessId `
            -ForegroundProcessId (Get-LocalForegroundProcessId) `
            -TargetProcessId $processId)) {
        throw "Windows reported activation of pinned harness PID $processId but a different process stayed foreground."
    }
    $remainingMilliseconds = [int][Math]::Floor(
        ($preflightDeadlineUtc - [DateTimeOffset]::UtcNow).TotalMilliseconds)
    if ($remainingMilliseconds -le 0) {
        throw "Harness PID $processId exhausted its $PreflightTimeoutSeconds-second focus preflight before the click."
    }

    $clickArguments = New-MarkdownWinAppTitleBarClickArguments -ProcessId $processId
    $clickResult = Invoke-LocalProcess `
        -ExecutablePath $winappPath `
        -Arguments $clickArguments `
        -TimeoutMilliseconds $remainingMilliseconds
    if ($clickResult.ExitCode -ne 0) {
        throw "WinApp CLI refused to foreground/click TitleBar for harness PID $processId (exit $($clickResult.ExitCode)). $($clickResult.StdErr.Trim()) $($clickResult.StdOut.Trim())"
    }

    $foregroundProcessId = Get-LocalForegroundProcessId
    if (-not (Test-MarkdownForegroundProcessId `
            -ForegroundProcessId $foregroundProcessId `
            -TargetProcessId $processId)) {
        throw "WinApp CLI click returned success, but harness PID $processId was not foreground (observed PID $foregroundProcessId)."
    }

    $focusedPids.Add($processId)
    Write-Host "Foreground-qualified $role harness PID $processId ($($focusedPids.Count)/4); leaving it alone."

    # Wait for this trial to end before searching for the next launch. This keeps
    # process enumeration off the measured interval; no UI calls occur within it.
    while (-not $newProcess.WaitForExit(1000)) {
        if (-not [string]::IsNullOrWhiteSpace($StopFilePath) -and
            (Test-Path -LiteralPath $StopFilePath)) {
            throw "Foreground coordinator was stopped while harness PID $processId was running."
        }
    }
}

Write-Host 'All four harness launches received one successful foreground click.'
