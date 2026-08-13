using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.Services.Markdown;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Common;
using MarkdownRenderer.Images;

namespace JitHub.WinUI.ViewModels.Pages;

public sealed partial class RepoIssuePageViewModel : ViewModelBase
{
    private static readonly TimeSpan SelectionLoadDebounce = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan HoverPrefetchDebounce = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan CachedNavigationCommentQuietPeriod = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan CachedNavigationRefreshQuietPeriod = TimeSpan.FromSeconds(1);
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;
    private readonly IGitHubIssueQueryService _issueQueryService;
    private readonly IGitHubRepositoryQueryService _repositoryQueryService;
    private readonly IIssueNavigationCache _issueNavigationCache;
    private readonly ITelemetryService _telemetryService;
    private IssueNavArg? _navArg;
    private int _detailRequestId;
    private int _listRequestId;
    private int _navigationInitializationVersion;
    private string? _loadedIssueQueryIdentity;
    private bool _suppressSelectionChanged;
    private CancellationTokenSource? _listLoadCancellationTokenSource;
    private CancellationTokenSource? _detailLoadCancellationTokenSource;
    private CancellationTokenSource? _selectionLoadCancellationTokenSource;
    private IDisposable? _selectionDwellPrefetch;
    private IDisposable? _neighborPrefetch;
    private readonly LatestWinsPrefetchScheduler _hoverPrefetch = new();
    private readonly List<GitHubIssue> _loadedIssues = [];
    private readonly GitHubIssueQueryOptions _issueQuery = new();
    private int _pinnedIssueNumber;
    private int _lastFocusedIssueNumber;
    private IssueDetailSnapshot? _pendingIssueSelectionState;
    private bool _isIssueCommentSubmissionInProgress;
    private bool _isIssueBodyDeferred;
    private bool _isNavigationPreview;
    private readonly IssueCapabilityDenialState _capabilityDenials = new();
    private readonly IssueCapabilityRecoveryCoordinator _capabilityRecovery;
    private IssueSectionState _issueListState = new(CacheState.Miss, Completeness: PagedDataCompleteness.Loading);

    public RepoIssuePageViewModel()
    {
        _capabilityRecovery = new IssueCapabilityRecoveryCoordinator(_capabilityDenials);
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _gitHubClientService = GetService<IGitHubClientService>();
        _issueQueryService = GetService<IGitHubIssueQueryService>();
        _repositoryQueryService = GetService<IGitHubRepositoryQueryService>();
        _issueNavigationCache = GetService<IIssueNavigationCache>();
        _telemetryService = SafeTelemetryService.Wrap(GetService<ITelemetryService>());

        StateOptions =
        [
            new QueryOption("open", GetString("RepoIssue.StateOpen", "Open")),
            new QueryOption("closed", GetString("RepoIssue.StateClosed", "Closed")),
            new QueryOption("all", GetString("RepoIssue.StateAll", "All"))
        ];
        ScopeOptions =
        [
            new QueryOption("all", GetString("RepoIssue.ScopeAll", "All")),
            new QueryOption("assigned", GetString("RepoIssue.ScopeAssigned", "Mine")),
            new QueryOption("created", GetString("RepoIssue.ScopeCreated", "Created")),
            new QueryOption("mentioned", GetString("RepoIssue.ScopeMentioned", "Mentioned"))
        ];
        SortOptions =
        [
            new QueryOption("updated", GetString("RepoIssue.SortUpdated", "Updated")),
            new QueryOption("created", GetString("RepoIssue.SortCreated", "Created")),
            new QueryOption("comments", GetString("RepoIssue.SortComments", "Comments"))
        ];
        DirectionOptions =
        [
            new QueryOption("desc", GetString("RepoIssue.DirectionNewestFirst", "Newest first")),
            new QueryOption("asc", GetString("RepoIssue.DirectionOldestFirst", "Oldest first"))
        ];

        ResetFilters();
        ResetIssueDetails();
        StatusText = LoadingStatusText;
    }

    public ObservableCollection<GitHubIssue> Issues { get; } = [];

    public ObservableCollection<GitHubIssueComment> IssueComments { get; } = [];

    public ObservableCollection<GitHubLabel> SelectedLabels { get; } = [];

    public ObservableCollection<GitHubActor> SelectedAssignees { get; } = [];

    public List<QueryOption> StateOptions { get; }

    public List<QueryOption> ScopeOptions { get; }

    public List<QueryOption> SortOptions { get; }

    public List<QueryOption> DirectionOptions { get; }

    public string AuthenticatedLogin => _authService.AuthenticatedUser?.Login ?? string.Empty;

    public string NewIssueButtonText => GetString("newIssue.Text", "New issue");

    public string ReloadButtonText => GetString("Common.ReloadButton", "Reload");

    public string SearchPlaceholderText => GetString("RepoIssue.SearchPlaceholder", "Search issues");

    public string UpdatedSincePlaceholderText => GetString("RepoIssue.UpdatedSincePlaceholder", "Updated since");

    public string ApplyFiltersButtonText => GetString("RepoIssue.ApplyFiltersButton", "Apply filters");

    public string ClearFiltersButtonText => GetString("RepoIssue.ClearFiltersButton", "Clear");

    public string OpenOnGitHubButtonText => GetString("RepoIssue.OpenOnGitHubButton", "Open on GitHub");

    public string EditButtonText => GetString("Common.EditButton", "Edit");

    public string MetadataButtonText => GetString("RepoIssue.MetadataButton", "Metadata");

    public string ReactionsButtonText => GetString("Common.ReactionsButton", "Reactions");

    public string ConversationTitleText => GetString("RepoIssue.ConversationTitle", "Conversation");

    public string NoCommentsText => GetString("RepoIssue.NoComments", "No comments yet.");

    public string CommentPlaceholderText => GetString("RepoIssue.CommentPlaceholder", "Leave a comment");

    public string CommentButtonText => GetString("Common.CommentButton", "Comment");

    public string NewIssueDialogTitle => GetString("RepoIssue.NewIssueDialogTitle", "New issue");

    public string CreateButtonText => GetString("Common.CreateButton", "Create");

    public string SaveButtonText => GetString("Common.SaveButton", "Save");

    public string CancelButtonText => GetString("Common.CancelButton", "Cancel");

    public string TitleHeaderText => GetString("Common.TitleHeader", "Title");

    public string DescriptionHeaderText => GetString("Common.DescriptionHeader", "Description");

    public string MilestoneHeaderText => GetString("RepoIssue.MilestoneHeader", "Milestone");

    public string AssigneesSectionTitle => GetString("RepoIssue.AssigneesSectionTitle", "Assignees");

    public string LabelsSectionTitle => GetString("RepoIssue.LabelsSectionTitle", "Labels");

    public string NoAssignableUsersText => GetString("RepoIssue.NoAssignableUsers", "No assignable users are available.");

    public string NoLabelsAvailableText => GetString("RepoIssue.NoLabelsAvailable", "No labels are available for this repository.");

    public string NoMilestoneText => GetString("RepoIssue.NoMilestone", "No milestone");

    public string ReactionDialogSaveButtonText => GetString("Common.SaveButton", "Save");

    public string ReactionDialogTitleText => SelectedIssue is null
        ? GetString("RepoIssue.ReactionsDialogTitle", "Reactions for issue")
        : FormatString("RepoIssue.ReactionsDialogTitleFormat", "Reactions for issue #{0}", SelectedIssue.Number);

    public string CommentReactionDialogTitleText => GetString("RepoIssue.CommentReactionsDialogTitle", "Reactions for comment");

    public string EmptyTitleValidationText => GetString("RepoIssue.EmptyTitleValidation", "Issue title cannot be empty.");

    public IssueNavArg? NavigationArgs => _navArg;

    public GitHubIssue? CurrentIssue => SelectedIssue;

    public string LoadingStatusText => GetString("RepoIssue.LoadingStatus", "Loading issues...");

    public string RepositoryFullName => _navArg is null
        ? string.Empty
        : string.IsNullOrWhiteSpace(_navArg.Repo.FullName)
            ? $"{_navArg.Repo.Owner.Login}/{_navArg.Repo.Name}"
            : _navArg.Repo.FullName;

    public string PageTitle => string.IsNullOrWhiteSpace(RepositoryFullName) ? "Issues" : $"{RepositoryFullName} issues";

    public bool HasSelectedIssue => SelectedIssue is not null;

    public bool IsDetailPlaceholderVisible => SelectedIssue is null;

    public bool IsIssueContentVisible => SelectedIssue is not null && !_isNavigationPreview;

    public string SelectedIssueNumberText => SelectedIssue is null
        ? string.Empty
        : $"#{SelectedIssue.Number.ToString(CultureInfo.InvariantCulture)}";

    public string SelectedIssueTitle => SelectedIssue?.Title ?? string.Empty;

    public string SelectedIssueAuthorDisplayName => string.IsNullOrWhiteSpace(SelectedIssue?.User?.Login)
        ? GetString("Common.UnknownUser", "unknown")
        : SelectedIssue.User.Login;

    public string? SelectedIssueAuthorLogin =>
        UserIdentityNavigationPolicy.GetRoutableLogin(SelectedIssue?.User?.Login);

    public string SelectedIssueAuthorAvatarUrl => SelectedIssue?.User?.AvatarUrl ?? string.Empty;

    public string SelectedIssueAuthorAutomationId => SelectedIssue?.AutomationId ?? "RepoIssueSelected_none";

    public string SelectedIssueStateText => SelectedIssue is null || _isNavigationPreview
        ? string.Empty
        : GetIssueStateDisplay(SelectedIssue.State);

    public string SelectedIssueMetadataText => SelectedIssue is null || _isNavigationPreview
        ? string.Empty
        : $"opened {MeWorkItemViewItem.FormatTimeAgo(SelectedIssue.CreatedAt)} · updated {MeWorkItemViewItem.FormatTimeAgo(SelectedIssue.UpdatedAt)}";

    public string SelectedIssueCommentText => SelectedIssue is null ? string.Empty : FormatCommentCount(SelectedIssue.Comments);

    public MarkdownDocumentSource? IssueBodyMarkdownSource => _isIssueBodyDeferred
        ? null
        : SelectedIssue?.MarkdownSource;

    public MarkdownDocumentSource? IssueCommentMarkdownSource => _navArg is null || SelectedIssue is null
        ? null
        : MarkdownDocumentSourceFactory.CreateRepositoryDocument(
            "issue-comment-draft",
            SelectedIssue.Id.ToString(CultureInfo.InvariantCulture),
            _navArg.Repo.Owner.Login,
            _navArg.Repo.Name,
            _navArg.Repo.DefaultBranch);

    public string MilestoneTitle => SelectedIssue?.Milestone?.Title ?? GetString("RepoIssue.MilestoneNoneShort", "No milestone");

    public bool HasLabels => SelectedLabels.Count > 0;

    public bool HasNoLabels => SelectedLabels.Count == 0;

    public bool HasAssignees => SelectedAssignees.Count > 0;

    public bool HasNoAssignees => SelectedAssignees.Count == 0;

    public string FormatEditIssueDialogTitle(int issueNumber)
    {
        return FormatString("RepoIssue.EditIssueDialogTitleFormat", "Edit issue #{0}", issueNumber);
    }

    public string FormatMetadataDialogTitle(int issueNumber)
    {
        return FormatString("RepoIssue.MetadataDialogTitleFormat", "Metadata for issue #{0}", issueNumber);
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IssueListScopeNotice { get; set; } = string.Empty;

    public bool HasIssueListScopeNotice => !string.IsNullOrWhiteSpace(IssueListScopeNotice);

    [ObservableProperty]
    public partial QueryOption? SelectedStateOption { get; set; }

    [ObservableProperty]
    public partial QueryOption? SelectedScopeOption { get; set; }

    [ObservableProperty]
    public partial QueryOption? SelectedSortOption { get; set; }

    [ObservableProperty]
    public partial QueryOption? SelectedDirectionOption { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? SelectedSinceDate { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial GitHubIssue? SelectedIssue { get; set; }

    [ObservableProperty]
    public partial string IssueTitleText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IssueMetaText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IssueMetadataText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IssueReactionsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IssueBodyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string IssueCommentDraft { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ToggleIssueStateButtonText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool AreIssueActionsEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsAddCommentEnabled { get; set; }

    [ObservableProperty]
    public partial bool CanCreateIssue { get; set; }

    [ObservableProperty]
    public partial bool CanEditIssue { get; set; }

    [ObservableProperty]
    public partial bool CanManageIssueMetadata { get; set; }

    [ObservableProperty]
    public partial bool CanChangeIssueState { get; set; }

    [ObservableProperty]
    public partial bool CanReactToIssue { get; set; }

    [ObservableProperty]
    public partial bool IsIssueCommentsEmptyVisible { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial bool IsDetailLoading { get; set; }

    public Task InitializeAsync(IssueNavArg? navArg) =>
        InitializeCoreAsync(navArg, returnAfterCachedDetail: false);

    public Task InitializeForNavigationAsync(IssueNavArg? navArg) =>
        InitializeCoreAsync(navArg, returnAfterCachedDetail: true);

    private async Task InitializeCoreAsync(IssueNavArg? navArg, bool returnAfterCachedDetail)
    {
        int initializationVersion = ++_navigationInitializationVersion;
        CancelActiveListLoad();
        CancelActiveDetailLoad();
        CancelPredictivePrefetches();
        _navArg = navArg;
        _capabilityDenials.TrackTarget(GetRepositoryIdentity(navArg?.Repo), 0);
        ApplyViewerCapabilities(null);
        _lastFocusedIssueNumber = 0;
        _pendingIssueSelectionState = null;
        _selectionDwellPrefetch?.Dispose();
        _selectionDwellPrefetch = null;
        _neighborPrefetch?.Dispose();
        _neighborPrefetch = null;
        _loadedIssues.Clear();
        Issues.Clear();
        IsEmpty = true;
        SetSelectedIssue(null);
        ResetIssueDetails();
        NotifyRepositoryPropertiesChanged();

        if (navArg is null)
        {
            _listRequestId++;
            _pinnedIssueNumber = 0;
            StatusText = GetString(
                "RepoIssue.InvalidNavigation",
                "JitHub could not determine which repository issues to load.");
            ResetIssueDetails();
            return;
        }

        IssueTelemetry.TrackOpened(_telemetryService);
        ResetFilters();
        string navigationPartition = GetActiveUserPartition(GetActiveToken() ?? string.Empty);
        if (returnAfterCachedDetail &&
            navArg.IsNotificationHandoff &&
            navArg.NavigationPreview is GitHubIssue notificationPreview)
        {
            IssueNavigationSnapshot previewSnapshot = new(
                navArg.Repo.Owner.Login,
                navArg.Repo.Name,
                navArg.IssueId,
                notificationPreview,
                [],
                DateTimeOffset.UtcNow,
                "notification-preview");
            ApplyNavigationSnapshot(
                navArg,
                previewSnapshot,
                "Opening issue...",
                deferComments: true);
            _ = CompleteCachedNavigationInitializationAsync(
                navArg,
                previewSnapshot,
                initializationVersion);
            return;
        }

        bool hasNavigationSnapshot = TryApplyNavigationSnapshot(
            navigationPartition,
            navArg,
            navArg.IssueId,
            "Showing cached issue while refreshing.",
            deferComments: returnAfterCachedDetail,
            out IssueNavigationSnapshot? navigationSnapshot);
        if (hasNavigationSnapshot && returnAfterCachedDetail && navigationSnapshot is not null)
        {
            _ = CompleteCachedNavigationInitializationAsync(
                navArg,
                navigationSnapshot,
                initializationVersion);
            return;
        }

        await LoadIssuesAsync(navArg.IssueId, preserveCurrentDetailDuringLoad: hasNavigationSnapshot);
        if (TryGetActiveToken(out string token) &&
            !GitHubAuthenticationConstants.IsPublicAccessToken(token))
        {
            await RefreshRepositoryCapabilitiesAsync(token, QueryFetchPolicy.StaleFirst);
        }
    }

    public async Task ReloadAsync()
    {
        if (Issues.Count == 0 && _lastFocusedIssueNumber > 0)
        {
            await LoadIssuesAsync(_lastFocusedIssueNumber, preservePreferredIssueOutsideQuery: false);
            return;
        }

        int preferredIssueNumber = SelectedIssue?.Number ?? _pinnedIssueNumber;
        await LoadIssuesAsync(preferredIssueNumber);

        GitHubIssue? selectedIssue = SelectedIssue;
        if (selectedIssue is not null && TryGetActiveToken(out string token))
        {
            await RefreshIssueSelectionAsync(selectedIssue, token);
        }

        if (TryGetActiveToken(out token) &&
            !GitHubAuthenticationConstants.IsPublicAccessToken(token))
        {
            if (_capabilityDenials.IsDenied(IssueDeniedCapability.Create))
            {
                await RefreshAuthoritativeRepositoryCapabilitiesAsync(token);
            }
            else
            {
                await RefreshRepositoryCapabilitiesAsync(token, QueryFetchPolicy.NetworkOnly);
            }
        }
    }

    public async Task ApplyFiltersAsync()
    {
        ApplyIssueQueryFromFilters();
        await LoadIssuesAsync(SelectedIssue?.Number ?? _lastFocusedIssueNumber, preservePreferredIssueOutsideQuery: false);
    }

    public async Task ClearFiltersAsync()
    {
        ResetFilters();
        ApplyIssueQueryFromFilters();
        await LoadIssuesAsync(SelectedIssue?.Number
            ?? (_lastFocusedIssueNumber > 0 ? _lastFocusedIssueNumber : _navArg?.IssueId ?? 0));
    }

    public async Task CreateIssueAsync(string title, string? body)
    {
        LastDialogMutationSucceeded = false;
        if (_navArg is null || !CanCreateIssue || !TryGetActiveToken(out string token))
        {
            return;
        }

        IssueCapabilityTarget capabilityTarget = _capabilityDenials.CaptureTarget();

        try
        {
            StatusText = GetString("RepoIssue.CreatingStatus", "Creating issue...");
            GitHubIssue issue = await _gitHubClientService.CreateIssueAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                title,
                body);
            LastDialogMutationSucceeded = true;
            TrackIssueAction(IssueActionKind.Create, IssueActionOutcome.Success);
            await _issueQueryService.InvalidateRepositoryIssuesAsync(
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name);
            StatusText = FormatString("RepoIssue.CreatedStatus", "Created issue #{0}.", issue.Number);
            await LoadIssuesAsync(issue.Number);
        }
        catch (GitHubAuthenticationException)
        {
            TrackIssueAction(IssueActionKind.Create, IssueActionOutcome.AuthenticationError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackIssueAction(IssueActionKind.Create, GetIssueActionOutcome(ex));
            ApplyRepositoryPermissionFailure(capabilityTarget, IssueDeniedCapability.Create, ex);
            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "issues");
        }
        catch (HttpRequestException)
        {
            TrackIssueAction(IssueActionKind.Create, IssueActionOutcome.NetworkError);
            StatusText = GetString("RepoIssue.CreateNetworkError", "JitHub could not reach GitHub to create this issue.");
        }
    }

    public async Task UpdateSelectedIssueAsync(string title, string? body)
    {
        LastDialogMutationSucceeded = false;
        if (_navArg is null || SelectedIssue is null || !CanEditIssue || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubIssue currentIssue = SelectedIssue;
        IssueCapabilityTarget capabilityTarget = _capabilityDenials.CaptureTarget();

        try
        {
            StatusText = FormatString("RepoIssue.UpdatingStatus", "Updating issue #{0}...", currentIssue.Number);
            GitHubIssue updatedIssue = await _gitHubClientService.UpdateIssueAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentIssue.Number,
                title,
                body);
            LastDialogMutationSucceeded = true;
            TrackIssueAction(IssueActionKind.Edit, IssueActionOutcome.Success);
            await _issueQueryService.InvalidateIssueAsync(
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentIssue.Number);
            await TryRefreshIssueSelectionAfterMutationAsync(
                updatedIssue,
                token,
                GetString("RepoIssue.UpdateRefreshError", "Issue updated, but JitHub could not refresh issue details."));
        }
        catch (GitHubAuthenticationException)
        {
            TrackIssueAction(IssueActionKind.Edit, IssueActionOutcome.AuthenticationError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackIssueAction(IssueActionKind.Edit, GetIssueActionOutcome(ex));
            ApplyPermissionFailure(capabilityTarget, IssueDeniedCapability.Edit, ex);
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "issues");
        }
        catch (HttpRequestException)
        {
            TrackIssueAction(IssueActionKind.Edit, IssueActionOutcome.NetworkError);
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = GetString("RepoIssue.UpdateNetworkError", "JitHub could not reach GitHub to update this issue.");
        }
    }

    public async Task<IssueMetadataDialogData?> LoadSelectedIssueMetadataDialogDataAsync()
    {
        if (DialogMatrixAutomationScenario.IsEnabled && SelectedIssue is not null)
        {
            return new IssueMetadataDialogData(
                SelectedAssignees.ToArray(),
                SelectedLabels.ToArray(),
                []);
        }

        if (_navArg is null || SelectedIssue is null || !CanManageIssueMetadata || !TryGetActiveToken(out string token))
        {
            return null;
        }

        int requestId = _detailRequestId;
        int issueNumber = SelectedIssue.Number;
        IssueCapabilityTarget capabilityTarget = _capabilityDenials.CaptureTarget();
        string previousStatusText = StatusText;
        try
        {
            StatusText = FormatString("RepoIssue.LoadMetadataStatus", "Loading issue #{0} metadata...", issueNumber);
            IssueRepositoryMetadata metadata = await _issueQueryService.GetRepositoryMetadataAsync(
                token,
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name);

            if (requestId != _detailRequestId || SelectedIssue?.Number != issueNumber)
            {
                return null;
            }

            bool metadataIsPartial = metadata.AssigneesState.Completeness != PagedDataCompleteness.Complete ||
                metadata.LabelsState.Completeness != PagedDataCompleteness.Complete ||
                metadata.MilestonesState.Completeness != PagedDataCompleteness.Complete;
            StatusText = metadataIsPartial
                ? GetString("RepoIssue.MetadataPartialStatus", "Some issue metadata could not be loaded. Available choices are shown.")
                : previousStatusText;

            return new IssueMetadataDialogData(
                metadata.Assignees,
                metadata.Labels,
                metadata.Milestones);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            ApplyPermissionFailure(capabilityTarget, IssueDeniedCapability.Metadata, ex);
            if (requestId == _detailRequestId && SelectedIssue?.Number == issueNumber)
            {
                StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "issues");
            }
        }
        catch (HttpRequestException)
        {
            if (requestId == _detailRequestId && SelectedIssue?.Number == issueNumber)
            {
                StatusText = GetString("RepoIssue.MetadataLoadNetworkError", "JitHub could not reach GitHub to load issue metadata.");
            }
        }

        return null;
    }

    public async Task UpdateSelectedIssueMetadataAsync(IssueMetadataUpdate update)
    {
        LastDialogMutationSucceeded = false;
        if (_navArg is null || SelectedIssue is null || !CanManageIssueMetadata || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubIssue currentIssue = SelectedIssue;
        IssueCapabilityTarget capabilityTarget = _capabilityDenials.CaptureTarget();

        try
        {
            StatusText = FormatString("RepoIssue.UpdateMetadataStatus", "Updating issue #{0} metadata...", currentIssue.Number);
            GitHubIssue updatedIssue = await _gitHubClientService.UpdateIssueMetadataAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentIssue.Number,
                update.Assignees,
                update.Labels,
                update.MilestoneNumber);
            LastDialogMutationSucceeded = true;
            TrackIssueAction(IssueActionKind.Metadata, IssueActionOutcome.Success);
            await _issueQueryService.InvalidateIssueAsync(
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentIssue.Number);
            if (IsSelectedIssue(currentIssue))
            {
                StatusText = FormatString("RepoIssue.UpdatedMetadataStatus", "Updated issue #{0} metadata.", updatedIssue.Number);
            }

            await TryRefreshIssueSelectionAfterMutationAsync(
                updatedIssue,
                token,
                GetString("RepoIssue.MetadataRefreshError", "Issue metadata updated, but JitHub could not refresh issue details."));
        }
        catch (GitHubAuthenticationException)
        {
            TrackIssueAction(IssueActionKind.Metadata, IssueActionOutcome.AuthenticationError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackIssueAction(IssueActionKind.Metadata, GetIssueActionOutcome(ex));
            ApplyPermissionFailure(capabilityTarget, IssueDeniedCapability.Metadata, ex);
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "issues");
        }
        catch (HttpRequestException)
        {
            TrackIssueAction(IssueActionKind.Metadata, IssueActionOutcome.NetworkError);
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = GetString("RepoIssue.MetadataNetworkError", "JitHub could not reach GitHub to update this issue metadata.");
        }
    }

    public async Task ToggleSelectedIssueStateAsync()
    {
        if (_navArg is null || SelectedIssue is null || !CanChangeIssueState || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubIssue currentIssue = SelectedIssue;
        IssueCapabilityTarget capabilityTarget = _capabilityDenials.CaptureTarget();

        string nextState = string.Equals(currentIssue.State, "closed", StringComparison.OrdinalIgnoreCase)
            ? "open"
            : "closed";

        try
        {
            StatusText = nextState == "closed"
                ? FormatString("RepoIssue.ClosingStatus", "Closing issue #{0}...", currentIssue.Number)
                : FormatString("RepoIssue.ReopeningStatus", "Reopening issue #{0}...", currentIssue.Number);
            GitHubIssue updatedIssue = await _gitHubClientService.UpdateIssueAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentIssue.Number,
                null,
                null,
                nextState);
            TrackIssueAction(IssueActionKind.ToggleState, IssueActionOutcome.Success);
            await _issueQueryService.InvalidateIssueAsync(
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentIssue.Number);
            await TryRefreshIssueSelectionAfterMutationAsync(
                updatedIssue,
                token,
                GetString("RepoIssue.StateRefreshError", "Issue state updated, but JitHub could not refresh issue details."));
        }
        catch (GitHubAuthenticationException)
        {
            TrackIssueAction(IssueActionKind.ToggleState, IssueActionOutcome.AuthenticationError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackIssueAction(IssueActionKind.ToggleState, GetIssueActionOutcome(ex));
            ApplyPermissionFailure(capabilityTarget, IssueDeniedCapability.State, ex);
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "issues");
        }
        catch (HttpRequestException)
        {
            TrackIssueAction(IssueActionKind.ToggleState, IssueActionOutcome.NetworkError);
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = GetString("RepoIssue.UpdateNetworkError", "JitHub could not reach GitHub to update this issue.");
        }
    }

    public async Task AddIssueCommentAsync()
    {
        if (_navArg is null || SelectedIssue is null || !IsAddCommentEnabled || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubIssue currentIssue = SelectedIssue;
        IssueCapabilityTarget capabilityTarget = _capabilityDenials.CaptureTarget();

        string body = IssueCommentDraft;
        if (string.IsNullOrWhiteSpace(body))
        {
            StatusText = GetString("RepoIssue.CommentValidation", "Type a comment before posting it.");
            return;
        }

        try
        {
            IsAddCommentEnabled = false;
            StatusText = FormatString("RepoIssue.AddCommentStatus", "Commenting on issue #{0}...", currentIssue.Number);
            _isIssueCommentSubmissionInProgress = true;
            UpdateIssueCommentEnabledState();
            await _gitHubClientService.CreateIssueCommentAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentIssue.Number,
                body);
            TrackIssueAction(IssueActionKind.Comment, IssueActionOutcome.Success);
            await _issueQueryService.InvalidateIssueAsync(
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                currentIssue.Number);
            ClearSubmittedIssueDraft(currentIssue.Number);
            if (IsSelectedIssue(currentIssue))
            {
                StatusText = FormatString("RepoIssue.AddedCommentStatus", "Comment added to issue #{0}.", currentIssue.Number);
            }

            await TryRefreshIssueSelectionAfterMutationAsync(
                currentIssue,
                token,
                GetString("RepoIssue.CommentRefreshError", "Comment added, but JitHub could not refresh issue details."));
        }
        catch (GitHubAuthenticationException)
        {
            TrackIssueAction(IssueActionKind.Comment, IssueActionOutcome.AuthenticationError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackIssueAction(IssueActionKind.Comment, GetIssueActionOutcome(ex));
            ApplyPermissionFailure(capabilityTarget, IssueDeniedCapability.Comment, ex);
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "issues");
        }
        catch (HttpRequestException)
        {
            TrackIssueAction(IssueActionKind.Comment, IssueActionOutcome.NetworkError);
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = GetString("RepoIssue.CommentNetworkError", "JitHub could not reach GitHub to post this comment.");
        }
        finally
        {
            _isIssueCommentSubmissionInProgress = false;
            UpdateIssueCommentEnabledState();
        }
    }

    public async Task<IReadOnlyList<GitHubReaction>?> GetSelectedIssueReactionsAsync()
    {
        if (DialogMatrixAutomationScenario.IsEnabled && SelectedIssue is not null)
        {
            return [];
        }

        if (_navArg is null || SelectedIssue is null || !AreIssueActionsEnabled || !TryGetActiveToken(out string token))
        {
            return null;
        }

        int issueNumber = SelectedIssue.Number;
        IssueCapabilityTarget capabilityTarget = _capabilityDenials.CaptureTarget();
        try
        {
            IssuePagedSection<GitHubReaction> reactions = await _issueQueryService.GetAllIssueReactionsAsync(
                token,
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                issueNumber);
            if (reactions.State.Completeness != PagedDataCompleteness.Complete && SelectedIssue?.Number == issueNumber)
            {
                StatusText = GetString("RepoIssue.ReactionsPartialStatus", "Some reactions could not be loaded.");
            }

            return reactions.Items;
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            ApplyPermissionFailure(capabilityTarget, IssueDeniedCapability.Reaction, ex);
            if (SelectedIssue?.Number == issueNumber)
            {
                StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "issues");
            }
        }
        catch (HttpRequestException)
        {
            if (SelectedIssue?.Number == issueNumber)
            {
                StatusText = GetString("RepoIssue.ReactionsNetworkError", "JitHub could not reach GitHub to update reactions.");
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<GitHubReaction>?> GetIssueCommentReactionsAsync(long commentId)
    {
        if (_navArg is null || SelectedIssue is null || !AreIssueActionsEnabled || !TryGetActiveToken(out string token))
        {
            return null;
        }

        try
        {
            IssuePagedSection<GitHubReaction> reactions = await _issueQueryService.GetAllIssueCommentReactionsAsync(
                token,
                GetActiveUserPartition(token),
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                commentId);
            if (reactions.State.Completeness != PagedDataCompleteness.Complete)
            {
                StatusText = GetString("RepoIssue.ReactionsPartialStatus", "Some reactions could not be loaded.");
            }

            return reactions.Items;
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "issues");
        }
        catch (HttpRequestException)
        {
            StatusText = GetString("RepoIssue.ReactionsNetworkError", "JitHub could not reach GitHub to update reactions.");
        }

        return null;
    }

    public async Task ApplySelectedIssueReactionSelectionAsync(
        HashSet<string> selectedContents,
        Dictionary<string, long> existingReactionIds)
    {
        LastDialogMutationSucceeded = false;
        if (_navArg is null || SelectedIssue is null || !CanReactToIssue || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubIssue currentIssue = SelectedIssue;
        IssueCapabilityTarget capabilityTarget = _capabilityDenials.CaptureTarget();

        try
        {
            string owner = _navArg.Repo.Owner.Login;
            string repoName = _navArg.Repo.Name;
            foreach (string content in selectedContents.Except(existingReactionIds.Keys, StringComparer.OrdinalIgnoreCase))
            {
                await _gitHubClientService.ReactToIssueAsync(token, owner, repoName, currentIssue.Number, content);
            }

            foreach (string content in existingReactionIds.Keys.Except(selectedContents, StringComparer.OrdinalIgnoreCase))
            {
                await _gitHubClientService.DeleteIssueReactionAsync(
                    token,
                    owner,
                    repoName,
                    currentIssue.Number,
                    existingReactionIds[content]);
            }

            LastDialogMutationSucceeded = true;

            TrackIssueAction(IssueActionKind.Reaction, IssueActionOutcome.Success);
            await _issueQueryService.InvalidateIssueAsync(
                GetActiveUserPartition(token),
                owner,
                repoName,
                currentIssue.Number);

            await RefreshIssueSelectionAsync(_loadedIssues.FirstOrDefault(issue => issue.Number == currentIssue.Number) ?? currentIssue, token);
        }
        catch (GitHubAuthenticationException)
        {
            TrackIssueAction(IssueActionKind.Reaction, IssueActionOutcome.AuthenticationError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackIssueAction(IssueActionKind.Reaction, GetIssueActionOutcome(ex));
            ApplyPermissionFailure(capabilityTarget, IssueDeniedCapability.Reaction, ex);
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "issues");
        }
        catch (HttpRequestException)
        {
            TrackIssueAction(IssueActionKind.Reaction, IssueActionOutcome.NetworkError);
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = GetString("RepoIssue.ReactionsNetworkError", "JitHub could not reach GitHub to update reactions.");
        }
    }

    public async Task ApplyIssueCommentReactionSelectionAsync(
        long commentId,
        HashSet<string> selectedContents,
        Dictionary<string, long> existingReactionIds)
    {
        if (_navArg is null || SelectedIssue is null || !CanReactToIssue || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubIssue currentIssue = SelectedIssue;
        IssueCapabilityTarget capabilityTarget = _capabilityDenials.CaptureTarget();

        try
        {
            string owner = _navArg.Repo.Owner.Login;
            string repoName = _navArg.Repo.Name;
            foreach (string content in selectedContents.Except(existingReactionIds.Keys, StringComparer.OrdinalIgnoreCase))
            {
                await _gitHubClientService.ReactToIssueCommentAsync(token, owner, repoName, commentId, content);
            }

            foreach (string content in existingReactionIds.Keys.Except(selectedContents, StringComparer.OrdinalIgnoreCase))
            {
                await _gitHubClientService.DeleteIssueCommentReactionAsync(
                    token,
                    owner,
                    repoName,
                    commentId,
                    existingReactionIds[content]);
            }

            TrackIssueAction(IssueActionKind.CommentReaction, IssueActionOutcome.Success);
            await _issueQueryService.InvalidateIssueAsync(
                GetActiveUserPartition(token),
                owner,
                repoName,
                currentIssue.Number);

            await RefreshIssueSelectionAsync(_loadedIssues.FirstOrDefault(issue => issue.Number == currentIssue.Number) ?? currentIssue, token);
        }
        catch (GitHubAuthenticationException)
        {
            TrackIssueAction(IssueActionKind.CommentReaction, IssueActionOutcome.AuthenticationError);
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            TrackIssueAction(IssueActionKind.CommentReaction, GetIssueActionOutcome(ex));
            ApplyPermissionFailure(capabilityTarget, IssueDeniedCapability.Reaction, ex);
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "issues");
        }
        catch (HttpRequestException)
        {
            TrackIssueAction(IssueActionKind.CommentReaction, IssueActionOutcome.NetworkError);
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = GetString("RepoIssue.ReactionsNetworkError", "JitHub could not reach GitHub to update reactions.");
        }
    }

    partial void OnSelectedIssueChanged(GitHubIssue? value)
    {
        NotifySelectedIssuePropertiesChanged();
        if (_suppressSelectionChanged)
        {
            return;
        }

        CancelActiveDetailLoad();
        _selectionDwellPrefetch?.Dispose();
        _selectionDwellPrefetch = null;
        _neighborPrefetch?.Dispose();
        _neighborPrefetch = null;
        int clearedPinnedIssueNumber = 0;
        if (_pinnedIssueNumber > 0 && value?.Number != _pinnedIssueNumber)
        {
            clearedPinnedIssueNumber = _pinnedIssueNumber;
            _pinnedIssueNumber = 0;
        }

        _detailRequestId++;
        CancelPendingSelectionLoad();
        if (value is null)
        {
            _pendingIssueSelectionState = null;
            _ = ShowIssueAsync(null);
            return;
        }

        IssueTelemetry.TrackSelected(_telemetryService);
        int previousIssueNumber = _lastFocusedIssueNumber;
        RemoveClearedPinnedIssueFromVisibleList(clearedPinnedIssueNumber);
        if (TryRestorePendingIssueSelectionState(value.Number))
        {
            _lastFocusedIssueNumber = value.Number;
            return;
        }

        if (_pendingIssueSelectionState is null && previousIssueNumber > 0 && AreIssueActionsEnabled)
        {
            CaptureIssueDetailSnapshot(previousIssueNumber);
        }

        _lastFocusedIssueNumber = value.Number;
        PrepareIssueForSelectionLoad(value);
        ScheduleIssuePrefetch(value, IssuePrefetchReason.Dwell, TimeSpan.FromSeconds(5), replaceDwell: true);
        ScheduleNeighborPrefetch(value);
        CancellationTokenSource cancellationTokenSource = new();
        _selectionLoadCancellationTokenSource = cancellationTokenSource;
        _ = ShowIssueAfterSelectionDelayAsync(
            value,
            cancellationTokenSource.Token,
            _navigationInitializationVersion);
    }

    private async Task LoadIssuesAsync(
        int preferredIssueNumber = 0,
        bool preservePreferredIssueOutsideQuery = true,
        bool preserveCurrentDetailDuringLoad = false)
    {
        if (_navArg is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        IssueNavArg navigationArgs = _navArg;
        Stopwatch loadStopwatch = Stopwatch.StartNew();
        CacheState observedCacheState = CacheState.Miss;
        string loadResult = "cancelled";

        int requestId = ++_listRequestId;
        CancellationTokenSource listLoad = BeginListLoad();
        CancellationToken cancellationToken = listLoad.Token;
        bool previousAreIssueActionsEnabled = AreIssueActionsEnabled;
        bool previousIsAddCommentEnabled = IsAddCommentEnabled;
        string? preferredIssueLoadFailureStatus = null;
        int issueNumberToSelect = preferredIssueNumber > 0 ? preferredIssueNumber : navigationArgs.IssueId;
        string queryIdentity = IssueRefreshProjectionPolicy.CreateQueryIdentity(_issueQuery);
        bool isSameQuery = string.Equals(
            _loadedIssueQueryIdentity,
            queryIdentity,
            StringComparison.Ordinal);
        IsLoading = Issues.Count == 0;
        StatusText = preserveCurrentDetailDuringLoad
            ? GetString("RepoIssue.RefreshingStatus", "Refreshing repository issues...")
            : LoadingStatusText;
        if (!preserveCurrentDetailDuringLoad)
        {
            CancelPendingSelectionLoad();
            CancelActiveDetailLoad();
            _pendingIssueSelectionState = null;
            _detailRequestId++;
            AreIssueActionsEnabled = false;
            IsAddCommentEnabled = false;
        }

        try
        {
            IssuePagedSection<GitHubIssue> issueSection = await _issueQueryService.GetAllIssuesProgressivelyAsync(
                token,
                GetActiveUserPartition(token),
                navigationArgs.Repo.Owner.Login,
                navigationArgs.Repo.Name,
                _issueQuery,
                async (progress, progressCancellationToken) =>
                {
                    if (requestId != _listRequestId)
                    {
                        return;
                    }

                    await ApplyProgressiveIssueListAsync(
                        progress,
                        queryIdentity,
                        isSameQuery,
                        issueNumberToSelect,
                        progressCancellationToken);
                },
                cancellationToken);
            observedCacheState = issueSection.State.CacheState;
            _issueListState = issueSection.State;
            UpdateIssueListScopeNotice();
            issueNumberToSelect = SelectedIssue?.Number ?? issueNumberToSelect;
            loadResult = issueSection.State.Completeness == PagedDataCompleteness.Complete &&
                string.IsNullOrWhiteSpace(issueSection.State.ErrorMessage)
                    ? "success"
                    : "partial";
            IReadOnlyList<GitHubIssue> issues = IssueRefreshProjectionPolicy.PreserveExistingRowsOnPartialRefresh(
                issueSection.Items,
                _loadedIssues,
                issueSection.State,
                isSameQuery);

            if (requestId != _listRequestId)
            {
                return;
            }

            List<GitHubIssue> loadedIssues = [.. issues];
            int pinnedIssueNumber = 0;
            if (issueNumberToSelect > 0)
            {
                GitHubIssue? selectedIssue = loadedIssues.FirstOrDefault(issue => issue.Number == issueNumberToSelect);
                if (selectedIssue is null)
                {
                    try
                    {
                        CachedResult<GitHubIssue> selectedResult = await _issueQueryService.GetIssueAsync(
                            token,
                            GetActiveUserPartition(token),
                            navigationArgs.Repo.Owner.Login,
                            navigationArgs.Repo.Name,
                            issueNumberToSelect,
                            cancellationToken);
                        selectedIssue = selectedResult.Value;
                    }
                    catch (GitHubAuthenticationException)
                    {
                        throw;
                    }
                    catch (GitHubApiException)
                    {
                        selectedIssue = null;
                        issueNumberToSelect = 0;
                        preferredIssueLoadFailureStatus = GetString("RepoIssue.PreferredLoadApiError", "JitHub could not load the requested issue.");
                    }
                    catch (HttpRequestException)
                    {
                        selectedIssue = null;
                        issueNumberToSelect = 0;
                        preferredIssueLoadFailureStatus = GetString("RepoIssue.PreferredLoadNetworkError", "JitHub could not reach GitHub to load the requested issue.");
                    }

                    if (requestId != _listRequestId)
                    {
                        return;
                    }
                }

                if (selectedIssue is not null)
                {
                    bool matchesQuery = MatchesIssueQuery(selectedIssue);
                    if (!matchesQuery && preservePreferredIssueOutsideQuery)
                    {
                        pinnedIssueNumber = selectedIssue.Number;
                    }

                    if ((matchesQuery || pinnedIssueNumber == selectedIssue.Number)
                        && loadedIssues.All(existingIssue => existingIssue.Number != selectedIssue.Number))
                    {
                        loadedIssues.Insert(0, selectedIssue);
                    }
                }
            }

            if (requestId != _listRequestId)
            {
                return;
            }

            _pinnedIssueNumber = pinnedIssueNumber;
            _loadedIssues.Clear();
            _loadedIssues.AddRange(loadedIssues);
            _loadedIssueQueryIdentity = queryIdentity;
            await ApplyIssueListFilterAsync(issueNumberToSelect);
            if (!string.IsNullOrWhiteSpace(preferredIssueLoadFailureStatus) && requestId == _listRequestId)
            {
                StatusText = preferredIssueLoadFailureStatus;
            }
        }
        catch (GitHubAuthenticationException)
        {
            loadResult = "auth_error";
            if (requestId != _listRequestId)
            {
                return;
            }

            AreIssueActionsEnabled = previousAreIssueActionsEnabled;
            IsAddCommentEnabled = previousIsAddCommentEnabled;
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            loadResult = "error";
            if (requestId != _listRequestId)
            {
                return;
            }

            AreIssueActionsEnabled = previousAreIssueActionsEnabled;
            IsAddCommentEnabled = previousIsAddCommentEnabled;
            StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "issues");
        }
        catch (HttpRequestException)
        {
            loadResult = "network_error";
            if (requestId != _listRequestId)
            {
                return;
            }

            AreIssueActionsEnabled = previousAreIssueActionsEnabled;
            IsAddCommentEnabled = previousIsAddCommentEnabled;
            StatusText = GetString("RepoIssue.LoadNetworkError", "JitHub could not reach GitHub to load issues.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            loadResult = "cancelled";
        }
        finally
        {
            CompleteListLoad(listLoad);
            IssueTelemetry.TrackListLoaded(
                _telemetryService,
                observedCacheState,
                loadResult,
                loadStopwatch.Elapsed);
            if (requestId == _listRequestId)
            {
                IsLoading = false;
                IsEmpty = Issues.Count == 0;
            }
        }
    }

    private Task ShowIssueAsync(GitHubIssue? issue)
    {
        return ShowIssueAsync(issue, preserveCurrentState: false, preserveStatusText: false);
    }

    private async Task ShowIssueAsync(GitHubIssue? issue, bool preserveCurrentState, bool preserveStatusText = false)
    {
        string preservedStatusText = StatusText;
        if (issue is null || _navArg is null)
        {
            CancelActiveDetailLoad();
            ResetIssueDetails();
            return;
        }

        IssueNavArg navigationArgs = _navArg;

        bool appliedSnapshot = TryApplyNavigationSnapshot(
            GetActiveUserPartition(GetActiveToken() ?? string.Empty),
            navigationArgs,
            issue.Number,
            preserveStatusText ? preservedStatusText : null);
        if (appliedSnapshot)
        {
            preserveCurrentState = true;
        }

        if (!preserveCurrentState)
        {
            PrepareIssueForSelectionLoad(issue);
        }

        if (!TryGetActiveToken(out string token))
        {
            return;
        }

        int requestId = ++_detailRequestId;
        CancellationTokenSource detailLoad = BeginDetailLoad();
        CancellationToken cancellationToken = detailLoad.Token;
        IsDetailLoading = true;

        try
        {
            if (!preserveStatusText)
            {
                StatusText = appliedSnapshot
                    ? FormatString("RepoIssue.RefreshIssueStatus", "Refreshing issue #{0}...", issue.Number)
                    : FormatString("RepoIssue.LoadIssueStatus", "Loading issue #{0}...", issue.Number);
            }

            CachedResult<GitHubIssue> issueResult = await _issueQueryService.GetIssueAsync(
                token,
                GetActiveUserPartition(token),
                navigationArgs.Repo.Owner.Login,
                navigationArgs.Repo.Name,
                issue.Number,
                cancellationToken);
            GitHubIssue latestIssue = issueResult.Value
                ?? throw new GitHubApiException(System.Net.HttpStatusCode.NotFound, "Issue is unavailable.");
            cancellationToken.ThrowIfCancellationRequested();
            if (requestId != _detailRequestId)
            {
                return;
            }

            GitHubIssue displayIssue = ReplaceIssueInCollection(latestIssue);
            _isNavigationPreview = false;
            _isIssueBodyDeferred = false;
            SetSelectedIssue(displayIssue);
            PopulateIssue(displayIssue);
            StoreCurrentIssueSnapshot(navigationArgs, latestIssue, "repo-issue-detail");

            IssuePagedSection<GitHubIssueComment> commentsSection =
                await _issueQueryService.GetAllIssueCommentsProgressivelyAsync(
                    token,
                    GetActiveUserPartition(token),
                    navigationArgs.Repo.Owner.Login,
                    navigationArgs.Repo.Name,
                    issue.Number,
                    (progress, progressCancellationToken) => ApplyProgressiveIssueCommentsAsync(
                        progress,
                        navigationArgs,
                        latestIssue,
                        requestId,
                        progressCancellationToken),
                    cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (requestId != _detailRequestId)
            {
                return;
            }

            bool preserveVisibleComments = commentsSection.State.Completeness != PagedDataCompleteness.Complete &&
                IssueComments.Count > 0;
            StatusText = preserveVisibleComments
                ? GetString("RepoIssue.CommentsRefreshFailedCachedStatus", "Some comments may be out of date.")
                : preserveStatusText
                    ? preservedStatusText
                    : FormatString("RepoIssue.LoadedStatus", "Issue #{0} loaded.", latestIssue.Number);
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
                StatusText = UserFacingError.For(ex, UserFacingErrorKind.Action, "issues");
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
                StatusText = GetString("RepoIssue.LoadDetailNetworkError", "JitHub could not reach GitHub to load issue details.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            CompleteDetailLoad(detailLoad);
            if (requestId == _detailRequestId)
            {
                IsDetailLoading = false;
            }
        }
    }

    private Task ApplyProgressiveIssueCommentsAsync(
        IssuePagedSection<GitHubIssueComment> progress,
        IssueNavArg navigationArgs,
        GitHubIssue issue,
        int requestId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (requestId != _detailRequestId || SelectedIssue?.Number != issue.Number)
        {
            return Task.CompletedTask;
        }

        IReadOnlyList<GitHubIssueComment> comments =
            IssueRefreshProjectionPolicy.PreserveExistingSectionOnPartialRefresh(
                progress,
                IssueComments,
                static comment => comment.Id);
        ApplyIssueComments(comments);
        IsIssueCommentsEmptyVisible = IssueComments.Count == 0 &&
            progress.State.Completeness != PagedDataCompleteness.Loading;
        NotifyIssueCommentsChanged();
        StoreCurrentIssueSnapshot(navigationArgs, issue, "repo-issue-detail");
        return Task.CompletedTask;
    }

    private void PopulateIssue(GitHubIssue issue)
    {
        ApplyViewerCapabilities(issue);
        IssueTitleText = FormatString("RepoIssue.DetailTitleFormat", "#{0} {1}", issue.Number, issue.Title);
        IssueMetaText = FormatString(
            "RepoIssue.DetailMetaFormat",
            "{0}  •  @{1}  •  Updated {2:g}  •  {3}",
            GetIssueStateDisplay(issue.State),
            GetIssueAuthorDisplayName(issue),
            issue.UpdatedAt.LocalDateTime,
            FormatCommentCount(issue.Comments));
        IssueMetadataText = FormatIssueMetadataSummary(issue);
        IssueReactionsText = issue.Reactions.DisplayText;
        IssueBodyText = string.IsNullOrWhiteSpace(issue.Body)
            ? GetString("RepoIssue.NoDescription", "No issue description is available.")
            : issue.Body;
        ToggleIssueStateButtonText = string.Equals(issue.State, "closed", StringComparison.OrdinalIgnoreCase)
            ? GetString("RepoIssue.ReopenButton", "Reopen issue")
            : GetString("RepoIssue.CloseButton", "Close issue");
        ReplaceInspectorCollections(issue);
        NotifySelectedIssuePropertiesChanged();
    }

    private void ResetIssueDetails()
    {
        _detailRequestId++;
        _isNavigationPreview = false;
        _isIssueBodyDeferred = false;
        ApplyViewerCapabilities(null);
        IssueTitleText = GetString("RepoIssue.SelectIssueTitle", "Select an issue");
        IssueMetaText = GetString("RepoIssue.SelectIssueSubtitle", "Choose an issue to inspect its details.");
        IssueMetadataText = string.Empty;
        IssueReactionsText = GetString("RepoIssue.NoReactions", "Reactions: none");
        IssueBodyText = string.Empty;
        IssueCommentDraft = string.Empty;
        IssueComments.Clear();
        IsIssueCommentsEmptyVisible = false;
        SelectedLabels.Clear();
        SelectedAssignees.Clear();
        ToggleIssueStateButtonText = GetString("RepoIssue.CloseButton", "Close issue");
        NotifyInspectorCollectionsChanged();
        NotifyIssueCommentsChanged();
        NotifySelectedIssuePropertiesChanged();
    }

    private void PrepareIssueForSelectionLoad(GitHubIssue issue)
    {
        ApplyViewerCapabilities(issue);
        IssueTitleText = FormatString("RepoIssue.DetailTitleFormat", "#{0} {1}", issue.Number, issue.Title);
        IssueMetaText = FormatString(
            "RepoIssue.DetailMetaFormat",
            "{0}  •  @{1}  •  Updated {2:g}  •  {3}",
            GetIssueStateDisplay(issue.State),
            GetIssueAuthorDisplayName(issue),
            issue.UpdatedAt.LocalDateTime,
            FormatCommentCount(issue.Comments));
        IssueMetadataText = FormatIssueMetadataSummary(issue);
        IssueReactionsText = issue.Reactions.DisplayText;
        IssueBodyText = string.IsNullOrWhiteSpace(issue.Body)
            ? GetString("RepoIssue.LoadingBodyPlaceholder", "Loading issue details...")
            : issue.Body;
        IssueCommentDraft = string.Empty;
        IssueComments.Clear();
        IsIssueCommentsEmptyVisible = false;
        ReplaceInspectorCollections(issue);
        ToggleIssueStateButtonText = string.Equals(issue.State, "closed", StringComparison.OrdinalIgnoreCase)
            ? GetString("RepoIssue.ReopenButton", "Reopen issue")
            : GetString("RepoIssue.CloseButton", "Close issue");
        StatusText = FormatString("RepoIssue.LoadIssueStatus", "Loading issue #{0}...", issue.Number);
        NotifyIssueCommentsChanged();
        NotifySelectedIssuePropertiesChanged();
    }

    private void CaptureIssueDetailSnapshot(int issueNumber)
    {
        if (issueNumber <= 0)
        {
            _pendingIssueSelectionState = null;
            return;
        }

        _pendingIssueSelectionState = new IssueDetailSnapshot(
            issueNumber,
            StatusText,
            IssueTitleText,
            IssueMetaText,
            IssueMetadataText,
            IssueReactionsText,
            IssueBodyText,
            IssueCommentDraft,
            ToggleIssueStateButtonText,
            AreIssueActionsEnabled,
            IsAddCommentEnabled,
            IsIssueCommentsEmptyVisible,
            IssueComments.ToArray());
    }

    private bool TryRestorePendingIssueSelectionState(int issueNumber)
    {
        if (_pendingIssueSelectionState is null || _pendingIssueSelectionState.IssueNumber != issueNumber)
        {
            return false;
        }

        RestoreIssueDetailSnapshot(_pendingIssueSelectionState);
        _pendingIssueSelectionState = null;
        return true;
    }

    private void RestoreIssueDetailSnapshot(IssueDetailSnapshot snapshot)
    {
        StatusText = snapshot.StatusText;
        AreIssueActionsEnabled = snapshot.AreActionsEnabled;
        IsAddCommentEnabled = snapshot.IsAddCommentEnabled;
        IssueTitleText = snapshot.TitleText;
        IssueMetaText = snapshot.MetaText;
        IssueMetadataText = snapshot.MetadataText;
        IssueReactionsText = snapshot.ReactionsText;
        IssueBodyText = snapshot.BodyText;
        IssueCommentDraft = snapshot.CommentDraft;
        ToggleIssueStateButtonText = snapshot.ToggleStateButtonText;
        IssueComments.Clear();
        foreach (GitHubIssueComment comment in snapshot.Comments)
        {
            IssueComments.Add(comment);
        }

        IsIssueCommentsEmptyVisible = snapshot.IsCommentsEmptyVisible;
        if (SelectedIssue is not null)
        {
            ReplaceInspectorCollections(SelectedIssue);
            ApplyViewerCapabilities(SelectedIssue);
        }

        NotifyIssueCommentsChanged();
        NotifySelectedIssuePropertiesChanged();
    }

    public void PrefetchIssue(GitHubIssue? issue, IssuePrefetchReason reason)
    {
        if (issue is null)
        {
            return;
        }

        if (reason == IssuePrefetchReason.Hover)
        {
            _hoverPrefetch.Schedule(
                HoverPrefetchDebounce,
                () => ScheduleIssuePrefetch(issue, reason, TimeSpan.Zero, replaceDwell: false));
            return;
        }

        _ = ScheduleIssuePrefetch(issue, reason, TimeSpan.Zero, replaceDwell: false);
    }

    public bool LastDialogMutationSucceeded { get; private set; }

    public void CancelPredictivePrefetches()
    {
        _hoverPrefetch.Cancel();
        _selectionDwellPrefetch?.Dispose();
        _selectionDwellPrefetch = null;
        _neighborPrefetch?.Dispose();
        _neighborPrefetch = null;
    }

    public void CancelNavigationWork()
    {
        _navigationInitializationVersion++;
        CancelActiveListLoad();
        CancelActiveDetailLoad();
        CancelPendingSelectionLoad();
        CancelPredictivePrefetches();
    }

    private bool TryApplyNavigationSnapshot(
        string accountPartition,
        IssueNavArg navigationArgs,
        int issueNumber,
        string? statusText) =>
        TryApplyNavigationSnapshot(
            accountPartition,
            navigationArgs,
            issueNumber,
            statusText,
            deferComments: false,
            out _);

    private bool TryApplyNavigationSnapshot(
        string accountPartition,
        IssueNavArg navigationArgs,
        int issueNumber,
        string? statusText,
        bool deferComments,
        out IssueNavigationSnapshot? navigationSnapshot)
    {
        navigationSnapshot = null;
        if (issueNumber <= 0 ||
            !_issueNavigationCache.TryGet(
                accountPartition,
                navigationArgs.Repo.Owner.Login,
                navigationArgs.Repo.Name,
                issueNumber,
                out IssueNavigationSnapshot snapshot))
        {
            return false;
        }

        navigationSnapshot = snapshot;

        ApplyNavigationSnapshot(navigationArgs, snapshot, statusText, deferComments);
        return true;
    }

    private void ApplyNavigationSnapshot(
        IssueNavArg navigationArgs,
        IssueNavigationSnapshot snapshot,
        string? statusText,
        bool deferComments)
    {
        _isNavigationPreview = string.Equals(
            snapshot.Source,
            "notification-preview",
            StringComparison.Ordinal);
        _isIssueBodyDeferred = deferComments || _isNavigationPreview;
        GitHubIssue displayIssue = ReplaceIssueInCollectionOrAdd(snapshot.Issue);
        SetSelectedIssue(displayIssue);
        if (_isNavigationPreview)
        {
            ApplyViewerCapabilities(null);
            IssueTitleText = FormatString("RepoIssue.DetailTitleFormat", "#{0} {1}", displayIssue.Number, displayIssue.Title);
            IssueMetaText = string.Empty;
            IssueMetadataText = string.Empty;
            IssueBodyText = string.Empty;
            IssueReactionsText = string.Empty;
            IssueComments.Clear();
            IsIssueCommentsEmptyVisible = false;
            NotifySelectedIssuePropertiesChanged();
        }
        else
        {
            PopulateIssue(displayIssue);
        }

        if (!deferComments && !_isNavigationPreview)
        {
            ApplyIssueComments(snapshot.Comments);
            IsIssueCommentsEmptyVisible = IssueComments.Count == 0;
            NotifyIssueCommentsChanged();
        }
        else
        {
            IsIssueCommentsEmptyVisible = snapshot.Comments.Length == 0;
        }

        if (!string.IsNullOrWhiteSpace(statusText))
        {
            StatusText = statusText!;
        }

        IsEmpty = Issues.Count == 0;
    }

    private void ApplyDeferredNavigationComments(IssueNavigationSnapshot snapshot)
    {
        if (_navArg is null ||
            SelectedIssue?.Number != snapshot.IssueNumber ||
            string.Equals(snapshot.Source, "notification-preview", StringComparison.Ordinal) ||
            !string.Equals(_navArg.Repo.Owner.Login, snapshot.Owner, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_navArg.Repo.Name, snapshot.RepositoryName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _isIssueBodyDeferred = false;
        OnPropertyChanged(nameof(IssueBodyMarkdownSource));
        ApplyIssueComments(snapshot.Comments);
        IsIssueCommentsEmptyVisible = IssueComments.Count == 0;
        NotifyIssueCommentsChanged();
    }

    private void StoreCurrentIssueSnapshot(IssueNavArg navigationArgs, GitHubIssue issue, string source)
    {
        if (issue.Number <= 0)
        {
            return;
        }

        string accountPartition = GetActiveUserPartition(GetActiveToken() ?? string.Empty);
        _issueNavigationCache.Store(accountPartition, new IssueNavigationSnapshot(
            navigationArgs.Repo.Owner.Login,
            navigationArgs.Repo.Name,
            issue.Number,
            issue,
            IssueComments.ToArray(),
            DateTimeOffset.UtcNow,
            source));
    }

    private IDisposable? ScheduleIssuePrefetch(GitHubIssue issue, IssuePrefetchReason reason, TimeSpan delay, bool replaceDwell)
    {
        string? token = GetActiveToken();
        if (_navArg is null || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string userPartition = GetActiveUserPartition(token);
        IssueTelemetry.TrackPrefetchStarted(_telemetryService, reason);
        IDisposable prefetch = _issueNavigationCache.SchedulePrefetch(
            token,
            userPartition,
            _navArg.Repo.Owner.Login,
            _navArg.Repo.Name,
            issue.Number,
            reason,
            delay,
            (result, duration) => IssueTelemetry.TrackPrefetchCompleted(
                _telemetryService,
                reason,
                result,
                duration));

        if (replaceDwell)
        {
            _selectionDwellPrefetch?.Dispose();
            _selectionDwellPrefetch = prefetch;
        }

        return prefetch;
    }

    private void ScheduleNeighborPrefetch(GitHubIssue issue)
    {
        int selectedIndex = Issues.IndexOf(issue);
        if (selectedIndex < 0)
        {
            return;
        }

        GitHubIssue? nextIssue = selectedIndex + 1 < Issues.Count ? Issues[selectedIndex + 1] : null;
        GitHubIssue? previousIssue = selectedIndex > 0 ? Issues[selectedIndex - 1] : null;
        GitHubIssue? neighbor = nextIssue ?? previousIssue;
        if (neighbor is null)
        {
            return;
        }

        _neighborPrefetch?.Dispose();
        string? token = GetActiveToken();
        if (_navArg is null || string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        IssueTelemetry.TrackPrefetchStarted(_telemetryService, IssuePrefetchReason.Neighbor);
        _neighborPrefetch = _issueNavigationCache.SchedulePrefetch(
            token,
            GetActiveUserPartition(token),
            _navArg.Repo.Owner.Login,
            _navArg.Repo.Name,
            neighbor.Number,
            IssuePrefetchReason.Neighbor,
            TimeSpan.FromMilliseconds(650),
            (result, duration) => IssueTelemetry.TrackPrefetchCompleted(
                _telemetryService,
                IssuePrefetchReason.Neighbor,
                result,
                duration));
    }

    private void ReplaceInspectorCollections(GitHubIssue issue)
    {
        SelectedLabels.Clear();
        foreach (GitHubLabel label in issue.Labels)
        {
            SelectedLabels.Add(label);
        }

        SelectedAssignees.Clear();
        foreach (GitHubActor assignee in issue.Assignees)
        {
            SelectedAssignees.Add(assignee);
        }

        NotifyInspectorCollectionsChanged();
    }

    private async Task ApplyIssueListFilterAsync(int preferredIssueNumber, bool refreshSelectionDetails = true)
    {
        GitHubIssue? previousSelectedIssue = SelectedIssue;
        IEnumerable<GitHubIssue> filteredIssues = _loadedIssues.Where(issue => MatchesIssueQuery(issue) || IsPinnedIssue(issue));

        string searchText = SearchText.Trim();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            filteredIssues = filteredIssues.Where(issue => MatchesIssueSearch(issue, searchText));
        }

        GitHubIssue? pinnedIssue = filteredIssues.FirstOrDefault(IsPinnedIssue);
        List<GitHubIssue> visibleIssues = SortIssues(filteredIssues.Where(issue => !IsPinnedIssue(issue))).ToList();
        if (pinnedIssue is not null)
        {
            visibleIssues.Insert(0, pinnedIssue);
        }

        GitHubIssue? selectedIssue = preferredIssueNumber > 0
            ? visibleIssues.FirstOrDefault(issue => issue.Number == preferredIssueNumber)
            : visibleIssues.FirstOrDefault();
        bool preserveFocusedDetails = selectedIssue is null
            && visibleIssues.Count == 0
            && _lastFocusedIssueNumber > 0;
        if (preserveFocusedDetails)
        {
            selectedIssue = _loadedIssues.FirstOrDefault(issue => issue.Number == _lastFocusedIssueNumber)
                ?? previousSelectedIssue;
        }

        StatusText = visibleIssues.Count == 0
            ? GetString("RepoIssue.NoMatchesStatus", "No issues matched the current filters.")
            : BuildIssueListStatus(visibleIssues.Count);

        bool selectionChanged = previousSelectedIssue?.Number != selectedIssue?.Number;
        if (SelectedIssue?.Number != selectedIssue?.Number)
        {
            CancelPendingSelectionLoad();
        }

        _suppressSelectionChanged = true;
        try
        {
            KeyedObservableReconciler.ApplySnapshot(
                Issues,
                visibleIssues,
                static issue => issue.Number.ToString(CultureInfo.InvariantCulture),
                AreIssueListSnapshotsEquivalent);

            if (!preserveFocusedDetails && selectedIssue is not null)
            {
                selectedIssue = Issues.FirstOrDefault(issue => issue.Number == selectedIssue.Number) ?? selectedIssue;
            }

            IsEmpty = Issues.Count == 0;
            SelectedIssue = selectedIssue;
            if (selectedIssue is not null)
            {
                _lastFocusedIssueNumber = selectedIssue.Number;
            }
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        if (preserveFocusedDetails)
        {
            if (refreshSelectionDetails && selectedIssue is not null)
            {
                await ShowIssueAsync(selectedIssue, preserveCurrentState: true, preserveStatusText: true);
            }
        }
        else if (refreshSelectionDetails || selectionChanged)
        {
            await ShowIssueAsync(selectedIssue, preserveCurrentState: refreshSelectionDetails && !selectionChanged);
        }
    }

    private void ApplyIssueQueryFromFilters()
    {
        _issueQuery.State = SelectedStateOption?.Value ?? "open";
        _issueQuery.Sort = SelectedSortOption?.Value ?? "updated";
        _issueQuery.Direction = SelectedDirectionOption?.Value ?? "desc";
        _issueQuery.Since = SelectedSinceDate is DateTimeOffset date
            ? CreateLocalMidnight(date)
            : null;
        _issueQuery.Assignee = null;
        _issueQuery.Creator = null;
        _issueQuery.Mentioned = null;
        if (!string.IsNullOrWhiteSpace(AuthenticatedLogin))
        {
            switch (SelectedScopeOption?.Value)
            {
                case "assigned":
                    _issueQuery.Assignee = AuthenticatedLogin;
                    break;
                case "created":
                    _issueQuery.Creator = AuthenticatedLogin;
                    break;
                case "mentioned":
                    _issueQuery.Mentioned = AuthenticatedLogin;
                    break;
            }
        }
    }

    private void ResetFilters()
    {
        SearchText = string.Empty;
        SelectedStateOption = StateOptions[0];
        SelectedScopeOption = ScopeOptions[0];
        SelectedSortOption = SortOptions[0];
        SelectedDirectionOption = DirectionOptions[0];
        SelectedSinceDate = null;
        ApplyIssueQueryFromFilters();
    }

    private async Task<bool> RefreshIssueSelectionAsync(GitHubIssue issue, string token)
    {
        if (_navArg is null)
        {
            return false;
        }

        IssueNavArg navigation = _navArg;
        int requestId = _listRequestId;
        string repositoryIdentity = GetRepositoryIdentity(navigation.Repo);
        IssueCapabilityTarget capabilityTarget = _capabilityDenials.CaptureTarget();
        GitHubIssue refreshedIssue;
        try
        {
            CachedResult<GitHubIssue> refreshResult = await _issueQueryService.RefreshIssueAsync(
                token,
                GetActiveUserPartition(token),
                navigation.Repo.Owner.Login,
                navigation.Repo.Name,
                issue.Number);
            refreshedIssue = refreshResult.Value
                ?? throw new GitHubApiException(System.Net.HttpStatusCode.NotFound, "Issue is unavailable.");
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

        IssueCapabilityRecoveryResult permissionRecovery = default;
        if (_capabilityDenials.HasDenials)
        {
            permissionRecovery = await _capabilityRecovery.RecoverAfterIssueRefreshAsync(
                capabilityTarget,
                refreshedIssue.Number,
                (fetchPolicy, cancellationToken) => TryGetAuthoritativeRepositoryAsync(
                    navigation,
                    token,
                    repositoryIdentity,
                    requestId,
                    fetchPolicy,
                    cancellationToken));
        }

        if (requestId != _listRequestId || !ReferenceEquals(_navArg, navigation))
        {
            return false;
        }

        ReplaceIssueInCollection(refreshedIssue);
        if (permissionRecovery.WasApplied &&
            permissionRecovery.Repository is not null &&
            ReferenceEquals(_navArg, navigation))
        {
            navigation.WithRepo(permissionRecovery.Repository);
            if (permissionRecovery.DenialsCleared)
            {
                ApplyViewerCapabilities(SelectedIssue?.Number == refreshedIssue.Number
                    ? refreshedIssue
                    : SelectedIssue);
            }
        }
        if (_loadedIssues.All(existingIssue => existingIssue.Number != refreshedIssue.Number)
            && MatchesIssueQuery(refreshedIssue))
        {
            _loadedIssues.Add(refreshedIssue);
        }

        bool isRetainedHiddenIssue = Issues.Count == 0 && _lastFocusedIssueNumber == issue.Number;
        if (SelectedIssue?.Number == issue.Number && !isRetainedHiddenIssue)
        {
            _pinnedIssueNumber = MatchesIssueQuery(refreshedIssue)
                ? 0
                : refreshedIssue.Number;
        }
        else if (_pinnedIssueNumber == refreshedIssue.Number
            && (MatchesIssueQuery(refreshedIssue) || isRetainedHiddenIssue))
        {
            _pinnedIssueNumber = 0;
        }

        int preferredIssueNumber = SelectedIssue?.Number ?? (_pinnedIssueNumber == refreshedIssue.Number ? refreshedIssue.Number : 0);
        await ApplyIssueListFilterAsync(
            preferredIssueNumber,
            refreshSelectionDetails: preferredIssueNumber == issue.Number);
        return true;
    }

    private async Task<bool> TryRefreshIssueSelectionAfterMutationAsync(
        GitHubIssue issue,
        string token,
        string refreshFailureStatus)
    {
        try
        {
            return await RefreshIssueSelectionAsync(issue, token);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException)
        {
            if (IsSelectedIssue(issue))
            {
                StatusText = refreshFailureStatus;
            }
        }
        catch (HttpRequestException)
        {
            if (IsSelectedIssue(issue))
            {
                StatusText = refreshFailureStatus;
            }
        }

        return false;
    }

    private void SetSelectedIssue(GitHubIssue? issue)
    {
        if (SelectedIssue?.Number != issue?.Number)
        {
            CancelPendingSelectionLoad();
        }

        _suppressSelectionChanged = true;
        SelectedIssue = issue;
        if (issue is not null)
        {
            _lastFocusedIssueNumber = issue.Number;
        }
        _suppressSelectionChanged = false;
    }

    private async Task ShowIssueAfterSelectionDelayAsync(
        GitHubIssue issue,
        CancellationToken cancellationToken,
        int navigationInitializationVersion)
    {
        try
        {
            await Task.Delay(SelectionLoadDebounce, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested ||
            navigationInitializationVersion != _navigationInitializationVersion ||
            _navArg is null)
        {
            return;
        }

        _pendingIssueSelectionState = null;
        await ShowIssueAsync(issue);
    }

    private void CancelPendingSelectionLoad()
    {
        _selectionLoadCancellationTokenSource?.Cancel();
        _selectionLoadCancellationTokenSource?.Dispose();
        _selectionLoadCancellationTokenSource = null;
    }

    private CancellationTokenSource BeginListLoad()
    {
        CancellationTokenSource current = new();
        TryCancel(Interlocked.Exchange(ref _listLoadCancellationTokenSource, current));
        return current;
    }

    private void CompleteListLoad(CancellationTokenSource current)
    {
        Interlocked.CompareExchange(ref _listLoadCancellationTokenSource, null, current);
        current.Dispose();
    }

    private void CancelActiveListLoad()
    {
        TryCancel(Volatile.Read(ref _listLoadCancellationTokenSource));
    }

    private CancellationTokenSource BeginDetailLoad()
    {
        CancellationTokenSource current = new();
        TryCancel(Interlocked.Exchange(ref _detailLoadCancellationTokenSource, current));
        return current;
    }

    private void CompleteDetailLoad(CancellationTokenSource current)
    {
        Interlocked.CompareExchange(ref _detailLoadCancellationTokenSource, null, current);
        current.Dispose();
    }

    private void CancelActiveDetailLoad()
    {
        TryCancel(Volatile.Read(ref _detailLoadCancellationTokenSource));
    }

    private static void TryCancel(CancellationTokenSource? cancellationTokenSource)
    {
        try
        {
            cancellationTokenSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation that owns the source completed between the atomic read and cancel.
        }
    }

    private static bool MatchesIssueSearch(GitHubIssue issue, string searchText)
    {
        return issue.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(issue.Body) && issue.Body.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            || (issue.User?.Login?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false)
            || issue.Number.ToString().Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateIssueCommentEnabledState()
    {
        IsAddCommentEnabled = CanCommentOnIssue() && !_isIssueCommentSubmissionInProgress;
    }

    private void ClearSubmittedIssueDraft(int issueNumber)
    {
        if (_pendingIssueSelectionState?.IssueNumber == issueNumber)
        {
            _pendingIssueSelectionState = _pendingIssueSelectionState with { CommentDraft = string.Empty };
        }

        if (SelectedIssue?.Number == issueNumber)
        {
            IssueCommentDraft = string.Empty;
        }
    }

    private static bool AreIssueListSnapshotsEquivalent(GitHubIssue current, GitHubIssue incoming)
    {
        return current.Id == incoming.Id &&
            current.Number == incoming.Number &&
            string.Equals(current.Title, incoming.Title, StringComparison.Ordinal) &&
            string.Equals(current.State, incoming.State, StringComparison.Ordinal) &&
            current.Comments == incoming.Comments &&
            current.UpdatedAt == incoming.UpdatedAt &&
            current.Locked == incoming.Locked &&
            string.Equals(current.User?.Login, incoming.User?.Login, StringComparison.Ordinal) &&
            current.Milestone?.Number == incoming.Milestone?.Number &&
            current.Labels.Select(static label => (label.Id, label.Name, label.Color))
                .SequenceEqual(incoming.Labels.Select(static label => (label.Id, label.Name, label.Color))) &&
            current.Assignees.Select(static actor => (actor.Id, actor.Login))
                .SequenceEqual(incoming.Assignees.Select(static actor => (actor.Id, actor.Login)));
    }

    private string BuildIssueListStatus(int visibleCount) => _issueListState.Completeness switch
    {
        PagedDataCompleteness.Partial => FormatString(
            "RepoIssue.ShowingPartialStatus",
            "Showing {0} issues; some results could not be loaded.",
            visibleCount),
        PagedDataCompleteness.ApiLimited => FormatString(
            "RepoIssue.ShowingLimitedStatus",
            "Showing the first {0} issues available for this view.",
            visibleCount),
        _ when visibleCount == 1 => FormatString("RepoIssue.ShowingSingleStatus", "Showing {0} issue.", visibleCount),
        _ => FormatString("RepoIssue.ShowingPluralStatus", "Showing {0} issues.", visibleCount)
    };

    private void UpdateIssueListScopeNotice()
    {
        IssueListScopeNotice = _issueListState.Completeness switch
        {
            PagedDataCompleteness.Partial => GetString(
                "RepoIssue.PartialScopeNotice",
                "Some issues could not be loaded. Available results remain visible."),
            PagedDataCompleteness.ApiLimited => GetString(
                "RepoIssue.LimitedScopeNotice",
                "This view reached GitHub's result limit."),
            _ => string.Empty
        };
        OnPropertyChanged(nameof(HasIssueListScopeNotice));
    }

    private void ApplyIssueComments(IReadOnlyList<GitHubIssueComment> comments)
    {
        Dictionary<long, GitHubIssueComment> existing = IssueComments
            .GroupBy(static comment => comment.Id)
            .ToDictionary(static group => group.Key, static group => group.First());
        HashSet<long> incomingIds = [];
        int targetIndex = 0;
        foreach (GitHubIssueComment comment in comments)
        {
            if (!incomingIds.Add(comment.Id))
            {
                continue;
            }

            int currentIndex = existing.TryGetValue(comment.Id, out GitHubIssueComment? current)
                ? IssueComments.IndexOf(current)
                : -1;
            if (currentIndex < 0)
            {
                IssueComments.Insert(targetIndex, comment);
            }
            else
            {
                if (!ReferenceEquals(IssueComments[currentIndex], comment) &&
                    !AreIssueCommentSnapshotsEquivalent(IssueComments[currentIndex], comment))
                {
                    IssueComments[currentIndex] = comment;
                }

                if (currentIndex != targetIndex)
                {
                    IssueComments.Move(currentIndex, targetIndex);
                }
            }

            targetIndex++;
        }

        for (int index = IssueComments.Count - 1; index >= 0; index--)
        {
            if (!incomingIds.Contains(IssueComments[index].Id))
            {
                IssueComments.RemoveAt(index);
            }
        }
    }

    private static bool AreIssueCommentSnapshotsEquivalent(
        GitHubIssueComment current,
        GitHubIssueComment incoming)
    {
        GitHubReactionSummary currentReactions = current.Reactions;
        GitHubReactionSummary incomingReactions = incoming.Reactions;
        return current.Id == incoming.Id &&
            string.Equals(current.NodeId, incoming.NodeId, StringComparison.Ordinal) &&
            string.Equals(current.HtmlUrl, incoming.HtmlUrl, StringComparison.Ordinal) &&
            string.Equals(current.Body, incoming.Body, StringComparison.Ordinal) &&
            current.CreatedAt == incoming.CreatedAt &&
            current.UpdatedAt == incoming.UpdatedAt &&
            current.User.Id == incoming.User.Id &&
            string.Equals(current.User.Login, incoming.User.Login, StringComparison.Ordinal) &&
            string.Equals(current.User.AvatarUrl, incoming.User.AvatarUrl, StringComparison.Ordinal) &&
            string.Equals(current.AuthorAssociation, incoming.AuthorAssociation, StringComparison.Ordinal) &&
            currentReactions.TotalCount == incomingReactions.TotalCount &&
            currentReactions.PlusOne == incomingReactions.PlusOne &&
            currentReactions.MinusOne == incomingReactions.MinusOne &&
            currentReactions.Laugh == incomingReactions.Laugh &&
            currentReactions.Hooray == incomingReactions.Hooray &&
            currentReactions.Confused == incomingReactions.Confused &&
            currentReactions.Heart == incomingReactions.Heart &&
            currentReactions.Rocket == incomingReactions.Rocket &&
            currentReactions.Eyes == incomingReactions.Eyes;
    }

    private async Task ApplyProgressiveIssueListAsync(
        IssuePagedSection<GitHubIssue> progress,
        string queryIdentity,
        bool isSameQuery,
        int preferredIssueNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (progress.Items.Length == 0 &&
            progress.State.CacheState == CacheState.Miss &&
            Issues.Count > 0)
        {
            return;
        }

        IReadOnlyList<GitHubIssue> projectedIssues =
            IssueRefreshProjectionPolicy.PreserveExistingRowsOnPartialRefresh(
                progress.Items,
                _loadedIssues,
                progress.State,
                isSameQuery);
        _issueListState = progress.State;
        UpdateIssueListScopeNotice();
        _loadedIssues.Clear();
        _loadedIssues.AddRange(projectedIssues);
        _loadedIssueQueryIdentity = queryIdentity;
        int selectionToPreserve = SelectedIssue?.Number ?? preferredIssueNumber;
        await ApplyIssueListFilterAsync(selectionToPreserve, refreshSelectionDetails: false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task CompleteCachedNavigationInitializationAsync(
        IssueNavArg navigationArgs,
        IssueNavigationSnapshot snapshot,
        int initializationVersion)
    {
        // Cached content is already local; reserve an input window before realizing the
        // Markdown conversation and starting stale-list reconciliation.
        TimeSpan commentDelay = CachedNavigationCommentQuietPeriod;
        await Task.Delay(commentDelay);
        if (initializationVersion != _navigationInitializationVersion ||
            !ReferenceEquals(_navArg, navigationArgs))
        {
            return;
        }

        ApplyDeferredNavigationComments(snapshot);
        TimeSpan refreshDelay = CachedNavigationRefreshQuietPeriod - commentDelay;
        if (refreshDelay > TimeSpan.Zero)
        {
            // Cached content is already interactive. Reserve the rest of the first second
            // for input before stale-list reconciliation can enqueue substantial UI work.
            await Task.Delay(refreshDelay);
            if (initializationVersion != _navigationInitializationVersion ||
                !ReferenceEquals(_navArg, navigationArgs))
            {
                return;
            }
        }

        await LoadIssuesAsync(
            navigationArgs.IssueId,
            preserveCurrentDetailDuringLoad: true);
        if (initializationVersion == _navigationInitializationVersion &&
            TryGetActiveToken(out string token) &&
            !GitHubAuthenticationConstants.IsPublicAccessToken(token))
        {
            await RefreshRepositoryCapabilitiesAsync(token, QueryFetchPolicy.StaleFirst);
        }
    }

    private void ApplyViewerCapabilities(GitHubIssue? issue)
    {
        int issueNumber = issue?.Number ?? 0;
        _capabilityDenials.TrackTarget(GetRepositoryIdentity(_navArg?.Repo), issueNumber);

        if (DialogMatrixAutomationScenario.IsEnabled)
        {
            bool hasIssue = issue is not null;
            AreIssueActionsEnabled = hasIssue;
            CanCreateIssue = true;
            CanEditIssue = hasIssue;
            CanManageIssueMetadata = hasIssue;
            CanChangeIssueState = hasIssue;
            CanReactToIssue = hasIssue;
            IsAddCommentEnabled = hasIssue && !_isIssueCommentSubmissionInProgress;
            return;
        }

        GitHubRepository? repository = _navArg?.Repo;
        string? token = GetActiveToken();
        IssueViewerCapabilities capabilities = IssuePermissionPolicy.Evaluate(
            repository,
            issue,
            AuthenticatedLogin,
            _authService.Authenticated,
            token is not null && GitHubAuthenticationConstants.IsPublicAccessToken(token));
        AreIssueActionsEnabled = issue is not null;
        CanCreateIssue = capabilities.CanCreateIssue && !_capabilityDenials.IsDenied(IssueDeniedCapability.Create);
        CanEditIssue = capabilities.CanEditIssue && !_capabilityDenials.IsDenied(IssueDeniedCapability.Edit);
        CanManageIssueMetadata = capabilities.CanManageMetadata && !_capabilityDenials.IsDenied(IssueDeniedCapability.Metadata);
        CanChangeIssueState = capabilities.CanChangeState && !_capabilityDenials.IsDenied(IssueDeniedCapability.State);
        CanReactToIssue = capabilities.CanReact && !_capabilityDenials.IsDenied(IssueDeniedCapability.Reaction);
        IsAddCommentEnabled = capabilities.CanComment &&
            !_capabilityDenials.IsDenied(IssueDeniedCapability.Comment) &&
            !_isIssueCommentSubmissionInProgress;
    }

    private bool CanCommentOnIssue()
    {
        if (_navArg is null || SelectedIssue is null)
        {
            return false;
        }

        string? token = GetActiveToken();
        IssueViewerCapabilities capabilities = IssuePermissionPolicy.Evaluate(
            _navArg.Repo,
            SelectedIssue,
            AuthenticatedLogin,
            _authService.Authenticated,
            token is not null && GitHubAuthenticationConstants.IsPublicAccessToken(token));
        return capabilities.CanComment && !_capabilityDenials.IsDenied(IssueDeniedCapability.Comment);
    }

    private void ApplyPermissionFailure(
        IssueCapabilityTarget target,
        IssueDeniedCapability capability,
        GitHubApiException exception)
    {
        if (!IssuePermissionPolicy.IsPermissionDenied(exception) ||
            !_capabilityRecovery.RecordIssueFailure(target, capability, exception.StatusCode))
        {
            return;
        }

        ApplyViewerCapabilities(SelectedIssue);
    }

    private void ApplyRepositoryPermissionFailure(
        IssueCapabilityTarget target,
        IssueDeniedCapability capability,
        GitHubApiException exception)
    {
        if (!IssuePermissionPolicy.IsPermissionDenied(exception) ||
            !_capabilityRecovery.RecordRepositoryFailure(
                target,
                capability,
                exception.StatusCode))
        {
            return;
        }

        ApplyViewerCapabilities(SelectedIssue);
    }

    private async Task RefreshAuthoritativeRepositoryCapabilitiesAsync(string token)
    {
        IssueNavArg? navigation = _navArg;
        if (navigation is null)
        {
            return;
        }

        int requestId = _listRequestId;
        string repositoryIdentity = GetRepositoryIdentity(navigation.Repo);
        IssueCapabilityTarget capabilityTarget = _capabilityDenials.CaptureTarget();
        IssueCapabilityRecoveryResult recovery = await _capabilityRecovery.RecoverRepositoryAsync(
            capabilityTarget,
            (fetchPolicy, cancellationToken) => TryGetAuthoritativeRepositoryAsync(
                navigation,
                token,
                repositoryIdentity,
                requestId,
                fetchPolicy,
                cancellationToken));
        if (!recovery.WasApplied ||
            recovery.Repository is null ||
            requestId != _listRequestId ||
            !ReferenceEquals(_navArg, navigation))
        {
            return;
        }

        navigation.WithRepo(recovery.Repository);
        ApplyViewerCapabilities(SelectedIssue);
    }

    private async Task RefreshRepositoryCapabilitiesAsync(
        string token,
        QueryFetchPolicy fetchPolicy)
    {
        IssueNavArg? navigation = _navArg;
        if (navigation is null)
        {
            return;
        }

        int requestId = _listRequestId;
        string repositoryIdentity = GetRepositoryIdentity(navigation.Repo);
        GitHubRepository? repository = await TryGetAuthoritativeRepositoryAsync(
            navigation,
            token,
            repositoryIdentity,
            requestId,
            fetchPolicy,
            CancellationToken.None);
        if (repository is null ||
            requestId != _listRequestId ||
            !ReferenceEquals(_navArg, navigation))
        {
            return;
        }

        navigation.WithRepo(repository);
        ApplyViewerCapabilities(SelectedIssue);
    }

    private async Task<GitHubRepository?> TryGetAuthoritativeRepositoryAsync(
        IssueNavArg navigation,
        string token,
        string repositoryIdentity,
        int requestId,
        QueryFetchPolicy fetchPolicy,
        CancellationToken cancellationToken)
    {
        try
        {
            CachedResult<GitHubRepository> result = await _repositoryQueryService.GetRepositoryAsync(
                token,
                GetActiveUserPartition(token),
                navigation.Repo.Owner.Login,
                navigation.Repo.Name,
                fetchPolicy,
                GitHubRequestPriority.Visible,
                cancellationToken);
            return requestId == _listRequestId &&
                ReferenceEquals(_navArg, navigation) &&
                string.Equals(
                    GetRepositoryIdentity(navigation.Repo),
                    repositoryIdentity,
                    StringComparison.OrdinalIgnoreCase)
                    ? result.Value
                    : null;
        }
        catch (GitHubAuthenticationException)
        {
            throw;
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

    private static string GetRepositoryIdentity(GitHubRepository? repository)
    {
        if (repository is null)
        {
            return string.Empty;
        }

        return !string.IsNullOrWhiteSpace(repository.FullName)
            ? repository.FullName.Trim()
            : $"{repository.Owner.Login}/{repository.Name}";
    }

    private void TrackIssueAction(IssueActionKind action, IssueActionOutcome outcome)
    {
        IssueTelemetry.TrackAction(_telemetryService, action, outcome);
    }

    private static IssueActionOutcome GetIssueActionOutcome(GitHubApiException exception)
    {
        return IssuePermissionPolicy.IsPermissionDenied(exception)
            ? IssueActionOutcome.PermissionDenied
            : IssueActionOutcome.Failure;
    }

    private bool IsSelectedIssue(GitHubIssue issue)
    {
        return SelectedIssue?.Number == issue.Number;
    }

    private bool IsPinnedIssue(GitHubIssue issue)
    {
        return _pinnedIssueNumber > 0 && issue.Number == _pinnedIssueNumber;
    }

    private void RemoveClearedPinnedIssueFromVisibleList(int clearedPinnedIssueNumber)
    {
        if (clearedPinnedIssueNumber <= 0)
        {
            return;
        }

        int pinnedIndex = Issues
            .Select((issue, index) => new { issue, index })
            .Where(item => item.issue.Number == clearedPinnedIssueNumber)
            .Select(item => item.index)
            .DefaultIfEmpty(-1)
            .First();
        if (pinnedIndex < 0)
        {
            return;
        }

        GitHubIssue pinnedIssue = Issues[pinnedIndex];
        bool matchesVisibleFilters = MatchesIssueQuery(pinnedIssue);
        string searchText = SearchText.Trim();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            matchesVisibleFilters = matchesVisibleFilters && MatchesIssueSearch(pinnedIssue, searchText);
        }

        if (matchesVisibleFilters)
        {
            return;
        }

        _suppressSelectionChanged = true;
        try
        {
            Issues.RemoveAt(pinnedIndex);
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    private IEnumerable<GitHubIssue> SortIssues(IEnumerable<GitHubIssue> issues)
    {
        bool descending = !string.Equals(_issueQuery.Direction, "asc", StringComparison.OrdinalIgnoreCase);
        return (_issueQuery.Sort ?? "updated").ToLowerInvariant() switch
        {
            "created" => descending
                ? issues.OrderByDescending(issue => issue.CreatedAt).ThenByDescending(issue => issue.Number)
                : issues.OrderBy(issue => issue.CreatedAt).ThenBy(issue => issue.Number),
            "comments" => descending
                ? issues.OrderByDescending(issue => issue.Comments).ThenByDescending(issue => issue.UpdatedAt).ThenByDescending(issue => issue.Number)
                : issues.OrderBy(issue => issue.Comments).ThenBy(issue => issue.UpdatedAt).ThenBy(issue => issue.Number),
            _ => descending
                ? issues.OrderByDescending(issue => issue.UpdatedAt).ThenByDescending(issue => issue.Number)
                : issues.OrderBy(issue => issue.UpdatedAt).ThenBy(issue => issue.Number)
        };
    }

    private bool MatchesIssueQuery(GitHubIssue issue)
    {
        if (!MatchesStateFilter(issue.State, _issueQuery.State))
        {
            return false;
        }

        if (!MatchesLoginFilter(issue.Assignees.Select(assignee => assignee.Login), _issueQuery.Assignee))
        {
            return false;
        }

        if (!MatchesSingleLoginFilter(issue.User?.Login, _issueQuery.Creator))
        {
            return false;
        }

        if (!MatchesMentionedFilter(issue, _issueQuery.Mentioned))
        {
            return false;
        }

        return !_issueQuery.Since.HasValue || issue.UpdatedAt >= _issueQuery.Since.Value;
    }

    private static bool MatchesStateFilter(string state, string filter)
    {
        return string.IsNullOrWhiteSpace(filter)
            || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesLoginFilter(IEnumerable<string> logins, string? filter)
    {
        return string.IsNullOrWhiteSpace(filter)
            || logins.Any(login => string.Equals(login, filter, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesSingleLoginFilter(string? login, string? filter)
    {
        return string.IsNullOrWhiteSpace(filter)
            || string.Equals(login, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MatchesMentionedFilter(GitHubIssue issue, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        return (!string.IsNullOrWhiteSpace(issue.Body) && issue.Body.Contains($"@{filter}", StringComparison.OrdinalIgnoreCase))
            || issue.Assignees.Any(assignee => string.Equals(assignee.Login, filter, StringComparison.OrdinalIgnoreCase))
            || string.Equals(issue.User?.Login, filter, StringComparison.OrdinalIgnoreCase);
    }

    private string GetIssueAuthorDisplayName(GitHubIssue issue) =>
        UserIdentityNavigationPolicy.CreatePresentation(
            issue.User?.Login,
            displayName: null,
            GetString("Common.UnknownUser", "unknown")).DisplayName;

    private static DateTimeOffset CreateLocalMidnight(DateTimeOffset date)
    {
        DateTime localMidnight = new(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        TimeSpan localOffset = TimeZoneInfo.Local.GetUtcOffset(localMidnight);
        return new DateTimeOffset(localMidnight, localOffset);
    }

    private GitHubIssue ReplaceIssueInCollection(GitHubIssue updatedIssue)
    {
        _suppressSelectionChanged = true;
        try
        {
            int loadedIndex = -1;
            for (int index = 0; index < _loadedIssues.Count; index++)
            {
                if (_loadedIssues[index].Number == updatedIssue.Number)
                {
                    loadedIndex = index;
                    break;
                }
            }

            for (int index = 0; index < Issues.Count; index++)
            {
                if (Issues[index].Number == updatedIssue.Number)
                {
                    GitHubIssue visibleIssue = Issues[index];
                    CopyIssueValues(visibleIssue, updatedIssue);
                    if (loadedIndex >= 0)
                    {
                        _loadedIssues[loadedIndex] = visibleIssue;
                    }

                    return visibleIssue;
                }
            }

            if (loadedIndex >= 0)
            {
                GitHubIssue loadedIssue = _loadedIssues[loadedIndex];
                CopyIssueValues(loadedIssue, updatedIssue);
                return loadedIssue;
            }

            return updatedIssue;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }
    }

    private GitHubIssue ReplaceIssueInCollectionOrAdd(GitHubIssue updatedIssue)
    {
        GitHubIssue displayIssue = ReplaceIssueInCollection(updatedIssue);
        if (_loadedIssues.All(issue => issue.Number != updatedIssue.Number))
        {
            _loadedIssues.Insert(0, displayIssue);
        }

        if (Issues.All(issue => issue.Number != updatedIssue.Number))
        {
            Issues.Insert(0, displayIssue);
            IsEmpty = false;
        }

        return displayIssue;
    }

    private static void CopyIssueValues(GitHubIssue target, GitHubIssue source)
    {
        target.Id = source.Id;
        target.Number = source.Number;
        target.Title = source.Title;
        target.Body = source.Body;
        target.State = source.State;
        target.HtmlUrl = source.HtmlUrl;
        target.RepositoryUrl = source.RepositoryUrl;
        target.Comments = source.Comments;
        target.CreatedAt = source.CreatedAt;
        target.UpdatedAt = source.UpdatedAt;
        target.ClosedAt = source.ClosedAt;
        target.User = source.User;
        target.Assignees = source.Assignees;
        target.Labels = source.Labels;
        target.Milestone = source.Milestone;
        target.Reactions = source.Reactions;
        target.PullRequest = source.PullRequest;
    }

    private void NotifyRepositoryPropertiesChanged()
    {
        OnPropertyChanged(nameof(RepositoryFullName));
        OnPropertyChanged(nameof(PageTitle));
    }

    private void NotifySelectedIssuePropertiesChanged()
    {
        OnPropertyChanged(nameof(HasSelectedIssue));
        OnPropertyChanged(nameof(IsDetailPlaceholderVisible));
        OnPropertyChanged(nameof(IsIssueContentVisible));
        OnPropertyChanged(nameof(SelectedIssueNumberText));
        OnPropertyChanged(nameof(SelectedIssueTitle));
        OnPropertyChanged(nameof(SelectedIssueAuthorDisplayName));
        OnPropertyChanged(nameof(SelectedIssueAuthorLogin));
        OnPropertyChanged(nameof(SelectedIssueAuthorAvatarUrl));
        OnPropertyChanged(nameof(SelectedIssueAuthorAutomationId));
        OnPropertyChanged(nameof(SelectedIssueStateText));
        OnPropertyChanged(nameof(SelectedIssueMetadataText));
        OnPropertyChanged(nameof(SelectedIssueCommentText));
        OnPropertyChanged(nameof(IssueBodyMarkdownSource));
        OnPropertyChanged(nameof(IssueCommentMarkdownSource));
        OnPropertyChanged(nameof(MilestoneTitle));
    }

    private void NotifyInspectorCollectionsChanged()
    {
        OnPropertyChanged(nameof(HasLabels));
        OnPropertyChanged(nameof(HasNoLabels));
        OnPropertyChanged(nameof(HasAssignees));
        OnPropertyChanged(nameof(HasNoAssignees));
        OnPropertyChanged(nameof(MilestoneTitle));
    }

    private void NotifyIssueCommentsChanged()
    {
        OnPropertyChanged(nameof(IsIssueCommentsEmptyVisible));
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
            return "public";
        }

        long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        return userId > 0 ? userId.ToString(CultureInfo.InvariantCulture) : string.Empty;
    }

    private string FormatIssueMetadataSummary(GitHubIssue issue)
    {
        string assignees = issue.Assignees.Length == 0
            ? GetString("RepoIssue.AssigneesNone", "Assignees: none")
            : FormatString(
                "RepoIssue.AssigneesFormat",
                "Assignees: {0}",
                string.Join(", ", issue.Assignees.Select(assignee => $"@{assignee.Login}")));
        string labels = issue.Labels.Length == 0
            ? GetString("RepoIssue.LabelsNone", "Labels: none")
            : FormatString(
                "RepoIssue.LabelsFormat",
                "Labels: {0}",
                string.Join(", ", issue.Labels.Select(label => label.Name)));
        string milestone = issue.Milestone is null
            ? GetString("RepoIssue.MilestoneNone", "Milestone: none")
            : FormatString("RepoIssue.MilestoneFormat", "Milestone: {0}", issue.Milestone.Title);
        return $"{assignees}  •  {labels}  •  {milestone}";
    }

    private string GetIssueStateDisplay(string state)
    {
        return string.Equals(state, "closed", StringComparison.OrdinalIgnoreCase)
            ? GetString("RepoIssue.StateClosed", "Closed")
            : GetString("RepoIssue.StateOpen", "Open");
    }

    private string FormatCommentCount(int count)
    {
        return count == 1
            ? FormatString("RepoIssue.CommentCountSingular", "{0} comment", count)
            : FormatString("RepoIssue.CommentCountPlural", "{0} comments", count);
    }

    public sealed record IssueMetadataDialogData(
        IReadOnlyList<GitHubActor> AvailableAssignees,
        IReadOnlyList<GitHubLabel> AvailableLabels,
        IReadOnlyList<GitHubMilestone> AvailableMilestones);

    private sealed record IssueDetailSnapshot(
        int IssueNumber,
        string StatusText,
        string TitleText,
        string MetaText,
        string MetadataText,
        string ReactionsText,
        string BodyText,
        string CommentDraft,
        string ToggleStateButtonText,
        bool AreActionsEnabled,
        bool IsAddCommentEnabled,
        bool IsCommentsEmptyVisible,
        IReadOnlyList<GitHubIssueComment> Comments);

    public sealed record IssueMetadataUpdate(
        IReadOnlyList<string> Assignees,
        IReadOnlyList<string> Labels,
        int? MilestoneNumber);

}
