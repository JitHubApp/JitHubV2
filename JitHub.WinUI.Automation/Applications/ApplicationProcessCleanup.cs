using System.Diagnostics;
using FlaUI.Core;
using JitHub.WinUI.Automation.Logging;
using JitHub.WinUI.Automation.Native;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;

namespace JitHub.WinUI.Automation.Applications;

/// <summary>
/// Closes the app under test and terminates only the processes this harness launched.
/// </summary>
internal static class ApplicationProcessCleanup
{
    // This method stays whole because it is one close protocol for a single owned process: prove ownership from the
    // registry, open an exit handle, send WM_CLOSE to its top-level window, wait for exit, check the exit code, and
    // release that handle in one finally block.
    internal static void TryClose(Application app)
    {
        if (!AutomationApplicationPathRegistry.TryGet(app, out AutomationApplicationRegistration? registration) ||
            registration is null)
        {
            int unknownProcessId = TryGetApplicationProcessId(app);
            AutomationLifecycleLog.Write(
                "close-incomplete",
                $"pid={unknownProcessId}; result=ownership-not-proven");
            throw new InvalidOperationException(
                $"Refusing to report a successful close for unregistered process {unknownProcessId}.");
        }

        int processId = registration.ProcessId;
        if (!TryGetOwnedProcess(
                registration.ProcessId,
                registration.AppPath,
                registration.LaunchStartedUtc,
                out Process? ownedProcess) ||
            ownedProcess is null)
        {
            try
            {
                using Process observedProcess = Process.GetProcessById(processId);
                if (observedProcess.HasExited)
                {
                    AutomationLifecycleLog.Write(
                        "close-confirmed",
                        $"pid={processId}; result=already-exited");
                    AutomationApplicationPathRegistry.Forget(app);
                    return;
                }
            }
            catch (ArgumentException)
            {
                AutomationLifecycleLog.Write(
                    "close-confirmed",
                    $"pid={processId}; result=already-exited");
                AutomationApplicationPathRegistry.Forget(app);
                return;
            }

            AutomationLifecycleLog.Write(
                "close-incomplete",
                $"pid={processId}; result=owned-process-not-provable");
            throw new InvalidOperationException(
                $"Could not prove the exact owned process {processId} before requesting close.");
        }

        IntPtr processExitHandle = IntPtr.Zero;
        using (ownedProcess)
        try
        {
            processExitHandle = NativeMethods.OpenProcessExitHandle(processId);
            if (!NativeMethods.TryRequestGracefulClose(processId, out IntPtr windowHandle))
            {
                AutomationLifecycleLog.Write(
                    "close-request-failed",
                    $"pid={processId}; result=owned-top-level-window-not-found-or-close-rejected");
                throw new InvalidOperationException(
                    $"Could not request a graceful close for the top-level window owned by process {processId}.");
            }

            AutomationLifecycleLog.Write(
                "close-requested",
                $"pid={processId}; hwnd=0x{windowHandle.ToInt64():X}; transport=WM_CLOSE");

            bool exited = ownedProcess.HasExited || ownedProcess.WaitForExit(TimeSpan.FromSeconds(12));
            if (!exited)
            {
                AutomationLifecycleLog.Write(
                    "close-incomplete",
                    $"pid={processId}; result=graceful-timeout");
                throw new InvalidOperationException(
                    $"Owned process {processId} did not exit after its graceful close request.");
            }

            int exitCode = unchecked((int)NativeMethods.GetProcessExitCode(processExitHandle));
            if (exitCode != 0)
            {
                AutomationLifecycleLog.Write(
                    "close-failed",
                    $"pid={processId}; result=abnormal-exit; exitCode={exitCode}");
                throw new InvalidOperationException(
                    $"Owned process {processId} exited abnormally with code {exitCode}.");
            }

            AutomationLifecycleLog.Write(
                "close-completed",
                $"pid={processId}; result=graceful; exitCode={exitCode}");
            AutomationApplicationPathRegistry.Forget(app);
        }
        catch (Exception exception)
        {
            if (exception is InvalidOperationException &&
                exception.Message.StartsWith("Owned process", StringComparison.Ordinal))
            {
                throw;
            }

            AutomationLifecycleLog.Write(
                "close-request-failed",
                $"pid={processId}; exception={exception.GetType().Name}; message={exception.Message}");
            throw new InvalidOperationException(
                $"Graceful close failed for owned process {processId}.",
                exception);
        }
        finally
        {
            NativeMethods.CloseProcessExitHandle(processExitHandle);
        }
    }

    internal static void KillExistingApplicationInstances(string appPath)
    {
        List<string> cleanupFailures = [];
        foreach (AutomationApplicationRegistration registration in
            AutomationApplicationPathRegistry.GetOwnedRegistrations(appPath))
        {
            if (WaitForProcessExit(registration.ProcessId, TimeSpan.FromSeconds(12)))
            {
                AutomationApplicationPathRegistry.Forget(registration.ProcessId);
                continue;
            }

            if (TryTerminateOwnedProcess(registration))
            {
                AutomationLifecycleLog.Write(
                    "owned-process-cleanup-failed",
                    $"pid={registration.ProcessId}; path={registration.AppPath}; result=forced-termination");
                cleanupFailures.Add($"forced termination of owned process {registration.ProcessId}");
            }
            else
            {
                AutomationLifecycleLog.Write(
                    "owned-process-cleanup-failed",
                    $"pid={registration.ProcessId}; path={registration.AppPath}; result=process-not-provable-or-termination-failed");
                cleanupFailures.Add($"unprovable cleanup of owned process {registration.ProcessId}");
            }

            AutomationApplicationPathRegistry.Forget(registration.ProcessId);
        }

        if (cleanupFailures.Count > 0)
        {
            throw new InvalidOperationException(
                "Automation required failed process cleanup: " + string.Join(", ", cleanupFailures));
        }
    }

    internal static HashSet<int> GetApplicationProcessIds(string appPath)
    {
        string processName = Path.GetFileNameWithoutExtension(appPath);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return [];
        }

        HashSet<int> processIds = [];
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (IsProcessForApplication(process, appPath) && !process.HasExited)
                {
                    processIds.Add(process.Id);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return processIds;
    }

    internal static bool TryGetOwnedProcess(
        int processId,
        string appPath,
        DateTime launchStartedUtc,
        out Process? process)
    {
        process = null;
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            Process candidate = Process.GetProcessById(processId);
            candidate.Refresh();
            if (candidate.HasExited ||
                !IsProcessForApplication(candidate, appPath) ||
                candidate.StartTime.ToUniversalTime() < launchStartedUtc.AddSeconds(-1))
            {
                candidate.Dispose();
                return false;
            }

            process = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.HasExited || process.WaitForExit((int)Math.Ceiling(timeout.TotalMilliseconds));
        }
        catch (ArgumentException)
        {
            // Process.GetProcessById throws when the owned process has already exited.
            // Absence is the successful state this cleanup wait is proving.
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    internal static bool TryTerminateOwnedProcess(AutomationApplicationRegistration registration)
    {
        if (!TryGetOwnedProcess(
                registration.ProcessId,
                registration.AppPath,
                registration.LaunchStartedUtc,
                out Process? process) ||
            process is null)
        {
            return false;
        }

        using (process)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                return process.WaitForExit(5000);
            }
            catch
            {
                return false;
            }
        }
    }

    internal static bool IsProcessForApplication(Process process, string appPath)
    {
        try
        {
            string? processPath = process.MainModule?.FileName;
            return !string.IsNullOrWhiteSpace(processPath) &&
                string.Equals(
                    Path.GetFullPath(processPath),
                    Path.GetFullPath(appPath),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
