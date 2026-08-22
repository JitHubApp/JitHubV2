using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Common;
using Microsoft.UI.Dispatching;

namespace JitHub.WinUI.ViewModels.Pages;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class StarLibraryPageViewModel : ViewModelBase, IDisposable
{
    private const int InitialPageSize = 24;
    private const int LocalPageSize = 100;
    private static readonly TimeSpan BackgroundHydrationDelay = TimeSpan.FromMilliseconds(500);
    private static string AllLanguagesText => L("Stars/Filters/AllLanguages", "All languages");
    private static string AllOwnersText => L("Stars/Filters/AllOwners", "All owners");
    private static string AllTopicsText => L("Stars/Filters/AllTopics", "All topics");
    private static string AllVisibilityText => L("Stars/Filters/AllVisibility", "All visibility");
    private static string PublicText => L("Stars/Filters/Public", "Public");
    private static string PrivateText => L("Stars/Filters/Private", "Private");
    private static string AllRepositoriesText => L("Stars/Filters/AllRepositories", "All repositories");
    private static string SourcesText => L("Stars/Filters/Sources", "Sources");
    private static string ForksText => L("Stars/Filters/Forks", "Forks");
    private static string ActiveAndArchivedText => L("Stars/Filters/ActiveAndArchived", "Active and archived");
    private static string ActiveText => L("Stars/Filters/Active", "Active");
    private static string ArchivedText => L("Stars/Filters/Archived", "Archived");
    private static string AnyCategoryText => L("Stars/Filters/AnyCategory", "Any category");
    private static string CategorizedText => L("Stars/Filters/Categorized", "Categorized");
    private static string UncategorizedText => L("Stars/Filters/Uncategorized", "Uncategorized");
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubStarLibraryService _libraryService;
    private readonly ITelemetryService _telemetry;
    private readonly ShellPageViewModel _shell;
    private readonly StarLibrarySessionState _sessionState;
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _pageLifetime = new();
    private CancellationTokenSource? _searchCancellation;
    private string _accessToken = string.Empty;
    private string _userId = "current";
    private bool _initialized;
    private bool _suppressQueryChanges;
    private bool _disposed;

    public StarLibraryPageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _libraryService = GetService<IGitHubStarLibraryService>();
        _telemetry = SafeTelemetryService.Wrap(GetService<ITelemetryService>());
        _shell = GetService<ShellPageViewModel>();
        _sessionState = GetService<StarLibrarySessionState>();
        _taskCoordinator = GetService<IApplicationTaskCoordinator>();
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        SortOptions = new ObservableCollection<StarSortOption>(
        [
            new(StarLibrarySort.RecentlyStarred, L("Stars/Sort/RecentlyStarred", "Recently starred")),
            new(StarLibrarySort.RecentlyActive, L("Stars/Sort/RecentlyActive", "Recently active")),
            new(StarLibrarySort.MostStars, L("Stars/Sort/MostStars", "Most stars")),
            new(StarLibrarySort.Name, L("Stars/Sort/Name", "Name")),
            new(StarLibrarySort.LeastRecentlyActive, L("Stars/Sort/LeastRecentlyActive", "Least recently active"))
        ]);
        _suppressQueryChanges = true;
        SelectedSortOption = SortOptions.FirstOrDefault(option => option.Value == _sessionState.Sort) ?? SortOptions[0];
        SearchText = _sessionState.SearchText;
        SelectedLanguage = NormalizeSessionDefault(_sessionState.Language, "All languages", AllLanguagesText);
        SelectedOwner = NormalizeSessionDefault(_sessionState.Owner, "All owners", AllOwnersText);
        SelectedTopic = NormalizeSessionDefault(_sessionState.Topic, "All topics", AllTopicsText);
        SelectedVisibility = NormalizeOption(_sessionState.Visibility, VisibilityOptions, ["All visibility", "Public", "Private"]);
        SelectedKind = NormalizeOption(_sessionState.RepositoryKind, KindOptions, ["All repositories", "Sources", "Forks"]);
        SelectedActivity = NormalizeOption(_sessionState.Activity, ActivityOptions, ["Active and archived", "Active", "Archived"]);
        SelectedCategoryState = NormalizeOption(_sessionState.CategoryState, CategoryStateOptions, ["Any category", "Categorized", "Uncategorized"]);
        _suppressQueryChanges = false;
        LanguageOptions.Add(AllLanguagesText);
        OwnerOptions.Add(AllOwnersText);
        TopicOptions.Add(AllTopicsText);
    }

    public KeyedObservableCollection<StarRepositoryViewItem, StarLibraryItem> Repositories { get; } = [];

    public ObservableCollection<StarNavigationItem> NavigationItems { get; } = [];

    public ObservableCollection<StarNavigationGroup> NavigationGroups { get; } = [];

    public ObservableCollection<StarCategoryViewItem> CustomCategories { get; } = [];

    public ObservableCollection<StarFilterChipViewItem> ActiveFilterChips { get; } = [];

    public ObservableCollection<string> LanguageOptions { get; } = [];

    public ObservableCollection<string> OwnerOptions { get; } = [];

    public ObservableCollection<string> TopicOptions { get; } = [];

    public ObservableCollection<StarSortOption> SortOptions { get; }

    public string[] VisibilityOptions { get; } = [AllVisibilityText, PublicText, PrivateText];

    public string[] KindOptions { get; } = [AllRepositoriesText, SourcesText, ForksText];

    public string[] ActivityOptions { get; } = [ActiveAndArchivedText, ActiveText, ArchivedText];

    public string[] CategoryStateOptions { get; } = [AnyCategoryText, CategorizedText, UncategorizedText];

    [ObservableProperty]
    public partial StarNavigationItem? SelectedNavigationItem { get; set; }

    [ObservableProperty]
    public partial StarSortOption SelectedSortOption { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedLanguage { get; set; } = AllLanguagesText;

    [ObservableProperty]
    public partial string SelectedOwner { get; set; } = AllOwnersText;

    [ObservableProperty]
    public partial string SelectedTopic { get; set; } = AllTopicsText;

    [ObservableProperty]
    public partial string SelectedVisibility { get; set; } = AllVisibilityText;

    [ObservableProperty]
    public partial string SelectedKind { get; set; } = AllRepositoriesText;

    [ObservableProperty]
    public partial string SelectedActivity { get; set; } = ActiveAndArchivedText;

    [ObservableProperty]
    public partial string SelectedCategoryState { get; set; } = AnyCategoryText;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    public partial bool HasSelection { get; set; }

    [ObservableProperty]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    public partial string ResultCountText { get; set; } = LF("Stars/Count/StarsFormat", "{0:N0} stars", 0);

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyTitle { get; set; } = L("Stars/Empty/NoStarsTitle", "No starred repositories");

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = L("Stars/Empty/NoStarsMessage", "Repositories you star on GitHub will appear here.");

    [ObservableProperty]
    public partial string SelectionText { get; set; } = string.Empty;

    public bool HasActiveFilters => ActiveFilterChips.Count > 0;

    public bool CanEditSelectedCategory => SelectedNavigationItem?.Category is not null;

    public string CurrentViewTitle => SelectedNavigationItem?.Title ?? L("Stars/SmartLists/All", "All stars");

    public IReadOnlyCollection<long> SelectedRepositoryIds => _sessionState.SelectedRepositoryIds;

    public double ListScrollOffset
    {
        get => _sessionState.ScrollOffset;
        set => _sessionState.ScrollOffset = Math.Max(0, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        Stopwatch loadDuration = Stopwatch.StartNew();
        _accessToken = GetActiveToken() ?? string.Empty;
        _userId = GetActiveUserPartition(_accessToken);
        if (string.IsNullOrWhiteSpace(_accessToken))
        {
            StatusText = L("Stars/Status/AuthUnavailable", "GitHub authentication is unavailable.");
            StarLibraryTelemetry.TrackAuthenticationUnavailable(_telemetry, loadDuration.Elapsed);
            _initialized = false;
            return;
        }

        IsLoading = true;
        try
        {
            StarLibraryQuery query = CreateQuery(0, InitialPageSize);
            StarLibraryPage page = await _libraryService.LoadCachedPageAsync(
                _accessToken,
                _userId,
                query,
                cancellationToken);
            bool hadCachedRows = page.Items.Count > 0;
            if (!hadCachedRows)
            {
                IsSyncing = true;
                page = await Task.Run(
                    async () =>
                    {
                        await _libraryService.SynchronizeAsync(
                            _accessToken,
                            _userId,
                            cancellationToken: cancellationToken).ConfigureAwait(false);
                        return await _libraryService.QueryAsync(query, cancellationToken).ConfigureAwait(false);
                    },
                    cancellationToken);
            }

            ApplyInitialPage(page);
            _libraryService.Changed += LibraryService_Changed;
            StarLibraryTelemetry.TrackOpened(
                _telemetry,
                TelemetryTaxonomy.Results.Success,
                hadCachedRows ? "local" : "miss",
                loadDuration.Elapsed);
            _ = _taskCoordinator.RunAsync(
                token => HydrateAndSynchronizeInBackgroundAsync(hadCachedRows, token),
                new ApplicationTaskOptions("stars.page_hydrate_and_synchronize", _userId),
                _pageLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            StarLibraryTelemetry.TrackOpened(
                _telemetry,
                TelemetryTaxonomy.Results.Cancelled,
                Repositories.Count > 0 ? "local" : "miss",
                loadDuration.Elapsed);
            _libraryService.Changed -= LibraryService_Changed;
            _initialized = false;
        }
        catch (Exception)
        {
            StatusText = Repositories.Count > 0
                ? L("Stars/Status/SyncFailedCached", "Stars could not be synchronized. Showing the local library.")
                : L("Stars/Status/LoadFailed", "Stars could not be loaded.");
            StarLibraryTelemetry.TrackOpened(
                _telemetry,
                TelemetryTaxonomy.Results.Error,
                Repositories.Count > 0 ? "local" : "miss",
                loadDuration.Elapsed);
        }
        finally
        {
            IsLoading = false;
            if (!_initialized)
            {
                IsSyncing = false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _libraryService.Changed -= LibraryService_Changed;
        _pageLifetime.Cancel();
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _pageLifetime.Dispose();
    }

    public void SaveSelection(IEnumerable<StarRepositoryViewItem> items)
    {
        _sessionState.SelectedRepositoryIds.Clear();
        foreach (long repositoryId in items.Select(static item => item.Repository.Id))
        {
            _sessionState.SelectedRepositoryIds.Add(repositoryId);
        }
    }

    public async Task LoadMoreAsync(CancellationToken cancellationToken = default)
    {
        if (!HasMore || IsLoading)
        {
            return;
        }

        StarLibraryPage page = await _libraryService.QueryAsync(CreateQuery(Repositories.Count, LocalPageSize), cancellationToken);
        HashSet<string> existing = Repositories.Select(static item => item.Key).ToHashSet(StringComparer.Ordinal);
        foreach (StarLibraryItem item in page.Items)
        {
            if (existing.Add(item.Key))
            {
                Repositories.Add(StarRepositoryViewItem.FromItem(item));
            }
        }

        HasMore = page.HasMore;
    }

    public void SetSelection(IReadOnlyList<StarRepositoryViewItem> selected)
    {
        SaveSelection(selected);
        SelectedCount = selected.Count;
        HasSelection = selected.Count > 0;
        SelectionText = selected.Count == 1
            ? L("Stars/Selection/OneSelected", "1 selected")
            : LF("Stars/Selection/ManySelectedFormat", "{0:N0} selected", selected.Count);
    }

    public void OpenRepository(StarRepositoryViewItem? item)
    {
        if (item is not null)
        {
            _shell.OpenRepository(item.Repository);
            TrackAction("open_repository", "success");
        }
    }

    public Task PrefetchRepositoryAsync(StarRepositoryViewItem? item) =>
        item is null
            ? Task.CompletedTask
            : _shell.PrefetchRepositoryCodeAsync(item.Repository, _pageLifetime.Token);

    public Task PrefetchLikelyRepositoriesAsync(int count = 1) =>
        Task.WhenAll(Repositories
            .Take(Math.Max(0, count))
            .Select(item => _shell.PrefetchRepositoryCodeAsync(item.Repository, _pageLifetime.Token)));

    public void OpenOwner(StarRepositoryViewItem? item)
    {
        if (!string.IsNullOrWhiteSpace(item?.Owner))
        {
            _shell.OpenUserProfile(item.Owner, "stars");
            TrackAction("open_owner", "success");
        }
    }

    public void CopyRepositoryLink(StarRepositoryViewItem? item)
    {
        if (string.IsNullOrWhiteSpace(item?.Repository.HtmlUrl))
        {
            return;
        }

        bool succeeded = PlatformHelper.CopyString(item.Repository.HtmlUrl);
        StatusText = succeeded
            ? L("Stars/Status/LinkCopied", "Repository link copied.")
            : L("Stars/Status/LinkCopyFailed", "The repository link could not be copied.");
        TrackAction(
            TelemetryTaxonomy.Actions.CopyLink,
            succeeded ? TelemetryTaxonomy.Results.Success : TelemetryTaxonomy.Results.Error);
    }

    public async Task<StarUndoState?> UnstarAsync(StarRepositoryViewItem? viewItem, CancellationToken cancellationToken = default)
    {
        if (viewItem is null)
        {
            return null;
        }

        IReadOnlyList<string> categoryIds = viewItem.Item.Categories.Select(static category => category.Id).ToArray();
        await _libraryService.UnstarAsync(_accessToken, _userId, viewItem.Item, cancellationToken);
        await RefreshFromStoreAsync();
        StatusText = FormatString("Stars/Status/UnstarredRepositoryFormat", "Unstarred {0}.", viewItem.FullName);
        return new StarUndoState(viewItem.Item, categoryIds);
    }

    public async Task UnstarManyAsync(IReadOnlyList<StarRepositoryViewItem> items, CancellationToken cancellationToken = default)
    {
        foreach (StarRepositoryViewItem item in items)
        {
            await _libraryService.UnstarAsync(_accessToken, _userId, item.Item, cancellationToken);
        }

        await RefreshFromStoreAsync();
        StatusText = FormatString("Stars/Status/BulkUnstarredFormat", "Unstarred {0} repositories.", items.Count);
    }

    public async Task UndoUnstarAsync(StarUndoState? undo, CancellationToken cancellationToken = default)
    {
        if (undo is null)
        {
            return;
        }

        await _libraryService.RestoreStarAsync(_accessToken, _userId, undo.Item, undo.CategoryIds, cancellationToken);
        await RefreshFromStoreAsync();
        StatusText = FormatString("Stars/Status/RestoredRepositoryFormat", "Restored {0}.", undo.Item.Repository.FullName);
    }

    public async Task<StarCategory> CreateCategoryAsync(string name, string color, CancellationToken cancellationToken = default)
    {
        StarCategory category = await _libraryService.CreateCategoryAsync(_userId, name, color, cancellationToken);
        await RefreshNavigationAsync();
        if (!SelectCategory(category.Id))
        {
            StarCategoryViewItem custom = new(category);
            CustomCategories.Add(custom);
            StarNavigationItem navigationItem = new(
                $"category:{category.Id}",
                category.Name,
                "\uE8EC",
                category.RepositoryCount,
                StarSmartList.All,
                custom);
            NavigationItems.Add(navigationItem);
            StarNavigationGroup? categoryGroup = NavigationGroups.FirstOrDefault(
                static group => group.Id == "categories");
            if (categoryGroup is null)
            {
                categoryGroup = new StarNavigationGroup(
                    "categories",
                    L("Stars/Navigation/CategoriesHeader", "CATEGORIES"));
                NavigationGroups.Add(categoryGroup);
            }

            categoryGroup.Add(navigationItem);
            SelectedNavigationItem = navigationItem;
        }
        return category;
    }

    public bool SelectCategory(string categoryId)
    {
        StarNavigationItem? navigationItem = NavigationItems.FirstOrDefault(
            item => string.Equals(item.Category?.Id, categoryId, StringComparison.Ordinal));
        if (navigationItem is null)
        {
            return false;
        }

        SelectedNavigationItem = navigationItem;
        return true;
    }

    public async Task UpdateCategoryAsync(StarCategoryViewItem item, string name, string color, CancellationToken cancellationToken = default)
    {
        await _libraryService.UpdateCategoryAsync(_userId, item.Id, name, color, cancellationToken);
        await RefreshNavigationAsync();
    }

    public async Task DeleteCategoryAsync(StarCategoryViewItem item, CancellationToken cancellationToken = default)
    {
        await _libraryService.DeleteCategoryAsync(_userId, item.Id, cancellationToken);
        SelectSmartList(StarSmartList.All);
        await RefreshNavigationAsync();
        await RefreshFromStoreAsync();
    }

    public async Task MoveCategoryAsync(StarCategoryViewItem item, int delta, CancellationToken cancellationToken = default)
    {
        int target = Math.Clamp(item.Position + delta, 0, Math.Max(0, CustomCategories.Count - 1));
        await _libraryService.ReorderCategoryAsync(_userId, item.Id, target, cancellationToken);
        await RefreshNavigationAsync();
    }

    public async Task AddToCategoryAsync(string categoryId, IReadOnlyList<StarRepositoryViewItem> items, CancellationToken cancellationToken = default)
    {
        long[] ids = items.Select(static item => item.Repository.Id).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        await _libraryService.AddToCategoryAsync(_userId, categoryId, ids, cancellationToken);
        await RefreshNavigationAsync();
        await RefreshFromStoreAsync();
        StatusText = ids.Length == 1
            ? L("Stars/Status/AddedToCategory", "Added to category.")
            : FormatString("Stars/Status/BulkAddedToCategoryFormat", "Added {0} repositories to category.", ids.Length);
    }

    public async Task RemoveFromCurrentCategoryAsync(IReadOnlyList<StarRepositoryViewItem> items, CancellationToken cancellationToken = default)
    {
        string? categoryId = SelectedNavigationItem?.Category?.Id;
        if (string.IsNullOrWhiteSpace(categoryId))
        {
            return;
        }

        await _libraryService.RemoveFromCategoryAsync(_userId, categoryId, items.Select(static item => item.Repository.Id).ToArray(), cancellationToken);
        await RefreshNavigationAsync();
        await RefreshFromStoreAsync();
    }

    public void ClearFilters()
    {
        _suppressQueryChanges = true;
        SelectedLanguage = AllLanguagesText;
        SelectedOwner = AllOwnersText;
        SelectedTopic = AllTopicsText;
        SelectedVisibility = AllVisibilityText;
        SelectedKind = AllRepositoriesText;
        SelectedActivity = ActiveAndArchivedText;
        SelectedCategoryState = AnyCategoryText;
        _suppressQueryChanges = false;
        ScheduleQueryChange("clear");
    }

    public void RemoveFilter(string id)
    {
        _suppressQueryChanges = true;
        switch (id)
        {
            case "language": SelectedLanguage = AllLanguagesText; break;
            case "owner": SelectedOwner = AllOwnersText; break;
            case "topic": SelectedTopic = AllTopicsText; break;
            case "visibility": SelectedVisibility = AllVisibilityText; break;
            case "kind": SelectedKind = AllRepositoriesText; break;
            case "activity": SelectedActivity = ActiveAndArchivedText; break;
            case "category": SelectedCategoryState = AnyCategoryText; break;
        }

        _suppressQueryChanges = false;
        ScheduleQueryChange(id);
    }

    partial void OnSelectedNavigationItemChanged(StarNavigationItem? value)
    {
        if (value is not null)
        {
            _sessionState.SelectedNavigationId = value.Id;
        }

        OnPropertyChanged(nameof(CurrentViewTitle));
        OnPropertyChanged(nameof(CanEditSelectedCategory));
        if (!_suppressQueryChanges && value is not null)
        {
            ScheduleQueryChange("navigation");
        }
    }

    partial void OnSelectedSortOptionChanged(StarSortOption value)
    {
        if (value is not null)
        {
            _sessionState.Sort = value.Value;
        }

        if (!_suppressQueryChanges && value is not null)
        {
            ScheduleQueryChange("sort");
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _sessionState.SearchText = value ?? string.Empty;
        if (_suppressQueryChanges)
        {
            return;
        }

        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        ScheduleQueryChange("search", TimeSpan.FromMilliseconds(180), _searchCancellation.Token);
    }

    partial void OnSelectedLanguageChanged(string value)
    {
        _sessionState.Language = value ?? AllLanguagesText;
        OnFilterChanged("language");
    }

    partial void OnSelectedOwnerChanged(string value)
    {
        _sessionState.Owner = value ?? AllOwnersText;
        OnFilterChanged("owner");
    }

    partial void OnSelectedTopicChanged(string value)
    {
        _sessionState.Topic = value ?? AllTopicsText;
        OnFilterChanged("topic");
    }

    partial void OnSelectedVisibilityChanged(string value)
    {
        _sessionState.Visibility = value ?? AllVisibilityText;
        OnFilterChanged("visibility");
    }

    partial void OnSelectedKindChanged(string value)
    {
        _sessionState.RepositoryKind = value ?? AllRepositoriesText;
        OnFilterChanged("kind");
    }

    partial void OnSelectedActivityChanged(string value)
    {
        _sessionState.Activity = value ?? ActiveAndArchivedText;
        OnFilterChanged("activity");
    }

    partial void OnSelectedCategoryStateChanged(string value)
    {
        _sessionState.CategoryState = value ?? AnyCategoryText;
        OnFilterChanged("category");
    }

    private void OnFilterChanged(string filterType)
    {
        if (!_suppressQueryChanges)
        {
            ScheduleQueryChange(filterType);
        }
    }

    private async Task ApplyQueryChangeAsync(
        string filterType,
        CancellationToken cancellationToken = default)
    {
        RebuildFilterChips();
        Stopwatch duration = Stopwatch.StartNew();
        string eventName = string.Equals(filterType, "sort", StringComparison.Ordinal)
            ? "stars.sort.changed"
            : "stars.filter.changed";
        Dictionary<string, string?> properties = new()
        {
            ["filter_type"] = string.Equals(filterType, "sort", StringComparison.Ordinal) ? null : filterType,
            ["sort"] = string.Equals(filterType, "sort", StringComparison.Ordinal)
                ? TelemetryTaxonomy.EnumValue(_sessionState.Sort)
                : null
        };

        try
        {
            bool applied = await RefreshFromStoreAsync(cancellationToken);
            properties["result"] = applied ? TelemetryTaxonomy.Results.Success : "deferred";
        }
        catch (OperationCanceledException)
        {
            properties["result"] = TelemetryTaxonomy.Results.Cancelled;
            throw;
        }
        catch
        {
            properties["result"] = TelemetryTaxonomy.Results.Error;
            throw;
        }
        finally
        {
            properties["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration.Elapsed);
            if (string.Equals(eventName, "stars.sort.changed", StringComparison.Ordinal))
            {
                _telemetry.TrackEvent("stars.sort.changed", properties);
            }
            else
            {
                _telemetry.TrackEvent("stars.filter.changed", properties);
            }
        }
    }

    private async Task<bool> RefreshFromStoreAsync(CancellationToken cancellationToken = default)
    {
        if (!_initialized || !await _refreshGate.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            int limit = Math.Max(LocalPageSize, Repositories.Count);
            StarLibraryPage page = await _libraryService.QueryAsync(CreateQuery(0, limit), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyPage(page, resetRows: true);
            return true;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshNavigationAsync(CancellationToken cancellationToken = default)
    {
        StarLibrarySnapshot snapshot = await _libraryService.InitializeAsync(
            _accessToken,
            _userId,
            CreateQuery(0, Math.Max(LocalPageSize, Repositories.Count)),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        ApplyNavigationSnapshot(snapshot);
    }

    private async Task SynchronizeInBackgroundAsync(CancellationToken cancellationToken)
    {
        IsSyncing = true;
        try
        {
            StarSyncState state = await _libraryService.SynchronizeAsync(
                _accessToken,
                _userId,
                cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            StatusText = string.IsNullOrWhiteSpace(state.ErrorMessage)
                ? string.Empty
                : Repositories.Count > 0
                    ? L("Stars/Status/UpdateFailedCached", "Could not update Stars. Showing the local library.")
                    : L("Stars/Status/LoadFromGitHubFailed", "Could not load Stars from GitHub.");
            await RefreshNavigationAsync(cancellationToken);
            await RefreshFromStoreAsync(cancellationToken);
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private async Task HydrateAndSynchronizeInBackgroundAsync(
        bool synchronize,
        CancellationToken cancellationToken)
    {
        await Task.Delay(BackgroundHydrationDelay, cancellationToken);
        await RefreshNavigationAsync(cancellationToken);
        if (synchronize)
        {
            await SynchronizeInBackgroundAsync(cancellationToken);
        }
        else
        {
            IsSyncing = false;
        }
    }

    private void LibraryService_Changed(object? sender, StarLibraryChangedEventArgs e)
    {
        if (!string.Equals(e.UserId, _userId, StringComparison.Ordinal))
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(() =>
        {
            if (e.Kind == StarLibraryChangeKind.Sync)
            {
                ScheduleProjectionRefresh(StarProjectionRefresh.SyncStatus);
            }
            else if (e.Kind == StarLibraryChangeKind.Degraded)
            {
                StarLibraryDegradedState state = _libraryService.GetDegradedState(_userId);
                StatusText = state.IsDegraded ? state.Message : string.Empty;
            }
            else if (e.Kind is StarLibraryChangeKind.Items or StarLibraryChangeKind.Categories)
            {
                StarProjectionRefresh refresh = StarProjectionRefresh.Repositories;
                if (e.Kind == StarLibraryChangeKind.Categories)
                {
                    refresh |= StarProjectionRefresh.Navigation;
                }

                ScheduleProjectionRefresh(refresh);
            }
        });
    }

    private async Task UpdateSyncStatusAsync(CancellationToken cancellationToken)
    {
        StarLibraryPage page = await _libraryService.QueryAsync(CreateQuery(0, 1), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        IsSyncing = page.SyncState.IsSyncing;
        ResultCountText = FormatCount(page.TotalCount, page.SyncState.IsComplete);
    }

    private void ScheduleQueryChange(
        string filterType,
        TimeSpan? delay = null,
        CancellationToken cancellationToken = default)
    {
        ScheduleAccountTask(
            "stars.page_query_projection",
            async taskToken =>
            {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                    taskToken,
                    cancellationToken);
                if (delay is TimeSpan debounce)
                {
                    await Task.Delay(debounce, linked.Token);
                }

                linked.Token.ThrowIfCancellationRequested();
                await ApplyQueryChangeAsync(filterType, linked.Token);
            });
    }

    private void ScheduleProjectionRefresh(StarProjectionRefresh refresh)
    {
        ScheduleAccountTask(
            "stars.page_projection_refresh",
            async cancellationToken =>
            {
                if ((refresh & StarProjectionRefresh.SyncStatus) != 0)
                {
                    await UpdateSyncStatusAsync(cancellationToken);
                }

                if ((refresh & StarProjectionRefresh.Navigation) != 0)
                {
                    await RefreshNavigationAsync(cancellationToken);
                }

                if ((refresh & StarProjectionRefresh.Repositories) != 0)
                {
                    await RefreshFromStoreAsync(cancellationToken);
                }
            });
    }

    private void ScheduleAccountTask(
        string taskName,
        Func<CancellationToken, Task> operation)
    {
        if (_disposed ||
            !_initialized ||
            _pageLifetime.IsCancellationRequested ||
            string.IsNullOrWhiteSpace(_accessToken) ||
            string.Equals(_userId, "current", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _ = _taskCoordinator.RunAsync(
            operation,
            new ApplicationTaskOptions(taskName, _userId),
            _pageLifetime.Token);
    }

    private void ApplySnapshot(StarLibrarySnapshot snapshot, bool resetRows)
    {
        ApplyNavigationSnapshot(snapshot);
        ApplyPage(snapshot.Page, resetRows);
    }

    private void ApplyInitialPage(StarLibraryPage page)
    {
        Dictionary<StarSmartList, int> initialCounts = new()
        {
            [StarSmartList.All] = page.TotalCount,
            [StarSmartList.RecentlyStarred] = page.TotalCount
        };
        ApplyNavigation([], initialCounts);
        ApplyPage(page, resetRows: true);
    }

    private void ApplyNavigationSnapshot(StarLibrarySnapshot snapshot)
    {
        bool wasSuppressingQueryChanges = _suppressQueryChanges;
        _suppressQueryChanges = true;
        try
        {
            ApplyNavigation(snapshot.Categories, snapshot.SmartListCounts);
            SelectedLanguage = ReplaceOptions(
                LanguageOptions,
                AllLanguagesText,
                snapshot.Languages,
                SelectedLanguage);
            SelectedOwner = ReplaceOptions(
                OwnerOptions,
                AllOwnersText,
                snapshot.Owners,
                SelectedOwner);
            SelectedTopic = ReplaceOptions(
                TopicOptions,
                AllTopicsText,
                snapshot.Topics,
                SelectedTopic);
        }
        finally
        {
            _suppressQueryChanges = wasSuppressingQueryChanges;
        }
    }

    private void ApplyPage(StarLibraryPage page, bool resetRows)
    {
        if (resetRows)
        {
            if (Repositories.Count == 0)
            {
                Repositories.ResetSnapshot(
                    page.Items,
                    static item => item.Key,
                    StarRepositoryViewItem.FromItem);
            }
            else
            {
                Repositories.ApplySnapshot(
                    page.Items,
                    static item => item.Key,
                    static item => item.Key,
                    StarRepositoryViewItem.FromItem,
                    static (existing, item) => existing.UpdateFrom(item));
            }
        }

        HasMore = page.HasMore;
        IsEmpty = Repositories.Count == 0;
        IsSyncing = page.SyncState.IsSyncing;
        ResultCountText = FormatCount(page.TotalCount, page.SyncState.IsComplete);
        UpdateEmptyState(page);
    }

    private void ApplyNavigation(
        IReadOnlyList<StarCategory> categories,
        IReadOnlyDictionary<StarSmartList, int> smartListCounts)
    {
        string? selectedId = SelectedNavigationItem?.Id ?? _sessionState.SelectedNavigationId;
        bool wasSuppressingQueryChanges = _suppressQueryChanges;
        _suppressQueryChanges = true;
        List<StarNavigationItem> smart =
        [
            new("smart:all", L("Stars/SmartLists/All", "All stars"), "\uE734", GetSmartListCount(StarSmartList.All), StarSmartList.All, null),
            new("smart:uncategorized", L("Stars/SmartLists/Uncategorized", "Uncategorized"), "\uE8B7", GetSmartListCount(StarSmartList.Uncategorized), StarSmartList.Uncategorized, null),
            new("smart:recently-starred", L("Stars/SmartLists/RecentlyStarred", "Recently starred"), "\uE823", GetSmartListCount(StarSmartList.RecentlyStarred), StarSmartList.RecentlyStarred, null),
            new("smart:recently-active", L("Stars/SmartLists/RecentlyActive", "Recently active"), "\uE9D9", GetSmartListCount(StarSmartList.RecentlyActive), StarSmartList.RecentlyActive, null),
            new("smart:archived", L("Stars/SmartLists/Archived", "Archived"), "\uE7B8", GetSmartListCount(StarSmartList.Archived), StarSmartList.Archived, null)
        ];
        NavigationItems.Clear();
        NavigationGroups.Clear();
        StarNavigationGroup smartGroup = new(
            "smart",
            L("Stars/Navigation/SmartListsHeader", "SMART LISTS"));
        NavigationGroups.Add(smartGroup);
        foreach (StarNavigationItem item in smart)
        {
            NavigationItems.Add(item);
            smartGroup.Add(item);
        }

        CustomCategories.Clear();
        StarNavigationGroup? categoryGroup = categories.Count > 0
            ? new StarNavigationGroup(
                "categories",
                L("Stars/Navigation/CategoriesHeader", "CATEGORIES"))
            : null;
        if (categoryGroup is not null)
        {
            NavigationGroups.Add(categoryGroup);
        }

        for (int index = 0; index < categories.Count; index++)
        {
            StarCategory category = categories[index];
            StarCategoryViewItem custom = new(category);
            CustomCategories.Add(custom);
            StarNavigationItem navigationItem = new(
                $"category:{category.Id}",
                category.Name,
                "\uE8EC",
                category.RepositoryCount,
                StarSmartList.All,
                custom);
            NavigationItems.Add(navigationItem);
            categoryGroup!.Add(navigationItem);
        }

        SelectedNavigationItem = NavigationItems.FirstOrDefault(item => item.Id == selectedId) ?? NavigationItems.FirstOrDefault();
        _suppressQueryChanges = wasSuppressingQueryChanges;
        OnPropertyChanged(nameof(CurrentViewTitle));
        OnPropertyChanged(nameof(CanEditSelectedCategory));

        int GetSmartListCount(StarSmartList smartList) =>
            smartListCounts.TryGetValue(smartList, out int count) ? count : 0;
    }

    private void SelectSmartList(StarSmartList smartList)
    {
        SelectedNavigationItem = NavigationItems.FirstOrDefault(item => item.SmartList == smartList && item.Category is null);
    }

    private void RebuildFilterChips()
    {
        ActiveFilterChips.Clear();
        AddFilterChip("language", SelectedLanguage, AllLanguagesText);
        AddFilterChip("owner", SelectedOwner, AllOwnersText);
        AddFilterChip("topic", SelectedTopic, AllTopicsText);
        AddFilterChip("visibility", SelectedVisibility, AllVisibilityText);
        AddFilterChip("kind", SelectedKind, AllRepositoriesText);
        AddFilterChip("activity", SelectedActivity, ActiveAndArchivedText);
        AddFilterChip("category", SelectedCategoryState, AnyCategoryText);
        OnPropertyChanged(nameof(HasActiveFilters));
    }

    private void AddFilterChip(string id, string value, string defaultValue)
    {
        if (!IsDefault(value, defaultValue))
        {
            ActiveFilterChips.Add(new StarFilterChipViewItem(id, value, new RelayCommand(() => RemoveFilter(id))));
        }
    }

    private StarLibraryQuery CreateQuery(int offset, int limit)
    {
        StarNavigationItem? selected = SelectedNavigationItem;
        StarLibrarySort sort = selected?.SmartList switch
        {
            StarSmartList.RecentlyActive => StarLibrarySort.RecentlyActive,
            StarSmartList.RecentlyStarred => StarLibrarySort.RecentlyStarred,
            _ => SelectedSortOption?.Value ?? StarLibrarySort.RecentlyStarred
        };
        return new StarLibraryQuery(
            _userId,
            selected?.SmartList ?? StarSmartList.All,
            selected?.Category?.Id,
            SearchText ?? string.Empty,
            BuildFilter(),
            sort,
            offset,
            limit);
    }

    private StarLibraryFilter BuildFilter() => new(
        IsDefault(SelectedLanguage, AllLanguagesText) ? [] : [SelectedLanguage],
        IsDefault(SelectedOwner, AllOwnersText) ? [] : [SelectedOwner],
        IsDefault(SelectedTopic, AllTopicsText) ? [] : [SelectedTopic],
        IsDefault(SelectedVisibility, PublicText) ? false : IsDefault(SelectedVisibility, PrivateText) ? true : null,
        IsDefault(SelectedKind, SourcesText) ? false : IsDefault(SelectedKind, ForksText) ? true : null,
        IsDefault(SelectedActivity, ActiveText) ? false : IsDefault(SelectedActivity, ArchivedText) ? true : null,
        IsDefault(SelectedCategoryState, CategorizedText) ? true : IsDefault(SelectedCategoryState, UncategorizedText) ? false : null);

    private void UpdateEmptyState(StarLibraryPage page)
    {
        if (!string.IsNullOrWhiteSpace(SearchText) || !BuildFilter().IsEmpty)
        {
            EmptyTitle = L("Stars/Empty/NoMatchesTitle", "No matching stars");
            EmptyMessage = L("Stars/Empty/NoMatchesMessage", "Try a different search or clear one of the active filters.");
        }
        else if (SelectedNavigationItem?.Category is not null)
        {
            EmptyTitle = L("Stars/Empty/CategoryTitle", "This category is empty");
            EmptyMessage = L("Stars/Empty/CategoryMessage", "Drag repositories here or use Add to category from the list.");
        }
        else if (!string.IsNullOrWhiteSpace(page.SyncState.ErrorMessage))
        {
            EmptyTitle = L("Stars/Empty/OfflineTitle", "Stars are unavailable offline");
            EmptyMessage = L("Stars/Empty/OfflineMessage", "JitHub will update this library automatically when GitHub is reachable.");
        }
        else
        {
            EmptyTitle = L("Stars/Empty/NoStarsTitle", "No starred repositories");
            EmptyMessage = L("Stars/Empty/NoStarsAutomaticMessage", "Repositories you star on GitHub will appear here automatically.");
        }
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
        return userId > 0 ? userId.ToString(CultureInfo.InvariantCulture) : "current";
    }

    private static string ReplaceOptions(
        ObservableCollection<string> target,
        string allLabel,
        IEnumerable<string> values,
        string? selectedValue)
    {
        string selection = string.IsNullOrWhiteSpace(selectedValue) ? allLabel : selectedValue;
        target.Clear();
        target.Add(allLabel);
        foreach (string value in values.Where(static value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            target.Add(value);
        }

        return target.FirstOrDefault(value => string.Equals(value, selection, StringComparison.OrdinalIgnoreCase))
            ?? allLabel;
    }

    private static bool IsDefault(string? value, string defaultValue) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value, defaultValue, StringComparison.OrdinalIgnoreCase);

    private static string FormatCount(int count, bool complete) => complete
        ? count == 1
            ? L("Stars/Count/OneStar", "1 star")
            : LF("Stars/Count/StarsFormat", "{0:N0} stars", count)
        : count == 1
            ? L("Stars/Count/OneIndexed", "1 indexed")
            : LF("Stars/Count/IndexedFormat", "{0:N0} indexed", count);

    private static string NormalizeSessionDefault(string? value, string englishDefault, string localizedDefault) =>
        string.IsNullOrWhiteSpace(value) ||
        string.Equals(value, englishDefault, StringComparison.OrdinalIgnoreCase)
            ? localizedDefault
            : value;

    private static string NormalizeOption(
        string? value,
        IReadOnlyList<string> localizedOptions,
        IReadOnlyList<string> englishOptions)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return localizedOptions[0];
        }

        for (int index = 0; index < localizedOptions.Count && index < englishOptions.Count; index++)
        {
            if (string.Equals(value, localizedOptions[index], StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, englishOptions[index], StringComparison.OrdinalIgnoreCase))
            {
                return localizedOptions[index];
            }
        }

        return localizedOptions[0];
    }

    private static string L(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);

    private static string LF(string key, string fallback, params object?[] arguments) =>
        LocalizedResourceText.Format(key, fallback, arguments);

    private void TrackAction(string action, string result) =>
        _telemetry.TrackEvent("stars.action.executed", new Dictionary<string, string?> { ["action"] = action, ["result"] = result });
}

public sealed record StarUndoState(StarLibraryItem Item, IReadOnlyList<string> CategoryIds);

[Flags]
internal enum StarProjectionRefresh
{
    None = 0,
    SyncStatus = 1,
    Navigation = 2,
    Repositories = 4
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class StarNavigationGroup : ObservableCollection<StarNavigationItem>
{
    public StarNavigationGroup(string id, string title)
    {
        Id = id;
        Title = title;
    }

    public string Id { get; }
    public string Title { get; }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class StarNavigationItem : ObservableObject
{
    public StarNavigationItem(
        string id,
        string title,
        string glyph,
        int count,
        StarSmartList smartList,
        StarCategoryViewItem? category)
    {
        Id = id;
        Title = title;
        Glyph = glyph;
        Count = count;
        SmartList = smartList;
        Category = category;
    }

    public string Id { get; }
    public string Title { get; }
    public string Glyph { get; }
    public int Count { get; }
    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
    public StarSmartList SmartList { get; }
    public StarCategoryViewItem? Category { get; }
    public bool IsCustomCategory => Category is not null;
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }
    public string AccentColor => Category?.AccentColor ?? string.Empty;
    public string AutomationId => "StarsNavigation_" + Id.Replace(':', '_');
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class StarCategoryViewItem
{
    public StarCategoryViewItem(StarCategory category)
    {
        Category = category;
    }

    public StarCategory Category { get; }
    public string Id => Category.Id;
    public string Name => Category.Name;
    public string Color => Category.Color;
    public int Position => Category.Position;
    public int Count => Category.RepositoryCount;
    public string AccentColor => Color;
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class StarFilterChipViewItem
{
    public StarFilterChipViewItem(string id, string label, ICommand removeCommand)
    {
        Id = id;
        Label = label;
        RemoveCommand = removeCommand;
    }

    public string Id { get; }
    public string Label { get; }
    public ICommand RemoveCommand { get; }
    public string AutomationId => "StarsFilterChip_" + Id;
    public string AutomationName => LocalizedResourceText.Format(
        "Stars/Filters/RemoveAutomationNameFormat",
        "Remove filter {0}",
        Label);
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class StarSortOption
{
    public StarSortOption(StarLibrarySort value, string label)
    {
        Value = value;
        Label = label;
    }

    public StarLibrarySort Value { get; }
    public string Label { get; }
    public override string ToString() => Label;
}
