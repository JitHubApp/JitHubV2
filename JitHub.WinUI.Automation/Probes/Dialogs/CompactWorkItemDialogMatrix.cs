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
/// Exercises the issue and pull request preview dialogs, including repeated invalid submission, at each compact viewport.
/// </summary>
internal static class CompactWorkItemDialogMatrix
{
    internal static void RunIssueAndPullRequestPreviewMatrix(CaptureOptions options)
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
            "RepoPullRequestsMetadataButton")
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

    private sealed record LiveDialogContract(
        string Surface,
        string DialogAutomationId,
        Func<Window, UIA3Automation, AutomationElement> Open,
        DialogValidationContract? Validation);

    private sealed record DialogValidationContract(
        string FieldAutomationId,
        string PrimaryButtonName,
        string ErrorAutomationId);
}
