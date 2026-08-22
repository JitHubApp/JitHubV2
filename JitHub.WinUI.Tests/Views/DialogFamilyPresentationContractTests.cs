using System.Text.RegularExpressions;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class DialogFamilyPresentationContractTests
{
    [Theory]
    [InlineData("RepoIssuePage.xaml.cs", 3, "RepoIssuesCreateDialog", "RepoIssuesEditDialog", "RepoIssuesMetadataDialog")]
    [InlineData("RepoPullRequestPage.xaml.cs", 5, "RepoPullRequestsCreateDialog", "RepoPullRequestsEditDialog", "RepoPullRequestsMetadataDialog")]
    public void IssueAndPullRequestMutationDialogs_UseSharedSingleFlightPresenter(
        string fileName,
        int minimumPresenterCalls,
        params string[] automationIds)
    {
        string source = ReadPage(fileName);

        Assert.True(
            Regex.Matches(source, "ShowForPrimaryActionAsync", RegexOptions.CultureInvariant).Count >= minimumPresenterCalls);
        Assert.DoesNotContain(".ShowAsync()", source, StringComparison.Ordinal);
        foreach (string automationId in automationIds)
        {
            Assert.Contains(automationId, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EditorDialogsKeepResponsiveDimensionsStableAcrossWriteAndPreview()
    {
        string productRoot = FindRepositoryDirectory("JitHub.WinUI");
        string catalog = File.ReadAllText(Path.Combine(productRoot, "Views", "Dialogs", "AppDialogStyleCatalog.cs"));
        string issuePage = ReadPage("RepoIssuePage.xaml.cs");
        string pullRequestPage = ReadPage("RepoPullRequestPage.xaml.cs");

        Assert.Contains("dialog.MinWidth = metrics.MaximumWidth", catalog, StringComparison.Ordinal);
        Assert.Contains("dialog.MaxWidth = metrics.MaximumWidth", catalog, StringComparison.Ordinal);
        Assert.Contains("dialog.MinHeight = metrics.MaximumHeight", catalog, StringComparison.Ordinal);
        Assert.Contains("dialog.MaxHeight = metrics.MaximumHeight", catalog, StringComparison.Ordinal);
        Assert.Contains("content.Width = contentWidth", catalog, StringComparison.Ordinal);
        Assert.Contains("content.MinWidth = contentWidth", catalog, StringComparison.Ordinal);
        Assert.Contains("content.MaxWidth = contentWidth", catalog, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Auto", catalog, StringComparison.Ordinal);
        Assert.Contains("layoutKind: AppDialogLayoutKind.Editor", issuePage, StringComparison.Ordinal);
        Assert.Contains("layoutKind: AppDialogLayoutKind.Editor", pullRequestPage, StringComparison.Ordinal);
        Assert.Contains("MarkdownForm", pullRequestPage, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellOwnedForms_AreSessionScopedAndScrimDoesNotLightDismiss()
    {
        string productRoot = FindRepositoryDirectory("JitHub.WinUI");
        string shellXaml = File.ReadAllText(Path.Combine(productRoot, "Views", "Pages", "ShellPage.xaml"));
        string shellCode = File.ReadAllText(Path.Combine(productRoot, "Views", "Pages", "ShellPage.xaml.cs"));
        string modalService = File.ReadAllText(Path.Combine(productRoot, "Services", "ModalService.cs"));
        string repositoryForm = File.ReadAllText(Path.Combine(productRoot, "ViewModels", "RepositoryViewModels", "RepoFormViewModel.cs"));
        string mergeForm = File.ReadAllText(Path.Combine(productRoot, "ViewModels", "PullRequestViewModels", "MergeFormViewModel.cs"));

        Assert.Contains("TabFocusNavigation=\"Cycle\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("SmokeFillColorDefaultBrush", shellXaml, StringComparison.Ordinal);
        Assert.Contains("ModalScrim_PointerPressed", shellCode, StringComparison.Ordinal);
        Assert.Contains("the scrim is modal, not light-dismiss", shellCode, StringComparison.Ordinal);
        Assert.Contains("Modal.AddHandler(KeyDownEvent", shellCode, StringComparison.Ordinal);
        Assert.Contains("RestoreFocusAfterModal", shellCode, StringComparison.Ordinal);
        Assert.Contains("_currentSession = null;", modalService, StringComparison.Ordinal);
        Assert.Contains("close.Execute(null);", modalService, StringComparison.Ordinal);

        AssertSingleFlightLegacyForm(repositoryForm, "CreateCommand", "session.TryBeginMutation()", "session.EndMutation()", "session.TryClose()");
        AssertSingleFlightLegacyForm(mergeForm, "MergeCommand", "session.TryBeginMutation()", "session.EndMutation()", "session.TryClose()");
    }

    [Fact]
    public void DashboardCustomizer_OwnsItsModalSessionAndSingleFlightsSave()
    {
        string productRoot = FindRepositoryDirectory("JitHub.WinUI");
        string control = File.ReadAllText(Path.Combine(productRoot, "Views", "Controls", "App", "DashboardWidgetCustomizeDialog.xaml.cs"));
        string xaml = File.ReadAllText(Path.Combine(productRoot, "Views", "Controls", "App", "DashboardWidgetCustomizeDialog.xaml"));

        Assert.Contains("IModalSessionAware", control, StringComparison.Ordinal);
        Assert.Contains("session.TryBeginMutation()", control, StringComparison.Ordinal);
        Assert.Contains("session.EndMutation();", control, StringComparison.Ordinal);
        Assert.Contains("session.TryClose();", control, StringComparison.Ordinal);
        Assert.Contains("Click=\"SaveButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Command=\"{Binding SaveCustomizeCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"720\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ScrollViewer Grid.Row=\"1\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileDeleteAndStarsDialogs_UseNativePresentationAndValidation()
    {
        string profile = ReadPage("ProfilePage.xaml.cs");
        string repositories = ReadPage("RepoManagePage.xaml.cs");
        string stars = ReadPage("StarsPage.xaml.cs");
        string settings = ReadPage("SettingsPage.xaml.cs");

        Assert.Contains("ProfileEditDialogError", profile, StringComparison.Ordinal);
        Assert.Contains("ShowForPrimaryActionAsync", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("MinWidth = 420", profile, StringComparison.Ordinal);

        Assert.Contains("RepositoryDeleteConfirmationError", repositories, StringComparison.Ordinal);
        Assert.Contains("AppDestructiveButtonStyle", repositories, StringComparison.Ordinal);
        Assert.Contains("ShowForPrimaryActionAsync", repositories, StringComparison.Ordinal);

        Assert.Contains("StarsCategoryDialogError", stars, StringComparison.Ordinal);
        Assert.Contains("StarsCategoryPickerDialogError", stars, StringComparison.Ordinal);
        Assert.Contains("StarsDeleteCategoryDialogError", stars, StringComparison.Ordinal);
        Assert.True(Regex.Matches(stars, "ShowForPrimaryActionAsync", RegexOptions.CultureInvariant).Count >= 4);
        Assert.True(Regex.Matches(stars, "AppDestructiveButtonStyle", RegexOptions.CultureInvariant).Count >= 2);
        Assert.True(Regex.Matches(
            stars,
            "DefaultButton = ContentDialogButton.Close",
            RegexOptions.CultureInvariant).Count >= 2);
        Assert.Contains("Func<string, string, Task> mutateAsync", stars, StringComparison.Ordinal);
        Assert.Contains("await mutateAsync(normalizedName, normalizedColor);", stars, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CreateCategoryAsync(name, color)", stars, StringComparison.Ordinal);
        Assert.Contains("ViewModel.UpdateCategoryAsync(category, name, color)", stars, StringComparison.Ordinal);

        Assert.Contains("AppContentDialogPresenter.ShowAsync", settings, StringComparison.Ordinal);
        Assert.Contains("AppDestructiveButtonStyle", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void GistEditor_UsesEditorLayoutAndSharedSubmissionGate()
    {
        string source = ReadPage("GistsPage.xaml.cs");
        string window = File.ReadAllText(Path.Combine(
            FindRepositoryDirectory("JitHub.WinUI"),
            "MainWindow.xaml.cs"));

        Assert.Contains("GistEditorDialogError", source, StringComparison.Ordinal);
        Assert.Contains("ShowForPrimaryActionAsync", source, StringComparison.Ordinal);
        Assert.Contains("canSubmit: () => session.CanSave", source, StringComparison.Ordinal);
        Assert.Contains("layoutKind: AppDialogLayoutKind.Editor", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dialog.PrimaryButtonClick +=", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ContentDialogMinWidth", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DismissActiveDialogAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dialog.Hide();", source, StringComparison.Ordinal);
        Assert.Contains("await DismissActiveContentDialogBeforeCloseAsync();", window, StringComparison.Ordinal);
        Assert.Contains("if (dialog.IsLoaded)", window, StringComparison.Ordinal);
        Assert.Contains("dialog.Hide();", window, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgrammaticDialogFields_UseSharedJitHubStyles()
    {
        string catalog = File.ReadAllText(Path.Combine(
            FindRepositoryDirectory("JitHub.WinUI"),
            "Views",
            "Dialogs",
            "AppDialogStyleCatalog.cs"));

        Assert.Contains("ApplyFieldStyles(dialog.Content as DependencyObject)", catalog, StringComparison.Ordinal);
        Assert.Contains("GetStyle(\"AppTextBoxStyle\")", catalog, StringComparison.Ordinal);
        Assert.Contains("GetStyle(\"AppCompactComboBoxStyle\")", catalog, StringComparison.Ordinal);
    }

    private static void AssertSingleFlightLegacyForm(string source, params string[] requiredFragments)
    {
        foreach (string fragment in requiredFragments)
        {
            Assert.Contains(fragment, source, StringComparison.Ordinal);
        }

        Assert.Contains("finally", source, StringComparison.Ordinal);
    }

    private static string ReadPage(string fileName) => File.ReadAllText(Path.Combine(
        FindRepositoryDirectory("JitHub.WinUI"),
        "Views",
        "Pages",
        fileName));

    private static string FindRepositoryDirectory(params string[] segments)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine([current.FullName, .. segments]);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate {Path.Combine(segments)}.");
    }
}
