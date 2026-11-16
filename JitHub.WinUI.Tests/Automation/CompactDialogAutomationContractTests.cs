using System.Collections.Concurrent;
using Xunit;

namespace JitHub.WinUI.Tests.Automation;

public sealed class CompactDialogAutomationContractTests
{
    [Fact]
    public void MatrixUsesRealPreviewRoutesAndEveryCompactViewport()
    {
        string source = LoadMatrixSource();
        string catalog = Load("JitHub.WinUI.Automation", "Probes", "ProbeCatalog.cs");

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
            "new Dictionary<string, Action<CaptureOptions>>(StringComparer.OrdinalIgnoreCase)",
            catalog,
            StringComparison.Ordinal);
        Assert.Contains("{ CompactDialogMatrixProbe.ProbeName, CompactDialogMatrixProbe.Run },", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void MatrixInventoriesEveryAuditedDialogSurface()
    {
        string source = LoadMatrixSource();
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
            "RepoPullRequestsCreateDialog",
            "RepoPullRequestsEditDialog",
            "RepoPullRequestsMetadataDialog",
            "RepoPullRequestsSubmitReviewDialog",
            "RepoPullRequestsMergeDialog"
        ];

        Assert.All(requiredAutomationIds, id => Assert.Contains($"\"{id}\"", source, StringComparison.Ordinal));
    }

    [Fact]
    public void MatrixAssertsVisualKeyboardDismissalAndDedupeContracts()
    {
        string source = LoadMatrixSource();
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
        Assert.DoesNotContain("RepoIssuesReactionDialog", issues, StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestsCreateDialog", pulls, StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestsMetadataDialog", pulls, StringComparison.Ordinal);
        Assert.DoesNotContain("RepoPullRequestsReactionDialog", pulls, StringComparison.Ordinal);
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

    /// <summary>
    /// The compact dialog matrix probe is a family of classes in one folder of
    /// the automation project; its contracts hold across all of them.
    /// </summary>
    private static string LoadMatrixSource() =>
        string.Concat(Directory
            .EnumerateFiles(Path.Combine(FindRepositoryRoot(), "JitHub.WinUI.Automation", "Probes", "Dialogs"), "*.cs")
            .Order(StringComparer.Ordinal)
            .Select(File.ReadAllText));

    private static string Load(params string[] path) =>
        File.ReadAllText(Path.Combine(new[] { FindRepositoryRoot() }.Concat(path).ToArray()));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
