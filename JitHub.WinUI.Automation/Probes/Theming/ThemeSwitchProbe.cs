using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Probes.Theming.ThemePalettePixels;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;

namespace JitHub.WinUI.Automation.Probes.Theming;

/// <summary>
/// Drives the theme-switch probe and checks that a live theme change repaints the app.
/// </summary>
internal static class ThemeSwitchProbe
{
    internal static void RunThemeSwitchProbe(CaptureOptions options)
    {
        using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
            ? LaunchApplication(
                options.AppPath,
                "--page=settings",
                "--theme=light",
                "--palette=visual-studio-code")
            : CreateProbeApplication(options);
        using var automation = new UIA3Automation();

        Window window = GetReadyWindow(app, automation, "theme-switch probe");

        AutomationElement lightCard = WaitForElement(
            "SettingsThemeLight",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsThemeLight")),
            TimeSpan.FromSeconds(10));
        AutomationElement darkCard = WaitForElement(
            "SettingsThemeDark",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsThemeDark")),
            TimeSpan.FromSeconds(10));

        string beforePath = Path.Combine(options.OutputDirectory, "probe-theme-before.png");
        CaptureWindow(window, beforePath);

        SelectAutomationItem(darkCard, "dark theme card");
        WaitUntil(
            "dark theme card selected",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsThemeDark")) is { } currentDark &&
                currentDark.Patterns.SelectionItem.IsSupported &&
                currentDark.Patterns.SelectionItem.Pattern.IsSelected.Value,
            TimeSpan.FromSeconds(5));
        Thread.Sleep(1200);

        string afterPath = Path.Combine(options.OutputDirectory, "probe-theme-after.png");
        CaptureWindow(window, afterPath);

        SelectAutomationItem(lightCard, "light theme card");
        WaitUntil(
            "light theme card selected again",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsThemeLight")) is { } currentLight &&
                currentLight.Patterns.SelectionItem.IsSupported &&
                currentLight.Patterns.SelectionItem.Pattern.IsSelected.Value,
            TimeSpan.FromSeconds(5));
        Thread.Sleep(300);
        string restoredPath = Path.Combine(options.OutputDirectory, "probe-theme-restored-light.png");
        CaptureWindow(window, restoredPath);

        AssertLiveThemeAppearanceRepaint(beforePath, afterPath, restoredPath);

        Console.WriteLine($"theme-switch probe: before={beforePath}, dark={afterPath}, restored={restoredPath}");
        if (string.IsNullOrWhiteSpace(options.AttachProcess))
        {
            TryClose(app);
        }
    }

    private static void AssertLiveThemeAppearanceRepaint(string lightPath, string darkPath, string restoredLightPath)
    {
        using var light = new Bitmap(lightPath);
        using var dark = new Bitmap(darkPath);
        using var restoredLight = new Bitmap(restoredLightPath);
        AssertProbe(
            light.Size == dark.Size && dark.Size == restoredLight.Size,
            "Live appearance repaint captures must use identical window dimensions.");

        double lightDarkDifference = AverageRgbDistance(
            AverageThemePaletteChrome(light),
            AverageThemePaletteChrome(dark));
        double restoredDifference = AverageRgbDistance(
            AverageThemePaletteChrome(light),
            AverageThemePaletteChrome(restoredLight));
        int minimumAccentPixels = Math.Max(12, (int)Math.Round(light.Width / 100d));
        int lightAccentPixels = CountThemePalettePixelsNearAnySurface(light, Color.FromArgb(0x00, 0x5F, 0xB8));
        int darkAccentPixels = CountThemePalettePixelsNearAnySurface(dark, Color.FromArgb(0x00, 0x78, 0xD4));
        int restoredAccentPixels = CountThemePalettePixelsNearAnySurface(restoredLight, Color.FromArgb(0x00, 0x5F, 0xB8));

        AssertProbe(
            lightDarkDifference >= 60,
            $"Live Light-to-Dark switching did not repaint existing chrome (RGB delta {lightDarkDifference:F2}).");
        AssertProbe(
            restoredDifference <= 3,
            $"The restored Light chrome differs from its initial frame (RGB delta {restoredDifference:F2}).");
        AssertProbe(
            lightAccentPixels >= minimumAccentPixels && restoredAccentPixels >= minimumAccentPixels,
            $"The Visual Studio Code Light accent did not survive the live appearance round trip " +
            $"({lightAccentPixels}/{restoredAccentPixels} pixels; expected at least {minimumAccentPixels}).");
        AssertProbe(
            darkAccentPixels >= minimumAccentPixels,
            $"The Visual Studio Code Dark accent did not repaint during the live appearance switch " +
            $"({darkAccentPixels} pixels; expected at least {minimumAccentPixels}).");
    }
}
