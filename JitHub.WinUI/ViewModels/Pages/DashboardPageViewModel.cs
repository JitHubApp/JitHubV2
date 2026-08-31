using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models;
using JitHub.Models.Activities;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Activities;
using JitHub.WinUI.ViewModels.Common;

namespace JitHub.WinUI.ViewModels.Pages;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class DashboardPageViewModel : ViewModelBase
{
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubDashboardQueryService _dashboardQueryService;
    private readonly IDashboardWidgetLayoutService _widgetLayoutService;
    private readonly ITelemetryService _telemetryService;
    private readonly ShellPageViewModel _shellViewModel;
    private readonly NotificationInboxState _notificationInboxState;
    private readonly NotificationOpenWorkflow _notificationOpenWorkflow;
    private bool _initialized;
    private bool _starProjectionRefreshPending;
    private DashboardWidgetLayout _currentLayout;
    private int _repositoryPreviewLimit = 4;

    public DashboardPageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _dashboardQueryService = GetService<IGitHubDashboardQueryService>();
        _widgetLayoutService = GetService<IDashboardWidgetLayoutService>();
        _telemetryService = SafeTelemetryService.Wrap(GetService<ITelemetryService>());
        _shellViewModel = GetService<ShellPageViewModel>();
        _notificationInboxState = GetService<NotificationInboxState>();
        _notificationOpenWorkflow = new NotificationOpenWorkflow(
            _notificationInboxState,
            GetService<IGitHubNotificationQueryService>());
        _currentLayout = _widgetLayoutService.Load();

        RefreshCommand = new AsyncRelayCommand(RefreshDashboardAsync);
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync);
        NavigateActivityTargetCommand = new RelayCommand<ActivityNavigationTarget>(NavigateActivityTarget);
        OpenSideRailCommand = new RelayCommand(OpenSideRail);
        CloseSideRailCommand = new RelayCommand(CloseSideRail);
        OpenCustomizeCommand = new RelayCommand(OpenCustomize);
        SaveCustomizeCommand = new RelayCommand(SaveCustomize);
        CancelCustomizeCommand = new RelayCommand(CancelCustomize);
        ResetCustomizeCommand = new RelayCommand(ResetCustomize);
        BuildQuickActions();
        RebuildWidgets();
    }

    public KeyedObservableCollection<DashboardMetricViewItem, DashboardMetricItem> Metrics { get; } = [];

    public KeyedObservableCollection<ActivityCardViewModel, GitHubActivityEvent> RecentActivity { get; } = [];

    public KeyedObservableCollection<ActivityCardViewModel, ActivityCardViewModel> RecentActivityPreview { get; } = [];

    public KeyedObservableCollection<DashboardRepositoryCardItem, GitHubRepository> RecentRepositories { get; } = [];

    public KeyedObservableCollection<DashboardRepositoryCardItem, DashboardRepositoryCardItem> RecentRepositoriesPreview { get; } = [];

    public KeyedObservableCollection<DashboardRepositoryCardItem, GitHubRepository> RecommendedRepositories { get; } = [];

    public KeyedObservableCollection<DashboardRepositoryCardItem, DashboardRepositoryCardItem> RecommendedRepositoriesPreview { get; } = [];

    public KeyedObservableCollection<DashboardNotificationItem, GitHubNotificationThread> Notifications { get; } = [];

    public KeyedObservableCollection<DashboardNotificationItem, DashboardNotificationItem> NotificationsPreview { get; } = [];

    public ObservableCollection<DashboardQuickActionItem> QuickActions { get; } = [];

    public ObservableCollection<DashboardWidgetViewItem> MainWidgets { get; } = [];

    public ObservableCollection<DashboardWidgetViewItem> SideWidgets { get; } = [];

    public ObservableCollection<DashboardWidgetCustomizeItem> CustomizeItems { get; } = [];

    public IAsyncRelayCommand RefreshCommand { get; }

    public IAsyncRelayCommand ReconnectCommand { get; }

    public RelayCommand<ActivityNavigationTarget> NavigateActivityTargetCommand { get; }

    public IRelayCommand OpenSideRailCommand { get; }

    public IRelayCommand CloseSideRailCommand { get; }

    public IRelayCommand OpenCustomizeCommand { get; }

    public IRelayCommand SaveCustomizeCommand { get; }

    public IRelayCommand CancelCustomizeCommand { get; }

    public IRelayCommand ResetCustomizeCommand { get; }

    [ObservableProperty]
    public partial string GreetingText { get; set; } = L("Dashboard/Greeting/Default", "Good afternoon");

    [ObservableProperty]
    public partial string UserStatusText { get; set; } = L("Dashboard/Status/ProfileUnavailable", "GitHub profile details are not available yet.");

    [ObservableProperty]
    public partial string DashboardStatusText { get; set; } = L("Dashboard/Status/Ready", "Home is ready.");

    [ObservableProperty]
    public partial string ActivityStatusText { get; set; } = L("Dashboard/Status/ActivityReady", "Activity is ready.");

    [ObservableProperty]
    public partial string RepositoryStatusText { get; set; } = L("Dashboard/Status/RepositoriesReady", "Repositories are ready.");

    [ObservableProperty]
    public partial string RecommendationStatusText { get; set; } = L("Dashboard/Status/RecommendationsReady", "Recommendations are ready.");

    [ObservableProperty]
    public partial string RecommendationEmptyStateMessage { get; set; } = L("Dashboard/Empty/RecommendationsDescription", "Open or star repositories to improve recommendations.");

    [ObservableProperty]
    public partial string NotificationStatusText { get; set; } = L("Dashboard/Status/NotificationsReady", "Notifications are ready.");

    [ObservableProperty]
    public partial string ReconnectBannerText { get; set; } = L("Dashboard/Status/ReconnectNotifications", "Reconnect GitHub to enable notification access.");

    [ObservableProperty]
    public partial bool IsDashboardRefreshing { get; set; }

    [ObservableProperty]
    public partial bool IsActivityLoading { get; set; }

    [ObservableProperty]
    public partial bool IsRepositoryLoading { get; set; }

    [ObservableProperty]
    public partial bool IsRecommendationLoading { get; set; }

    [ObservableProperty]
    public partial bool IsNotificationLoading { get; set; }

    [ObservableProperty]
    public partial bool AreActivityItemsVisible { get; set; }

    [ObservableProperty]
    public partial bool IsActivityEmptyStateVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool AreRepositoriesVisible { get; set; }

    [ObservableProperty]
    public partial bool IsRepositoriesEmptyStateVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool AreRecommendationsVisible { get; set; }

    [ObservableProperty]
    public partial bool IsRecommendationsEmptyStateVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool AreNotificationsVisible { get; set; }

    [ObservableProperty]
    public partial bool IsNotificationsEmptyStateVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsReconnectBannerVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSideRailExpanded))]
    public partial bool IsSideRailCompact { get; set; }

    [ObservableProperty]
    public partial bool IsSideRailDrawerOpen { get; set; }

    [ObservableProperty]
    public partial bool IsCustomizeDialogOpen { get; set; }

    [ObservableProperty]
    public partial double RepositoryCardWidth { get; set; } = 318;

    [ObservableProperty]
    public partial double QuickActionCardWidth { get; set; } = 124;

    public bool IsSideRailExpanded => !IsSideRailCompact;

    public string RecentActivityTitle => L("Dashboard/Widget/RecentActivity", "Recent activity");

    public string RecentRepositoriesTitle => L("Dashboard/Widget/RecentRepositories", "Recent repositories");

    public string RecommendedRepositoriesTitle => L("Dashboard/Widget/RecommendedRepositories", "Recommended repositories");

    public string NotificationsTitle => L("Dashboard/Widget/LatestNotifications", "Latest notifications");

    public string OverviewTitle => L("Dashboard/Widget/Overview", "Overview");

    public string QuickActionsTitle => L("Dashboard/Widget/QuickActions", "Quick actions");

    public string RepositoryWidgetTitle => L("Dashboard/Widget/RecentRepositories", "Recent repositories");

    public string ActivityEmptyStateTitle => L("Dashboard/Empty/ActivityTitle", "No activity available.");

    public string ActivityEmptyStateDescription => L("Dashboard/Empty/ActivityDescription", "Cached activity will appear here after GitHub returns events.");

    public string RepositoriesEmptyStateTitle => L("Dashboard/Empty/RepositoriesTitle", "No repositories available.");

    public string RepositoriesEmptyStateDescription => L("Dashboard/Empty/RepositoriesDescription", "Recent repositories will appear here after GitHub returns them.");

    public string RecommendationsEmptyStateTitle => L("Dashboard/Empty/RecommendationsTitle", "No recommendations yet.");

    public string RecommendationsEmptyStateDescription => L("Dashboard/Empty/RecommendationsSignals", "Recommendations need recent or starred repository signals.");

    public string NotificationsEmptyStateTitle => L("Dashboard/Empty/NotificationsTitle", "No notifications.");

    public string NotificationsEmptyStateDescription => L("Dashboard/Empty/NotificationsDescription", "Unread GitHub notifications will appear here.");

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _telemetryService.TrackEvent(
            "dashboard.opened",
            new Dictionary<string, string?> { ["source"] = "home" });
        await RefreshDashboardAsync(cancellationToken);
    }

    public async Task RefreshDashboardAsync(CancellationToken cancellationToken = default)
    {
        if (IsDashboardRefreshing)
        {
            return;
        }

        Stopwatch refreshDuration = Stopwatch.StartNew();
        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            DashboardStatusText = L("Dashboard/Status/AuthenticationUnavailable", "GitHub authentication is no longer available. Please sign in again.");
            _telemetryService.TrackEvent(
                "dashboard.refresh.completed",
                new Dictionary<string, string?>
                {
                    ["source"] = "home",
                    ["result"] = TelemetryTaxonomy.Results.AuthError,
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(refreshDuration.Elapsed)
                });
            _authService.SignOut();
            return;
        }

        IsDashboardRefreshing = true;
        SetLoadingStates(true);
        DashboardStatusText = HasAnyDashboardContent()
            ? L("Dashboard/Status/RefreshingCached", "Refreshing Home while keeping cached sections visible.")
            : L("Dashboard/Status/Loading", "Loading Home dashboard...");
        _telemetryService.TrackEvent(
            "dashboard.refresh.started",
            new Dictionary<string, string?>
            {
                ["source"] = "home",
                ["result"] = "started"
            });

        try
        {
            GitHubUser? user = _authService.AuthenticatedUser ?? await _authService.RefreshAuthenticatedUserAsync();
            string userPartition = GetActiveUserPartition(token);
            long notificationGeneration = _notificationInboxState.CaptureMutationGeneration(userPartition);
            DashboardHomeSnapshot snapshot = await _dashboardQueryService.GetSnapshotAsync(
                token,
                userPartition,
                user,
                cancellationToken);
            ApplySnapshot(snapshot, notificationGeneration);
            _telemetryService.TrackEvent(
                "dashboard.refresh.completed",
                new Dictionary<string, string?>
                {
                    ["source"] = "home",
                    ["result"] = "success",
                    ["cache_state"] = OverallCacheState(snapshot).ToString(),
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(refreshDuration.Elapsed)
                });
        }
        catch (OperationCanceledException)
        {
            _telemetryService.TrackEvent(
                "dashboard.refresh.completed",
                new Dictionary<string, string?>
                {
                    ["source"] = "home",
                    ["result"] = TelemetryTaxonomy.Results.Cancelled,
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(refreshDuration.Elapsed)
                });
            throw;
        }
        catch (GitHubAuthenticationException)
        {
            DashboardStatusText = L("Dashboard/Status/AuthenticationInvalid", "GitHub authentication is no longer valid. Please sign in again.");
            _telemetryService.TrackEvent(
                "dashboard.refresh.completed",
                new Dictionary<string, string?>
                {
                    ["source"] = "home",
                    ["result"] = "auth_error",
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(refreshDuration.Elapsed)
                });
            _authService.SignOut();
        }
        catch (Exception ex) when (ex is GitHubApiException or System.Net.Http.HttpRequestException)
        {
            DashboardStatusText = HasAnyDashboardContent()
                ? L("Dashboard/Status/RefreshFailedCached", "Home refresh failed. Showing cached dashboard data.")
                : JitHub.WinUI.Helpers.UserFacingError.For(
                    ex,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                    "dashboard");
            _telemetryService.TrackEvent(
                "dashboard.refresh.completed",
                new Dictionary<string, string?>
                {
                    ["source"] = "home",
                    ["result"] = "error",
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(refreshDuration.Elapsed)
                });
        }
        catch
        {
            _telemetryService.TrackEvent(
                "dashboard.refresh.completed",
                new Dictionary<string, string?>
                {
                    ["source"] = "home",
                    ["result"] = TelemetryTaxonomy.Results.Error,
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(refreshDuration.Elapsed)
                });
            throw;
        }
        finally
        {
            IsDashboardRefreshing = false;
            SetLoadingStates(false);
            if (_starProjectionRefreshPending)
            {
                BackgroundTaskObserver.Run(
                    RefreshStarProjectionAsync,
                    "dashboard",
                    _telemetryService);
            }
        }
    }

    public Task RefreshRecentRepositoriesAsync() => RefreshDashboardAsync();

    public Task RefreshActivityAsync() => RefreshDashboardAsync();

    public void NotifyStarLibraryChanged(string userId)
    {
        if (!_initialized || string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token) ||
            !string.Equals(userId, GetActiveUserPartition(token), StringComparison.Ordinal))
        {
            return;
        }

        _starProjectionRefreshPending = true;
        if (!IsDashboardRefreshing)
        {
            BackgroundTaskObserver.Run(
                RefreshStarProjectionAsync,
                "dashboard",
                _telemetryService);
        }
    }

    public void SignOut()
    {
        _authService.SignOut();
    }

    private async Task RefreshStarProjectionAsync()
    {
        if (!_starProjectionRefreshPending || IsDashboardRefreshing)
        {
            return;
        }

        _starProjectionRefreshPending = false;
        await RefreshDashboardAsync();
    }

    public void SetCompactSideRail(bool compact)
    {
        IsSideRailCompact = compact;
        if (!compact)
        {
            IsSideRailDrawerOpen = false;
        }
    }

    public void SetDashboardCardWidths(double mainRailWidth)
    {
        double contentWidth = Math.Max(240, mainRailWidth - 36);
        int repositoryColumns = contentWidth >= 560 ? 2 : 1;
        double repositoryGap = repositoryColumns > 1 ? 10 : 0;
        RepositoryCardWidth = Math.Floor((contentWidth - repositoryGap) / repositoryColumns);
        int repositoryPreviewLimit = repositoryColumns > 1 ? 4 : 2;
        if (_repositoryPreviewLimit != repositoryPreviewLimit)
        {
            _repositoryPreviewLimit = repositoryPreviewLimit;
            RefreshPreviewCollections();
        }

        QuickActionCardWidth = repositoryColumns > 1
            ? Math.Clamp(Math.Floor(contentWidth / Math.Max(1, QuickActions.Count)) - 8, 104, 124)
            : 124;
    }

    private void OpenSideRail()
    {
        if (IsSideRailDrawerOpen)
        {
            IsSideRailDrawerOpen = false;
            return;
        }

        IsSideRailDrawerOpen = true;
        _telemetryService.TrackEvent(
            "dashboard.side_rail.opened",
            new Dictionary<string, string?>
            {
                ["source"] = "home",
                ["result"] = "opened"
            });
    }

    private void CloseSideRail()
    {
        IsSideRailDrawerOpen = false;
    }

    private void OpenCustomize()
    {
        BuildCustomizeItems(_currentLayout);
        IsCustomizeDialogOpen = true;
        _telemetryService.TrackEvent(
            "dashboard.customize.opened",
            new Dictionary<string, string?>
            {
                ["source"] = "home",
                ["result"] = "opened"
            });
    }

    private void SaveCustomize()
    {
        DashboardWidgetLayout staged = CreateLayoutFromCustomizeItems();
        _widgetLayoutService.Save(staged);
        _currentLayout = _widgetLayoutService.Normalize(staged);
        RebuildWidgets();
        IsCustomizeDialogOpen = false;
        _telemetryService.TrackEvent(
            "dashboard.customize.saved",
            new Dictionary<string, string?>
            {
                ["source"] = "home",
                ["result"] = "success"
            });
    }

    private void CancelCustomize()
    {
        IsCustomizeDialogOpen = false;
    }

    private void ResetCustomize()
    {
        BuildCustomizeItems(_widgetLayoutService.CreateDefault());
        _telemetryService.TrackEvent(
            "dashboard.customize.reset",
            new Dictionary<string, string?>
            {
                ["source"] = "home",
                ["result"] = "staged"
            });
    }

    private void RebuildWidgets()
    {
        MainWidgets.Clear();
        SideWidgets.Clear();

        foreach (string id in _currentLayout.MainWidgetIds)
        {
            MainWidgets.Add(CreateWidget(id));
        }

        foreach (string id in _currentLayout.SideWidgetIds)
        {
            SideWidgets.Add(CreateWidget(id));
        }
    }

    private DashboardWidgetViewItem CreateWidget(string id)
    {
        return new DashboardWidgetViewItem
        {
            Id = id,
            Title = WidgetTitle(id),
            Subtitle = WidgetSubtitle(id),
            Glyph = WidgetGlyph(id),
            Height = WidgetHeight(id),
            Dashboard = this,
            ViewAllCommand = new RelayCommand(() => ViewAllWidget(id))
        };
    }

    private void ViewAllWidget(string id)
    {
        string result = TelemetryTaxonomy.Results.Unavailable;
        try
        {
            switch (id)
            {
                case DashboardWidgetIds.Notifications:
                    result = TelemetryTaxonomy.NavigationResult(
                        _shellViewModel.TryOpenNotificationsPage());
                    break;
            }
        }
        catch
        {
            result = TelemetryTaxonomy.Results.Error;
            throw;
        }
        finally
        {
            _telemetryService.TrackEvent(
                "dashboard.widget.view_all.clicked",
                new Dictionary<string, string?>
                {
                    ["source"] = "home",
                    ["widget"] = id,
                    ["result"] = result
                });
        }
    }

    private void RefreshPreviewCollections()
    {
        RecentActivityPreview.ApplySnapshot(
            RecentActivity.Take(4),
            ActivityKey,
            ActivityKey,
            static item => item);
        RecentRepositoriesPreview.ApplySnapshot(
            RecentRepositories.Take(_repositoryPreviewLimit),
            RepositoryCardKey,
            RepositoryCardKey,
            static item => item);
        RecommendedRepositoriesPreview.ApplySnapshot(
            RecommendedRepositories.Take(4),
            RepositoryCardKey,
            RepositoryCardKey,
            static item => item);
        NotificationsPreview.ApplySnapshot(
            Notifications.Take(3),
            static item => item.Id,
            static item => item.Id,
            static item => item);
    }

    private void BuildCustomizeItems(DashboardWidgetLayout layout)
    {
        CustomizeItems.Clear();
        HashSet<string> hidden = new(layout.HiddenWidgetIds, StringComparer.Ordinal);

        foreach (string id in layout.MainWidgetIds.Concat(layout.SideWidgetIds).Concat(layout.HiddenWidgetIds))
        {
            if (CustomizeItems.Any(item => string.Equals(item.Id, id, StringComparison.Ordinal)))
            {
                continue;
            }

            DashboardWidgetCustomizeItem item = new()
            {
                Id = id,
                Title = WidgetTitle(id),
                Glyph = WidgetGlyph(id),
                Rail = layout.SideWidgetIds.Contains(id) ? "side" : "main",
                IsVisible = !hidden.Contains(id)
            };
            item.ToggleVisibilityCommand = new RelayCommand(() =>
            {
                item.IsVisible = !item.IsVisible;
                _telemetryService.TrackEvent(
                    "dashboard.widget.toggled",
                    new Dictionary<string, string?>
                    {
                        ["widget"] = item.Id,
                        ["source"] = "home",
                        ["result"] = item.IsVisible ? "shown" : "hidden"
                    });
            });
            item.MoveUpCommand = new RelayCommand(() => MoveCustomizeItem(item, -1));
            item.MoveDownCommand = new RelayCommand(() => MoveCustomizeItem(item, 1));
            item.ToggleRailCommand = new RelayCommand(() => item.Rail = string.Equals(item.Rail, "side", StringComparison.Ordinal) ? "main" : "side");
            CustomizeItems.Add(item);
        }
    }

    private void MoveCustomizeItem(DashboardWidgetCustomizeItem item, int delta)
    {
        int index = CustomizeItems.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        int target = Math.Clamp(index + delta, 0, CustomizeItems.Count - 1);
        if (target == index)
        {
            return;
        }

        CustomizeItems.Move(index, target);
    }

    private DashboardWidgetLayout CreateLayoutFromCustomizeItems()
    {
        List<string> main = [];
        List<string> side = [];
        List<string> hidden = [];

        foreach (DashboardWidgetCustomizeItem item in CustomizeItems)
        {
            if (!item.IsVisible)
            {
                hidden.Add(item.Id);
                continue;
            }

            if (string.Equals(item.Rail, "side", StringComparison.Ordinal))
            {
                side.Add(item.Id);
            }
            else
            {
                main.Add(item.Id);
            }
        }

        return _widgetLayoutService.Normalize(new DashboardWidgetLayout(1, main, side, hidden));
    }

    private void ApplySnapshot(DashboardHomeSnapshot snapshot, long notificationGeneration)
    {
        ApplyUser(snapshot.User);
        ApplyMetrics(snapshot.Metrics);
        ApplyRepositories(snapshot.RecentRepositories, RecentRepositories, isRecommendation: false);
        ApplyActivity(snapshot.RecentActivity);
        ApplyNotifications(snapshot.Notifications, notificationGeneration);
        ApplyRepositories(snapshot.RecommendedRepositories, RecommendedRepositories, isRecommendation: true);
        RefreshPreviewCollections();
        OnPropertyChanged(nameof(RepositoryWidgetTitle));

        IsReconnectBannerVisible = snapshot.Notifications.RequiresReconnect;
        ReconnectBannerText = snapshot.Notifications.RequiresReconnect
            ? L("Dashboard/Status/ReconnectNotifications", "Reconnect GitHub to enable notification access.")
            : string.Empty;
        DashboardStatusText = BuildDashboardStatus(snapshot);
    }

    private void ApplyUser(GitHubUser? user)
    {
        string displayName = string.IsNullOrWhiteSpace(user?.Name)
            ? user?.Login ?? L("Dashboard/Greeting/DeveloperFallback", "developer")
            : user!.Name!;
        GreetingText = LF("Dashboard/Greeting/NamedFormat", "Good afternoon, {0}", displayName);
        UserStatusText = string.IsNullOrWhiteSpace(user?.Login)
            ? L("Dashboard/Status/ProfileUnavailable", "GitHub profile details are not available yet.")
            : string.IsNullOrWhiteSpace(user.Name)
                ? LF("Dashboard/Status/SignedInLoginFormat", "Signed in as @{0}.", user.Login)
                : LF("Dashboard/Status/SignedInNameFormat", "Signed in as {0} (@{1}).", user.Name, user.Login);
    }

    private void ApplyMetrics(DashboardSectionResult<DashboardMetricItem[]> section)
    {
        if (section.Value.Length > 0 || !section.HasError || Metrics.Count == 0)
        {
            Metrics.ApplySnapshot(
                section.Value,
                static item => item.Label,
                static item => item.Label,
                static item => new DashboardMetricViewItem { Metric = item },
                static (item, snapshot) =>
                {
                    if (item.Metric == snapshot)
                    {
                        return false;
                    }

                    item.Metric = snapshot;
                    return true;
                });
        }

        TrackSection("overview", section.CacheState, section.HasError ? "error" : "success");
    }

    private void ApplyRepositories(
        DashboardSectionResult<GitHubRepository[]> section,
        KeyedObservableCollection<DashboardRepositoryCardItem, GitHubRepository> target,
        bool isRecommendation)
    {
        if (section.Value.Length > 0 || !section.HasError || target.Count == 0)
        {
            string source = isRecommendation ? "recommendations" : "recent_repositories";
            target.ApplySnapshot(
                section.Value,
                RepositoryKey,
                item => RepositoryKey(item.Repository),
                repository => CreateRepositoryCard(repository, source),
                static (item, repository) =>
                {
                    if (ReferenceEquals(item.Repository, repository))
                    {
                        return false;
                    }

                    item.Repository = repository;
                    return true;
                });
        }

        bool hasItems = target.Count > 0;
        if (isRecommendation)
        {
            AreRecommendationsVisible = hasItems;
            IsRecommendationsEmptyStateVisible = !hasItems;
            RecommendationStatusText = BuildSectionStatus(
                section,
                target.Count,
                L("Dashboard/Entity/Recommendation", "recommendation"),
                L("Dashboard/Entity/Recommendations", "recommendations"));
            RecommendationEmptyStateMessage = section.HasError
                ? L("Dashboard/Status/RecommendationsOffline", "Recommendations are unavailable while GitHub is offline.")
                : L("Dashboard/Empty/RecommendationsDescription", "Open or star repositories to improve recommendations.");
            TrackSection("recommendations", section.CacheState, section.HasError ? "error" : "success");
        }
        else
        {
            AreRepositoriesVisible = hasItems;
            IsRepositoriesEmptyStateVisible = !hasItems;
            RepositoryStatusText = BuildSectionStatus(
                section,
                target.Count,
                L("Dashboard/Entity/Repository", "repository"),
                L("Dashboard/Entity/Repositories", "repositories"));
            TrackSection("repositories", section.CacheState, section.HasError ? "error" : "success");
        }
    }

    private void ApplyActivity(DashboardSectionResult<GitHubActivityEvent[]> section)
    {
        if (section.Value.Length > 0 || !section.HasError || RecentActivity.Count == 0)
        {
            RecentActivity.ApplySnapshot(
                section.Value,
                DashboardActivityMerger.CreateStableActivityId,
                ActivityKey,
                activityEvent => ActivityCardViewModelFactory.Create(activityEvent, NavigateActivityTargetCommand));
        }

        AreActivityItemsVisible = RecentActivity.Count > 0;
        IsActivityEmptyStateVisible = RecentActivity.Count == 0;
        ActivityStatusText = BuildSectionStatus(
            section,
            RecentActivity.Count,
            L("Dashboard/Entity/ActivityItem", "activity item"),
            L("Dashboard/Entity/ActivityItems", "activity items"));
        TrackSection("activity", section.CacheState, section.HasError ? "error" : "success");
    }

    private void ApplyNotifications(
        DashboardSectionResult<GitHubNotificationThread[]> section,
        long notificationGeneration)
    {
        if (section.Value.Length > 0 || !section.HasError || Notifications.Count == 0)
        {
            Notifications.ApplySnapshot(
                section.Value,
                NotificationKey,
                static item => item.Id,
                CreateNotificationItem,
                (item, notification) => UpdateNotificationItem(item, notification));
        }

        string? token = GetActiveToken();
        if (!string.IsNullOrWhiteSpace(token))
        {
            _notificationInboxState.ApplySnapshot(
                GetActiveUserPartition(token),
                section.Value,
                section.Value.Length >= 10,
                section.FetchedAt,
                NotificationCountSource.HomePreview,
                notificationGeneration);
        }

        AreNotificationsVisible = Notifications.Count > 0;
        IsNotificationsEmptyStateVisible = Notifications.Count == 0;
        NotificationStatusText = section.RequiresReconnect
            ? L("Dashboard/Status/ReconnectNotificationData", "Reconnect GitHub to show notification data.")
            : BuildSectionStatus(
                section,
                Notifications.Count,
                L("Dashboard/Entity/Notification", "notification"),
                L("Dashboard/Entity/Notifications", "notifications"));
        TrackSection("notifications", section.CacheState, section.RequiresReconnect ? "reconnect" : section.HasError ? "error" : "success");
    }

    private DashboardRepositoryCardItem CreateRepositoryCard(GitHubRepository repository, string source)
    {
        return new DashboardRepositoryCardItem
        {
            Repository = repository,
            Command = new RelayCommand(() =>
            {
                _telemetryService.TrackEvent(
                    "dashboard.section.loaded",
                    new Dictionary<string, string?>
                    {
                        ["section"] = source,
                        ["source"] = "home",
                        ["result"] = "opened"
                    });
                _shellViewModel.OpenRepository(repository);
            })
        };
    }

    private DashboardNotificationItem CreateNotificationItem(GitHubNotificationThread notification)
    {
        bool isUnread = ResolveNotificationUnread(notification);
        return new DashboardNotificationItem
        {
            Id = NotificationKey(notification),
            Title = string.IsNullOrWhiteSpace(notification.Subject.Title)
                ? L("Dashboard/Notification/FallbackTitle", "GitHub notification")
                : notification.Subject.Title,
            Subtitle = BuildNotificationSubtitle(notification),
            MetaText = $"{HumanizeReason(notification.Reason)} · {DashboardRepositoryCardItem.FormatRelativeTime(notification.UpdatedAt)}",
            Glyph = NotificationGlyph(notification.Subject.Type),
            IsUnread = isUnread,
            Command = new AsyncRelayCommand(() => OpenNotificationAsync(notification))
        };
    }

    public void ApplySharedNotificationReadStates()
    {
        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        string accountId = GetActiveUserPartition(token);
        foreach (DashboardNotificationItem item in Notifications)
        {
            if (_notificationInboxState.TryGetThreadUnreadState(accountId, item.Id, out bool isUnread))
            {
                item.IsUnread = isUnread;
            }
        }
    }

    private bool ResolveNotificationUnread(GitHubNotificationThread notification)
    {
        string? token = GetActiveToken();
        if (!string.IsNullOrWhiteSpace(token) &&
            _notificationInboxState.TryGetThreadUnreadState(
                GetActiveUserPartition(token),
                NotificationKey(notification),
                out bool isUnread))
        {
            return isUnread;
        }

        return notification.Unread;
    }

    private bool UpdateNotificationItem(DashboardNotificationItem item, GitHubNotificationThread notification)
    {
        DashboardNotificationItem updated = CreateNotificationItem(notification);
        bool changed = item.Title != updated.Title ||
            item.Subtitle != updated.Subtitle ||
            item.MetaText != updated.MetaText ||
            item.Glyph != updated.Glyph ||
            item.IsUnread != updated.IsUnread;
        if (!changed)
        {
            return false;
        }

        item.Title = updated.Title;
        item.Subtitle = updated.Subtitle;
        item.MetaText = updated.MetaText;
        item.Glyph = updated.Glyph;
        item.IsUnread = updated.IsUnread;
        return true;
    }

    private static string ActivityKey(ActivityCardViewModel item) =>
        string.IsNullOrWhiteSpace(item.EventId)
            ? string.Join(':', item.EventType, item.ActorLogin, item.RepoDisplayName, item.TimestampText)
            : item.EventId;

    private static string RepositoryKey(GitHubRepository repository) =>
        !string.IsNullOrWhiteSpace(repository.FullName)
            ? repository.FullName
            : repository.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string RepositoryCardKey(DashboardRepositoryCardItem item) => RepositoryKey(item.Repository);

    private static string NotificationKey(GitHubNotificationThread notification) =>
        !string.IsNullOrWhiteSpace(notification.Id)
            ? notification.Id
            : string.Join(':', notification.Repository.FullName, notification.Subject.Type, notification.Subject.Url);

    private void BuildQuickActions()
    {
        QuickActions.Clear();
        AddQuickAction("new_repository", L("Dashboard/QuickAction/NewRepository", "New Repository"), L("Dashboard/QuickAction/NewRepositoryDescription", "Create a repository"), "\uE8F4", ActivityCardTone.Success, _shellViewModel.TryOpenNewRepositoryModal);
        AddQuickAction("search_repositories", L("Dashboard/QuickAction/SearchRepositories", "Search Repositories"), L("Dashboard/QuickAction/SearchRepositoriesDescription", "Find repositories"), "\uE721", ActivityCardTone.Accent, () =>
        {
            _shellViewModel.FocusCommandSearchRequested();
            return true;
        });
        AddQuickAction("manage_repositories", L("Dashboard/QuickAction/ManageRepositories", "Manage Repositories"), L("Dashboard/QuickAction/ManageRepositoriesDescription", "Open repository management"), "\uE8B7", ActivityCardTone.Gold, _shellViewModel.TryOpenManageRepositories);
        AddQuickAction("my_issues", L("Dashboard/QuickAction/MyIssues", "My Issues"), L("Dashboard/QuickAction/MyIssuesDescription", "Issues involving you"), "\uE8A5", ActivityCardTone.Warning, _shellViewModel.TryOpenMyIssuesPage);
        AddQuickAction("my_pull_requests", L("Dashboard/QuickAction/MyPullRequests", "My Pull Requests"), L("Dashboard/QuickAction/MyPullRequestsDescription", "Pull requests involving you"), "\uE8EE", ActivityCardTone.Purple, _shellViewModel.TryOpenMyPullRequestsPage);
    }

    private void AddQuickAction(string id, string title, string subtitle, string glyph, ActivityCardTone tone, Func<bool> action)
    {
        QuickActions.Add(new DashboardQuickActionItem
        {
            Id = id,
            Title = title,
            Subtitle = subtitle,
            Glyph = glyph,
            Tone = tone,
            Command = new RelayCommand(() =>
            {
                _telemetryService.TrackEvent(
                    "dashboard.quick_action.executed",
                    new Dictionary<string, string?>
                    {
                        ["action"] = id,
                        ["source"] = "home",
                        ["result"] = "started"
                    });
                try
                {
                    bool accepted = action();
                    _telemetryService.TrackEvent(
                        "dashboard.quick_action.executed",
                        new Dictionary<string, string?>
                        {
                            ["action"] = id,
                            ["source"] = "home",
                            ["result"] = TelemetryTaxonomy.NavigationResult(accepted)
                        });
                }
                catch
                {
                    _telemetryService.TrackEvent(
                        "dashboard.quick_action.executed",
                        new Dictionary<string, string?>
                        {
                            ["action"] = id,
                            ["source"] = "home",
                            ["result"] = "error"
                        });
                    throw;
                }
            })
        });
    }

    private async Task ReconnectAsync()
    {
        _telemetryService.TrackEvent(
            "dashboard.reconnect.clicked",
            new Dictionary<string, string?>
            {
                ["source"] = "home",
                ["result"] = "started"
            });
        try
        {
            await _authService.Authenticate();
            _telemetryService.TrackEvent(
                "dashboard.reconnect.clicked",
                new Dictionary<string, string?>
                {
                    ["source"] = "home",
                    ["result"] = "success"
                });
        }
        catch (OperationCanceledException)
        {
            _telemetryService.TrackEvent(
                "dashboard.reconnect.clicked",
                new Dictionary<string, string?>
                {
                    ["source"] = "home",
                    ["result"] = TelemetryTaxonomy.Results.Cancelled
                });
            throw;
        }
        catch
        {
            _telemetryService.TrackEvent(
                "dashboard.reconnect.clicked",
                new Dictionary<string, string?>
                {
                    ["source"] = "home",
                    ["result"] = "error"
                });
            throw;
        }
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

        _shellViewModel.OpenRepositoryTarget(repository, page, pageArg);
    }

    private Task OpenNotificationAsync(GitHubNotificationThread notification)
    {
        string? token = GetActiveToken();
        string accountId = string.IsNullOrWhiteSpace(token) ? string.Empty : GetActiveUserPartition(token);
        return _notificationOpenWorkflow.ExecuteAsync(
            token,
            accountId,
            notification,
            () => _shellViewModel.OpenNotification(notification, "home"),
            ApplySharedNotificationReadStates,
            () => NotificationStatusText = L("Dashboard/Notification/MarkReadFailed", "The notification opened, but GitHub could not mark it as read."));
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

    private string? GetActiveToken()
    {
        long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        return _authService.GetToken(userId);
    }

    private string GetActiveUserPartition(string token)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(token))
        {
            return "public";
        }

        long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        return userId > 0 ? userId.ToString(System.Globalization.CultureInfo.InvariantCulture) : "current";
    }

    private void SetLoadingStates(bool isLoading)
    {
        IsActivityLoading = isLoading && RecentActivity.Count == 0;
        IsRepositoryLoading = isLoading && RecentRepositories.Count == 0;
        IsRecommendationLoading = isLoading && RecommendedRepositories.Count == 0;
        IsNotificationLoading = isLoading && Notifications.Count == 0;
    }

    private bool HasAnyDashboardContent() =>
        Metrics.Count > 0 ||
        RecentActivity.Count > 0 ||
        RecentRepositories.Count > 0 ||
        RecommendedRepositories.Count > 0 ||
        Notifications.Count > 0;

    private static string BuildDashboardStatus(DashboardHomeSnapshot snapshot)
    {
        if (snapshot.RecentActivity.IsRefreshInProgress ||
            snapshot.RecentRepositories.IsRefreshInProgress ||
            snapshot.RecommendedRepositories.IsRefreshInProgress ||
            snapshot.Notifications.IsRefreshInProgress ||
            snapshot.Metrics.IsRefreshInProgress)
        {
            return L("Dashboard/Status/ShowingCached", "Showing cached Home data while refreshing.");
        }

        return L("Dashboard/Status/Current", "Home dashboard is current.");
    }

    private static string BuildSectionStatus<T>(
        DashboardSectionResult<T> section,
        int count,
        string singular,
        string plural)
        where T : class
    {
        string label = count == 1 ? singular : plural;
        if (section.HasError && count > 0)
        {
            return LF("Dashboard/Status/SectionRefreshFailedFormat", "Showing cached {0}. Refresh failed.", label);
        }

        if (section.HasError)
        {
            return LF("Dashboard/Status/SectionLoadFailedFormat", "Could not load {0}.", plural);
        }

        if (section.IsRefreshInProgress)
        {
            return LF("Dashboard/Status/SectionRefreshingFormat", "Showing cached {0} while refreshing.", label);
        }

        return count == 0
            ? LF("Dashboard/Status/SectionEmptyFormat", "No {0} available.", plural)
            : LF("Dashboard/Status/SectionCountFormat", "Showing {0} {1}.", count, label);
    }

    private static string BuildNotificationSubtitle(GitHubNotificationThread notification)
    {
        string repo = notification.Repository.FullName;
        string type = string.IsNullOrWhiteSpace(notification.Subject.Type)
            ? "thread"
            : notification.Subject.Type;
        return string.IsNullOrWhiteSpace(repo) ? type : $"{type} in {repo}";
    }

    private static string HumanizeReason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "notification";
        }

        return reason.Replace('_', ' ');
    }

    private static string NotificationGlyph(string? subjectType) => subjectType?.Trim() switch
    {
        "Issue" => "\uE8A5",
        "PullRequest" => "\uE8EE",
        "Commit" => "\uE7C1",
        "Release" => "\uE896",
        _ => "\uE8BD"
    };

    private static CacheState OverallCacheState(DashboardHomeSnapshot snapshot)
    {
        CacheState[] states =
        [
            snapshot.Metrics.CacheState,
            snapshot.RecentRepositories.CacheState,
            snapshot.RecentActivity.CacheState,
            snapshot.Notifications.CacheState,
            snapshot.RecommendedRepositories.CacheState
        ];

        if (states.Any(static state => state == CacheState.Error))
        {
            return CacheState.Error;
        }

        if (states.Any(static state => state is CacheState.Stale or CacheState.Refreshing))
        {
            return CacheState.Stale;
        }

        if (states.Any(static state => state == CacheState.Miss))
        {
            return CacheState.Miss;
        }

        return CacheState.Fresh;
    }

    private static string WidgetTitle(string id) => id switch
    {
        DashboardWidgetIds.RecentActivity => L("Dashboard/Widget/RecentActivity", "Recent activity"),
        DashboardWidgetIds.Repositories => L("Dashboard/Widget/RecentRepositories", "Recent repositories"),
        DashboardWidgetIds.QuickActions => L("Dashboard/Widget/QuickActions", "Quick actions"),
        DashboardWidgetIds.Overview => L("Dashboard/Widget/Overview", "Overview"),
        DashboardWidgetIds.RecommendedRepositories => L("Dashboard/Widget/RecommendedRepositories", "Recommended repositories"),
        DashboardWidgetIds.Notifications => L("Dashboard/Widget/Notifications", "Notifications"),
        _ => L("Dashboard/Widget/Fallback", "Widget")
    };

    private static string WidgetSubtitle(string id) => string.Empty;

    private static string WidgetGlyph(string id) => id switch
    {
        DashboardWidgetIds.RecentActivity => "\uE9D9",
        DashboardWidgetIds.Repositories => "\uE8B7",
        DashboardWidgetIds.QuickActions => "\uE8A7",
        DashboardWidgetIds.Overview => "\uE9D2",
        DashboardWidgetIds.RecommendedRepositories => "\uE734",
        DashboardWidgetIds.Notifications => "\uEA8F",
        _ => "\uE8A7"
    };

    private static double WidgetHeight(string id) => id switch
    {
        DashboardWidgetIds.RecentActivity => 430,
        DashboardWidgetIds.Repositories => 370,
        DashboardWidgetIds.QuickActions => 220,
        DashboardWidgetIds.Overview => 300,
        DashboardWidgetIds.RecommendedRepositories => 380,
        DashboardWidgetIds.Notifications => 300,
        _ => 260
    };

    private void TrackSection(string section, CacheState cacheState, string result)
    {
        _telemetryService.TrackEvent(
            "dashboard.section.loaded",
            new Dictionary<string, string?>
            {
                ["section"] = section,
                ["source"] = "home",
                ["cache_state"] = cacheState.ToString(),
                ["result"] = result
            });
    }

    private static string L(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);

    private static string LF(string key, string fallback, params object?[] args) =>
        LocalizedResourceText.Format(key, fallback, args);
}
