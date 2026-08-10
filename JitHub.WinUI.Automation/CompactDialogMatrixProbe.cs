using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

internal static class CompactDialogMatrixProbe
{
    public const string ProbeName = "compact-dialog-matrix";

    private static readonly CompactViewport[] CompactViewports =
    [
        new(900, 700),
        new(760, 650),
        new(640, 600)
    ];

    private static readonly List<DialogMatrixResult> Results = [];
    private static string _runDataRoot = string.Empty;

    public static void Run(CaptureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Directory.CreateDirectory(options.OutputDirectory);
        Results.Clear();
        _runDataRoot = Path.Combine(
            Path.GetTempPath(),
            "JitHub.WinUI.Automation",
            $"compact-dialog-matrix-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_runDataRoot);

        try
        {
            string? selectedGroup = Environment.GetEnvironmentVariable("JITHUB_COMPACT_DIALOG_GROUP");
            RunSelectedGroup(selectedGroup, "shell-new-repository", () => RunShellNewRepositoryMatrix(options));
            RunSelectedGroup(selectedGroup, "widget-customize", () => RunWidgetCustomizeMatrix(options));
            RunSelectedGroup(selectedGroup, "profile-edit", () => RunProfileEditMatrix(options));
            RunSelectedGroup(selectedGroup, "repository-delete", () => RunRepositoryDeleteMatrix(options));
            RunSelectedGroup(selectedGroup, "stars-categories", () => RunStarsCategoryMatrix(options));
            RunSelectedGroup(selectedGroup, "issue-pr-production-previews", () => RunIssueAndPullRequestPreviewMatrix(options));
        }
        finally
        {
            WriteReport(options.OutputDirectory);
            TryDeleteDirectory(_runDataRoot);
        }

        DialogMatrixResult[] failures = Results
            .Where(static result => string.Equals(result.Status, "failed", StringComparison.Ordinal))
            .ToArray();
        if (failures.Length > 0)
        {
            throw new InvalidOperationException(
                $"Compact dialog matrix failed {failures.Length} row(s): " +
                string.Join(" | ", failures.Select(static result => $"{result.Surface}: {result.Detail}")));
        }

        int passed = Results.Count(static result => string.Equals(result.Status, "passed", StringComparison.Ordinal));
        int blocked = Results.Count(static result => string.Equals(result.Status, "blocked", StringComparison.Ordinal));
        Console.WriteLine(
            $"compact-dialog-matrix: passed={passed}; blocked={blocked}; " +
            $"report={Path.Combine(options.OutputDirectory, "compact-dialog-matrix-results.json")}");
    }

    private static void RunSelectedGroup(string? selectedGroup, string group, Action action)
    {
        if (string.IsNullOrWhiteSpace(selectedGroup) ||
            string.Equals(selectedGroup, group, StringComparison.OrdinalIgnoreCase))
        {
            RunGroup(group, action);
        }
    }

    private static void RunShellNewRepositoryMatrix(CaptureOptions options)
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

    private static void RunWidgetCustomizeMatrix(CaptureOptions options)
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

    private static void RunProfileEditMatrix(CaptureOptions options)
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

    private static void RunRepositoryDeleteMatrix(CaptureOptions options)
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

    private static void RunStarsCategoryMatrix(CaptureOptions options)
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

    private static void RunIssueAndPullRequestPreviewMatrix(CaptureOptions options)
    {
        using (ProbeApplication issues = Launch(
            options,
            "issue-dialog-matrix",
            "--page=repo-issues",
            "--scenario=compact-dialog-matrix",
            "--theme=dark",
            $"--repo={options.RepositoryFullName}"))
        {
            WaitFor("repository Issues", () => Find(issues.Window, issues.Automation, "RepoIssuesPageRoot"));
            WaitFor("repository issue detail", () => Find(issues.Window, issues.Automation, "RepoIssuesDetailTitle"));
            LiveDialogContract[] contracts =
            [
                new(
                    "content-dialog/issue-create",
                    "RepoIssuesCreateDialog",
                    (window, automation) => OpenIssueDialog(window, automation, "RepoIssuesNewIssueButton"),
                    new("RepoIssuesCreateTitleBox", "Create", "RepoIssuesCreateDialogError")),
                new(
                    "content-dialog/issue-edit",
                    "RepoIssuesEditDialog",
                    (window, automation) => OpenIssueDialog(window, automation, "RepoIssuesEditButton"),
                    new("RepoIssuesEditTitleBox", "Save", "RepoIssuesEditDialogError")),
                new(
                    "content-dialog/issue-metadata",
                    "RepoIssuesMetadataDialog",
                    (window, automation) => OpenIssueDialog(window, automation, "RepoIssuesInspectorMetadataButton"),
                    null),
                new(
                    "content-dialog/issue-reactions",
                    "RepoIssuesReactionDialog",
                    (window, automation) => OpenIssueDialog(window, automation, "RepoIssuesReactionsButton"),
                    null)
            ];
            foreach (LiveDialogContract contract in contracts)
            {
                RunLiveDialogContract(options, issues, contract);
            }
        }

        using (ProbeApplication pulls = Launch(
            options,
            "pull-request-dialog-matrix",
            "--page=repo-pulls",
            "--scenario=compact-dialog-matrix",
            "--theme=dark",
            $"--repo={options.RepositoryFullName}"))
        {
            WaitFor("repository Pull Requests", () => Find(pulls.Window, pulls.Automation, "RepoPullRequestsPageRoot"));
            WaitFor("repository pull request detail", () => Find(pulls.Window, pulls.Automation, "RepoPullRequestsDetailTitle"));
            LiveDialogContract[] contracts =
            [
                new(
                    "content-dialog/pr-create",
                    "RepoPullRequestsCreateDialog",
                    (window, automation) => OpenPullRequestDialog(window, automation, "RepoPullRequestsNewButton"),
                    new("RepoPullRequestsCreateTitleBox", "Create", "RepoPullRequestsCreateDialogError")),
                new(
                    "content-dialog/pr-edit",
                    "RepoPullRequestsEditDialog",
                    (window, automation) => OpenPullRequestDialog(window, automation, "RepoPullRequestsEditButton"),
                    new("RepoPullRequestsEditTitleBox", "Save", "RepoPullRequestsEditDialogError")),
                new(
                    "content-dialog/pr-metadata",
                    "RepoPullRequestsMetadataDialog",
                    (window, automation) => OpenPullRequestDialog(window, automation, "RepoPullRequestsMetadataButton"),
                    null),
                new(
                    "content-dialog/pr-reactions",
                    "RepoPullRequestsReactionDialog",
                    (window, automation) => OpenPullRequestDialog(window, automation, "RepoPullRequestsReactionsButton"),
                    null),
                new(
                    "content-dialog/pr-review",
                    "RepoPullRequestsSubmitReviewDialog",
                    (window, automation) => OpenPullRequestDialog(window, automation, "RepoPullRequestsSubmitReviewButton"),
                    null),
                new(
                    "content-dialog/pr-merge",
                    "RepoPullRequestsMergeDialog",
                    OpenPullRequestMergeDialog,
                    null)
            ];
            foreach (LiveDialogContract contract in contracts)
            {
                RunLiveDialogContract(options, pulls, contract);
            }
        }
    }

    private static void RunLiveDialogContract(
        CaptureOptions options,
        ProbeApplication probe,
        LiveDialogContract contract)
    {
        foreach (CompactViewport viewport in CompactViewports)
        {
            Resize(probe.Window, viewport);
            string token = contract.Surface.Replace('/', '-');
            string baseline = Capture(
                probe.Window,
                options.OutputDirectory,
                $"dialogs-{token}-{viewport.Token}-baseline.png");

            Resize(probe.Window, new CompactViewport(1366, 900));
            AutomationElement opener = contract.Open(probe.Window, probe.Automation);
            AutomationElement dialog = WaitForVisible(
                contract.Surface,
                () => Find(probe.Window, probe.Automation, contract.DialogAutomationId));
            Resize(probe.Window, viewport);
            Rectangle contentBounds = GetDialogContentEnvelope(dialog);

            AssertDialogVisualContract(
                probe.Window,
                probe.Automation,
                dialog,
                contentBounds,
                baseline,
                options.OutputDirectory,
                $"dialogs-{token}-{viewport.Token}-open.png",
                $"{contract.Surface} {viewport.Token}");
            AssertFocusTrap(probe.Automation, dialog, contentBounds, $"{contract.Surface} {viewport.Token}");
            AssertNoLightDismiss(
                probe.Window,
                probe.Automation,
                contract.DialogAutomationId,
                contentBounds,
                $"{contract.Surface} {viewport.Token}");

            if (contract.Validation is not null)
            {
                AssertRepeatedInvalidSubmission(
                    probe.Window,
                    probe.Automation,
                    dialog,
                    contract.DialogAutomationId,
                    contract.Validation,
                    $"{contract.Surface} {viewport.Token}");
            }

            CloseWithEscape(
                probe.Window,
                probe.Automation,
                contract.DialogAutomationId,
                $"{contract.Surface} {viewport.Token}");
            _ = opener;
            Pass(
                contract.Surface,
                viewport,
                "real production dialog, centered window host, single scrim, compact bounds, focus cycle, non-light-dismiss, Esc" +
                (contract.Validation is null ? string.Empty : ", validation and repeated-submit containment"));
        }
    }

    private static AutomationElement OpenIssueDialog(
        Window window,
        UIA3Automation automation,
        string openerAutomationId)
    {
        if (string.Equals(openerAutomationId, "RepoIssuesInspectorMetadataButton", StringComparison.Ordinal) &&
            !IsVisible(Find(window, automation, openerAutomationId)))
        {
            Invoke(WaitForVisible(
                "issue inspector opener",
                () => FindVisible(window, automation, "RepoIssuesOpenInspectorPaneButton")
                    ?? FindVisible(window, automation, "RepoIssuesCompactOpenInspectorPaneButton")));
        }

        AutomationElement opener = WaitForVisible(
            openerAutomationId,
            () => Find(window, automation, openerAutomationId));
        Focus(opener);
        Invoke(opener);
        return opener;
    }

    private static AutomationElement OpenPullRequestDialog(
        Window window,
        UIA3Automation automation,
        string openerAutomationId)
    {
        if (openerAutomationId is "RepoPullRequestsEditButton" or
            "RepoPullRequestsMetadataButton" or
            "RepoPullRequestsReactionsButton")
        {
            if (!IsVisible(Find(window, automation, openerAutomationId)))
            {
                Invoke(WaitForVisible(
                    "pull request inspector opener",
                    () => Find(window, automation, "RepoPullRequestsOpenInspectorPaneButton")));
            }
        }

        AutomationElement? opener = FindVisible(window, automation, openerAutomationId);
        if (opener is null && string.Equals(openerAutomationId, "RepoPullRequestsSubmitReviewButton", StringComparison.Ordinal))
        {
            Invoke(WaitForVisible(
                "pull request actions menu",
                () => Find(window, automation, "RepoPullRequestsCompactActionsButton")));
            opener = WaitForVisible(
                "submit review action",
                () => Find(window, automation, "RepoPullRequestsCompactSubmitReviewAction"));
        }

        opener ??= WaitForVisible(openerAutomationId, () => Find(window, automation, openerAutomationId));
        Focus(opener);
        Invoke(opener);
        return opener;
    }

    private static AutomationElement OpenPullRequestMergeDialog(Window window, UIA3Automation automation)
    {
        AutomationElement? merge = FindVisible(window, automation, "RepoPullRequestsMergeButton");
        if (merge is not null)
        {
            Focus(merge);
            Invoke(merge);
            AutomationElement action = WaitForVisible(
                "merge commit action",
                () => Find(window, automation, "RepoPullRequestsMergeCommitAction"));
            Invoke(action);
            return merge;
        }

        AutomationElement menu = WaitForVisible(
            "pull request actions menu",
            () => Find(window, automation, "RepoPullRequestsCompactActionsButton"));
        Focus(menu);
        Invoke(menu);
        AutomationElement compactAction = WaitForVisible(
            "compact merge commit action",
            () => Find(window, automation, "RepoPullRequestsCompactMergeCommitAction"));
        Invoke(compactAction);
        return menu;
    }

    private static void AssertRepeatedInvalidSubmission(
        Window window,
        UIA3Automation automation,
        AutomationElement dialog,
        string dialogAutomationId,
        DialogValidationContract validation,
        string context)
    {
        AutomationElement field = WaitForVisible(
            validation.FieldAutomationId,
            () => Find(window, automation, validation.FieldAutomationId));
        Focus(field);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Press(VirtualKeyShort.BACK);
        Keyboard.Press(VirtualKeyShort.TAB);

        AutomationElement primary = FindDialogButton(window, automation, dialog, validation.PrimaryButtonName);
        Invoke(primary);
        TryInvoke(primary);
        WaitForVisible(
            validation.ErrorAutomationId,
            () => Find(window, automation, validation.ErrorAutomationId));
        AssertSingleVisible(window, automation, dialogAutomationId, $"{context} repeated invalid submit");
    }

    private static void RunGroup(string surface, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            Results.Add(new DialogMatrixResult(
                surface,
                "group",
                "failed",
                exception.Message,
                exception.ToString()));
        }
    }

    private static ProbeApplication Launch(CaptureOptions options, string dataKey, params string[] arguments)
    {
        CloseExistingAppProcesses(options.AppPath);
        string dataRoot = Path.Combine(_runDataRoot, dataKey);
        Directory.CreateDirectory(dataRoot);
        ProcessStartInfo startInfo = new(options.AppPath)
        {
            WorkingDirectory = Path.GetDirectoryName(options.AppPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false
        };
        startInfo.Environment["JITHUB_AUTOMATION_DATA_ROOT"] = dataRoot;
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
            if (argument.StartsWith("--page=", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment["JITHUB_PREVIEW_PAGE"] = argument[7..];
            }
            else if (argument.StartsWith("--scenario=", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment["JITHUB_PREVIEW_SCENARIO"] = argument[11..];
            }
            else if (argument.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment["JITHUB_PREVIEW_THEME"] = argument[8..];
            }
            else if (argument.StartsWith("--repo=", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment["JITHUB_PREVIEW_REPOSITORY"] = argument[7..];
            }
        }

        Application application = Application.Launch(startInfo);
        UIA3Automation automation = new();
        try
        {
            Window window = Retry.WhileNull(
                () => application.GetMainWindow(automation),
                TimeSpan.FromSeconds(18),
                TimeSpan.FromMilliseconds(100),
                ignoreException: true).Result
                ?? throw new InvalidOperationException($"JitHub did not expose a main window for {dataKey}.");
            window.SetForeground();
            return new ProbeApplication(application, automation, window);
        }
        catch
        {
            automation.Dispose();
            application.Dispose();
            throw;
        }
    }

    private static AutomationElement? Find(Window window, UIA3Automation automation, string automationId)
    {
        AutomationElement? element = window.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (element is not null)
        {
            return element;
        }

        int processId = window.Properties.ProcessId.ValueOrDefault;
        return automation.GetDesktop()
            .FindAllDescendants(cf => cf.ByAutomationId(automationId))
            .FirstOrDefault(candidate => candidate.Properties.ProcessId.ValueOrDefault == processId);
    }

    private static AutomationElement? FindVisible(Window window, UIA3Automation automation, string automationId)
    {
        AutomationElement? candidate = Find(window, automation, automationId);
        return IsVisible(candidate) ? candidate : null;
    }

    private static AutomationElement? FindByName(Window window, UIA3Automation automation, string name)
    {
        AutomationElement? element = window.FindAllDescendants()
            .FirstOrDefault(candidate => IsVisible(candidate) && string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (element is not null)
        {
            return element;
        }

        int processId = window.Properties.ProcessId.ValueOrDefault;
        return automation.GetDesktop().FindAllDescendants()
            .FirstOrDefault(candidate =>
                candidate.Properties.ProcessId.ValueOrDefault == processId &&
                IsVisible(candidate) &&
                string.Equals(candidate.Name, name, StringComparison.Ordinal));
    }

    private static AutomationElement WaitFor(string description, Func<AutomationElement?> find)
    {
        AutomationElement? result = Retry.WhileNull(
            find,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromMilliseconds(100),
            ignoreException: true).Result;
        return result ?? throw new InvalidOperationException($"Timed out waiting for {description}.");
    }

    private static AutomationElement WaitForVisible(string description, Func<AutomationElement?> find) =>
        WaitFor(description, () =>
        {
            AutomationElement? candidate = find();
            return IsVisible(candidate) ? candidate : null;
        });

    private static void WaitUntil(string description, Func<bool> predicate)
    {
        bool result = Retry.WhileFalse(
            predicate,
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(100),
            ignoreException: true).Result;
        Assert(result, $"Timed out waiting for {description}.");
    }

    private static void Resize(Window window, CompactViewport viewport)
    {
        Assert(window.Patterns.Transform.IsSupported, "JitHub main window does not support UIA resize.");
        window.Patterns.Transform.Pattern.Resize(viewport.Width, viewport.Height);
        window.Move(24, 24);
        window.SetForeground();
        Thread.Sleep(300);
    }

    private static void Invoke(AutomationElement element)
    {
        if (element.Patterns.Invoke.IsSupported)
        {
            element.Patterns.Invoke.Pattern.Invoke();
        }
        else
        {
            element.Click();
        }
    }

    private static void TryInvoke(AutomationElement element)
    {
        try
        {
            Invoke(element);
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or FlaUI.Core.Exceptions.ElementNotEnabledException)
        {
            // A production modal can make its opener non-interactive before UIA dispatches.
            // The single-surface assertion still verifies that no duplicate was presented.
        }
    }

    private static void Toggle(AutomationElement element)
    {
        if (element.Patterns.Toggle.IsSupported)
        {
            element.Patterns.Toggle.Pattern.Toggle();
        }
        else
        {
            Invoke(element);
        }
    }

    private static void Focus(AutomationElement element)
    {
        element.FocusNative();
        WaitUntil("automation focus", () => element.Properties.HasKeyboardFocus.ValueOrDefault);
    }

    private static void AssertDialogVisualContract(
        Window window,
        UIA3Automation automation,
        AutomationElement dialog,
        Rectangle contentBounds,
        string baselinePath,
        string outputDirectory,
        string openedFileName,
        string context)
    {
        _ = automation;
        string openedPath = Capture(window, outputDirectory, openedFileName);
        AssertDialogGeometry(window, contentBounds, context);
        AssertScrim(baselinePath, openedPath, window.BoundingRectangle, contentBounds, context);
        Assert(IsVisible(dialog), $"{context} was not visible after visual capture.");
    }

    private static void AssertDialogGeometry(Window window, Rectangle contentBounds, string context)
    {
        Rectangle owner = window.BoundingRectangle;
        Assert(contentBounds.Width > 0 && contentBounds.Height > 0, $"{context} exposed empty dialog bounds.");
        Assert(IsInside(contentBounds, owner, 2), $"{context} escaped the owner window: dialog={contentBounds}, owner={owner}.");
        double horizontalDelta = Math.Abs(CenterX(contentBounds) - CenterX(owner));
        double verticalDelta = Math.Abs(CenterY(contentBounds) - CenterY(owner));
        Assert(horizontalDelta <= Math.Max(52, owner.Width * 0.07), $"{context} was not horizontally centered (delta {horizontalDelta:0.0}; owner={owner}; content={contentBounds}).");
        Assert(verticalDelta <= Math.Max(78, owner.Height * 0.14), $"{context} was not vertically centered (delta {verticalDelta:0.0}; owner={owner}; content={contentBounds}).");
        Assert(contentBounds.Width <= owner.Width - 16, $"{context} did not preserve horizontal compact margins.");
        Assert(contentBounds.Height <= owner.Height - 16, $"{context} did not preserve vertical compact margins.");
        Assert(contentBounds.Width <= 700, $"{context} exceeded the standard dialog max-width envelope ({contentBounds.Width}px).");
    }

    private static void AssertShellOverlay(Window window, AutomationElement overlay, Rectangle contentBounds, string context)
    {
        Rectangle owner = window.BoundingRectangle;
        Rectangle scrim = overlay.BoundingRectangle;
        Assert(IsInside(contentBounds, scrim, 2), $"{context} content escaped its shell scrim.");
        Assert(scrim.Width >= owner.Width - 20, $"{context} shell scrim did not cover the client width (owner={owner}; scrim={scrim}).");
        Assert(scrim.Height >= owner.Height - 48, $"{context} shell scrim did not cover the owner content height (owner={owner}; scrim={scrim}).");
        AutomationElement? background = window.FindFirstDescendant(cf => cf.ByAutomationId("ShellSearchTextBox"));
        Assert(background is null || !background.IsEnabled, $"{context} left the shell search background enabled.");
    }

    private static void AssertFocusTrap(
        UIA3Automation automation,
        AutomationElement focusRoot,
        Rectangle contentBounds,
        string context)
    {
        Rectangle focusEnvelope = contentBounds;
        focusEnvelope.Inflate(36, 36);
        if (!IsFocusWithin(automation, focusRoot, focusEnvelope))
        {
            bool focused = focusRoot.FindAllDescendants()
                .Where(candidate =>
                    IsVisible(candidate) &&
                    candidate.IsEnabled &&
                    candidate.Properties.IsKeyboardFocusable.ValueOrDefault)
                .OrderByDescending(static candidate => IsUsefulDialogFocusTarget(candidate.ControlType))
                .Any(candidate => TryFocusWithin(candidate, focusEnvelope, automation));
            if (!focused)
            {
                Keyboard.Press(VirtualKeyShort.TAB);
            }
        }
        bool receivedFocus = Retry.WhileFalse(
            () => IsFocusWithin(automation, focusRoot, focusEnvelope),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromMilliseconds(100),
            ignoreException: true).Result;
        if (!receivedFocus)
        {
            AutomationElement focused = automation.FocusedElement();
            string candidates = string.Join(
                " | ",
                focusRoot.FindAllDescendants()
                    .Where(candidate => IsVisible(candidate))
                    .Select(DescribeAutomationElement)
                    .Take(24));
            throw new InvalidOperationException(
                $"{context} did not receive focus. Focus remained on " +
                $"{DescribeAutomationElement(focused)}. " +
                $"Visible dialog descendants: {candidates}");
        }
        for (int index = 0; index < 10; index++)
        {
            Keyboard.Press(VirtualKeyShort.TAB);
            Thread.Sleep(50);
            AutomationElement focused = automation.FocusedElement();
            Assert(
                IsFocusWithin(automation, focusRoot, focusEnvelope),
                $"{context} focus escaped after Tab {index + 1} to " +
                $"'{focused.Properties.AutomationId.ValueOrDefault}'/'{focused.Name}' at {focused.BoundingRectangle}.");
        }
    }

    private static bool TryFocusWithin(
        AutomationElement candidate,
        Rectangle focusEnvelope,
        UIA3Automation automation)
    {
        try
        {
            candidate.FocusNative();
            return Retry.WhileFalse(
                () => IsInside(automation.FocusedElement().BoundingRectangle, focusEnvelope, 2) ||
                    HasKeyboardFocus(candidate),
                TimeSpan.FromMilliseconds(500),
                TimeSpan.FromMilliseconds(50),
                ignoreException: true).Result;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or FlaUI.Core.Exceptions.ElementNotEnabledException)
        {
            return false;
        }
    }

    private static bool IsFocusWithin(
        UIA3Automation automation,
        AutomationElement focusRoot,
        Rectangle focusEnvelope)
    {
        try
        {
            if (IsInside(automation.FocusedElement().BoundingRectangle, focusEnvelope, 2))
            {
                return true;
            }

            return HasKeyboardFocus(focusRoot) ||
                focusRoot.FindAllDescendants().Any(HasKeyboardFocus);
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or FlaUI.Core.Exceptions.PropertyNotSupportedException)
        {
            return false;
        }
    }

    private static bool HasKeyboardFocus(AutomationElement element)
    {
        try
        {
            return element.Properties.HasKeyboardFocus.ValueOrDefault;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or FlaUI.Core.Exceptions.PropertyNotSupportedException)
        {
            return false;
        }
    }

    private static bool IsUsefulDialogFocusTarget(FlaUI.Core.Definitions.ControlType controlType) =>
        controlType == FlaUI.Core.Definitions.ControlType.Button ||
        controlType == FlaUI.Core.Definitions.ControlType.Edit ||
        controlType == FlaUI.Core.Definitions.ControlType.ComboBox ||
        controlType == FlaUI.Core.Definitions.ControlType.CheckBox ||
        controlType == FlaUI.Core.Definitions.ControlType.ListItem;

    private static string DescribeAutomationElement(AutomationElement element)
    {
        try
        {
            return $"{element.ControlType}:" +
                $"{element.Properties.AutomationId.ValueOrDefault}/" +
                $"{element.Properties.Name.ValueOrDefault}:" +
                $"enabled={element.Properties.IsEnabled.ValueOrDefault}:" +
                $"focusable={element.Properties.IsKeyboardFocusable.ValueOrDefault}:" +
                $"bounds={element.BoundingRectangle}";
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or FlaUI.Core.Exceptions.PropertyNotSupportedException)
        {
            return $"unavailable:{exception.GetType().Name}";
        }
    }

    private static void AssertNoLightDismiss(
        Window window,
        UIA3Automation automation,
        string dialogAutomationId,
        Rectangle contentBounds,
        string context)
    {
        Point point = FindScrimPoint(window.BoundingRectangle, contentBounds);
        Mouse.Click(point);
        Thread.Sleep(180);
        Assert(
            IsVisible(Find(window, automation, dialogAutomationId)),
            $"{context} light-dismissed when its modal scrim was clicked.");
    }

    private static void CloseWithEscape(
        Window window,
        UIA3Automation automation,
        string dialogAutomationId,
        string context)
    {
        window.SetForeground();
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        WaitUntil($"{context} closes with Escape", () => !IsVisible(Find(window, automation, dialogAutomationId)));
    }

    private static void AssertFocusRestored(UIA3Automation automation, AutomationElement opener, string context)
    {
        string openerId = opener.Properties.AutomationId.ValueOrDefault ?? string.Empty;
        Assert(!string.IsNullOrWhiteSpace(openerId), $"{context} opener did not expose an AutomationId.");
        WaitUntil(
            $"{context} restores opener focus",
            () => string.Equals(
                automation.FocusedElement().Properties.AutomationId.ValueOrDefault,
                openerId,
                StringComparison.Ordinal));
    }

    private static void AssertSingleVisible(
        Window window,
        UIA3Automation automation,
        string automationId,
        string context)
    {
        int processId = window.Properties.ProcessId.ValueOrDefault;
        int visibleCount = automation.GetDesktop()
            .FindAllDescendants(cf => cf.ByAutomationId(automationId))
            .Where(candidate => candidate.Properties.ProcessId.ValueOrDefault == processId && IsVisible(candidate))
            .Select(candidate => candidate.BoundingRectangle)
            .Distinct()
            .Count();
        Assert(visibleCount == 1, $"{context} produced {visibleCount} visible dialog surfaces.");
    }

    private static AutomationElement FindDialogButton(
        Window window,
        UIA3Automation automation,
        AutomationElement dialog,
        string name)
    {
        AutomationElement? result = dialog.FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
            .FirstOrDefault(candidate => IsVisible(candidate) && string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (result is not null)
        {
            return result;
        }

        Rectangle envelope = GetDialogContentEnvelope(dialog);
        int processId = window.Properties.ProcessId.ValueOrDefault;
        result = automation.GetDesktop()
            .FindAllDescendants(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Button))
            .FirstOrDefault(candidate =>
                candidate.Properties.ProcessId.ValueOrDefault == processId &&
                IsVisible(candidate) &&
                string.Equals(candidate.Name, name, StringComparison.Ordinal) &&
                IsInside(candidate.BoundingRectangle, envelope, 36));
        return result ?? throw new InvalidOperationException($"Could not find '{name}' in dialog '{dialog.Name}'.");
    }

    private static Rectangle GetDialogContentEnvelope(AutomationElement dialog)
    {
        Rectangle[] bounds = dialog.FindAllDescendants()
            .Where(candidate => IsVisible(candidate) && IsDialogContentControl(candidate))
            .Select(candidate => candidate.BoundingRectangle)
            .Where(static rectangle => rectangle.Width > 0 && rectangle.Height > 0)
            .ToArray();
        Assert(bounds.Length > 0, $"Dialog '{dialog.Name}' exposed no content bounds.");
        return Rectangle.FromLTRB(
            bounds.Min(static rectangle => rectangle.Left) - 24,
            bounds.Min(static rectangle => rectangle.Top) - 24,
            bounds.Max(static rectangle => rectangle.Right) + 24,
            bounds.Max(static rectangle => rectangle.Bottom) + 24);
    }

    private static bool IsDialogContentControl(AutomationElement element)
    {
        FlaUI.Core.Definitions.ControlType type = element.ControlType;
        return type == FlaUI.Core.Definitions.ControlType.Button ||
            type == FlaUI.Core.Definitions.ControlType.Text ||
            type == FlaUI.Core.Definitions.ControlType.CheckBox ||
            type == FlaUI.Core.Definitions.ControlType.Edit ||
            type == FlaUI.Core.Definitions.ControlType.ComboBox ||
            type == FlaUI.Core.Definitions.ControlType.ListItem;
    }

    private static void AssertScrim(
        string baselinePath,
        string openedPath,
        Rectangle windowBounds,
        Rectangle dialogBounds,
        string context)
    {
        using Bitmap baseline = new(baselinePath);
        using Bitmap opened = new(openedPath);
        Assert(baseline.Size == opened.Size, $"{context} changed capture size while opening.");
        int left = dialogBounds.Left - windowBounds.Left;
        int top = dialogBounds.Top - windowBounds.Top;
        int right = dialogBounds.Right - windowBounds.Left;
        int bottom = dialogBounds.Bottom - windowBounds.Top;
        double beforeLuminance = 0;
        double afterLuminance = 0;
        int samples = 0;
        for (int y = 12; y < baseline.Height - 12; y += 12)
        {
            for (int x = 12; x < baseline.Width - 12; x += 12)
            {
                if (x >= left - 12 && x <= right + 12 && y >= top - 12 && y <= bottom + 12)
                {
                    continue;
                }

                Color before = baseline.GetPixel(x, y);
                Color after = opened.GetPixel(x, y);
                beforeLuminance += Luminance(before);
                afterLuminance += Luminance(after);
                samples++;
            }
        }

        Assert(samples > 20, $"{context} left too little background area to verify its scrim.");
        double darkening = (beforeLuminance - afterLuminance) / samples;
        Assert(darkening >= 1.0, $"{context} scrim did not visibly darken the background (average {darkening:0.0}).");
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

    private static void AssertDisabled(Window window, UIA3Automation automation, string automationId)
    {
        AutomationElement action = WaitFor(automationId, () => Find(window, automation, automationId));
        Assert(!action.IsEnabled, $"Public preview unexpectedly enabled production write trigger '{automationId}'.");
    }

    private static string Capture(Window window, string outputDirectory, string fileName)
    {
        window.SetForeground();
        Thread.Sleep(100);
        Rectangle bounds = window.BoundingRectangle;
        Assert(bounds.Width > 0 && bounds.Height > 0, "Cannot capture an empty JitHub window.");
        Directory.CreateDirectory(outputDirectory);
        string path = Path.Combine(outputDirectory, fileName);
        using Bitmap bitmap = new(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        }
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }

    private static void Pass(string surface, CompactViewport viewport, string detail) =>
        Results.Add(new DialogMatrixResult(surface, viewport.Token, "passed", detail, string.Empty));

    private static void WriteReport(string outputDirectory)
    {
        string path = Path.Combine(outputDirectory, "compact-dialog-matrix-results.json");
        var report = new
        {
            schemaVersion = 1,
            probe = ProbeName,
            generatedAt = DateTimeOffset.UtcNow,
            compactViewports = CompactViewports.Select(static viewport => viewport.Token).ToArray(),
            summary = new
            {
                passed = Results.Count(static result => result.Status == "passed"),
                blocked = Results.Count(static result => result.Status == "blocked"),
                failed = Results.Count(static result => result.Status == "failed")
            },
            results = Results
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void EnsureVisible(AutomationElement element)
    {
        if (!IsVisible(element) && element.Patterns.ScrollItem.IsSupported)
        {
            element.Patterns.ScrollItem.Pattern.ScrollIntoView();
        }

        WaitUntil($"{element.Name} is visible", () => IsVisible(element));
    }

    private static bool IsVisible(AutomationElement? element)
    {
        if (element is null)
        {
            return false;
        }

        try
        {
            Rectangle bounds = element.BoundingRectangle;
            return !element.Properties.IsOffscreen.ValueOrDefault && bounds.Width > 0 && bounds.Height > 0;
        }
        catch (COMException)
        {
            return false;
        }
    }

    private static bool IsInside(Rectangle inner, Rectangle outer, int tolerance) =>
        inner.Width > 0 && inner.Height > 0 &&
        inner.Left >= outer.Left - tolerance &&
        inner.Top >= outer.Top - tolerance &&
        inner.Right <= outer.Right + tolerance &&
        inner.Bottom <= outer.Bottom + tolerance;

    private static Point FindScrimPoint(Rectangle owner, Rectangle dialog)
    {
        Point[] candidates =
        [
            new(owner.Left + 8, owner.Top + (owner.Height / 2)),
            new(owner.Right - 8, owner.Top + (owner.Height / 2)),
            new(owner.Left + (owner.Width / 2), owner.Bottom - 8)
        ];
        return candidates.First(candidate => !dialog.Contains(candidate));
    }

    private static double CenterX(Rectangle rectangle) => rectangle.Left + (rectangle.Width / 2d);

    private static double CenterY(Rectangle rectangle) => rectangle.Top + (rectangle.Height / 2d);

    private static double Luminance(Color color) =>
        (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);

    private static void CloseExistingAppProcesses(string appPath)
    {
        string processName = Path.GetFileNameWithoutExtension(appPath);
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                _ = process.CloseMainWindow();
                if (!process.WaitForExit(2000))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed class ProbeApplication : IDisposable
    {
        public ProbeApplication(Application application, UIA3Automation automation, Window window)
        {
            Application = application;
            Automation = automation;
            Window = window;
        }

        public Application Application { get; }

        public UIA3Automation Automation { get; }

        public Window Window { get; }

        public void Dispose()
        {
            try
            {
                if (!Application.HasExited)
                {
                    Window.Close();
                    Retry.WhileFalse(
                        () => Application.HasExited,
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromMilliseconds(100),
                        ignoreException: true);
                }
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
            }
            finally
            {
                Automation.Dispose();
                Application.Dispose();
            }
        }
    }

    private readonly record struct CompactViewport(int Width, int Height)
    {
        public string Token => $"{Width}x{Height}";
    }

    private sealed record LiveDialogContract(
        string Surface,
        string DialogAutomationId,
        Func<Window, UIA3Automation, AutomationElement> Open,
        DialogValidationContract? Validation);

    private sealed record DialogValidationContract(
        string FieldAutomationId,
        string PrimaryButtonName,
        string ErrorAutomationId);

    private sealed record DialogMatrixResult(
        string Surface,
        string Viewport,
        string Status,
        string Detail,
        string Evidence);
}
