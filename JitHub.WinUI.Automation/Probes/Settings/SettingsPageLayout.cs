using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Native;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Elements.FocusQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Interaction.KeyboardInput;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Settings;

/// <summary>
/// Opens Settings sections and checks the responsive layout and the theme and palette card semantics.
/// </summary>
internal static class SettingsPageLayout
{
    // This method stays whole because, at each of five widths, it chooses between the compact section picker and the
    // section rail, then uses that same navigation surface to visit all six sections, check the contributor link and
    // reset to Appearance before the capture.
    internal static void AssertSettingsResponsiveLayoutAtAllWidths(
        Window window,
        UIA3Automation automation,
        string outputDirectory,
        string theme)
    {
        (string Title, string SectionId, string ProofId)[] sections =
        [
            ("Appearance", "SettingsSection_appearance", "SettingsThemeSystem"),
            ("General", "SettingsSection_general", "SettingsDeveloperModeToggle"),
            ("Privacy", "SettingsSection_privacy", "SettingsDiagnosticsToggle"),
            ("Data & Cache", "SettingsSection_data-cache", "SettingsClearQueryCacheButton"),
            ("Diagnostics", "SettingsSection_diagnostics", "SettingsExportDiagnosticsButton"),
            ("About", "SettingsSection_about", "SettingsViewSourceButton")
        ];
        (int Width, int Height)[] sizes =
        [
            (1366, 900),
            (1180, 800),
            (900, 700),
            (760, 650),
            (640, 600)
        ];

        foreach ((int width, int height) in sizes)
        {
            Rectangle resizedBounds = ResizeWindow(window, width, height);
            int actualWidth = resizedBounds.Width;
            string viewportLabel = GetResponsiveViewportLabel(width, height, resizedBounds);
            bool usesCompactNavigation = actualWidth < 820;
            AutomationElement? rail = window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList"));
            AutomationElement? picker = window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsCompactSectionPicker"));
            if (usesCompactNavigation)
            {
                picker = WaitForElement(
                    "SettingsCompactSectionPicker",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsCompactSectionPicker")),
                    TimeSpan.FromSeconds(5));
                picker.AsComboBox().Expand();
                foreach ((string title, string sectionId, _) in sections)
                {
                    AutomationElement compactItem = WaitForElement(
                        $"compact Settings section {title}",
                        () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId($"{sectionId}_Compact")),
                        TimeSpan.FromSeconds(5));
                    AssertProbe(
                        string.Equals(compactItem.Name, title, StringComparison.Ordinal),
                        $"Compact Settings section {title} exposed the wrong accessible name.");
                    AssertProbe(IsInsideWindowBounds(compactItem, window), $"Compact Settings section {title} was clipped at {viewportLabel}.");
                }
                picker.AsComboBox().Collapse();
            }
            else
            {
                rail = WaitForElement(
                    "SettingsSectionList",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSectionList")),
                    TimeSpan.FromSeconds(5));
            }

            AssertProbe(IsVisible(picker) == usesCompactNavigation, $"Settings compact picker visibility was wrong at {viewportLabel}.");
            AssertProbe(IsVisible(rail) != usesCompactNavigation, $"Settings section rail visibility was wrong at {viewportLabel}.");

            foreach ((string title, string sectionId, string proofId) in sections)
            {
                if (usesCompactNavigation)
                {
                    picker!.AsComboBox().Select(title);
                }
                else
                {
                    AutomationElement section = WaitForElement(
                        $"Settings section {title}",
                        () => rail!.FindFirstDescendant(cf => cf.ByAutomationId(sectionId)),
                        TimeSpan.FromSeconds(5));
                    AssertProbe(
                        string.Equals(section.Name, title, StringComparison.Ordinal),
                        $"Settings section {title} exposed the wrong accessible name: '{section.Name}'.");
                    SelectAutomationItem(section, $"Settings section {title}");
                }

                AutomationElement proof = WaitForElement(
                    proofId,
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId(proofId)),
                    TimeSpan.FromSeconds(5));
                RevealForInteraction(proof, proofId);
                AssertProbe(IsInsideWindowBounds(proof, window), $"Settings {title} controls escaped the window at {viewportLabel}.");
            }

            AutomationElement contributorLink = WaitForElement(
                "contributor social link",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("ContributorLink_GitHub_httpsgithubcomGet0457")),
                TimeSpan.FromSeconds(5));
            AssertProbe(
                string.Equals(contributorLink.Name, "Open GitHub profile", StringComparison.Ordinal),
                "Contributor social link did not expose a meaningful accessible name.");
            RevealForInteraction(contributorLink, "contributor social link");
            AssertProbe(IsInsideWindowBounds(contributorLink, window), $"Contributor social action was clipped at {viewportLabel}.");

            if (usesCompactNavigation)
            {
                picker!.AsComboBox().Select("Appearance");
            }
            else
            {
                AutomationElement appearance = WaitForElement(
                    "Appearance section at capture reset",
                    () => rail!.FindFirstDescendant(cf => cf.ByAutomationId("SettingsSection_appearance")),
                    TimeSpan.FromSeconds(5));
                SelectAutomationItem(appearance, "Settings Appearance section");
            }
            AutomationElement topControl = WaitForElement(
                "Settings theme card at capture reset",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsThemeSystem")),
                TimeSpan.FromSeconds(5));
            RevealForInteraction(topControl, "Settings theme card at capture reset");
            CaptureWindow(window, Path.Combine(outputDirectory, $"settings-responsive-{theme}-{viewportLabel}.png"));
        }
    }

    internal static void OpenSettingsSection(Window window, string sectionAutomationId, string contentAutomationId)
    {
        string title = sectionAutomationId switch
        {
            "SettingsSection_appearance" => "Appearance",
            "SettingsSection_general" => "General",
            "SettingsSection_privacy" => "Privacy",
            "SettingsSection_data-cache" => "Data & Cache",
            "SettingsSection_diagnostics" => "Diagnostics",
            "SettingsSection_about" => "About",
            _ => throw new ArgumentOutOfRangeException(nameof(sectionAutomationId), sectionAutomationId, "Unknown Settings section.")
        };
        AutomationElement selector = WaitForElement(
            $"visible {title} Settings selector",
            () =>
            {
                AutomationElement? picker = window.FindFirstDescendant(cf => cf.ByAutomationId("SettingsCompactSectionPicker"));
                if (IsVisible(picker))
                {
                    return picker;
                }

                AutomationElement? section = window.FindFirstDescendant(cf => cf.ByAutomationId(sectionAutomationId));
                return IsVisible(section) ? section : null;
            },
            TimeSpan.FromSeconds(5));
        if (selector.ControlType == ControlType.ComboBox)
        {
            selector.AsComboBox().Select(title);
        }
        else
        {
            RevealForInteraction(selector, sectionAutomationId);
            SelectAutomationItem(selector, $"Settings {title} section");
        }
        AutomationElement content = WaitForElement(
            contentAutomationId,
            () => window.FindFirstDescendant(cf => cf.ByAutomationId(contentAutomationId)),
            TimeSpan.FromSeconds(5));
        RevealForInteraction(content, contentAutomationId);
    }

    internal static void AssertSettingsThemeCardSemantics(Window window)
    {
        AutomationElement system = AssertNamedAutomationElement(window, "SettingsThemeSystem", ControlType.RadioButton);
        AutomationElement light = AssertNamedAutomationElement(window, "SettingsThemeLight", ControlType.RadioButton);
        AutomationElement dark = AssertNamedAutomationElement(window, "SettingsThemeDark", ControlType.RadioButton);
        AssertProbe(system.Patterns.SelectionItem.IsSupported, "System theme card did not expose native selection semantics.");
        AssertProbe(light.Patterns.SelectionItem.IsSupported, "Light theme card did not expose native selection semantics.");
        AssertProbe(dark.Patterns.SelectionItem.IsSupported, "Dark theme card did not expose native selection semantics.");

        if (NativeMethods.HasInvisibleForeignForegroundWindow(window.Properties.NativeWindowHandle.ValueOrDefault))
        {
            light.Patterns.SelectionItem.Pattern.Select();
            WaitUntil("Light theme card native selection", () => light.Patterns.SelectionItem.Pattern.IsSelected.Value, TimeSpan.FromSeconds(5));
            return;
        }

        FocusForKeyboardActivation(window, system);
        WaitUntil("System theme card receives keyboard focus", () => IsElementFocused(system), TimeSpan.FromSeconds(5));
        Keyboard.Press(VirtualKeyShort.RIGHT);
        WaitUntil("theme card Right selects Light", () => light.Patterns.SelectionItem.Pattern.IsSelected.Value, TimeSpan.FromSeconds(5));
        Keyboard.Press(VirtualKeyShort.RIGHT);
        WaitUntil("theme card Right selects Dark", () => dark.Patterns.SelectionItem.Pattern.IsSelected.Value, TimeSpan.FromSeconds(5));
        Keyboard.Press(VirtualKeyShort.LEFT);
        WaitUntil("theme card Left selects Light", () => light.Patterns.SelectionItem.Pattern.IsSelected.Value, TimeSpan.FromSeconds(5));
        Keyboard.Press(VirtualKeyShort.SPACE);
        AssertProbe(light.Patterns.SelectionItem.Pattern.IsSelected.Value, "Space did not preserve native Light theme radio selection.");
    }

    internal static void AssertSettingsPaletteCardSemantics(
        Window window,
        IReadOnlyList<(string Id, string Name)> palettes)
    {
        foreach ((string id, string name) in palettes)
        {
            string automationId = $"SettingsPalette_{id}";
            AutomationElement card = WaitForElement(
                automationId,
                () => window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)),
                TimeSpan.FromSeconds(8));
            RevealForInteraction(card, $"{name} palette card");
            card = WaitForElement(
                $"visible {automationId}",
                () =>
                {
                    AutomationElement? current = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
                    return IsVisible(current) ? current : null;
                },
                TimeSpan.FromSeconds(5));
            AssertProbe(card.ControlType == ControlType.RadioButton, $"{automationId} exposed {card.ControlType} instead of RadioButton.");
            AssertProbe(
                string.Equals(card.Name, name, StringComparison.Ordinal),
                $"{id} palette card exposed the wrong accessible name: '{card.Name}'.");
            AssertProbe(
                card.Patterns.SelectionItem.IsSupported,
                $"{name} palette card did not expose native selection semantics.");
            AssertProbe(
                !string.IsNullOrWhiteSpace(card.Properties.HelpText.ValueOrDefault),
                $"{name} palette card did not expose its description to assistive technology.");
        }
    }
}
