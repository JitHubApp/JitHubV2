using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Native;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Probes.Settings.SettingsDialogAssertions;
using static JitHub.WinUI.Automation.Probes.Settings.SettingsExportPickerProbe;
using static JitHub.WinUI.Automation.Probes.Settings.SettingsPageLayout;
using static JitHub.WinUI.Automation.Probes.Settings.SettingsPseudoLongLabelsProbe;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Settings;

/// <summary>
/// Drives the settings-responsive probe across dark, light and high contrast.
/// </summary>
internal static class SettingsResponsiveProbe
{
    internal static void RunSettingsResponsiveProbe(CaptureOptions options)
    {
        RunSettingsDarkAndLightProbe(options);
        RunSettingsPseudoLongLabelsProbe(options);

        if (NativeMethods.IsHighContrastEnabled())
        {
            RunSettingsHighContrastProbe(options);
        }
        else
        {
            const string reason =
                "Genuine Windows High Contrast is not active in this desktop session. " +
                "The harness did not simulate a palette and skipped this conditional pass.";
            File.WriteAllText(Path.Combine(options.OutputDirectory, "settings-high-contrast-skipped.txt"), reason);
            Console.WriteLine($"settings-responsive: {reason}");
        }
    }

    private static void RunSettingsDarkAndLightProbe(CaptureOptions options)
    {
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(options.AppPath, "--page=settings", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "settings-responsive probe");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "settings-responsive-launch.png"));
            WaitForElement(
                "SettingsSectionList",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList")),
                TimeSpan.FromSeconds(10));

            AssertSettingsResponsiveLayoutAtAllWidths(window, automation, options.OutputDirectory, "dark");

            OpenSettingsSection(window, "SettingsSection_data-cache", "SettingsClearQueryCacheButton");
            ResizeWindow(window, 1366, 900);
            (string ActionId, string DialogId, string Name)[] dataConfirmations =
            [
                ("SettingsClearQueryCacheButton", "SettingsConfirmClearQueryCache", "Clear GitHub query cache?"),
                ("SettingsClearStarLibraryButton", "SettingsConfirmClearStarsLibrary", "Clear the Stars library?"),
                ("SettingsClearImageCacheButton", "SettingsConfirmClearImageCache", "Clear avatar and image cache?"),
                ("SettingsClearRepoFileCacheButton", "SettingsConfirmClearRepoFileCache", "Clear repository file cache?"),
                ("SettingsClearAllCacheButton", "SettingsConfirmClearAllCache", "Clear all cache data?")
            ];
            foreach ((string actionId, string dialogId, string name) in dataConfirmations)
            {
                AssertSettingsConfirmation(window, automation, options.OutputDirectory, actionId, dialogId, name);
            }

            OpenSettingsSection(window, "SettingsSection_diagnostics", "SettingsExportDiagnosticsButton");
            AssertSettingsConfirmation(
                window,
                automation,
                options.OutputDirectory,
                "SettingsClearDiagnosticsButton",
                "SettingsConfirmClearDiagnostics",
                "Clear diagnostics?");

            OpenSettingsSection(window, "SettingsSection_general", "SettingsDeveloperModeToggle");
            AssertSettingsSignOutConfirmation(window, automation, options.OutputDirectory);

            OpenSettingsSection(window, "SettingsSection_appearance", "SettingsThemeSystem");
            AssertSettingsThemeCardSemantics(window);
            AssertSettingsResponsiveLayoutAtAllWidths(window, automation, options.OutputDirectory, "light");
            ResizeWindow(window, 1366, 900);
            AssertSettingsExportPicker(window, automation, options.OutputDirectory);
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }

    private static void RunSettingsHighContrastProbe(CaptureOptions options)
    {
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(options.AppPath, "--page=settings");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "settings genuine High Contrast probe");
            AssertSettingsPaletteCardSemantics(
                window,
                [
                    ("jithub", "JitHub (default)"),
                    ("windows-11", "Windows 11"),
                    ("visual-studio-code", "Visual Studio Code"),
                    ("github", "GitHub"),
                    ("solarized", "Solarized")
                ]);
            AssertSettingsResponsiveLayoutAtAllWidths(window, automation, options.OutputDirectory, "high-contrast");
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }
    }
}
