using System.Text.RegularExpressions;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class ContentDialogPresentationContractTests
{
    [Fact]
    public void ProductContentDialogs_UseSingleFlightPresenter()
    {
        string viewsRoot = FindRepositoryDirectory("JitHub.WinUI", "Views");
        string presenterPath = Path.Combine(viewsRoot, "Dialogs", "AppContentDialogPresenter.cs");
        string[] directPresentations = Directory
            .EnumerateFiles(viewsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, presenterPath, StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => Regex.Matches(
                    File.ReadAllText(path),
                    @"\b(?:dialog|confirmation|empty)\.ShowAsync\s*\(\s*\)",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                .Select(match => $"{Path.GetRelativePath(viewsRoot, path)}: {match.Value}"))
            .ToArray();

        Assert.Empty(directPresentations);
    }

    [Fact]
    public void Presenter_EnforcesGateStyleAndFocusRestoration()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryDirectory("JitHub.WinUI", "Views"),
            "Dialogs",
            "AppContentDialogPresenter.cs"));

        Assert.Contains("GetService<DialogPresentationCoordinator>()", source, StringComparison.Ordinal);
        Assert.Contains("TryBegin(DialogPresentationKind.NativeContentDialog", source, StringComparison.Ordinal);
        Assert.Contains("dialog.presentation.failed", source, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException)", source, StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("catch (OperationCanceledException)", StringComparison.Ordinal) <
            source.IndexOf("catch (Exception ex)", StringComparison.Ordinal));
        Assert.Contains("App.LogHandledException", source, StringComparison.Ordinal);
        Assert.Contains("return ContentDialogResult.None", source, StringComparison.Ordinal);
        Assert.Contains("coordinator.Complete(lease);", source, StringComparison.Ordinal);
        Assert.Contains("AppDialogStyleCatalog.Apply(dialog);", source, StringComparison.Ordinal);
        Assert.Contains("DialogFocusRestorationGate.Shared", source, StringComparison.Ordinal);
        Assert.Contains("focusRestorationGate.CanRestore(focusGeneration", source, StringComparison.Ordinal);
        Assert.Contains("FocusManager.FindFirstFocusableElement(root)", source, StringComparison.Ordinal);
        Assert.Contains("presentationRoot.Changed += rootChangedHandler;", source, StringComparison.Ordinal);
        Assert.Contains("presentationRoot.Changed -= rootChangedHandler;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileEditDialog_HasExactlyOneOwnedVerticalScrollRegion()
    {
        string productRoot = FindRepositoryDirectory("JitHub.WinUI");
        string profilePage = File.ReadAllText(Path.Combine(
            productRoot,
            "Views",
            "Pages",
            "ProfilePage.xaml.cs"));
        string catalog = File.ReadAllText(Path.Combine(
            productRoot,
            "Views",
            "Dialogs",
            "AppDialogStyleCatalog.cs"));

        Assert.Contains("AppDialogScrollableContent dialogContent", profilePage, StringComparison.Ordinal);
        Assert.Contains("ProfileEditFieldsScrollViewer", profilePage, StringComparison.Ordinal);
        Assert.Contains("if (content is AppDialogScrollableContent)", catalog, StringComparison.Ordinal);
        Assert.Contains("return content;", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void DialogFailures_AreLocalizedAndDoNotExposeUnhandledExceptionMessages()
    {
        string productRoot = FindRepositoryDirectory("JitHub.WinUI");
        string presenter = File.ReadAllText(Path.Combine(
            productRoot,
            "Views",
            "Dialogs",
            "AppContentDialogPresenter.cs"));
        string signOut = File.ReadAllText(Path.Combine(
            productRoot,
            "Views",
            "Dialogs",
            "AccountSignOutDialogFlow.cs"));
        string profileViewModel = File.ReadAllText(Path.Combine(
            productRoot,
            "ViewModels",
            "Pages",
            "ProfilePageViewModel.cs"));
        string[] productSources = Directory
            .EnumerateFiles(productRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToArray();

        Assert.Contains("Dialogs/Status/Working", presenter, StringComparison.Ordinal);
        Assert.Contains("Dialogs/Error/AutomationName", presenter, StringComparison.Ordinal);
        Assert.Contains("Dialogs/Error/Generic", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowInlineError(errorPresenter, ex.Message)", presenter, StringComparison.Ordinal);
        Assert.DoesNotContain("StatusText = ex.Message", profileViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain(
            productSources,
            source => source.Contains("DialogMutationResult.Failure(ex.Message)", StringComparison.Ordinal) ||
                source.Contains("DialogMutationResult.Failure(exception.Message)", StringComparison.Ordinal));
        Assert.Contains("Dialogs/SignOut/RemovalFailed", signOut, StringComparison.Ordinal);
        Assert.DoesNotContain("failure.Component", signOut, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredAndLegacySubmissions_AreSingleFlightAndFailurePreserving()
    {
        string productRoot = FindRepositoryDirectory("JitHub.WinUI");
        string profilePage = File.ReadAllText(Path.Combine(productRoot, "Views", "Pages", "ProfilePage.xaml.cs"));
        string profileViewModel = File.ReadAllText(Path.Combine(productRoot, "ViewModels", "Pages", "ProfilePageViewModel.cs"));
        string mergeViewModel = File.ReadAllText(Path.Combine(
            productRoot,
            "ViewModels",
            "PullRequestViewModels",
            "MergeFormViewModel.cs"));
        string mergeForm = File.ReadAllText(Path.Combine(
            productRoot,
            "Views",
            "Controls",
            "PullRequest",
            "MergeForm.xaml"));
        string repositoryFormViewModel = File.ReadAllText(Path.Combine(
            productRoot,
            "ViewModels",
            "RepositoryViewModels",
            "RepoFormViewModel.cs"));

        Assert.Contains("ShowForPrimaryActionAsync(", profilePage, StringComparison.Ordinal);
        Assert.Contains("ProfileEditDialogError", profilePage, StringComparison.Ordinal);
        Assert.Contains("public async Task<bool> SaveProfileAsync", profileViewModel, StringComparison.Ordinal);

        Assert.Contains("MergeCommand = new AsyncRelayCommand(MergeAsync);", mergeViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("async void Merge", mergeViewModel, StringComparison.Ordinal);
        Assert.Contains("session.TryBeginMutation()", mergeViewModel, StringComparison.Ordinal);
        Assert.Contains("session.TryClose()", mergeViewModel, StringComparison.Ordinal);
        Assert.Contains("UserFacingError.For(", mergeViewModel, StringComparison.Ordinal);
        Assert.Contains("Command=\"{x:Bind ViewModel.MergeCommand", mergeForm, StringComparison.Ordinal);
        Assert.DoesNotContain("Click=\"{x:Bind ViewModel.Merge", mergeForm, StringComparison.Ordinal);

        Assert.Contains("public IAsyncRelayCommand CreateCommand", repositoryFormViewModel, StringComparison.Ordinal);
        Assert.Contains("() => !string.IsNullOrWhiteSpace(Name)", repositoryFormViewModel, StringComparison.Ordinal);
        Assert.Contains("CreateCommand.NotifyCanExecuteChanged();", repositoryFormViewModel, StringComparison.Ordinal);
        Assert.Contains("session.TryBeginMutation()", repositoryFormViewModel, StringComparison.Ordinal);
        Assert.Contains("session.TryClose()", repositoryFormViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void MutationDialogs_KeepPresentationUntilOperationCompletes()
    {
        string productRoot = FindRepositoryDirectory("JitHub.WinUI");
        string presenter = File.ReadAllText(Path.Combine(productRoot, "Views", "Dialogs", "AppContentDialogPresenter.cs"));
        string issuePage = File.ReadAllText(Path.Combine(productRoot, "Views", "Pages", "RepoIssuePage.xaml.cs"));
        string pullRequestPage = File.ReadAllText(Path.Combine(productRoot, "Views", "Pages", "RepoPullRequestPage.xaml.cs"));
        string starsPage = File.ReadAllText(Path.Combine(productRoot, "Views", "Pages", "StarsPage.xaml.cs"));
        string gistsPage = File.ReadAllText(Path.Combine(productRoot, "Views", "Pages", "GistsPage.xaml.cs"));
        string repoManagePage = File.ReadAllText(Path.Combine(productRoot, "Views", "Pages", "RepoManagePage.xaml.cs"));
        string signOutFlow = File.ReadAllText(Path.Combine(productRoot, "Views", "Dialogs", "AccountSignOutDialogFlow.cs"));

        Assert.Contains("ContentDialogButtonClickDeferral", presenter, StringComparison.Ordinal);
        Assert.Contains("if (submissionGate.IsSubmitting)", presenter, StringComparison.Ordinal);
        Assert.Contains("args.Cancel = true;", presenter, StringComparison.Ordinal);
        Assert.Contains("ShowInlineError(errorPresenter", presenter, StringComparison.Ordinal);

        Assert.True(Regex.Matches(issuePage, "ShowForPrimaryActionAsync", RegexOptions.CultureInvariant).Count >= 4);
        Assert.True(Regex.Matches(pullRequestPage, "ShowForPrimaryActionAsync", RegexOptions.CultureInvariant).Count >= 5);
        Assert.Contains("StarsBulkUnstarDialogError", starsPage, StringComparison.Ordinal);
        Assert.Contains("StarsDeleteCategoryDialogError", starsPage, StringComparison.Ordinal);
        Assert.Contains("GistDeleteDialogError", gistsPage, StringComparison.Ordinal);
        Assert.Contains("RepositoryDeleteConfirmationError", repoManagePage, StringComparison.Ordinal);
        Assert.Contains("SignOutConfirmationDialogError", signOutFlow, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellAndNativeDialogs_ShareOnePresentationCoordinator()
    {
        string productRoot = FindRepositoryDirectory("JitHub.WinUI");
        string modalService = File.ReadAllText(Path.Combine(productRoot, "Services", "ModalService.cs"));
        string presenter = File.ReadAllText(Path.Combine(productRoot, "Views", "Dialogs", "AppContentDialogPresenter.cs"));
        string shellViewModel = File.ReadAllText(Path.Combine(productRoot, "ViewModels", "Pages", "ShellPageViewModel.cs"));

        Assert.Contains("DialogPresentationCoordinator", modalService, StringComparison.Ordinal);
        Assert.Contains("DialogPresentationKind.ShellOverlay", modalService, StringComparison.Ordinal);
        Assert.Contains("DialogPresentationKind.NativeContentDialog", presenter, StringComparison.Ordinal);
        Assert.Contains("expectedSession", modalService, StringComparison.Ordinal);
        Assert.Contains("private ShellNavigationAttempt EvaluateModalForNavigation()", shellViewModel, StringComparison.Ordinal);
        Assert.True(Regex.Matches(
            shellViewModel,
            "EvaluateModalForNavigation\\(\\)",
            RegexOptions.CultureInvariant).Count >= 4);
        Assert.Contains("Finish the current dialog action before navigating.", shellViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void UserFacingFailureProperties_DoNotExposeRawExceptionMessages()
    {
        string productRoot = FindRepositoryDirectory("JitHub.WinUI");
        Regex unsafeAssignment = new(
            @"(?:StatusText|DetailStatusText|ErrorText|ErrorMessage|ReplyErrorMessage|LoadError)\s*=\s*[^;\r\n]*(?:ex|exception|error)\.Message",
            RegexOptions.CultureInvariant);
        Regex unsafePresentation = new(
            @"\bShow[A-Za-z]+\s*\([^;\r\n]*(?:ex|exception|error)\.Message",
            RegexOptions.CultureInvariant);

        string[] violations = Directory
            .EnumerateFiles(productRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .SelectMany(path => File.ReadLines(path)
                .Select((line, index) => (line, index))
                .Where(item => unsafeAssignment.IsMatch(item.line) || unsafePresentation.IsMatch(item.line))
                .Select(item => $"{Path.GetRelativePath(productRoot, path)}:{item.index + 1}: {item.line.Trim()}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void DashboardCustomizer_IsResponsiveAndCleansUpEveryDismissalPath()
    {
        string productRoot = FindRepositoryDirectory("JitHub.WinUI");
        string customizeXaml = File.ReadAllText(Path.Combine(
            productRoot,
            "Views",
            "Controls",
            "App",
            "DashboardWidgetCustomizeDialog.xaml"));
        string dashboardPage = File.ReadAllText(Path.Combine(productRoot, "Views", "Pages", "DashboardPage.xaml.cs"));
        string shellXaml = File.ReadAllText(Path.Combine(productRoot, "Views", "Pages", "ShellPage.xaml"));
        string shellCode = File.ReadAllText(Path.Combine(productRoot, "Views", "Pages", "ShellPage.xaml.cs"));

        Assert.DoesNotMatch("(?m)^\\s*Width=\"720\"", customizeXaml);
        Assert.Contains("MaxWidth=\"720\"", customizeXaml, StringComparison.Ordinal);
        Assert.Contains("<ScrollViewer Grid.Row=\"1\"", customizeXaml, StringComparison.Ordinal);
        Assert.Contains("callback: new RelayCommand(OnCustomizeModalClosed)", dashboardPage, StringComparison.Ordinal);
        Assert.Contains("CloseCustomizeDialog(cancelChanges: true);", dashboardPage, StringComparison.Ordinal);
        Assert.Contains("ViewModel.CancelCustomizeCommand.Execute(null);", dashboardPage, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ModalContent\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ShellModalScrollViewer\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollMode=\"Auto\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalScrollMode=\"Disabled\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("DialogLayoutPolicy.Calculate(viewport.Width, contentHeight)", shellCode, StringComparison.Ordinal);
        Assert.Contains("UpdateModalLayout(e.NewSize);", shellCode, StringComparison.Ordinal);
    }

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
