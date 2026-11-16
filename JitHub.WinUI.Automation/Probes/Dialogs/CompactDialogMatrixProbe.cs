using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Tools;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Probes.Dialogs.CompactFormDialogMatrices;
using static JitHub.WinUI.Automation.Probes.Dialogs.CompactStarsCategoryDialogMatrix;
using static JitHub.WinUI.Automation.Probes.Dialogs.CompactWorkItemDialogMatrix;

namespace JitHub.WinUI.Automation.Probes.Dialogs;

/// <summary>
/// Runs the compact-dialog-matrix probe: selects the dialog groups, launches the app per group and writes the matrix report.
/// </summary>
internal static class CompactDialogMatrixProbe
{
    public const string ProbeName = "compact-dialog-matrix";

    internal static readonly CompactViewport[] CompactViewports =
    [
        new(900, 700),
        new(760, 650),
        new(640, 600)
    ];

    private static readonly List<DialogMatrixResult> Results = [];
    private static string _runDataRoot = string.Empty;

    public static void Run(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.OutputDirectory);
        Results.Clear();
        _runDataRoot = Path.Combine(
            Path.GetTempPath(),
            "JitHub.WinUI.Automation",
            $"compact-dialog-matrix-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_runDataRoot);

        try
        {
            string? selectedGroup = Environment.GetEnvironmentVariable("JITHUB_COMPACT_DIALOG_GROUP");
            RunSelectedGroup(selectedGroup, "shell-new-repository", () => RunShellNewRepositoryMatrix(options));
            RunSelectedGroup(selectedGroup, "widget-customize", () => RunWidgetCustomizeMatrix(options));
            RunSelectedGroup(selectedGroup, "profile-edit", () => RunProfileEditMatrix(options));
            RunSelectedGroup(selectedGroup, "repository-delete", () => RunRepositoryDeleteMatrix(options));
            RunSelectedGroup(selectedGroup, "stars-categories", () => RunStarsCategoryMatrix(options));
            RunSelectedGroup(selectedGroup, "issue-pr-production-previews", () => RunIssueAndPullRequestPreviewMatrix(options));
        }
        finally
        {
            WriteReport(options.OutputDirectory);
            TryDeleteDirectory(_runDataRoot);
        }

        DialogMatrixResult[] failures = Results
            .Where(static result => string.Equals(result.Status, "failed", StringComparison.Ordinal))
            .ToArray();
        if (failures.Length > 0)
        {
            throw new InvalidOperationException(
                $"Compact dialog matrix failed {failures.Length} row(s): " +
                string.Join(" | ", failures.Select(static result => $"{result.Surface}: {result.Detail}")));
        }

        int passed = Results.Count(static result => string.Equals(result.Status, "passed", StringComparison.Ordinal));
        int blocked = Results.Count(static result => string.Equals(result.Status, "blocked", StringComparison.Ordinal));
        Console.WriteLine(
            $"compact-dialog-matrix: passed={passed}; blocked={blocked}; " +
            $"report={Path.Combine(options.OutputDirectory, "compact-dialog-matrix-results.json")}");
    }

    private static void RunSelectedGroup(string? selectedGroup, string group, Action action)
    {
        if (string.IsNullOrWhiteSpace(selectedGroup) ||
            string.Equals(selectedGroup, group, StringComparison.OrdinalIgnoreCase))
        {
            RunGroup(group, action);
        }
    }

    private static void RunGroup(string surface, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Results.Add(new DialogMatrixResult(
                surface,
                "group",
                "failed",
                exception.Message,
                exception.ToString()));
        }
    }

    internal static ProbeApplication Launch(CaptureOptions options, string dataKey, params string[] arguments)
    {
        CloseExistingAppProcesses(options.AppPath);
        string dataRoot = Path.Combine(_runDataRoot, dataKey);
        Directory.CreateDirectory(dataRoot);
        ProcessStartInfo startInfo = new(options.AppPath)
        {
            WorkingDirectory = Path.GetDirectoryName(options.AppPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false
        };
        startInfo.Environment["JITHUB_AUTOMATION_DATA_ROOT"] = dataRoot;
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
            if (argument.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment["JITHUB_PREVIEW_PAGE"] = argument[7..];
            }
            else if (argument.StartsWith("--scenario=", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment["JITHUB_PREVIEW_SCENARIO"] = argument[11..];
            }
            else if (argument.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment["JITHUB_PREVIEW_THEME"] = argument[8..];
            }
            else if (argument.StartsWith("--repo=", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment["JITHUB_PREVIEW_REPOSITORY"] = argument[7..];
            }
        }

        Application application = Application.Launch(startInfo);
        UIA3Automation automation = new();
        try
        {
            Window window = Retry.WhileNull(
                () => application.GetMainWindow(automation),
                TimeSpan.FromSeconds(18),
                TimeSpan.FromMilliseconds(100),
                ignoreException: true).Result
                ?? throw new InvalidOperationException($"JitHub did not expose a main window for {dataKey}.");
            window.SetForeground();
            return new ProbeApplication(application, automation, window);
        }
        catch
        {
            automation.Dispose();
            application.Dispose();
            throw;
        }
    }

    internal static void Pass(string surface, CompactViewport viewport, string detail) =>
        Results.Add(new DialogMatrixResult(surface, viewport.Token, "passed", detail, string.Empty));

    private static void WriteReport(string outputDirectory)
    {
        string path = Path.Combine(outputDirectory, "compact-dialog-matrix-results.json");
        var report = new
        {
            schemaVersion = 1,
            probe = ProbeName,
            generatedAt = DateTimeOffset.UtcNow,
            compactViewports = CompactViewports.Select(static viewport => viewport.Token).ToArray(),
            summary = new
            {
                passed = Results.Count(static result => result.Status == "passed"),
                blocked = Results.Count(static result => result.Status == "blocked"),
                failed = Results.Count(static result => result.Status == "failed")
            },
            results = Results
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void CloseExistingAppProcesses(string appPath)
    {
        string processName = Path.GetFileNameWithoutExtension(appPath);
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                _ = process.CloseMainWindow();
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal sealed class ProbeApplication : IDisposable
    {
        public ProbeApplication(Application application, UIA3Automation automation, Window window)
        {
            Application = application;
            Automation = automation;
            Window = window;
        }

        public Application Application { get; }

        public UIA3Automation Automation { get; }

        public Window Window { get; }

        public void Dispose()
        {
            try
            {
                if (!Application.HasExited)
                {
                    Window.Close();
                    Retry.WhileFalse(
                        () => Application.HasExited,
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromMilliseconds(100),
                        ignoreException: true);
                }
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
            }
            finally
            {
                Automation.Dispose();
                Application.Dispose();
            }
        }
    }

    internal readonly record struct CompactViewport(int Width, int Height)
    {
        public string Token => $"{Width}x{Height}";
    }

    private sealed record DialogMatrixResult(
        string Surface,
        string Viewport,
        string Status,
        string Detail,
        string Evidence);
}
