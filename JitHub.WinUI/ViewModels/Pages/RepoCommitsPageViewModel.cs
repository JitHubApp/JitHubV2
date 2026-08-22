using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Common;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;

namespace JitHub.WinUI.ViewModels.Pages;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class RepoCommitsPageViewModel : ViewModelBase
{
    private const int ParsedDiffCacheCapacity = 4;
    private const long ParsedDiffCacheByteLimit = 64L * 1024 * 1024;
    private const long ParsedDiffPrefetchInputByteLimit = 32L * 1024 * 1024;
    private static readonly TimeSpan HoverPrefetchDebounce = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan InputCriticalDetailDeferral = TimeSpan.FromMilliseconds(20);
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;
    private readonly IGitHubCommitQueryService _commitQueryService;
    private readonly ICommitNavigationCache _commitNavigationCache;
    private readonly ITelemetryService _telemetryService;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly IAdaptivePrefetchPolicy _prefetchPolicy;
    private readonly List<GitHubCommit> _loadedCommits = [];
    private readonly CommitListQueryOptions _commitQuery = new();
    private CommitPageNavArg? _navArg;
    private int _listRequestId;
    private int _detailRequestId;
    private int _diffRequestId;
    private int _compareDiffRequestId;
    private int _diffProjectionRequestId;
    private CancellationTokenSource? _diffBuildCancellationTokenSource;
    private CancellationTokenSource? _compareDiffBuildCancellationTokenSource;
    private bool _suppressSelectionChanged;
    private IDisposable? _selectionDwellPrefetch;
    private IDisposable? _neighborPrefetch;
    private readonly LatestWinsPrefetchScheduler _hoverPrefetch = new();
    private string? _pinnedCommitSha;
    private string? _lastFocusedCommitSha;
    private string? _loadedCommitQueryIdentity;
    private string? _projectedCommitDetailSha;
    private string? _projectedDiffSha;
    private string? _projectedDiffRowsSha;
    private int _navigationGeneration;
    private readonly CommitDiffDocumentCache _parsedDiffCache = new(
        ParsedDiffCacheCapacity,
        ParsedDiffCacheByteLimit);
    private Task _commitListCompletionTask = Task.CompletedTask;
    private CommitSectionState _commitListState = new(CacheState.Miss, Completeness: PagedDataCompleteness.Loading);
    private CommitSectionState _branchListState = new(CacheState.Miss, Completeness: PagedDataCompleteness.Loading);

    public RepoCommitsPageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _gitHubClientService = GetService<IGitHubClientService>();
        _commitQueryService = GetService<IGitHubCommitQueryService>();
        _commitNavigationCache = GetService<ICommitNavigationCache>();
        _telemetryService = SafeTelemetryService.Wrap(GetService<ITelemetryService>());
        _taskCoordinator = GetService<IApplicationTaskCoordinator>();
        _prefetchPolicy = GetService<IAdaptivePrefetchPolicy>();
        NextDiffMatchCommand = new RelayCommand(NextDiffMatch, CanNavigateDiffMatches);
        PreviousDiffMatchCommand = new RelayCommand(PreviousDiffMatch, CanNavigateDiffMatches);

        DiffModeOptions =
        [
            new QueryOption(nameof(CommitDiffViewMode.Unified), GetString("RepoCommits.DiffUnified", "Unified"))
        ];
        SelectedDiffModeOption = DiffModeOptions[0];
        SelectedSection = CommitWorkspaceSection.Diff;
        StatusText = GetString("RepoCommits.LoadingStatusShort", "Loading commits...");
    }

    public ObservableCollection<GitHubBranch> Branches { get; } = [];

    public ObservableCollection<GitHubCommit> Commits { get; } = [];

    public ObservableCollection<GitHubCommitComment> CommitComments { get; } = [];

    public ObservableCollection<GitHubCheckRun> CheckRuns { get; } = [];

    public ObservableCollection<GitHubCommitStatus> CommitStatuses { get; } = [];

    public ObservableCollection<GitHubPullRequest> AssociatedPullRequests { get; } = [];

    public ObservableCollection<GitHubCommitParent> ParentCommits { get; } = [];

    public List<QueryOption> DiffModeOptions { get; }

    public IRelayCommand NextDiffMatchCommand { get; }

    public IRelayCommand PreviousDiffMatchCommand { get; }

    public CommitPageNavArg? NavigationArgs => _navArg;

    public string RepositoryFullName => _navArg?.Repo is null
        ? string.Empty
        : $"{_navArg.Repo.Owner.Login}/{_navArg.Repo.Name}";

    public bool HasSelectedCommit => SelectedCommit is not null;

    public bool IsDetailPlaceholderVisible => SelectedCommit is null;

    public bool IsCommitDetailCoherent(GitHubCommit commit) =>
        IsSelectedCommit(commit) &&
        string.Equals(_projectedCommitDetailSha, commit.Sha, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(_projectedDiffSha, commit.Sha, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(_projectedDiffRowsSha, commit.Sha, StringComparison.OrdinalIgnoreCase) &&
        !IsDiffLoading;

    public string SelectedCommitTitle => SelectedCommit?.SummaryMessage ?? GetString("RepoCommits.SelectCommitTitle", "Select a commit");

    public string SelectedCommitShaText => SelectedCommit is null ? string.Empty : SelectedCommit.ShortSha;

    public string SelectedCommitAuthor => SelectedCommit?.AuthorDisplayName ?? string.Empty;

    public string? SelectedCommitAuthorLogin =>
        UserIdentityNavigationPolicy.GetRoutableLogin(SelectedCommit?.Author?.Login);

    public string SelectedCommitAvatarUrl => SelectedCommit?.Author?.AvatarUrl ?? string.Empty;

    public string SelectedCommitAuthorAutomationId => SelectedCommit?.AutomationId ?? "RepoCommitSelected_none";

    public string SelectedCommitMetaText => SelectedCommit is null
        ? GetString("RepoCommits.SelectCommitDescription", "Choose a commit to inspect its details.")
        : FormatString(
            "RepoCommits.CommitTimeFormat",
            "Committed {0}",
            FormatTimeAgo(SelectedCommit.Commit.Author.Date));

    public string SelectedCommitStatsText => SelectedCommit?.Stats?.SummaryText ?? string.Empty;

    public string SelectedCommitVerificationText => SelectedCommit?.Commit.Verification.DisplayText ?? string.Empty;

    public string SelectedCommitBodyText => SelectedCommit?.Commit.Message ?? string.Empty;

    public MarkdownDocumentSource? CommitBodyMarkdownSource => SelectedCommit?.MarkdownSource;

    public MarkdownDocumentSource? CommitCommentMarkdownSource => _navArg is null || SelectedCommit is null
        ? null
        : MarkdownDocumentSourceFactory.CreateRepositoryDocument(
            "commit-comment-draft",
            SelectedCommit.Sha,
            _navArg.Repo.Owner.Login,
            _navArg.Repo.Name,
            SelectedCommit.Sha);

    public string SelectedCommitCommentsText => FormatString("RepoCommits.CommentCount", "{0} comments", CommitComments.Count);

    public string CheckSummaryText => CheckRuns.Count == 0 && CommitStatuses.Count == 0
        ? GetString("RepoCommits.NoChecks", "No checks reported.")
        : FormatString("RepoCommits.CheckSummary", "{0} checks · {1} statuses", CheckRuns.Count, CommitStatuses.Count);

    public string AssociatedPullRequestsText => AssociatedPullRequests.Count == 0
        ? GetString("RepoCommits.NoAssociatedPullRequests", "No associated pull requests.")
        : FormatString("RepoCommits.AssociatedPullRequestsCount", "{0} associated PRs", AssociatedPullRequests.Count);

    public bool AreCommitActionsEnabled => SelectedCommit is not null && !IsCommitDetailsLoading;

    public bool IsCommitCommentEnabled =>
        SelectedCommit is not null &&
        !IsCommitCommentSubmissionInProgress &&
        !string.IsNullOrWhiteSpace(CommentText);

    public bool IsDiffSectionVisible => SelectedSection == CommitWorkspaceSection.Diff;

    public bool IsCommentsSectionVisible => SelectedSection == CommitWorkspaceSection.Comments;

    public bool IsChecksSectionVisible => SelectedSection == CommitWorkspaceSection.Checks;

    public bool IsCompareSectionVisible => SelectedSection == CommitWorkspaceSection.Compare;

    public bool IsEmpty => Commits.Count == 0 && !IsLoadingCommits;

    public bool IsDiffStatusVisible => IsDiffLoading || !string.IsNullOrWhiteSpace(DiffStatusText);

    public CommitDiffViewMode SelectedDiffViewMode => CommitDiffViewMode.Unified;

    public bool HasDiffSearchMatches => DiffSearchMatchCount > 0;

    public string DiffSearchMatchCountText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DiffSearchText))
            {
                return string.Empty;
            }

            if (DiffSearchMatchCount == 0)
            {
                return GetString("RepoCommits.DiffSearchNoMatches", "No matches");
            }

            return FormatString(
                "RepoCommits.DiffSearchMatchCount",
                "{0} of {1}",
                Math.Clamp(SelectedDiffSearchMatchIndex + 1, 1, DiffSearchMatchCount),
                DiffSearchMatchCount);
        }
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CommitListScopeNotice { get; set; } = string.Empty;

    public bool HasCommitListScopeNotice => !string.IsNullOrWhiteSpace(CommitListScopeNotice);

    [ObservableProperty]
    public partial GitHubBranch? SelectedBranch { get; set; }

    [ObservableProperty]
    public partial QueryOption? SelectedDiffModeOption { get; set; }

    [ObservableProperty]
    public partial GitHubCommit? SelectedCommit { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PathFilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AuthorFilterText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSinceFilter))]
    [NotifyPropertyChangedFor(nameof(SinceFilterLabel))]
    public partial DateTimeOffset? SinceFilterDate { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUntilFilter))]
    [NotifyPropertyChangedFor(nameof(UntilFilterLabel))]
    public partial DateTimeOffset? UntilFilterDate { get; set; }

    public bool HasSinceFilter => SinceFilterDate.HasValue;

    public bool HasUntilFilter => UntilFilterDate.HasValue;

    public string SinceFilterLabel => SinceFilterDate is DateTimeOffset date
        ? $"Since {date.ToString("d", CultureInfo.CurrentCulture)}"
        : string.Empty;

    public string UntilFilterLabel => UntilFilterDate is DateTimeOffset date
        ? $"Until {date.ToString("d", CultureInfo.CurrentCulture)}"
        : string.Empty;

    [ObservableProperty]
    public partial string DiffFileFilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DiffSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int DiffSearchMatchCount { get; set; }

    [ObservableProperty]
    public partial int SelectedDiffSearchMatchIndex { get; set; } = -1;

    [ObservableProperty]
    public partial string CommentText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CompareBaseText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CompareHeadText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CompareSummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoadingCommits { get; set; }

    [ObservableProperty]
    public partial bool IsCommitDetailsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsCommitCommentSubmissionInProgress { get; set; }

    [ObservableProperty]
    public partial bool IsDiffLoading { get; set; }

    [ObservableProperty]
    public partial string DiffStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial CommitWorkspaceSection SelectedSection { get; set; }

    [ObservableProperty]
    public partial CommitDiffDocument DiffDocument { get; set; } = CommitDiffDocument.Empty;

    [ObservableProperty]
    public partial CommitDiffDocument CompareDiffDocument { get; set; } = CommitDiffDocument.Empty;

    [ObservableProperty]
    public partial CommitDiffRowProjection DiffRowProjection { get; set; } = CommitDiffRowProjection.Empty;

    [ObservableProperty]
    public partial CommitDiffRowProjection CompareDiffRowProjection { get; set; } = CommitDiffRowProjection.Empty;

    public async Task InitializeAsync(CommitPageNavArg? navArg)
    {
        CancelPredictivePrefetches();
        _navArg = navArg;
        _loadedCommits.Clear();
        _pinnedCommitSha = null;
        _lastFocusedCommitSha = null;
        SetSelectedCommit(null);
        ResetCommitDetails();

        if (navArg?.Repo is null)
        {
            StatusText = GetString("RepoCommits.InvalidNavigation", "JitHub could not determine which repository commits to load.");
            return;
        }

        ApplyNavigationDefaults(navArg);
        TrackEvent("commits.opened", new Dictionary<string, string?> { ["page"] = "repo" });
        _ = LoadBranchesAsync();
        await LoadCommitsAsync(navArg.GitRef);
    }

    public async Task ApplyFiltersAsync()
    {
        string currentIdentity = CreateCommitQueryIdentity(_commitQuery);
        ApplyQueryFromFilters();
        if (string.Equals(currentIdentity, CreateCommitQueryIdentity(_commitQuery), StringComparison.Ordinal))
        {
            return;
        }

        TrackEvent("commits.filter.changed", new Dictionary<string, string?> { ["page"] = "repo", ["filter_type"] = "list" });
        await LoadCommitsAsync(SelectedCommit?.Sha, preservePreferredCommitOutsideQuery: true, preserveCurrentDetailDuringLoad: true);
    }

    public async Task RefreshAsync()
    {
        await LoadCommitsAsync(SelectedCommit?.Sha, preservePreferredCommitOutsideQuery: true, preserveCurrentDetailDuringLoad: true);
    }

    public void SetSection(CommitWorkspaceSection section)
    {
        SelectedSection = section;
    }

    private void NextDiffMatch()
    {
        if (DiffSearchMatchCount <= 0)
        {
            return;
        }

        SelectedDiffSearchMatchIndex = (SelectedDiffSearchMatchIndex + 1) % DiffSearchMatchCount;
        NotifyDiffSearchPropertiesChanged();
    }

    private void PreviousDiffMatch()
    {
        if (DiffSearchMatchCount <= 0)
        {
            return;
        }

        int next = SelectedDiffSearchMatchIndex <= 0
            ? DiffSearchMatchCount - 1
            : SelectedDiffSearchMatchIndex - 1;
        SelectedDiffSearchMatchIndex = next;
        NotifyDiffSearchPropertiesChanged();
    }

    private bool CanNavigateDiffMatches() => DiffSearchMatchCount > 0;

    public void PrefetchCommit(GitHubCommit? commit, CommitPrefetchReason reason)
    {
        if (_navArg?.Repo is null || commit is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        string userPartition = GetActiveUserPartition(token);
        string owner = _navArg.Repo.Owner.Login;
        string repositoryName = _navArg.Repo.Name;
        _hoverPrefetch.Schedule(
            HoverPrefetchDebounce,
            () => ScheduleTrackedPrefetch(
                token,
                userPartition,
                owner,
                repositoryName,
                commit.Sha,
                reason,
                TimeSpan.Zero));
    }

    public void CancelPredictivePrefetches()
    {
        Interlocked.Increment(ref _navigationGeneration);
        Interlocked.Increment(ref _listRequestId);
        Interlocked.Increment(ref _detailRequestId);
        Interlocked.Increment(ref _diffProjectionRequestId);
        _hoverPrefetch.Cancel();
        _selectionDwellPrefetch?.Dispose();
        _selectionDwellPrefetch = null;
        _neighborPrefetch?.Dispose();
        _neighborPrefetch = null;
        CancelDiffBuild();
        CancelCompareDiffBuild();
    }

    public async Task AddCommitCommentAsync()
    {
        if (_navArg?.Repo is null || SelectedCommit is null || !IsCommitCommentEnabled || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubCommit currentCommit = SelectedCommit;
        IsCommitCommentSubmissionInProgress = true;
        NotifyActionPropertiesChanged();
        try
        {
            GitHubCommitComment comment = await _gitHubClientService.CreateCommitCommentAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentCommit.Sha,
                CommentText);
            if (IsSelectedCommit(currentCommit))
            {
                CommentText = string.Empty;
                ReplaceCollectionByKey(
                    CommitComments,
                    CommitComments.Concat([comment]).OrderBy(item => item.CreatedAt),
                    static commentItem => commentItem.Id.ToString(CultureInfo.InvariantCulture));
                NotifyCommentPropertiesChanged();
                StoreNavigationSnapshot(currentCommit, "comment");
                StatusText = GetString("RepoCommits.CommentAdded", "Comment added.");
            }
            TrackCommitAction(CommitActionKind.Comment, CommitActionOutcome.Success);
        }
        catch (GitHubAuthenticationException)
        {
            TrackCommitAction(CommitActionKind.Comment, CommitActionOutcome.AuthenticationError);
            _authService.SignOut();
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException)
        {
            TrackCommitAction(CommitActionKind.Comment, CommitActionOutcome.Failure);
            if (IsSelectedCommit(currentCommit))
            {
                StatusText = GetString("RepoCommits.CommentFailed", "JitHub could not add the commit comment.");
            }
        }
        finally
        {
            IsCommitCommentSubmissionInProgress = false;
            NotifyActionPropertiesChanged();
        }
    }

    public async Task RunCompareAsync()
    {
        if (_navArg?.Repo is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        string baseRef = NormalizeFilterText(CompareBaseText);
        string headRef = NormalizeFilterText(CompareHeadText);
        if (string.IsNullOrWhiteSpace(baseRef) || string.IsNullOrWhiteSpace(headRef))
        {
            StatusText = GetString("RepoCommits.CompareRequiresRefs", "Choose both a base and head ref to compare.");
            return;
        }

        Stopwatch compareDuration = Stopwatch.StartNew();
        try
        {
            CachedResult<GitHubCompareResult> result = await _commitQueryService.CompareCommitsAsync(
                token,
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                baseRef,
                headRef);
            GitHubCompareResult? compare = result.Value;
            CompareSummaryText = compare is null
                ? GetString("RepoCommits.CompareUnavailable", "Compare data unavailable.")
                : FormatString(
                    "RepoCommits.CompareSummary",
                    "{0} commits · +{1} -{2}",
                    compare.TotalCommits,
                    compare.Files.Sum(file => file.Additions),
                    compare.Files.Sum(file => file.Deletions));
            await BuildCompareDiffDocumentAsync(compare?.Files ?? []);
            TrackEvent(
                "commits.compare.opened",
                new Dictionary<string, string?>
                {
                    ["page"] = "repo",
                    ["result"] = compare is null ? "empty" : "success",
                    ["cache_state"] = result.CacheState.ToString().ToLowerInvariant(),
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(compareDuration.Elapsed)
                });
        }
        catch (GitHubAuthenticationException)
        {
            TrackEvent(
                "commits.compare.opened",
                new Dictionary<string, string?>
                {
                    ["page"] = "repo",
                    ["result"] = "auth_error",
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(compareDuration.Elapsed)
                });
            _authService.SignOut();
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException)
        {
            StatusText = GetString("RepoCommits.CompareFailed", "JitHub could not compare those refs.");
            TrackEvent(
                "commits.compare.opened",
                new Dictionary<string, string?>
                {
                    ["page"] = "repo",
                    ["result"] = "error",
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(compareDuration.Elapsed)
                });
        }
    }

    partial void OnSelectedCommitChanged(GitHubCommit? value)
    {
        if (_suppressSelectionChanged)
        {
            return;
        }

        CancelDiffBuild();

        if (value is null)
        {
            _selectionDwellPrefetch?.Dispose();
            _neighborPrefetch?.Dispose();
            _selectionDwellPrefetch = null;
            _neighborPrefetch = null;
            ResetCommitDetails();
            NotifySelectedCommitPropertiesChanged();
            return;
        }

        _lastFocusedCommitSha = value.Sha;
        _ = ShowCommitAfterInputCommitAsync(value);
    }

    partial void OnSelectedSectionChanged(CommitWorkspaceSection value)
    {
        OnPropertyChanged(nameof(IsDiffSectionVisible));
        OnPropertyChanged(nameof(IsCommentsSectionVisible));
        OnPropertyChanged(nameof(IsChecksSectionVisible));
        OnPropertyChanged(nameof(IsCompareSectionVisible));
        UpdateDiffSearchStateForActiveProjection();
        TrackEvent(
            "commits.section.opened",
            new Dictionary<string, string?>
            {
                ["page"] = "repo",
                ["section"] = TelemetryTaxonomy.EnumValue(value)
            });
    }

    partial void OnSelectedDiffModeOptionChanged(QueryOption? value)
    {
        OnPropertyChanged(nameof(SelectedDiffViewMode));
        TrackEvent(
            "commits.diff.mode.changed",
            new Dictionary<string, string?>
            {
                ["page"] = "repo",
                ["view_mode"] = SelectedDiffViewMode.ToString().ToLowerInvariant()
            });
    }

    partial void OnIsDiffLoadingChanged(bool value) => OnPropertyChanged(nameof(IsDiffStatusVisible));

    partial void OnDiffStatusTextChanged(string value) => OnPropertyChanged(nameof(IsDiffStatusVisible));

    partial void OnDiffFileFilterTextChanged(string value) => QueueDiffProjectionUpdate();

    partial void OnDiffSearchTextChanged(string value) => QueueDiffProjectionUpdate();

    partial void OnDiffDocumentChanged(CommitDiffDocument value) => QueueDiffProjectionUpdate(debounce: false);

    partial void OnCompareDiffDocumentChanged(CommitDiffDocument value) => QueueDiffProjectionUpdate(debounce: false);

    partial void OnSelectedDiffSearchMatchIndexChanged(int value) => NotifyDiffSearchPropertiesChanged();

    partial void OnCommentTextChanged(string value) => NotifyActionPropertiesChanged();

    private void QueueDiffProjectionUpdate(bool debounce = true)
    {
        int requestId = Interlocked.Increment(ref _diffProjectionRequestId);
        string? diffSha = _projectedDiffSha;
        CommitDiffDocument diffDocument = DiffDocument;
        CommitDiffDocument compareDiffDocument = CompareDiffDocument;
        string fileFilterText = DiffFileFilterText;
        string searchText = DiffSearchText;
        _ = BuildDiffRowProjectionsAsync(
            diffDocument,
            compareDiffDocument,
            fileFilterText,
            searchText,
            diffSha,
            requestId,
            debounce);
    }

    private async Task BuildDiffRowProjectionsAsync(
        CommitDiffDocument diffDocument,
        CommitDiffDocument compareDiffDocument,
        string fileFilterText,
        string searchText,
        string? diffSha,
        int requestId,
        bool debounce)
    {
        if (debounce)
        {
            await Task.Delay(160);
        }

        CommitDiffRowProjection diffProjection = await Task.Run(() =>
            CommitDiffRowProjection.Create(diffDocument, fileFilterText, searchText));
        CommitDiffRowProjection compareProjection = await Task.Run(() =>
            CommitDiffRowProjection.Create(compareDiffDocument, fileFilterText, searchText));

        if (requestId != _diffProjectionRequestId)
        {
            return;
        }

        DiffRowProjection = diffProjection;
        CompareDiffRowProjection = compareProjection;
        _projectedDiffRowsSha = diffSha;
        UpdateDiffSearchStateForActiveProjection();
    }

    private void UpdateDiffSearchStateForActiveProjection()
    {
        CommitDiffRowProjection projection = IsCompareSectionVisible ? CompareDiffRowProjection : DiffRowProjection;
        DiffSearchMatchCount = projection.MatchCount;

        if (projection.MatchCount == 0)
        {
            if (SelectedDiffSearchMatchIndex != -1)
            {
                SelectedDiffSearchMatchIndex = -1;
            }
        }
        else if (SelectedDiffSearchMatchIndex < 0 || SelectedDiffSearchMatchIndex >= projection.MatchCount)
        {
            SelectedDiffSearchMatchIndex = 0;
        }

        NotifyDiffSearchPropertiesChanged();
    }

    private void NotifyDiffSearchPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasDiffSearchMatches));
        OnPropertyChanged(nameof(DiffSearchMatchCountText));
        NextDiffMatchCommand.NotifyCanExecuteChanged();
        PreviousDiffMatchCommand.NotifyCanExecuteChanged();
    }

    private async Task LoadBranchesAsync()
    {
        if (_navArg?.Repo is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        try
        {
            CommitPagedSection<GitHubBranch> result = await _commitQueryService.GetAllBranchesAsync(
                token,
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name);
            _branchListState = result.State;
            UpdateCommitListScopeNotice();
            string? selectedBranchName = SelectedBranch?.Name;
            GitHubBranch[] branchProjection = PagedRefreshProjectionPolicy.Merge(
                result.Items,
                Branches,
                static branch => branch.Name,
                result.State.Completeness);
            ReplaceCollectionByKey(Branches, branchProjection, static branch => branch.Name);
            SelectedBranch = Branches.FirstOrDefault(branch => string.Equals(
                    branch.Name,
                    selectedBranchName,
                    StringComparison.OrdinalIgnoreCase))
                ?? Branches.FirstOrDefault(branch => string.Equals(
                    branch.Name,
                    _commitQuery.GitRef,
                    StringComparison.OrdinalIgnoreCase))
                ?? Branches.FirstOrDefault();
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch
        {
            // Branch loading is useful, but commits can still load with the default ref.
        }
    }

    private async Task LoadCommitsAsync(
        string? preferredSha = null,
        bool preservePreferredCommitOutsideQuery = true,
        bool preserveCurrentDetailDuringLoad = false)
    {
        if (_navArg?.Repo is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        int requestId = ++_listRequestId;
        Stopwatch loadDuration = Stopwatch.StartNew();
        IsLoadingCommits = true;
        StatusText = GetString("RepoCommits.LoadingStatusShort", "Loading commits...");
        try
        {
            string accountPartition = GetActiveUserPartition(token);
            string queryIdentity = CreateCommitQueryIdentity(_commitQuery);
            bool isSameQuery = string.Equals(_loadedCommitQueryIdentity, queryIdentity, StringComparison.Ordinal);
            CachedResult<GitHubCommit[]> firstPage = await _commitQueryService.GetCommitsAsync(
                token,
                accountPartition,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                _commitQuery,
                100,
                1);
            _commitListState = new CommitSectionState(
                firstPage.CacheState,
                firstPage.IsRefreshInProgress,
                firstPage.RefreshError is null
                    ? null
                    : JitHub.WinUI.Helpers.UserFacingError.For(
                        firstPage.RefreshError,
                        JitHub.WinUI.Helpers.UserFacingErrorKind.Refresh,
                        "commit-list"),
                PagedDataCompleteness.Loading,
                firstPage.Value?.Length ?? 0,
                1);
            UpdateCommitListScopeNotice();
            if (requestId != _listRequestId)
            {
                return;
            }

            List<GitHubCommit> loadedCommits = MergeCommitRows(
                firstPage.Value ?? [],
                _loadedCommits,
                preserveExistingTail: isSameQuery);
            string? commitShaToSelect = !string.IsNullOrWhiteSpace(preferredSha)
                ? preferredSha
                : _navArg.NoRef ? null : _navArg.GitRef;
            if (!string.IsNullOrWhiteSpace(commitShaToSelect) &&
                loadedCommits.All(commit => !string.Equals(commit.Sha, commitShaToSelect, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    CachedResult<GitHubCommit> selectedResult = await _commitQueryService.GetCommitAsync(
                        token,
                        accountPartition,
                        _navArg.Repo.Owner.Login,
                        _navArg.Repo.Name,
                        commitShaToSelect);
                    if (selectedResult.Value is not null && preservePreferredCommitOutsideQuery)
                    {
                        _pinnedCommitSha = selectedResult.Value.Sha;
                        loadedCommits.Insert(0, selectedResult.Value);
                    }
                }
                catch (Exception ex) when (ex is GitHubApiException or HttpRequestException)
                {
                    StatusText = GetString("RepoCommits.PreferredLoadFailed", "JitHub could not load the requested commit.");
                }
            }

            _loadedCommits.Clear();
            _loadedCommits.AddRange(loadedCommits);
            await ApplyCommitListFilterAsync(commitShaToSelect);
            if (requestId != _listRequestId)
            {
                return;
            }

            CommitListQueryOptions querySnapshot = CloneCommitQuery(_commitQuery);
            string owner = _navArg.Repo.Owner.Login;
            string repositoryName = _navArg.Repo.Name;
            _commitListCompletionTask = CompleteCommitListAsync(
                requestId,
                token,
                accountPartition,
                owner,
                repositoryName,
                querySnapshot,
                queryIdentity,
                isSameQuery,
                loadedCommits,
                commitShaToSelect,
                loadDuration);
        }
        catch (GitHubAuthenticationException)
        {
            TrackEvent(
                "commits.list.loaded",
                new Dictionary<string, string?>
                {
                    ["page"] = "repo",
                    ["result"] = "auth_error",
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(loadDuration.Elapsed)
                });
            _authService.SignOut();
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException)
        {
            StatusText = GetString("RepoCommits.LoadFailed", "JitHub could not load commits.");
            TrackEvent(
                "commits.list.loaded",
                new Dictionary<string, string?>
                {
                    ["page"] = "repo",
                    ["result"] = "error",
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(loadDuration.Elapsed)
                });
        }
        finally
        {
            if (requestId == _listRequestId)
            {
                IsLoadingCommits = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    private async Task CompleteCommitListAsync(
        int requestId,
        string token,
        string accountPartition,
        string owner,
        string repositoryName,
        CommitListQueryOptions query,
        string queryIdentity,
        bool isSameQuery,
        IReadOnlyList<GitHubCommit> firstPageCommits,
        string? preferredSha,
        Stopwatch loadDuration)
    {
        try
        {
            CommitPagedSection<GitHubCommit> result = await _commitQueryService.GetAllCommitsAsync(
                token,
                accountPartition,
                owner,
                repositoryName,
                query);
            if (requestId != _listRequestId)
            {
                return;
            }

            _commitListState = result.State;
            UpdateCommitListScopeNotice();
            List<GitHubCommit> completeCommits = MergeCommitRows(
                result.Items,
                _loadedCommits,
                preserveExistingTail: isSameQuery && result.State.Completeness != PagedDataCompleteness.Complete);
            if (!string.IsNullOrWhiteSpace(_pinnedCommitSha))
            {
                GitHubCommit? pinned = firstPageCommits.FirstOrDefault(commit =>
                    string.Equals(commit.Sha, _pinnedCommitSha, StringComparison.OrdinalIgnoreCase));
                if (pinned is not null && completeCommits.All(commit =>
                        !string.Equals(commit.Sha, pinned.Sha, StringComparison.OrdinalIgnoreCase)))
                {
                    completeCommits.Insert(0, pinned);
                }
            }

            _loadedCommits.Clear();
            _loadedCommits.AddRange(completeCommits);
            _loadedCommitQueryIdentity = queryIdentity;
            await ApplyCommitListFilterAsync(SelectedCommit?.Sha ?? preferredSha);
            TrackEvent(
                "commits.list.loaded",
                new Dictionary<string, string?>
                {
                    ["page"] = "repo",
                    ["result"] = result.State.Completeness == PagedDataCompleteness.Complete ? "success" : "partial",
                    ["cache_state"] = result.State.CacheState.ToString().ToLowerInvariant(),
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(loadDuration.Elapsed)
                });
        }
        catch (GitHubAuthenticationException)
        {
            if (requestId == _listRequestId)
            {
                _authService.SignOut();
            }
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException)
        {
            if (requestId == _listRequestId)
            {
                StatusText = Commits.Count > 0
                    ? GetString("RepoCommits.PartialLoadFailed", "Some commit history could not be refreshed.")
                    : GetString("RepoCommits.LoadFailed", "JitHub could not load commits.");
            }
        }
    }

    private Task ApplyCommitListFilterAsync(string? preferredSha)
    {
        IEnumerable<GitHubCommit> filtered = _loadedCommits;
        string searchText = SearchText.Trim();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            filtered = filtered.Where(commit =>
                commit.SummaryMessage.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                commit.Commit.Message.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                commit.AuthorDisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                commit.ShortSha.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        }

        List<GitHubCommit> visibleCommits = [.. filtered];
        GitHubCommit? selectedCommit = !string.IsNullOrWhiteSpace(preferredSha)
            ? visibleCommits.FirstOrDefault(commit => string.Equals(commit.Sha, preferredSha, StringComparison.OrdinalIgnoreCase))
            : null;
        selectedCommit ??= visibleCommits.FirstOrDefault();
        bool selectionChanged = SelectedCommit?.Sha != selectedCommit?.Sha;

        _suppressSelectionChanged = true;
        try
        {
            ReplaceCollectionByKey(
                Commits,
                visibleCommits,
                static commit => commit.Sha,
                UpdateCommit);
            SelectedCommit = selectedCommit;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        StatusText = visibleCommits.Count == 0
            ? GetString("RepoCommits.NoMatchesStatus", "No commits match this view.")
            : BuildCommitListStatus(visibleCommits.Count);
        OnPropertyChanged(nameof(IsEmpty));

        if (selectedCommit is null)
        {
            ResetCommitDetails();
        }
        else if (selectionChanged || !HasSelectedCommit)
        {
            PopulateCommit(selectedCommit, hasAuthoritativeDiff: false);
            _ = ShowCommitAfterInputCommitAsync(selectedCommit);
        }

        return Task.CompletedTask;
    }

    private async Task ShowCommitAfterInputCommitAsync(GitHubCommit commit)
    {
        int navigationGeneration = Volatile.Read(ref _navigationGeneration);
        // Task.Yield resumes before composition. Give the lightweight header a
        // frame before projecting cached diff rows and ancillary sections.
        await Task.Delay(InputCriticalDetailDeferral);
        if (navigationGeneration == Volatile.Read(ref _navigationGeneration) && IsSelectedCommit(commit))
        {
            BeginCommitDetailTransition(commit);
            ScheduleSelectedCommitPrefetch(commit, CommitPrefetchReason.Dwell, TimeSpan.FromSeconds(5));
            ScheduleNeighborPrefetch(commit);
            TrackEvent("commits.selected", new Dictionary<string, string?> { ["page"] = "repo", ["source"] = "list" });
            await ShowCommitAsync(commit, populateSummary: false);
        }
    }

    private void BeginCommitDetailTransition(GitHubCommit commit)
    {
        _projectedCommitDetailSha = null;
        _projectedDiffSha = null;
        _projectedDiffRowsSha = null;
        Interlocked.Increment(ref _diffProjectionRequestId);
        DiffRowProjection = CommitDiffRowProjection.Empty;
        ReplaceCollectionByKey(
            CommitComments,
            Array.Empty<GitHubCommitComment>(),
            static comment => comment.Id.ToString(CultureInfo.InvariantCulture));
        ReplaceCollectionByKey(CheckRuns, Array.Empty<GitHubCheckRun>(), static checkRun => checkRun.Id.ToString(CultureInfo.InvariantCulture));
        ReplaceCollectionByKey(CommitStatuses, Array.Empty<GitHubCommitStatus>(), static status => status.Id.ToString(CultureInfo.InvariantCulture));
        ReplaceCollectionByKey(
            AssociatedPullRequests,
            Array.Empty<GitHubPullRequest>(),
            static pullRequest => pullRequest.Number.ToString(CultureInfo.InvariantCulture));
        PopulateCommit(commit, hasAuthoritativeDiff: false);
        NotifyCommentPropertiesChanged();
        NotifyInspectorPropertiesChanged();
    }

    private string BuildCommitListStatus(int visibleCount) => _commitListState.Completeness switch
    {
        PagedDataCompleteness.Partial => FormatString(
            "RepoCommits.ShowingPartialStatus",
            "Showing {0} commits; some results could not be loaded.",
            visibleCount),
        PagedDataCompleteness.ApiLimited => FormatString(
            "RepoCommits.ShowingLimitedStatus",
            "Showing the first {0} commits available for this view.",
            visibleCount),
        _ => FormatString("RepoCommits.ShowingStatus", "Showing {0} commits.", visibleCount)
    };

    private static List<GitHubCommit> MergeCommitRows(
        IEnumerable<GitHubCommit> incoming,
        IEnumerable<GitHubCommit> existing,
        bool preserveExistingTail)
    {
        List<GitHubCommit> merged = [];
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        foreach (GitHubCommit commit in preserveExistingTail ? incoming.Concat(existing) : incoming)
        {
            if (!string.IsNullOrWhiteSpace(commit.Sha) && keys.Add(commit.Sha))
            {
                merged.Add(commit);
            }
        }

        return merged;
    }

    private static CommitListQueryOptions CloneCommitQuery(CommitListQueryOptions source) => new()
    {
        GitRef = source.GitRef,
        Path = source.Path,
        Author = source.Author,
        Since = source.Since,
        Until = source.Until
    };

    private static string CreateCommitQueryIdentity(CommitListQueryOptions options) => string.Join(
        "|",
        options.GitRef?.Trim().ToLowerInvariant() ?? string.Empty,
        options.Path?.Trim().ToLowerInvariant() ?? string.Empty,
        options.Author?.Trim().ToLowerInvariant() ?? string.Empty,
        options.Since?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
        options.Until?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);

    private void UpdateCommitListScopeNotice()
    {
        CommitListScopeNotice = _commitListState.Completeness switch
        {
            PagedDataCompleteness.Partial => GetString(
                "RepoCommits.PartialScopeNotice",
                "Some commits could not be loaded. Available history remains visible."),
            PagedDataCompleteness.ApiLimited => GetString(
                "RepoCommits.LimitedScopeNotice",
                "This history reached GitHub's result limit."),
            _ when _branchListState.Completeness == PagedDataCompleteness.Partial => GetString(
                "RepoCommits.BranchesPartialScopeNotice",
                "Some branches could not be loaded. Commit history is still available."),
            _ when _branchListState.Completeness == PagedDataCompleteness.ApiLimited => GetString(
                "RepoCommits.BranchesLimitedScopeNotice",
                "The branch picker reached GitHub's result limit."),
            _ => string.Empty
        };
        OnPropertyChanged(nameof(HasCommitListScopeNotice));
    }

    private async Task ShowCommitAsync(GitHubCommit? commit, bool populateSummary = true)
    {
        int requestId = ++_detailRequestId;
        if (populateSummary)
        {
            PopulateCommit(commit, hasAuthoritativeDiff: false);
        }
        if (commit is null || _navArg?.Repo is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        if (TryApplyNavigationSnapshot(token, GetActiveUserPartition(token), _navArg.Repo.Owner.Login, _navArg.Repo.Name, commit.Sha))
        {
            return;
        }

        IsCommitDetailsLoading = true;
        NotifyActionPropertiesChanged();
        try
        {
            CommitDetailAggregate? aggregate = await _commitQueryService.GetCommitDetailAsync(
                token,
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                commit.Sha);
            if (requestId != _detailRequestId || aggregate is null || !IsSelectedCommit(aggregate.Commit))
            {
                return;
            }

            PopulateAggregate(aggregate);
            StoreNavigationSnapshot(aggregate.Commit, "selection");
            StatusText = FormatString("RepoCommits.SelectedStatus", "Showing commit {0}.", aggregate.Commit.ShortSha);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (Exception ex) when (ex is GitHubApiException or HttpRequestException)
        {
            if (requestId == _detailRequestId && IsSelectedCommit(commit))
            {
                StatusText = GetString("RepoCommits.DetailLoadFailed", "JitHub could not load commit details.");
                IsDiffLoading = false;
                DiffStatusText = GetString("RepoCommits.DiffUnavailable", "Diff unavailable.");
            }
        }
        finally
        {
            if (requestId == _detailRequestId && IsSelectedCommit(commit))
            {
                IsCommitDetailsLoading = false;
                NotifyActionPropertiesChanged();
            }
        }
    }

    private void PopulateCommit(GitHubCommit? commit, bool hasAuthoritativeDiff)
    {
        if (commit is null)
        {
            ResetCommitDetails();
            return;
        }

        StartDiffDocumentBuild(commit, hasAuthoritativeDiff);
        ReplaceCollectionByKey(ParentCommits, commit.Parents, static parent => parent.Sha);
        NotifySelectedCommitPropertiesChanged();
        NotifyInspectorPropertiesChanged();
    }

    private void StartDiffDocumentBuild(GitHubCommit commit, bool hasAuthoritativeDiff)
    {
        CancelDiffBuild();
        int requestId = Interlocked.Increment(ref _diffRequestId);
        GitHubCommitFile[] files = commit.Files ?? [];
        DiffStatusText = string.Empty;
        if (TryGetParsedDiff(commit.Sha, out CommitDiffDocument cachedDocument))
        {
            _projectedDiffSha = commit.Sha;
            _projectedDiffRowsSha = null;
            DiffDocument = cachedDocument;
            IsDiffLoading = false;
            return;
        }

        if (files.Length == 0)
        {
            Interlocked.Increment(ref _diffProjectionRequestId);
            DiffRowProjection = CommitDiffRowProjection.Empty;
            _projectedDiffSha = hasAuthoritativeDiff ? commit.Sha : null;
            _projectedDiffRowsSha = hasAuthoritativeDiff ? commit.Sha : null;
            DiffDocument = CommitDiffDocument.Empty;
            IsDiffLoading = !hasAuthoritativeDiff;
            DiffStatusText = hasAuthoritativeDiff
                ? GetString("RepoCommits.NoDiffAvailable", "No diff is available for this commit.")
                : GetString("RepoCommits.DiffLoading", "Loading diff...");
            return;
        }

        _projectedDiffSha = null;
        _projectedDiffRowsSha = null;
        DiffDocument = CommitDiffDocument.Empty;
        IsDiffLoading = true;
        DiffStatusText = GetString("RepoCommits.DiffPreparing", "Preparing diff...");
        CancellationTokenSource cancellationTokenSource = new();
        _diffBuildCancellationTokenSource = cancellationTokenSource;
        _ = BuildDiffDocumentAsync(commit.Sha, files, requestId, cancellationTokenSource);
    }

    private async Task BuildDiffDocumentAsync(
        string sha,
        GitHubCommitFile[] files,
        int requestId,
        CancellationTokenSource cancellationTokenSource)
    {
        try
        {
            CommitDiffDocument document = await CommitDiffParser.ParseAsync(
                files,
                cancellationTokenSource.Token);
            if (requestId != _diffRequestId ||
                !string.Equals(SelectedCommit?.Sha, sha, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _projectedDiffSha = sha;
            _projectedDiffRowsSha = null;
            DiffDocument = document;
            StoreParsedDiff(sha, document);
            DiffStatusText = document.HasFiles
                ? string.Empty
                : GetString("RepoCommits.NoDiffAvailable", "No diff is available for this commit.");
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (requestId == _diffRequestId &&
                string.Equals(SelectedCommit?.Sha, sha, StringComparison.OrdinalIgnoreCase))
            {
                DiffStatusText = GetString("RepoCommits.DiffFailed", "JitHub could not prepare this diff.");
            }
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _diffBuildCancellationTokenSource,
                null,
                cancellationTokenSource);
            cancellationTokenSource.Dispose();
            if (requestId == _diffRequestId &&
                string.Equals(SelectedCommit?.Sha, sha, StringComparison.OrdinalIgnoreCase))
            {
                IsDiffLoading = false;
            }
        }
    }

    private async Task BuildCompareDiffDocumentAsync(GitHubCommitFile[] files)
    {
        CancelCompareDiffBuild();
        int requestId = Interlocked.Increment(ref _compareDiffRequestId);
        if (files.Length == 0)
        {
            CompareDiffDocument = CommitDiffDocument.Empty;
            return;
        }

        CancellationTokenSource cancellationTokenSource = new();
        _compareDiffBuildCancellationTokenSource = cancellationTokenSource;
        try
        {
            CommitDiffDocument document = await CommitDiffParser.ParseAsync(
                files,
                cancellationTokenSource.Token);
            if (requestId == _compareDiffRequestId)
            {
                CompareDiffDocument = document;
            }
        }
        catch (OperationCanceledException) when (cancellationTokenSource.IsCancellationRequested)
        {
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _compareDiffBuildCancellationTokenSource,
                null,
                cancellationTokenSource);
            cancellationTokenSource.Dispose();
        }
    }

    private void PopulateAggregate(CommitDetailAggregate aggregate)
    {
        GitHubCommit commit = aggregate.Commit;
        bool canPreservePublishedSections = string.Equals(
            _projectedCommitDetailSha,
            commit.Sha,
            StringComparison.OrdinalIgnoreCase);
        ReplaceCommitInCollections(commit);
        if (SelectedCommit?.Sha == commit.Sha)
        {
            _suppressSelectionChanged = true;
            try
            {
                SelectedCommit = commit;
            }
            finally
            {
                _suppressSelectionChanged = false;
            }
        }

        PopulateCommit(commit, hasAuthoritativeDiff: true);
        GitHubCommitComment[] comments = CommitSectionProjectionPolicy.Project(
            aggregate.Comments.OrderBy(comment => comment.CreatedAt),
            canPreservePublishedSections ? CommitComments : [],
            aggregate.CommentsState,
            static comment => comment.Id.ToString(CultureInfo.InvariantCulture));
        GitHubCheckRun[] checkRuns = CommitSectionProjectionPolicy.Project(
            aggregate.CheckRuns,
            canPreservePublishedSections ? CheckRuns : [],
            aggregate.CheckRunsState,
            static checkRun => checkRun.Id.ToString(CultureInfo.InvariantCulture));
        GitHubPullRequest[] associatedPullRequests = CommitSectionProjectionPolicy.Project(
            aggregate.AssociatedPullRequests,
            canPreservePublishedSections ? AssociatedPullRequests : [],
            aggregate.AssociatedPullRequestsState,
            static pullRequest => pullRequest.Number.ToString(CultureInfo.InvariantCulture));

        ReplaceCollectionByKey(
            CommitComments,
            comments,
            static comment => comment.Id.ToString(CultureInfo.InvariantCulture));
        ReplaceCollectionByKey(CheckRuns, checkRuns, static checkRun => checkRun.Id.ToString(CultureInfo.InvariantCulture));
        ReplaceCollectionByKey(CommitStatuses, aggregate.CombinedStatus?.Statuses ?? [], static status => status.Id.ToString(CultureInfo.InvariantCulture));
        ReplaceCollectionByKey(AssociatedPullRequests, associatedPullRequests, static pullRequest => pullRequest.Number.ToString(CultureInfo.InvariantCulture));
        _projectedCommitDetailSha = commit.Sha;
        NotifyCommentPropertiesChanged();
        NotifyInspectorPropertiesChanged();
    }

    private bool TryApplyNavigationSnapshot(
        string token,
        string userPartition,
        string owner,
        string repositoryName,
        string sha)
    {
        if (!_commitNavigationCache.TryGet(
                userPartition,
                owner,
                repositoryName,
                sha,
                out CommitNavigationSnapshot snapshot))
        {
            _ = QueueTrackedPrefetch(
                token,
                userPartition,
                owner,
                repositoryName,
                sha,
                CommitPrefetchReason.NavigationHandoff);
            return false;
        }

        PopulateAggregate(new CommitDetailAggregate(
            snapshot.Commit,
            snapshot.Comments,
            snapshot.CombinedStatus,
            snapshot.CheckRuns,
            snapshot.AssociatedPullRequests,
            new CommitSectionState(CacheState.Fresh),
            new CommitSectionState(CacheState.Fresh),
            new CommitSectionState(CacheState.Fresh),
            new CommitSectionState(CacheState.Fresh),
            new CommitSectionState(CacheState.Fresh)));
        StatusText = FormatString("RepoCommits.CachedStatus", "Showing cached commit {0}.", snapshot.Commit.ShortSha);
        ScheduleSelectedCommitPrefetch(snapshot.Commit, CommitPrefetchReason.Dwell, TimeSpan.FromSeconds(5));
        return true;
    }

    private void StoreNavigationSnapshot(GitHubCommit commit, string source)
    {
        if (_navArg?.Repo is null)
        {
            return;
        }

        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        _commitNavigationCache.Store(GetActiveUserPartition(token), new CommitNavigationSnapshot(
            _navArg.Repo.Owner.Login,
            _navArg.Repo.Name,
            commit.Sha,
            commit,
            [.. CommitComments],
            CommitStatuses.Count == 0 ? null : new GitHubCombinedStatus { Sha = commit.Sha, State = "cached", TotalCount = CommitStatuses.Count, Statuses = [.. CommitStatuses] },
            [.. CheckRuns],
            [.. AssociatedPullRequests],
            DateTimeOffset.UtcNow,
            source));
    }

    private void ScheduleSelectedCommitPrefetch(GitHubCommit commit, CommitPrefetchReason reason, TimeSpan delay)
    {
        if (_navArg?.Repo is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        _selectionDwellPrefetch?.Dispose();
        _selectionDwellPrefetch = ScheduleTrackedPrefetch(
            token,
            GetActiveUserPartition(token),
            _navArg.Repo.Owner.Login,
            _navArg.Repo.Name,
            commit.Sha,
            reason,
            delay);
    }

    private void ScheduleNeighborPrefetch(GitHubCommit commit)
    {
        if (_navArg?.Repo is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubCommit? neighbor = Commits
            .SkipWhile(item => item.Sha != commit.Sha)
            .Skip(1)
            .FirstOrDefault()
            ?? Commits.TakeWhile(item => item.Sha != commit.Sha).LastOrDefault();
        if (neighbor is null)
        {
            return;
        }

        _neighborPrefetch?.Dispose();
        _neighborPrefetch = ScheduleTrackedPrefetch(
            token,
            GetActiveUserPartition(token),
            _navArg.Repo.Owner.Login,
            _navArg.Repo.Name,
            neighbor.Sha,
            CommitPrefetchReason.Neighbor,
            TimeSpan.FromMilliseconds(350));
    }

    private IDisposable ScheduleTrackedPrefetch(
        string token,
        string userPartition,
        string owner,
        string repositoryName,
        string sha,
        CommitPrefetchReason reason,
        TimeSpan delay)
    {
        if (!_prefetchPolicy.Evaluate(
                userPartition,
                AdaptivePrefetchFeature.Commits,
                AdaptivePrefetchStage.Schedule).IsAllowed)
        {
            return EmptyDisposable.Instance;
        }

        CancellationTokenSource cancellation = new();
        _ = _taskCoordinator.RunAsync(
            taskToken => RunScheduledTrackedPrefetchAsync(
                token,
                userPartition,
                owner,
                repositoryName,
                sha,
                reason,
                delay,
                taskToken),
            new ApplicationTaskOptions("commits.page_prefetch", userPartition),
            cancellation.Token);
        return new PrefetchCancellation(cancellation);
    }

    private async Task RunScheduledTrackedPrefetchAsync(
        string token,
        string userPartition,
        string owner,
        string repositoryName,
        string sha,
        CommitPrefetchReason reason,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            await RunTrackedPrefetchAsync(
                token,
                userPartition,
                owner,
                repositoryName,
                sha,
                reason,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A superseded prediction never became an in-flight prefetch.
        }
    }

    private async Task RunTrackedPrefetchAsync(
        string token,
        string userPartition,
        string owner,
        string repositoryName,
        string sha,
        CommitPrefetchReason reason,
        CancellationToken cancellationToken)
    {
        CommitPrefetchOutcome outcome = await CommitPrefetchTelemetry.RunAsync(
            _commitNavigationCache,
            _telemetryService,
            token,
            userPartition,
            owner,
            repositoryName,
            sha,
            reason,
            cancellationToken).ConfigureAwait(false);

        if (outcome != CommitPrefetchOutcome.Success)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (TryGetParsedDiff(sha, out _) ||
            !_commitNavigationCache.TryGet(
                userPartition,
                owner,
                repositoryName,
                sha,
                out CommitNavigationSnapshot snapshot) ||
            snapshot.Commit.Files is not { Length: > 0 } files)
        {
            return;
        }

        if (EstimatePatchInputBytes(files) > ParsedDiffPrefetchInputByteLimit)
        {
            return;
        }

        CommitDiffDocument document = await CommitDiffParser.ParseAsync(files, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        StoreParsedDiff(sha, document);
    }

    private Task QueueTrackedPrefetch(
        string token,
        string userPartition,
        string owner,
        string repositoryName,
        string sha,
        CommitPrefetchReason reason,
        CancellationToken cancellationToken = default) =>
        _taskCoordinator.RunAsync(
            taskToken => RunTrackedPrefetchAsync(
                token,
                userPartition,
                owner,
                repositoryName,
                sha,
                reason,
                taskToken),
            new ApplicationTaskOptions("commits.page_prefetch", userPartition),
            cancellationToken);

    private void ApplyNavigationDefaults(CommitPageNavArg navArg)
    {
        string gitRef = !navArg.NoBranch
            ? navArg.Branch!
            : !navArg.NoRef
                ? navArg.GitRef!
                : navArg.Repo.DefaultBranch;
        _commitQuery.GitRef = gitRef;
        CompareBaseText = navArg.Repo.DefaultBranch;
        CompareHeadText = gitRef;
    }

    private void ApplyQueryFromFilters()
    {
        _commitQuery.GitRef = SelectedBranch?.Name ?? _commitQuery.GitRef ?? _navArg?.Repo.DefaultBranch;
        _commitQuery.Path = NormalizeFilterText(PathFilterText);
        _commitQuery.Author = NormalizeFilterText(AuthorFilterText);
        _commitQuery.Since = CommitDateRangePolicy.StartOfDay(SinceFilterDate);
        _commitQuery.Until = CommitDateRangePolicy.EndOfDay(UntilFilterDate);
    }

    private void ReplaceCommitInCollections(GitHubCommit commit)
    {
        int loadedIndex = _loadedCommits.FindIndex(item => string.Equals(item.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase));
        if (loadedIndex >= 0)
        {
            UpdateCommit(_loadedCommits[loadedIndex], commit);
        }
        else
        {
            _loadedCommits.Insert(0, commit);
        }

        int visibleIndex = IndexOfCommit(Commits, commit.Sha);
        if (visibleIndex >= 0)
        {
            UpdateCommit(Commits[visibleIndex], commit);
        }
    }

    private void ResetCommitDetails()
    {
        _projectedCommitDetailSha = null;
        _projectedDiffSha = null;
        _projectedDiffRowsSha = null;
        Interlocked.Increment(ref _diffProjectionRequestId);
        DiffRowProjection = CommitDiffRowProjection.Empty;
        CancelDiffBuild();
        Interlocked.Increment(ref _diffRequestId);
        IsDiffLoading = false;
        DiffStatusText = string.Empty;
        DiffDocument = CommitDiffDocument.Empty;
        CancelCompareDiffBuild();
        Interlocked.Increment(ref _compareDiffRequestId);
        CompareDiffDocument = CommitDiffDocument.Empty;
        CompareSummaryText = string.Empty;
        ReplaceCollectionByKey(CommitComments, Array.Empty<GitHubCommitComment>(), static comment => comment.Id.ToString(CultureInfo.InvariantCulture));
        ReplaceCollectionByKey(CheckRuns, Array.Empty<GitHubCheckRun>(), static checkRun => checkRun.Id.ToString(CultureInfo.InvariantCulture));
        ReplaceCollectionByKey(CommitStatuses, Array.Empty<GitHubCommitStatus>(), static status => status.Id.ToString(CultureInfo.InvariantCulture));
        ReplaceCollectionByKey(AssociatedPullRequests, Array.Empty<GitHubPullRequest>(), static pullRequest => pullRequest.Number.ToString(CultureInfo.InvariantCulture));
        ReplaceCollectionByKey(ParentCommits, Array.Empty<GitHubCommitParent>(), static parent => parent.Sha);
        NotifySelectedCommitPropertiesChanged();
        NotifyInspectorPropertiesChanged();
        NotifyCommentPropertiesChanged();
        NotifyActionPropertiesChanged();
    }

    private bool TryGetParsedDiff(string sha, out CommitDiffDocument document)
        => _parsedDiffCache.TryGet(sha, out document);

    private bool StoreParsedDiff(string sha, CommitDiffDocument document)
        => _parsedDiffCache.TryStore(sha, document);

    private static long EstimatePatchInputBytes(IEnumerable<GitHubCommitFile> files)
    {
        long bytes = 0;
        foreach (GitHubCommitFile file in files)
        {
            long fileBytes = ((long)(file.Patch?.Length ?? 0) +
                file.Filename.Length +
                (file.PreviousFilename?.Length ?? 0)) * sizeof(char);
            if (bytes > long.MaxValue - fileBytes)
            {
                return long.MaxValue;
            }

            bytes += fileBytes;
        }

        return bytes;
    }

    private void CancelDiffBuild()
    {
        CancellationTokenSource? cancellationTokenSource = Interlocked.Exchange(
            ref _diffBuildCancellationTokenSource,
            null);
        CancelWithoutDisposing(cancellationTokenSource);
    }

    private void CancelCompareDiffBuild()
    {
        CancellationTokenSource? cancellationTokenSource = Interlocked.Exchange(
            ref _compareDiffBuildCancellationTokenSource,
            null);
        CancelWithoutDisposing(cancellationTokenSource);
    }

    private static void CancelWithoutDisposing(CancellationTokenSource? cancellationTokenSource)
    {
        if (cancellationTokenSource is null)
        {
            return;
        }

        try
        {
            cancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void SetSelectedCommit(GitHubCommit? commit)
    {
        _suppressSelectionChanged = true;
        SelectedCommit = commit;
        _suppressSelectionChanged = false;
        NotifySelectedCommitPropertiesChanged();
    }

    private bool IsSelectedCommit(GitHubCommit commit) =>
        string.Equals(SelectedCommit?.Sha, commit.Sha, StringComparison.OrdinalIgnoreCase);

    private static int IndexOfCommit(ObservableCollection<GitHubCommit> commits, string sha)
    {
        for (int index = 0; index < commits.Count; index++)
        {
            if (string.Equals(commits[index].Sha, sha, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool UpdateCommit(GitHubCommit target, GitHubCommit snapshot)
    {
        bool changed =
            target.Commit.Message != snapshot.Commit.Message ||
            target.Commit.CommentCount != snapshot.Commit.CommentCount ||
            target.Stats?.Total != snapshot.Stats?.Total ||
            target.Files.Length != snapshot.Files.Length;
        target.NodeId = snapshot.NodeId;
        target.HtmlUrl = snapshot.HtmlUrl;
        target.Commit = snapshot.Commit;
        target.Author = snapshot.Author;
        target.Committer = snapshot.Committer;
        target.Stats = snapshot.Stats;
        target.Files = snapshot.Files;
        target.Parents = snapshot.Parents;
        return changed;
    }

    private static void ReplaceCollectionByKey<T>(
        ObservableCollection<T> collection,
        IEnumerable<T> snapshot,
        Func<T, string> keySelector,
        Func<T, T, bool>? updateItem = null)
    {
        int targetIndex = 0;
        HashSet<string> targetKeys = new(StringComparer.Ordinal);

        foreach (T snapshotItem in snapshot)
        {
            string key = keySelector(snapshotItem);
            if (string.IsNullOrWhiteSpace(key) || !targetKeys.Add(key))
            {
                continue;
            }

            int currentIndex = IndexOfKey(collection, key, keySelector);
            if (currentIndex < 0)
            {
                collection.Insert(targetIndex, snapshotItem);
                targetIndex++;
                continue;
            }

            T currentItem = collection[currentIndex];
            if (updateItem is null)
            {
                if (!ReferenceEquals(currentItem, snapshotItem))
                {
                    collection[currentIndex] = snapshotItem;
                    currentItem = snapshotItem;
                }
            }
            else
            {
                updateItem(currentItem, snapshotItem);
            }

            if (currentIndex != targetIndex)
            {
                collection.Move(currentIndex, targetIndex);
            }

            targetIndex++;
        }

        for (int index = collection.Count - 1; index >= 0; index--)
        {
            if (!targetKeys.Contains(keySelector(collection[index])))
            {
                collection.RemoveAt(index);
            }
        }
    }

    private static int IndexOfKey<T>(ObservableCollection<T> collection, string key, Func<T, string> keySelector)
    {
        for (int index = 0; index < collection.Count; index++)
        {
            if (string.Equals(keySelector(collection[index]), key, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private void NotifySelectedCommitPropertiesChanged()
    {
        NotifySelectedCommitHeaderPropertiesChanged();
        OnPropertyChanged(nameof(SelectedCommitBodyText));
        OnPropertyChanged(nameof(CommitBodyMarkdownSource));
        OnPropertyChanged(nameof(CommitCommentMarkdownSource));
    }

    private void NotifySelectedCommitHeaderPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasSelectedCommit));
        OnPropertyChanged(nameof(IsDetailPlaceholderVisible));
        OnPropertyChanged(nameof(SelectedCommitTitle));
        OnPropertyChanged(nameof(SelectedCommitShaText));
        OnPropertyChanged(nameof(SelectedCommitAuthor));
        OnPropertyChanged(nameof(SelectedCommitAuthorLogin));
        OnPropertyChanged(nameof(SelectedCommitAvatarUrl));
        OnPropertyChanged(nameof(SelectedCommitAuthorAutomationId));
        OnPropertyChanged(nameof(SelectedCommitMetaText));
        OnPropertyChanged(nameof(SelectedCommitStatsText));
        OnPropertyChanged(nameof(SelectedCommitVerificationText));
    }

    private void NotifyInspectorPropertiesChanged()
    {
        OnPropertyChanged(nameof(CheckSummaryText));
        OnPropertyChanged(nameof(AssociatedPullRequestsText));
    }

    private void NotifyCommentPropertiesChanged()
    {
        OnPropertyChanged(nameof(SelectedCommitCommentsText));
    }

    private void NotifyActionPropertiesChanged()
    {
        OnPropertyChanged(nameof(AreCommitActionsEnabled));
        OnPropertyChanged(nameof(IsCommitCommentEnabled));
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

    private string GetActiveUserPartition(string token)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(token))
        {
            return "public-preview";
        }

        long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        return userId > 0 ? userId.ToString(CultureInfo.InvariantCulture) : "current";
    }

    private void TrackEvent(string name, IReadOnlyDictionary<string, string?> properties)
    {
        try
        {
            _telemetryService.TrackEvent(name, properties);
        }
        catch
        {
            // Telemetry is best-effort and must never affect repository workflows.
        }
    }

    public void TrackCommitAction(CommitActionKind action, CommitActionOutcome outcome) =>
        CommitActionTelemetry.Track(_telemetryService, action, outcome);

    private static string NormalizeFilterText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string FormatTimeAgo(DateTimeOffset? timestamp)
    {
        if (timestamp is null)
        {
            return "unknown time";
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - timestamp.Value.ToUniversalTime();
        if (elapsed.TotalMinutes < 1)
        {
            return "just now";
        }

        if (elapsed.TotalHours < 1)
        {
            return $"{Math.Max(1, (int)elapsed.TotalMinutes).ToString(CultureInfo.CurrentCulture)}m ago";
        }

        if (elapsed.TotalDays < 1)
        {
            return $"{Math.Max(1, (int)elapsed.TotalHours).ToString(CultureInfo.CurrentCulture)}h ago";
        }

        return $"{Math.Max(1, (int)elapsed.TotalDays).ToString(CultureInfo.CurrentCulture)}d ago";
    }

    private sealed partial class PrefetchCancellation : IDisposable
    {
        private CancellationTokenSource? _cancellation;

        public PrefetchCancellation(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public void Dispose()
        {
            CancellationTokenSource? cancellation = Interlocked.Exchange(ref _cancellation, null);
            if (cancellation is null)
            {
                return;
            }

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                cancellation.Dispose();
            }
        }
    }

    private sealed partial class EmptyDisposable : IDisposable
    {
        public static readonly IDisposable Instance = new EmptyDisposable();

        public void Dispose()
        {
        }
    }
}
