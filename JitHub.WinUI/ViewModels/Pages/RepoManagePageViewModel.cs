using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.GitHub;
using JitHub.Services;

namespace JitHub.WinUI.ViewModels.Pages;

public sealed partial class RepoManagePageViewModel : ViewModelBase
{
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;

    public RepoManagePageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _gitHubClientService = GetService<IGitHubClientService>();
        StatusText = GetString("RepoManage.LoadingRepositories", "Loading repositories...");
        EmptyStateTitle = GetString("RepoManage.EmptyStateTitle", "No repositories are available to manage.");
        EmptyStateDescription = GetString(
            "RepoManage.EmptyStateDescription",
            "This account has no repositories that can be managed from JitHub.");
    }

    public ObservableCollection<GitHubRepositorySelectionItem> Repositories { get; } = [];

    public string DeleteSelectedButtonText => GetString("RepoManage.DeleteSelectedButton", "Delete selected");

    public string ClearSelectionButtonText => GetString("RepoManage.ClearSelectionButton", "Clear selection");

    public string ReloadButtonText => GetString("Common.ReloadButton", "Reload");

    public string DeleteDialogTitle => GetString("RepoManage.DeleteDialogTitle", "Delete repositories");

    public string DeleteDialogConfirmButtonText => GetString("RepoManage.DeleteDialogConfirmButton", "Delete");

    public string DeleteDialogCloseButtonText => GetString("cancel.Content", "Cancel");

    public string DeleteFailureDialogTitle => GetString("RepoManage.DeleteFailureDialogTitle", "Some repositories were not deleted");

    public string CloseButtonText => GetString("Common.CloseButton", "Close");

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AreRepositoriesVisible { get; set; }

    [ObservableProperty]
    public partial bool IsEmptyStateVisible { get; set; }

    [ObservableProperty]
    public partial string EmptyStateTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyStateDescription { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsDeletionProgressVisible { get; set; }

    [ObservableProperty]
    public partial double DeletionProgressValue { get; set; }

    [ObservableProperty]
    public partial double DeletionProgressMaximum { get; set; } = 1;

    [ObservableProperty]
    public partial bool IsInteractionEnabled { get; set; } = true;

    public async Task LoadRepositoriesAsync()
    {
        if (!TryGetActiveToken(out string token))
        {
            return;
        }

        try
        {
            StatusText = GetString("RepoManage.LoadingRepositories", "Loading repositories...");
            Repositories.Clear();
            AreRepositoriesVisible = false;
            IsEmptyStateVisible = false;

            List<GitHubRepository> repositories = [];
            int pageNumber = 1;
            while (true)
            {
                IReadOnlyList<GitHubRepository> page = await _gitHubClientService.GetRepositoriesForCurrentUserAsync(
                    token,
                    100,
                    pageNumber);
                repositories.AddRange(page);

                if (page.Count < 100)
                {
                    break;
                }

                pageNumber++;
            }

            foreach (GitHubRepository repository in repositories
                         .OrderBy(repository => repository.FullName, StringComparer.OrdinalIgnoreCase))
            {
                Repositories.Add(new GitHubRepositorySelectionItem(repository));
            }

            AreRepositoriesVisible = Repositories.Count > 0;
            IsEmptyStateVisible = Repositories.Count == 0;
            StatusText = Repositories.Count == 0
                ? GetString("RepoManage.NoRepositoriesStatus", "No repositories are available to manage.")
                : FormatString("RepoManage.RepositoriesLoadedStatus", "Loaded {0} repositories.", Repositories.Count);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            AreRepositoriesVisible = false;
            IsEmptyStateVisible = true;
            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            AreRepositoriesVisible = false;
            IsEmptyStateVisible = true;
            StatusText = GetString(
                "Dashboard.RepositoriesNetworkError",
                "JitHub could not reach GitHub to load your repositories.");
        }
    }

    public void ClearSelection()
    {
        foreach (GitHubRepositorySelectionItem repository in Repositories)
        {
            repository.Selected = false;
        }

        StatusText = GetString("RepoManage.SelectionCleared", "Selection cleared.");
    }

    public IReadOnlyList<GitHubRepositorySelectionItem> GetSelectedRepositories()
    {
        List<GitHubRepositorySelectionItem> selectedRepositories = [.. Repositories.Where(repository => repository.Selected)];
        if (selectedRepositories.Count == 0)
        {
            StatusText = GetString("RepoManage.SelectRepositoryPrompt", "Select at least one repository to delete.");
        }

        return selectedRepositories;
    }

    public async Task<RepositoryDeletionResult?> DeleteSelectedAsync(IReadOnlyList<GitHubRepositorySelectionItem> selectedRepositories)
    {
        if (selectedRepositories.Count == 0)
        {
            StatusText = GetString("RepoManage.SelectRepositoryPrompt", "Select at least one repository to delete.");
            return RepositoryDeletionResult.Empty;
        }

        if (!TryGetActiveToken(out string token))
        {
            return null;
        }

        try
        {
            IsInteractionEnabled = false;
            IsDeletionProgressVisible = true;
            DeletionProgressValue = 0;
            DeletionProgressMaximum = selectedRepositories.Count;
            StatusText = FormatString("RepoManage.DeletingStatus", "Deleting {0} repositories...", selectedRepositories.Count);

            List<string> failures = [];
            foreach (GitHubRepositorySelectionItem repositoryItem in selectedRepositories)
            {
                try
                {
                    await _gitHubClientService.DeleteRepositoryAsync(
                        token,
                        repositoryItem.Repository.Owner.Login,
                        repositoryItem.Repository.Name);
                    DeletionProgressValue += 1;
                }
                catch (GitHubAuthenticationException)
                {
                    _authService.SignOut();
                    return null;
                }
                catch (GitHubApiException ex)
                {
                    failures.Add($"{repositoryItem.Repository.FullName}: {ex.Message}");
                }
                catch (HttpRequestException)
                {
                    failures.Add($"{repositoryItem.Repository.FullName}: {GetString("RepoManage.DeleteFailureNetworkErrorShort", "network error")}");
                }
            }

            await LoadRepositoriesAsync();

            if (failures.Count == 0)
            {
                StatusText = GetString("RepoManage.DeleteSuccess", "Selected repositories were deleted.");
                return new RepositoryDeletionResult(selectedRepositories.Count, failures);
            }

            StatusText = FormatString("RepoManage.DeleteFailureStatus", "{0} repositories could not be deleted.", failures.Count);
            return new RepositoryDeletionResult(selectedRepositories.Count, failures);
        }
        finally
        {
            IsInteractionEnabled = true;
            IsDeletionProgressVisible = false;
        }
    }

    public string FormatDeleteDialogContent(int selectedRepositoryCount)
    {
        return FormatString(
            "RepoManage.DeleteDialogContent",
            "Delete {0} selected repositories? This cannot be undone.",
            selectedRepositoryCount);
    }

    private bool TryGetActiveToken(out string token)
    {
        token = GetActiveToken() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(token))
        {
            return true;
        }

        _authService.SignOut();
        return false;
    }

    private string? GetActiveToken()
    {
        long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        return _authService.GetToken(userId);
    }
}

public sealed record RepositoryDeletionResult(int AttemptedCount, IReadOnlyList<string> Failures)
{
    public static RepositoryDeletionResult Empty { get; } = new(0, Array.Empty<string>());

    public bool HasFailures => Failures.Count > 0;
}
