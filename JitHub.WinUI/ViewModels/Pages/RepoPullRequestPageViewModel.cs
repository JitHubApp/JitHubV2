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
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Services.Markdown;
using JitHub.WinUI.Helpers;
using MarkdownRenderer.Images;

namespace JitHub.WinUI.ViewModels.Pages;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class RepoPullRequestPageViewModel : ViewModelBase
{
    private static readonly TimeSpan SelectionLoadDebounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan InitialDetailLoadDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HoverPrefetchDebounce = TimeSpan.FromMilliseconds(500);
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;
    private readonly IGitHubPullRequestQueryService _pullRequestQueryService;
    private readonly IPullRequestNavigationCache _pullRequestNavigationCache;
    private readonly ITelemetryService _telemetryService;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly PullRequestCapabilityDenialState _capabilityDenials = new();
    private PullRequestPageNavArg? _navArg;
    private GitHubRepository? _capabilityRepository;
    private GitHubIssue? _selectedPullRequestIssue;
    private int _detailRequestId;
    private int _projectedPullRequestDetailNumber;
    private int _listRequestId;
    private bool _suppressSelectionChanged;
    private CancellationTokenSource? _selectionLoadCancellationTokenSource;
    private CancellationTokenSource? _pullRequestDiffBuildCancellationTokenSource;
    private readonly List<GitHubPullRequest> _loadedPullRequests = [];
    private readonly GitHubPullRequestQueryOptions _pullRequestQuery = new();
    private int _pinnedPullRequestNumber;
    private int _lastFocusedPullRequestNumber;
    private PullRequestDetailSnapshot? _pendingPullRequestSelectionState;
    private bool _isPullRequestCommentSubmissionInProgress;
    private bool _canCommentOnPullRequest;
    private readonly HashSet<long> _inProgressReviewReplyCommentIds = [];
    private readonly Dictionary<string, bool> _commentMinimizationOverrides = new(StringComparer.Ordinal);
    private IDisposable? _selectionDwellPrefetch;
    private IDisposable? _neighborPrefetch;
    private CancellationTokenSource? _navigationRefresh;
    private readonly LatestWinsPrefetchScheduler _hoverPrefetch = new();
    private int _pullRequestDiffProjectionVersion;
    private DateTimeOffset _lastSuccessfulListLoadAt;
    private PullRequestSectionState _pullRequestListState = new(
        CacheState.Miss,
        Completeness: PagedDataCompleteness.Loading);

    public RepoPullRequestPageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _gitHubClientService = GetService<IGitHubClientService>();
        _pullRequestQueryService = GetService<IGitHubPullRequestQueryService>();
        _pullRequestNavigationCache = GetService<IPullRequestNavigationCache>();
        _telemetryService = SafeTelemetryService.Wrap(GetService<ITelemetryService>());
        _taskCoordinator = GetService<IApplicationTaskCoordinator>();

        StateOptions =
        [
            new QueryOption("open", GetString("RepoPullRequest.StateOpen", "Open")),
            new QueryOption("closed", GetString("RepoPullRequest.StateClosed", "Closed")),
            new QueryOption("all", GetString("RepoPullRequest.StateAll", "All"))
        ];
        SortOptions =
        [
            new QueryOption("updated", GetString("RepoPullRequest.SortUpdated", "Updated")),
            new QueryOption("created", GetString("RepoPullRequest.SortCreated", "Created")),
            new QueryOption("popularity", GetString("RepoPullRequest.SortPopularity", "Popularity")),
            new QueryOption("long-running", GetString("RepoPullRequest.SortLongRunning", "Long-running"))
        ];
        DirectionOptions =
        [
            new QueryOption("desc", GetString("RepoPullRequest.DirectionNewestFirst", "Newest first")),
            new QueryOption("asc", GetString("RepoPullRequest.DirectionOldestFirst", "Oldest first"))
        ];

        ResetFilters();
        ResetPullRequestDetails();
        PullRequests.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        StatusText = LoadingStatusText;
    }

    public ObservableCollection<GitHubPullRequest> PullRequests { get; } = [];

    public ObservableCollection<GitHubIssueComment> PullRequestComments { get; } = [];

    public ObservableCollection<GitHubCommit> PullRequestCommits { get; } = [];

    public ObservableCollection<PullRequestReviewItem> PullRequestReviews { get; } = [];

    public ObservableCollection<GitHubIssueEvent> PullRequestTimelineEvents { get; } = [];

    public ObservableCollection<GitHubLabel> SelectedLabels { get; } = [];

    public ObservableCollection<GitHubActor> SelectedAssignees { get; } = [];

    public ObservableCollection<GitHubActor> RequestedReviewers { get; } = [];

    public List<QueryOption> StateOptions { get; }

    public List<QueryOption> SortOptions { get; }

    public List<QueryOption> DirectionOptions { get; }

    public string AuthenticatedLogin => _authService.AuthenticatedUser?.Login ?? string.Empty;

    public PullRequestPageNavArg? NavigationArgs => _navArg;

    public string RepositoryFullName => _navArg?.Repo is null
        ? string.Empty
        : $"{_navArg.Repo.Owner.Login}/{_navArg.Repo.Name}";

    public string PageTitle => string.IsNullOrWhiteSpace(RepositoryFullName)
        ? GetString("RepoPullRequest.PageTitle", "Pull requests")
        : FormatString("RepoPullRequest.PageTitleWithRepo", "{0} pull requests", RepositoryFullName);

    public GitHubPullRequest? CurrentPullRequest => SelectedPullRequest;

    public GitHubIssue? CurrentPullRequestIssue => _selectedPullRequestIssue;

    public bool HasSelectedPullRequest => SelectedPullRequest is not null;

    public bool CanSubmitPullRequestReview =>
        !IsPullRequestReviewSubmissionInProgress &&
        (CanSubmitReviewComment || CanApprovePullRequest || CanRequestPullRequestChanges);

    public bool IsEmpty => PullRequests.Count == 0;

    public bool IsDetailPlaceholderVisible => SelectedPullRequest is null;

    public string SelectedPullRequestNumberText => SelectedPullRequest is null
        ? string.Empty
        : $"#{SelectedPullRequest.Number.ToString(CultureInfo.InvariantCulture)}";

    public string SelectedPullRequestTitle => SelectedPullRequest?.Title ?? PullRequestTitleText;

    public string SelectedPullRequestAuthorDisplayName => string.IsNullOrWhiteSpace(SelectedPullRequest?.User?.Login)
        ? UnknownUserText
        : SelectedPullRequest.User.Login;

    public string? SelectedPullRequestAuthorLogin =>
        GetRoutablePullRequestAuthorLogin(SelectedPullRequest?.User);

    public string SelectedPullRequestAuthorAvatarUrl => SelectedPullRequest?.User?.AvatarUrl ?? string.Empty;

    public string SelectedPullRequestAuthorAutomationId => SelectedPullRequest?.AutomationId ?? "RepoPullRequestSelected_none";

    internal static string? GetRoutablePullRequestAuthorLogin(GitHubActor? author) =>
        UserIdentityNavigationPolicy.GetRoutableLogin(author?.Login);

    public string SelectedPullRequestStateText => SelectedPullRequest is null
        ? string.Empty
        : GetPullRequestStateDisplay(SelectedPullRequest);

    public string SelectedPullRequestMetadataText => SelectedPullRequest is null
        ? string.Empty
        : FormatString(
            "RepoPullRequest.SelectedMetadataFormat",
            "@{0} opened {1:g}  •  updated {2:g}",
            SelectedPullRequestAuthorDisplayName,
            SelectedPullRequest.CreatedAt.LocalDateTime,
            SelectedPullRequest.UpdatedAt.LocalDateTime);

    public string BranchSummaryText => SelectedPullRequest is null
        ? string.Empty
        : $"{SelectedPullRequest.Head.GitRef} -> {SelectedPullRequest.Base.GitRef}";

    public string SelectedPullRequestCommentText => SelectedPullRequest is null
        ? string.Empty
        : FormatCommentCount(SelectedPullRequest.Comments);

    public long SelectedPullRequestReactionTargetId => SelectedPullRequest?.Number ?? 0;

    public string SelectedPullRequestHtmlUrl => SelectedPullRequest?.HtmlUrl ?? string.Empty;

    public GitHubReactionSummary PullRequestReactions => _selectedPullRequestIssue?.Reactions ?? new();

    public MarkdownDocumentSource? PullRequestBodyMarkdownSource => SelectedPullRequest?.MarkdownSource;

    public MarkdownDocumentSource? PullRequestCommentMarkdownSource => _navArg is null || SelectedPullRequest is null
        ? null
        : MarkdownDocumentSourceFactory.CreateRepositoryDocument(
            "pull-request-comment-draft",
            SelectedPullRequest.Id.ToString(CultureInfo.InvariantCulture),
            _navArg.Repo.Owner.Login,
            _navArg.Repo.Name,
            SelectedPullRequest.Base.GitRef);

    public string MilestoneTitle => _selectedPullRequestIssue?.Milestone?.Title ?? NoMilestoneText;

    public bool HasLabels => SelectedLabels.Count > 0;

    public bool HasNoLabels => SelectedLabels.Count == 0;

    public bool HasAssignees => SelectedAssignees.Count > 0;

    public bool HasNoAssignees => SelectedAssignees.Count == 0;

    public bool HasRequestedReviewers => RequestedReviewers.Count > 0;

    public bool HasNoRequestedReviewers => RequestedReviewers.Count == 0;

    public bool IsConversationSectionVisible => SelectedSection == PullRequestWorkspaceSection.Conversation;

    public bool IsFilesSectionVisible => SelectedSection == PullRequestWorkspaceSection.Files;

    public bool IsScrollableContentSectionVisible => SelectedSection != PullRequestWorkspaceSection.Files;

    public bool IsCommitsSectionVisible => SelectedSection == PullRequestWorkspaceSection.Commits;

    public bool IsReviewsSectionVisible => SelectedSection == PullRequestWorkspaceSection.Reviews;

    public bool IsTimelineSectionVisible => SelectedSection == PullRequestWorkspaceSection.Timeline;

    public string NewPullRequestButtonText => GetString("RepoPullRequest.NewButton", "New pull request");

    public string NewPullRequestDialogTitle => GetString("RepoPullRequest.NewDialogTitle", "New pull request");

    public string ReloadButtonText => GetString("Common.ReloadButton", "Reload");

    public string SearchPlaceholderText => GetString("RepoPullRequest.SearchPlaceholder", "Search pull requests");

    public string HeadBranchPlaceholderText => GetString("RepoPullRequest.HeadBranchPlaceholder", "Head branch filter");

    public string BaseBranchPlaceholderText => GetString("RepoPullRequest.BaseBranchPlaceholder", "Base branch filter");

    public string ApplyFiltersButtonText => GetString("RepoPullRequest.ApplyFiltersButton", "Apply filters");

    public string ClearFiltersButtonText => GetString("RepoPullRequest.ClearFiltersButton", "Clear");

    public string OpenOnGitHubButtonText => GetString("RepoPullRequest.OpenOnGitHubButton", "Open on GitHub");

    public string OpenButtonText => GetString("Common.OpenButton", "Open");

    public string EditButtonText => GetString("Common.EditButton", "Edit");

    public string MetadataButtonText => GetString("RepoPullRequest.MetadataButton", "Metadata");

    public string ReactionsButtonText => GetString("Common.ReactionsButton", "Reactions");

    public string MergeButtonText => GetString("RepoPullRequest.MergeButton", "Merge");

    public string MergeCommitOptionText => GetString("RepoPullRequest.MergeCommitOption", "Create a merge commit");

    public string SquashMergeOptionText => GetString("RepoPullRequest.SquashMergeOption", "Squash and merge");

    public string RebaseMergeOptionText => GetString("RepoPullRequest.RebaseMergeOption", "Rebase and merge");

    public string ConversationTabText => GetString("RepoPullRequest.ConversationTab", "Conversation");

    public string CommitsTabText => GetString("RepoPullRequest.CommitsTab", "Commits");

    public string ReviewsTabText => GetString("RepoPullRequest.ReviewsTab", "Reviews");

    public string TimelineTabText => GetString("RepoPullRequest.TimelineTab", "Timeline");

    public string ConversationTitleText => GetString("RepoPullRequest.ConversationTitle", "Conversation");

    public string NoCommentsText => GetString("RepoPullRequest.NoComments", "No comments yet.");

    public string CommentPlaceholderText => GetString("RepoPullRequest.CommentPlaceholder", "Leave a comment");

    public string CommentButtonText => GetString("Common.CommentButton", "Comment");

    public string NoCommitsText => GetString("RepoPullRequest.NoCommits", "No commits are available for this pull request.");

    public string NoReviewsText => GetString("RepoPullRequest.NoReviews", "No review activity is available for this pull request.");

    public string ReplyPlaceholderText => GetString("RepoPullRequest.ReplyPlaceholder", "Reply to this review comment");

    public string ReplyButtonText => GetString("Common.ReplyButton", "Reply");

    public string ReplyPrefixText => GetString("RepoPullRequest.ReplyPrefix", "Reply by @");

    public string NoTimelineText => GetString("RepoPullRequest.NoTimeline", "No timeline events are available for this pull request.");

    public string ChangedFileText => GetString("RepoPullRequest.ChangedFileLabel", "Changed file");

    public string UnknownUserText => GetString("Common.UnknownUser", "unknown");

    public string LoadingStatusText => GetString("RepoPullRequest.LoadingStatus", "Loading pull requests...");

    public string CreateButtonText => GetString("Common.CreateButton", "Create");

    public string SaveButtonText => GetString("Common.SaveButton", "Save");

    public string ContinueButtonText => GetString("Common.ContinueButton", "Continue");

    public string CancelButtonText => GetString("Common.CancelButton", "Cancel");

    public string TitleHeaderText => GetString("Common.TitleHeader", "Title");

    public string DescriptionHeaderText => GetString("Common.DescriptionHeader", "Description");

    public string HeadBranchHeaderText => GetString("RepoPullRequest.HeadBranchHeader", "Head branch");

    public string HeadBranchDialogPlaceholderText => GetString("RepoPullRequest.HeadBranchDialogPlaceholder", "feature-branch or owner:feature-branch");

    public string BaseBranchHeaderText => GetString("RepoPullRequest.BaseBranchHeader", "Base branch");

    public string CommitTitleHeaderText => GetString("RepoPullRequest.CommitTitleHeader", "Commit title (optional)");

    public string CommitMessageHeaderText => GetString("RepoPullRequest.CommitMessageHeader", "Commit message (optional)");

    public string RequestedReviewersSectionTitle => GetString("RepoPullRequest.RequestedReviewersSectionTitle", "Requested reviewers");

    public string AssigneesSectionTitle => GetString("RepoIssue.AssigneesSectionTitle", "Assignees");

    public string LabelsSectionTitle => GetString("RepoIssue.LabelsSectionTitle", "Labels");

    public string MilestoneHeaderText => GetString("RepoIssue.MilestoneHeader", "Milestone");

    public string NoMilestoneText => GetString("RepoIssue.NoMilestone", "No milestone");

    public string NoReviewersAvailableText => GetString("RepoPullRequest.NoReviewersAvailable", "No reviewers are available for this repository.");

    public string NoAssignableUsersText => GetString("RepoIssue.NoAssignableUsers", "No assignable users are available.");

    public string NoLabelsAvailableText => GetString("RepoIssue.NoLabelsAvailable", "No labels are available for this repository.");

    public string ReactionDialogSaveButtonText => GetString("Common.SaveButton", "Save");

    public string SelectedPullRequestReactionDialogTitle => SelectedPullRequest is null
        ? GetString("RepoPullRequest.ReactionsDialogTitle", "Reactions for pull request")
        : FormatString("RepoPullRequest.ReactionsDialogTitleFormat", "Reactions for pull request #{0}", SelectedPullRequest.Number);

    public string CommentReactionDialogTitleText => GetString("RepoPullRequest.CommentReactionsDialogTitle", "Reactions for comment");

    public string EmptyTitleValidationText => GetString("RepoPullRequest.EmptyTitleValidation", "Pull request title cannot be empty.");

    public string EmptyHeadValidationText => GetString("RepoPullRequest.EmptyHeadValidation", "Enter the head branch to compare.");

    public string EmptyBaseValidationText => GetString("RepoPullRequest.EmptyBaseValidation", "Enter the base branch for the pull request.");

    public string PendingReviewText => GetString("RepoPullRequest.PendingReview", "Pending");

    public string ReviewCommentStateText => GetString("RepoPullRequest.ReviewCommentState", "Review comment");

    public string NoReactionSummaryText => GetString("RepoPullRequest.NoReactions", "Reactions: none");

    public string FormatEditPullRequestDialogTitle(int pullRequestNumber)
    {
        return FormatString("RepoPullRequest.EditDialogTitleFormat", "Edit pull request #{0}", pullRequestNumber);
    }

    public string FormatMetadataDialogTitle(int pullRequestNumber)
    {
        return FormatString("RepoPullRequest.MetadataDialogTitleFormat", "Metadata for pull request #{0}", pullRequestNumber);
    }

    public string FormatMergeOperationTitle(string mergeMethod)
    {
        return mergeMethod switch
        {
            "merge" => GetString("RepoPullRequest.MergeOperationTitle", "Merge pull request"),
            "squash" => GetString("RepoPullRequest.SquashOperationTitle", "Squash and merge"),
            "rebase" => GetString("RepoPullRequest.RebaseOperationTitle", "Rebase and merge"),
            _ => GetString("RepoPullRequest.MergeOperationTitle", "Merge pull request")
        };
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PullRequestListScopeNotice { get; set; } = string.Empty;

    public bool HasPullRequestListScopeNotice => !string.IsNullOrWhiteSpace(PullRequestListScopeNotice);

    [ObservableProperty]
    public partial QueryOption? SelectedStateOption { get; set; }

    [ObservableProperty]
    public partial QueryOption? SelectedSortOption { get; set; }

    [ObservableProperty]
    public partial QueryOption? SelectedDirectionOption { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HeadFilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BaseFilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial GitHubPullRequest? SelectedPullRequest { get; set; }

    [ObservableProperty]
    public partial PullRequestWorkspaceSection SelectedSection { get; set; } = PullRequestWorkspaceSection.Conversation;

    [ObservableProperty]
    public partial string PullRequestTitleText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PullRequestMetaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PullRequestMetadataText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PullRequestReactionsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MergeStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PullRequestBodyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PullRequestCommentDraft { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TogglePullRequestStateButtonText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPullRequestListLoading { get; set; }

    [ObservableProperty]
    public partial bool ArePullRequestActionsEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsTogglePullRequestStateEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsMergeEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsPullRequestCommentEnabled { get; set; }

    [ObservableProperty]
    public partial bool CanEditPullRequest { get; set; }

    [ObservableProperty]
    public partial bool CanManagePullRequestMetadata { get; set; }

    [ObservableProperty]
    public partial bool CanReactToPullRequest { get; set; }

    [ObservableProperty]
    public partial bool CanSubmitReviewComment { get; set; }

    [ObservableProperty]
    public partial bool CanApprovePullRequest { get; set; }

    [ObservableProperty]
    public partial bool CanRequestPullRequestChanges { get; set; }

    [ObservableProperty]
    public partial bool IsPullRequestReviewSubmissionInProgress { get; set; }

    [ObservableProperty]
    public partial bool CanMergeWithMergeCommit { get; set; }

    [ObservableProperty]
    public partial bool CanMergeWithSquash { get; set; }

    [ObservableProperty]
    public partial bool CanMergeWithRebase { get; set; }

    [ObservableProperty]
    public partial bool IsPullRequestCommentsEmptyVisible { get; set; }

    [ObservableProperty]
    public partial bool IsPullRequestCommitsEmptyVisible { get; set; }

    [ObservableProperty]
    public partial bool IsPullRequestReviewsEmptyVisible { get; set; }

    [ObservableProperty]
    public partial bool IsPullRequestTimelineEmptyVisible { get; set; }

    [ObservableProperty]
    public partial CommitDiffDocument PullRequestDiffDocument { get; set; } = CommitDiffDocument.Empty;

    [ObservableProperty]
    public partial CommitDiffRowProjection PullRequestDiffRowProjection { get; set; } = CommitDiffRowProjection.Empty;

    [ObservableProperty]
    public partial string PullRequestFileFilterText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PullRequestDiffSearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int SelectedPullRequestDiffSearchMatchIndex { get; set; } = -1;

    public string PullRequestDiffSearchMatchCountText => PullRequestDiffRowProjection.MatchCountText;

    public bool HasPullRequestDiffSearchMatches => PullRequestDiffRowProjection.MatchCount > 0;

    public bool IsPullRequestSelectionCoherent(GitHubPullRequest pullRequest) =>
        SelectedPullRequest?.Number == pullRequest.Number &&
        (_projectedPullRequestDetailNumber == pullRequest.Number ||
            !string.IsNullOrWhiteSpace(pullRequest.Body) &&
            string.Equals(PullRequestBodyText, pullRequest.Body, StringComparison.Ordinal));

    public async Task InitializeAsync(PullRequestPageNavArg? navArg)
    {
        CancelPredictivePrefetches();
        if (CanReuseNavigationState(navArg))
        {
            _navArg = navArg;
            _capabilityRepository = navArg!.Repo;
            NotifyRepositoryPropertiesChanged();
            ScheduleNavigationRefresh();
            return;
        }

        _navArg = navArg;
        _capabilityRepository = navArg?.Repo;
        _lastFocusedPullRequestNumber = 0;
        _pendingPullRequestSelectionState = null;
        _selectionDwellPrefetch?.Dispose();
        _selectionDwellPrefetch = null;
        _neighborPrefetch?.Dispose();
        _neighborPrefetch = null;
        _loadedPullRequests.Clear();
        PullRequests.Clear();
        SetSelectedPullRequest(null);
        ResetPullRequestDetails();
        NotifyRepositoryPropertiesChanged();

        if (navArg is null)
        {
            _listRequestId++;
            _pinnedPullRequestNumber = 0;
            StatusText = GetString(
                "RepoPullRequest.InvalidNavigation",
                "JitHub could not determine which repository pull requests to load.");
            ResetPullRequestDetails();
            return;
        }

        ResetFilters();
        PullRequestTelemetry.TrackOpened(
            _telemetryService,
            "repo",
            TelemetryTaxonomy.Sources.Navigation);
        if (navArg.PullRequestId > 0 &&
            TryGetActiveToken(out string token) &&
            TryApplyNavigationSnapshot(token, GetActiveUserPartition(token), navArg.Repo.Owner.Login, navArg.Repo.Name, navArg.PullRequestId))
        {
            await LoadPullRequestsAsync(
                navArg.PullRequestId,
                preservePreferredPullRequestOutsideQuery: true,
                preserveCurrentDetailDuringLoad: true,
                deferSelectedDetails: true);
            return;
        }

        await LoadPullRequestsAsync(navArg.PullRequestId);
    }

    private bool CanReuseNavigationState(PullRequestPageNavArg? navArg) =>
        navArg is not null &&
        _navArg is not null &&
        PullRequests.Count > 0 &&
        (navArg.PullRequestId <= 0 || SelectedPullRequest?.Number == navArg.PullRequestId) &&
        string.Equals(
            navArg.Repo.Owner.Login,
            _navArg.Repo.Owner.Login,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            navArg.Repo.Name,
            _navArg.Repo.Name,
            StringComparison.OrdinalIgnoreCase);

    private void ScheduleNavigationRefresh()
    {
        _navigationRefresh?.Cancel();
        _navigationRefresh?.Dispose();
        _navigationRefresh = null;
        if (_lastSuccessfulListLoadAt != default &&
            DateTimeOffset.UtcNow - _lastSuccessfulListLoadAt < TimeSpan.FromMinutes(5))
        {
            return;
        }

        if (_navArg is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        string userPartition = GetActiveUserPartition(token);
        CancellationTokenSource refresh = new();
        _navigationRefresh = refresh;
        _ = _taskCoordinator.RunAsync(
            async cancellationToken =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                await LoadPullRequestsAsync(
                    SelectedPullRequest?.Number ?? _pinnedPullRequestNumber,
                    preservePreferredPullRequestOutsideQuery: true,
                    preserveCurrentDetailDuringLoad: true,
                    deferSelectedDetails: true);
            },
            new ApplicationTaskOptions("pull_requests.navigation_refresh", userPartition),
            refresh.Token);
    }

    public async Task ReloadAsync()
    {
        if (PullRequests.Count == 0 && _lastFocusedPullRequestNumber > 0)
        {
            await LoadPullRequestsAsync(_lastFocusedPullRequestNumber, preservePreferredPullRequestOutsideQuery: false);
            return;
        }

        await LoadPullRequestsAsync(SelectedPullRequest?.Number ?? _pinnedPullRequestNumber);
    }

    public async Task ApplyFiltersAsync()
    {
        ApplyPullRequestQueryFromFilters();
        await LoadPullRequestsAsync(
            SelectedPullRequest?.Number ?? _lastFocusedPullRequestNumber,
            preservePreferredPullRequestOutsideQuery: false);
    }

    public async Task ClearFiltersAsync()
    {
        ResetFilters();
        ApplyPullRequestQueryFromFilters();
        await LoadPullRequestsAsync(
            SelectedPullRequest?.Number
            ?? (_lastFocusedPullRequestNumber > 0 ? _lastFocusedPullRequestNumber : _navArg?.PullRequestId ?? 0));
    }

    public async Task<PullRequestCreateDialogData?> LoadCreateDialogDataAsync()
    {
        if (DialogMatrixAutomationScenario.IsEnabled && _navArg is not null)
        {
            string defaultBase = string.IsNullOrWhiteSpace(_navArg.Repo.DefaultBranch)
                ? "main"
                : _navArg.Repo.DefaultBranch;
            return new PullRequestCreateDialogData(
                "feature/automation",
                defaultBase,
                (GitHubBranch[])
                [
                    new GitHubBranch { Name = defaultBase },
                    new GitHubBranch { Name = "feature/automation" }
                ],
                PagedDataCompleteness.Complete);
        }

        if (_navArg is null || !TryGetActiveToken(out string token))
        {
            return null;
        }

        try
        {
            PullRequestPagedSection<GitHubBranch> branches = await _pullRequestQueryService.GetAllRepositoryBranchesAsync(
                token,
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name);
            string defaultBase = _navArg.Repo.DefaultBranch;
            string defaultHead = branches.Items
                .FirstOrDefault(branch => !string.Equals(branch.Name, defaultBase, StringComparison.OrdinalIgnoreCase))
                ?.Name ?? string.Empty;
            UpdateRepositoryMetadataScopeStatus(previousStatusText: string.Empty, branches.State);
            return new PullRequestCreateDialogData(
                defaultHead,
                defaultBase,
                branches.Items,
                branches.Completeness);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
        }
        catch (HttpRequestException)
        {
            StatusText = GetString("RepoPullRequest.CreateNetworkError", "JitHub could not reach GitHub to create this pull request.");
        }

        return null;
    }

    public async Task CreatePullRequestAsync(string title, string head, string baseBranch, string? body)
    {
        if (_navArg is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        try
        {
            StatusText = GetString("RepoPullRequest.CreatingStatus", "Creating pull request...");
            GitHubPullRequest pullRequest = await _gitHubClientService.CreatePullRequestAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                title,
                head,
                baseBranch,
                body);
            StatusText = FormatString("RepoPullRequest.CreatedStatus", "Created pull request #{0}.", pullRequest.Number);
            await _pullRequestQueryService.InvalidatePullRequestAsync(
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                pullRequest.Number);
            await LoadPullRequestsAsync(pullRequest.Number);
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Create, TelemetryTaxonomy.Results.Success);
        }
        catch (GitHubAuthenticationException)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Create, TelemetryTaxonomy.Results.AuthError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Create, TelemetryTaxonomy.Results.Error);
            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
        }
        catch (HttpRequestException)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Create, TelemetryTaxonomy.Results.Error);
            StatusText = GetString("RepoPullRequest.CreateNetworkError", "JitHub could not reach GitHub to create this pull request.");
        }
    }

    public async Task UpdateSelectedPullRequestAsync(string title, string? body)
    {
        if (_navArg is null || SelectedPullRequest is null || !CanEditPullRequest || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubPullRequest currentPullRequest = SelectedPullRequest;

        try
        {
            StatusText = FormatString("RepoPullRequest.UpdatingStatus", "Updating pull request #{0}...", currentPullRequest.Number);
            GitHubPullRequest updatedPullRequest = await _gitHubClientService.UpdatePullRequestAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentPullRequest.Number,
                title,
                body);
            await TryRefreshPullRequestSelectionAfterMutationAsync(
                updatedPullRequest,
                token,
                GetString("RepoPullRequest.UpdateRefreshError", "Pull request updated, but JitHub could not refresh pull request details."));
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Edit, TelemetryTaxonomy.Results.Success);
        }
        catch (GitHubAuthenticationException)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Edit, TelemetryTaxonomy.Results.AuthError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Edit, TelemetryTaxonomy.Results.Error);
            DisableRejectedCapability(ex, "edit", currentPullRequest.Number);
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
        }
        catch (HttpRequestException)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Edit, TelemetryTaxonomy.Results.Error);
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = GetString("RepoPullRequest.UpdateNetworkError", "JitHub could not reach GitHub to update this pull request.");
        }
    }

    public async Task<PullRequestMetadataDialogData?> LoadSelectedPullRequestMetadataDialogDataAsync()
    {
        if (DialogMatrixAutomationScenario.IsEnabled && SelectedPullRequest is not null)
        {
            return new PullRequestMetadataDialogData(
                RequestedReviewers.ToArray(),
                SelectedAssignees.ToArray(),
                SelectedLabels.ToArray(),
                [],
                PagedDataCompleteness.Complete);
        }

        if (_navArg is null
            || SelectedPullRequest is null
            || _selectedPullRequestIssue is null
            || !CanManagePullRequestMetadata
            || !TryGetActiveToken(out string token))
        {
            return null;
        }

        int requestId = _detailRequestId;
        int pullRequestNumber = SelectedPullRequest.Number;
        string previousStatusText = StatusText;
        try
        {
            StatusText = FormatString("RepoPullRequest.LoadMetadataStatus", "Loading pull request #{0} metadata...", pullRequestNumber);
            Task<PullRequestPagedSection<GitHubActor>> reviewersTask = _pullRequestQueryService.GetAllRepositoryCollaboratorsAsync(
                token,
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name);
            Task<PullRequestPagedSection<GitHubActor>> assigneesTask = _pullRequestQueryService.GetAllRepositoryAssigneesAsync(
                token,
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name);
            Task<PullRequestPagedSection<GitHubLabel>> labelsTask = _pullRequestQueryService.GetAllRepositoryLabelsAsync(
                token,
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name);
            Task<PullRequestPagedSection<GitHubMilestone>> milestonesTask = _pullRequestQueryService.GetAllRepositoryMilestonesAsync(
                token,
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name);
            await Task.WhenAll(reviewersTask, assigneesTask, labelsTask, milestonesTask);
            PullRequestPagedSection<GitHubActor> reviewers = await reviewersTask;
            PullRequestPagedSection<GitHubActor> assignees = await assigneesTask;
            PullRequestPagedSection<GitHubLabel> labels = await labelsTask;
            PullRequestPagedSection<GitHubMilestone> milestones = await milestonesTask;

            if (requestId != _detailRequestId || SelectedPullRequest?.Number != pullRequestNumber)
            {
                return null;
            }

            PullRequestSectionState[] metadataStates =
            [
                reviewers.State,
                assignees.State,
                labels.State,
                milestones.State
            ];
            UpdateRepositoryMetadataScopeStatus(previousStatusText, metadataStates);

            return new PullRequestMetadataDialogData(
                reviewers.Items,
                assignees.Items,
                labels.Items,
                milestones.Items,
                CombineCompleteness(metadataStates));
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (requestId == _detailRequestId && SelectedPullRequest?.Number == pullRequestNumber)
            {
                StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
            }
        }
        catch (HttpRequestException)
        {
            if (requestId == _detailRequestId && SelectedPullRequest?.Number == pullRequestNumber)
            {
                StatusText = GetString("RepoPullRequest.MetadataLoadNetworkError", "JitHub could not reach GitHub to load pull request metadata.");
            }
        }

        return null;
    }

    public async Task UpdateSelectedPullRequestMetadataAsync(PullRequestMetadataUpdate update)
    {
        if (_navArg is null
            || SelectedPullRequest is null
            || _selectedPullRequestIssue is null
            || !CanManagePullRequestMetadata
            || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubPullRequest currentPullRequest = SelectedPullRequest;
        string owner = _navArg.Repo.Owner.Login;
        string repoName = _navArg.Repo.Name;

        try
        {
            StatusText = FormatString("RepoPullRequest.UpdateMetadataStatus", "Updating pull request #{0} metadata...", currentPullRequest.Number);
            GitHubIssue updatedIssue = await _gitHubClientService.UpdateIssueMetadataAsync(
                token,
                owner,
                repoName,
                currentPullRequest.Number,
                update.Assignees,
                update.Labels,
                update.MilestoneNumber);
            if (IsSelectedPullRequest(currentPullRequest))
            {
                _selectedPullRequestIssue = updatedIssue;
                PullRequestMetadataText = FormatPullRequestMetadataSummary(updatedIssue, currentPullRequest);
            }

            HashSet<string> selectedReviewers = update.Reviewers.ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> existingReviewers = currentPullRequest.RequestedReviewers
                .Select(reviewer => reviewer.Login)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> reviewersToAdd = selectedReviewers.Except(existingReviewers, StringComparer.OrdinalIgnoreCase).ToList();
            List<string> reviewersToRemove = existingReviewers.Except(selectedReviewers, StringComparer.OrdinalIgnoreCase).ToList();

            try
            {
                if (reviewersToAdd.Count > 0)
                {
                    await _gitHubClientService.AddPullRequestReviewersAsync(
                        token,
                        owner,
                        repoName,
                        currentPullRequest.Number,
                        reviewersToAdd);
                }

                if (reviewersToRemove.Count > 0)
                {
                    await _gitHubClientService.RemovePullRequestReviewersAsync(
                        token,
                        owner,
                        repoName,
                        currentPullRequest.Number,
                        reviewersToRemove);
                }
            }
            catch (GitHubAuthenticationException)
            {
                TrackPullRequestAction(TelemetryTaxonomy.Actions.Metadata, TelemetryTaxonomy.Results.AuthError);
                _authService.SignOut();
                return;
            }
            catch (GitHubApiException ex)
            {
                TrackPullRequestAction(TelemetryTaxonomy.Actions.Metadata, TelemetryTaxonomy.Results.Error);
                DisableRejectedCapability(ex, "metadata", currentPullRequest.Number);
                bool partialRefreshSucceeded = await TryRefreshPullRequestSelectionAfterMutationAsync(
                    currentPullRequest,
                    token,
                    GetString(
                        "RepoPullRequest.MetadataPartialRefreshError",
                        "Pull request metadata updated, but reviewer changes failed and JitHub could not refresh pull request details."));
                if (partialRefreshSucceeded && IsSelectedPullRequest(currentPullRequest))
                {
                    StatusText = FormatString(
                        "RepoPullRequest.MetadataReviewerPartialError",
                        "Pull request metadata updated, but reviewer changes failed: {0}",
                        UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-request-reviewers"));
                }

                return;
            }
            catch (HttpRequestException)
            {
                TrackPullRequestAction(TelemetryTaxonomy.Actions.Metadata, TelemetryTaxonomy.Results.Error);
                bool partialRefreshSucceeded = await TryRefreshPullRequestSelectionAfterMutationAsync(
                    currentPullRequest,
                    token,
                    GetString(
                        "RepoPullRequest.MetadataPartialRefreshError",
                        "Pull request metadata updated, but reviewer changes failed and JitHub could not refresh pull request details."));
                if (partialRefreshSucceeded && IsSelectedPullRequest(currentPullRequest))
                {
                    StatusText = GetString(
                        "RepoPullRequest.MetadataReviewerNetworkPartialError",
                        "Pull request metadata updated, but JitHub could not update reviewers.");
                }

                return;
            }
            bool refreshed = await TryRefreshPullRequestSelectionAfterMutationAsync(
                currentPullRequest,
                token,
                GetString("RepoPullRequest.MetadataRefreshError", "Pull request metadata updated, but JitHub could not refresh pull request details."));
            if (refreshed && IsSelectedPullRequest(currentPullRequest))
            {
                StatusText = FormatString("RepoPullRequest.UpdatedMetadataStatus", "Updated pull request #{0} metadata.", currentPullRequest.Number);
            }
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Metadata, TelemetryTaxonomy.Results.Success);
        }
        catch (GitHubAuthenticationException)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Metadata, TelemetryTaxonomy.Results.AuthError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Metadata, TelemetryTaxonomy.Results.Error);
            DisableRejectedCapability(ex, "metadata", currentPullRequest.Number);
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
        }
        catch (HttpRequestException)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Metadata, TelemetryTaxonomy.Results.Error);
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = GetString("RepoPullRequest.MetadataNetworkError", "JitHub could not reach GitHub to update this pull request metadata.");
        }
    }

    public async Task ToggleSelectedPullRequestStateAsync()
    {
        if (_navArg is null
            || SelectedPullRequest is null
            || !IsTogglePullRequestStateEnabled
            || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubPullRequest currentPullRequest = SelectedPullRequest;

        string nextState = string.Equals(currentPullRequest.State, "closed", StringComparison.OrdinalIgnoreCase)
            ? "open"
            : "closed";

        try
        {
            StatusText = nextState == "closed"
                ? FormatString("RepoPullRequest.ClosingStatus", "Closing pull request #{0}...", currentPullRequest.Number)
                : FormatString("RepoPullRequest.ReopeningStatus", "Reopening pull request #{0}...", currentPullRequest.Number);
            GitHubPullRequest updatedPullRequest = await _gitHubClientService.UpdatePullRequestAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentPullRequest.Number,
                null,
                null,
                nextState);
            await TryRefreshPullRequestSelectionAfterMutationAsync(
                updatedPullRequest,
                token,
                GetString("RepoPullRequest.StateRefreshError", "Pull request state updated, but JitHub could not refresh pull request details."));
            TrackPullRequestAction(
                nextState == "closed" ? TelemetryTaxonomy.Actions.Close : TelemetryTaxonomy.Actions.Reopen,
                TelemetryTaxonomy.Results.Success);
        }
        catch (GitHubAuthenticationException)
        {
            TrackPullRequestAction(
                nextState == "closed" ? TelemetryTaxonomy.Actions.Close : TelemetryTaxonomy.Actions.Reopen,
                TelemetryTaxonomy.Results.AuthError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackPullRequestAction(
                nextState == "closed" ? TelemetryTaxonomy.Actions.Close : TelemetryTaxonomy.Actions.Reopen,
                TelemetryTaxonomy.Results.Error);
            DisableRejectedCapability(ex, "state", currentPullRequest.Number);
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
        }
        catch (HttpRequestException)
        {
            TrackPullRequestAction(
                nextState == "closed" ? TelemetryTaxonomy.Actions.Close : TelemetryTaxonomy.Actions.Reopen,
                TelemetryTaxonomy.Results.Error);
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = GetString("RepoPullRequest.UpdateNetworkError", "JitHub could not reach GitHub to update this pull request.");
        }
    }

    public async Task AddPullRequestCommentAsync()
    {
        if (_navArg is null || SelectedPullRequest is null || !IsPullRequestCommentEnabled || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubPullRequest currentPullRequest = SelectedPullRequest;

        string body = PullRequestCommentDraft;
        if (string.IsNullOrWhiteSpace(body))
        {
            StatusText = GetString("RepoPullRequest.CommentValidation", "Type a comment before posting it.");
            return;
        }

        try
        {
            _isPullRequestCommentSubmissionInProgress = true;
            UpdatePullRequestCommentEnabledState();
            StatusText = FormatString("RepoPullRequest.AddCommentStatus", "Commenting on pull request #{0}...", currentPullRequest.Number);
            await _gitHubClientService.CreateIssueCommentAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentPullRequest.Number,
                body);
            ClearSubmittedPullRequestDraft(currentPullRequest.Number);
            if (IsSelectedPullRequest(currentPullRequest))
            {
                StatusText = FormatString("RepoPullRequest.AddedCommentStatus", "Comment added to pull request #{0}.", currentPullRequest.Number);
            }

            await TryRefreshPullRequestSelectionAfterMutationAsync(
                currentPullRequest,
                token,
                GetString("RepoPullRequest.CommentRefreshError", "Comment added, but JitHub could not refresh pull request details."));
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Comment, TelemetryTaxonomy.Results.Success);
        }
        catch (GitHubAuthenticationException)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Comment, TelemetryTaxonomy.Results.AuthError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Comment, TelemetryTaxonomy.Results.Error);
            DisableRejectedCapability(ex, "comment", currentPullRequest.Number);
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
        }
        catch (HttpRequestException)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Comment, TelemetryTaxonomy.Results.Error);
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = GetString("RepoPullRequest.CommentNetworkError", "JitHub could not reach GitHub to post this comment.");
        }
        finally
        {
            _isPullRequestCommentSubmissionInProgress = false;
            UpdatePullRequestCommentEnabledState();
        }
    }

    public async Task<IReadOnlyList<GitHubReaction>?> GetSelectedPullRequestReactionsAsync()
    {
        if (DialogMatrixAutomationScenario.IsEnabled && SelectedPullRequest is not null)
        {
            return [];
        }

        if (_navArg is null || SelectedPullRequest is null || !CanReactToPullRequest || !TryGetActiveToken(out string token))
        {
            return null;
        }

        int pullRequestNumber = SelectedPullRequest.Number;
        try
        {
            string userPartition = GetActiveUserPartition(token);
            if (string.IsNullOrWhiteSpace(userPartition))
            {
                return null;
            }

            PullRequestPagedSection<GitHubReaction> section =
                await _pullRequestQueryService.GetAllPullRequestReactionsAsync(
                token,
                userPartition,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                pullRequestNumber);
            return section.Items;
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            DisableRejectedCapability(ex, "reaction", pullRequestNumber);
            if (SelectedPullRequest?.Number == pullRequestNumber)
            {
                StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
            }
        }
        catch (HttpRequestException)
        {
            StatusText = GetString("RepoPullRequest.ReactionsNetworkError", "JitHub could not reach GitHub to update reactions.");
        }

        return null;
    }

    public async Task<IReadOnlyList<GitHubReaction>?> GetPullRequestCommentReactionsAsync(long commentId)
    {
        if (_navArg is null || SelectedPullRequest is null || !CanReactToPullRequest || !TryGetActiveToken(out string token))
        {
            return null;
        }

        int pullRequestNumber = SelectedPullRequest.Number;
        try
        {
            string userPartition = GetActiveUserPartition(token);
            if (string.IsNullOrWhiteSpace(userPartition))
            {
                return null;
            }

            PullRequestPagedSection<GitHubReaction> section =
                await _pullRequestQueryService.GetAllPullRequestCommentReactionsAsync(
                token,
                userPartition,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                commentId);
            return section.Items;
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            DisableRejectedCapability(ex, "reaction", pullRequestNumber);
            if (SelectedPullRequest?.Number == pullRequestNumber)
            {
                StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
            }
        }
        catch (HttpRequestException)
        {
            StatusText = GetString("RepoPullRequest.ReactionsNetworkError", "JitHub could not reach GitHub to update reactions.");
        }

        return null;
    }

    public async Task<IReadOnlyList<GitHubReaction>?> GetReviewCommentReactionsAsync(long commentId)
    {
        if (_navArg is null || SelectedPullRequest is null || !CanReactToPullRequest || !TryGetActiveToken(out string token))
        {
            return null;
        }

        int pullRequestNumber = SelectedPullRequest.Number;
        try
        {
            string userPartition = GetActiveUserPartition(token);
            if (string.IsNullOrWhiteSpace(userPartition))
            {
                return null;
            }

            PullRequestPagedSection<GitHubReaction> section =
                await _pullRequestQueryService.GetAllPullRequestReviewCommentReactionsAsync(
                token,
                userPartition,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                commentId);
            return section.Items;
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            DisableRejectedCapability(ex, "reaction", pullRequestNumber);
            if (SelectedPullRequest?.Number == pullRequestNumber)
            {
                StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
            }
        }
        catch (HttpRequestException)
        {
            StatusText = GetString("RepoPullRequest.ReactionsNetworkError", "JitHub could not reach GitHub to update reactions.");
        }

        return null;
    }

    public async Task ApplySelectedPullRequestReactionSelectionAsync(
        HashSet<string> selectedContents,
        Dictionary<string, long> existingReactionIds)
    {
        if (SelectedPullRequest is null)
        {
            return;
        }

        await ApplyPullRequestReactionSelectionAsync(
            SelectedPullRequest,
            ReactionDialogTargetKind.Issue,
            SelectedPullRequest?.Number ?? 0,
            selectedContents,
            existingReactionIds);
    }

    public async Task ApplyPullRequestCommentReactionSelectionAsync(
        long commentId,
        HashSet<string> selectedContents,
        Dictionary<string, long> existingReactionIds)
    {
        if (SelectedPullRequest is null)
        {
            return;
        }

        await ApplyPullRequestReactionSelectionAsync(
            SelectedPullRequest,
            ReactionDialogTargetKind.Comment,
            commentId,
            selectedContents,
            existingReactionIds);
    }

    public async Task ApplyReviewCommentReactionSelectionAsync(
        long commentId,
        HashSet<string> selectedContents,
        Dictionary<string, long> existingReactionIds)
    {
        if (SelectedPullRequest is null)
        {
            return;
        }

        await ApplyPullRequestReactionSelectionAsync(
            SelectedPullRequest,
            ReactionDialogTargetKind.ReviewComment,
            commentId,
            selectedContents,
            existingReactionIds);
    }

    public Task<bool> UpdatePullRequestCommentAsync(long commentId, string body, bool isReviewComment) =>
        ExecutePullRequestCommentMutationAsync(
            (token, owner, repoName) => isReviewComment
                ? _gitHubClientService.UpdatePullRequestReviewCommentAsync(token, owner, repoName, commentId, body)
                : _gitHubClientService.UpdateIssueCommentAsync(token, owner, repoName, commentId, body),
            TelemetryTaxonomy.Actions.CommentEdit,
            GetString("RepoPullRequest.CommentUpdatingStatus", "Updating comment..."));

    public Task<bool> DeletePullRequestCommentAsync(long commentId, bool isReviewComment) =>
        ExecutePullRequestCommentMutationAsync(
            (token, owner, repoName) => isReviewComment
                ? _gitHubClientService.DeletePullRequestReviewCommentAsync(token, owner, repoName, commentId)
                : _gitHubClientService.DeleteIssueCommentAsync(token, owner, repoName, commentId),
            TelemetryTaxonomy.Actions.CommentDelete,
            GetString("RepoPullRequest.CommentDeletingStatus", "Deleting comment..."));

    public async Task<bool> SetPullRequestCommentMinimizedAsync(string nodeId, string? classifier)
    {
        if (!CanManagePullRequestMetadata || string.IsNullOrWhiteSpace(nodeId))
        {
            return false;
        }

        string normalizedNodeId = nodeId.Trim();
        bool hadPreviousOverride = _commentMinimizationOverrides.TryGetValue(normalizedNodeId, out bool previousOverride);
        _commentMinimizationOverrides[normalizedNodeId] = !string.IsNullOrWhiteSpace(classifier);
        bool succeeded = await ExecutePullRequestCommentMutationAsync(
            (token, _, _) => string.IsNullOrWhiteSpace(classifier)
                ? _gitHubClientService.UnminimizeCommentAsync(token, normalizedNodeId)
                : _gitHubClientService.MinimizeCommentAsync(token, normalizedNodeId, classifier),
            string.IsNullOrWhiteSpace(classifier)
                ? TelemetryTaxonomy.Actions.CommentUnhide
                : TelemetryTaxonomy.Actions.CommentHide,
            string.IsNullOrWhiteSpace(classifier)
                ? GetString("RepoPullRequest.CommentUnhidingStatus", "Unhiding comment...")
                : GetString("RepoPullRequest.CommentHidingStatus", "Hiding comment..."));
        if (!succeeded)
        {
            if (hadPreviousOverride)
            {
                _commentMinimizationOverrides[normalizedNodeId] = previousOverride;
            }
            else
            {
                _commentMinimizationOverrides.Remove(normalizedNodeId);
            }
        }

        return succeeded;
    }

    private async Task<bool> ExecutePullRequestCommentMutationAsync(
        Func<string, string, string, Task> mutation,
        string action,
        string status)
    {
        if (_navArg is null || SelectedPullRequest is null || !TryGetActiveToken(out string token))
        {
            return false;
        }

        GitHubPullRequest currentPullRequest = SelectedPullRequest;
        try
        {
            StatusText = status;
            await mutation(token, _navArg.Repo.Owner.Login, _navArg.Repo.Name);
            await TryRefreshPullRequestSelectionAfterMutationAsync(
                currentPullRequest,
                token,
                GetString("RepoPullRequest.CommentRefreshError", "Comment updated, but JitHub could not refresh pull request details."));
            TrackPullRequestAction(action, TelemetryTaxonomy.Results.Success);
            return true;
        }
        catch (GitHubAuthenticationException)
        {
            TrackPullRequestAction(action, TelemetryTaxonomy.Results.AuthError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackPullRequestAction(
                action,
                IssuePermissionPolicy.IsPermissionDenied(ex)
                    ? TelemetryTaxonomy.Results.PermissionDenied
                    : TelemetryTaxonomy.Results.Error);
            DisableRejectedCapability(ex, "comment", currentPullRequest.Number);
            if (IsSelectedPullRequest(currentPullRequest))
            {
                StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
            }
        }
        catch (HttpRequestException)
        {
            TrackPullRequestAction(action, TelemetryTaxonomy.Results.NetworkError);
            if (IsSelectedPullRequest(currentPullRequest))
            {
                StatusText = GetString("RepoPullRequest.CommentMutationNetworkError", "JitHub could not reach GitHub to update this comment.");
            }
        }
        catch (OperationCanceledException)
        {
            TrackPullRequestAction(action, TelemetryTaxonomy.Results.Cancelled);
        }
        catch (Exception ex)
        {
            JitHub.WinUI.App.LogHandledException(ex, "pull-request-comment-mutation");
            TrackPullRequestAction(action, TelemetryTaxonomy.Results.Error);
            if (IsSelectedPullRequest(currentPullRequest))
            {
                StatusText = GetString("RepoPullRequest.CommentMutationUnexpectedError", "JitHub could not update this comment.");
            }
        }

        return false;
    }

    public async Task ReplyToReviewCommentAsync(PullRequestReviewThreadItem threadItem)
    {
        if (_navArg is null || SelectedPullRequest is null || !_canCommentOnPullRequest || !TryGetActiveToken(out string token))
        {
            return;
        }

        if (threadItem.IsReplyInProgress)
        {
            return;
        }

        GitHubPullRequest currentPullRequest = SelectedPullRequest;

        string replyText = threadItem.ReplyText.Trim();
        if (string.IsNullOrWhiteSpace(replyText))
        {
            StatusText = GetString("RepoPullRequest.ReplyValidation", "Type a reply before posting it.");
            return;
        }

        try
        {
            _inProgressReviewReplyCommentIds.Add(threadItem.CommentId);
            threadItem.IsReplyInProgress = true;
            StatusText = GetString("RepoPullRequest.ReplyStatus", "Replying to review comment...");
            await _gitHubClientService.ReplyToPullRequestReviewCommentAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentPullRequest.Number,
                threadItem.CommentId,
                replyText);
            if (IsSelectedPullRequest(currentPullRequest))
            {
                threadItem.ReplyText = string.Empty;
            }

            await TryRefreshPullRequestSelectionAfterMutationAsync(
                currentPullRequest,
                token,
                GetString("RepoPullRequest.ReplyRefreshError", "Reply posted, but JitHub could not refresh pull request details."));
            TrackPullRequestAction(TelemetryTaxonomy.Actions.ReviewReply, TelemetryTaxonomy.Results.Success);
        }
        catch (GitHubAuthenticationException)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.ReviewReply, TelemetryTaxonomy.Results.AuthError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.ReviewReply, TelemetryTaxonomy.Results.Error);
            DisableRejectedCapability(ex, "comment", currentPullRequest.Number);
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
        }
        catch (HttpRequestException)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.ReviewReply, TelemetryTaxonomy.Results.Error);
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = GetString("RepoPullRequest.ReplyNetworkError", "JitHub could not reach GitHub to reply to this review comment.");
        }
        finally
        {
            _inProgressReviewReplyCommentIds.Remove(threadItem.CommentId);
            threadItem.IsReplyInProgress = false;
            if (IsSelectedPullRequest(currentPullRequest))
            {
                PullRequestReviewThreadItem? activeThread = PullRequestReviews
                    .SelectMany(review => review.Threads)
                    .FirstOrDefault(thread => thread.CommentId == threadItem.CommentId);
                if (activeThread is not null && !ReferenceEquals(activeThread, threadItem))
                {
                    activeThread.IsReplyInProgress = false;
                }
            }
        }
    }

    private void ClearSubmittedPullRequestDraft(int pullRequestNumber)
    {
        if (_pendingPullRequestSelectionState?.PullRequestNumber == pullRequestNumber)
        {
            _pendingPullRequestSelectionState = _pendingPullRequestSelectionState with { CommentDraft = string.Empty };
        }

        if (SelectedPullRequest?.Number == pullRequestNumber)
        {
            PullRequestCommentDraft = string.Empty;
        }
    }

    public async Task SubmitPullRequestReviewAsync(
        PullRequestReviewDecision decision,
        string? body)
    {
        if (_navArg is null ||
            SelectedPullRequest is null ||
            !CanSubmitReview(decision) ||
            IsPullRequestReviewSubmissionInProgress ||
            !TryGetActiveToken(out string token))
        {
            return;
        }

        PullRequestReviewSubmission submission = new(decision, body?.Trim());
        try
        {
            PullRequestReviewSubmissionPolicy.Validate(submission);
        }
        catch (ArgumentException ex)
        {
            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
            return;
        }

        GitHubPullRequest currentPullRequest = SelectedPullRequest;
        string action = decision switch
        {
            PullRequestReviewDecision.Approve => TelemetryTaxonomy.Actions.ReviewApprove,
            PullRequestReviewDecision.RequestChanges => TelemetryTaxonomy.Actions.ReviewRequestChanges,
            _ => TelemetryTaxonomy.Actions.ReviewComment
        };

        try
        {
            IsPullRequestReviewSubmissionInProgress = true;
            StatusText = FormatString(
                "RepoPullRequest.SubmitReviewStatus",
                "Submitting review for pull request #{0}...",
                currentPullRequest.Number);
            await _gitHubClientService.CreatePullRequestReviewAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentPullRequest.Number,
                submission);

            bool refreshed = await TryRefreshPullRequestSelectionAfterMutationAsync(
                currentPullRequest,
                token,
                GetString(
                    "RepoPullRequest.ReviewRefreshError",
                    "Review submitted, but JitHub could not refresh pull request details."));
            if (refreshed && IsSelectedPullRequest(currentPullRequest))
            {
                SetSection(PullRequestWorkspaceSection.Reviews);
                StatusText = FormatString(
                    "RepoPullRequest.ReviewSubmittedStatus",
                    "Review submitted for pull request #{0}.",
                    currentPullRequest.Number);
            }

            TrackPullRequestAction(action, TelemetryTaxonomy.Results.Success);
        }
        catch (GitHubAuthenticationException)
        {
            TrackPullRequestAction(action, TelemetryTaxonomy.Results.AuthError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackPullRequestAction(action, TelemetryTaxonomy.Results.Error);
            DisableRejectedCapability(ex, "review", currentPullRequest.Number);
            if (IsSelectedPullRequest(currentPullRequest))
            {
                StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
            }
        }
        catch (HttpRequestException)
        {
            TrackPullRequestAction(action, TelemetryTaxonomy.Results.Error);
            if (IsSelectedPullRequest(currentPullRequest))
            {
                StatusText = GetString(
                    "RepoPullRequest.ReviewNetworkError",
                    "JitHub could not reach GitHub to submit this review.");
            }
        }
        finally
        {
            IsPullRequestReviewSubmissionInProgress = false;
        }
    }

    public async Task MergeSelectedPullRequestAsync(
        string mergeMethod,
        string operationTitle,
        string? commitTitle,
        string? commitMessage)
    {
        if (_navArg is null || SelectedPullRequest is null || !IsMergeEnabled ||
            !IsMergeMethodAllowed(mergeMethod) || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubPullRequest currentPullRequest = SelectedPullRequest;

        try
        {
            StatusText = FormatString("RepoPullRequest.MergeStatus", "{0}...", operationTitle);
            GitHubPullRequestMergeResult mergeResult = await _gitHubClientService.MergePullRequestAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentPullRequest.Number,
                mergeMethod,
                commitTitle,
                commitMessage);
            if (!mergeResult.Merged)
            {
                TrackPullRequestAction(TelemetryTaxonomy.Actions.Merge, TelemetryTaxonomy.Results.Rejected);
                if (IsSelectedPullRequest(currentPullRequest))
                {
                    StatusText = string.IsNullOrWhiteSpace(mergeResult.Message)
                        ? GetString("RepoPullRequest.MergeDidNotCompleteStatus", "GitHub did not merge this pull request.")
                        : UserFacingError.ForInternalMessage(
                            mergeResult.Message,
                            UserFacingErrorKind.Action,
                            "pull-request-merge");
                }

                return;
            }

            if (IsSelectedPullRequest(currentPullRequest))
            {
                StatusText = FormatString(
                    "RepoPullRequest.MergedStatus",
                    "Merged pull request #{0}.",
                    currentPullRequest.Number);
            }

            await TryRefreshPullRequestSelectionAfterMutationAsync(
                currentPullRequest,
                token,
                GetString("RepoPullRequest.MergeRefreshError", "Pull request merged, but JitHub could not refresh pull request details."));
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Merge, TelemetryTaxonomy.Results.Success);
        }
        catch (GitHubAuthenticationException)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Merge, TelemetryTaxonomy.Results.AuthError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Merge, TelemetryTaxonomy.Results.Error);
            DisableRejectedCapability(ex, "merge", currentPullRequest.Number);
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
        }
        catch (HttpRequestException)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Merge, TelemetryTaxonomy.Results.Error);
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = GetString("RepoPullRequest.MergeNetworkError", "JitHub could not reach GitHub to merge this pull request.");
        }
    }

    public CommitPageNavArg? CreateCommitNavigationArg(GitHubCommit? commit)
    {
        return _navArg is null || commit is null
            ? null
            : CommitPageNavArg.CreateWithGitRef(_navArg.Repo, commit.Sha);
    }

    partial void OnSelectedPullRequestChanged(GitHubPullRequest? value)
    {
        if (_suppressSelectionChanged)
        {
            return;
        }

        int clearedPinnedPullRequestNumber = 0;
        if (_pinnedPullRequestNumber > 0 && value?.Number != _pinnedPullRequestNumber)
        {
            clearedPinnedPullRequestNumber = _pinnedPullRequestNumber;
            _pinnedPullRequestNumber = 0;
        }

        _detailRequestId++;
        CancelPendingSelectionLoad();
        CancelPullRequestDiffBuild();
        if (value is null)
        {
            _selectionDwellPrefetch?.Dispose();
            _selectionDwellPrefetch = null;
            _neighborPrefetch?.Dispose();
            _neighborPrefetch = null;
            _pendingPullRequestSelectionState = null;
            BackgroundTaskObserver.Run(
                () => ShowPullRequestAsync(null),
                "pull_requests",
                _telemetryService);
            NotifySelectedPullRequestPropertiesChanged();
            return;
        }

        _capabilityDenials.TrackPullRequest(value.Number);
        int previousPullRequestNumber = _lastFocusedPullRequestNumber;
        RemoveClearedPinnedPullRequestFromVisibleList(clearedPinnedPullRequestNumber);
        if (TryRestorePendingPullRequestSelectionState(value.Number))
        {
            _lastFocusedPullRequestNumber = value.Number;
            return;
        }

        if (_pendingPullRequestSelectionState is null && previousPullRequestNumber > 0 && ArePullRequestActionsEnabled)
        {
            CapturePullRequestDetailSnapshot(previousPullRequestNumber);
        }

        _lastFocusedPullRequestNumber = value.Number;
        PreparePullRequestForSelectionLoad(value);
        TrackEvent(
            "pull_requests.selected",
            new Dictionary<string, string?>
            {
                ["page"] = "repo",
                ["source"] = TelemetryTaxonomy.Sources.List
            });
        ScheduleSelectedPullRequestPrefetch(value, PullRequestPrefetchReason.Dwell, TimeSpan.FromSeconds(5));
        ScheduleNeighborPrefetch(value);
        NotifySelectedPullRequestHeaderPropertiesChanged();
        CancellationTokenSource cancellationTokenSource = new();
        _selectionLoadCancellationTokenSource = cancellationTokenSource;
        BackgroundTaskObserver.Run(
            () => ShowPullRequestAfterSelectionDelayAsync(value, cancellationTokenSource.Token),
            "pull_requests",
            _telemetryService);
    }

    partial void OnIsPullRequestReviewSubmissionInProgressChanged(bool value) =>
        OnPropertyChanged(nameof(CanSubmitPullRequestReview));

    partial void OnSelectedSectionChanged(PullRequestWorkspaceSection value)
    {
        OnPropertyChanged(nameof(IsConversationSectionVisible));
        OnPropertyChanged(nameof(IsFilesSectionVisible));
        OnPropertyChanged(nameof(IsScrollableContentSectionVisible));
        OnPropertyChanged(nameof(IsCommitsSectionVisible));
        OnPropertyChanged(nameof(IsReviewsSectionVisible));
        OnPropertyChanged(nameof(IsTimelineSectionVisible));
        TrackEvent(
            "pull_requests.section.opened",
            new Dictionary<string, string?>
            {
                ["page"] = "repo",
                ["section"] = TelemetryTaxonomy.EnumValue(value)
            });
    }

    partial void OnPullRequestDiffDocumentChanged(CommitDiffDocument value) => QueuePullRequestDiffProjectionUpdate(false);

    partial void OnPullRequestFileFilterTextChanged(string value) => QueuePullRequestDiffProjectionUpdate(true);

    partial void OnPullRequestDiffSearchTextChanged(string value) => QueuePullRequestDiffProjectionUpdate(true);

    partial void OnPullRequestDiffRowProjectionChanged(CommitDiffRowProjection value)
    {
        OnPropertyChanged(nameof(PullRequestDiffSearchMatchCountText));
        OnPropertyChanged(nameof(HasPullRequestDiffSearchMatches));
        SelectedPullRequestDiffSearchMatchIndex = value.MatchCount == 0 ? -1 : 0;
    }

    partial void OnPullRequestListScopeNoticeChanged(string value) =>
        OnPropertyChanged(nameof(HasPullRequestListScopeNotice));

    public void MovePullRequestDiffSearchMatch(int direction)
    {
        int count = PullRequestDiffRowProjection.MatchCount;
        if (count == 0)
        {
            SelectedPullRequestDiffSearchMatchIndex = -1;
            return;
        }

        int current = SelectedPullRequestDiffSearchMatchIndex < 0 ? 0 : SelectedPullRequestDiffSearchMatchIndex;
        SelectedPullRequestDiffSearchMatchIndex = (current + direction + count) % count;
    }

    private async Task LoadPullRequestsAsync(
        int preferredPullRequestNumber = 0,
        bool preservePreferredPullRequestOutsideQuery = true,
        bool preserveCurrentDetailDuringLoad = false,
        bool deferSelectedDetails = false)
    {
        if (_navArg is null)
        {
            return;
        }

        Stopwatch listDuration = Stopwatch.StartNew();
        if (!TryGetActiveToken(out string token))
        {
            TrackPullRequestListOutcome(
                TelemetryTaxonomy.Results.AuthError,
                listDuration.Elapsed,
                errorKind: "authentication");
            return;
        }

        PullRequestPageNavArg navigationArgs = _navArg;

        int requestId = ++_listRequestId;
        IsPullRequestListLoading = true;
        bool previousArePullRequestActionsEnabled = ArePullRequestActionsEnabled;
        bool previousIsTogglePullRequestStateEnabled = IsTogglePullRequestStateEnabled;
        bool previousIsPullRequestCommentEnabled = IsPullRequestCommentEnabled;
        bool previousIsMergeEnabled = IsMergeEnabled;
        string? preferredPullRequestLoadFailureStatus = null;
        StatusText = LoadingStatusText;
        if (!preserveCurrentDetailDuringLoad)
        {
            CancelPendingSelectionLoad();
            _pendingPullRequestSelectionState = null;
            _detailRequestId++;
            ArePullRequestActionsEnabled = false;
            IsTogglePullRequestStateEnabled = false;
            IsPullRequestCommentEnabled = false;
            IsMergeEnabled = false;
        }

        try
        {
            int pullRequestNumberToSelect = preferredPullRequestNumber > 0 ? preferredPullRequestNumber : navigationArgs.PullRequestId;
            PullRequestPagedSection<GitHubPullRequest> pullRequestResult = await _pullRequestQueryService.GetAllPullRequestsAsync(
                token,
                GetActiveUserPartition(token),
                navigationArgs.Repo.Owner.Login,
                navigationArgs.Repo.Name,
                _pullRequestQuery,
                progress: progress =>
                {
                    if (requestId != _listRequestId)
                    {
                        return;
                    }

                    ApplyPullRequestListProjection(progress.Items, progress.Completeness);
                    _pullRequestListState = progress.State;
                    UpdatePullRequestListScopeNotice();
                    BackgroundTaskObserver.Run(
                        () => ApplyPullRequestListFilterAsync(
                            pullRequestNumberToSelect,
                            refreshSelectionDetails: false,
                            suppressDetailRefresh: true),
                        "pull_requests",
                        _telemetryService);
                });
            IReadOnlyList<GitHubPullRequest> pullRequests = pullRequestResult.Items;

            if (CompleteSupersededPullRequestListRead(requestId, listDuration.Elapsed))
            {
                return;
            }

            List<GitHubPullRequest> loadedPullRequests = [.. pullRequests];
            int pinnedPullRequestNumber = 0;
            if (pullRequestNumberToSelect > 0)
            {
                GitHubPullRequest? selectedPullRequest = loadedPullRequests.FirstOrDefault(pullRequest => pullRequest.Number == pullRequestNumberToSelect);
                if (selectedPullRequest is null)
                {
                    try
                    {
                        CachedResult<GitHubPullRequest> selectedResult = await _pullRequestQueryService.GetPullRequestAsync(
                            token,
                            GetActiveUserPartition(token),
                            navigationArgs.Repo.Owner.Login,
                            navigationArgs.Repo.Name,
                            pullRequestNumberToSelect);
                        selectedPullRequest = selectedResult.Value;
                    }
                    catch (GitHubAuthenticationException)
                    {
                        throw;
                    }
                    catch (GitHubApiException)
                    {
                        selectedPullRequest = null;
                        pullRequestNumberToSelect = 0;
                        preferredPullRequestLoadFailureStatus = GetString("RepoPullRequest.PreferredLoadApiError", "JitHub could not load the requested pull request.");
                    }
                    catch (HttpRequestException)
                    {
                        selectedPullRequest = null;
                        pullRequestNumberToSelect = 0;
                        preferredPullRequestLoadFailureStatus = GetString("RepoPullRequest.PreferredLoadNetworkError", "JitHub could not reach GitHub to load the requested pull request.");
                    }

                    if (CompleteSupersededPullRequestListRead(requestId, listDuration.Elapsed))
                    {
                        return;
                    }
                }

                if (selectedPullRequest is not null)
                {
                    bool matchesQuery = MatchesPullRequestQuery(selectedPullRequest);
                    if (!matchesQuery && preservePreferredPullRequestOutsideQuery)
                    {
                        pinnedPullRequestNumber = selectedPullRequest.Number;
                    }

                    if ((matchesQuery || pinnedPullRequestNumber == selectedPullRequest.Number)
                        && loadedPullRequests.All(existingPullRequest => existingPullRequest.Number != selectedPullRequest.Number))
                    {
                        loadedPullRequests.Insert(0, selectedPullRequest);
                    }
                }
            }

            if (CompleteSupersededPullRequestListRead(requestId, listDuration.Elapsed))
            {
                return;
            }

            _pinnedPullRequestNumber = pinnedPullRequestNumber;
            ApplyPullRequestListProjection(loadedPullRequests, pullRequestResult.Completeness);
            _pullRequestListState = pullRequestResult.State;
            _lastSuccessfulListLoadAt = DateTimeOffset.UtcNow;
            UpdatePullRequestListScopeNotice();
            await ApplyPullRequestListFilterAsync(
                pullRequestNumberToSelect,
                refreshSelectionDetails: true,
                deferDetailLoad: deferSelectedDetails);
            if (CompleteSupersededPullRequestListRead(requestId, listDuration.Elapsed))
            {
                return;
            }

            PullRequestTelemetry.TrackListLoaded(
                _telemetryService,
                "repo",
                PullRequestSectionProjectionPolicy.CreateListTelemetryResult(pullRequestResult.Completeness),
                listDuration.Elapsed,
                pullRequestResult.State.CacheState,
                loadedPullRequests.Count);
            if (!string.IsNullOrWhiteSpace(preferredPullRequestLoadFailureStatus) && requestId == _listRequestId)
            {
                StatusText = preferredPullRequestLoadFailureStatus;
            }
        }
        catch (GitHubAuthenticationException)
        {
            if (CompleteSupersededPullRequestListRead(requestId, listDuration.Elapsed))
            {
                return;
            }

            ArePullRequestActionsEnabled = previousArePullRequestActionsEnabled;
            IsTogglePullRequestStateEnabled = previousIsTogglePullRequestStateEnabled;
            IsPullRequestCommentEnabled = previousIsPullRequestCommentEnabled;
            IsMergeEnabled = previousIsMergeEnabled;
            TrackPullRequestListOutcome(
                TelemetryTaxonomy.Results.AuthError,
                listDuration.Elapsed,
                errorKind: "authentication");
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (CompleteSupersededPullRequestListRead(requestId, listDuration.Elapsed))
            {
                return;
            }

            ArePullRequestActionsEnabled = previousArePullRequestActionsEnabled;
            IsTogglePullRequestStateEnabled = previousIsTogglePullRequestStateEnabled;
            IsPullRequestCommentEnabled = previousIsPullRequestCommentEnabled;
            IsMergeEnabled = previousIsMergeEnabled;
            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
            TrackPullRequestListOutcome(
                TelemetryTaxonomy.Results.Error,
                listDuration.Elapsed,
                errorKind: "api");
        }
        catch (HttpRequestException)
        {
            if (CompleteSupersededPullRequestListRead(requestId, listDuration.Elapsed))
            {
                return;
            }

            ArePullRequestActionsEnabled = previousArePullRequestActionsEnabled;
            IsTogglePullRequestStateEnabled = previousIsTogglePullRequestStateEnabled;
            IsPullRequestCommentEnabled = previousIsPullRequestCommentEnabled;
            IsMergeEnabled = previousIsMergeEnabled;
            StatusText = GetString("RepoPullRequest.LoadNetworkError", "JitHub could not reach GitHub to load pull requests.");
            TrackPullRequestListOutcome(
                TelemetryTaxonomy.Results.Error,
                listDuration.Elapsed,
                errorKind: "network");
        }
        catch (OperationCanceledException)
        {
            TrackPullRequestListOutcome(TelemetryTaxonomy.Results.Cancelled, listDuration.Elapsed);
        }
        finally
        {
            if (requestId == _listRequestId)
            {
                IsPullRequestListLoading = false;
            }
        }
    }

    private Task ShowPullRequestAsync(GitHubPullRequest? pullRequest)
    {
        return ShowPullRequestAsync(pullRequest, preserveCurrentState: false, preserveStatusText: false);
    }

    private async Task ShowPullRequestAsync(GitHubPullRequest? pullRequest, bool preserveCurrentState, bool preserveStatusText = false)
    {
        string preservedStatusText = StatusText;
        string preservedCommentDraft = string.Empty;
        Dictionary<long, string> preservedReplyDrafts = new();
        if (pullRequest is null || _navArg is null)
        {
            ResetPullRequestDetails();
            return;
        }

        if (!preserveCurrentState)
        {
            PreparePullRequestForSelectionLoad(pullRequest);
        }

        if (!TryGetActiveToken(out string token))
        {
            return;
        }

        int requestId = ++_detailRequestId;
        CancellationTokenSource diffBuildCancellationTokenSource = BeginPullRequestDiffBuild();

        try
        {
            if (!preserveStatusText)
            {
                StatusText = FormatString("RepoPullRequest.LoadDetailStatus", "Loading pull request #{0}...", pullRequest.Number);
            }

            string accountPartition = GetActiveUserPartition(token);
            PullRequestDetailAggregate? aggregate = await _pullRequestQueryService.GetPullRequestDetailAsync(
                token,
                accountPartition,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                pullRequest.Number);
            if (aggregate is null)
            {
                if (!preserveStatusText)
                {
                    StatusText = GetString("RepoPullRequest.LoadDetailNetworkError", "JitHub could not reach GitHub to load pull request details.");
                }

                return;
            }

            if (requestId != _detailRequestId)
            {
                return;
            }

            _pullRequestNavigationCache.TryGet(
                accountPartition,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                pullRequest.Number,
                out PullRequestNavigationSnapshot existingSnapshot);
            bool canPreserveVisibleSections = _projectedPullRequestDetailNumber == pullRequest.Number;

            GitHubPullRequest latestPullRequest = aggregate.PullRequest;
            GitHubIssue? latestIssue = aggregate.IssueState.ErrorMessage is null
                ? aggregate.Issue
                : aggregate.Issue ?? existingSnapshot?.Issue ?? _selectedPullRequestIssue;
            IReadOnlyList<GitHubIssueComment> comments = PullRequestSectionProjectionPolicy.ProjectSection(
                aggregate.Comments,
                (IReadOnlyList<GitHubIssueComment>?)existingSnapshot?.Comments
                    ?? (canPreserveVisibleSections ? PullRequestComments : []),
                aggregate.CommentsState,
                static comment => comment.Id.ToString(CultureInfo.InvariantCulture));
            IReadOnlyList<GitHubIssueEvent> timelineEvents = PullRequestSectionProjectionPolicy.ProjectSection(
                aggregate.TimelineEvents,
                (IReadOnlyList<GitHubIssueEvent>?)existingSnapshot?.TimelineEvents
                    ?? (canPreserveVisibleSections ? PullRequestTimelineEvents : []),
                aggregate.TimelineState,
                static timelineEvent => timelineEvent.Id.ToString(CultureInfo.InvariantCulture));
            IReadOnlyList<GitHubPullRequestReview> reviews = PullRequestSectionProjectionPolicy.ProjectSection(
                aggregate.Reviews,
                existingSnapshot?.Reviews ?? [],
                aggregate.ReviewsState,
                static (review, ordinal) => review.Id > 0
                    ? $"id:{review.Id.ToString(CultureInfo.InvariantCulture)}"
                    : !string.IsNullOrWhiteSpace(review.NodeId)
                        ? $"node:{review.NodeId}"
                        : $"identityless:{ordinal.ToString(CultureInfo.InvariantCulture)}");
            IReadOnlyList<GitHubPullRequestReviewComment> reviewComments = PullRequestSectionProjectionPolicy.ProjectSection(
                aggregate.ReviewComments,
                existingSnapshot?.ReviewComments ?? [],
                aggregate.ReviewCommentsState,
                static (comment, ordinal) => comment.Id > 0
                    ? $"id:{comment.Id.ToString(CultureInfo.InvariantCulture)}"
                    : !string.IsNullOrWhiteSpace(comment.NodeId)
                        ? $"node:{comment.NodeId}"
                        : $"identityless:{ordinal.ToString(CultureInfo.InvariantCulture)}");
            IReadOnlyList<GitHubCommit> commits = PullRequestSectionProjectionPolicy.ProjectSection(
                aggregate.Commits,
                (IReadOnlyList<GitHubCommit>?)existingSnapshot?.Commits
                    ?? (canPreserveVisibleSections ? PullRequestCommits : []),
                aggregate.CommitsState,
                static commit => commit.Sha);
            Task<CommitDiffDocument> diffBuildTask = CommitDiffParser.ParseAsync(
                aggregate.ChangedFiles,
                diffBuildCancellationTokenSource.Token);

            ReplacePullRequestInCollection(latestPullRequest);
            _selectedPullRequestIssue = latestIssue;
            SetSelectedPullRequest(latestPullRequest);
            PopulatePullRequest(latestPullRequest);
            if (preserveCurrentState)
            {
                preservedCommentDraft = PullRequestCommentDraft;
                preservedReplyDrafts = CaptureReviewReplyDrafts();
            }

            ConfigurePullRequestComments(comments);
            ReplaceCollectionByKey(
                PullRequestComments,
                comments,
                static comment => comment.Id.ToString(CultureInfo.InvariantCulture));

            IReadOnlyList<PullRequestReviewItem> incomingReviewItems = BuildPullRequestReviewItems(reviews, reviewComments);
            IReadOnlyList<PullRequestReviewItem> reviewItems = PullRequestSectionProjectionPolicy.ProjectSection(
                incomingReviewItems,
                canPreserveVisibleSections ? PullRequestReviews : [],
                static review => review.AutomationId,
                aggregate.ReviewsState,
                aggregate.ReviewCommentsState);
            RestoreReviewReplyDrafts(reviewItems, preservedReplyDrafts);
            ApplyReviewReplyInProgressState(reviewItems);
            ReplaceCollectionByKey(
                PullRequestReviews,
                reviewItems,
                static review => review.AutomationId);
            ReplaceCollectionByKey(
                PullRequestTimelineEvents,
                timelineEvents.OrderBy(item => item.CreatedAt),
                static timelineEvent => timelineEvent.Id.ToString(CultureInfo.InvariantCulture));
            ReplaceCollectionByKey(
                PullRequestCommits,
                commits,
                static commit => commit.Sha);

            IsPullRequestCommentsEmptyVisible = PullRequestComments.Count == 0;
            IsPullRequestCommitsEmptyVisible = PullRequestCommits.Count == 0;
            IsPullRequestReviewsEmptyVisible = PullRequestReviews.Count == 0;
            IsPullRequestTimelineEmptyVisible = PullRequestTimelineEvents.Count == 0;
            PullRequestCommentDraft = preserveCurrentState ? preservedCommentDraft : string.Empty;
            CommitDiffDocument incomingDiffDocument = await diffBuildTask;
            if (requestId != _detailRequestId)
            {
                return;
            }

            PullRequestDiffDocument = PullRequestSectionProjectionPolicy.ProjectDiffDocument(
                incomingDiffDocument,
                canPreserveVisibleSections ? PullRequestDiffDocument : CommitDiffDocument.Empty,
                aggregate.ChangedFilesState);
            _projectedPullRequestDetailNumber = pullRequest.Number;
            StoreNavigationSnapshot(latestPullRequest, latestIssue, comments, commits, reviews, reviewComments, timelineEvents, "selection");
            string sectionErrorText = PullRequestSectionProjectionPolicy.CreateErrorText(aggregate);
            StatusText = !string.IsNullOrWhiteSpace(sectionErrorText)
                ? sectionErrorText
                : preserveStatusText
                    ? preservedStatusText
                    : FormatString("RepoPullRequest.LoadedStatus", "Pull request #{0} loaded.", latestPullRequest.Number);
        }
        catch (OperationCanceledException) when (diffBuildCancellationTokenSource.IsCancellationRequested)
        {
        }
        catch (GitHubAuthenticationException)
        {
            if (requestId != _detailRequestId)
            {
                return;
            }

            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (requestId != _detailRequestId)
            {
                return;
            }

            if (!preserveStatusText)
            {
                StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
            }
        }
        catch (HttpRequestException)
        {
            if (requestId != _detailRequestId)
            {
                return;
            }

            if (!preserveStatusText)
            {
                StatusText = GetString("RepoPullRequest.LoadDetailNetworkError", "JitHub could not reach GitHub to load pull request details.");
            }
        }
        finally
        {
            Interlocked.CompareExchange(
                ref _pullRequestDiffBuildCancellationTokenSource,
                null,
                diffBuildCancellationTokenSource);
            diffBuildCancellationTokenSource.Dispose();
        }
    }

    private void PopulatePullRequest(GitHubPullRequest pullRequest)
    {
        PullRequestTitleText = FormatString("RepoPullRequest.DetailTitleFormat", "#{0} {1}", pullRequest.Number, pullRequest.Title);
        PullRequestMetaText = FormatString(
            "RepoPullRequest.DetailMetaFormat",
            "{0}  •  @{1}  •  {2} -> {3}  •  Updated {4:g}  •  {5}",
            GetPullRequestStateDisplay(pullRequest),
            pullRequest.User.Login,
            pullRequest.Head.GitRef,
            pullRequest.Base.GitRef,
            pullRequest.UpdatedAt.LocalDateTime,
            FormatCommentCount(pullRequest.Comments));
        PullRequestMetadataText = FormatPullRequestMetadataSummary(_selectedPullRequestIssue, pullRequest);
        PullRequestReactionsText = _selectedPullRequestIssue?.Reactions.DisplayText
            ?? GetString("RepoPullRequest.ReactionsLoading", "Reactions: loading...");
        PullRequestBodyText = string.IsNullOrWhiteSpace(pullRequest.Body)
            ? GetString("RepoPullRequest.NoDescription", "No pull request description is available.")
            : pullRequest.Body;
        TogglePullRequestStateButtonText = GetTogglePullRequestStateButtonText(pullRequest);
        MergeStatusText = pullRequest.Merged
            ? GetString("RepoPullRequest.AlreadyMergedStatus", "This pull request is already merged.")
            : pullRequest.MergeableState is null
                ? GetString("RepoPullRequest.MergeablePendingStatus", "GitHub is still calculating mergeability.")
                : FormatString("RepoPullRequest.MergeableStateFormat", "Merge status: {0}.", pullRequest.MergeableState);
        ApplyPullRequestCapabilities(pullRequest);
        UpdatePullRequestCommentEnabledState();
        ReplaceCollectionByKey(
            RequestedReviewers,
            pullRequest.RequestedReviewers,
            static reviewer => reviewer.Login);
        ApplyIssueInspectorMetadata(_selectedPullRequestIssue);
        NotifySelectedPullRequestPropertiesChanged();
    }

    private void ResetPullRequestDetails()
    {
        CancelPullRequestDiffBuild();
        _projectedPullRequestDetailNumber = 0;
        _detailRequestId++;
        _selectedPullRequestIssue = null;
        ArePullRequestActionsEnabled = false;
        IsTogglePullRequestStateEnabled = false;
        IsMergeEnabled = false;
        IsPullRequestCommentEnabled = false;
        CanEditPullRequest = false;
        CanManagePullRequestMetadata = false;
        CanReactToPullRequest = false;
        CanSubmitReviewComment = false;
        CanApprovePullRequest = false;
        CanRequestPullRequestChanges = false;
        OnPropertyChanged(nameof(CanSubmitPullRequestReview));
        CanMergeWithMergeCommit = false;
        CanMergeWithSquash = false;
        CanMergeWithRebase = false;
        _canCommentOnPullRequest = false;
        PullRequestTitleText = GetString("RepoPullRequest.SelectTitle", "Select a pull request");
        PullRequestMetaText = GetString("RepoPullRequest.SelectSubtitle", "Choose a pull request to inspect its details.");
        PullRequestMetadataText = string.Empty;
        PullRequestReactionsText = GetString("RepoPullRequest.NoReactions", "Reactions: none");
        MergeStatusText = GetString("RepoPullRequest.MergeDetailsPlaceholder", "Merge details will appear here.");
        PullRequestBodyText = string.Empty;
        PullRequestCommentDraft = string.Empty;
        PullRequestDiffDocument = CommitDiffDocument.Empty;
        PullRequestDiffRowProjection = CommitDiffRowProjection.Empty;
        PullRequestFileFilterText = string.Empty;
        PullRequestDiffSearchText = string.Empty;
        PullRequestComments.Clear();
        PullRequestCommits.Clear();
        PullRequestReviews.Clear();
        PullRequestTimelineEvents.Clear();
        SelectedLabels.Clear();
        SelectedAssignees.Clear();
        RequestedReviewers.Clear();
        IsPullRequestCommentsEmptyVisible = false;
        IsPullRequestCommitsEmptyVisible = false;
        IsPullRequestReviewsEmptyVisible = false;
        IsPullRequestTimelineEmptyVisible = false;
        TogglePullRequestStateButtonText = GetString("RepoPullRequest.CloseButton", "Close pull request");
        NotifySelectedPullRequestPropertiesChanged();
    }

    private void PreparePullRequestForSelectionLoad(GitHubPullRequest pullRequest)
    {
        _selectedPullRequestIssue = null;
        ArePullRequestActionsEnabled = false;
        IsTogglePullRequestStateEnabled = false;
        IsMergeEnabled = false;
        IsPullRequestCommentEnabled = false;
        PullRequestTitleText = FormatString("RepoPullRequest.DetailTitleFormat", "#{0} {1}", pullRequest.Number, pullRequest.Title);
        PullRequestMetaText = FormatString(
            "RepoPullRequest.DetailMetaFormat",
            "{0}  •  @{1}  •  {2} -> {3}  •  Updated {4:g}  •  {5}",
            GetPullRequestStateDisplay(pullRequest),
            pullRequest.User.Login,
            pullRequest.Head.GitRef,
            pullRequest.Base.GitRef,
            pullRequest.UpdatedAt.LocalDateTime,
            FormatCommentCount(pullRequest.Comments));
        PullRequestMetadataText = FormatPullRequestMetadataSummary(null, pullRequest);
        PullRequestReactionsText = GetString("RepoPullRequest.ReactionsLoading", "Reactions: loading...");
        MergeStatusText = GetString("RepoPullRequest.MergeDetailsLoading", "Loading merge details...");
        PullRequestBodyText = string.IsNullOrWhiteSpace(pullRequest.Body)
            ? GetString("RepoPullRequest.BodyLoading", "Loading pull request details...")
            : pullRequest.Body;
        PullRequestCommentDraft = string.Empty;
        PullRequestComments.Clear();
        PullRequestCommits.Clear();
        PullRequestReviews.Clear();
        PullRequestTimelineEvents.Clear();
        SelectedLabels.Clear();
        SelectedAssignees.Clear();
        ReplaceCollectionByKey(
            RequestedReviewers,
            pullRequest.RequestedReviewers,
            static reviewer => reviewer.Login);
        IsPullRequestCommentsEmptyVisible = false;
        IsPullRequestCommitsEmptyVisible = false;
        IsPullRequestReviewsEmptyVisible = false;
        IsPullRequestTimelineEmptyVisible = false;
        TogglePullRequestStateButtonText = GetTogglePullRequestStateButtonText(pullRequest);
        StatusText = FormatString("RepoPullRequest.LoadDetailStatus", "Loading pull request #{0}...", pullRequest.Number);
        NotifySelectedPullRequestPropertiesChanged();
    }

    private void CapturePullRequestDetailSnapshot(int pullRequestNumber)
    {
        if (pullRequestNumber <= 0)
        {
            _pendingPullRequestSelectionState = null;
            return;
        }

        _pendingPullRequestSelectionState = new PullRequestDetailSnapshot(
            pullRequestNumber,
            _selectedPullRequestIssue,
            StatusText,
            PullRequestTitleText,
            PullRequestMetaText,
            PullRequestMetadataText,
            PullRequestReactionsText,
            MergeStatusText,
            PullRequestBodyText,
            PullRequestCommentDraft,
            TogglePullRequestStateButtonText,
            ArePullRequestActionsEnabled,
            IsTogglePullRequestStateEnabled,
            IsMergeEnabled,
            IsPullRequestCommentEnabled,
            IsPullRequestCommentsEmptyVisible,
            IsPullRequestCommitsEmptyVisible,
            IsPullRequestReviewsEmptyVisible,
            IsPullRequestTimelineEmptyVisible,
            PullRequestComments.ToArray(),
            PullRequestCommits.ToArray(),
            PullRequestReviews.ToArray(),
            PullRequestTimelineEvents.ToArray(),
            PullRequestDiffDocument);
    }

    private bool TryRestorePendingPullRequestSelectionState(int pullRequestNumber)
    {
        if (_pendingPullRequestSelectionState is null || _pendingPullRequestSelectionState.PullRequestNumber != pullRequestNumber)
        {
            return false;
        }

        RestorePullRequestDetailSnapshot(_pendingPullRequestSelectionState);
        _pendingPullRequestSelectionState = null;
        return true;
    }

    private void RestorePullRequestDetailSnapshot(PullRequestDetailSnapshot snapshot)
    {
        _selectedPullRequestIssue = snapshot.PullRequestIssue;
        StatusText = snapshot.StatusText;
        PullRequestTitleText = snapshot.TitleText;
        PullRequestMetaText = snapshot.MetaText;
        PullRequestMetadataText = snapshot.MetadataText;
        PullRequestReactionsText = snapshot.ReactionsText;
        MergeStatusText = snapshot.MergeStatusText;
        PullRequestBodyText = snapshot.BodyText;
        PullRequestCommentDraft = snapshot.CommentDraft;
        TogglePullRequestStateButtonText = snapshot.ToggleStateButtonText;
        ArePullRequestActionsEnabled = snapshot.AreActionsEnabled;
        IsTogglePullRequestStateEnabled = snapshot.IsToggleStateEnabled;
        IsMergeEnabled = snapshot.IsMergeEnabled;
        IsPullRequestCommentEnabled = snapshot.IsCommentEnabled;
        ConfigurePullRequestComments(snapshot.Comments);
        ReplaceCollectionByKey(
            PullRequestComments,
            snapshot.Comments,
            static comment => comment.Id.ToString(CultureInfo.InvariantCulture));
        ReplaceCollectionByKey(
            PullRequestCommits,
            snapshot.Commits,
            static commit => commit.Sha);
        ReplaceCollectionByKey(
            PullRequestReviews,
            snapshot.Reviews,
            static review => review.AutomationId);
        ReplaceCollectionByKey(
            PullRequestTimelineEvents,
            snapshot.TimelineEvents,
            static timelineEvent => timelineEvent.Id.ToString(CultureInfo.InvariantCulture));

        IsPullRequestCommentsEmptyVisible = snapshot.IsCommentsEmptyVisible;
        IsPullRequestCommitsEmptyVisible = snapshot.IsCommitsEmptyVisible;
        IsPullRequestReviewsEmptyVisible = snapshot.IsReviewsEmptyVisible;
        IsPullRequestTimelineEmptyVisible = snapshot.IsTimelineEmptyVisible;
        PullRequestDiffDocument = snapshot.DiffDocument;
        if (SelectedPullRequest is not null)
        {
            ApplyPullRequestCapabilities(SelectedPullRequest);
            UpdatePullRequestCommentEnabledState();
        }
        ApplyIssueInspectorMetadata(_selectedPullRequestIssue);
        NotifySelectedPullRequestPropertiesChanged();
    }

    private async Task ApplyPullRequestListFilterAsync(
        int preferredPullRequestNumber,
        bool refreshSelectionDetails = true,
        bool suppressDetailRefresh = false,
        bool deferDetailLoad = false)
    {
        GitHubPullRequest? previousSelectedPullRequest = SelectedPullRequest;
        IEnumerable<GitHubPullRequest> filteredPullRequests = _loadedPullRequests.Where(
            pullRequest => MatchesPullRequestQuery(pullRequest) || IsPinnedPullRequest(pullRequest));

        string searchText = SearchText.Trim();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            filteredPullRequests = filteredPullRequests.Where(pullRequest => MatchesPullRequestSearch(pullRequest, searchText));
        }

        GitHubPullRequest? pinnedPullRequest = filteredPullRequests.FirstOrDefault(IsPinnedPullRequest);
        List<GitHubPullRequest> visiblePullRequests = SortPullRequests(filteredPullRequests.Where(
            pullRequest => !IsPinnedPullRequest(pullRequest))).ToList();
        if (pinnedPullRequest is not null)
        {
            visiblePullRequests.Insert(0, pinnedPullRequest);
        }

        GitHubPullRequest? selectedPullRequest = preferredPullRequestNumber > 0
            ? visiblePullRequests.FirstOrDefault(pullRequest => pullRequest.Number == preferredPullRequestNumber)
            : visiblePullRequests.FirstOrDefault();
        bool preserveFocusedDetails = selectedPullRequest is null
            && visiblePullRequests.Count == 0
            && _lastFocusedPullRequestNumber > 0;
        if (suppressDetailRefresh)
        {
            return;
        }

        if (preserveFocusedDetails)
        {
            selectedPullRequest = _loadedPullRequests.FirstOrDefault(pullRequest => pullRequest.Number == _lastFocusedPullRequestNumber)
                ?? previousSelectedPullRequest;
        }
        StatusText = visiblePullRequests.Count == 0
            ? GetString("RepoPullRequest.NoMatchesStatus", "No pull requests matched the current filters.")
            : visiblePullRequests.Count == 1
                ? FormatString("RepoPullRequest.ShowingSingleStatus", "Showing {0} pull request.", visiblePullRequests.Count)
                : FormatString("RepoPullRequest.ShowingPluralStatus", "Showing {0} pull requests.", visiblePullRequests.Count);

        bool selectionChanged = previousSelectedPullRequest?.Number != selectedPullRequest?.Number;
        if (SelectedPullRequest?.Number != selectedPullRequest?.Number)
        {
            CancelPendingSelectionLoad();
        }

        _suppressSelectionChanged = true;
        try
        {
            ReplaceCollectionByKey(
                PullRequests,
                visiblePullRequests,
                static pullRequest => pullRequest.Number.ToString(CultureInfo.InvariantCulture));

            SelectedPullRequest = selectedPullRequest;
            if (selectedPullRequest is not null)
            {
                _lastFocusedPullRequestNumber = selectedPullRequest.Number;
            }
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        if (preserveFocusedDetails)
        {
            if (refreshSelectionDetails && selectedPullRequest is not null)
            {
                await ShowPullRequestAsync(selectedPullRequest, preserveCurrentState: true, preserveStatusText: true);
            }
        }
        else if (refreshSelectionDetails || selectionChanged)
        {
            if (deferDetailLoad && selectedPullRequest is not null)
            {
                NotifySelectedPullRequestHeaderPropertiesChanged();
                SchedulePullRequestDetailLoad(selectedPullRequest, InitialDetailLoadDelay);
            }
            else
            {
                await ShowPullRequestAsync(selectedPullRequest, preserveCurrentState: refreshSelectionDetails && !selectionChanged);
            }
        }
    }

    private void ApplyPullRequestQueryFromFilters()
    {
        _pullRequestQuery.State = SelectedStateOption?.Value ?? "open";
        _pullRequestQuery.Sort = SelectedSortOption?.Value ?? "updated";
        _pullRequestQuery.Direction = SelectedDirectionOption?.Value ?? "desc";
        _pullRequestQuery.Head = NormalizeFilterText(HeadFilterText);
        _pullRequestQuery.Base = NormalizeFilterText(BaseFilterText);
    }

    private void ResetFilters()
    {
        SearchText = string.Empty;
        HeadFilterText = string.Empty;
        BaseFilterText = string.Empty;
        SelectedStateOption = StateOptions[0];
        SelectedSortOption = SortOptions[0];
        SelectedDirectionOption = DirectionOptions[0];
        ApplyPullRequestQueryFromFilters();
    }

    private async Task<bool> RefreshPullRequestSelectionAsync(GitHubPullRequest pullRequest, string token)
    {
        if (_navArg is null)
        {
            return false;
        }

        int requestId = _listRequestId;
        PullRequestCapabilitySnapshot? snapshot;
        try
        {
            await _pullRequestQueryService.InvalidatePullRequestAsync(
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                pullRequest.Number);
            snapshot = await _pullRequestQueryService.RefreshPullRequestCapabilitiesAsync(
                token,
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                pullRequest.Number);
            if (snapshot is null)
            {
                return false;
            }
        }
        catch (GitHubAuthenticationException)
        {
            if (requestId != _listRequestId)
            {
                return false;
            }

            throw;
        }
        catch (GitHubApiException)
        {
            if (requestId != _listRequestId)
            {
                return false;
            }

            throw;
        }
        catch (HttpRequestException)
        {
            if (requestId != _listRequestId)
            {
                return false;
            }

            throw;
        }

        if (requestId != _listRequestId)
        {
            return false;
        }

        GitHubPullRequest refreshedPullRequest = snapshot.PullRequest;
        _capabilityRepository = snapshot.Repository;
        _selectedPullRequestIssue = snapshot.Issue;
        _capabilityDenials.ConfirmSuccessfulRefresh(refreshedPullRequest.Number);
        ReplacePullRequestInCollection(refreshedPullRequest);
        if (_loadedPullRequests.All(existingPullRequest => existingPullRequest.Number != refreshedPullRequest.Number)
            && MatchesPullRequestQuery(refreshedPullRequest))
        {
            _loadedPullRequests.Add(refreshedPullRequest);
        }

        bool isRetainedHiddenPullRequest = PullRequests.Count == 0 && _lastFocusedPullRequestNumber == pullRequest.Number;
        if (SelectedPullRequest?.Number == pullRequest.Number && !isRetainedHiddenPullRequest)
        {
            _pinnedPullRequestNumber = MatchesPullRequestQuery(refreshedPullRequest)
                ? 0
                : refreshedPullRequest.Number;
        }
        else if (_pinnedPullRequestNumber == refreshedPullRequest.Number
            && (MatchesPullRequestQuery(refreshedPullRequest) || isRetainedHiddenPullRequest))
        {
            _pinnedPullRequestNumber = 0;
        }

        int preferredPullRequestNumber = SelectedPullRequest?.Number
            ?? (_pinnedPullRequestNumber == refreshedPullRequest.Number ? refreshedPullRequest.Number : 0);
        await ApplyPullRequestListFilterAsync(
            preferredPullRequestNumber,
            refreshSelectionDetails: preferredPullRequestNumber == pullRequest.Number);
        return true;
    }

    private async Task<bool> TryRefreshPullRequestSelectionAfterMutationAsync(
        GitHubPullRequest pullRequest,
        string token,
        string refreshFailureStatus)
    {
        try
        {
            return await RefreshPullRequestSelectionAsync(pullRequest, token);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException)
        {
            if (IsSelectedPullRequest(pullRequest))
            {
                StatusText = refreshFailureStatus;
            }
        }
        catch (HttpRequestException)
        {
            if (IsSelectedPullRequest(pullRequest))
            {
                StatusText = refreshFailureStatus;
            }
        }

        return false;
    }

    private void SetSelectedPullRequest(GitHubPullRequest? pullRequest)
    {
        if (SelectedPullRequest?.Number != pullRequest?.Number)
        {
            CancelPendingSelectionLoad();
        }

        _suppressSelectionChanged = true;
        SelectedPullRequest = pullRequest;
        if (pullRequest is not null)
        {
            _lastFocusedPullRequestNumber = pullRequest.Number;
            _capabilityDenials.TrackPullRequest(pullRequest.Number);
        }
        _suppressSelectionChanged = false;
        NotifySelectedPullRequestPropertiesChanged();
    }

    private void SchedulePullRequestDetailLoad(GitHubPullRequest pullRequest, TimeSpan delay)
    {
        CancelPendingSelectionLoad();
        CancellationTokenSource cancellationTokenSource = new();
        _selectionLoadCancellationTokenSource = cancellationTokenSource;
        BackgroundTaskObserver.Run(
            () => ShowPullRequestAfterSelectionDelayAsync(
                pullRequest,
                cancellationTokenSource.Token,
                delay),
            "pull_requests",
            _telemetryService);
    }

    private async Task ShowPullRequestAfterSelectionDelayAsync(
        GitHubPullRequest pullRequest,
        CancellationToken cancellationToken,
        TimeSpan? delay = null)
    {
        try
        {
            await Task.Delay(delay ?? SelectionLoadDebounce, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        _pendingPullRequestSelectionState = null;
        await ShowPullRequestAsync(pullRequest, preserveCurrentState: true, preserveStatusText: true);
    }

    private void CancelPendingSelectionLoad()
    {
        _selectionLoadCancellationTokenSource?.Cancel();
        _selectionLoadCancellationTokenSource?.Dispose();
        _selectionLoadCancellationTokenSource = null;
    }

    private CancellationTokenSource BeginPullRequestDiffBuild()
    {
        CancellationTokenSource next = new();
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _pullRequestDiffBuildCancellationTokenSource,
            next);
        CancelWithoutDisposing(previous);
        return next;
    }

    private void CancelPullRequestDiffBuild()
    {
        CancellationTokenSource? cancellationTokenSource = Interlocked.Exchange(
            ref _pullRequestDiffBuildCancellationTokenSource,
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

    private static string? NormalizeFilterText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private void UpdatePullRequestCommentEnabledState()
    {
        IsPullRequestCommentEnabled = _canCommentOnPullRequest && !_isPullRequestCommentSubmissionInProgress;
    }

    private void ConfigurePullRequestComments(IEnumerable<GitHubIssueComment> comments)
    {
        foreach (GitHubIssueComment comment in comments)
        {
            ApplyCommentMinimizationOverride(comment);
            comment.ViewerLogin = AuthenticatedLogin;
            comment.CanViewerReact = CanReactToPullRequest;
            comment.CanViewerReply = _canCommentOnPullRequest;
            comment.CanViewerModerate = CanManagePullRequestMetadata;
        }
    }

    private void ApplyCommentMinimizationOverride(GitHubIssueComment comment)
    {
        if (!string.IsNullOrWhiteSpace(comment.NodeId) &&
            _commentMinimizationOverrides.TryGetValue(comment.NodeId, out bool isMinimized))
        {
            comment.Minimized = isMinimized ? comment.Minimized ?? new GitHubIssueCommentMinimization() : null;
        }
    }

    private void ApplyCommentMinimizationOverride(GitHubPullRequestReviewComment comment)
    {
        if (!string.IsNullOrWhiteSpace(comment.NodeId) &&
            _commentMinimizationOverrides.TryGetValue(comment.NodeId, out bool isMinimized))
        {
            comment.Minimized = isMinimized ? comment.Minimized ?? new GitHubIssueCommentMinimization() : null;
        }
    }

    private void QueuePullRequestDiffProjectionUpdate(bool debounce)
    {
        int requestVersion = Interlocked.Increment(ref _pullRequestDiffProjectionVersion);
        CommitDiffDocument document = PullRequestDiffDocument;
        string fileFilter = PullRequestFileFilterText;
        string search = PullRequestDiffSearchText;
        BackgroundTaskObserver.Run(
            () => BuildPullRequestDiffProjectionAsync(
                document,
                fileFilter,
                search,
                requestVersion,
                debounce),
            "pull_requests",
            _telemetryService);
    }

    private async Task BuildPullRequestDiffProjectionAsync(
        CommitDiffDocument document,
        string fileFilter,
        string search,
        int requestVersion,
        bool debounce)
    {
        if (debounce)
        {
            await Task.Delay(160);
        }

        CommitDiffRowProjection projection = await Task.Run(() =>
            CommitDiffRowProjection.Create(document, fileFilter, search));
        if (requestVersion == _pullRequestDiffProjectionVersion)
        {
            PullRequestDiffRowProjection = projection;
        }
    }

    private bool IsSelectedPullRequest(GitHubPullRequest pullRequest)
    {
        return SelectedPullRequest?.Number == pullRequest.Number;
    }

    private bool IsPinnedPullRequest(GitHubPullRequest pullRequest)
    {
        return _pinnedPullRequestNumber > 0 && pullRequest.Number == _pinnedPullRequestNumber;
    }

    private void RemoveClearedPinnedPullRequestFromVisibleList(int clearedPinnedPullRequestNumber)
    {
        if (clearedPinnedPullRequestNumber <= 0)
        {
            return;
        }

        int pinnedIndex = PullRequests
            .Select((pullRequest, index) => new { pullRequest, index })
            .Where(item => item.pullRequest.Number == clearedPinnedPullRequestNumber)
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        if (pinnedIndex < 0)
        {
            return;
        }

        GitHubPullRequest pinnedPullRequest = PullRequests[pinnedIndex];
        bool matchesVisibleFilters = MatchesPullRequestQuery(pinnedPullRequest);
        string searchText = SearchText.Trim();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            matchesVisibleFilters = matchesVisibleFilters && MatchesPullRequestSearch(pinnedPullRequest, searchText);
        }

        if (matchesVisibleFilters)
        {
            return;
        }

        _suppressSelectionChanged = true;
        try
        {
            PullRequests.RemoveAt(pinnedIndex);
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    private IEnumerable<GitHubPullRequest> SortPullRequests(IEnumerable<GitHubPullRequest> pullRequests)
    {
        bool descending = !string.Equals(_pullRequestQuery.Direction, "asc", StringComparison.OrdinalIgnoreCase);
        return (_pullRequestQuery.Sort ?? "updated").ToLowerInvariant() switch
        {
            "created" => descending
                ? pullRequests.OrderByDescending(pullRequest => pullRequest.CreatedAt).ThenByDescending(pullRequest => pullRequest.Number)
                : pullRequests.OrderBy(pullRequest => pullRequest.CreatedAt).ThenBy(pullRequest => pullRequest.Number),
            "popularity" => descending
                ? pullRequests.OrderByDescending(pullRequest => pullRequest.Comments).ThenByDescending(pullRequest => pullRequest.UpdatedAt).ThenByDescending(pullRequest => pullRequest.Number)
                : pullRequests.OrderBy(pullRequest => pullRequest.Comments).ThenBy(pullRequest => pullRequest.UpdatedAt).ThenBy(pullRequest => pullRequest.Number),
            "long-running" => descending
                ? pullRequests.OrderByDescending(pullRequest => pullRequest.CreatedAt).ThenByDescending(pullRequest => pullRequest.UpdatedAt).ThenByDescending(pullRequest => pullRequest.Number)
                : pullRequests.OrderBy(pullRequest => pullRequest.CreatedAt).ThenBy(pullRequest => pullRequest.UpdatedAt).ThenBy(pullRequest => pullRequest.Number),
            _ => descending
                ? pullRequests.OrderByDescending(pullRequest => pullRequest.UpdatedAt).ThenByDescending(pullRequest => pullRequest.Number)
                : pullRequests.OrderBy(pullRequest => pullRequest.UpdatedAt).ThenBy(pullRequest => pullRequest.Number)
        };
    }

    private Dictionary<long, string> CaptureReviewReplyDrafts()
    {
        return PullRequestReviews
            .SelectMany(review => review.Threads)
            .Where(thread => !string.IsNullOrEmpty(thread.ReplyText))
            .ToDictionary(thread => thread.CommentId, thread => thread.ReplyText);
    }

    private static void RestoreReviewReplyDrafts(
        IEnumerable<PullRequestReviewItem> reviewItems,
        IReadOnlyDictionary<long, string> replyDrafts)
    {
        if (replyDrafts.Count == 0)
        {
            return;
        }

        foreach (PullRequestReviewThreadItem thread in reviewItems.SelectMany(review => review.Threads))
        {
            if (replyDrafts.TryGetValue(thread.CommentId, out string? replyText))
            {
                thread.ReplyText = replyText;
            }
        }
    }

    private void ApplyReviewReplyInProgressState(IEnumerable<PullRequestReviewItem> reviewItems)
    {
        foreach (PullRequestReviewThreadItem thread in reviewItems.SelectMany(review => review.Threads))
        {
            thread.IsReplyInProgress = _inProgressReviewReplyCommentIds.Contains(thread.CommentId);
        }
    }

    private static bool MatchesPullRequestSearch(GitHubPullRequest pullRequest, string searchText)
    {
        return pullRequest.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(pullRequest.Body) && pullRequest.Body.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            || pullRequest.User.Login.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || pullRequest.Head.GitRef.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || pullRequest.Base.GitRef.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || pullRequest.Number.ToString().Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private bool MatchesPullRequestQuery(GitHubPullRequest pullRequest)
    {
        return MatchesStateFilter(pullRequest.State, _pullRequestQuery.State)
            && MatchesBranchFilter(_pullRequestQuery.Head, pullRequest.Head)
            && MatchesBranchFilter(_pullRequestQuery.Base, pullRequest.Base)
            && MatchesLongRunningFilter(pullRequest);
    }

    private bool MatchesLongRunningFilter(GitHubPullRequest pullRequest)
    {
        if (!string.Equals(_pullRequestQuery.Sort, "long-running", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        return string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase)
            && pullRequest.CreatedAt <= cutoff
            && pullRequest.UpdatedAt >= cutoff;
    }

    private static bool MatchesBranchFilter(string? filter, GitHubPullRequestBranch branch)
    {
        return string.IsNullOrWhiteSpace(filter)
            || string.Equals(branch.GitRef, filter, StringComparison.OrdinalIgnoreCase)
            || string.Equals(branch.Label, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesStateFilter(string state, string filter)
    {
        return string.IsNullOrWhiteSpace(filter)
            || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, filter, StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<PullRequestReviewItem> BuildPullRequestReviewItems(
        IReadOnlyList<GitHubPullRequestReview> reviews,
        IReadOnlyList<GitHubPullRequestReviewComment> reviewComments)
    {
        int pullRequestNumber = SelectedPullRequest?.Number ?? _navArg?.PullRequestId ?? 0;
        List<PullRequestReviewItem> projectedReviews = [];
        Dictionary<long, PullRequestReviewItem> reviewLookup = [];
        for (int reviewOrdinal = 0; reviewOrdinal < reviews.Count; reviewOrdinal++)
        {
            GitHubPullRequestReview review = reviews[reviewOrdinal];
            PullRequestReviewItem reviewItem = new(
                review,
                FormatReviewState(review.State),
                PendingReviewText,
                UnknownUserText,
                OpenButtonText,
                $"pr:{pullRequestNumber}:review:{reviewOrdinal}");
            projectedReviews.Add(reviewItem);
            if (review.Id > 0)
            {
                reviewLookup.TryAdd(review.Id, reviewItem);
            }
        }

        Dictionary<long, PullRequestReviewThreadItem> threadLookup = [];
        List<PullRequestReviewItem> syntheticReviews = [];

        GitHubPullRequestReviewComment[] orderedComments = reviewComments
            .OrderBy(item => item.CreatedAt)
            .ToArray();
        for (int commentOrdinal = 0; commentOrdinal < orderedComments.Length; commentOrdinal++)
        {
            GitHubPullRequestReviewComment comment = orderedComments[commentOrdinal];
            ApplyCommentMinimizationOverride(comment);
            if (comment.InReplyToId.HasValue && threadLookup.TryGetValue(comment.InReplyToId.Value, out PullRequestReviewThreadItem? existingThread))
            {
                existingThread.AddReply(comment, comment.Reactions.DisplayText, UnknownUserText, ReplyPrefixText, OpenButtonText, ReactionsButtonText);
                continue;
            }

            PullRequestReviewThreadItem threadItem = new(
                comment,
                ChangedFileText,
                comment.Reactions.DisplayText,
                UnknownUserText,
                OpenButtonText,
                ReactionsButtonText,
                ReplyPlaceholderText,
                ReplyButtonText,
                ReplyPrefixText,
                $"pr:{pullRequestNumber}:thread:{commentOrdinal}");
            threadItem.ViewerLogin = AuthenticatedLogin;
            threadItem.CanReact = CanReactToPullRequest;
            threadItem.CanReply = _canCommentOnPullRequest;
            threadItem.CanModerate = CanManagePullRequestMetadata;
            if (comment.Id > 0)
            {
                threadLookup[comment.Id] = threadItem;
            }

            if (comment.PullRequestReviewId.HasValue && reviewLookup.TryGetValue(comment.PullRequestReviewId.Value, out PullRequestReviewItem? reviewItem))
            {
                reviewItem.Threads.Add(threadItem);
                continue;
            }

            PullRequestReviewItem syntheticReview = PullRequestReviewItem.CreateSynthetic(
                comment,
                ReviewCommentStateText,
                UnknownUserText,
                OpenButtonText,
                $"pr:{pullRequestNumber}:synthetic-review:{commentOrdinal}");
            syntheticReview.Threads.Add(threadItem);
            syntheticReviews.Add(syntheticReview);
        }

        return projectedReviews
            .Concat(syntheticReviews)
            .OrderBy(review => review.SortKey)
            .ToList();
    }

    private string FormatReviewState(string? state)
    {
        return state?.ToUpperInvariant() switch
        {
            "APPROVED" => GetString("RepoPullRequest.ReviewStateApproved", "Approved"),
            "CHANGES_REQUESTED" => GetString("RepoPullRequest.ReviewStateChangesRequested", "Requested changes"),
            "COMMENTED" => GetString("RepoPullRequest.ReviewStateCommented", "Commented"),
            "DISMISSED" => GetString("RepoPullRequest.ReviewStateDismissed", "Dismissed"),
            "PENDING" => GetString("RepoPullRequest.ReviewStatePending", "Pending"),
            _ => string.IsNullOrWhiteSpace(state)
                ? GetString("RepoPullRequest.ReviewStateDefault", "Review")
                : string.Join(" ", state
                    .Split('_', StringSplitOptions.RemoveEmptyEntries)
                    .Select(word => char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()))
        };
    }

    private static string TrimDiffHunk(string? diffHunk)
    {
        if (string.IsNullOrWhiteSpace(diffHunk))
        {
            return string.Empty;
        }

        string[] lines = diffHunk
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .TakeLast(4)
            .ToArray();
        return string.Join(Environment.NewLine, lines);
    }

    private async Task ApplyPullRequestReactionSelectionAsync(
        GitHubPullRequest targetPullRequest,
        ReactionDialogTargetKind targetKind,
        long targetId,
        HashSet<string> selectedContents,
        Dictionary<string, long> existingReactionIds)
    {
        if (_navArg is null || SelectedPullRequest is null || !CanReactToPullRequest || !TryGetActiveToken(out string token))
        {
            return;
        }

        try
        {
            string owner = _navArg.Repo.Owner.Login;
            string repoName = _navArg.Repo.Name;
            foreach (string content in selectedContents.Except(existingReactionIds.Keys, StringComparer.OrdinalIgnoreCase))
            {
                switch (targetKind)
                {
                    case ReactionDialogTargetKind.Issue:
                        await _gitHubClientService.ReactToIssueAsync(token, owner, repoName, targetPullRequest.Number, content);
                        break;
                    case ReactionDialogTargetKind.Comment:
                        await _gitHubClientService.ReactToIssueCommentAsync(token, owner, repoName, targetId, content);
                        break;
                    case ReactionDialogTargetKind.ReviewComment:
                        await _gitHubClientService.ReactToPullRequestReviewCommentAsync(token, owner, repoName, targetId, content);
                        break;
                }
            }

            foreach (string content in existingReactionIds.Keys.Except(selectedContents, StringComparer.OrdinalIgnoreCase))
            {
                long reactionId = existingReactionIds[content];
                switch (targetKind)
                {
                    case ReactionDialogTargetKind.Issue:
                        await _gitHubClientService.DeleteIssueReactionAsync(token, owner, repoName, targetPullRequest.Number, reactionId);
                        break;
                    case ReactionDialogTargetKind.Comment:
                        await _gitHubClientService.DeleteIssueCommentReactionAsync(token, owner, repoName, targetId, reactionId);
                        break;
                    case ReactionDialogTargetKind.ReviewComment:
                        await _gitHubClientService.DeletePullRequestReviewCommentReactionAsync(token, owner, repoName, targetId, reactionId);
                        break;
                }
            }

            await RefreshPullRequestSelectionAsync(_loadedPullRequests.FirstOrDefault(pullRequest => pullRequest.Number == targetPullRequest.Number) ?? targetPullRequest, token);
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Reaction, TelemetryTaxonomy.Results.Success);
        }
        catch (GitHubAuthenticationException)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Reaction, TelemetryTaxonomy.Results.AuthError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Reaction, TelemetryTaxonomy.Results.Error);
            DisableRejectedCapability(ex, "reaction", targetPullRequest.Number);
            if (!IsSelectedPullRequest(targetPullRequest))
            {
                return;
            }

            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "pull-requests");
        }
        catch (HttpRequestException)
        {
            TrackPullRequestAction(TelemetryTaxonomy.Actions.Reaction, TelemetryTaxonomy.Results.Error);
            if (!IsSelectedPullRequest(targetPullRequest))
            {
                return;
            }

            StatusText = GetString("RepoPullRequest.ReactionsNetworkError", "JitHub could not reach GitHub to update reactions.");
        }
    }

    public void PrefetchPullRequest(GitHubPullRequest? pullRequest, PullRequestPrefetchReason reason)
    {
        if (_navArg is null || pullRequest is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        string userPartition = GetActiveUserPartition(token);
        string owner = _navArg.Repo.Owner.Login;
        string repositoryName = _navArg.Repo.Name;
        _hoverPrefetch.Schedule(
            HoverPrefetchDebounce,
            () =>
            {
                PullRequestTelemetry.TrackPrefetchStarted(_telemetryService, reason);
                return _pullRequestNavigationCache.SchedulePrefetch(
                    token,
                    userPartition,
                    owner,
                    repositoryName,
                    pullRequest.Number,
                    reason,
                    TimeSpan.Zero,
                    (result, duration) => PullRequestTelemetry.TrackPrefetchCompleted(
                        _telemetryService,
                        reason,
                        result,
                        duration));
            });
    }

    public void CancelHoverPrefetch() => _hoverPrefetch.Cancel();

    public void CancelPredictivePrefetches()
    {
        _navigationRefresh?.Cancel();
        _navigationRefresh?.Dispose();
        _navigationRefresh = null;
        _hoverPrefetch.Cancel();
        _selectionDwellPrefetch?.Dispose();
        _selectionDwellPrefetch = null;
        _neighborPrefetch?.Dispose();
        _neighborPrefetch = null;
        CancelPullRequestDiffBuild();
    }

    public void SetSection(PullRequestWorkspaceSection section)
    {
        SelectedSection = section;
    }

    private bool TryApplyNavigationSnapshot(
        string token,
        string userPartition,
        string owner,
        string repositoryName,
        int pullRequestNumber)
    {
        if (!_pullRequestNavigationCache.TryGet(
                userPartition,
                owner,
                repositoryName,
                pullRequestNumber,
                out PullRequestNavigationSnapshot snapshot))
        {
            _ = PullRequestTelemetry.ObservePrefetchAsync(
                _telemetryService,
                PullRequestPrefetchReason.NavigationHandoff,
                () => _pullRequestNavigationCache.PrefetchAsync(
                    token,
                    userPartition,
                    owner,
                    repositoryName,
                    pullRequestNumber,
                    PullRequestPrefetchReason.NavigationHandoff));
            return false;
        }

        _selectedPullRequestIssue = snapshot.Issue;
        if (_loadedPullRequests.All(existing => existing.Number != snapshot.PullRequestNumber))
        {
            _loadedPullRequests.Insert(0, snapshot.PullRequest);
        }

        if (PullRequests.All(existing => existing.Number != snapshot.PullRequestNumber))
        {
            PullRequests.Insert(0, snapshot.PullRequest);
        }

        SetSelectedPullRequest(snapshot.PullRequest);
        PopulatePullRequest(snapshot.PullRequest);
        ConfigurePullRequestComments(snapshot.Comments);
        ReplaceCollectionByKey(
            PullRequestComments,
            snapshot.Comments,
            static comment => comment.Id.ToString(CultureInfo.InvariantCulture));
        ReplaceCollectionByKey(
            PullRequestCommits,
            snapshot.Commits,
            static commit => commit.Sha);
        IReadOnlyList<PullRequestReviewItem> reviewItems = BuildPullRequestReviewItems(snapshot.Reviews, snapshot.ReviewComments);
        ReplaceCollectionByKey(
            PullRequestReviews,
            reviewItems,
            static review => $"{review.ReviewerLogin}:{review.StateText}:{review.SortKey.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}");
        ReplaceCollectionByKey(
            PullRequestTimelineEvents,
            snapshot.TimelineEvents.OrderBy(item => item.CreatedAt),
            static timelineEvent => timelineEvent.Id.ToString(CultureInfo.InvariantCulture));
        IsPullRequestCommentsEmptyVisible = PullRequestComments.Count == 0;
        IsPullRequestCommitsEmptyVisible = PullRequestCommits.Count == 0;
        IsPullRequestReviewsEmptyVisible = PullRequestReviews.Count == 0;
        IsPullRequestTimelineEmptyVisible = PullRequestTimelineEvents.Count == 0;
        _projectedPullRequestDetailNumber = snapshot.PullRequestNumber;
        StatusText = FormatString("RepoPullRequest.CachedStatus", "Showing cached pull request #{0}.", snapshot.PullRequestNumber);
        ScheduleSelectedPullRequestPrefetch(snapshot.PullRequest, PullRequestPrefetchReason.Dwell, TimeSpan.FromSeconds(5));
        return true;
    }

    private async Task TryRecoverPullRequestCapabilitiesAfterForbiddenAsync(
        GitHubPullRequest pullRequest,
        string token)
    {
        if (_navArg is null || !IsSelectedPullRequest(pullRequest))
        {
            return;
        }

        try
        {
            PullRequestCapabilitySnapshot? snapshot =
                await _pullRequestQueryService.RefreshPullRequestCapabilitiesAsync(
                    token,
                    GetActiveUserPartition(token),
                    _navArg.Repo.Owner.Login,
                    _navArg.Repo.Name,
                    pullRequest.Number);
            if (snapshot is null || !IsSelectedPullRequest(pullRequest))
            {
                return;
            }

            _capabilityRepository = snapshot.Repository;
            _selectedPullRequestIssue = snapshot.Issue;
            _capabilityDenials.ConfirmSuccessfulRefresh(snapshot.PullRequest.Number);
            ReplacePullRequestInCollection(snapshot.PullRequest);
            SetSelectedPullRequest(snapshot.PullRequest);
            PopulatePullRequest(snapshot.PullRequest);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
    }

    private void StoreNavigationSnapshot(
        GitHubPullRequest pullRequest,
        GitHubIssue? issue,
        IReadOnlyList<GitHubIssueComment> comments,
        IReadOnlyList<GitHubCommit> commits,
        IReadOnlyList<GitHubPullRequestReview> reviews,
        IReadOnlyList<GitHubPullRequestReviewComment> reviewComments,
        IReadOnlyList<GitHubIssueEvent> timelineEvents,
        string source)
    {
        if (_navArg is null)
        {
            return;
        }

        string accountPartition = GetActiveUserPartition(GetActiveToken() ?? string.Empty);
        _pullRequestNavigationCache.Store(accountPartition, new PullRequestNavigationSnapshot(
            _navArg.Repo.Owner.Login,
            _navArg.Repo.Name,
            pullRequest.Number,
            pullRequest,
            issue,
            [.. comments],
            [.. commits],
            [.. reviews],
            [.. reviewComments],
            [.. timelineEvents],
            DateTimeOffset.UtcNow,
            source));
    }

    private void ScheduleSelectedPullRequestPrefetch(
        GitHubPullRequest pullRequest,
        PullRequestPrefetchReason reason,
        TimeSpan delay)
    {
        if (_navArg is null)
        {
            return;
        }

        if (!TryGetActiveToken(out string token))
        {
            return;
        }

        _selectionDwellPrefetch?.Dispose();
        PullRequestTelemetry.TrackPrefetchStarted(_telemetryService, reason);
        _selectionDwellPrefetch = _pullRequestNavigationCache.SchedulePrefetch(
            token,
            GetActiveUserPartition(token),
            _navArg.Repo.Owner.Login,
            _navArg.Repo.Name,
            pullRequest.Number,
            reason,
            delay,
            (result, duration) => PullRequestTelemetry.TrackPrefetchCompleted(
                _telemetryService,
                reason,
                result,
                duration));
    }

    private void ScheduleNeighborPrefetch(GitHubPullRequest pullRequest)
    {
        if (_navArg is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubPullRequest? neighbor = PullRequests
            .SkipWhile(item => item.Number != pullRequest.Number)
            .Skip(1)
            .FirstOrDefault()
            ?? PullRequests
                .TakeWhile(item => item.Number != pullRequest.Number)
                .LastOrDefault();
        if (neighbor is null)
        {
            return;
        }

        _neighborPrefetch?.Dispose();
        PullRequestTelemetry.TrackPrefetchStarted(_telemetryService, PullRequestPrefetchReason.Neighbor);
        _neighborPrefetch = _pullRequestNavigationCache.SchedulePrefetch(
            token,
            GetActiveUserPartition(token),
            _navArg.Repo.Owner.Login,
            _navArg.Repo.Name,
            neighbor.Number,
            PullRequestPrefetchReason.Neighbor,
            TimeSpan.FromMilliseconds(350),
            (result, duration) => PullRequestTelemetry.TrackPrefetchCompleted(
                _telemetryService,
                PullRequestPrefetchReason.Neighbor,
                result,
                duration));
    }

    private void ApplyIssueInspectorMetadata(GitHubIssue? issue)
    {
        if (issue is null)
        {
            SelectedLabels.Clear();
            SelectedAssignees.Clear();
        }
        else
        {
            ReplaceCollectionByKey(
                SelectedLabels,
                issue.Labels,
                static label => label.Name);
            ReplaceCollectionByKey(
                SelectedAssignees,
                issue.Assignees,
                static assignee => assignee.Login);
        }

        NotifyInspectorPropertiesChanged();
    }

    private void ApplyPullRequestListProjection(
        IEnumerable<GitHubPullRequest> incoming,
        PagedDataCompleteness completeness)
    {
        GitHubPullRequest[] projection = PagedRefreshProjectionPolicy.Merge(
            incoming,
            _loadedPullRequests,
            static pullRequest => pullRequest.Number.ToString(CultureInfo.InvariantCulture),
            completeness);
        _loadedPullRequests.Clear();
        _loadedPullRequests.AddRange(projection);
    }

    private void UpdatePullRequestListScopeNotice() =>
        PullRequestListScopeNotice = PullRequestSectionProjectionPolicy.CreateListScopeNotice(
            _pullRequestListState);

    private void UpdateRepositoryMetadataScopeStatus(
        string previousStatusText,
        params PullRequestSectionState[] states)
    {
        PagedDataCompleteness completeness = CombineCompleteness(states);
        StatusText = completeness switch
        {
            PagedDataCompleteness.ApiLimited => GetString(
                "RepoPullRequest.MetadataApiLimitedStatus",
                "Repository options reached GitHub's API limit. The available options remain usable."),
            PagedDataCompleteness.Partial => GetString(
                "RepoPullRequest.MetadataPartialStatus",
                "Some repository options could not be loaded. Available options remain usable."),
            _ => previousStatusText
        };
    }

    private static PagedDataCompleteness CombineCompleteness(
        IEnumerable<PullRequestSectionState> states)
    {
        PagedDataCompleteness[] values = states.Select(static state => state.Completeness).ToArray();
        if (values.Contains(PagedDataCompleteness.ApiLimited))
        {
            return PagedDataCompleteness.ApiLimited;
        }

        if (values.Contains(PagedDataCompleteness.Partial))
        {
            return PagedDataCompleteness.Partial;
        }

        return values.Contains(PagedDataCompleteness.Loading)
            ? PagedDataCompleteness.Loading
            : PagedDataCompleteness.Complete;
    }

    private static void ReplaceCollectionByKey<T>(
        ObservableCollection<T> collection,
        IEnumerable<T> snapshot,
        Func<T, string> keySelector)
    {
        List<T> items = snapshot.ToList();
        for (int targetIndex = 0; targetIndex < items.Count; targetIndex++)
        {
            T item = items[targetIndex];
            string key = keySelector(item);
            int existingIndex = -1;
            for (int index = targetIndex; index < collection.Count; index++)
            {
                if (string.Equals(keySelector(collection[index]), key, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = index;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                collection.Insert(targetIndex, item);
                continue;
            }

            if (existingIndex != targetIndex)
            {
                collection.Move(existingIndex, targetIndex);
            }

            if (!ReferenceEquals(collection[targetIndex], item))
            {
                collection[targetIndex] = item;
            }
        }

        for (int index = collection.Count - 1; index >= items.Count; index--)
        {
            collection.RemoveAt(index);
        }
    }

    private void NotifySelectedPullRequestPropertiesChanged()
    {
        NotifySelectedPullRequestHeaderPropertiesChanged();
        OnPropertyChanged(nameof(CurrentPullRequestIssue));
        OnPropertyChanged(nameof(SelectedPullRequestMetadataText));
        NotifyInspectorPropertiesChanged();
    }

    private void NotifySelectedPullRequestHeaderPropertiesChanged()
    {
        OnPropertyChanged(nameof(CurrentPullRequest));
        OnPropertyChanged(nameof(HasSelectedPullRequest));
        OnPropertyChanged(nameof(IsDetailPlaceholderVisible));
        OnPropertyChanged(nameof(SelectedPullRequestNumberText));
        OnPropertyChanged(nameof(SelectedPullRequestTitle));
        OnPropertyChanged(nameof(SelectedPullRequestAuthorDisplayName));
        OnPropertyChanged(nameof(SelectedPullRequestAuthorLogin));
        OnPropertyChanged(nameof(SelectedPullRequestAuthorAvatarUrl));
        OnPropertyChanged(nameof(SelectedPullRequestAuthorAutomationId));
        OnPropertyChanged(nameof(SelectedPullRequestStateText));
        OnPropertyChanged(nameof(BranchSummaryText));
        OnPropertyChanged(nameof(SelectedPullRequestCommentText));
        OnPropertyChanged(nameof(SelectedPullRequestReactionTargetId));
        OnPropertyChanged(nameof(SelectedPullRequestHtmlUrl));
        OnPropertyChanged(nameof(PullRequestReactions));
        OnPropertyChanged(nameof(PullRequestBodyMarkdownSource));
        OnPropertyChanged(nameof(PullRequestCommentMarkdownSource));
    }

    private void NotifyRepositoryPropertiesChanged()
    {
        OnPropertyChanged(nameof(RepositoryFullName));
        OnPropertyChanged(nameof(PageTitle));
    }

    private void NotifyInspectorPropertiesChanged()
    {
        OnPropertyChanged(nameof(HasLabels));
        OnPropertyChanged(nameof(HasNoLabels));
        OnPropertyChanged(nameof(HasAssignees));
        OnPropertyChanged(nameof(HasNoAssignees));
        OnPropertyChanged(nameof(HasRequestedReviewers));
        OnPropertyChanged(nameof(HasNoRequestedReviewers));
        OnPropertyChanged(nameof(MilestoneTitle));
    }

    private void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null)
    {
        PullRequestTelemetry.TrackSafely(_telemetryService, name, properties);
    }

    private void TrackPullRequestListOutcome(
        string result,
        TimeSpan duration,
        string? errorKind = null)
    {
        PullRequestTelemetry.TrackListLoaded(
            _telemetryService,
            "repo",
            result,
            duration,
            errorKind: errorKind);
    }

    private bool CompleteSupersededPullRequestListRead(int requestId, TimeSpan duration)
    {
        if (requestId == _listRequestId)
        {
            return false;
        }

        TrackPullRequestListOutcome(TelemetryTaxonomy.Results.Cancelled, duration);
        return true;
    }

    private void TrackPullRequestAction(string action, string result) =>
        PullRequestTelemetry.TrackAction(_telemetryService, action, result);

    public void TrackCommentQuoteReply() =>
        TrackPullRequestAction(TelemetryTaxonomy.Actions.QuoteReply, TelemetryTaxonomy.Results.Success);

    public void TrackCommentCopyLink(bool succeeded) =>
        TrackPullRequestAction(
            TelemetryTaxonomy.Actions.CopyLink,
            succeeded ? TelemetryTaxonomy.Results.Success : TelemetryTaxonomy.Results.Error);

    public void TrackCommentCopyMarkdown(bool succeeded) =>
        TrackPullRequestAction(
            TelemetryTaxonomy.Actions.CopyMarkdown,
            succeeded ? TelemetryTaxonomy.Results.Success : TelemetryTaxonomy.Results.Error);

    public void TrackDiffViewerAction(string action, string result)
    {
        if (action is not (TelemetryTaxonomy.Actions.CopyDiff or TelemetryTaxonomy.Actions.CopyPath) ||
            result is not (TelemetryTaxonomy.Results.Success or TelemetryTaxonomy.Results.Error))
        {
            return;
        }

        TrackPullRequestAction(action, result);
    }

    private string GetActiveUserPartition(string token)
    {
        if (GitHubAuthenticationConstants.IsPublicAccessToken(token))
        {
            return "public";
        }

        long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        return userId > 0 ? userId.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private void ReplacePullRequestInCollection(GitHubPullRequest updatedPullRequest)
    {
        _suppressSelectionChanged = true;
        try
        {
            for (int index = 0; index < _loadedPullRequests.Count; index++)
            {
                if (_loadedPullRequests[index].Number == updatedPullRequest.Number)
                {
                    _loadedPullRequests[index] = updatedPullRequest;
                    break;
                }
            }

            for (int index = 0; index < PullRequests.Count; index++)
            {
                if (PullRequests[index].Number == updatedPullRequest.Number)
                {
                    PullRequests[index] = updatedPullRequest;
                    return;
                }
            }
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
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

    private string GetPullRequestStateDisplay(GitHubPullRequest pullRequest)
    {
        if (pullRequest.Draft)
        {
            return GetString("RepoPullRequest.StateDraft", "draft");
        }

        return string.Equals(pullRequest.State, "closed", StringComparison.OrdinalIgnoreCase)
            ? GetString("RepoPullRequest.StateClosedDisplay", "closed")
            : GetString("RepoPullRequest.StateOpenDisplay", "open");
    }

    private string FormatPullRequestMetadataSummary(GitHubIssue? issue, GitHubPullRequest pullRequest)
    {
        string reviewers = pullRequest.RequestedReviewers.Length == 0
            ? GetString("RepoPullRequest.RequestedReviewersNone", "Requested reviewers: none")
            : FormatString(
                "RepoPullRequest.RequestedReviewersFormat",
                "Requested reviewers: {0}",
                string.Join(", ", pullRequest.RequestedReviewers.Select(reviewer => $"@{reviewer.Login}")));
        string assignees = issue is null
            ? GetString("RepoPullRequest.AssigneesLoading", "Assignees: loading...")
            : issue.Assignees.Length == 0
                ? GetString("RepoIssue.AssigneesNone", "Assignees: none")
                : FormatString(
                    "RepoIssue.AssigneesFormat",
                    "Assignees: {0}",
                    string.Join(", ", issue.Assignees.Select(assignee => $"@{assignee.Login}")));
        string labels = issue is null
            ? GetString("RepoPullRequest.LabelsLoading", "Labels: loading...")
            : issue.Labels.Length == 0
                ? GetString("RepoIssue.LabelsNone", "Labels: none")
                : FormatString(
                    "RepoIssue.LabelsFormat",
                    "Labels: {0}",
                    string.Join(", ", issue.Labels.Select(label => label.Name)));
        string milestone = issue?.Milestone is null
            ? issue is null
                ? GetString("RepoPullRequest.MilestoneLoading", "Milestone: loading...")
                : GetString("RepoIssue.MilestoneNone", "Milestone: none")
            : FormatString("RepoIssue.MilestoneFormat", "Milestone: {0}", issue.Milestone.Title);
        return $"{reviewers}  •  {assignees}  •  {labels}  •  {milestone}";
    }

    private string FormatCommentCount(int count)
    {
        return count == 1
            ? FormatString("RepoPullRequest.CommentCountSingular", "{0} comment", count)
            : FormatString("RepoPullRequest.CommentCountPlural", "{0} comments", count);
    }

    private void ApplyPullRequestCapabilities(GitHubPullRequest pullRequest)
    {
        if (DialogMatrixAutomationScenario.IsEnabled)
        {
            CanEditPullRequest = true;
            CanManagePullRequestMetadata = true;
            IsTogglePullRequestStateEnabled = true;
            _canCommentOnPullRequest = true;
            IsPullRequestCommentEnabled = true;
            CanReactToPullRequest = true;
            CanSubmitReviewComment = true;
            CanApprovePullRequest = true;
            CanRequestPullRequestChanges = true;
            IsMergeEnabled = true;
            CanMergeWithMergeCommit = true;
            CanMergeWithSquash = true;
            CanMergeWithRebase = true;
            ArePullRequestActionsEnabled = true;
            OnPropertyChanged(nameof(CanSubmitPullRequestReview));
            return;
        }

        GitHubRepository? repository = _capabilityRepository ?? _navArg?.Repo;
        if (repository is null)
        {
            ResetPullRequestCapabilities();
            return;
        }

        string? token = GetActiveToken();
        PullRequestCapabilities capabilities = PullRequestPermissionPolicy.Evaluate(
            repository,
            pullRequest,
            _selectedPullRequestIssue,
            AuthenticatedLogin,
            string.IsNullOrWhiteSpace(token) || GitHubAuthenticationConstants.IsPublicAccessToken(token));
        CanEditPullRequest = capabilities.CanEdit && !_capabilityDenials.IsDenied(PullRequestDeniedCapability.Edit);
        CanManagePullRequestMetadata = capabilities.CanManageMetadata && !_capabilityDenials.IsDenied(PullRequestDeniedCapability.Metadata);
        IsTogglePullRequestStateEnabled = capabilities.CanChangeState && !_capabilityDenials.IsDenied(PullRequestDeniedCapability.State);
        _canCommentOnPullRequest = capabilities.CanComment && !_capabilityDenials.IsDenied(PullRequestDeniedCapability.Comment);
        CanReactToPullRequest = capabilities.CanReact && !_capabilityDenials.IsDenied(PullRequestDeniedCapability.Reaction);
        CanSubmitReviewComment = capabilities.CanSubmitReviewComment && !_capabilityDenials.IsDenied(PullRequestDeniedCapability.Review);
        CanApprovePullRequest = capabilities.CanApprove && !_capabilityDenials.IsDenied(PullRequestDeniedCapability.Review);
        CanRequestPullRequestChanges = capabilities.CanRequestChanges && !_capabilityDenials.IsDenied(PullRequestDeniedCapability.Review);
        IsMergeEnabled = capabilities.CanMerge && !_capabilityDenials.IsDenied(PullRequestDeniedCapability.Merge);
        bool mergeDenied = _capabilityDenials.IsDenied(PullRequestDeniedCapability.Merge);
        CanMergeWithMergeCommit = capabilities.CanMergeCommit && !mergeDenied;
        CanMergeWithSquash = capabilities.CanSquashMerge && !mergeDenied;
        CanMergeWithRebase = capabilities.CanRebaseMerge && !mergeDenied;
        ArePullRequestActionsEnabled = CanEditPullRequest || CanManagePullRequestMetadata ||
            IsTogglePullRequestStateEnabled || _canCommentOnPullRequest || CanReactToPullRequest ||
            CanSubmitPullRequestReview || IsMergeEnabled;
        OnPropertyChanged(nameof(CanSubmitPullRequestReview));
        if (!capabilities.CanMerge && !string.IsNullOrWhiteSpace(capabilities.MergeUnavailableReason))
        {
            MergeStatusText = capabilities.MergeUnavailableReason;
        }
    }

    private void ResetPullRequestCapabilities()
    {
        ArePullRequestActionsEnabled = false;
        CanEditPullRequest = false;
        CanManagePullRequestMetadata = false;
        IsTogglePullRequestStateEnabled = false;
        _canCommentOnPullRequest = false;
        IsPullRequestCommentEnabled = false;
        CanReactToPullRequest = false;
        CanSubmitReviewComment = false;
        CanApprovePullRequest = false;
        CanRequestPullRequestChanges = false;
        IsMergeEnabled = false;
        CanMergeWithMergeCommit = false;
        CanMergeWithSquash = false;
        CanMergeWithRebase = false;
        OnPropertyChanged(nameof(CanSubmitPullRequestReview));
    }

    private bool CanSubmitReview(PullRequestReviewDecision decision) => decision switch
    {
        PullRequestReviewDecision.Comment => CanSubmitReviewComment,
        PullRequestReviewDecision.Approve => CanApprovePullRequest,
        PullRequestReviewDecision.RequestChanges => CanRequestPullRequestChanges,
        _ => false
    };

    private bool IsMergeMethodAllowed(string mergeMethod) => mergeMethod.Trim().ToLowerInvariant() switch
    {
        "merge" => CanMergeWithMergeCommit,
        "squash" => CanMergeWithSquash,
        "rebase" => CanMergeWithRebase,
        _ => false
    };

    private void DisableRejectedCapability(
        GitHubApiException exception,
        string capability,
        int pullRequestNumber)
    {
        if (SelectedPullRequest?.Number != pullRequestNumber)
        {
            return;
        }

        PullRequestDeniedCapability deniedCapability = capability switch
        {
            "edit" => PullRequestDeniedCapability.Edit,
            "metadata" => PullRequestDeniedCapability.Metadata,
            "state" => PullRequestDeniedCapability.State,
            "comment" => PullRequestDeniedCapability.Comment,
            "reaction" => PullRequestDeniedCapability.Reaction,
            "merge" => PullRequestDeniedCapability.Merge,
            "review" => PullRequestDeniedCapability.Review,
            _ => PullRequestDeniedCapability.None
        };
        if (deniedCapability == PullRequestDeniedCapability.None ||
            !_capabilityDenials.RecordFailureForCurrent(
                pullRequestNumber,
                deniedCapability,
                exception.StatusCode))
        {
            return;
        }

        switch (capability)
        {
            case "edit":
                CanEditPullRequest = false;
                break;
            case "metadata":
                CanManagePullRequestMetadata = false;
                break;
            case "state":
                IsTogglePullRequestStateEnabled = false;
                break;
            case "comment":
                _canCommentOnPullRequest = false;
                IsPullRequestCommentEnabled = false;
                break;
            case "reaction":
                CanReactToPullRequest = false;
                break;
            case "review":
                CanSubmitReviewComment = false;
                CanApprovePullRequest = false;
                CanRequestPullRequestChanges = false;
                OnPropertyChanged(nameof(CanSubmitPullRequestReview));
                break;
            case "merge":
                IsMergeEnabled = false;
                CanMergeWithMergeCommit = false;
                CanMergeWithSquash = false;
                CanMergeWithRebase = false;
                MergeStatusText = GetString(
                    "RepoPullRequest.MergePermissionChanged",
                    "GitHub no longer allows this account to merge the pull request.");
                break;
        }

        ArePullRequestActionsEnabled = CanEditPullRequest || CanManagePullRequestMetadata ||
            IsTogglePullRequestStateEnabled || _canCommentOnPullRequest || CanReactToPullRequest ||
            CanSubmitPullRequestReview || IsMergeEnabled;

        GitHubPullRequest deniedPullRequest = SelectedPullRequest;
        if (TryGetActiveToken(out string token))
        {
            BackgroundTaskObserver.Run(
                () => TryRecoverPullRequestCapabilitiesAfterForbiddenAsync(deniedPullRequest, token),
                "pull_requests",
                _telemetryService);
        }
    }

    private string GetTogglePullRequestStateButtonText(GitHubPullRequest pullRequest)
    {
        return pullRequest.Merged
            ? GetString("RepoPullRequest.MergedButton", "Pull request merged")
            : string.Equals(pullRequest.State, "closed", StringComparison.OrdinalIgnoreCase)
                ? GetString("RepoPullRequest.ReopenButton", "Reopen pull request")
                : GetString("RepoPullRequest.CloseButton", "Close pull request");
    }

    public sealed record PullRequestCreateDialogData(
        string DefaultHead,
        string DefaultBase,
        IReadOnlyList<GitHubBranch> AvailableBranches,
        PagedDataCompleteness Completeness);

    private sealed record PullRequestDetailSnapshot(
        int PullRequestNumber,
        GitHubIssue? PullRequestIssue,
        string StatusText,
        string TitleText,
        string MetaText,
        string MetadataText,
        string ReactionsText,
        string MergeStatusText,
        string BodyText,
        string CommentDraft,
        string ToggleStateButtonText,
        bool AreActionsEnabled,
        bool IsToggleStateEnabled,
        bool IsMergeEnabled,
        bool IsCommentEnabled,
        bool IsCommentsEmptyVisible,
        bool IsCommitsEmptyVisible,
        bool IsReviewsEmptyVisible,
        bool IsTimelineEmptyVisible,
        IReadOnlyList<GitHubIssueComment> Comments,
        IReadOnlyList<GitHubCommit> Commits,
        IReadOnlyList<PullRequestReviewItem> Reviews,
        IReadOnlyList<GitHubIssueEvent> TimelineEvents,
        CommitDiffDocument DiffDocument);

    public sealed record PullRequestMetadataDialogData(
        IReadOnlyList<GitHubActor> AvailableReviewers,
        IReadOnlyList<GitHubActor> AvailableAssignees,
        IReadOnlyList<GitHubLabel> AvailableLabels,
        IReadOnlyList<GitHubMilestone> AvailableMilestones,
        PagedDataCompleteness Completeness);

    public sealed record PullRequestMetadataUpdate(
        IReadOnlyList<string> Reviewers,
        IReadOnlyList<string> Assignees,
        IReadOnlyList<string> Labels,
        int? MilestoneNumber);

    private enum ReactionDialogTargetKind
    {
        Issue,
        Comment,
        ReviewComment
    }

    [WinRT.GeneratedBindableCustomProperty]
    public sealed partial class PullRequestReviewItem : IPullRequestReviewItem
    {
        public PullRequestReviewItem(
            GitHubPullRequestReview review,
            string stateText,
            string pendingText,
            string unknownUserText,
            string openButtonText,
            string deterministicContext)
        {
            PullRequestIdentityPresentation identity = PullRequestIdentityProjection.Create(
                review,
                unknownUserText,
                "PullRequestReview",
                deterministicContext);
            AutomationId = identity.AutomationInstanceId;
            ReviewerLogin = identity.DisplayName;
            ReviewerProfileLogin = identity.ProfileLogin;
            ReviewerAvatarUrl = identity.AvatarUrl;
            StateText = stateText;
            SubmittedAtText = review.SubmittedAt?.LocalDateTime.ToString("g") ?? pendingText;
            BodyText = review.Body ?? string.Empty;
            HtmlUrl = review.HtmlUrl;
            MarkdownSource = review.MarkdownSource;
            OpenButtonText = openButtonText;
            SortKey = review.SubmittedAt ?? DateTimeOffset.MinValue;
        }

        public string ReviewerLogin { get; }

        public string? ReviewerProfileLogin { get; }

        public string ReviewerAvatarUrl { get; }

        public string AutomationId { get; }

        public string StateText { get; }

        public string SubmittedAtText { get; }

        public string BodyText { get; }

        public string HtmlUrl { get; }

        public MarkdownDocumentSource? MarkdownSource { get; }

        public string OpenButtonText { get; }

        public DateTimeOffset SortKey { get; }

        public ObservableCollection<PullRequestReviewThreadItem> Threads { get; } = [];

        System.Collections.IEnumerable IPullRequestReviewItem.Threads => Threads;

        public static PullRequestReviewItem CreateSynthetic(
            GitHubPullRequestReviewComment comment,
            string stateText,
            string unknownUserText,
            string openButtonText,
            string deterministicContext)
        {
            return new PullRequestReviewItem(
                comment,
                stateText,
                unknownUserText,
                openButtonText,
                deterministicContext);
        }

        private PullRequestReviewItem(
            GitHubPullRequestReviewComment comment,
            string stateText,
            string unknownUserText,
            string openButtonText,
            string deterministicContext)
        {
            PullRequestIdentityPresentation identity = PullRequestIdentityProjection.Create(
                comment,
                unknownUserText,
                "PullRequestReviewComment",
                deterministicContext);
            AutomationId = identity.AutomationInstanceId;
            ReviewerLogin = identity.DisplayName;
            ReviewerProfileLogin = identity.ProfileLogin;
            ReviewerAvatarUrl = identity.AvatarUrl;
            StateText = stateText;
            SubmittedAtText = comment.CreatedAt.LocalDateTime.ToString("g");
            BodyText = string.Empty;
            HtmlUrl = comment.HtmlUrl;
            MarkdownSource = comment.MarkdownSource;
            OpenButtonText = openButtonText;
            SortKey = comment.CreatedAt;
        }
    }

    [WinRT.GeneratedBindableCustomProperty]
    public sealed partial class PullRequestReviewThreadItem : ObservableObject, IPullRequestReviewThreadItem
    {
        public PullRequestReviewThreadItem(
            GitHubPullRequestReviewComment comment,
            string changedFileText,
            string reactionText,
            string unknownUserText,
            string openButtonText,
            string reactionsButtonText,
            string replyPlaceholderText,
            string replyButtonText,
            string replyPrefixText,
            string deterministicContext)
        {
            CommentId = comment.Id;
            CommentNodeId = comment.NodeId ?? string.Empty;
            PullRequestIdentityPresentation identity = PullRequestIdentityProjection.Create(
                comment,
                unknownUserText,
                "PullRequestReviewThread",
                deterministicContext);
            AutomationId = identity.AutomationInstanceId;
            CommentUserLogin = identity.DisplayName;
            CommentAuthorLogin = comment.User?.Login ?? string.Empty;
            CommentUserProfileLogin = identity.ProfileLogin;
            CommentUserAvatarUrl = identity.AvatarUrl;
            CommentBody = comment.Body;
            CommentHtmlUrl = comment.HtmlUrl;
            Reactions = comment.Reactions;
            IsMinimized = comment.IsMinimized;
            MarkdownSource = comment.MarkdownSource;
            PathDisplayText = string.IsNullOrWhiteSpace(comment.Path) ? changedFileText : comment.Path;
            CreatedAtText = comment.CreatedAt.LocalDateTime.ToString("g");
            DiffHunkText = TrimDiffHunk(comment.DiffHunk);
            ReactionText = reactionText;
            OpenButtonText = openButtonText;
            ReactionsButtonText = reactionsButtonText;
            ReplyPlaceholderText = replyPlaceholderText;
            ReplyButtonText = replyButtonText;
            ReplyPrefixText = replyPrefixText;
        }

        public long CommentId { get; }

        public string CommentNodeId { get; }

        public string AutomationId { get; }

        public string ReplyAutomationId => $"{AutomationId}_Reply";

        public string ReplyFormAutomationId => $"{AutomationId}_ReplyForm";

        public string CommentUserLogin { get; }

        public string CommentAuthorLogin { get; }

        public string? CommentUserProfileLogin { get; }

        public string CommentUserAvatarUrl { get; }

        public string CommentBody { get; }

        public string CommentHtmlUrl { get; }

        public GitHubReactionSummary Reactions { get; }

        public bool IsMinimized { get; }

        public string ViewerLogin { get; set; } = string.Empty;

        public bool CanReact { get; set; }

        public bool CanReply { get; set; }

        public bool CanModerate { get; set; }

        public MarkdownDocumentSource? MarkdownSource { get; }

        public MarkdownDocumentSource? ReplyMarkdownSource => MarkdownSource is null
            ? null
            : MarkdownSource with { DocumentId = $"pull-request-review-reply-draft:{AutomationId}" };

        public string PathDisplayText { get; }

        public string CreatedAtText { get; }

        public string DiffHunkText { get; }

        public string ReactionText { get; }

        public string OpenButtonText { get; }

        public string ReactionsButtonText { get; }

        public string ReplyPlaceholderText { get; }

        public string ReplyButtonText { get; }

        public string ReplyPrefixText { get; }

        [ObservableProperty]
        public partial string ReplyText { get; set; } = string.Empty;

        [ObservableProperty]
        public partial bool IsReplyInProgress { get; set; }

        public bool IsReplyEnabled => !IsReplyInProgress;

        public ObservableCollection<PullRequestReviewReplyItem> Replies { get; } = [];

        System.Collections.IEnumerable IPullRequestReviewThreadItem.Replies => Replies;

        public void AddReply(
            GitHubPullRequestReviewComment reply,
            string reactionText,
            string unknownUserText,
            string replyPrefixText,
            string openButtonText,
            string reactionsButtonText)
        {
            Replies.Add(new PullRequestReviewReplyItem(
                reply,
                reactionText,
                unknownUserText,
                replyPrefixText,
                openButtonText,
                reactionsButtonText,
                $"{AutomationId}:reply:{Replies.Count}",
                ViewerLogin,
                CanReact,
                CanReply,
                CanModerate));
        }

        partial void OnIsReplyInProgressChanged(bool value)
        {
            OnPropertyChanged(nameof(IsReplyEnabled));
        }
    }

    [WinRT.GeneratedBindableCustomProperty]
    public sealed partial class PullRequestReviewReplyItem
    {
        public PullRequestReviewReplyItem(
            GitHubPullRequestReviewComment comment,
            string reactionText,
            string unknownUserText,
            string replyPrefixText,
            string openButtonText,
            string reactionsButtonText,
            string deterministicContext,
            string viewerLogin,
            bool canReact,
            bool canReply,
            bool canModerate)
        {
            Id = comment.Id;
            NodeId = comment.NodeId ?? string.Empty;
            PullRequestIdentityPresentation identity = PullRequestIdentityProjection.Create(
                comment,
                unknownUserText,
                "PullRequestReviewReply",
                deterministicContext);
            AutomationId = identity.AutomationInstanceId;
            UserLogin = identity.DisplayName;
            AuthorLogin = comment.User?.Login ?? string.Empty;
            UserProfileLogin = identity.ProfileLogin;
            UserAvatarUrl = identity.AvatarUrl;
            CreatedAtText = comment.CreatedAt.LocalDateTime.ToString("g");
            HtmlUrl = comment.HtmlUrl;
            Body = comment.Body;
            MarkdownSource = comment.MarkdownSource;
            ReactionText = reactionText;
            ReplyPrefixText = replyPrefixText;
            OpenButtonText = openButtonText;
            ReactionsButtonText = reactionsButtonText;
            Reactions = comment.Reactions;
            IsMinimized = comment.IsMinimized;
            ViewerLogin = viewerLogin;
            CanReact = canReact;
            CanReply = canReply;
            CanModerate = canModerate;
        }

        public long Id { get; }

        public string NodeId { get; }

        public string AutomationId { get; }

        public string UserLogin { get; }

        public string AuthorLogin { get; }

        public string? UserProfileLogin { get; }

        public string UserAvatarUrl { get; }

        public string CreatedAtText { get; }

        public string HtmlUrl { get; }

        public string Body { get; }

        public MarkdownDocumentSource? MarkdownSource { get; }

        public string ReactionText { get; }

        public string ReplyPrefixText { get; }

        public string OpenButtonText { get; }

        public string ReactionsButtonText { get; }

        public GitHubReactionSummary Reactions { get; }

        public bool IsMinimized { get; }

        public string ViewerLogin { get; }

        public bool CanReact { get; }

        public bool CanReply { get; }

        public bool CanModerate { get; }
    }
}
