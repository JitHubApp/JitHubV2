using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Elements.FocusQueries;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Shell.ShellSearch;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowActivation;

namespace JitHub.WinUI.Automation.Probes.Search;

/// <summary>
/// Drives the search-focus-contract probe, including the Ctrl+K tooltip check.
/// </summary>
internal static class SearchFocusContractProbe
{
    // This method stays whole because it checks the shell search focus contract as one ordered keyboard script: startup
    // with no focus and a visible badge, titlebar hover with no tooltip, Ctrl+K focus, typing, Escape, then typing
    // again, each assertion depending on the focus state the previous key left.
    internal static void RunSearchFocusContractProbe(CaptureOptions options)
    {
        string[] requirements =
        [
            "Startup leaves no visible or real keyboard focus inside the search text box.",
            "The Ctrl and K shortcut badge is visible while search is unfocused and does not cover the placeholder.",
            "Hovering empty titlebar space does not show a Ctrl+K tooltip.",
            "Ctrl+K focuses the real search text box, selects it as the active input, and hides the shortcut badge.",
            "Esc from search dismisses suggestions, removes the light ring state, and moves real keyboard focus away from search.",
            "Esc must not move visible focus to the titlebar menu button.",
            "Typing after Esc must not modify search text, proving the caret is gone from the text field."
        ];

        Console.WriteLine("search-focus-contract requirements:");
        foreach (string requirement in requirements)
        {
            Console.WriteLine($" - {requirement}");
        }

        using var app = string.IsNullOrWhiteSpace(options.AttachProcess)
            ? LaunchApplication(options.AppPath, "--page=shell", "--scenario=search-suggestions", "--theme=dark")
            : CreateProbeApplication(options);
        using var automation = new UIA3Automation();

        try
        {
            Window window = GetReadyWindow(app, automation, "search-focus-contract probe");
            AutomationElement searchBox = WaitForElement(
                "ShellSearchTextBox",
                () => FindShellSearchTextBox(window),
                TimeSpan.FromSeconds(10));
            AutomationElement? searchBadge = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchShortcutBadge"));
            AutomationElement? shellMenuButton = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellMenuButton"));

            WaitUntil(
                "startup search focus is cleared",
                () => !IsSearchActuallyFocused(automation, searchBox),
                TimeSpan.FromSeconds(6));
            AssertProbe(!IsSearchActuallyFocused(automation, searchBox), "Startup left keyboard focus inside the search box.");
            AssertProbe(!IsElementFocused(shellMenuButton), "Startup moved visible keyboard focus to the titlebar menu button.");
            AssertProbe(IsVisible(searchBadge), "Shortcut badge was not visible while search was unfocused.");

            MoveMouseToEmptyTitleBar(window, searchBox);
            Thread.Sleep(1200);
            AssertProbe(!HasCtrlKTooltip(automation), "Hovering empty titlebar space displayed a Ctrl+K tooltip.");

            TryActivateWindow(window);
            Thread.Sleep(150);
            PressCtrlK();
            WaitUntil(
                "Ctrl+K focuses search",
                () => IsSearchActuallyFocused(automation, searchBox),
                TimeSpan.FromSeconds(3));
            AssertProbe(IsSearchActuallyFocused(automation, searchBox), "Ctrl+K did not put real keyboard focus in the search box.");
            AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchShortcutBadge"))), "Shortcut badge stayed visible while search was focused.");

            TextBox textBox = searchBox.AsTextBox();
            textBox.Text = string.Empty;
            Keyboard.Type("focusprobe");
            WaitUntil(
                "search text accepted typed input",
                () => string.Equals(GetTextBoxText(searchBox), "focusprobe", StringComparison.Ordinal),
                TimeSpan.FromSeconds(3));

            Keyboard.Press(VirtualKeyShort.ESCAPE);
            WaitUntil(
                "Esc removes search focus",
                () => !IsSearchActuallyFocused(automation, searchBox),
                TimeSpan.FromSeconds(3));
            AssertProbe(!IsSearchActuallyFocused(automation, searchBox), "Esc left real keyboard focus in the search text box.");
            AssertProbe(!IsElementFocused(shellMenuButton), "Esc moved visible keyboard focus to the titlebar menu button.");
            AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchShortcutBadge"))), "Shortcut badge did not return after Esc dismissed search focus.");

            string textAfterEscape = GetTextBoxText(searchBox);
            Keyboard.Type("x");
            Thread.Sleep(400);
            AssertProbe(
                string.Equals(GetTextBoxText(searchBox), textAfterEscape, StringComparison.Ordinal),
                "Typing after Esc changed the search text, so the caret was still in the search box.");

            MoveMouseToEmptyTitleBar(window, searchBox);
            Thread.Sleep(1200);
            AssertProbe(!HasCtrlKTooltip(automation), "Ctrl+K tooltip appeared over empty titlebar space after Esc.");

            string filePath = Path.Combine(options.OutputDirectory, "probe-search-focus-contract.png");
            CaptureWindowWithPopups(window, filePath);

            string focusedId = GetFocusedAutomationId(automation);
            Console.WriteLine($"search-focus-contract probe: passed, focusedAutomationId='{focusedId}', screenshot={filePath}");
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(options.AttachProcess))
            {
                TryClose(app);
                KillExistingApplicationInstances(options.AppPath);
            }
        }
    }

    private static bool HasCtrlKTooltip(UIA3Automation automation)
    {
        AutomationElement[] tooltips = automation.GetDesktop().FindAllDescendants(cf => cf.ByControlType(ControlType.ToolTip));
        foreach (AutomationElement tooltip in tooltips)
        {
            var text = new StringBuilder(GetElementName(tooltip));
            foreach (AutomationElement childText in tooltip.FindAllDescendants(cf => cf.ByControlType(ControlType.Text)))
            {
                text.Append(' ');
                text.Append(GetElementName(childText));
            }

            string tooltipText = text.ToString();
            if (tooltipText.Contains("Ctrl", StringComparison.OrdinalIgnoreCase)
                && tooltipText.Contains("K", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
