using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.AutomationDataRoot;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Interaction.KeyboardInput;
using static JitHub.WinUI.Automation.Probes.Authentication.AuthDataRootReader;
using static JitHub.WinUI.Automation.Probes.Settings.SettingsPageLayout;
using static JitHub.WinUI.Automation.Probes.Theming.ThemePalettePixels;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Theming;

/// <summary>
/// Drives the theme-palettes probe: palette selection, keyboard navigation, chrome repaint and the home matrix.
/// </summary>
internal static class ThemePaletteProbe
{
    // This method stays whole because it runs one ordered palette protocol across separate app launches: switch to
    // Visual Studio Code live and check the saved setting, restart to confirm persistence, compare the three captures
    // for chrome repaint, then sweep every theme and palette startup combination.
    internal static void RunThemePaletteProbe(CaptureOptions options)
    {
        AssertProbe(
            string.IsNullOrWhiteSpace(options.AttachProcess),
            "theme-palettes requires app ownership so it can verify persistence across a clean restart.");
        (string Id, string Name)[] palettes =
        [
            ("jithub", "JitHub (default)"),
            ("windows-11", "Windows 11"),
            ("visual-studio-code", "Visual Studio Code"),
            ("github", "GitHub"),
            ("solarized", "Solarized")
        ];
        string dataRoot = GetAutomationDataRoot();
        string initialPath = Path.Combine(options.OutputDirectory, "theme-palette-live-jithub-dark.png");
        string livePath = Path.Combine(options.OutputDirectory, "theme-palette-live-visual-studio-code-dark.png");
        string persistedPath = Path.Combine(options.OutputDirectory, "theme-palette-persisted-visual-studio-code-dark.png");

        KillExistingApplicationInstances(options.AppPath);
        using (var app = LaunchApplication(
            options.AppPath,
            "--page=settings",
            "--theme=dark",
            "--palette=jithub"))
        using (var automation = new UIA3Automation())
        {
            try
            {
                Window window = GetReadyWindow(app, automation, "theme-palettes live switch");
                ResizeLogicalWindow(window, 1180, 700);
                AssertSettingsPaletteCardSemantics(window, palettes);
                AssertThemePaletteKeyboardNavigation(window, dataRoot);
                Thread.Sleep(200);
                CaptureWindow(window, initialPath);

                AutomationElement visualStudioCode = WaitForElement(
                    "SettingsPalette_visual-studio-code",
                    () => window.FindFirstDescendant(
                        cf => cf.ByAutomationId("SettingsPalette_visual-studio-code")),
                    TimeSpan.FromSeconds(10));
                RevealForInteraction(visualStudioCode, "Visual Studio Code palette");
                SelectAutomationItem(visualStudioCode, "Visual Studio Code palette");
                WaitUntil(
                    "Visual Studio Code palette native selection",
                    () => window.FindFirstDescendant(
                            cf => cf.ByAutomationId("SettingsPalette_visual-studio-code")) is { } current &&
                        current.Patterns.SelectionItem.IsSupported &&
                        current.Patterns.SelectionItem.Pattern.IsSelected.Value,
                    TimeSpan.FromSeconds(5));
                WaitUntil(
                    "Visual Studio Code palette persistence",
                    () => string.Equals(
                        ReadAuthSetting(dataRoot, "APPLICATION_PALETTE_KEY"),
                        "visual-studio-code",
                        StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5));
                Thread.Sleep(350);
                CaptureWindow(window, livePath);

                ResizeLogicalWindow(window, 640, 650);
                AutomationElement narrowSelection = WaitForElement(
                    "Visual Studio Code palette at narrow width",
                    () => window.FindFirstDescendant(
                        cf => cf.ByAutomationId("SettingsPalette_visual-studio-code")),
                    TimeSpan.FromSeconds(5));
                RevealForInteraction(narrowSelection, "Visual Studio Code palette at narrow width");
                AssertProbe(
                    IsInsideWindowBounds(narrowSelection, window),
                    "The selected palette card escaped the Settings viewport at 640x700.");
                CaptureWindow(
                    window,
                    Path.Combine(options.OutputDirectory, "theme-palette-live-visual-studio-code-dark-640x700.png"));
            }
            finally
            {
                TryClose(app);
                KillExistingApplicationInstances(options.AppPath);
            }
        }

        using (var app = LaunchApplication(options.AppPath, "--page=settings", "--theme=dark"))
        using (var automation = new UIA3Automation())
        {
            try
            {
                Window window = GetReadyWindow(app, automation, "theme-palettes persistence restart");
                ResizeLogicalWindow(window, 1180, 700);
                AutomationElement persisted = WaitForElement(
                    "persisted Visual Studio Code palette",
                    () => window.FindFirstDescendant(
                        cf => cf.ByAutomationId("SettingsPalette_visual-studio-code")),
                    TimeSpan.FromSeconds(10));
                WaitUntil(
                    "persisted Visual Studio Code palette selection",
                    () => persisted.Patterns.SelectionItem.IsSupported &&
                        persisted.Patterns.SelectionItem.Pattern.IsSelected.Value,
                    TimeSpan.FromSeconds(5));
                CaptureWindow(window, persistedPath);
            }
            finally
            {
                TryClose(app);
                KillExistingApplicationInstances(options.AppPath);
            }
        }

        AssertThemePaletteChromeRepaint(initialPath, livePath, persistedPath);

        foreach (string requestedTheme in options.Themes)
        {
            string theme = requestedTheme.Trim().ToLowerInvariant();
            AssertProbe(theme is "light" or "dark", "theme-palettes themes must be light or dark.");
            foreach ((string paletteId, string paletteName) in palettes)
            {
                using var app = LaunchApplication(
                    options.AppPath,
                    "--page=settings",
                    $"--theme={theme}",
                    $"--palette={paletteId}");
                using var automation = new UIA3Automation();
                try
                {
                    Window window = GetReadyWindow(app, automation, $"theme-palettes {theme}/{paletteId}");
                    ResizeLogicalWindow(window, 1180, 700);
                    AutomationElement selected = WaitForElement(
                        $"SettingsPalette_{paletteId}",
                        () => window.FindFirstDescendant(cf => cf.ByAutomationId($"SettingsPalette_{paletteId}")),
                        TimeSpan.FromSeconds(10));
                    RevealForInteraction(selected, $"{paletteName} palette");
                    AssertProbe(
                        selected.Patterns.SelectionItem.IsSupported &&
                        selected.Patterns.SelectionItem.Pattern.IsSelected.Value,
                        $"{paletteName} was not selected for the {theme} startup matrix.");
                    Thread.Sleep(250);
                    CaptureWindow(
                        window,
                        Path.Combine(options.OutputDirectory, $"theme-palette-{paletteId}-{theme}.png"));
                }
                finally
                {
                    TryClose(app);
                    KillExistingApplicationInstances(options.AppPath);
                }
            }
        }

        RunThemePaletteHomeMatrix(options, palettes);

        Console.WriteLine(
            $"theme-palettes probe: verified keyboard and live switching, visual repaint, restart persistence, " +
            $"narrow layout, and {palettes.Length * options.Themes.Count} Settings plus Home palette/theme combinations.");
    }

    private static void AssertThemePaletteKeyboardNavigation(Window window, string dataRoot)
    {
        AutomationElement jithub = WaitForElement(
            "JitHub palette for keyboard navigation",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsPalette_jithub")),
            TimeSpan.FromSeconds(5));
        AutomationElement windows11 = WaitForElement(
            "Windows 11 palette for keyboard navigation",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsPalette_windows-11")),
            TimeSpan.FromSeconds(5));

        RevealForInteraction(jithub, "JitHub palette for keyboard navigation");
        FocusForKeyboardActivation(window, jithub);
        PressKeyForWindow(window, VirtualKeyShort.RIGHT);
        WaitUntil(
            "Right Arrow selects Windows 11 palette",
            () => windows11.Patterns.SelectionItem.Pattern.IsSelected.Value &&
                string.Equals(
                    ReadAuthSetting(dataRoot, "APPLICATION_PALETTE_KEY"),
                    "windows-11",
                    StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));

        PressKeyForWindow(window, VirtualKeyShort.LEFT);
        WaitUntil(
            "Left Arrow restores JitHub palette",
            () => jithub.Patterns.SelectionItem.Pattern.IsSelected.Value &&
                string.Equals(
                    ReadAuthSetting(dataRoot, "APPLICATION_PALETTE_KEY"),
                    "jithub",
                    StringComparison.Ordinal),
            TimeSpan.FromSeconds(5));
    }

    private static void AssertThemePaletteChromeRepaint(string initialPath, string livePath, string persistedPath)
    {
        using var initial = new Bitmap(initialPath);
        using var live = new Bitmap(livePath);
        using var persisted = new Bitmap(persistedPath);
        AssertProbe(
            initial.Size == live.Size && live.Size == persisted.Size,
            "Theme palette repaint captures must use identical window dimensions.");

        (double R, double G, double B) initialColor = AverageThemePaletteChrome(initial);
        (double R, double G, double B) liveColor = AverageThemePaletteChrome(live);
        (double R, double G, double B) persistedColor = AverageThemePaletteChrome(persisted);
        double liveChange = AverageRgbDistance(initialColor, liveColor);
        double restartDifference = AverageRgbDistance(liveColor, persistedColor);
        int changedAccentPixels = CountSignificantThemePaletteChromeChanges(initial, live);
        int minimumChangedAccentPixels = Math.Max(12, (int)Math.Round(initial.Width / 60d));
        int initialJitHubAccentPixels = CountThemePalettePixelsNear(initial, Color.FromArgb(0x77, 0xB5, 0x9A));
        int liveVisualStudioCodeAccentPixels = CountThemePalettePixelsNear(live, Color.FromArgb(0x00, 0x78, 0xD4));
        int persistedVisualStudioCodeAccentPixels = CountThemePalettePixelsNear(persisted, Color.FromArgb(0x00, 0x78, 0xD4));
        int minimumAccentPixels = Math.Max(12, (int)Math.Round(initial.Width / 100d));

        AssertProbe(
            liveChange >= 3.5,
            $"Live palette switching did not repaint existing chrome (RGB delta {liveChange:F2}).");
        AssertProbe(
            changedAccentPixels >= minimumChangedAccentPixels,
            $"Live palette switching did not repaint the existing navigation accent " +
            $"({changedAccentPixels} significant pixels; expected at least {minimumChangedAccentPixels}).");
        AssertProbe(
            initialJitHubAccentPixels >= minimumAccentPixels,
            $"The initial navigation indicator did not render the JitHub dark accent " +
            $"({initialJitHubAccentPixels} pixels; expected at least {minimumAccentPixels}).");
        AssertProbe(
            liveVisualStudioCodeAccentPixels >= minimumAccentPixels,
            $"The live navigation indicator did not render the Visual Studio Code dark accent " +
            $"({liveVisualStudioCodeAccentPixels} pixels; expected at least {minimumAccentPixels}).");
        AssertProbe(
            persistedVisualStudioCodeAccentPixels >= minimumAccentPixels,
            $"The persisted navigation indicator did not render the Visual Studio Code dark accent " +
            $"({persistedVisualStudioCodeAccentPixels} pixels; expected at least {minimumAccentPixels}).");
        AssertProbe(
            restartDifference <= 3,
            $"Live palette chrome differs from a clean-start frame (RGB delta {restartDifference:F2}).");
    }

    private static void RunThemePaletteHomeMatrix(
        CaptureOptions options,
        IReadOnlyList<(string Id, string Name)> palettes)
    {
        foreach (string requestedTheme in options.Themes)
        {
            string theme = requestedTheme.Trim().ToLowerInvariant();
            foreach ((string paletteId, _) in palettes)
            {
                using var app = LaunchApplication(
                    options.AppPath,
                    "--page=home",
                    "--scenario=website-showcase",
                    "--website-showcase",
                    $"--theme={theme}",
                    $"--palette={paletteId}");
                using var automation = new UIA3Automation();
                try
                {
                    Window window = GetReadyWindow(app, automation, $"theme-palettes Home {theme}/{paletteId}");
                    ResizeLogicalWindow(window, 1180, 700);
                    WaitForElement(
                        $"Home widget board for {theme}/{paletteId}",
                        () => window.FindFirstDescendant(cf => cf.ByAutomationId("DashboardWidgetBoard")),
                        TimeSpan.FromSeconds(12));
                    WaitUntil(
                        $"Home palette surface ready for {theme}/{paletteId}",
                        () => IsVisible(window.FindFirstDescendant(
                            cf => cf.ByAutomationId("DashboardRepository_1"))),
                        TimeSpan.FromSeconds(12));
                    Thread.Sleep(200);
                    CaptureWindow(
                        window,
                        Path.Combine(options.OutputDirectory, $"theme-palette-{paletteId}-{theme}-home.png"));
                }
                finally
                {
                    TryClose(app);
                    KillExistingApplicationInstances(options.AppPath);
                }
            }
        }
    }
}
