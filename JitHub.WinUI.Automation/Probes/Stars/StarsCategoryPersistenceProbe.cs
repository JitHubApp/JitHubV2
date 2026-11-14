using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Native;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Applications.ApplicationProcessCleanup;
using static JitHub.WinUI.Automation.Applications.ProbeApplicationLauncher;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Interaction.MouseInput;
using static JitHub.WinUI.Automation.Probes.Stars.StarsCategories;
using static JitHub.WinUI.Automation.Screenshots.WindowCapture;
using static JitHub.WinUI.Automation.Windows.ReadyWindows;
using static JitHub.WinUI.Automation.Windows.WindowSizing;

namespace JitHub.WinUI.Automation.Probes.Stars;

/// <summary>
/// Drives the stars-categories probe.
/// </summary>
internal static class StarsCategoryPersistenceProbe
{
    // This method stays whole because it proves persistence across two app launches: the first creates, renames,
    // reorders and drag-assigns automation categories, and the relaunch checks the renamed category's order and
    // membership against the timestamped names generated at the top, then purges them.
    internal static void RunStarsCategoryPersistenceProbe(CaptureOptions options)
    {
        string suffix = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        string firstName = $"Automation Alpha {suffix}";
        string secondName = $"Automation Beta {suffix}";
        string renamedName = $"Automation Priority {suffix}";

        KillExistingApplicationInstances(options.AppPath);
        using (var app = LaunchApplication(options.AppPath, "--page=stars", "--theme=dark"))
        using (var automation = new UIA3Automation())
        {
            bool setupCompleted = false;
            try
            {
                Window window = GetReadyWindow(app, automation, "stars category persistence setup");
                ResizeWindow(window, 1180, 800);
                WaitForElement("StarsSearch", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSearch")), TimeSpan.FromSeconds(10));
                WaitForElement("Stars category setup row", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsList"))?.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(10));
                PurgeAutomationStarsCategories(window, automation);

                CreateStarsCategory(window, automation, firstName);
                CreateStarsCategory(window, automation, secondName);
                RenameStarsCategory(window, automation, secondName, renamedName);
                MoveStarsCategoryToTop(window, automation, renamedName);

                SelectStarsCategory(window, "All stars");
                AutomationElement list = WaitForElement("StarsList", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsList")), TimeSpan.FromSeconds(5));
                InvokeOrClick(WaitForElement("StarsSelectionMode", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSelectionMode")), TimeSpan.FromSeconds(5)));
                AutomationElement row = WaitForElement("Stars drag source", () => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(5));
                AssertStarsRepositoryAutomationIdentity(row);
                InvokeOrClick(WaitForElement(
                    "Stars drag source checkbox",
                    () => row.FindFirstDescendant(cf => cf.ByControlType(ControlType.CheckBox)),
                    TimeSpan.FromSeconds(5)));
                WaitUntil(
                    "Stars selected repository count",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsSelectionCount")) is AutomationElement count
                        && string.Equals(GetElementName(count), "1 selected", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(5));
                WaitForElement("StarsCancelSelection", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCancelSelection")), TimeSpan.FromSeconds(5));
                row = WaitForElement("selected Stars drag source", () => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(5));
                AutomationElement dragSurface = WaitForElement(
                    "Stars repository drag handle",
                    () => row.FindAllDescendants()
                        .FirstOrDefault(element => GetAutomationId(element).StartsWith("StarsDragHandle_", StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(5));
                AutomationElement target = WaitForElement(renamedName, () => FindVisibleStarsCategory(window, renamedName), TimeSpan.FromSeconds(5));
                if (NativeMethods.HasInvisibleForeignForegroundWindow(window.Properties.NativeWindowHandle.ValueOrDefault))
                {
                    InvokeOrClick(WaitForElement(
                        "StarsBulkCategory",
                        () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsBulkCategory")),
                        TimeSpan.FromSeconds(5)));
                    AutomationElement picker = WaitForElement(
                        "StarsCategoryPickerList",
                        () => FindElementInWindowOrDialog(window, automation, "StarsCategoryPickerList"),
                        TimeSpan.FromSeconds(5));
                    AutomationElement pickerItem = WaitForElement(
                        renamedName,
                        () => picker.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                            .FirstOrDefault(item => string.Equals(item.Name, renamedName, StringComparison.Ordinal)),
                        TimeSpan.FromSeconds(5));
                    AssertProbe(pickerItem.Patterns.SelectionItem.IsSupported, "The Stars category picker omitted native selection semantics.");
                    pickerItem.Patterns.SelectionItem.Pattern.Select();
                    InvokeOrClick(WaitForElement(
                        "Add selected repositories",
                        () => automation.GetDesktop().FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                            .FirstOrDefault(element => IsVisible(element) && string.Equals(element.Name, "Add", StringComparison.Ordinal)),
                        TimeSpan.FromSeconds(5)));
                    Console.WriteLine("stars-categories: used the visible bulk category action because an invisible foreign window owned the system pointer queue.");
                }
                else
                {
                    DragBetween(dragSurface, target, window);
                    try
                    {
                        WaitUntil(
                            "Stars category drop completion",
                            () => string.Equals(list.Properties.ItemStatus.ValueOrDefault, "drop-completed", StringComparison.Ordinal),
                            TimeSpan.FromSeconds(8));
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"Stars category drag stopped at '{list.Properties.ItemStatus.ValueOrDefault ?? "no-stage"}'.",
                            ex);
                    }
                }
                WaitForElement(
                    "Stars category assignment status",
                    () => window.FindAllDescendants()
                        .FirstOrDefault(element => IsVisible(element)
                            && GetElementName(element).Contains("Added", StringComparison.Ordinal)
                            && GetElementName(element).Contains(renamedName, StringComparison.Ordinal)),
                    TimeSpan.FromSeconds(8));
                AutomationElement? cancelSelection = window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCancelSelection"));
                if (IsVisible(cancelSelection))
                {
                    InvokeOrClick(cancelSelection!);
                }
                SelectStarsCategory(window, renamedName);
                WaitForElement("assigned Stars row", () => list.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)), TimeSpan.FromSeconds(5));
                CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-category-drag-assignment.png"));
                setupCompleted = true;
            }
            finally
            {
                if (!setupCompleted)
                {
                    TryPurgeAutomationStarsCategories(app, automation);
                }

                TryClose(app);
                KillExistingApplicationInstances(options.AppPath);
            }
        }

        using (var relaunched = LaunchApplication(options.AppPath, "--page=stars", "--theme=dark"))
        using (var automation = new UIA3Automation())
        {
            try
            {
                Window window = GetReadyWindow(relaunched, automation, "stars category persistence verification");
                ResizeWindow(window, 1180, 800);
                AutomationElement persisted = WaitForElement(
                    "persisted Stars category",
                    () => FindVisibleText(window, renamedName),
                    TimeSpan.FromSeconds(10));
                AssertProbe(IsVisible(persisted), "Stars category did not persist across app relaunch.");
                AutomationElement navigation = WaitForElement(
                    "StarsCategoryNavigation after relaunch",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryNavigation")),
                    TimeSpan.FromSeconds(5));
                string[] customCategoryNames = navigation.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                    .Where(IsVisible)
                    .Select(GetElementName)
                    .Where(name => name.StartsWith("Automation ", StringComparison.Ordinal))
                    .ToArray();
                AssertProbe(
                    customCategoryNames.Length >= 2 && string.Equals(customCategoryNames[0], renamedName, StringComparison.Ordinal),
                    "Reordered Stars category position did not persist across app relaunch.");
                SelectStarsCategory(window, renamedName);
                AutomationElement persistedList = WaitForElement(
                    "StarsList after category relaunch",
                    () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsList")),
                    TimeSpan.FromSeconds(5));
                AutomationElement persistedRow = WaitForElement(
                    "persisted category membership row",
                    () => persistedList.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem)),
                    TimeSpan.FromSeconds(5));
                AssertStarsRepositoryAutomationIdentity(persistedRow);
                CaptureWindow(window, Path.Combine(options.OutputDirectory, "stars-category-relaunch-persistence.png"));

                PurgeAutomationStarsCategories(window, automation);
            }
            finally
            {
                TryPurgeAutomationStarsCategories(relaunched, automation);
                TryClose(relaunched);
                KillExistingApplicationInstances(options.AppPath);
            }
        }
    }
}
