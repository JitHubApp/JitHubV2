using System.Diagnostics;
using FlaUI.Core;
using JitHub.WinUI.Automation.Logging;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.AutomationDataRoot;

namespace JitHub.WinUI.Automation.Applications;

/// <summary>
/// Launches or attaches to the app under test with an automation data root and waits for its visible process.
/// </summary>
internal static class ProbeApplicationLauncher
{
    internal static Application CreateProbeApplication(CaptureOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.AttachProcess))
        {
            string attachValue = options.AttachProcess.Trim();
            if (int.TryParse(attachValue, out int processId))
            {
                return Application.Attach(processId);
            }

            Process[] matches = Process.GetProcessesByName(attachValue);
            try
            {
                Process[] visibleMatches = matches
                    .Where(static process => process.MainWindowHandle != IntPtr.Zero)
                    .ToArray();
                if (visibleMatches.Length == 0)
                {
                    throw new InvalidOperationException($"Could not find a visible process '{attachValue}' to attach automation.");
                }

                if (visibleMatches.Length != 1)
                {
                    string processIds = string.Join(", ", visibleMatches.Select(static process => process.Id));
                    throw new InvalidOperationException(
                        $"Process name '{attachValue}' is ambiguous ({processIds}). Pass --attach-process=<pid> instead.");
                }

                return Application.Attach(visibleMatches[0].Id);
            }
            finally
            {
                foreach (Process match in matches)
                {
                    match.Dispose();
                }
            }
        }

        return LaunchApplication(options.AppPath);
    }

    internal static Application LaunchApplication(string appPath, params string[] arguments) =>
        LaunchApplicationWithDataRoot(appPath, GetAutomationDataRoot(), killExisting: true, arguments);

    // This method stays whole because one launch start time and one set of excluded process IDs are shared across
    // launching, registering, waiting for the visible process, terminating and unregistering on failure, and re-
    // registering an attached process when the window belongs to a different PID.
    internal static Application LaunchApplicationWithDataRoot(
        string appPath,
        string dataRoot,
        bool killExisting,
        params string[] arguments)
    {
        if (killExisting)
        {
            KillExistingApplicationInstances(appPath);
        }

        Directory.CreateDirectory(dataRoot);
        DateTime launchStartedUtc = DateTime.UtcNow;
        HashSet<int> excludedProcessIds = GetApplicationProcessIds(appPath);
        var processStartInfo = new ProcessStartInfo(appPath)
        {
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false
        };

        foreach (string argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        AddPreviewEnvironment(processStartInfo, arguments, dataRoot);
        Application launched = Application.Launch(processStartInfo);
        int launchedProcessId = TryGetApplicationProcessId(launched);
        AutomationLifecycleLog.Write(
            "launch-started",
            $"pid={launchedProcessId}; path={Path.GetFullPath(appPath)}; args={string.Join(' ', arguments)}");
        var launchedRegistration = new AutomationApplicationRegistration(
            Path.GetFullPath(appPath),
            launchedProcessId,
            launchStartedUtc,
            excludedProcessIds);
        AutomationApplicationPathRegistry.Register(
            launched,
            appPath,
            launchedProcessId,
            launchStartedUtc,
            excludedProcessIds);

        int stableProcessId;
        try
        {
            stableProcessId = WaitForVisibleApplicationProcess(
                appPath,
                launchedProcessId,
                launchStartedUtc,
                excludedProcessIds,
                TimeSpan.FromSeconds(15));
        }
        catch
        {
            bool terminated = TryTerminateOwnedProcess(launchedRegistration);
            AutomationLifecycleLog.Write(
                "launch-failed-cleanup",
                $"pid={launchedProcessId}; terminated={terminated}; path={Path.GetFullPath(appPath)}");
            AutomationApplicationPathRegistry.Forget(launched);
            launched.Dispose();
            throw;
        }

        if (stableProcessId == launchedProcessId)
        {
            return launched;
        }

        AutomationApplicationPathRegistry.Forget(launched);
        try
        {
            launched.Dispose();
        }
        catch
        {
        }

        Application attached = Application.Attach(stableProcessId);
        AutomationApplicationPathRegistry.Register(
            attached,
            appPath,
            stableProcessId,
            launchStartedUtc,
            excludedProcessIds);
        AutomationLifecycleLog.Write(
            "launch-handoff",
            $"launchedPid={launchedProcessId}; windowPid={stableProcessId}; path={Path.GetFullPath(appPath)}");
        return attached;
    }

    internal static int TryGetApplicationProcessId(Application application)
    {
        try
        {
            return application.ProcessId;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private static int WaitForVisibleApplicationProcess(
        string appPath,
        int preferredProcessId,
        DateTime launchStartedUtc,
        IReadOnlySet<int> excludedProcessIds,
        TimeSpan timeout)
    {
        string processName = Path.GetFileNameWithoutExtension(appPath);
        string normalizedAppPath = Path.GetFullPath(appPath);
        Stopwatch stopwatch = Stopwatch.StartNew();
        do
        {
            bool preferredIsAlive = false;
            if (TryGetOwnedProcess(preferredProcessId, normalizedAppPath, launchStartedUtc, out Process? preferred) &&
                preferred is not null)
            {
                using (preferred)
                {
                    preferredIsAlive = true;
                    if (preferred.MainWindowHandle != IntPtr.Zero)
                    {
                        return preferredProcessId;
                    }
                }
            }

            if (preferredIsAlive)
            {
                Thread.Sleep(100);
                continue;
            }

            foreach (Process process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (excludedProcessIds.Contains(process.Id) ||
                        !IsProcessForApplication(process, normalizedAppPath) ||
                        process.HasExited ||
                        process.StartTime.ToUniversalTime() < launchStartedUtc.AddSeconds(-1))
                    {
                        continue;
                    }

                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        return process.Id;
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

            Thread.Sleep(100);
        }
        while (stopwatch.Elapsed < timeout);

        throw new TimeoutException(
            $"Timed out after {timeout.TotalSeconds:0.#} seconds waiting for a visible JitHub window " +
            $"from owned process {preferredProcessId} at '{normalizedAppPath}'.");
    }

    private static void AddPreviewEnvironment(
        ProcessStartInfo processStartInfo,
        IEnumerable<string> arguments,
        string? dataRoot = null)
    {
        processStartInfo.Environment["JITHUB_AUTOMATION_DATA_ROOT"] = dataRoot ?? GetAutomationDataRoot();
        foreach (string argument in arguments)
        {
            if (argument.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
            {
                processStartInfo.Environment["JITHUB_PREVIEW_PAGE"] = argument[7..];
            }
            else if (argument.StartsWith("--scenario=", StringComparison.OrdinalIgnoreCase))
            {
                processStartInfo.Environment["JITHUB_PREVIEW_SCENARIO"] = argument[11..];
            }
            else if (argument.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
            {
                processStartInfo.Environment["JITHUB_PREVIEW_THEME"] = argument[8..];
            }
            else if (argument.StartsWith("--palette=", StringComparison.OrdinalIgnoreCase))
            {
                processStartInfo.Environment["JITHUB_PREVIEW_PALETTE"] = argument[10..];
            }
            else if (argument.StartsWith("--repo=", StringComparison.OrdinalIgnoreCase))
            {
                processStartInfo.Environment["JITHUB_PREVIEW_REPOSITORY"] = argument[7..];
            }
            else if (argument.StartsWith("--repository=", StringComparison.OrdinalIgnoreCase))
            {
                processStartInfo.Environment["JITHUB_PREVIEW_REPOSITORY"] = argument[13..];
            }
            else if (argument.StartsWith("--branch=", StringComparison.OrdinalIgnoreCase))
            {
                processStartInfo.Environment["JITHUB_PREVIEW_BRANCH"] = argument[9..];
            }
            else if (string.Equals(argument, "--high-contrast", StringComparison.OrdinalIgnoreCase))
            {
                processStartInfo.Environment["JITHUB_AUTOMATION_HIGH_CONTRAST"] = "1";
            }
            else if (string.Equals(argument, "--large-commit", StringComparison.OrdinalIgnoreCase))
            {
                processStartInfo.Environment["JITHUB_AUTOMATION_LARGE_COMMIT"] = "1";
            }
            else if (string.Equals(argument, "--network-disabled", StringComparison.OrdinalIgnoreCase))
            {
                const string blockedProxy = "http://127.0.0.1:9";
                processStartInfo.Environment["JITHUB_AUTOMATION_NETWORK_DISABLED"] = "1";
                processStartInfo.Environment["HTTP_PROXY"] = blockedProxy;
                processStartInfo.Environment["HTTPS_PROXY"] = blockedProxy;
                processStartInfo.Environment["ALL_PROXY"] = blockedProxy;
                processStartInfo.Environment["NO_PROXY"] = string.Empty;
            }
        }
    }
}
