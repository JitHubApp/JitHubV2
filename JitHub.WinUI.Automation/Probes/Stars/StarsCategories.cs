using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;

namespace JitHub.WinUI.Automation.Probes.Stars;

/// <summary>
/// Creates, renames, reorders, selects and deletes star categories, and checks star row automation identity.
/// </summary>
internal static class StarsCategories
{
    internal static void TryPurgeAutomationStarsCategories(Application application, UIA3Automation automation)
    {
        try
        {
            Window? window = application.GetMainWindow(automation);
            if (window is not null)
            {
                PurgeAutomationStarsCategories(window, automation);
            }
        }
        catch
        {
            // Best effort after a failed probe. The next run purges test-owned rows before setup.
        }
    }

    internal static void PurgeAutomationStarsCategories(Window window, UIA3Automation automation)
    {
        const string automationPrefix = "Automation ";
        for (int attempt = 0; attempt < 100; attempt++)
        {
            AutomationElement navigation = WaitForElement(
                "StarsCategoryNavigation",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryNavigation")),
                TimeSpan.FromSeconds(5));
            string? categoryName = navigation.FindAllDescendants()
                .Where(IsVisible)
                .Select(element => element.Name)
                .FirstOrDefault(name => name.StartsWith(automationPrefix, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(categoryName))
            {
                return;
            }

            DeleteStarsCategory(window, automation, categoryName);
            WaitUntil(
                $"test category deletion: {categoryName}",
                () => !IsVisible(FindVisibleStarsCategory(window, categoryName)),
                TimeSpan.FromSeconds(10));
        }

        throw new InvalidOperationException("Unable to purge all automation-owned Stars categories.");
    }

    internal static void CreateStarsCategory(Window window, UIA3Automation automation, string name)
    {
        InvokeOrClick(WaitForElement("StarsNewCategory", () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsNewCategory")), TimeSpan.FromSeconds(5)));
        AutomationElement dialog = WaitForElement(
            "StarsCreateCategoryDialog",
            () => FindElementInWindowOrDialog(window, automation, "StarsCreateCategoryDialog"),
            TimeSpan.FromSeconds(5));
        AutomationElement nameBox = WaitForElement(
            "StarsCategoryNameBox",
            () => dialog.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryNameBox")),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(nameBox);
        nameBox.AsTextBox().Text = name;
        AssertProbe(string.Equals(nameBox.AsTextBox().Text, name, StringComparison.Ordinal), "Stars category name was not entered completely.");
        InvokeOrClick(WaitForElement(
            "Create category",
            () => dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(element => IsVisible(element) && string.Equals(element.Name, "Create", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)));
        WaitUntil(
            "category dialog dismissal",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, "StarsCreateCategoryDialog")),
            TimeSpan.FromSeconds(5));
        try
        {
            WaitForElement(
                $"created category {name}",
                () => FindVisibleStarsCategory(window, name),
                TimeSpan.FromSeconds(15));
        }
        catch
        {
            AutomationElement? navigation = window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryNavigation"));
            string peers = navigation is null
                ? "<navigation unavailable>"
                : string.Join(" | ", navigation.FindAllDescendants()
                    .Where(IsVisible)
                    .Select(element => $"{element.ControlType}:{GetElementName(element)} [{GetAutomationId(element)}]"));
            string status = string.Join(" | ", window.FindAllDescendants()
                .Where(element => IsVisible(element) && GetElementName(element).Contains("category", StringComparison.OrdinalIgnoreCase))
                .Select(element => $"{element.ControlType}:{GetElementName(element)}"));
            Console.Error.WriteLine($"Stars category navigation peers: {peers}");
            Console.Error.WriteLine($"Stars category status peers: {status}");
            throw;
        }
        SelectStarsCategory(window, name);
    }

    internal static void RenameStarsCategory(Window window, UIA3Automation automation, string oldName, string newName)
    {
        SelectStarsCategory(window, oldName);
        InvokeOrClick(OpenStarsCategoryMenuItem(window, automation, "StarsCategoryActionRename"));
        AutomationElement dialog = WaitForElement(
            "StarsEditCategoryDialog",
            () => FindElementInWindowOrDialog(window, automation, "StarsEditCategoryDialog"),
            TimeSpan.FromSeconds(5));
        AutomationElement nameBox = WaitForElement(
            "StarsCategoryNameBox",
            () => dialog.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryNameBox")),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(nameBox);
        nameBox.AsTextBox().Text = newName;
        AssertProbe(string.Equals(nameBox.AsTextBox().Text, newName, StringComparison.Ordinal), "Stars renamed category value was not entered completely.");
        InvokeOrClick(WaitForElement(
            "Save category",
            () => dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(element => IsVisible(element) && string.Equals(element.Name, "Save", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5)));
        WaitUntil(
            "category rename dialog dismissal",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, "StarsEditCategoryDialog")),
            TimeSpan.FromSeconds(5));
        WaitForElement(
            $"renamed category {newName}",
            () => FindVisibleText(window, newName),
            TimeSpan.FromSeconds(5));
        SelectStarsCategory(window, newName);
    }

    internal static void MoveStarsCategoryToTop(Window window, UIA3Automation automation, string name)
    {
        SelectStarsCategory(window, name);
        for (int attempt = 0; attempt < 100; attempt++)
        {
            AutomationElement moveUp = OpenStarsCategoryMenuItem(window, automation, "StarsCategoryActionMoveUp");
            if (!moveUp.Properties.IsEnabled.ValueOrDefault)
            {
                Keyboard.Press(VirtualKeyShort.ESCAPE);
                return;
            }

            InvokeOrClick(moveUp);
            Thread.Sleep(140);
        }

        throw new InvalidOperationException($"Category '{name}' did not reach the first custom-category position.");
    }

    private static void DeleteStarsCategory(Window window, UIA3Automation automation, string name)
    {
        SelectStarsCategory(window, name);
        InvokeOrClick(OpenStarsCategoryMenuItem(window, automation, "StarsCategoryActionDelete"));
        AutomationElement dialog = WaitForElement(
            "StarsDeleteCategoryDialog",
            () => FindElementInWindowOrDialog(window, automation, "StarsDeleteCategoryDialog"),
            TimeSpan.FromSeconds(5));
        AutomationElement delete = WaitForElement(
            "Delete category primary action",
            () => dialog.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
                .FirstOrDefault(element =>
                    IsVisible(element) &&
                    string.Equals(GetAutomationId(element), "PrimaryButton", StringComparison.Ordinal)),
            TimeSpan.FromSeconds(5));
        AssertProbe(delete.Properties.IsEnabled.ValueOrDefault, "Delete category primary action was disabled.");
        InvokeOrClick(delete);
        WaitUntil(
            "delete category dialog dismissal",
            () => !IsVisible(FindElementInWindowOrDialog(window, automation, "StarsDeleteCategoryDialog")),
            TimeSpan.FromSeconds(10));
    }

    private static AutomationElement OpenStarsCategoryMenuItem(
        Window window,
        UIA3Automation automation,
        string automationId)
    {
        AutomationElement menuButton = WaitForElement(
            "StarsCategoryMenu",
            () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryMenu")),
            TimeSpan.FromSeconds(5));
        for (int attempt = 0; attempt < 3; attempt++)
        {
            InvokeOrClick(menuButton);
            Thread.Sleep(250);
            AutomationElement? item = automation.GetDesktop()
                .FindFirstDescendant(cf => cf.ByAutomationId(automationId));
            if (IsVisible(item))
            {
                return item!;
            }

            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Thread.Sleep(150);
        }

        throw new InvalidOperationException($"Timed out waiting for {automationId} after reopening the category menu.");
    }

    internal static void SelectStarsCategory(Window window, string name)
    {
        AutomationElement? category = FindVisibleStarsCategory(window, name);
        if (!IsVisible(category))
        {
            AutomationElement navigationAnchor = WaitForElement(
                "StarsNewCategory",
                () => window.FindFirstDescendant(cf => cf.ByAutomationId("StarsNewCategory")),
                TimeSpan.FromSeconds(5));
            System.Drawing.Point panePoint = CenterPoint(navigationAnchor, window);
            panePoint.Y = window.BoundingRectangle.Center().Y;
            Mouse.MoveTo(panePoint);
            Mouse.Scroll(30);
            Thread.Sleep(250);
            for (int attempt = 0; attempt < 25 && !IsVisible(category); attempt++)
            {
                category = FindVisibleStarsCategory(window, name);
                if (IsVisible(category))
                {
                    break;
                }

                Mouse.Scroll(-4);
                Thread.Sleep(120);
            }
        }

        category = WaitForElement(name, () => IsVisible(category) ? category : FindVisibleStarsCategory(window, name), TimeSpan.FromSeconds(12));
        if (category.Patterns.SelectionItem.IsSupported)
        {
            category.Patterns.SelectionItem.Pattern.Select();
        }
        else
        {
            InvokeOrClick(category);
        }
        Thread.Sleep(450);
    }

    internal static AutomationElement? FindVisibleStarsCategory(Window window, string name)
    {
        AutomationElement? navigation = window.FindFirstDescendant(cf => cf.ByAutomationId("StarsCategoryNavigation"));
        if (navigation is null)
        {
            return null;
        }

        return navigation.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                   .FirstOrDefault(element => IsVisible(element) && GetElementName(element).Contains(name, StringComparison.Ordinal))
               ?? navigation.FindAllDescendants(cf => cf.ByText(name)).FirstOrDefault(IsVisible);
    }

    internal static void AssertStarsRepositoryAutomationIdentity(AutomationElement row)
    {
        AssertProbe(
            row.ControlType == ControlType.ListItem,
            "Stars repository automation target is not the native ListViewItem container.");
        AssertProbe(
            GetAutomationId(row).StartsWith("StarsRepository_", StringComparison.Ordinal),
            "Stars repository row did not expose its stable automation ID on the ListViewItem container.");
        AssertProbe(
            !string.IsNullOrWhiteSpace(GetElementName(row)) && GetElementName(row).Contains('/', StringComparison.Ordinal),
            "Stars repository row did not expose a meaningful owner/name automation label.");
    }
}
