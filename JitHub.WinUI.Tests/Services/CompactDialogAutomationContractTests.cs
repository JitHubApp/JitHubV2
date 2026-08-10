using System.Collections.Concurrent;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class CompactDialogAutomationContractTests
{
    [Fact]
    public void MatrixUsesRealPreviewRoutesAndEveryCompactViewport()
    {
        string source = Load("JitHub.WinUI.Automation", "CompactDialogMatrixProbe.cs");
        string program = Load("JitHub.WinUI.Automation", "Program.cs");

        Assert.Contains("new(900, 700)", source, StringComparison.Ordinal);
        Assert.Contains("new(760, 650)", source, StringComparison.Ordinal);
        Assert.Contains("new(640, 600)", source, StringComparison.Ordinal);
        Assert.Contains("--page=repositories", source, StringComparison.Ordinal);
        Assert.Contains("--scenario=repository-library", source, StringComparison.Ordinal);
        Assert.Contains("--page=repo-issues", source, StringComparison.Ordinal);
        Assert.Contains("--page=repo-pulls", source, StringComparison.Ordinal);
        Assert.Contains("--page=profile", source, StringComparison.Ordinal);
        Assert.Contains("--page=stars", source, StringComparison.Ordinal);
        Assert.DoesNotContain("design-lab", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mock page", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "string.Equals(options.Probe, CompactDialogMatrixProbe.ProbeName, StringComparison.OrdinalIgnoreCase)",
            program,
            StringComparison.Ordinal);
        Assert.Contains("CompactDialogMatrixProbe.Run(options);", program, StringComparison.Ordinal);
    }

    [Fact]
    public void MatrixInventoriesEveryAuditedDialogSurface()
    {
        string source = Load("JitHub.WinUI.Automation", "CompactDialogMatrixProbe.cs");
        string[] requiredAutomationIds =
        [
            "ShellModalOverlay",
            "ShellModalContent",
            "RepoFormNameTextBox",
            "RepoFormCreateButton",
            "DashboardCustomizeDialog",
            "ProfileEditDialog",
            "RepositoryDeleteConfirmation",
            "StarsCreateCategoryDialog",
            "StarsEditCategoryDialog",
            "StarsDialog_Deletecategory",
            "RepoIssuesCreateDialog",
            "RepoIssuesEditDialog",
            "RepoIssuesMetadataDialog",
            "RepoIssuesReactionDialog",
            "RepoPullRequestsCreateDialog",
            "RepoPullRequestsEditDialog",
            "RepoPullRequestsMetadataDialog",
            "RepoPullRequestsReactionDialog",
            "RepoPullRequestsSubmitReviewDialog",
            "RepoPullRequestsMergeDialog"
        ];

        Assert.All(requiredAutomationIds, id => Assert.Contains($"\"{id}\"", source, StringComparison.Ordinal));
    }

    [Fact]
    public void MatrixAssertsVisualKeyboardDismissalAndDedupeContracts()
    {
        string source = Load("JitHub.WinUI.Automation", "CompactDialogMatrixProbe.cs");
        string[] requiredContracts =
        [
            "AssertDialogVisualContract",
            "AssertDialogGeometry",
            "AssertScrim",
            "AssertFocusTrap",
            "AssertNoLightDismiss",
            "CloseWithEscape",
            "AssertFocusRestored",
            "AssertSingleVisible",
            "repeated-submit",
            "rapid-open"
        ];

        Assert.All(requiredContracts, contract => Assert.Contains(contract, source, StringComparison.Ordinal));
        Assert.Contains("compact-dialog-matrix-results.json", source, StringComparison.Ordinal);
        Assert.Contains("\"blocked\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionDialogBuildersUseTheSharedStyleAndPresenter()
    {
        string issues = Load("JitHub.WinUI", "Views", "Pages", "RepoIssuePage.xaml.cs");
        string pulls = Load("JitHub.WinUI", "Views", "Pages", "RepoPullRequestPage.xaml.cs");
        string profile = Load("JitHub.WinUI", "Views", "Pages", "ProfilePage.xaml.cs");
        string repositories = Load("JitHub.WinUI", "Views", "Pages", "RepoManagePage.xaml.cs");
        string stars = Load("JitHub.WinUI", "Views", "Pages", "StarsPage.xaml.cs");

        foreach (string source in new[] { issues, pulls, profile, repositories, stars })
        {
            Assert.Contains("AppDialogStyleCatalog.Apply(dialog);", source, StringComparison.Ordinal);
            Assert.Contains("AppContentDialogPresenter.Show", source, StringComparison.Ordinal);
        }

        Assert.Contains("RepoIssuesCreateDialog", issues, StringComparison.Ordinal);
        Assert.Contains("RepoIssuesMetadataDialog", issues, StringComparison.Ordinal);
        Assert.Contains("RepoIssuesReactionDialog", issues, StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestsCreateDialog", pulls, StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestsMetadataDialog", pulls, StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestsReactionDialog", pulls, StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestsMergeDialog", pulls, StringComparison.Ordinal);
        Assert.Contains("ProfileEditDialog", profile, StringComparison.Ordinal);
        Assert.Contains("RepositoryDeleteConfirmation", repositories, StringComparison.Ordinal);
        Assert.Contains("StarsCreateCategoryDialog", stars, StringComparison.Ordinal);
        Assert.Contains("StarsEditCategoryDialog", stars, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedPresenterOwnsVisibleBusyValidationAndRetryState()
    {
        string presenter = Load("JitHub.WinUI", "Views", "Dialogs", "AppContentDialogPresenter.cs");

        Assert.Contains("if (submissionGate.IsSubmitting)", presenter, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true;", presenter, StringComparison.Ordinal);
        Assert.Contains("dialog.IsPrimaryButtonEnabled = false;", presenter, StringComparison.Ordinal);
        Assert.Contains("dialog.IsSecondaryButtonEnabled = false;", presenter, StringComparison.Ordinal);
        Assert.Contains("closeButton.IsEnabled = false;", presenter, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetItemStatus(", presenter, StringComparison.Ordinal);
        Assert.Contains("Dialogs/Status/Working", presenter, StringComparison.Ordinal);
        Assert.Contains("ShowInlineError(errorPresenter, result.ErrorMessage);", presenter, StringComparison.Ordinal);
        Assert.Contains("dialog.IsPrimaryButtonEnabled = EvaluateCanSubmit", presenter, StringComparison.Ordinal);
        Assert.Contains("AutomationLiveSetting.Polite", presenter, StringComparison.Ordinal);
    }

    [Fact]
    public void BusySubmissionGateAdmitsOnlyOneRepeatedSubmit()
    {
        DialogSubmissionGate gate = new();
        ConcurrentBag<int> admitted = [];

        Parallel.For(0, 128, index =>
        {
            if (gate.TryBegin())
            {
                admitted.Add(index);
            }
        });

        Assert.Single(admitted);
        Assert.True(gate.IsSubmitting);
        gate.Complete();
        Assert.False(gate.IsSubmitting);
        Assert.True(gate.TryBegin());
    }

    [Fact]
    public void PresentationCoordinatorDeduplicatesRapidCrossHostOpen()
    {
        DialogPresentationCoordinator coordinator = new();
        Assert.True(coordinator.TryBegin(DialogPresentationKind.ShellOverlay, out long shellLease));
        Assert.False(coordinator.TryBegin(DialogPresentationKind.ShellOverlay, out _));
        Assert.False(coordinator.TryBegin(DialogPresentationKind.NativeContentDialog, out _));
        Assert.Equal(DialogPresentationKind.ShellOverlay, coordinator.ActiveKind);

        Assert.True(coordinator.Complete(shellLease));
        Assert.True(coordinator.TryBegin(DialogPresentationKind.NativeContentDialog, out long dialogLease));
        Assert.Equal(DialogPresentationKind.NativeContentDialog, coordinator.ActiveKind);
        Assert.True(coordinator.Complete(dialogLease));
    }

    [Theory]
    [InlineData(900, 700)]
    [InlineData(760, 650)]
    [InlineData(640, 600)]
    public void SharedLayoutPolicyPreservesCompactBounds(double width, double height)
    {
        DialogLayoutMetrics metrics = DialogLayoutPolicy.Calculate(width, height);

        Assert.True(metrics.MinimumWidth <= metrics.MaximumWidth);
        Assert.True(metrics.MaximumWidth + (metrics.OuterMargin * 2) <= width);
        Assert.True(metrics.MaximumHeight + (metrics.OuterMargin * 2) <= height);
        Assert.InRange(metrics.MaximumWidth, 1, 620);
        Assert.InRange(metrics.MaximumHeight, 1, 720);
    }

    private static string Load(params string[] path) =>
        File.ReadAllText(Path.Combine(new[] { FindRepositoryRoot() }.Concat(path).ToArray()));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
