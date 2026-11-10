using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Native;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Probes.Stars.StarsCategories;
using static JitHub.WinUI.Automation.Probes.Stars.StarsCategoryPersistenceProbe;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Stars;

/// <summary>
/// Drives the stars-library probe.
/// </summary>
internal static class StarsLibraryProbe
{
    // This method stays whole because it is one ordered live-UI script against a single Stars window that walks five
    // sizes toggling the category drawer, then search, sort, each filter chip, selection-mode toggle and cancel, row
    // hover, context menu, and unstar/undo, reusing the row and list handles each step leaves behind.
    internal static void RunStarsLibraryProbe(CaptureOptions options, bool includeCategoryPersistence = true)
    {
        KillExistingApplicationInstances(options.AppPath);
        using var app = LaunchApplication(options.AppPath, "--page=stars", "--theme=dark");
        using var automation = new UIA3Automation();
        try
        {
            Window window = GetReadyWindow(app, automation, "stars-library probe");
            ResizeWindow(window, 1366, 900);
            AutomationElement starsRoot = AssertNamedAutomationElement(window, "StarsSearch", ControlType.Edit);
            _ = AssertNamedAutomationElement(window, "StarsSort", ControlType.ComboBox);
            _ = AssertNamedAutomationElement(window, "StarsFilter", ControlType.Button);
            _ = AssertNamedAutomationElement(window, "StarsCategoryNavigation", ControlType.List);
            AutomationElement starsList = AssertNamedAutomationElement(window, "StarsList", ControlType.List);
            AutomationElement firstStarRow = WaitForElement(
                "first Stars row",
                () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                TimeSpan.FromSeconds(12));
            AssertProbe(
                !string.IsNullOrWhiteSpace(firstStarRow.Name) &&
                !firstStarRow.Name.Contains("StarRepositoryViewItem", StringComparison.Ordinal),
                "Stars rows must expose repository details instead of a CLR model name.");
            Thread.Sleep(900);

            AssertProbe(IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryNavigation"))), "Wide Stars did not show the category pane.");
            AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsOpenCategories"))), "Wide Stars exposed the compact category drawer button.");

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
                AutomationElement? categoryButton = window.FindFirstDescendant(cf => cf.ByAutomationId("StarsOpenCategories"));
                bool compact = IsVisible(categoryButton);
                if (actualWidth <= 760)
                {
                    AssertProbe(compact, $"Stars did not expose its category drawer at {viewportLabel}.");
                    AssertProbe(!IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCloseCategories"))), $"Stars category drawer started open at {viewportLabel}.");
                }

                CaptureWindow(window, Path.Combine(options.OutputDirectory, $"stars-responsive-{viewportLabel}.png"));
                if (compact)
                {
                    int toggleCount = actualWidth <= 640 ? 3 : 1;
                    for (int toggleIndex = 0; toggleIndex < toggleCount; toggleIndex++)
                    {
                        AutomationElement opener = WaitForElement(
                            "StarsOpenCategories",
                            () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsOpenCategories")),
                            TimeSpan.FromSeconds(5));
                        AssertProbe(IsVisible(opener), $"Stars category drawer opener disappeared at {viewportLabel}.");
                        InvokeOrClick(opener);
                        AutomationElement close = WaitForElement(
                            "StarsCloseCategories",
                            () =>
                            {
                                AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCloseCategories"));
                                return IsVisible(candidate) ? candidate : null;
                            },
                            TimeSpan.FromSeconds(5));
                        Thread.Sleep(250);
                        if (toggleIndex == 0)
                        {
                            CaptureWindow(window, Path.Combine(options.OutputDirectory, $"stars-responsive-{viewportLabel}-drawer.png"));
                        }

                        InvokeOrClick(close);
                        WaitUntil(
                            "Stars category drawer close",
                            () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCloseCategories"))),
                            TimeSpan.FromSeconds(5));
                    }
                }
            }

            ResizeWindow(window, 1366, 900);
            Thread.Sleep(700);
            AutomationElement search = WaitForElement("StarsSearch", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSearch")), TimeSpan.FromSeconds(5));
            InvokeOrClick(search);
            Keyboard.Type("WinUI");
            Thread.Sleep(500);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-search-filtered.png"));
            search.AsTextBox().Text = string.Empty;
            WaitUntil(
                "Stars search cleared",
                () => string.IsNullOrEmpty(GetTextBoxText(search)),
                TimeSpan.FromSeconds(3));
            Thread.Sleep(400);

            AutomationElement sort = WaitForElement("StarsSort", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSort")), TimeSpan.FromSeconds(5));
            ComboBox sortCombo = sort.AsComboBox();
            sortCombo.Expand();
            WaitUntil("Stars sort options realized", () => sortCombo.Items.Length > 2, TimeSpan.FromSeconds(3));
            sortCombo.Select(2);
            Thread.Sleep(650);
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-sort-most-stars.png"));

            AutomationElement filter = WaitForElement("StarsFilter", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsFilter")), TimeSpan.FromSeconds(5));
            InvokeOrClick(filter);
            AutomationElement languageFilter = WaitForElement(
                "StarsFilterLanguage",
                () => FindElementInWindowOrDialog(window, automation, "StarsFilterLanguage"),
                TimeSpan.FromSeconds(5));
            foreach (string filterId in new[]
            {
                "StarsFilterOwner",
                "StarsFilterTopic",
                "StarsFilterVisibility",
                "StarsFilterKind",
                "StarsFilterActivity",
                "StarsFilterCategoryState"
            })
            {
                AssertProbe(IsVisible(FindElementInWindowOrDialog(window, automation, filterId)), $"Filter flyout omitted {filterId}.");
            }
            CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "stars-filter-flyout.png"));
            (string FilterId, string ChipId, string OptionName)[] filterCases =
            [
                ("StarsFilterLanguage", "language", "C#"),
                ("StarsFilterOwner", "owner", "JitHubApp"),
                ("StarsFilterTopic", "topic", "developer-tools"),
                ("StarsFilterVisibility", "visibility", "Public"),
                ("StarsFilterKind", "kind", "Sources"),
                ("StarsFilterActivity", "activity", "Active"),
                ("StarsFilterCategoryState", "category", "Categorized")
            ];
            for (int filterIndex = 0; filterIndex < filterCases.Length; filterIndex++)
            {
                (string filterId, string chipId, string optionName) = filterCases[filterIndex];
                AutomationElement filterControl;
                if (filterIndex == 0)
                {
                    filterControl = languageFilter;
                }
                else
                {
                    filterControl = WaitForElement(
                        filterId,
                        () => FindElementInWindowOrDialog(window, automation, filterId),
                        TimeSpan.FromSeconds(5));
                }

                filterControl.AsComboBox().Select(optionName);
                WaitForElement(
                    $"{chipId} filter chip",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId($"StarsFilterChip_{chipId}")),
                    TimeSpan.FromSeconds(5));
                if (filterIndex == 0)
                {
                    CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "stars-filter-chip.png"));
                }

                AutomationElement? clearFilters = Retry.WhileNull(
                    () =>
                    {
                        AutomationElement? candidate = FindElementInWindowOrDialog(window, automation, "StarsClearFilters");
                        return IsVisible(candidate) ? candidate : null;
                    },
                    timeout: TimeSpan.FromSeconds(1),
                    interval: TimeSpan.FromMilliseconds(100),
                    ignoreException: true).Result;
                if (!IsVisible(clearFilters))
                {
                    Keyboard.Press(VirtualKeyShort.ESCAPE);
                    filter = WaitForElement(
                        "StarsFilter for clear",
                        () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsFilter")),
                        TimeSpan.FromSeconds(5));
                    InvokeOrClick(filter);
                    clearFilters = WaitForElement(
                        "StarsClearFilters after reopening filter flyout",
                        () =>
                        {
                            AutomationElement? candidate = FindElementInWindowOrDialog(window, automation, "StarsClearFilters");
                            return IsVisible(candidate) ? candidate : null;
                        },
                        TimeSpan.FromSeconds(5));
                }

                InvokeOrClick(clearFilters!);
                Thread.Sleep(250);
            }
            Keyboard.Press(VirtualKeyShort.ESCAPE);

            AutomationElement selection = WaitForElement("StarsSelectionMode", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSelectionMode")), TimeSpan.FromSeconds(5));
            InvokeOrClick(selection);
            AutomationElement firstRow = WaitForElement("first Stars row", () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(5));
            AssertStarsRepositoryAutomationIdentity(firstRow);
            AutomationElement firstRowCheckBox = WaitForElement(
                "first Stars row checkbox",
                () => firstRow.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox)),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(firstRowCheckBox);
            _ = WaitForElement(
                "Stars selection contextual commands",
                () =>
                {
                    AutomationElement? candidate = window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory"));
                    return IsVisible(candidate) ? candidate : null;
                },
                TimeSpan.FromSeconds(5));
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-selection-mode.png"));

            // Toggling selection mode off with an active selection used to clear an already-invalidated
            // WinRT vector and crash with E_UNEXPECTED. Exercise the toolbar toggle itself first.
            InvokeOrClick(selection);
            WaitUntil(
                "Stars selection toolbar toggle dismissal",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory"))),
                TimeSpan.FromSeconds(5));
            AssertProbe(
                IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSearch"))) &&
                IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsList"))),
                "Stars interactive workspace disappeared after toggling selection mode off.");

            // Exercise the explicit cancel path independently as it shares the selection-mode transition.
            InvokeOrClick(selection);
            firstRow = WaitForElement("first Stars row for cancel selection", () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(5));
            firstRowCheckBox = WaitForElement(
                "first Stars row checkbox for cancel selection",
                () => firstRow.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox)),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(firstRowCheckBox);
            InvokeOrClick(WaitForElement("StarsCancelSelection", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCancelSelection")), TimeSpan.FromSeconds(5)));
            WaitUntil(
                "Stars cancel selection dismissal",
                () => !IsVisible(window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory"))),
                TimeSpan.FromSeconds(5));

            firstRow = WaitForElement("first Stars row after selection", () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(5));
            var firstRowBounds = firstRow.BoundingRectangle;
            Mouse.MoveTo(new System.Drawing.Point(
                firstRowBounds.Left + firstRowBounds.Width / 2,
                firstRowBounds.Top + firstRowBounds.Height / 2));
            Thread.Sleep(350);
            AutomationElement hoverUnstar = WaitForElement(
                "Stars hover Unstar action",
                () => firstRow.FindFirstDescendant(cf => cf.ByName("Unstar repository")),
                TimeSpan.FromSeconds(5));
            AssertProbe(IsVisible(hoverUnstar), "Hovering a Stars row did not expose the Unstar action.");
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-row-hover.png"));

            var contextPoint = new System.Drawing.Point(
                firstRowBounds.Left + Math.Min(120, firstRowBounds.Width / 3),
                firstRowBounds.Top + 4);
            if (NativeMethods.HasInvisibleForeignForegroundWindow(window.Properties.NativeWindowHandle.ValueOrDefault))
            {
                AutomationElement hoverMenu = WaitForElement(
                    "Stars hover repository actions",
                    () => firstRow.FindFirstDescendant(cf => cf.ByName("Repository actions")),
                    TimeSpan.FromSeconds(5));
                InvokeOrClick(hoverMenu);
            }
            else
            {
                Mouse.MoveTo(contextPoint);
                Mouse.RightClick();
            }
            WaitForElement("Stars row context menu", () => automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("StarsContextOpenrepository")), TimeSpan.FromSeconds(5));
            AssertProbe(IsVisible(automation.GetDesktop().FindFirstDescendant(cf => cf.ByAutomationId("StarsContextAddtocategory"))), "Stars row context menu omitted category assignment.");
            Thread.Sleep(300);
            CaptureWindowWithPopups(window, Path.Combine(options.OutputDirectory, "stars-row-top-context-menu.png"));
            Keyboard.Press(VirtualKeyShort.ESCAPE);

            Mouse.MoveTo(new System.Drawing.Point(
                firstRowBounds.Left + firstRowBounds.Width / 2,
                firstRowBounds.Top + firstRowBounds.Height / 2));
            Thread.Sleep(250);
            hoverUnstar = WaitForElement(
                "Stars hover Unstar action after context menu",
                () => firstRow.FindFirstDescendant(cf => cf.ByName("Unstar repository")),
                TimeSpan.FromSeconds(5));
            InvokeOrClick(hoverUnstar);
            AutomationElement undo = WaitForElement(
                "StarsUndoUnstar",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsUndoUnstar")),
                TimeSpan.FromSeconds(5));
            CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-unstar-undo.png"));
            InvokeOrClick(undo);
            WaitForElement(
                "restored Stars row",
                () => starsList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                TimeSpan.FromSeconds(5));

            AssertProbe(starsRoot.BoundingRectangle.Width > 0 && starsRoot.BoundingRectangle.Height > 0, "Stars workspace lost its stable root bounds.");
        }
        finally
        {
            TryClose(app);
            KillExistingApplicationInstances(options.AppPath);
        }

        if (includeCategoryPersistence)
        {
            RunStarsCategoryPersistenceProbe(options);
        }
    }
}
