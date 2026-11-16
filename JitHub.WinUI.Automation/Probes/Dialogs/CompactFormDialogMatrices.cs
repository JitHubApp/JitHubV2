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
/// Exercises the new repository, widget customize, profile edit and repository delete dialogs at each compact viewport.
/// </summary>
internal static class CompactFormDialogMatrices
{
    internal static void RunShellNewRepositoryMatrix(CaptureOptions options)
    {
        using ProbeApplication probe = Launch(
            options,
            "shell-new-repository",
            "--page=repositories",
            "--scenario=repository-library",
            "--theme=dark");
        Window window = probe.Window;
        UIA3Automation automation = probe.Automation;
        WaitFor("repository library", () => Find(window, automation, "RepoManagePageRoot"));

        foreach (CompactViewport viewport in CompactViewports)
        {
            Resize(window, viewport);
            AutomationElement opener = WaitForVisible(
                "New Repository opener",
                () => Find(window, automation, "RepositoryLibraryNew"));
            Focus(opener);
            string baseline = Capture(window, options.OutputDirectory, $"dialogs-new-repository-{viewport.Token}-baseline.png");
            Invoke(opener);

            AutomationElement overlay = WaitForVisible(
                "shell modal overlay",
                () => Find(window, automation, "ShellModalOverlay"));
            AutomationElement dialog = WaitForVisible(
                "New Repository shell dialog",
                () => Find(window, automation, "ShellModalContent"));
            AutomationElement name = WaitForVisible(
                "repository name",
                () => Find(window, automation, "RepoFormNameTextBox"));
            AutomationElement create = WaitForVisible(
                "repository Create",
                () => Find(window, automation, "RepoFormCreateButton"));

            Assert(!create.IsEnabled, "New Repository Create was enabled with an empty required name.");
            Focus(name);
            Keyboard.Type("compact-dialog-contract");
            Keyboard.Press(VirtualKeyShort.TAB);
            WaitUntil("repository Create enables after valid input", () => Find(window, automation, "RepoFormCreateButton")?.IsEnabled == true);
            Focus(name);
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Press(VirtualKeyShort.BACK);
            Keyboard.Press(VirtualKeyShort.TAB);
            WaitUntil("repository Create disables after clearing input", () => Find(window, automation, "RepoFormCreateButton")?.IsEnabled == false);

            Rectangle contentBounds = dialog.BoundingRectangle;
            AssertShellOverlay(window, overlay, contentBounds, $"New Repository {viewport.Token}");
            AssertDialogVisualContract(
                window,
                automation,
                dialog,
                contentBounds,
                baseline,
                options.OutputDirectory,
                $"dialogs-new-repository-{viewport.Token}-open.png",
                $"New Repository {viewport.Token}");
            AssertFocusTrap(automation, dialog, contentBounds, $"New Repository {viewport.Token}");
            AssertNoLightDismiss(window, automation, "ShellModalContent", contentBounds, $"New Repository {viewport.Token}");

            // UIA invocation bypasses pointer hit-testing, so this directly exercises the
            // presentation coordinator while one real shell modal is already active.
            TryInvoke(opener);
            Thread.Sleep(120);
            AssertSingleVisible(window, automation, "ShellModalContent", $"New Repository rapid-open {viewport.Token}");

            CloseWithEscape(window, automation, "ShellModalContent", $"New Repository {viewport.Token}");
            AssertFocusRestored(automation, opener, $"New Repository {viewport.Token}");
            Pass(
                "shell-modal/new-repository",
                viewport,
                "scrim, centered max bounds, focus cycle, non-light-dismiss, validation, focus return, rapid-open dedupe");
        }
    }

    internal static void RunWidgetCustomizeMatrix(CaptureOptions options)
    {
        using ProbeApplication probe = Launch(
            options,
            "widget-customize",
            "--page=shell",
            "--theme=dark");
        Window window = probe.Window;
        UIA3Automation automation = probe.Automation;
        WaitFor("dashboard", () => Find(window, automation, "DashboardPageRoot"));

        foreach (CompactViewport viewport in CompactViewports)
        {
            Resize(window, viewport);
            AutomationElement opener = WaitForVisible(
                "Customize Home opener",
                () => Find(window, automation, "DashboardCustomizeButton"));
            Focus(opener);
            string baseline = Capture(window, options.OutputDirectory, $"dialogs-widget-customize-{viewport.Token}-baseline.png");
            Invoke(opener);

            AutomationElement overlay = WaitForVisible(
                "customize shell overlay",
                () => Find(window, automation, "ShellModalOverlay"));
            AutomationElement shellDialog = WaitForVisible(
                "customize shell content",
                () => Find(window, automation, "ShellModalContent"));
            AutomationElement customize = WaitForVisible(
                "Customize Home dialog",
                () => Find(window, automation, "DashboardCustomizeDialog"));
            AutomationElement save = WaitFor(
                "Customize Save",
                () => Find(window, automation, "DashboardCustomizeSaveButton"));
            EnsureVisible(save);

            Rectangle contentBounds = shellDialog.BoundingRectangle;
            AssertShellOverlay(window, overlay, contentBounds, $"Customize Home {viewport.Token}");
            AssertDialogVisualContract(
                window,
                automation,
                customize,
                contentBounds,
                baseline,
                options.OutputDirectory,
                $"dialogs-widget-customize-{viewport.Token}-open.png",
                $"Customize Home {viewport.Token}");
            AssertFocusTrap(automation, customize, contentBounds, $"Customize Home {viewport.Token}");
            AssertNoLightDismiss(window, automation, "DashboardCustomizeDialog", contentBounds, $"Customize Home {viewport.Token}");
            TryInvoke(opener);
            AssertSingleVisible(window, automation, "DashboardCustomizeDialog", $"Customize Home rapid-open {viewport.Token}");

            CloseWithEscape(window, automation, "DashboardCustomizeDialog", $"Customize Home {viewport.Token}");
            AssertFocusRestored(automation, opener, $"Customize Home {viewport.Token}");
            Pass(
                "shell-modal/widget-customize",
                viewport,
                "real widget controls, scrim, compact bounds, focus cycle, dismissal policy, focus return, rapid-open dedupe");
        }
    }

    internal static void RunProfileEditMatrix(CaptureOptions options)
    {
        using ProbeApplication probe = Launch(
            options,
            "profile-edit",
            "--page=profile",
            "--theme=dark");
        Window window = probe.Window;
        UIA3Automation automation = probe.Automation;
        WaitFor("profile", () => Find(window, automation, "ProfilePageRoot"));

        foreach (CompactViewport viewport in CompactViewports)
        {
            Resize(window, viewport);
            AutomationElement opener = WaitForVisible(
                "profile Edit opener",
                () => FindVisible(window, automation, "ProfileEditButton")
                    ?? FindVisible(window, automation, "ProfileCompactEditButton"));
            Focus(opener);
            string baseline = Capture(window, options.OutputDirectory, $"dialogs-profile-edit-{viewport.Token}-baseline.png");
            Invoke(opener);

            AutomationElement dialog = WaitForVisible(
                "profile Edit dialog",
                () => Find(window, automation, "ProfileEditDialog"));
            Capture(window, options.OutputDirectory, $"dialogs-profile-edit-{viewport.Token}-open-before-scroll.png");
            WaitForVisible("profile Name", () => Find(window, automation, "ProfileEditNameBox"));
            WaitForVisible("profile Bio", () => Find(window, automation, "ProfileEditBioBox"));
            AutomationElement hireable = WaitFor(
                "profile Hireable",
                () => Find(window, automation, "ProfileEditHireableToggle"));
            if (!IsVisible(hireable) && hireable.Patterns.ScrollItem.IsSupported)
            {
                hireable.Patterns.ScrollItem.Pattern.ScrollIntoView();
            }
            WaitUntil("profile Hireable is reachable", () => IsVisible(Find(window, automation, "ProfileEditHireableToggle")));
            Focus(hireable);
            Rectangle contentBounds = GetDialogContentEnvelope(dialog);

            AssertDialogVisualContract(
                window,
                automation,
                dialog,
                contentBounds,
                baseline,
                options.OutputDirectory,
                $"dialogs-profile-edit-{viewport.Token}-open.png",
                $"Profile Edit {viewport.Token}");
            AssertFocusTrap(automation, dialog, contentBounds, $"Profile Edit {viewport.Token}");
            AssertNoLightDismiss(window, automation, "ProfileEditDialog", contentBounds, $"Profile Edit {viewport.Token}");
            TryInvoke(opener);
            AssertSingleVisible(window, automation, "ProfileEditDialog", $"Profile Edit rapid-open {viewport.Token}");

            CloseWithEscape(window, automation, "ProfileEditDialog", $"Profile Edit {viewport.Token}");
            AssertFocusRestored(automation, opener, $"Profile Edit {viewport.Token}");
            Pass(
                "content-dialog/profile-edit",
                viewport,
                "standard surface, scrim, compact max bounds, focus cycle, non-light-dismiss, Esc, focus return, rapid-open dedupe");
        }
    }

    internal static void RunRepositoryDeleteMatrix(CaptureOptions options)
    {
        using ProbeApplication probe = Launch(
            options,
            "repository-delete",
            "--page=repositories",
            "--scenario=repository-library",
            "--theme=dark");
        Window window = probe.Window;
        UIA3Automation automation = probe.Automation;
        WaitFor("repository library", () => Find(window, automation, "RepoManagePageRoot"));
        Resize(window, CompactViewports[0]);
        Invoke(WaitForVisible("selection mode", () => Find(window, automation, "RepositoryLibrarySelectionMode")));
        AutomationElement selection = WaitForVisible(
            "repository selection",
            () => Find(window, automation, "RepositoryLibrarySelect_900000"));
        Toggle(selection);
        WaitUntil("one repository selected", () => Find(window, automation, "RepositoryLibraryDeleteSelected")?.IsEnabled == true);

        foreach (CompactViewport viewport in CompactViewports)
        {
            Resize(window, viewport);
            AutomationElement opener = WaitForVisible(
                "delete selected repositories",
                () => Find(window, automation, "RepositoryLibraryDeleteSelected"));
            Focus(opener);
            string baseline = Capture(window, options.OutputDirectory, $"dialogs-repository-delete-{viewport.Token}-baseline.png");
            Invoke(opener);

            AutomationElement dialog = WaitForVisible(
                "repository delete confirmation",
                () => Find(window, automation, "RepositoryDeleteConfirmation"));
            AutomationElement delete = FindDialogButton(window, automation, dialog, "Delete");
            AutomationElement cancel = FindDialogButton(window, automation, dialog, "Cancel");
            Assert(delete.IsEnabled && cancel.IsEnabled, "Repository delete confirmation did not expose enabled Delete and Cancel commands.");
            Rectangle contentBounds = GetDialogContentEnvelope(dialog);

            AssertDialogVisualContract(
                window,
                automation,
                dialog,
                contentBounds,
                baseline,
                options.OutputDirectory,
                $"dialogs-repository-delete-{viewport.Token}-open.png",
                $"Repository Delete {viewport.Token}");
            AssertFocusTrap(automation, dialog, contentBounds, $"Repository Delete {viewport.Token}");
            AssertNoLightDismiss(window, automation, "RepositoryDeleteConfirmation", contentBounds, $"Repository Delete {viewport.Token}");
            TryInvoke(opener);
            AssertSingleVisible(window, automation, "RepositoryDeleteConfirmation", $"Repository Delete rapid-open {viewport.Token}");

            CloseWithEscape(window, automation, "RepositoryDeleteConfirmation", $"Repository Delete {viewport.Token}");
            AssertFocusRestored(automation, opener, $"Repository Delete {viewport.Token}");
            Pass(
                "content-dialog/delete-confirmation",
                viewport,
                "destructive default-safe confirmation, scrim, compact bounds, focus cycle, dismissal policy, focus return, rapid-open dedupe");
        }
    }
}
