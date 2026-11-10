using System.Diagnostics;
using System.Runtime.ExceptionServices;
using FlaUI.Core;
using static JitHub.WinUI.Automation.Probes.HighContrast.HighContrastPixelAssertions;

namespace JitHub.WinUI.Automation.Probes.HighContrast;

/// <summary>
/// Closes the app processes the high contrast probe launched and prepares its output directory.
/// </summary>
internal static class HighContrastApplicationLifecycle
{
    internal static void CloseOwnedApplication(Application app)
    {
        int processId = app.ProcessId;
        Exception? closeFailure = null;
        try
        {
            if (!app.HasExited)
            {
                app.Close();
            }
        }
        catch (Exception exception)
        {
            closeFailure = exception;
        }

        if (!WaitForProcessExit(processId, TimeSpan.FromSeconds(8)))
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
            }
            catch (Exception exception)
            {
                closeFailure = closeFailure is null
                    ? exception
                    : new AggregateException(closeFailure, exception);
            }
        }

        Assert(
            WaitForProcessExit(processId, TimeSpan.FromSeconds(5)),
            $"Owned JitHub process {processId} remained active after cleanup.");
        if (closeFailure is not null)
        {
            ExceptionDispatchInfo.Capture(closeFailure).Throw();
        }
    }

    private static bool WaitForProcessExit(int processId, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        do
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (process.HasExited)
                {
                    return true;
                }
            }
            catch (ArgumentException)
            {
                return true;
            }

            Thread.Sleep(100);
        }
        while (stopwatch.Elapsed < timeout);

        return false;
    }

    internal static void EnsureNoExistingApplicationProcess(string appPath)
    {
        string processName = Path.GetFileNameWithoutExtension(appPath);
        List<int> processIds = [];
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (!process.HasExited)
                {
                    processIds.Add(process.Id);
                }
            }
        }

        processIds.Sort();
        Assert(
            processIds.Count == 0,
            $"Refusing to disturb existing {processName} processes: {string.Join(", ", processIds)}.");
    }

    internal static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }
}
