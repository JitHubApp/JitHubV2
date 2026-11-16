using System.Diagnostics;
using System.Runtime.ExceptionServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Probes.HighContrast.HighContrastApplicationLifecycle;
using static JitHub.WinUI.Automation.Probes.HighContrast.HighContrastElements;
using static JitHub.WinUI.Automation.Probes.HighContrast.HighContrastPixelAssertions;
using static JitHub.WinUI.Automation.Probes.HighContrast.HighContrastSurfaceValidation;
using static JitHub.WinUI.Automation.Probes.HighContrast.SystemHighContrastSetting;

namespace JitHub.WinUI.Automation.Probes.HighContrast;

/// <summary>
/// Runs the high-contrast-live probe: turns on system high contrast, drives each page and dialog, and restores the prior setting.
/// </summary>
internal static class HighContrastLiveProbe
{
    internal static readonly Viewport Wide = new(1366, 900);
    internal static readonly Viewport Compact = new(760, 650);

    // This method stays whole because it owns the system High Contrast lifecycle: turn it on only if needed, run every
    // page check under it, then always restore or verify the prior setting and clean the data root, folding every
    // failure into one aggregate before rethrowing.
    public static void Run(CaptureOptions options)
    {
        Directory.CreateDirectory(options.OutputDirectory);
        EnsureNoExistingApplicationProcess(options.AppPath);

        NativeHighContrastSnapshot prior = ReadHighContrast();
        bool changedHighContrast = false;
        string dataRoot = Path.Combine(
            Path.GetTempPath(),
            "JitHub.WinUI.Automation",
            $"high-contrast-live-{Environment.ProcessId}");
        Exception? failure = null;

        try
        {
            RecreateDirectory(dataRoot);
            if (!prior.IsEnabled)
            {
                changedHighContrast = true;
                WriteHighContrast(prior with { Flags = prior.Flags | HcfHighContrastOn });
                WaitForHighContrast(
                    static state => state.IsEnabled,
                    "Windows High Contrast to become active");
                Thread.Sleep(1_250);
            }

            NativeHighContrastSnapshot active = ReadHighContrast();
            Assert(active.IsEnabled, "Windows did not report genuine High Contrast as active.");
            SystemColorPalette palette = SystemColorPalette.Capture();

            RunSettingsPage(options, dataRoot, palette);
            RunProfilePage(options, dataRoot, palette);
            RunMarkdownHostPage(options, dataRoot, palette);
            RunGistEditorDialog(options, dataRoot, palette);
            RunDashboardCustomShellDialog(options, dataRoot, palette);
            Console.WriteLine(
                "high-contrast-live probe: genuine Windows High Contrast, focus, selection, " +
                "destructive/editor/custom-shell dialogs, system colors, and responsive page captures passed; " +
                $"output={options.OutputDirectory}");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            if (changedHighContrast)
            {
                try
                {
                    RestoreHighContrast(prior);
                }
                catch (Exception restoreException)
                {
                    failure = failure is null
                        ? restoreException
                        : new AggregateException(failure, restoreException);
                }
            }
            else
            {
                try
                {
                    NativeHighContrastSnapshot current = ReadHighContrast();
                    Assert(
                        current == prior,
                        "Windows High Contrast changed during the probe even though the probe did not mutate it.");
                }
                catch (Exception verificationException)
                {
                    failure = failure is null
                        ? verificationException
                        : new AggregateException(failure, verificationException);
                }
            }

            try
            {
                if (failure is null && Directory.Exists(dataRoot))
                {
                    Directory.Delete(dataRoot, recursive: true);
                }
            }
            catch (Exception cleanupException)
            {
                failure = failure is null
                    ? cleanupException
                    : new AggregateException(failure, cleanupException);
            }
        }

        if (failure is not null)
        {
            File.WriteAllText(
                Path.Combine(options.OutputDirectory, "high-contrast-live-failure.txt"),
                failure.ToString());
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        string failurePath = Path.Combine(options.OutputDirectory, "high-contrast-live-failure.txt");
        if (File.Exists(failurePath))
        {
            File.Delete(failurePath);
        }
    }

    private static void RunSettingsPage(
        CaptureOptions options,
        string dataRoot,
        SystemColorPalette palette)
    {
        RunPage(
            options.AppPath,
            dataRoot,
            options.OutputDirectory,
            "settings",
            (window, _) =>
            {
                WaitForElement(
                    "Settings Appearance section",
                    () => FindVisible(window, "SettingsSection_appearance"));

                ValidateSettingsWide(window, options.OutputDirectory, palette);
                ValidateSettingsCompact(window, options.OutputDirectory, palette);
                ValidateDestructiveDialog(window, options.OutputDirectory, palette, Wide, "1366x900");
                ValidateDestructiveDialog(window, options.OutputDirectory, palette, Compact, "760x650");
            });
    }

    private static void RunProfilePage(
        CaptureOptions options,
        string dataRoot,
        SystemColorPalette palette)
    {
        RunPage(
            options.AppPath,
            dataRoot,
            options.OutputDirectory,
            "profile",
            (window, automation) =>
            {
                WaitForElement(
                    "Profile Overview mode",
                    () => FindVisible(window, "ProfileModeOverviewItem"));

                ValidateProfileViewport(
                    window,
                    Wide,
                    "ProfileEditButton",
                    palette,
                    Path.Combine(options.OutputDirectory, "high-contrast-live-profile-1366x900.png"),
                    "Profile wide Overview selection");
                ValidateContributionGraph(window, automation, options.OutputDirectory, palette);
                ValidateProfileViewport(
                    window,
                    Compact,
                    "ProfileCompactEditButton",
                    palette,
                    Path.Combine(options.OutputDirectory, "high-contrast-live-profile-760x650.png"),
                    "Profile compact Overview selection");
            });
    }

    private static void RunGistEditorDialog(
        CaptureOptions options,
        string dataRoot,
        SystemColorPalette palette)
    {
        RunPage(
            options.AppPath,
            dataRoot,
            options.OutputDirectory,
            "gists",
            (window, _) =>
            {
                ValidateGistEditorDialog(window, options.OutputDirectory, palette, Wide, "1366x900");
                ValidateGistEditorDialog(window, options.OutputDirectory, palette, Compact, "760x650");
            });
    }

    private static void RunMarkdownHostPage(
        CaptureOptions options,
        string dataRoot,
        SystemColorPalette palette)
    {
        const string hostId = "MarkdownHost_RepositoryReadme_RepoCodeReadme";
        RunPage(
            options.AppPath,
            dataRoot,
            options.OutputDirectory,
            "repo-code",
            (window, _) =>
            {
                ValidateMarkdownHostViewport(
                    window,
                    Wide,
                    hostId,
                    palette,
                    Path.Combine(options.OutputDirectory, "high-contrast-live-markdown-1366x900.png"),
                    "Repository README Markdown at wide width");
                ValidateMarkdownHostViewport(
                    window,
                    Compact,
                    hostId,
                    palette,
                    Path.Combine(options.OutputDirectory, "high-contrast-live-markdown-760x650.png"),
                    "Repository README Markdown at compact width");
            },
            startInfo => startInfo.ArgumentList.Add($"--repo={options.RepositoryFullName}"));
    }

    private static void RunDashboardCustomShellDialog(
        CaptureOptions options,
        string dataRoot,
        SystemColorPalette palette)
    {
        RunPage(
            options.AppPath,
            dataRoot,
            options.OutputDirectory,
            "home",
            (window, _) =>
            {
                ValidateDashboardCustomShellDialog(window, options.OutputDirectory, palette, Wide, "1366x900");
                ValidateDashboardCustomShellDialog(window, options.OutputDirectory, palette, Compact, "760x650");
            });
    }

    // This method stays whole because one failure variable accumulates the exercise exception, any screenshot or UIA-
    // tree capture error, and any owned-app close error into a single AggregateException that is rethrown after app and
    // automation disposal.
    private static void RunPage(
        string appPath,
        string dataRoot,
        string outputDirectory,
        string page,
        Action<Window, UIA3Automation> exercise,
        Action<ProcessStartInfo>? configureStartInfo = null)
    {
        EnsureNoExistingApplicationProcess(appPath);
        Assert(ReadHighContrast().IsEnabled, $"High Contrast was not active before launching {page}.");

        ProcessStartInfo startInfo = new(appPath)
        {
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add($"--page={page}");
        startInfo.Environment["JITHUB_AUTOMATION_DATA_ROOT"] = dataRoot;
        startInfo.Environment["JITHUB_PREVIEW_PAGE"] = page;
        configureStartInfo?.Invoke(startInfo);

        Application? app = null;
        UIA3Automation? automation = null;
        Window? window = null;
        Exception? failure = null;
        try
        {
            app = Application.Launch(startInfo);
            automation = new UIA3Automation();
            window = WaitForWindow(app, automation, page);
            exercise(window, automation);
        }
        catch (Exception exception)
        {
            failure = exception;
            if (window is not null && automation is not null)
            {
                try
                {
                    using CapturedFrame frame = CaptureWindow(window);
                    frame.Save(Path.Combine(outputDirectory, $"high-contrast-live-{page}-failure.png"));
                    string tree = string.Join(
                        Environment.NewLine,
                        window.FindAllDescendants().Take(400).Select(element =>
                            $"{element.ControlType}\t{element.Properties.AutomationId.ValueOrDefault}\t" +
                            $"{SafeAutomationName(element)}\t{element.BoundingRectangle}"));
                    File.WriteAllText(
                        Path.Combine(outputDirectory, $"high-contrast-live-{page}-failure-uia.txt"),
                        tree);
                }
                catch (Exception evidenceException)
                {
                    failure = new AggregateException(failure, evidenceException);
                }
            }
        }
        finally
        {
            if (app is not null)
            {
                try
                {
                    CloseOwnedApplication(app);
                }
                catch (Exception closeException)
                {
                    failure = failure is null
                        ? closeException
                        : new AggregateException(failure, closeException);
                }
                finally
                {
                    app.Dispose();
                }
            }

            automation?.Dispose();
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
