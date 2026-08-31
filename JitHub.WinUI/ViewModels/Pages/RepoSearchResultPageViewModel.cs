using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Common;

namespace JitHub.WinUI.ViewModels.Pages;

public sealed partial class RepoSearchResultPageViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 50;
    private const int GitHubSearchResultLimit = 1000;
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubRepositorySearchQueryService _searchService;
    private readonly ITelemetryService _telemetryService;
    private readonly Dictionary<int, IReadOnlyList<GitHubRepository>> _pageSnapshots = [];
    private IReadOnlyList<GitHubRepository>? _automationRepositories;
    private CancellationTokenSource? _requestCancellation;
    private int _requestVersion;
    private int _loadedPage;
    private int _availableResultCount;
    private bool _disposed;
    private Task _backgroundRefreshTask = Task.CompletedTask;

    public RepoSearchResultPageViewModel(
        IAuthService authService,
        IAccountService accountService,
        IGitHubRepositorySearchQueryService searchService,
        ITelemetryService telemetryService)
    {
        _authService = authService;
        _accountService = accountService;
        _searchService = searchService;
        _telemetryService = SafeTelemetryService.Wrap(telemetryService);
    }

    public KeyedObservableCollection<RepositorySearchResultItem, GitHubRepository> Results { get; } = [];

    public ObservableCollection<RepositorySearchFilterChip> ActiveFilters { get; } = [];

    public IReadOnlyList<string> VisibilityOptions { get; } =
        (string[])["Any visibility", "Public", "Private"];

    public IReadOnlyList<string> ForkOptions { get; } =
        (string[])["All repositories", "Sources", "Forks"];

    public IReadOnlyList<string> ArchiveOptions { get; } =
        (string[])["Active and archived", "Active", "Archived"];

    public IReadOnlyList<string> SortOptions { get; } =
        (string[])["Best match", "Recently updated", "Most stars", "Most forks"];

    [ObservableProperty]
    public partial string QueryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string OwnerFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LanguageFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TopicFilter { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedVisibility { get; set; } = "Any visibility";

    [ObservableProperty]
    public partial string SelectedForkScope { get; set; } = "All repositories";

    [ObservableProperty]
    public partial string SelectedArchiveScope { get; set; } = "Active and archived";

    [ObservableProperty]
    public partial string SelectedSort { get; set; } = "Best match";

    [ObservableProperty]
    public partial string ResultSummary { get; set; } = "Search repositories";

    [ObservableProperty]
    public partial string ErrorText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool HasActiveFilters { get; set; }

    [ObservableProperty]
    public partial bool IsApiCapped { get; set; }

    [ObservableProperty]
    public partial bool IsResultSetPartial { get; set; }

    [ObservableProperty]
    public partial int ReportedResultCount { get; set; }

    public bool HasResults => Results.Count > 0;

    public bool CanLoadMore =>
        !IsLoading &&
        !IsLoadingMore &&
        Results.Count > 0 &&
        Results.Count < _availableResultCount;

    internal Task PendingBackgroundRefresh => _backgroundRefreshTask;

    public async Task InitializeAsync(string query, CancellationToken cancellationToken = default)
    {
        TrackEvent("repository_search.opened", new Dictionary<string, string?>
        {
            ["page"] = "repository_search",
            ["source"] = "route"
        });
        QueryText = query?.Trim() ?? string.Empty;
        await SearchAsync(reset: true, forceRefresh: false, "initial", cancellationToken);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        return SearchAsync(reset: true, forceRefresh: true, "refresh", cancellationToken, "refresh");
    }

    public Task ApplySearchAsync(CancellationToken cancellationToken = default)
    {
        return SearchAsync(reset: true, forceRefresh: false, "manual", cancellationToken, "search");
    }

    public Task LoadNextPageAsync(CancellationToken cancellationToken = default)
    {
        if (!CanLoadMore)
        {
            return Task.CompletedTask;
        }

        return SearchAsync(reset: false, forceRefresh: false, "pagination", cancellationToken, "load_next_page");
    }

    internal void SetAutomationRepositories(IReadOnlyList<GitHubRepository> repositories)
    {
        _automationRepositories = repositories;
    }

    public void ClearFilter(string id)
    {
        switch (id)
        {
            case "owner": OwnerFilter = string.Empty; break;
            case "language": LanguageFilter = string.Empty; break;
            case "topic": TopicFilter = string.Empty; break;
            case "visibility": SelectedVisibility = VisibilityOptions[0]; break;
            case "fork": SelectedForkScope = ForkOptions[0]; break;
            case "archive": SelectedArchiveScope = ArchiveOptions[0]; break;
        }

        RefreshFilterChips();
        TrackEvent("repository_search.action.executed", new Dictionary<string, string?>
        {
            ["page"] = "repository_search",
            ["action"] = "clear_filter",
            ["filter_type"] = NormalizeFilterType(id),
            ["result"] = "success"
        });
    }

    public void ClearAllFilters()
    {
        OwnerFilter = string.Empty;
        LanguageFilter = string.Empty;
        TopicFilter = string.Empty;
        SelectedVisibility = VisibilityOptions[0];
        SelectedForkScope = ForkOptions[0];
        SelectedArchiveScope = ArchiveOptions[0];
        RefreshFilterChips();
        TrackSearchAction("clear_all_filters", TelemetryTaxonomy.Results.Success);
    }

    public void CancelPendingWork()
    {
        Interlocked.Increment(ref _requestVersion);
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _requestCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelPendingWork();
    }

    private async Task SearchAsync(
        bool reset,
        bool forceRefresh,
        string source,
        CancellationToken cancellationToken,
        string? action = null)
    {
        if (_disposed)
        {
            return;
        }

        RepositorySearchQuery query = BuildQuery();
        RefreshFilterChips();
        if (!HasSearchCriteria(query))
        {
            CancelPendingWork();
            Results.Clear();
            _pageSnapshots.Clear();
            _loadedPage = 0;
            _availableResultCount = 0;
            ReportedResultCount = 0;
            IsApiCapped = false;
            IsResultSetPartial = false;
            IsEmpty = false;
            HasError = false;
            ResultSummary = "Enter a repository, owner, language, or topic";
            RaiseCollectionStateChanged();
            TrackSearchAction(action, TelemetryTaxonomy.Results.Empty);
            return;
        }

        if (_automationRepositories is not null)
        {
            Stopwatch automationStopwatch = Stopwatch.StartNew();
            ApplyAutomationResult(query);
            automationStopwatch.Stop();
            TrackSearchLoaded("preview", CacheState.Fresh, automationStopwatch.Elapsed);
            TrackSearchAction(action, TelemetryTaxonomy.Results.Success, automationStopwatch.Elapsed);
            return;
        }

        int version = reset ? Interlocked.Increment(ref _requestVersion) : _requestVersion;
        CancellationTokenSource? localLinked = null;
        CancellationToken token;
        if (reset)
        {
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationTokenSource? previous = Interlocked.Exchange(ref _requestCancellation, linked);
            previous?.Cancel();
            previous?.Dispose();
            token = linked.Token;
        }
        else
        {
            CancellationTokenSource? current = _requestCancellation;
            if (current is null)
            {
                TrackSearchAction(action, "deferred");
                return;
            }

            localLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, current.Token);
            token = localLinked.Token;
        }

        try
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            if (reset)
            {
                _loadedPage = 0;
                _pageSnapshots.Clear();
            }

            int page = reset ? 1 : _loadedPage + 1;
            if (reset)
            {
                IsLoading = true;
            }
            else
            {
                IsLoadingMore = true;
            }

            HasError = false;
            ErrorText = string.Empty;
            try
            {
                string? accessToken = GetActiveToken();
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    throw new GitHubAuthenticationException("GitHub authentication is unavailable.");
                }

                string partition = GetActiveUserPartition(accessToken);
                CachedResult<GitHubRepositorySearchResponse> result = await _searchService.SearchAsync(
                    accessToken,
                    partition,
                    query,
                    page,
                    PageSize,
                    forceRefresh,
                    token);
                if (version != _requestVersion || result.Value is null)
                {
                    TrackSearchAction(action, TelemetryTaxonomy.Results.Cancelled, stopwatch.Elapsed);
                    return;
                }

                ApplyResult(result.Value, page, result.CacheState);
                stopwatch.Stop();
                TrackSearchLoaded(source, result.CacheState, stopwatch.Elapsed);
                TrackSearchAction(action, IsEmpty ? TelemetryTaxonomy.Results.Empty : TelemetryTaxonomy.Results.Success, stopwatch.Elapsed);
                if (page == 1 && result.IsRefreshInProgress)
                {
                    _backgroundRefreshTask = RefreshCachedFirstPageAsync(
                        accessToken,
                        partition,
                        query,
                        version,
                        token);
                }
            }
            catch (OperationCanceledException) when (version != _requestVersion || token.IsCancellationRequested)
            {
                stopwatch.Stop();
                TrackSearchAction(action, TelemetryTaxonomy.Results.Cancelled, stopwatch.Elapsed);
            }
            catch (GitHubAuthenticationException)
            {
                HasError = true;
                ErrorText = "GitHub authentication is no longer valid. Sign in again to search.";
                stopwatch.Stop();
                TrackSearchError(source, "authentication", stopwatch.Elapsed);
                TrackSearchAction(action, TelemetryTaxonomy.Results.AuthError, stopwatch.Elapsed);
            }
            catch (GitHubApiException ex)
            {
                HasError = true;
                ErrorText = JitHub.WinUI.Helpers.UserFacingError.For(
                    ex,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Loading,
                    "repository-search");
                stopwatch.Stop();
                TrackSearchError(source, "api", stopwatch.Elapsed);
                TrackSearchAction(action, TelemetryTaxonomy.Results.Error, stopwatch.Elapsed);
            }
            catch (HttpRequestException)
            {
                HasError = true;
                ErrorText = Results.Count > 0
                    ? LocalizedResourceText.GetString(
                        "RepositorySearch.LaterPageError",
                        "GitHub could not load more results. Existing results remain available.")
                    : "JitHub could not reach GitHub. Check your connection and retry.";
                stopwatch.Stop();
                TrackSearchError(source, "network", stopwatch.Elapsed);
                TrackSearchAction(action, TelemetryTaxonomy.Results.Error, stopwatch.Elapsed);
            }
            finally
            {
                if (version == _requestVersion)
                {
                    IsLoading = false;
                    IsLoadingMore = false;
                    IsEmpty = Results.Count == 0 && !HasError;
                    RaiseCollectionStateChanged();
                }
            }
        }
        finally
        {
            localLinked?.Dispose();
        }
    }

    private void ApplyAutomationResult(RepositorySearchQuery query)
    {
        IEnumerable<GitHubRepository> repositories = _automationRepositories!;
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            string text = query.Text.Trim();
            repositories = repositories.Where(repository =>
                repository.FullName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                (repository.Description?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (repository.Language?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false) ||
                repository.Topics.Any(topic => topic.Contains(text, StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(query.Owner))
        {
            repositories = repositories.Where(repository =>
                repository.Owner.Login.Contains(query.Owner.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Language))
        {
            repositories = repositories.Where(repository =>
                string.Equals(repository.Language, query.Language.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(query.Topic))
        {
            repositories = repositories.Where(repository =>
                repository.Topics.Any(topic => string.Equals(topic, query.Topic.Trim(), StringComparison.OrdinalIgnoreCase)));
        }

        repositories = query.Visibility switch
        {
            RepositorySearchVisibility.Public => repositories.Where(static repository => !repository.Private),
            RepositorySearchVisibility.Private => repositories.Where(static repository => repository.Private),
            _ => repositories
        };
        repositories = query.ForkScope switch
        {
            RepositorySearchForkScope.Sources => repositories.Where(static repository => !repository.Fork),
            RepositorySearchForkScope.Forks => repositories.Where(static repository => repository.Fork),
            _ => repositories
        };
        repositories = query.ArchiveScope switch
        {
            RepositorySearchArchiveScope.Active => repositories.Where(static repository => !repository.Archived),
            RepositorySearchArchiveScope.Archived => repositories.Where(static repository => repository.Archived),
            _ => repositories
        };
        repositories = query.Sort switch
        {
            RepositorySearchSort.RecentlyUpdated => repositories.OrderByDescending(static repository => repository.UpdatedAt),
            RepositorySearchSort.MostStars => repositories.OrderByDescending(static repository => repository.StargazersCount),
            RepositorySearchSort.MostForks => repositories.OrderByDescending(static repository => repository.ForksCount),
            _ => repositories
        };

        GitHubRepository[] items = repositories.ToArray();
        _loadedPage = 0;
        _pageSnapshots.Clear();
        HasError = false;
        ErrorText = string.Empty;
        IsLoading = false;
        IsLoadingMore = false;
        ApplyResult(new GitHubRepositorySearchResponse { TotalCount = items.Length, Items = items }, 1, CacheState.Fresh);
    }

    private async Task RefreshCachedFirstPageAsync(
        string accessToken,
        string partition,
        RepositorySearchQuery query,
        int version,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            CachedResult<GitHubRepositorySearchResponse> refreshed = await _searchService.SearchAsync(
                accessToken,
                partition,
                query,
                1,
                PageSize,
                forceRefresh: true,
                cancellationToken);
            if (version == _requestVersion && refreshed.Value is not null)
            {
                ApplyResult(
                    refreshed.Value,
                    1,
                    refreshed.CacheState,
                    preserveFollowingPages: _loadedPage > 1);
                stopwatch.Stop();
                TrackSearchLoaded("background", refreshed.CacheState, stopwatch.Elapsed);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // Cached rows remain visible. Explicit retries surface errors through SearchAsync.
            stopwatch.Stop();
            TrackSearchError("background", GetErrorKind(ex), stopwatch.Elapsed);
        }
    }

    private void ApplyResult(
        GitHubRepositorySearchResponse response,
        int page,
        CacheState cacheState,
        bool preserveFollowingPages = false)
    {
        if (page == 1 && !preserveFollowingPages)
        {
            _pageSnapshots.Clear();
        }

        _pageSnapshots[page] = response.Items;
        IReadOnlyList<GitHubRepository> snapshots = _pageSnapshots
            .OrderBy(static pair => pair.Key)
            .SelectMany(static pair => pair.Value)
            .DistinctBy(RepositoryKey, StringComparer.Ordinal)
            .ToArray();

        Results.ApplySnapshot(
            snapshots,
            RepositoryKey,
            static item => item.Key,
            static repository => new RepositorySearchResultItem(repository),
            static (item, repository) => item.Update(repository));
        _loadedPage = Math.Max(_loadedPage, page);
        _availableResultCount = Math.Min(
            GitHubSearchResultLimit,
            Math.Max(response.TotalCount, Results.Count));
        ReportedResultCount = Math.Max(response.TotalCount, Results.Count);
        IsApiCapped = ReportedResultCount > GitHubSearchResultLimit;
        IsResultSetPartial = Results.Count < _availableResultCount;
        ResultSummary = BuildResultSummary(cacheState);
        IsEmpty = Results.Count == 0;
        RaiseCollectionStateChanged();
    }

    private string BuildResultSummary(CacheState cacheState)
    {
        string updateSuffix = cacheState is CacheState.Fresh or CacheState.Miss
            ? string.Empty
            : LocalizedResourceText.GetString("RepositorySearch.UpdatingSuffix", " - updating");
        if (IsApiCapped)
        {
            return LocalizedResourceText.Format(
                "RepositorySearch.ApiLimitedSummary",
                "{0:N0} of {1:N0} accessible repositories - GitHub limits each search to {2:N0} results",
                Results.Count,
                GitHubSearchResultLimit,
                GitHubSearchResultLimit) + updateSuffix;
        }

        if (IsResultSetPartial)
        {
            return LocalizedResourceText.Format(
                "RepositorySearch.PartialSummary",
                "{0:N0} of {1:N0} repositories",
                Results.Count,
                _availableResultCount) + updateSuffix;
        }

        return _availableResultCount == 1
            ? LocalizedResourceText.GetString("RepositorySearch.SingleSummary", "1 repository") + updateSuffix
            : LocalizedResourceText.Format(
                "RepositorySearch.PluralSummary",
                "{0:N0} repositories",
                _availableResultCount) + updateSuffix;
    }

    private RepositorySearchQuery BuildQuery() => new(
        QueryText.Trim(),
        OwnerFilter.Trim(),
        LanguageFilter.Trim(),
        TopicFilter.Trim(),
        SelectedVisibility switch
        {
            "Public" => RepositorySearchVisibility.Public,
            "Private" => RepositorySearchVisibility.Private,
            _ => RepositorySearchVisibility.Any
        },
        SelectedForkScope switch
        {
            "Sources" => RepositorySearchForkScope.Sources,
            "Forks" => RepositorySearchForkScope.Forks,
            _ => RepositorySearchForkScope.Any
        },
        SelectedArchiveScope switch
        {
            "Active" => RepositorySearchArchiveScope.Active,
            "Archived" => RepositorySearchArchiveScope.Archived,
            _ => RepositorySearchArchiveScope.Any
        },
        SelectedSort switch
        {
            "Recently updated" => RepositorySearchSort.RecentlyUpdated,
            "Most stars" => RepositorySearchSort.MostStars,
            "Most forks" => RepositorySearchSort.MostForks,
            _ => RepositorySearchSort.BestMatch
        });

    private void RefreshFilterChips()
    {
        ActiveFilters.Clear();
        AddFilterChip("owner", "Owner", OwnerFilter);
        AddFilterChip("language", "Language", LanguageFilter);
        AddFilterChip("topic", "Topic", TopicFilter);
        if (SelectedVisibility != VisibilityOptions[0]) ActiveFilters.Add(new("visibility", SelectedVisibility));
        if (SelectedForkScope != ForkOptions[0]) ActiveFilters.Add(new("fork", SelectedForkScope));
        if (SelectedArchiveScope != ArchiveOptions[0]) ActiveFilters.Add(new("archive", SelectedArchiveScope));
        HasActiveFilters = ActiveFilters.Count > 0;
    }

    private void AddFilterChip(string id, string label, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            ActiveFilters.Add(new RepositorySearchFilterChip(id, $"{label}: {value.Trim()}"));
        }
    }

    private void RaiseCollectionStateChanged()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CanLoadMore));
    }

    private string? GetActiveToken() =>
        _authService.GetToken(_authService.AuthenticatedUser?.Id ?? _accountService.GetUser());

    private string GetActiveUserPartition(string accessToken) =>
        GitHubAuthenticationConstants.IsPublicAccessToken(accessToken)
            ? "public-preview"
            : (_authService.AuthenticatedUser?.Id ?? _accountService.GetUser()).ToString();

    private static bool HasSearchCriteria(RepositorySearchQuery query) =>
        !string.IsNullOrWhiteSpace(query.Text) ||
        !string.IsNullOrWhiteSpace(query.Owner) ||
        !string.IsNullOrWhiteSpace(query.Language) ||
        !string.IsNullOrWhiteSpace(query.Topic) ||
        query.Visibility != RepositorySearchVisibility.Any ||
        query.ForkScope != RepositorySearchForkScope.Any ||
        query.ArchiveScope != RepositorySearchArchiveScope.Any;

    private static string RepositoryKey(GitHubRepository repository) =>
        repository.Id != 0 ? repository.Id.ToString() : repository.FullName;

    private void TrackSearchAction(string? action, string result, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        TrackEvent("repository_search.action.executed", new Dictionary<string, string?>
        {
            ["page"] = "repository_search",
            ["action"] = action,
            ["result"] = result,
            ["duration_bucket"] = duration is null
                ? null
                : TelemetrySanitizer.CreateDurationBucket(duration.Value)
        });
    }

    private void TrackSearchLoaded(string source, CacheState cacheState, TimeSpan duration)
    {
        TrackEvent("repository_search.loaded", new Dictionary<string, string?>
        {
            ["page"] = "repository_search",
            ["source"] = source,
            ["result"] = IsEmpty ? "empty" : "success",
            ["cache_state"] = cacheState.ToString().ToLowerInvariant(),
            ["count_bucket"] = CreateCountBucket(Results.Count),
            ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
        });
    }

    private void TrackSearchError(string source, string errorKind, TimeSpan duration)
    {
        TrackEvent("repository_search.error", new Dictionary<string, string?>
        {
            ["page"] = "repository_search",
            ["source"] = source,
            ["result"] = "error",
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
            // Search remains functional if diagnostics are unavailable.
        }
    }

    private static string NormalizeFilterType(string id) => id switch
    {
        "owner" or "language" or "topic" or "visibility" or "fork" or "archive" => id,
        _ => "unknown"
    };

    private static string GetErrorKind(Exception exception) => exception switch
    {
        OperationCanceledException => "canceled",
        GitHubAuthenticationException => "authentication",
        GitHubApiException => "api",
        HttpRequestException => "network",
        _ => "unexpected"
    };

    private static string CreateCountBucket(int count) => TelemetryTaxonomy.CountBucket(count);
}

public sealed partial class RepositorySearchResultItem : ObservableObject
{
    public RepositorySearchResultItem(GitHubRepository repository)
    {
        Repository = repository;
    }

    public GitHubRepository Repository { get; private set; }

    public string Key => Repository.Id != 0 ? Repository.Id.ToString() : Repository.FullName;

    public string FullName => Repository.FullName;

    public string Description => string.IsNullOrWhiteSpace(Repository.Description)
        ? "No description"
        : Repository.Description;

    public string OwnerAvatarUrl => Repository.Owner.AvatarUrl ?? string.Empty;

    public string Language => Repository.Language ?? string.Empty;

    public bool HasLanguage => !string.IsNullOrWhiteSpace(Language);

    public string LanguageColor => RepositoryLanguageColorPalette.GetHex(Language);

    public string StarsText => Repository.StargazersCount.ToString("N0");

    public string ForksText => Repository.ForksCount.ToString("N0");

    public string UpdatedText => Repository.UpdatedAt is DateTimeOffset updated
        ? $"Updated {RelativeTime(updated)}"
        : string.Empty;

    public string VisibilityLabel => Repository.Private ? "Private" : "Public";

    public bool IsFork => Repository.Fork;

    public bool IsArchived => Repository.Archived;

    public bool Update(GitHubRepository repository)
    {
        Repository = repository;
        OnPropertyChanged(string.Empty);
        return true;
    }

    private static string RelativeTime(DateTimeOffset timestamp)
    {
        TimeSpan age = DateTimeOffset.UtcNow - timestamp;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)age.TotalMinutes)}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)age.TotalHours)}h ago";
        if (age < TimeSpan.FromDays(30)) return $"{Math.Max(1, (int)age.TotalDays)}d ago";
        if (age < TimeSpan.FromDays(365)) return $"{Math.Max(1, (int)(age.TotalDays / 30))}mo ago";
        return $"{Math.Max(1, (int)(age.TotalDays / 365))}y ago";
    }

}

public sealed record RepositorySearchFilterChip(string Id, string Label)
{
    public string AutomationId => $"RepoSearchFilterChip_{Id}";
}
