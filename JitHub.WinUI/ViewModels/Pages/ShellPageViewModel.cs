using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Common;
using JitHub.WinUI.ViewModels.CodeViewer;
using JitHub.WinUI.Views.Controls.Common;
using JitHub.WinUI.Views.Controls.Repo;
using JitHub.WinUI.Views.Pages;
using JitHub.WinUI.Views.Pages.Design;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using DashboardPageType = JitHub.WinUI.Views.Pages.DashboardPage;

namespace JitHub.WinUI.ViewModels.Pages;

public sealed partial class ShellPageViewModel : ViewModelBase
{
    public event EventHandler? SignOutRequested;

    private const string StorePage = "https://www.microsoft.com/store/apps/9MXRBJBB552V";
    private const string AppRepositoryName = "JitHubV2";
    private const string AppRepositoryOwner = "JitHubApp";
    private const int SearchRepositoryPageSize = 8;
    private const int SearchDebounceMilliseconds = 220;

    private readonly NavigationService _navigationService;
    private readonly IGitHubRepositoryQueryService _repositoryQueryService;
    private readonly IGitHubPilotQueryService _gitHubPilotQueryService;
    private readonly IGitHubRepositoryIndexService _repositoryIndexService;
    private readonly ShellStarLibraryProjection _starLibraryProjection;
    private readonly ITelemetryService _telemetryService;
    private readonly IAccountService _accountService;
    private readonly IAuthService _authService;
    private readonly ModalService _modalService;
    private readonly DialogPresentationCoordinator _dialogPresentationCoordinator;
    private readonly NotificationInboxState _notificationInboxState;
    private readonly RepositoryRoutePrefetchCoordinator _routePrefetchCoordinator;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly List<GitHubRepository> _repositoryCache = [];
    private readonly List<GitHubRepository> _automationRepositories = [];
    private DataTransferManager? _shareManager;
    private Window? _shareWindow;
    private ICommand? _openModalCommand;
    private ICommand? _closeModalCommand;
    private FrameworkElement? _content;
    private string _title = string.Empty;
    private bool _useHeader;
    private ObservableCollection<TabViewItem> _pages = [];
    private TabViewItem? _selectedTab;
    private Frame? _contentFrame;
    private string _currentRouteKey = string.Empty;
    private ObservableCollection<ShellCommandSearchResult> _searchResults = [];
    private KeyedObservableCollection<ShellRepositoryItem, GitHubRepository> _repositoryItems = [];
    private bool _searching;
    private bool _isRepositoryRailRefreshing;
    private bool _hasRepositoryRailError;
    private string _repositoryFilterText = string.Empty;
    private ShellRepositoryFilter _repositoryFilter = ShellRepositoryFilter.Public;
    private string _repositoryRailStatusText = ShellText("RepositoryStatus.Ready", "Repositories are ready.");
    private string _activeRepositoryPartition = string.Empty;
    private bool _isRepositoryIndexComplete;
    private bool _isPublicRepositoryPreview;
    private int _starLibraryIndexedCount;
    private bool _isStarLibraryDegraded;
    private string _userDisplayName = "GitHub";
    private string _userSubtitle = "Signed in";
    private GitHubRepository? _activeRepository;
    private int _searchRequestVersion;
    private CancellationTokenSource? _searchCancellationTokenSource;
    private readonly ShellRouteHistory _routeHistory = new();
    private bool _isShellFrameNavigation;
    private Func<ShellRouteViewState?>? _captureRouteViewState;
    private Action<ShellRouteViewState>? _restoreRouteViewState;
    private string? _navigationOriginFocusTargetId;

    public ShellPageViewModel()
    {
        _navigationService = GetService<NavigationService>();
        _repositoryQueryService = GetService<IGitHubRepositoryQueryService>();
        _gitHubPilotQueryService = GetService<IGitHubPilotQueryService>();
        _repositoryIndexService = GetService<IGitHubRepositoryIndexService>();
        _starLibraryProjection = new ShellStarLibraryProjection(GetService<IGitHubStarLibraryService>());
        _telemetryService = SafeTelemetryService.Wrap(GetService<ITelemetryService>());
        _accountService = GetService<IAccountService>();
        _authService = GetService<IAuthService>();
        _modalService = GetService<ModalService>();
        _dialogPresentationCoordinator = GetService<DialogPresentationCoordinator>();
        _modalService.DismissalStateChanged += ModalService_DismissalStateChanged;
        _notificationInboxState = GetService<NotificationInboxState>();
        _routePrefetchCoordinator = GetService<RepositoryRoutePrefetchCoordinator>();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        GlobalViewModel = GetService<GlobalViewModel>();

        GoHomeCommand = new RelayCommand(GoHome);
        NewTabCommand = new RelayCommand(OpenNewHomeTab);
        CloseSelectedTabCommand = new RelayCommand(CloseSelectedTab);
        OpenNewRepositoryCommand = new RelayCommand(OpenNewRepositoryModal);
        GoToSettingsPageCommand = new RelayCommand(GoToSettingsPage);
        GoToProfilePageCommand = new RelayCommand(GoToProfilePage);
        SetRepositoryFilterCommand = new RelayCommand<string?>(SetRepositoryFilter);
        GoBackCommand = new RelayCommand(GoBack, () => CanGoBack);
        GoForwardCommand = new RelayCommand(GoForward, () => CanGoForward);

        NavigationItems =
        [
            new("home", ShellNavigationText("Home", "Home"), "\uE80F", new RelayCommand(GoHome)),
            new("issues", ShellNavigationText("Issues", "Issues"), "\uE8A5", new RelayCommand(OpenMyIssuesPage)),
            new("pull-requests", ShellNavigationText("PullRequests", "Pull Requests"), "\uE8EE", new RelayCommand(OpenMyPullRequestsPage)),
            new("notifications", ShellNavigationText("Notifications", "Notifications"), "\uEA8F", new RelayCommand(OpenNotificationsPage)),
            new("stars", ShellNavigationText("Stars", "Stars"), "\uE734", new RelayCommand(OpenStarsPage)),
            new("gists", ShellNavigationText("Gists", "Gists"), "\uE943", new RelayCommand(OpenGistsPage)),
            new("explore", ShellNavigationText("Search", "Search"), "\uE721", new RelayCommand(FocusCommandSearchRequested))
        ];

        _notificationInboxState.PropertyChanged += NotificationInboxState_PropertyChanged;
        _repositoryIndexService.Changed += RepositoryIndexService_Changed;
        _starLibraryProjection.Changed += StarLibraryProjection_Changed;
        UpdateNotificationBadge();

        _navigationService.RegisterTabTitleChangeEvent(new RelayCommand<string?>(ChangeTabTitle));
    }

    public ObservableCollection<ShellNavigationItem> NavigationItems { get; }

    private static string ShellNavigationText(string key, string fallback) =>
        LocalizedResourceText.GetString($"Shell.Navigation.{key}", fallback);

    private static string ShellText(string key, string fallback) =>
        LocalizedResourceText.GetString($"Shell.{key}", fallback);

    private static string ShellFormat(string key, string fallback, params object?[] arguments) =>
        LocalizedResourceText.Format($"Shell.{key}", fallback, arguments);

    private void NotificationInboxState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(NotificationInboxState.BadgeText))
        {
            UpdateNotificationBadge();
        }
    }

    private void UpdateNotificationBadge()
    {
        ShellNavigationItem? item = NavigationItems.FirstOrDefault(static item => item.Id == "notifications");
        if (item is not null)
        {
            item.BadgeText = _notificationInboxState.BadgeText;
            item.BadgeValue = _notificationInboxState.UnreadCount;
        }
    }

    public GlobalViewModel GlobalViewModel { get; }

    public ICommand GoHomeCommand { get; }

    public ICommand NewTabCommand { get; }

    public ICommand CloseSelectedTabCommand { get; }

    public ICommand OpenNewRepositoryCommand { get; }

    public ICommand GoToSettingsPageCommand { get; }

    public ICommand GoToProfilePageCommand { get; }

    public ICommand SetRepositoryFilterCommand { get; }

    public RelayCommand GoBackCommand { get; }

    public RelayCommand GoForwardCommand { get; }

    public bool CanGoBack => _routeHistory.CanGoBack && _contentFrame?.CanGoBack == true;

    public bool CanGoForward => _routeHistory.CanGoForward && _contentFrame?.CanGoForward == true;

    public ShellRouteViewState? CurrentRouteViewState => _routeHistory.Current?.ViewState;

    public string CurrentRoutePage => _routeHistory.Current?.Identity.Page ?? string.Empty;

    public bool IsCurrentRoute(ShellWorkspaceTabIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (_contentFrame is not null)
        {
            return _contentFrame.Content is not null &&
                string.Equals(_currentRouteKey, identity.Key, StringComparison.Ordinal);
        }

        return SelectedTab?.Content is Frame { Content: not null } &&
            string.Equals(SelectedTab.Tag as string, identity.Key, StringComparison.Ordinal);
    }

    public bool UpdateCurrentRouteViewState(ShellRouteViewState viewState) =>
        _routeHistory.UpdateCurrentViewState(viewState);

    public event EventHandler? CommandSearchFocusRequested;

    public FrameworkElement? Content
    {
        get => _content;
        private set => SetProperty(ref _content, value);
    }

    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }

    public bool UseHeader
    {
        get => _useHeader;
        private set => SetProperty(ref _useHeader, value);
    }

    public ObservableCollection<TabViewItem> Pages
    {
        get => _pages;
        private set => SetProperty(ref _pages, value);
    }

    public TabViewItem? SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    public ObservableCollection<ShellCommandSearchResult> SearchResults
    {
        get => _searchResults;
        private set => SetProperty(ref _searchResults, value);
    }

    public KeyedObservableCollection<ShellRepositoryItem, GitHubRepository> RepositoryItems
    {
        get => _repositoryItems;
        private set => SetProperty(ref _repositoryItems, value);
    }

    public bool Searching
    {
        get => _searching;
        private set => SetProperty(ref _searching, value);
    }

    public bool IsRepositoryRailRefreshing
    {
        get => _isRepositoryRailRefreshing;
        private set => SetProperty(ref _isRepositoryRailRefreshing, value);
    }

    public bool HasRepositoryRailError
    {
        get => _hasRepositoryRailError;
        private set
        {
            if (SetProperty(ref _hasRepositoryRailError, value))
            {
                OnPropertyChanged(nameof(IsRepositoryRailEmpty));
            }
        }
    }

    public string RepositoryFilterText
    {
        get => _repositoryFilterText;
        set
        {
            if (SetProperty(ref _repositoryFilterText, value ?? string.Empty))
            {
                RebuildRepositoryItems();
            }
        }
    }

    public string RepositoryRailStatusText
    {
        get => _repositoryRailStatusText;
        private set => SetProperty(ref _repositoryRailStatusText, value);
    }

    public string UserDisplayName
    {
        get => _userDisplayName;
        private set => SetProperty(ref _userDisplayName, value);
    }

    public string UserSubtitle
    {
        get => _userSubtitle;
        private set => SetProperty(ref _userSubtitle, value);
    }

    public GitHubRepository? ActiveRepository
    {
        get => _activeRepository;
        private set
        {
            if (SetProperty(ref _activeRepository, value))
            {
                UpdateSelectedRepositoryState();
                OnPropertyChanged(nameof(ActiveRepositoryLabel));
            }
        }
    }

    public string ActiveRepositoryLabel => ActiveRepository?.FullName ?? "No repository selected";

    public bool AreRepositoriesVisible => RepositoryItems.Count > 0;

    public bool IsRepositoryRailEmpty =>
        !IsRepositoryRailRefreshing && !HasRepositoryRailError && RepositoryItems.Count == 0;

    public bool IsPublicRepositoryFilterSelected => _repositoryFilter == ShellRepositoryFilter.Public;

    public int StarLibraryIndexedCount
    {
        get => _starLibraryIndexedCount;
        private set => SetProperty(ref _starLibraryIndexedCount, Math.Max(0, value));
    }

    public bool IsStarLibraryDegraded
    {
        get => _isStarLibraryDegraded;
        private set => SetProperty(ref _isStarLibraryDegraded, value);
    }

    public bool IsPrivateRepositoryFilterSelected => _repositoryFilter == ShellRepositoryFilter.Private;

    public bool IsForkedRepositoryFilterSelected => _repositoryFilter == ShellRepositoryFilter.Forked;

    public void InitializeDesktopIntegration(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (ReferenceEquals(_shareWindow, window))
        {
            return;
        }

        if (_shareManager is not null)
        {
            _shareManager.DataRequested -= OnDataRequested;
            _shareManager = null;
        }

        _shareWindow = window;
    }

    public void InitializeContentFrame(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_contentFrame is not null)
        {
            _contentFrame.Navigated -= ContentFrame_Navigated;
        }

        _contentFrame = frame;
        _contentFrame.CacheSize = 16;
        _contentFrame.Navigated += ContentFrame_Navigated;
        _navigationService.ApplicationFrame = frame;
        UpdateHistoryCommands();
    }

    public void InitializeRouteStatePersistence(
        Func<ShellRouteViewState?> capture,
        Action<ShellRouteViewState> restore)
    {
        _captureRouteViewState = capture ?? throw new ArgumentNullException(nameof(capture));
        _restoreRouteViewState = restore ?? throw new ArgumentNullException(nameof(restore));
    }

    public void GoBack()
    {
        if (_contentFrame?.CanGoBack != true)
        {
            return;
        }

        if (!EvaluateModalForNavigation().Accepted)
        {
            ShowNotification("Finish the current dialog action before navigating.");
            return;
        }

        _routePrefetchCoordinator.Cancel();

        CaptureCurrentRouteViewState();
        if (!_routeHistory.TryGoBack(out ShellRouteEntry? route))
        {
            return;
        }

        _isShellFrameNavigation = true;
        _contentFrame.GoBack(new SuppressNavigationTransitionInfo());
        TrackHistoryNavigation("back", route);
    }

    public void GoForward()
    {
        if (_contentFrame?.CanGoForward != true)
        {
            return;
        }

        if (!EvaluateModalForNavigation().Accepted)
        {
            ShowNotification("Finish the current dialog action before navigating.");
            return;
        }

        _routePrefetchCoordinator.Cancel();

        CaptureCurrentRouteViewState();
        if (!_routeHistory.TryGoForward(out ShellRouteEntry? route))
        {
            return;
        }

        _isShellFrameNavigation = true;
        _contentFrame.GoForward();
        TrackHistoryNavigation("forward", route);
    }

    public void LoadApplication(ICommand openModal, ICommand closeModal)
    {
        _openModalCommand = openModal;
        _closeModalCommand = closeModal;
        _modalService.Init(
            new RelayCommand<ModalArg?>(
                OpenModalWithControl,
                arg => arg is not null && _openModalCommand?.CanExecute(null) == true),
            new RelayCommand(
                CloseModalWithControl,
                () => _closeModalCommand?.CanExecute(null) == true));

        if (!_authService.Authenticated && !_authService.CheckAuth(_accountService.GetUser()))
        {
            _navigationService.Unauthorized();
            return;
        }

        EnsureHomeTab();
        RefreshUserDisplay();
        SelectNavigationItem("home");
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        RefreshUserDisplay();
        await RefreshRepositoryRailAsync(cancellationToken);
    }

    public async Task RefreshRepositoryRailAsync(
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            RepositoryRailStatusText = "GitHub authentication is unavailable.";
            HasRepositoryRailError = true;
            _authService.SignOut();
            return;
        }

        _activeRepositoryPartition = GetActiveUserPartition(token);
        _isPublicRepositoryPreview = GitHubAuthenticationConstants.IsPublicAccessToken(token);
        await _starLibraryProjection.SetUserAsync(_activeRepositoryPartition, cancellationToken);
        HasRepositoryRailError = false;
        ApplyRepositoryIndexSnapshot(_repositoryIndexService.GetSnapshot(_activeRepositoryPartition));
        IsRepositoryRailRefreshing = true;

        try
        {
            AccountRepositoryIndexSnapshot cached = await _repositoryIndexService.InitializeAsync(
                token,
                _activeRepositoryPartition,
                cancellationToken);
            ApplyRepositoryIndexSnapshot(cached);
            AccountRepositoryIndexSnapshot result = cached.IsSynchronizing
                ? cached
                : await _repositoryIndexService.SynchronizeAsync(
                    token,
                    _activeRepositoryPartition,
                    cancellationToken,
                    forceRefresh);
            ApplyRepositoryIndexSnapshot(result);
            TrackEvent(
                "shell.rail.refresh.completed",
                new Dictionary<string, string?>
                {
                    ["resource"] = GitHubCachePolicy.RepositoryResource,
                    ["source"] = "shell",
                    ["cache_state"] = result.CacheState.ToString(),
                    ["result"] = "success"
                });
        }
        catch (GitHubAuthenticationException)
        {
            RepositoryRailStatusText = "GitHub authentication is unavailable.";
            HasRepositoryRailError = true;
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            RepositoryRailStatusText = JitHub.WinUI.Helpers.UserFacingError.For(
                ex,
                JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                "shell-repositories");
            HasRepositoryRailError = true;
            TrackRailRefreshFailure(nameof(GitHubApiException));
        }
        catch (HttpRequestException)
        {
            RepositoryRailStatusText = _repositoryCache.Count == 0
                ? "JitHub could not reach GitHub to load repositories."
                : "Showing cached repositories.";
            HasRepositoryRailError = true;
            TrackRailRefreshFailure(nameof(HttpRequestException));
        }
        finally
        {
            IsRepositoryRailRefreshing = false;
            NotifyRepositoryVisibilityChanged();
        }
    }

    public async Task UpdateCommandSearchAsync(
        string? query,
        bool forceImmediate = false,
        CancellationToken cancellationToken = default)
    {
        int requestVersion = Interlocked.Increment(ref _searchRequestVersion);
        _searchCancellationTokenSource?.Cancel();
        _searchCancellationTokenSource?.Dispose();
        _searchCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = _searchCancellationTokenSource.Token;

        string term = query?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(term))
        {
            Searching = false;
            SearchResults = BuildDefaultSearchResults();
            return;
        }

        if (!forceImmediate)
        {
            try
            {
                await Task.Delay(SearchDebounceMilliseconds, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        Stopwatch searchDuration = Stopwatch.StartNew();
        List<ShellCommandSearchResult> results = BuildCommandResults(term).ToList();
        results.Add(CreateSearchQueryResult(term));

        string? accessToken = GetActiveToken();
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            Searching = true;
            try
            {
                CachedResult<GitHubRepository[]> repositoryResult = _automationRepositories.Count > 0
                    ? new CachedResult<GitHubRepository[]>(_automationRepositories.ToArray(), CacheState.Fresh, DateTimeOffset.Now, DateTimeOffset.Now.AddMinutes(15))
                    : await _gitHubPilotQueryService.SearchRepositoriesAsync(
                        accessToken,
                        GetActiveUserPartition(accessToken),
                        term,
                        SearchRepositoryPageSize,
                        cancellationToken: token);

                token.ThrowIfCancellationRequested();

                results.AddRange((repositoryResult.Value ?? [])
                    .Take(SearchRepositoryPageSize)
                    .Select(CreateRepositorySearchResult));

                TrackSearchCompletion(
                    TelemetryTaxonomy.Results.Success,
                    searchDuration.Elapsed,
                    repositoryResult.CacheState);
            }
            catch (OperationCanceledException) when (requestVersion != _searchRequestVersion)
            {
                TrackSearchCompletion(TelemetryTaxonomy.Results.Cancelled, searchDuration.Elapsed);
                return;
            }
            catch (OperationCanceledException)
            {
                TrackSearchCompletion(TelemetryTaxonomy.Results.Cancelled, searchDuration.Elapsed);
                throw;
            }
            catch (GitHubAuthenticationException)
            {
                TrackSearchCompletion(
                    TelemetryTaxonomy.Results.AuthError,
                    searchDuration.Elapsed,
                    errorKind: "authentication");
                _authService.SignOut();
            }
            catch (GitHubApiException)
            {
                TrackSearchCompletion(
                    TelemetryTaxonomy.Results.Error,
                    searchDuration.Elapsed,
                    errorKind: "api");
            }
            catch (HttpRequestException)
            {
                TrackSearchCompletion(
                    TelemetryTaxonomy.Results.Error,
                    searchDuration.Elapsed,
                    errorKind: "network");
            }
            finally
            {
                if (requestVersion == _searchRequestVersion)
                {
                    Searching = false;
                }
            }
        }
        else
        {
            TrackSearchCompletion(
                TelemetryTaxonomy.Results.AuthError,
                searchDuration.Elapsed,
                errorKind: "authentication");
        }

        if (requestVersion == _searchRequestVersion)
        {
            token.ThrowIfCancellationRequested();
            SearchResults = new ObservableCollection<ShellCommandSearchResult>(
                results
                    .OrderByDescending(static result => result.Score)
                    .ThenBy(static result => result.Kind)
                    .ThenBy(static result => result.Title, StringComparer.CurrentCultureIgnoreCase)
                    .Take(12));
        }
    }

    public void TrackCommandSearchOpened()
    {
        TrackEvent(
            "shell.command.opened",
            new Dictionary<string, string?>
            {
                ["source"] = "shell",
                ["feature"] = "command_search"
            });
    }

    public void TrackShellCommand(string action, string result) =>
        TrackEvent(
            "shell.command.executed",
            new Dictionary<string, string?>
            {
                ["action"] = action,
                ["source"] = TelemetryTaxonomy.Sources.Shell,
                ["result"] = result
            });

    public void ExecuteSearchResult(ShellCommandSearchResult? result)
    {
        if (result is null)
        {
            return;
        }

        if (result.Kind == ShellCommandSearchResultKind.SearchQuery)
        {
            TrackEvent(
                "shell.search.submitted",
                new Dictionary<string, string?>
                {
                    ["source"] = "command_search",
                    ["resource"] = "repositories",
                    ["result"] = "submitted"
                });
        }
        if (!result.Command.CanExecute(result.Payload))
        {
            TrackEvent(
                "shell.command.executed",
                new Dictionary<string, string?>
                {
                    ["feature"] = result.KindLabel.ToLowerInvariant(),
                    ["source"] = "shell",
                    ["result"] = "unavailable"
                });
            return;
        }

        try
        {
            result.Command.Execute(result.Payload);
            TrackEvent(
                "shell.command.executed",
                new Dictionary<string, string?>
                {
                    ["feature"] = result.KindLabel.ToLowerInvariant(),
                    ["source"] = "shell",
                    ["result"] = "success"
                });
        }
        catch
        {
            TrackEvent(
                "shell.command.executed",
                new Dictionary<string, string?>
                {
                    ["feature"] = result.KindLabel.ToLowerInvariant(),
                    ["source"] = "shell",
                    ["result"] = "error"
                });
            throw;
        }
    }

    public void SetAutomationSearchResults(IReadOnlyList<GitHubRepository> repositories)
    {
        _automationRepositories.Clear();
        _automationRepositories.AddRange(repositories);
        SearchResults = new ObservableCollection<ShellCommandSearchResult>(
            repositories.Select(CreateRepositorySearchResult));
    }

    public void FocusCommandSearchRequested()
    {
        CommandSearchFocusRequested?.Invoke(this, EventArgs.Empty);
        TrackEvent(
            "shell.nav.opened",
            new Dictionary<string, string?>
            {
                ["page"] = "explore",
                ["source"] = "shell"
            });
    }

    public void OnAddTab(TabView sender, object args)
    {
        OpenNewHomeTab();
    }

    public void OnTabClose(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (args.Item is TabViewItem item)
        {
            CloseTab(item);
        }
    }

    public void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _navigationService.ApplicationFrame = null;
        if (e.AddedItems.FirstOrDefault() is TabViewItem { Content: Frame frame } item)
        {
            _navigationService.ApplicationFrame = frame;
            SelectNavigationForTab(item);
        }
    }

    public void OpenNewHomeTab()
    {
        OpenTab(
            ShellWorkspaceTabIdentity.Home(),
            TitleForPage(typeof(DashboardPageType)),
            typeof(DashboardPageType),
            null,
            focusExisting: false);
    }

    public void CloseSelectedTab()
    {
        if (SelectedTab is not null)
        {
            CloseTab(SelectedTab);
        }
    }

    public void SelectNextTab(int step)
    {
        if (Pages.Count <= 1 || SelectedTab is null)
        {
            return;
        }

        int index = Pages.IndexOf(SelectedTab);
        if (index < 0)
        {
            SelectedTab = Pages[0];
            return;
        }

        int next = (index + step) % Pages.Count;
        if (next < 0)
        {
            next += Pages.Count;
        }

        SelectedTab = Pages[next];
    }

    public void GoHome()
    {
        if (EnsureHomeTab())
        {
            TrackEvent(
                "shell.nav.opened",
                new Dictionary<string, string?>
                {
                    ["page"] = "home",
                    ["source"] = TelemetryTaxonomy.Sources.Shell,
                    ["result"] = TelemetryTaxonomy.Results.Success
                });
        }
    }

    public void GoToSettingsPage() => TryOpenSettingsPage();

    public bool TryOpenSettingsPage() =>
        OpenTab(ShellWorkspaceTabIdentity.Settings(), TitleForPage(typeof(SettingsPage)), typeof(SettingsPage), null);

    public void OpenMyIssuesPage() => TryOpenMyIssuesPage();

    public bool TryOpenMyIssuesPage() =>
        OpenTab(new ShellWorkspaceTabIdentity("my-issues", "issues"), TitleForPage(typeof(MyIssuesPage)), typeof(MyIssuesPage), null);

    public void OpenMyPullRequestsPage() => TryOpenMyPullRequestsPage();

    public bool TryOpenMyPullRequestsPage() =>
        OpenTab(new ShellWorkspaceTabIdentity("my-pull-requests", "pull-requests"), TitleForPage(typeof(MyPullRequestsPage)), typeof(MyPullRequestsPage), null);

    public void OpenStarsPage() => TryOpenStarsPage();

    public bool TryOpenStarsPage() =>
        OpenTab(new ShellWorkspaceTabIdentity("stars", "stars"), TitleForPage(typeof(StarsPage)), typeof(StarsPage), null);

    public void OpenGistsPage() => TryOpenGistsPage();

    public bool TryOpenGistsPage() =>
        OpenTab(new ShellWorkspaceTabIdentity("gists", "gists"), TitleForPage(typeof(GistsPage)), typeof(GistsPage), null);

    public void GoToDesignLabPage()
    {
        if (!DeveloperRoutePolicy.CanOpenDesignLab(
                GlobalViewModel.DevMode,
                hasIsolatedAutomationRoots: false))
        {
            return;
        }

        OpenTab(ShellWorkspaceTabIdentity.DesignLab(), ShellText("Route.DesignLab", "Design Lab"), typeof(DesignLabPage), null);
    }

    public void GoToProfilePage()
    {
        OpenAuthenticatedProfile();
    }

    public void OpenNotificationsPage() => TryOpenNotificationsPage();

    public bool TryOpenNotificationsPage() =>
        OpenTab(
            new ShellWorkspaceTabIdentity("notifications", "notifications"),
            TitleForPage(typeof(NotificationsPage)),
            typeof(NotificationsPage),
            null);

    public void OpenNotification(GitHubNotificationThread? notification, string source)
    {
        if (notification is null || string.IsNullOrWhiteSpace(notification.Repository.FullName))
        {
            ShowNotification("This notification does not have an available destination.");
            return;
        }

        GitHubRepository repository = notification.Repository;
        RepoPageType? page = null;
        PageNavArg? pageArg = null;
        if (NotificationDestinationPolicy.TryResolveInternal(notification, out NotificationInternalDestination internalDestination))
        {
            (page, pageArg) = internalDestination.Kind switch
            {
                NotificationInternalDestinationKind.Issue =>
                    ((RepoPageType?)RepoPageType.IssuePage, (PageNavArg)CreateNotificationIssueNavigation(
                        notification,
                        repository,
                        internalDestination.Number)),
                NotificationInternalDestinationKind.PullRequest =>
                    ((RepoPageType?)RepoPageType.PullRequestPage, (PageNavArg)new PullRequestPageNavArg(repository, internalDestination.Number)),
                NotificationInternalDestinationKind.Commit =>
                    ((RepoPageType?)RepoPageType.CommitPage, (PageNavArg)CommitPageNavArg.CreateWithGitRef(repository, internalDestination.GitRef)),
                _ => ((RepoPageType?)null, (PageNavArg?)null)
            };
        }
        bool opened;
        if (page is not null && pageArg is not null)
        {
            opened = OpenRepositoryTarget(repository, page.Value, pageArg);
        }
        else if (NotificationDestinationPolicy.ResolveWebUri(notification) is Uri destination)
        {
            _ = OpenExternalNotificationAsync(destination, source);
            return;
        }
        else
        {
            ShowNotification("This notification type does not have an available destination.");
            return;
        }

        TrackEvent(
            "notifications.action.executed",
            new Dictionary<string, string?>
            {
                ["action"] = "open",
                ["source"] = source,
                ["result"] = TelemetryTaxonomy.NavigationResult(opened)
            });
    }

    private IssueNavArg CreateNotificationIssueNavigation(
        GitHubNotificationThread notification,
        GitHubRepository repository,
        int issueNumber)
    {
        string repositoryUrl = string.IsNullOrWhiteSpace(repository.HtmlUrl)
            ? $"https://github.com/{repository.FullName}"
            : repository.HtmlUrl.TrimEnd('/');
        IssueNavArg navigation = new(repository, issueNumber)
        {
            IsNotificationHandoff = true,
            NavigationPreview = new GitHubIssue
            {
                Number = issueNumber,
                Title = notification.Subject.Title,
                HtmlUrl = $"{repositoryUrl}/issues/{issueNumber}",
                UpdatedAt = notification.UpdatedAt ?? DateTimeOffset.UtcNow
            }
        };
        string? token = GetActiveToken();
        if (issueNumber <= 0 ||
            string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(repository.Owner?.Login) ||
            string.IsNullOrWhiteSpace(repository.Name))
        {
            return navigation;
        }

        string userPartition = GetActiveUserPartition(token);
        IIssueNavigationCache cache = GetService<IIssueNavigationCache>();
        if (cache.TryGet(
                userPartition,
                repository.Owner.Login,
                repository.Name,
                issueNumber,
                out _))
        {
            return navigation;
        }

        cache.Store(
            userPartition,
            new IssueNavigationSnapshot(
                repository.Owner.Login,
                repository.Name,
                issueNumber,
                navigation.NavigationPreview!,
                [],
                DateTimeOffset.UtcNow,
                "notification-preview"));
        return navigation;
    }

    public async Task PrefetchNotificationAsync(
        GitHubNotificationThread notification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(notification.Repository.FullName) ||
            !NotificationDestinationPolicy.TryResolveInternal(
                notification,
                out NotificationInternalDestination destination))
        {
            return;
        }

        string[] repositoryParts = notification.Repository.FullName.Split(
            '/',
            2,
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (repositoryParts.Length != 2)
        {
            return;
        }

        string userPartition = GetActiveUserPartition(token);
        try
        {
            switch (destination.Kind)
            {
                case NotificationInternalDestinationKind.Issue:
                    _ = await GetService<IIssueNavigationCache>().PrefetchAsync(
                        token,
                        userPartition,
                        repositoryParts[0],
                        repositoryParts[1],
                        destination.Number,
                        IssuePrefetchReason.Hover,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case NotificationInternalDestinationKind.PullRequest:
                    _ = await GetService<IPullRequestNavigationCache>().PrefetchAsync(
                        token,
                        userPartition,
                        repositoryParts[0],
                        repositoryParts[1],
                        destination.Number,
                        PullRequestPrefetchReason.Hover,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case NotificationInternalDestinationKind.Commit
                    when !string.IsNullOrWhiteSpace(destination.GitRef):
                    await GetService<ICommitNavigationCache>().PrefetchAsync(
                        token,
                        userPartition,
                        repositoryParts[0],
                        repositoryParts[1],
                        destination.GitRef,
                        CommitPrefetchReason.Hover,
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Hover prediction is opportunistic; normal stale-first navigation
            // remains authoritative when prediction misses.
        }
    }

    public async Task PrefetchRepositoryCodeAsync(
        GitHubRepository repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        string owner = repository.Owner?.Login?.Trim() ?? string.Empty;
        string name = repository.Name?.Trim() ?? string.Empty;
        string gitRef = repository.DefaultBranch?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(gitRef))
        {
            return;
        }

        try
        {
            await GetService<RepoCodeNavigationPreparationCache>()
                .PrefetchAsync(owner, name, gitRef, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Repository prediction is opportunistic. Normal stale-first navigation
            // remains authoritative when a hover/focus prediction misses.
        }
    }

    private async Task OpenExternalNotificationAsync(Uri destination, string source)
    {
        try
        {
            bool opened = await Windows.System.Launcher.LaunchUriAsync(destination);
            if (!opened)
            {
                ShowNotification("GitHub could not be opened for this notification.");
                TrackNotificationOpen(source, "error");
                return;
            }

            TrackNotificationOpen(source, "success");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to open notification destination: {ex}");
            ShowNotification("GitHub could not be opened for this notification.");
            TrackNotificationOpen(source, "error");
        }
    }

    private void TrackNotificationOpen(string source, string result) =>
        TrackEvent(
            "notifications.action.executed",
            new Dictionary<string, string?>
            {
                ["action"] = "open",
                ["source"] = source,
                ["result"] = result
            });

    public void OpenAuthenticatedProfile() => TryOpenAuthenticatedProfile();

    public bool TryOpenAuthenticatedProfile()
    {
        string login = _authService.AuthenticatedUser?.Login ?? string.Empty;
        return OpenUserProfile(login, TelemetryTaxonomy.Sources.Shell);
    }

    public bool OpenUserProfile(string login, string source, string? originFocusTargetId = null)
    {
        string normalizedLogin = login.Trim();
        string header = string.IsNullOrWhiteSpace(normalizedLogin)
            ? "User Profile"
            : $"@{normalizedLogin}";
        _navigationOriginFocusTargetId = string.IsNullOrWhiteSpace(originFocusTargetId)
            ? null
            : originFocusTargetId.Trim();
        try
        {
            return OpenTab(
                ShellWorkspaceTabIdentity.Profile(normalizedLogin),
                header,
                typeof(ProfilePage),
                new UserProfilePageArgs(normalizedLogin, Source: source));
        }
        finally
        {
            _navigationOriginFocusTargetId = null;
        }
    }

    public void GoToFeedbackPage()
    {
        GitHubRepository repository = CreateMinimalRepository($"{AppRepositoryOwner}/{AppRepositoryName}", "main");
        OpenRepositoryPage(repository, RepoPageType.IssuePage, new IssueNavArg(repository, 0));
    }

    public async Task<RepoDetailPageArgs?> GetFeedbackNavigationArgsAsync(
        CancellationToken cancellationToken = default)
    {
        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        try
        {
            CachedResult<GitHubRepository> result = await _repositoryQueryService.GetRepositoryAsync(
                token,
                GetActiveUserPartition(token),
                AppRepositoryOwner,
                AppRepositoryName,
                QueryFetchPolicy.StaleFirst,
                GitHubRequestPriority.Visible,
                cancellationToken);
            GitHubRepository? repository = result.Value;
            if (repository is null)
            {
                return null;
            }

            TrackEvent(
                "shell.route.opened",
                new Dictionary<string, string?>
                {
                    ["page"] = "feedback",
                    ["source"] = "shell",
                    ["result"] = "success",
                    ["cache_state"] = result.CacheState.ToString().ToLowerInvariant()
                });
            return new RepoDetailPageArgs(
                RepoPageType.IssuePage,
                new IssueNavArg(repository, 0),
                repository);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            TrackEvent(
                "shell.route.opened",
                new Dictionary<string, string?>
                {
                    ["page"] = "feedback",
                    ["source"] = "shell",
                    ["result"] = "error"
                });
            return null;
        }
    }

    public void OpenRepository(GitHubRepository? repo)
    {
        _ = TryOpenRepository(repo);
    }

    public bool TryOpenRepository(GitHubRepository? repo)
    {
        if (repo is null)
        {
            return false;
        }

        return OpenRepositoryPage(repo, RepoPageType.CodePage, CodeViewerNavArg.CreateWithBranch(repo, repo.DefaultBranch));
    }

    private void OpenRepositoryFromRail(GitHubRepository repo)
    {
        bool opened = TryOpenRepository(repo);
        TrackEvent(
            "shell.repo.selected",
            new Dictionary<string, string?>
            {
                ["page"] = "code",
                ["source"] = "shell",
                ["result"] = TelemetryTaxonomy.NavigationResult(opened)
            });
    }

    public bool OpenRepositoryPage(string fullName, string? page, string? branch)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return false;
        }

        GitHubRepository repository = CreateMinimalRepository(fullName, branch);
        RepoPageType pageType = ResolveRepositoryPageType(page);
        PageNavArg pageArg = pageType switch
        {
            RepoPageType.IssuePage => new IssueNavArg(repository, 0),
            RepoPageType.PullRequestPage => new PullRequestPageNavArg(repository, 0),
            RepoPageType.CommitPage => CommitPageNavArg.CreateWithBranch(repository, branch),
            _ => CodeViewerNavArg.CreateWithBranch(repository, branch)
        };

        return OpenRepositoryPage(repository, pageType, pageArg, branch);
    }

    public void OpenSearchQuery(string? queryText)
    {
        string term = queryText?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(term))
        {
            return;
        }

        OpenTab(
            ShellWorkspaceTabIdentity.Search(term),
            $"Search: {term}",
            typeof(RepoSearchResultPage),
            term);
    }

    public void ShowNotification(string? message)
    {
        NotificationMessage = string.IsNullOrWhiteSpace(message)
            ? "JitHub has an update."
            : message;
        IsNotificationOpen = true;
    }

    public void OpenModalWithControl(ModalArg? arg)
    {
        if (arg is null)
        {
            return;
        }

        Content = arg.Content;
        Title = arg.Title;
        UseHeader = arg.UseHeader;
        try
        {
            if (_openModalCommand?.CanExecute(null) != true)
            {
                throw new InvalidOperationException("The shell modal host is not available.");
            }

            _openModalCommand.Execute(null);
        }
        catch
        {
            Content = null;
            Title = string.Empty;
            UseHeader = false;
            throw;
        }
    }

    public void CloseModalWithControl()
    {
        if (_closeModalCommand?.CanExecute(null) == true)
        {
            _closeModalCommand.Execute(null);
        }

        Content = null;
        Title = string.Empty;
        UseHeader = false;
    }

    public void RequestCloseModal()
    {
        _modalService.Close();
    }

    public bool IsModalDismissEnabled => _modalService.CanDismiss;

    private void ModalService_DismissalStateChanged(object? sender, EventArgs e)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            OnPropertyChanged(nameof(IsModalDismissEnabled));
            return;
        }

        _ = _dispatcherQueue.TryEnqueue(() => OnPropertyChanged(nameof(IsModalDismissEnabled)));
    }

    public void OnShareJitHub()
    {
        if (_shareWindow is null)
        {
            throw new InvalidOperationException("Share UI is not initialized.");
        }

        EnsureShareManager();
        DesktopDataTransferManagerHelper.ShowShareUIForWindow(_shareWindow);
    }

    public void OnSignOut()
    {
        SignOutRequested?.Invoke(this, EventArgs.Empty);
    }

    public void OnOpenDevConsole()
    {
        if (!DeveloperRoutePolicy.CanOpenDevConsole(GlobalViewModel.DevMode))
        {
            return;
        }

        _modalService.Open("Dev Console", new DevConsole());
    }

    public bool IsNotificationOpen
    {
        get => _isNotificationOpen;
        set => SetProperty(ref _isNotificationOpen, value);
    }

    private bool _isNotificationOpen;

    public string NotificationMessage
    {
        get => _notificationMessage;
        set => SetProperty(ref _notificationMessage, value);
    }

    private string _notificationMessage = ShellText("Notification.Update", "JitHub has an update.");

    private bool EnsureHomeTab() =>
        OpenTab(ShellWorkspaceTabIdentity.Home(), TitleForPage(typeof(DashboardPageType)), typeof(DashboardPageType), null);

    private bool OpenRepositoryPage(GitHubRepository repository, RepoPageType page, PageNavArg pageArg, string? branch = null)
    {
        ProductPerformanceReadiness.RecordTraversalStage("shell.repository.begin");
        string header = string.IsNullOrWhiteSpace(repository.FullName) ? repository.Name : repository.FullName;
        Type pageSource = page == RepoPageType.IssuePage
            ? typeof(RepoIssuePage)
            : typeof(RepoDetailPage);
        object navigationParameter = page == RepoPageType.IssuePage
            ? pageArg.WithRepo(repository)
            : new RepoDetailPageArgs(page, pageArg, repository);
        bool opened = OpenTab(
            ShellWorkspaceTabIdentity.Repository(repository, page, branch ?? repository.DefaultBranch),
            header,
            pageSource,
            navigationParameter);
        ProductPerformanceReadiness.RecordTraversalStage("shell.repository.opened");
        if (opened)
        {
            ActiveRepository = repository;
            StartRepositoryNavigationPrefetch(repository, page, branch);
            ProductPerformanceReadiness.RecordTraversalStage("shell.repository.prefetch_queued");
        }

        return opened;
    }

    private void StartRepositoryNavigationPrefetch(
        GitHubRepository repository,
        RepoPageType page,
        string? branch)
    {
        if (page is not (RepoPageType.CodePage or RepoPageType.CommitPage) ||
            string.IsNullOrWhiteSpace(repository.Owner?.Login) ||
            string.IsNullOrWhiteSpace(repository.Name) ||
            GetActiveToken() is not { } token)
        {
            return;
        }

        string accountPartition = GetActiveUserPartition(token);
        string gitRef = string.IsNullOrWhiteSpace(branch)
            ? repository.DefaultBranch
            : branch;
        try
        {
            if (page == RepoPageType.CodePage)
            {
                _ = _routePrefetchCoordinator.StartCodeAsync(
                    token,
                    accountPartition,
                    repository.Owner.Login,
                    repository.Name,
                    gitRef);
            }
            else
            {
                _ = _routePrefetchCoordinator.StartCommitsAsync(
                    token,
                    accountPartition,
                    repository.Owner.Login,
                    repository.Name,
                    gitRef);
            }
        }
        catch (ArgumentException)
        {
            // Route prediction requires a stable account partition. The destination
            // page remains authoritative while authentication is settling.
        }
    }

    private bool OpenTab(
        ShellWorkspaceTabIdentity identity,
        string header,
        Type pageSource,
        object? parameter,
        bool focusExisting = true)
    {
        ShellNavigationAttempt modalAttempt = EvaluateModalForNavigation();
        if (!modalAttempt.Accepted)
        {
            ShowNotification("Finish the current dialog action before navigating.");
            TrackRouteOutcome(identity.Page, modalAttempt.Result);
            return false;
        }

        _routePrefetchCoordinator.Cancel();

        if (_contentFrame is not null)
        {
            if (focusExisting &&
                string.Equals(_currentRouteKey, identity.Key, StringComparison.Ordinal) &&
                _contentFrame.Content is not null)
            {
                TrackRouteOutcome(identity.Page, TelemetryTaxonomy.Results.Success);
                return true;
            }

            _navigationService.ApplicationFrame = _contentFrame;
            ProductPerformanceReadiness.RecordTraversalStage("shell.capture.begin");
            CaptureCurrentRouteViewState();
            ProductPerformanceReadiness.RecordTraversalStage("shell.capture.end");
            _isShellFrameNavigation = true;
            ProductPerformanceReadiness.RecordTraversalStage("shell.frame.navigate.begin");
            ShellNavigationAttempt navigationAttempt = ShellNavigationAttempt.Navigate(
                () => _contentFrame.Navigate(
                    pageSource,
                    parameter,
                    new SuppressNavigationTransitionInfo()));
            ProductPerformanceReadiness.RecordTraversalStage("shell.frame.navigate.end");
            _isShellFrameNavigation = false;
            if (!navigationAttempt.Accepted)
            {
                TrackRouteOutcome(identity.Page, navigationAttempt.Result);
                return false;
            }

            _routeHistory.Push(new ShellRouteEntry(identity, header, pageSource, parameter));
            ApplyCurrentHistoryRoute();
            RestoreCurrentRouteViewState();
            ProductPerformanceReadiness.RecordTraversalStage("shell.history.applied");
            TrackRouteOutcome(identity.Page, TelemetryTaxonomy.Results.Success);
            return true;
        }

        Frame frame = new()
        {
            ContentTransitions = null
        };
        TabViewItem tab = new()
        {
            Header = header,
            Tag = identity.Key,
            Content = frame
        };

        ShellNavigationAttempt fallbackAttempt = ShellNavigationAttempt.Navigate(
            () => frame.Navigate(pageSource, parameter, new SuppressNavigationTransitionInfo()));
        if (!fallbackAttempt.Accepted)
        {
            TrackRouteOutcome(identity.Page, fallbackAttempt.Result);
            return false;
        }

        Pages.Add(tab);
        SelectedTab = tab;
        TrackRouteOutcome(identity.Page, TelemetryTaxonomy.Results.Success);
        return true;
    }

    private ShellNavigationAttempt EvaluateModalForNavigation() =>
        ShellNavigationAttempt.EvaluateModal(
            _modalService.IsOpen,
            _dialogPresentationCoordinator.ActiveKind == DialogPresentationKind.NativeContentDialog,
            () => _modalService.TryClose(expectedSession: null));

    private void TrackRouteOutcome(string page, string result) =>
        TrackEvent(
            "shell.route.opened",
            new Dictionary<string, string?>
            {
                ["page"] = page,
                ["source"] = TelemetryTaxonomy.Sources.Shell,
                ["result"] = result
            });

    private void CloseTab(TabViewItem item)
    {
        Pages.Remove(item);

        if (Pages.Count == 0)
        {
            EnsureHomeTab();
        }
    }

    private void ChangeTabTitle(string? title)
    {
        if (title is not null && _contentFrame is not null)
        {
            Title = title;
            return;
        }

        if (SelectedTab is not null && title is not null)
        {
            SelectedTab.Header = title;
        }
    }

    public void OpenActiveRepositoryIssues() =>
        TryOpenActiveRepositoryPage(RepoPageType.IssuePage);

    public void OpenActiveRepositoryPullRequests() =>
        TryOpenActiveRepositoryPage(RepoPageType.PullRequestPage);

    public void OpenActiveRepositoryCommits() =>
        TryOpenActiveRepositoryPage(RepoPageType.CommitPage);

    public bool TryOpenActiveRepositoryIssues() =>
        TryOpenActiveRepositoryPage(RepoPageType.IssuePage);

    public bool TryOpenActiveRepositoryPullRequests() =>
        TryOpenActiveRepositoryPage(RepoPageType.PullRequestPage);

    public bool TryOpenActiveRepositoryCommits() =>
        TryOpenActiveRepositoryPage(RepoPageType.CommitPage);

    private bool TryOpenActiveRepositoryPage(RepoPageType pageType)
    {
        if (ActiveRepository is null)
        {
            ShowNotification("Select a repository first.");
            return false;
        }

        PageNavArg pageArg = pageType switch
        {
            RepoPageType.IssuePage => new IssueNavArg(ActiveRepository, 0),
            RepoPageType.PullRequestPage => new PullRequestPageNavArg(ActiveRepository, 0),
            RepoPageType.CommitPage => CommitPageNavArg.CreateWithBranch(ActiveRepository, ActiveRepository.DefaultBranch),
            _ => CodeViewerNavArg.CreateWithBranch(ActiveRepository, ActiveRepository.DefaultBranch)
        };
        return OpenRepositoryPage(ActiveRepository, pageType, pageArg);
    }

    public bool OpenRepositoryTarget(GitHubRepository repository, RepoPageType page, PageNavArg pageArg, string? branch = null) =>
        OpenRepositoryPage(repository, page, pageArg, branch);

    public bool OpenRepositoryTarget(string fullName, RepoPageType page, PageNavArg pageArg, string? branch = null)
    {
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return false;
        }

        GitHubRepository repository = CreateMinimalRepository(fullName, branch);
        string header = string.IsNullOrWhiteSpace(repository.FullName) ? repository.Name : repository.FullName;
        bool opened = OpenTab(
            ShellWorkspaceTabIdentity.Repository(repository.FullName, page, branch ?? repository.DefaultBranch),
            header,
            typeof(RepoDetailPage),
            new RepoDetailPageArgs(page, pageArg, repository.FullName));
        if (opened)
        {
            ActiveRepository = repository;
        }

        return opened;
    }

    public void OpenNewRepositoryModal()
    {
        _ = TryOpenNewRepositoryModal();
    }

    public bool TryOpenNewRepositoryModal() =>
        _modalService.TryOpenSession(
            "New Repository",
            new RepoForm(new AsyncRelayCommand(() => RefreshRepositoryRailAsync(forceRefresh: true)))) is not null;

    private void SetRepositoryFilter(string? value)
    {
        ShellRepositoryFilter next = value?.Trim().ToLowerInvariant() switch
        {
            "private" => ShellRepositoryFilter.Private,
            "forked" or "forks" => ShellRepositoryFilter.Forked,
            _ => ShellRepositoryFilter.Public
        };

        if (_repositoryFilter == next)
        {
            return;
        }

        _repositoryFilter = next;
        NotifyRepositoryFilterChanged();
        RebuildRepositoryItems();
    }

    private void RebuildRepositoryItems()
    {
        string filterText = RepositoryFilterText.Trim();
        IEnumerable<GitHubRepository> repositories = _repositoryCache;

        repositories = _repositoryFilter switch
        {
            ShellRepositoryFilter.Private => repositories.Where(static repo => repo.Private && !repo.Fork),
            ShellRepositoryFilter.Forked => repositories.Where(static repo => repo.Fork),
            _ => repositories.Where(static repo => !repo.Private && !repo.Fork)
        };

        if (!string.IsNullOrWhiteSpace(filterText))
        {
            repositories = repositories.Where(repo =>
                repo.FullName.Contains(filterText, StringComparison.CurrentCultureIgnoreCase) ||
                repo.Name.Contains(filterText, StringComparison.CurrentCultureIgnoreCase) ||
                repo.Owner.Login.Contains(filterText, StringComparison.CurrentCultureIgnoreCase));
        }

        RepositoryItems.ApplySnapshot(
            repositories,
            RepositoryLibraryProjection.RepositoryKey,
            static item => item.Key,
            repository => new ShellRepositoryItem(repository, OpenRepositoryFromRail)
            {
                IsSelected = IsSameRepository(repository, ActiveRepository)
            },
            (item, repository) =>
            {
                bool updated = item.Update(repository);
                item.IsSelected = IsSameRepository(repository, ActiveRepository);
                return updated;
            });
        RepositoryRailStatusText = FormatRepositoryRailStatus(RepositoryItems.Count);
        NotifyRepositoryVisibilityChanged();
    }

    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.Content is Page page)
        {
            page.NavigationCacheMode = Microsoft.UI.Xaml.Navigation.NavigationCacheMode.Required;
        }

        bool shellFrameNavigation = _isShellFrameNavigation;
        _isShellFrameNavigation = false;
        if (!shellFrameNavigation)
        {
            ShellWorkspaceTabIdentity identity = IdentityForPage(e.SourcePageType, e.Parameter);
            _routeHistory.Push(new ShellRouteEntry(identity, TitleForPage(e.SourcePageType), e.SourcePageType, e.Parameter));
        }

        ApplyCurrentHistoryRoute();
        RestoreCurrentRouteViewState();
    }

    private void ApplyCurrentHistoryRoute()
    {
        ShellRouteEntry? route = _routeHistory.Current;
        if (route is null)
        {
            UpdateHistoryCommands();
            return;
        }

        _currentRouteKey = route.Identity.Key;
        Title = route.Header;
        SelectNavigationItem(ShellWorkspaceTabIdentity.NavigationItemId(route.Identity.Page));
        ActiveRepository = route.Parameter switch
        {
            RepoDetailPageArgs detail when detail.Repo is not null => detail.Repo,
            PageNavArg pageArg when !string.IsNullOrWhiteSpace(pageArg.Repo.FullName) => pageArg.Repo,
            _ => ActiveRepository
        };
        UpdateHistoryCommands();
    }

    private void UpdateHistoryCommands()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    private void TrackHistoryNavigation(string action, ShellRouteEntry? route)
    {
        TrackEvent(
            "shell.route.opened",
            new Dictionary<string, string?>
            {
                ["page"] = route?.Identity.Page ?? "unknown",
                ["source"] = "history",
                ["action"] = action,
                ["result"] = "success"
            });
    }

    private void CaptureCurrentRouteViewState()
    {
        ShellRouteViewState? viewState = _captureRouteViewState?.Invoke();
        if (!string.IsNullOrWhiteSpace(_navigationOriginFocusTargetId))
        {
            viewState = viewState is null
                ? new ShellRouteViewState(
                    SelectedIndex: null,
                    VerticalOffset: 0,
                    HorizontalOffset: 0,
                    FocusTargetId: _navigationOriginFocusTargetId)
                : viewState with { FocusTargetId = _navigationOriginFocusTargetId };
        }

        if (viewState is not null)
        {
            _routeHistory.UpdateCurrentViewState(viewState);
        }
    }

    private void RestoreCurrentRouteViewState()
    {
        ShellRouteViewState? viewState = _routeHistory.Current?.ViewState;
        if (viewState is not null)
        {
            _restoreRouteViewState?.Invoke(viewState);
        }
    }

    private static ShellWorkspaceTabIdentity IdentityForPage(Type pageSource, object? parameter) =>
        pageSource == typeof(DashboardPageType) ? ShellWorkspaceTabIdentity.Home() :
        pageSource == typeof(SettingsPage) ? ShellWorkspaceTabIdentity.Settings() :
        pageSource == typeof(ProfilePage) ? ShellWorkspaceTabIdentity.Profile() :
        pageSource == typeof(MyIssuesPage) ? new("my-issues", "issues") :
        pageSource == typeof(MyPullRequestsPage) ? new("my-pull-requests", "pull-requests") :
        pageSource == typeof(NotificationsPage) ? new("notifications", "notifications") :
        pageSource == typeof(StarsPage) ? new("stars", "stars") :
        pageSource == typeof(GistsPage) ? new("gists", "gists") :
        new ShellWorkspaceTabIdentity(pageSource.FullName ?? pageSource.Name, string.Empty);

    private static string TitleForPage(Type pageSource) =>
        pageSource == typeof(DashboardPageType) ? ShellText("Route.Home", "Home") :
        pageSource == typeof(SettingsPage) ? ShellText("Route.Settings", "Settings") :
        pageSource == typeof(ProfilePage) ? ShellText("Route.Profile", "Profile") :
        pageSource == typeof(MyIssuesPage) ? ShellText("Route.MyIssues", "My Issues") :
        pageSource == typeof(MyPullRequestsPage) ? ShellText("Route.MyPullRequests", "My Pull Requests") :
        pageSource == typeof(NotificationsPage) ? ShellText("Route.Notifications", "Notifications") :
        pageSource == typeof(StarsPage) ? ShellText("Route.Stars", "Stars") :
        pageSource == typeof(GistsPage) ? ShellText("Route.Gists", "Gists") :
        pageSource.Name;

    private string FormatRepositoryRailStatus(int visibleCount)
    {
        string filterLabel = _repositoryFilter switch
        {
            ShellRepositoryFilter.Private => ShellText("RepositoryFilter.Private", "private"),
            ShellRepositoryFilter.Forked => ShellText("RepositoryFilter.Forked", "forked"),
            _ => ShellText("RepositoryFilter.Public", "public")
        };

        if (visibleCount == 0)
        {
            return string.IsNullOrWhiteSpace(RepositoryFilterText)
                ? ShellFormat("RepositoryStatus.Empty", "No {0} repos shown", filterLabel)
                : ShellFormat("RepositoryStatus.NoMatch", "No {0} repo matches", filterLabel);
        }

        return string.IsNullOrWhiteSpace(RepositoryFilterText)
            ? _isPublicRepositoryPreview
                ? ShellFormat(
                    visibleCount == 1 ? "RepositoryStatus.PreviewOne" : "RepositoryStatus.PreviewMany",
                    visibleCount == 1 ? "{0} preview public repo" : "{0} preview public repos",
                    visibleCount)
                : _isRepositoryIndexComplete
                    ? ShellFormat(
                        visibleCount == 1 ? "RepositoryStatus.FilteredOne" : "RepositoryStatus.FilteredMany",
                        visibleCount == 1 ? "{0} filtered {1} repo" : "{0} filtered {1} repos",
                        visibleCount,
                        filterLabel)
                    : ShellFormat(
                        visibleCount == 1 ? "RepositoryStatus.IndexedOne" : "RepositoryStatus.IndexedMany",
                        visibleCount == 1 ? "{0} indexed {1} repo" : "{0} indexed {1} repos",
                        visibleCount,
                        filterLabel)
            : ShellFormat(
                visibleCount == 1 ? "RepositoryStatus.MatchOne" : "RepositoryStatus.MatchMany",
                visibleCount == 1 ? "{0} {1} repo matches" : "{0} {1} repos match",
                visibleCount,
                filterLabel);
    }

    private void UpdateSelectedRepositoryState()
    {
        foreach (ShellRepositoryItem item in RepositoryItems)
        {
            item.IsSelected = IsSameRepository(item.Repository, ActiveRepository);
        }
    }

    private ObservableCollection<ShellCommandSearchResult> BuildDefaultSearchResults()
    {
        List<ShellCommandSearchResult> results = BuildCommandResults(string.Empty)
            .Take(6)
            .ToList();

        results.AddRange(_repositoryCache
            .Take(6)
            .Select(CreateRepositorySearchResult));

        return new ObservableCollection<ShellCommandSearchResult>(results);
    }

    private IEnumerable<ShellCommandSearchResult> BuildCommandResults(string term)
    {
        string normalizedTerm = term.Trim();
        IEnumerable<ShellCommandSearchResult> commands =
        [
            CreateCommandResult("Home", "Go Home", "Open the Home workspace", "\uE80F", 100, GoHome),
            CreateCommandResult("Settings", "Open Settings", "Open app settings", "\uE713", 98, GoToSettingsPage),
            CreateCommandResult("MyIssues", "My Issues", "Open issues assigned to you", "\uE8A5", 96, OpenMyIssuesPage),
            CreateCommandResult("MyPullRequests", "My Pull Requests", "Open pull requests involving you", "\uE8EE", 94, OpenMyPullRequestsPage),
            CreateCommandResult("Notifications", "Notifications", "Open GitHub notifications", "\uEA8F", 93, OpenNotificationsPage),
            CreateCommandResult("Stars", "Stars", "Open starred repositories", "\uE734", 92, OpenStarsPage),
            CreateCommandResult("Gists", "Gists", "Open your gists", "\uE943", 90, OpenGistsPage),
            CreateCommandResult("NewRepository", "New Repository", "Create a repository", "\uE8F4", 88, OpenNewRepositoryModal),
            CreateCommandResult("AllRepositories", "All Repositories", "Browse account repositories", "\uE8B7", 86, OpenManageRepositories),
            CreateCommandResult("ActiveRepoIssues", "Open Active Repo Issues", "Open issues for the selected repository", "\uE8A5", 84, OpenActiveRepositoryIssues),
            CreateCommandResult("ActiveRepoPullRequests", "Open Active Repo Pull Requests", "Open pull requests for the selected repository", "\uE8EE", 82, OpenActiveRepositoryPullRequests),
            CreateCommandResult("ActiveRepoCommits", "Open Active Repo Commits", "Open commits for the selected repository", "\uE7C1", 80, OpenActiveRepositoryCommits),
            CreateCommandResult("Share", "Share JitHub", "Open Windows share", "\uE72D", 78, OnShareJitHub),
            CreateCommandResult("SignOut", "Sign Out", "End the current GitHub session", "\uF3B1", 76, OnSignOut)
        ];

        if (GlobalViewModel.DevMode)
        {
            commands = commands.Concat(
            [
                CreateCommandResult("DevConsole", "Dev Console", "Open the developer console", "\uEC7A", 80, OnOpenDevConsole),
                CreateCommandResult("DesignLab", "Design Lab", "Open vNext design lab", "\uF158", 78, GoToDesignLabPage)
            ]);
        }

        if (string.IsNullOrWhiteSpace(normalizedTerm))
        {
            return commands;
        }

        return commands.Where(command =>
            command.Title.Contains(normalizedTerm, StringComparison.CurrentCultureIgnoreCase) ||
            command.Subtitle.Contains(normalizedTerm, StringComparison.CurrentCultureIgnoreCase));
    }

    private ShellCommandSearchResult CreateCommandResult(
        string resourceKey,
        string title,
        string subtitle,
        string glyph,
        int score,
        Action action)
    {
        return new ShellCommandSearchResult(
            ShellCommandSearchResultKind.Command,
            ShellText($"Command.{resourceKey}.Title", title),
            ShellText($"Command.{resourceKey}.Subtitle", subtitle),
            glyph,
            score,
            new RelayCommand(action));
    }

    private ShellCommandSearchResult CreateRepositorySearchResult(GitHubRepository repository)
    {
        string subtitle = string.IsNullOrWhiteSpace(repository.Description)
            ? repository.Private
                ? ShellText("Repository.Private", "Private repository")
                : ShellText("Repository.Public", "Public repository")
            : repository.Description!;
        return new ShellCommandSearchResult(
            ShellCommandSearchResultKind.Repository,
            repository.FullName,
            subtitle,
            repository.Private ? "\uE72E" : "\uE8F1",
            70,
            new RelayCommand(() => OpenRepository(repository)),
            repository);
    }

    private ShellCommandSearchResult CreateSearchQueryResult(string term)
    {
        return new ShellCommandSearchResult(
            ShellCommandSearchResultKind.SearchQuery,
            ShellFormat("Search.QueryTitle", "Search repositories for \"{0}\"", term),
            ShellText("Search.QuerySubtitle", "Open repository search results"),
            "\uE721",
            60,
            new RelayCommand(() => OpenSearchQuery(term)));
    }

    public void OpenManageRepositories() => TryOpenManageRepositories();

    public bool TryOpenManageRepositories() =>
        OpenTab(
            ShellWorkspaceTabIdentity.ManageRepositories(),
            ShellText("Route.Repositories", "Repositories"),
            typeof(RepoManagePage),
            null);

    private void SelectNavigationForTab(TabViewItem item)
    {
        string key = item.Tag as string ?? string.Empty;
        if (key.StartsWith("repo:", StringComparison.Ordinal))
        {
            SelectNavigationItem(ParseRepositoryPageFromKey(key));
            return;
        }

        if (key.StartsWith("settings", StringComparison.Ordinal))
        {
            SelectNavigationItem("settings");
            return;
        }

        SelectNavigationItem(key.StartsWith("home", StringComparison.Ordinal) ? "home" : string.Empty);
    }

    private void SelectNavigationItem(string id)
    {
        foreach (ShellNavigationItem item in NavigationItems)
        {
            item.IsSelected = string.Equals(item.Id, id, StringComparison.Ordinal);
        }
    }

    private static string ParseRepositoryPageFromKey(string key)
    {
        if (key.Contains($":{RepoPageType.IssuePage}", StringComparison.Ordinal))
        {
            return "issues";
        }

        if (key.Contains($":{RepoPageType.PullRequestPage}", StringComparison.Ordinal))
        {
            return "pull-requests";
        }

        if (key.Contains($":{RepoPageType.CommitPage}", StringComparison.Ordinal))
        {
            return "commits";
        }

        return string.Empty;
    }

    private string GetTelemetryPage(TabViewItem item)
    {
        string key = item.Tag as string ?? string.Empty;
        if (key.StartsWith("repo:", StringComparison.Ordinal))
        {
            return ParseRepositoryPageFromKey(key) switch
            {
                "issues" => "issues",
                "pull-requests" => "pull-requests",
                "commits" => "commits",
                _ => "code"
            };
        }

        if (key.StartsWith("search:", StringComparison.Ordinal))
        {
            return "search";
        }

        return key switch
        {
            "settings" => "settings",
            "profile" => "profile",
            "design-lab" => "design-lab",
            "manage-repositories" => "manage-repositories",
            _ => "home"
        };
    }

    private void NotifyRepositoryFilterChanged()
    {
        OnPropertyChanged(nameof(IsPublicRepositoryFilterSelected));
        OnPropertyChanged(nameof(IsPrivateRepositoryFilterSelected));
        OnPropertyChanged(nameof(IsForkedRepositoryFilterSelected));
    }

    private void NotifyRepositoryVisibilityChanged()
    {
        OnPropertyChanged(nameof(AreRepositoriesVisible));
        OnPropertyChanged(nameof(IsRepositoryRailEmpty));
    }

    private void TrackRailRefreshFailure(string errorKind)
    {
        TrackEvent(
            "shell.rail.refresh.completed",
            new Dictionary<string, string?>
            {
                ["resource"] = GitHubCachePolicy.RepositoryResource,
                ["source"] = "shell",
                ["result"] = "error",
                ["error_kind"] = errorKind
            });
    }

    private void TrackSearchCompletion(
        string result,
        TimeSpan duration,
        CacheState? cacheState = null,
        string? errorKind = null)
    {
        TrackEvent(
            "shell.search.completed",
            new Dictionary<string, string?>
            {
                ["resource"] = GitHubCachePolicy.SearchResource,
                ["source"] = "shell",
                ["cache_state"] = cacheState?.ToString(),
                ["result"] = result,
                ["error_kind"] = errorKind,
                ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
            });
    }

    private void TrackEvent(string name, IReadOnlyDictionary<string, string?> properties)
    {
        try
        {
            _telemetryService.TrackEvent(name, properties);
        }
        catch
        {
            // Shell behavior must never depend on best-effort telemetry.
        }
    }

    private void RefreshUserDisplay()
    {
        GitHubUser? user = _authService.AuthenticatedUser;
        string login = user?.Login?.Trim() ?? string.Empty;
        string name = user?.Name?.Trim() ?? string.Empty;
        UserDisplayName = !string.IsNullOrWhiteSpace(name)
            ? name
            : !string.IsNullOrWhiteSpace(login)
                ? login
                : "GitHub";
        UserSubtitle = !string.IsNullOrWhiteSpace(login) && !string.Equals(login, UserDisplayName, StringComparison.OrdinalIgnoreCase)
            ? $"@{login}"
            : "GitHub account";
    }

    private void RepositoryIndexService_Changed(object? sender, AccountRepositoryIndexChangedEventArgs e)
    {
        if (!string.Equals(e.Snapshot.UserId, _activeRepositoryPartition, StringComparison.Ordinal))
        {
            return;
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            ApplyRepositoryIndexSnapshot(e.Snapshot);
            return;
        }

        _ = _dispatcherQueue.TryEnqueue(() => ApplyRepositoryIndexSnapshot(e.Snapshot));
    }

    private void StarLibraryProjection_Changed(object? sender, ShellStarLibrarySnapshot snapshot)
    {
        if (!string.Equals(snapshot.UserId, _activeRepositoryPartition, StringComparison.Ordinal))
        {
            return;
        }

        if (_dispatcherQueue.HasThreadAccess)
        {
            ApplyStarLibrarySnapshot(snapshot);
            return;
        }

        _ = _dispatcherQueue.TryEnqueue(() => ApplyStarLibrarySnapshot(snapshot));
    }

    private void ApplyStarLibrarySnapshot(ShellStarLibrarySnapshot snapshot)
    {
        if (!string.Equals(snapshot.UserId, _activeRepositoryPartition, StringComparison.Ordinal))
        {
            return;
        }

        StarLibraryIndexedCount = snapshot.IndexedCount;
        IsStarLibraryDegraded = snapshot.DegradedState.IsDegraded;
        ShellNavigationItem? stars = NavigationItems.FirstOrDefault(static item => item.Id == "stars");
        if (stars is not null)
        {
            stars.BadgeValue = snapshot.IndexedCount;
            stars.BadgeText = snapshot.IndexedCount > 99
                ? "99+"
                : snapshot.IndexedCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void ApplyRepositoryIndexSnapshot(AccountRepositoryIndexSnapshot snapshot)
    {
        if (!string.Equals(snapshot.UserId, _activeRepositoryPartition, StringComparison.Ordinal))
        {
            return;
        }

        _repositoryCache.Clear();
        _repositoryCache.AddRange(snapshot.Repositories);
        _isRepositoryIndexComplete = snapshot.IsComplete;
        IsRepositoryRailRefreshing = snapshot.IsSynchronizing;
        HasRepositoryRailError = !string.IsNullOrWhiteSpace(snapshot.ErrorMessage);
        RebuildRepositoryItems();
        if (HasRepositoryRailError)
        {
            RepositoryRailStatusText = _repositoryCache.Count == 0
                ? "JitHub could not load repositories."
                : "Showing indexed repositories; synchronization will retry later.";
        }
    }

    private void EnsureShareManager()
    {
        if (_shareWindow is null)
        {
            throw new InvalidOperationException("Share UI is not initialized.");
        }

        if (_shareManager is not null)
        {
            return;
        }

        _shareManager = DesktopDataTransferManagerHelper.GetForWindow(_shareWindow);
        _shareManager.DataRequested += OnDataRequested;
    }

    private void OnDataRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        DataRequest request = args.Request;
        request.Data.SetWebLink(new Uri(StorePage));
        request.Data.Properties.Title = StorePage;
        request.Data.Properties.Description = "JitHub";
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
        return userId > 0 ? userId.ToString() : "current";
    }

    private static bool IsSameRepository(GitHubRepository? left, GitHubRepository? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        if (left.Id != 0 && right.Id != 0)
        {
            return left.Id == right.Id;
        }

        return string.Equals(left.FullName, right.FullName, StringComparison.OrdinalIgnoreCase);
    }

    private static RepoPageType ResolveRepositoryPageType(string? page) =>
        page?.ToLowerInvariant() switch
        {
            "repo-issues" => RepoPageType.IssuePage,
            "repo-pulls" => RepoPageType.PullRequestPage,
            "repo-pull-requests" => RepoPageType.PullRequestPage,
            "repo-commits" => RepoPageType.CommitPage,
            _ => RepoPageType.CodePage
        };

    private static GitHubRepository CreateMinimalRepository(string fullName, string? branch)
    {
        string[] parts = fullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string owner = parts.Length == 2 ? parts[0] : string.Empty;
        string name = parts.Length == 2 ? parts[1] : fullName.Trim();
        return new GitHubRepository
        {
            Name = name,
            FullName = string.IsNullOrWhiteSpace(owner) ? name : $"{owner}/{name}",
            DefaultBranch = string.IsNullOrWhiteSpace(branch) ? "main" : branch,
            Owner = new GitHubRepositoryOwner
            {
                Login = owner
            }
        };
    }
}

internal static class ShellRepositoryExtensions
{
    public static string VisibilityLabel(this GitHubRepository repository) =>
        repository.Private ? "Private repository" : "Public repository";
}
