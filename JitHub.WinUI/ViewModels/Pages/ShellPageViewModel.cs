using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;

namespace JitHub.WinUI.ViewModels.Pages;

public sealed partial class ShellPageViewModel : ViewModelBase
{
    private const string AppRepositoryName = "JitHubV2";
    private const string AppRepositoryOwner = "JitHubApp";
    private int _searchRequestId;
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;
    private bool _isStarted;

    public ShellPageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _gitHubClientService = GetService<IGitHubClientService>();
        NotificationMessage = DefaultNotificationMessage;
    }

    public Uri FeedbackUri { get; } = new("https://github.com/JitHubApp/JitHubV2/issues");

    public string FeedbackTabKey => "feedback";

    public string AppTitle => "JitHub";

    public string DefaultTabTitle => AppTitle;

    public string SearchPlaceholderText => GetString(
        "Shell.SearchPlaceholder",
        "Type to search for repository...");

    public string MoreButtonText => GetString("Shell.MoreButton", "More");

    public string HomeTabHeader => GetString("Shell.HomeTabHeader", "Home");

    public string ProfileTabHeader => GetString("pageTitle_DeveloperProfileView", "Profile");

    public string SettingsTabHeader => GetString("pageTitle_SettingsView", "Settings");

    public string ManageRepositoriesTabHeader => GetString(
        "Shell.ManageRepositoriesMenu",
        "Manage repositories");

    public string FeedbackTabHeader => GetString("Shell.FeedbackTabHeader", "Feedback");

    public string SignOutMenuText => GetString("signOut.Text", "Sign out");

    public bool CanOpenMultipleTabs => true;

    [ObservableProperty]
    public partial bool IsSearchLoading { get; set; }

    [ObservableProperty]
    public partial bool IsNotificationOpen { get; set; }

    [ObservableProperty]
    public partial string NotificationMessage { get; set; } = string.Empty;

    public void Start()
    {
        if (_isStarted)
        {
            return;
        }
        _isStarted = true;
    }

    public void Stop()
    {
        if (!_isStarted)
        {
            return;
        }
        _isStarted = false;
    }

    public void SignOut()
    {
        _authService.SignOut();
    }

    public void CancelSearch()
    {
        _searchRequestId++;
        IsSearchLoading = false;
    }

    public async Task<IReadOnlyList<GitHubRepository>?> SearchRepositoriesAsync(string query)
    {
        int requestId = ++_searchRequestId;
        string trimmedQuery = query.Trim();
        if (string.IsNullOrWhiteSpace(trimmedQuery))
        {
            IsSearchLoading = false;
            return Array.Empty<GitHubRepository>();
        }

        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            _authService.SignOut();
            return null;
        }

        IsSearchLoading = true;

        try
        {
            await Task.Delay(400);
            if (requestId != _searchRequestId)
            {
                return null;
            }

            return await _gitHubClientService.SearchRepositoriesAsync(token, trimmedQuery, 8);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
            return null;
        }
        catch (GitHubApiException ex)
        {
            ShowNotification(ex.Message);
            return Array.Empty<GitHubRepository>();
        }
        catch (HttpRequestException)
        {
            ShowNotification(GetString(
                "Shell.SearchNetworkError",
                "JitHub could not reach GitHub to update repository suggestions."));
            return Array.Empty<GitHubRepository>();
        }
        finally
        {
            if (requestId == _searchRequestId)
            {
                IsSearchLoading = false;
            }
        }
    }

    public string FormatSearchTabHeader(string query)
    {
        return FormatString("Shell.SearchTabHeaderFormat", "Search: {0}", query);
    }

    public async Task<RepoDetailPageArgs?> GetFeedbackNavigationArgsAsync()
    {
        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            GitHubRepository repository =
                await _gitHubClientService.GetRepositoryAsync(token, AppRepositoryOwner, AppRepositoryName);
            return new RepoDetailPageArgs(
                RepoPageType.IssuePage,
                new IssueNavArg(repository, 0),
                repository);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
            return null;
        }
        catch (GitHubApiException)
        {
            return null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    public void ShowNotification(string? message)
    {
        NotificationMessage = string.IsNullOrWhiteSpace(message)
            ? DefaultNotificationMessage
            : message;
        IsNotificationOpen = true;
    }

    private string DefaultNotificationMessage => GetString(
        "Shell.DefaultNotification",
        "JitHub has an update.");

    private string? GetActiveToken()
    {
        long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        return _authService.GetToken(userId);
    }
}
