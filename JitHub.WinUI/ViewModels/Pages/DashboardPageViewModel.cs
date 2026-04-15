using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models;
using JitHub.Models.GitHub;
using JitHub.Services;
using Microsoft.UI.Xaml.Data;

namespace JitHub.WinUI.ViewModels.Pages;

[Bindable]
public sealed partial class DashboardPageViewModel : ViewModelBase
{
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly FeatureService _featureService;
    private readonly IGitHubClientService _gitHubClientService;

    public DashboardPageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _featureService = GetService<FeatureService>();
        _gitHubClientService = GetService<IGitHubClientService>();

        UserStatusText = GetString("Dashboard.UserStatusUnavailable", "GitHub profile details are not available yet.");
    }

    public ObservableCollection<GitHubRepository> RecentRepositories { get; } = [];

    public ObservableCollection<GitHubActivityEvent> RecentActivity { get; } = [];

    public string SignedInTitle => GetString("Dashboard.SignedInTitle", "Signed in");

    public string UnlockProButtonText => GetString("Dashboard.UnlockProButton", "Unlock Pro");

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
        "Track the latest activity GitHub sends to your account.");

    public string RecentActivityEmptyStateTitle => GetString(
        "Dashboard.RecentActivityEmptyStateTitle",
        "No activity available.");

    public string RecentActivityEmptyStateDescription => GetString(
        "Dashboard.RecentActivityEmptyStateDescription",
        "Reload to try again or wait for GitHub activity to arrive.");

    [ObservableProperty]
    public partial string UserStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LicenseStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepositoryStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ActivityStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBuyProVisible { get; set; }

    [ObservableProperty]
    public partial bool IsBuyProEnabled { get; set; } = true;

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
        await RefreshLicenseStatusAsync();
        await RefreshRecentRepositoriesAsync();
        await RefreshActivityAsync();
    }

    public async Task PurchaseProAsync()
    {
        IsBuyProEnabled = false;

        try
        {
            FeaturePurchaseState purchaseState = await _featureService.BuyProFeature();
            await RefreshLicenseStatusAsync();

            LicenseStatusText = purchaseState switch
            {
                FeaturePurchaseState.Success => GetString(
                    "Dashboard.ProPurchaseSuccess",
                    "JitHub Pro is now active for this account."),
                FeaturePurchaseState.AlreadyOwn => GetString(
                    "Dashboard.ProPurchaseAlreadyOwned",
                    "JitHub Pro is already active for this account."),
                FeaturePurchaseState.Cancel => GetString(
                    "Dashboard.ProPurchaseCanceled",
                    "The Pro purchase was canceled."),
                _ => GetString(
                    "Dashboard.ProPurchaseFailed",
                    "JitHub Pro could not be activated.")
            };
        }
        finally
        {
            IsBuyProEnabled = true;
        }
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
            IReadOnlyList<GitHubActivityEvent> events = await _gitHubClientService.GetReceivedEventsAsync(token, user.Login, 20);
            RecentActivity.Clear();
            foreach (GitHubActivityEvent activityEvent in events)
            {
                RecentActivity.Add(activityEvent);
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

    private async Task RefreshLicenseStatusAsync()
    {
        await _featureService.SetLicenseStatus();
        if (_featureService.ProLicense)
        {
            LicenseStatusText = GetString("Dashboard.ProActive", "JitHub Pro is active.");
            IsBuyProVisible = false;
            return;
        }

        LicenseStatusText = GetString("Dashboard.ProInactive", "JitHub Pro is not active yet.");
        IsBuyProVisible = true;
    }

    private string? GetActiveToken()
    {
        long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        return _authService.GetToken(userId);
    }
}
