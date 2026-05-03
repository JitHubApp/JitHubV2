using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models;
using JitHub.Models.Activities;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Activities;
using JitHub.WinUI.Views.Pages;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.ViewModels.Pages;

[Bindable]
public sealed partial class DashboardPageViewModel : ViewModelBase
{
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;
    private readonly NavigationService _navigationService;

    public DashboardPageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _gitHubClientService = GetService<IGitHubClientService>();
        _navigationService = GetService<NavigationService>();
        NavigateActivityTargetCommand = new RelayCommand<ActivityNavigationTarget>(NavigateActivityTarget);

        UserStatusText = GetString("Dashboard.UserStatusUnavailable", "GitHub profile details are not available yet.");
    }

    public ObservableCollection<GitHubRepository> RecentRepositories { get; } = [];

    public ObservableCollection<ActivityCardViewModel> RecentActivity { get; } = [];

    public RelayCommand<ActivityNavigationTarget> NavigateActivityTargetCommand { get; }

    public string SignedInTitle => GetString("Dashboard.SignedInTitle", "Signed in");

    public string RefreshButtonText => GetString("refreshAppBarButton.Label", "Refresh");

    public string SignOutButtonText => GetString("signOut.Text", "Sign out");

    public string RecentRepositoriesTitle => GetString("Dashboard.RecentRepositoriesTitle", "Recent repositories");

    public string RecentRepositoriesDescription => GetString(
        "Dashboard.RecentRepositoriesDescription",
        "Open code directly from the latest repositories on your account.");

    public string RecentRepositoriesEmptyStateTitle => GetString(
        "Dashboard.RecentRepositoriesEmptyStateTitle",
        "No repositories available.");

    public string RecentRepositoriesEmptyStateDescription => GetString(
        "Dashboard.RecentRepositoriesEmptyStateDescription",
        "Create a repository on GitHub or reload to try again.");

    public string RecentActivityTitle => GetString("Dashboard.RecentActivityTitle", "Recent activity");

    public string RecentActivityDescription => GetString(
        "Dashboard.RecentActivityDescription",
        "Track your latest GitHub activity and events from repositories you follow.");

    public string RecentActivityEmptyStateTitle => GetString(
        "Dashboard.RecentActivityEmptyStateTitle",
        "No activity available.");

    public string RecentActivityEmptyStateDescription => GetString(
        "Dashboard.RecentActivityEmptyStateDescription",
        "Reload to try again or wait for GitHub activity to arrive.");

    [ObservableProperty]
    public partial string UserStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepositoryStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ActivityStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsRepositoryLoading { get; set; }

    [ObservableProperty]
    public partial bool AreRepositoriesVisible { get; set; }

    [ObservableProperty]
    public partial bool IsRepositoriesEmptyStateVisible { get; set; }

    [ObservableProperty]
    public partial bool IsActivityLoading { get; set; }

    [ObservableProperty]
    public partial bool AreActivityItemsVisible { get; set; }

    [ObservableProperty]
    public partial bool IsActivityEmptyStateVisible { get; set; }

    public async Task RefreshDashboardAsync()
    {
        await RefreshUserStatusAsync();
        await RefreshRecentRepositoriesAsync();
        await RefreshActivityAsync();
    }

    public void SignOut()
    {
        _authService.SignOut();
    }

    public async Task RefreshRecentRepositoriesAsync()
    {
        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            UserStatusText = GetString(
                "Common.AuthUnavailable",
                "GitHub authentication is no longer available. Please sign in again.");
            _authService.SignOut();
            return;
        }

        IsRepositoryLoading = true;
        AreRepositoriesVisible = false;
        IsRepositoriesEmptyStateVisible = false;
        RepositoryStatusText = GetString("Dashboard.LoadingRepositories", "Loading recent repositories...");

        try
        {
            IReadOnlyList<GitHubRepository> repositories = await _gitHubClientService.GetRepositoriesForCurrentUserAsync(token, 20);
            RecentRepositories.Clear();
            foreach (GitHubRepository repository in repositories)
            {
                RecentRepositories.Add(repository);
            }

            AreRepositoriesVisible = RecentRepositories.Count > 0;
            IsRepositoriesEmptyStateVisible = RecentRepositories.Count == 0;
            RepositoryStatusText = RecentRepositories.Count == 0
                ? GetString("Dashboard.NoRepositoriesStatus", "No repositories are available for this account.")
                : FormatString("Dashboard.RepositoriesCountStatus", "Showing {0} recent repositories.", RecentRepositories.Count);
        }
        catch (GitHubAuthenticationException)
        {
            UserStatusText = GetString(
                "Common.AuthInvalid",
                "GitHub authentication is no longer valid. Please sign in again.");
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            AreRepositoriesVisible = false;
            IsRepositoriesEmptyStateVisible = true;
            RepositoryStatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            AreRepositoriesVisible = false;
            IsRepositoriesEmptyStateVisible = true;
            RepositoryStatusText = GetString(
                "Dashboard.RepositoriesNetworkError",
                "JitHub could not reach GitHub to load your repositories.");
        }
        finally
        {
            IsRepositoryLoading = false;
        }
    }

    public async Task RefreshActivityAsync()
    {
        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            ActivityStatusText = GetString(
                "Common.AuthUnavailable",
                "GitHub authentication is no longer available. Please sign in again.");
            _authService.SignOut();
            return;
        }

        GitHubUser? user = _authService.AuthenticatedUser ?? await _authService.RefreshAuthenticatedUserAsync();
        if (user is null || string.IsNullOrWhiteSpace(user.Login))
        {
            ActivityStatusText = GetString("Dashboard.UserStatusUnavailable", "GitHub profile details are not available yet.");
            AreActivityItemsVisible = false;
            IsActivityEmptyStateVisible = false;
            return;
        }

        IsActivityLoading = true;
        AreActivityItemsVisible = false;
        IsActivityEmptyStateVisible = false;
        ActivityStatusText = GetString("Dashboard.LoadingActivity", "Loading recent activity...");

        try
        {
            Task<IReadOnlyList<GitHubActivityEvent>> userEventsTask =
                _gitHubClientService.GetUserEventsAsync(token, user.Login, 20);
            Task<IReadOnlyList<GitHubActivityEvent>> receivedEventsTask =
                _gitHubClientService.GetReceivedEventsAsync(token, user.Login, 20);

            await Task.WhenAll(userEventsTask, receivedEventsTask);

            IReadOnlyList<GitHubActivityEvent> userEvents = await userEventsTask;
            IReadOnlyList<GitHubActivityEvent> receivedEvents = await receivedEventsTask;

            IReadOnlyList<GitHubActivityEvent> events = userEvents
                .Concat(receivedEvents)
                .GroupBy(static activityEvent => activityEvent.Id)
                .Select(static group => group.First())
                .OrderByDescending(static activityEvent => activityEvent.CreatedAt ?? DateTimeOffset.MinValue)
                .Take(20)
                .ToList();

            await EnrichActivityEventsAsync(token, events);

            RecentActivity.Clear();
            foreach (GitHubActivityEvent activityEvent in events)
            {
                RecentActivity.Add(ActivityCardViewModelFactory.Create(activityEvent, NavigateActivityTargetCommand));
            }

            AreActivityItemsVisible = RecentActivity.Count > 0;
            IsActivityEmptyStateVisible = RecentActivity.Count == 0;
            ActivityStatusText = RecentActivity.Count == 0
                ? GetString("Dashboard.NoActivityStatus", "No recent activity is available for this account.")
                : FormatString("Dashboard.ActivityCountStatus", "Showing {0} recent activity events.", RecentActivity.Count);
        }
        catch (GitHubAuthenticationException)
        {
            ActivityStatusText = GetString(
                "Common.AuthInvalid",
                "GitHub authentication is no longer valid. Please sign in again.");
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            AreActivityItemsVisible = false;
            IsActivityEmptyStateVisible = true;
            ActivityStatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            AreActivityItemsVisible = false;
            IsActivityEmptyStateVisible = true;
            ActivityStatusText = GetString(
                "Dashboard.ActivityNetworkError",
                "JitHub could not reach GitHub to load your recent activity.");
        }
        finally
        {
            IsActivityLoading = false;
        }
    }

    private async Task RefreshUserStatusAsync()
    {
        GitHubUser? user = _authService.AuthenticatedUser ?? await _authService.RefreshAuthenticatedUserAsync();
        if (user is null)
        {
            UserStatusText = GetString("Dashboard.UserStatusUnavailable", "GitHub profile details are not available yet.");
            return;
        }

        UserStatusText = string.IsNullOrWhiteSpace(user.Name)
            ? FormatString("Dashboard.UserStatusLoginOnly", "Signed in as @{0}.", user.Login)
            : FormatString("Dashboard.UserStatusNameAndLogin", "Signed in as {0} (@{1}).", user.Name, user.Login);
    }

    private string? GetActiveToken()
    {
        long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        return _authService.GetToken(userId);
    }

    private async Task EnrichActivityEventsAsync(string token, IReadOnlyList<GitHubActivityEvent> events)
    {
        using var gate = new SemaphoreSlim(4);
        Task[] enrichTasks = events
            .Where(static activityEvent => string.Equals(activityEvent.Type, "PushEvent", StringComparison.Ordinal))
            .Select(activityEvent => EnrichPushEventAsync(token, activityEvent, gate))
            .ToArray();

        await Task.WhenAll(enrichTasks);
    }

    private async Task EnrichPushEventAsync(
        string token,
        GitHubActivityEvent activityEvent,
        SemaphoreSlim gate)
    {
        if (activityEvent.TypedPayload is not PushEventPayload payload
            || (payload.Commits?.Length ?? 0) > 0
            || !IsComparableSha(payload.Before)
            || !IsComparableSha(payload.Head)
            || !TrySplitRepository(activityEvent.Repo.Name, out string owner, out string name))
        {
            return;
        }

        await gate.WaitAsync();
        try
        {
            GitHubCompareResult compare = await _gitHubClientService.CompareCommitsAsync(
                token,
                owner,
                name,
                payload.Before!,
                payload.Head!);

            GitHubActivityPushCommit[] commits = compare.Commits
                .Select(static commit => new GitHubActivityPushCommit
                {
                    Sha = commit.Sha,
                    Message = commit.Commit.Message,
                    Url = commit.HtmlUrl,
                    Distinct = true,
                    Author = new GitHubActivityCommitAuthor
                    {
                        Name = commit.Commit.Author.Name,
                        Email = commit.Commit.Author.Email
                    }
                })
                .ToArray();

            activityEvent.EnrichedPayload = new PushEventPayload
            {
                RepositoryId = payload.RepositoryId,
                PushId = payload.PushId,
                Ref = payload.Ref,
                Head = payload.Head,
                Before = payload.Before,
                Size = payload.Size,
                DistinctSize = payload.DistinctSize,
                EnrichedCommitCount = compare.TotalCommits > 0 ? compare.TotalCommits : commits.Length,
                Commits = commits
            };
        }
        catch (GitHubApiException)
        {
            // Push payloads are still useful without compare details; do not fail the whole feed.
        }
        catch (HttpRequestException)
        {
            // Keep the activity card visible when commit enrichment is temporarily unavailable.
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool TrySplitRepository(string? repositoryFullName, out string owner, out string name)
    {
        owner = string.Empty;
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(repositoryFullName))
        {
            return false;
        }

        string[] parts = repositoryFullName.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            return false;
        }

        owner = parts[0];
        name = parts[1];
        return true;
    }

    private static bool IsComparableSha(string? sha)
    {
        return !string.IsNullOrWhiteSpace(sha)
            && sha.Length >= 7
            && sha.Any(static c => c != '0');
    }

    private void NavigateActivityTarget(ActivityNavigationTarget? target)
    {
        if (target is null
            || target.Kind == ActivityNavigationTargetKind.UnsupportedTodo
            || string.IsNullOrWhiteSpace(target.RepositoryFullName))
        {
            return;
        }

        GitHubRepository repository = CreateRepository(target);
        PageNavArg pageArg = target.Kind switch
        {
            ActivityNavigationTargetKind.Issue => new IssueNavArg(repository, target.Number),
            ActivityNavigationTargetKind.PullRequest => new PullRequestPageNavArg(repository, target.Number),
            ActivityNavigationTargetKind.Commit => CommitPageNavArg.CreateWithGitRef(repository, target.Sha),
            _ => CodeViewerNavArg.CreateWithBranch(repository, target.Branch)
        };

        RepoPageType page = target.Kind switch
        {
            ActivityNavigationTargetKind.Issue => RepoPageType.IssuePage,
            ActivityNavigationTargetKind.PullRequest => RepoPageType.PullRequestPage,
            ActivityNavigationTargetKind.Commit => RepoPageType.CommitPage,
            _ => RepoPageType.CodePage
        };

        _navigationService.NavigateTo(
            repository.FullName,
            typeof(RepoDetailPage),
            new RepoDetailPageArgs(page, pageArg, repository.FullName));
    }

    private static GitHubRepository CreateRepository(ActivityNavigationTarget target)
    {
        if (target.Repository is { FullName: { Length: > 0 } } repository)
        {
            return repository;
        }

        string[] parts = target.RepositoryFullName.Split('/', 2, StringSplitOptions.TrimEntries);
        string owner = parts.Length == 2 ? parts[0] : string.Empty;
        string name = parts.Length == 2 ? parts[1] : target.RepositoryFullName;

        return new GitHubRepository
        {
            Name = name,
            FullName = target.RepositoryFullName,
            HtmlUrl = $"https://github.com/{target.RepositoryFullName}",
            Owner = new GitHubRepositoryOwner
            {
                Login = owner,
                HtmlUrl = string.IsNullOrWhiteSpace(owner) ? null : $"https://github.com/{owner}"
            }
        };
    }
}
