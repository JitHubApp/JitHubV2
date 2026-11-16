using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Options;
using static JitHub.WinUI.Automation.Probes.Dialogs.CompactDialogAssertions;
using static JitHub.WinUI.Automation.Probes.Dialogs.CompactDialogElements;
using static JitHub.WinUI.Automation.Probes.Dialogs.CompactDialogMatrixProbe;

namespace JitHub.WinUI.Automation.Probes.Dialogs;

/// <summary>
/// Exercises the star category dialogs at each compact viewport.
/// </summary>
internal static class CompactStarsCategoryDialogMatrix
{
    // This method stays whole because the category it creates through the Create Category dialog at the first compact
    // viewport is the same one it selects later to open the Rename and Delete dialogs at 640x600, so the create, edit,
    // and delete checks share one launched app and one category name.
    internal static void RunStarsCategoryMatrix(CaptureOptions options)
    {
        using ProbeApplication probe = Launch(
            options,
            "stars-categories",
            "--page=stars",
            "--theme=dark");
        Window window = probe.Window;
        UIA3Automation automation = probe.Automation;
        WaitFor("Stars", () => Find(window, automation, "StarsPageRoot"));
        string categoryName = $"Dialog Matrix {Environment.ProcessId}";

        foreach (CompactViewport viewport in CompactViewports)
        {
            Resize(window, viewport);
            EnsureStarsCategoriesOpen(window, automation);
            AutomationElement opener = WaitForVisible(
                "new Stars category",
                () => Find(window, automation, "StarsNewCategory"));
            Focus(opener);
            string baseline = Capture(window, options.OutputDirectory, $"dialogs-stars-create-{viewport.Token}-baseline.png");
            Invoke(opener);

            AutomationElement dialog = WaitForVisible(
                "create Stars category",
                () => Find(window, automation, "StarsCreateCategoryDialog"));
            AutomationElement name = WaitForVisible(
                "Stars category name",
                () => Find(window, automation, "StarsCategoryNameBox"));
            AutomationElement create = FindDialogButton(window, automation, dialog, "Create");
            Rectangle contentBounds = GetDialogContentEnvelope(dialog);

            AssertDialogVisualContract(
                window,
                automation,
                dialog,
                contentBounds,
                baseline,
                options.OutputDirectory,
                $"dialogs-stars-create-{viewport.Token}-open.png",
                $"Stars Create Category {viewport.Token}");
            AssertFocusTrap(automation, dialog, contentBounds, $"Stars Create Category {viewport.Token}");
            AssertNoLightDismiss(window, automation, "StarsCreateCategoryDialog", contentBounds, $"Stars Create Category {viewport.Token}");
            TryInvoke(opener);
            AssertSingleVisible(window, automation, "StarsCreateCategoryDialog", $"Stars Create Category rapid-open {viewport.Token}");

            // Empty submission is deterministic and exercises validation/retry without
            // contacting GitHub. Invoke twice to prove repeated invalid submissions do not
            // duplicate or dismiss the live dialog.
            Invoke(create);
            TryInvoke(create);
            AutomationElement validation = WaitForVisible(
                "Stars category validation",
                () => Find(window, automation, "StarsCategoryDialogError"));
            Assert(
                validation.Name.Contains("Enter a category name", StringComparison.OrdinalIgnoreCase),
                $"Stars category validation exposed unexpected text '{validation.Name}'.");
            AssertSingleVisible(window, automation, "StarsCreateCategoryDialog", $"Stars repeated-submit {viewport.Token}");

            if (viewport == CompactViewports[0])
            {
                Focus(name);
                Keyboard.Type(categoryName);
                Keyboard.Press(VirtualKeyShort.TAB);
                WaitUntil(
                    "Stars category name is committed",
                    () => string.Equals(name.AsTextBox().Text, categoryName, StringComparison.Ordinal));
                Invoke(FindDialogButton(window, automation, dialog, "Create"));
                WaitUntil(
                    "Stars category creation closes",
                    () => !IsVisible(Find(window, automation, "StarsCreateCategoryDialog")));
                WaitUntil(
                    $"{categoryName} becomes the selected Stars view",
                    () => string.Equals(
                        Find(window, automation, "StarsCurrentViewTitle")?.Name,
                        categoryName,
                        StringComparison.Ordinal));
                AssertFocusRestored(automation, opener, $"Stars Create Category {viewport.Token}");
            }
            else
            {
                CloseWithEscape(window, automation, "StarsCreateCategoryDialog", $"Stars Create Category {viewport.Token}");
                AssertFocusRestored(automation, opener, $"Stars Create Category {viewport.Token}");
            }

            Pass(
                "content-dialog/stars-category-create",
                viewport,
                "real category fields, validation retry, repeated-submit containment, scrim, compact bounds, focus and dismissal contracts");
        }

        Resize(window, CompactViewports[^1]);
        EnsureStarsCategoriesOpen(window, automation);
        SelectStarsCategory(window, automation, categoryName);
        AutomationElement menu = WaitForVisible("Stars category menu", () => Find(window, automation, "StarsCategoryMenu"));

        Invoke(menu);
        AutomationElement rename = WaitForVisible("rename Stars category", () => Find(window, automation, "StarsCategoryActionRename"));
        Invoke(rename);
        AutomationElement editDialog = WaitForVisible("edit Stars category", () => Find(window, automation, "StarsEditCategoryDialog"));
        AssertDialogGeometry(window, GetDialogContentEnvelope(editDialog), "Stars Edit Category 640x600");
        AssertFocusTrap(automation, editDialog, GetDialogContentEnvelope(editDialog), "Stars Edit Category 640x600");
        CloseWithEscape(window, automation, "StarsEditCategoryDialog", "Stars Edit Category 640x600");
        Pass("content-dialog/stars-category-edit", CompactViewports[^1], "real edit dialog, compact max bounds, focus cycle, Esc");

        EnsureStarsCategoriesOpen(window, automation);
        SelectStarsCategory(window, automation, categoryName);
        menu = WaitForVisible("Stars category menu", () => Find(window, automation, "StarsCategoryMenu"));
        Invoke(menu);
        AutomationElement delete = WaitForVisible("delete Stars category", () => Find(window, automation, "StarsCategoryActionDelete"));
        Invoke(delete);
        AutomationElement deleteDialog = WaitForVisible(
            "delete Stars category confirmation",
            () => Find(window, automation, "StarsDialog_Deletecategory"));
        AssertDialogGeometry(window, GetDialogContentEnvelope(deleteDialog), "Stars Delete Category 640x600");
        FindDialogButton(window, automation, deleteDialog, "Delete");
        FindDialogButton(window, automation, deleteDialog, "Cancel");
        AssertFocusTrap(automation, deleteDialog, GetDialogContentEnvelope(deleteDialog), "Stars Delete Category 640x600");
        CloseWithEscape(window, automation, "StarsDialog_Deletecategory", "Stars Delete Category 640x600");
        Pass("content-dialog/stars-category-delete", CompactViewports[^1], "destructive category confirmation, compact max bounds, focus cycle, Esc");
    }

    private static void EnsureStarsCategoriesOpen(Window window, UIA3Automation automation)
    {
        if (IsVisible(Find(window, automation, "StarsNewCategory")))
        {
            return;
        }

        AutomationElement open = WaitForVisible(
            "open Stars categories",
            () => Find(window, automation, "StarsOpenCategories"));
        Invoke(open);
        WaitForVisible("Stars categories", () => Find(window, automation, "StarsNewCategory"));
    }

    private static void SelectStarsCategory(
        Window window,
        UIA3Automation automation,
        string categoryName)
    {
        AutomationElement? currentTitle = Find(window, automation, "StarsCurrentViewTitle");
        if (string.Equals(currentTitle?.Name, categoryName, StringComparison.Ordinal))
        {
            return;
        }

        AutomationElement navigation = WaitFor(
            "Stars category navigation",
            () => Find(window, automation, "StarsCategoryNavigation"));
        if (navigation.Patterns.Scroll.IsSupported)
        {
            var scroll = navigation.Patterns.Scroll.Pattern;
            for (int index = 0; index < 8 && scroll.VerticallyScrollable.ValueOrDefault; index++)
            {
                scroll.Scroll(
                    FlaUI.Core.Definitions.ScrollAmount.NoAmount,
                    FlaUI.Core.Definitions.ScrollAmount.LargeIncrement);
                Thread.Sleep(60);
            }
        }

        AutomationElement category = WaitFor(
            categoryName,
            () =>
            {
                return navigation.FindAllDescendants(
                        cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.ListItem))
                    .FirstOrDefault(candidate =>
                        candidate.Name.Contains(categoryName, StringComparison.Ordinal));
            });
        EnsureVisible(category);
        if (category.Patterns.SelectionItem.IsSupported)
        {
            category.Patterns.SelectionItem.Pattern.Select();
        }
        else
        {
            Invoke(category);
        }
        Thread.Sleep(180);
    }
}
