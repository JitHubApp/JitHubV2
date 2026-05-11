using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;

namespace JitHub.WinUI.ViewModels.Pages;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class RepoPullRequestPageViewModel : ViewModelBase
{
    private static readonly TimeSpan SelectionLoadDebounce = TimeSpan.FromMilliseconds(150);
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;
    private PullRequestPageNavArg? _navArg;
    private GitHubIssue? _selectedPullRequestIssue;
    private int _detailRequestId;
    private int _listRequestId;
    private bool _suppressSelectionChanged;
    private CancellationTokenSource? _selectionLoadCancellationTokenSource;
    private readonly List<GitHubPullRequest> _loadedPullRequests = [];
    private readonly GitHubPullRequestQueryOptions _pullRequestQuery = new();
    private int _pinnedPullRequestNumber;
    private int _lastFocusedPullRequestNumber;
    private PullRequestDetailSnapshot? _pendingPullRequestSelectionState;
    private bool _isPullRequestCommentSubmissionInProgress;
    private readonly HashSet<long> _inProgressReviewReplyCommentIds = [];

    public RepoPullRequestPageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _gitHubClientService = GetService<IGitHubClientService>();

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
        StatusText = LoadingStatusText;
    }

    public ObservableCollection<GitHubPullRequest> PullRequests { get; } = [];

    public ObservableCollection<GitHubIssueComment> PullRequestComments { get; } = [];

    public ObservableCollection<GitHubCommit> PullRequestCommits { get; } = [];

    public ObservableCollection<PullRequestReviewItem> PullRequestReviews { get; } = [];

    public ObservableCollection<GitHubIssueEvent> PullRequestTimelineEvents { get; } = [];

    public List<QueryOption> StateOptions { get; }

    public List<QueryOption> SortOptions { get; }

    public List<QueryOption> DirectionOptions { get; }

    public string AuthenticatedLogin => _authService.AuthenticatedUser?.Login ?? string.Empty;

    public PullRequestPageNavArg? NavigationArgs => _navArg;

    public GitHubPullRequest? CurrentPullRequest => SelectedPullRequest;

    public GitHubIssue? CurrentPullRequestIssue => _selectedPullRequestIssue;

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
    public partial bool ArePullRequestActionsEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsTogglePullRequestStateEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsMergeEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsPullRequestCommentEnabled { get; set; }

    [ObservableProperty]
    public partial bool IsPullRequestCommentsEmptyVisible { get; set; }

    [ObservableProperty]
    public partial bool IsPullRequestCommitsEmptyVisible { get; set; }

    [ObservableProperty]
    public partial bool IsPullRequestReviewsEmptyVisible { get; set; }

    [ObservableProperty]
    public partial bool IsPullRequestTimelineEmptyVisible { get; set; }

    public async Task InitializeAsync(PullRequestPageNavArg? navArg)
    {
        _navArg = navArg;
        _lastFocusedPullRequestNumber = 0;
        _pendingPullRequestSelectionState = null;
        _loadedPullRequests.Clear();
        PullRequests.Clear();
        SetSelectedPullRequest(null);
        ResetPullRequestDetails();

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
        await LoadPullRequestsAsync(navArg.PullRequestId);
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
        if (_navArg is null || !TryGetActiveToken(out string token))
        {
            return null;
        }

        try
        {
            IReadOnlyList<GitHubBranch> branches = await _gitHubClientService.GetBranchesAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name);
            string defaultBase = _navArg.Repo.DefaultBranch;
            string defaultHead = branches
                .FirstOrDefault(branch => !string.Equals(branch.Name, defaultBase, StringComparison.OrdinalIgnoreCase))
                ?.Name ?? string.Empty;
            return new PullRequestCreateDialogData(defaultHead, defaultBase);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            StatusText = ex.Message;
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
            await LoadPullRequestsAsync(pullRequest.Number);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            StatusText = GetString("RepoPullRequest.CreateNetworkError", "JitHub could not reach GitHub to create this pull request.");
        }
    }

    public async Task UpdateSelectedPullRequestAsync(string title, string? body)
    {
        if (_navArg is null || SelectedPullRequest is null || !ArePullRequestActionsEnabled || !TryGetActiveToken(out string token))
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
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = GetString("RepoPullRequest.UpdateNetworkError", "JitHub could not reach GitHub to update this pull request.");
        }
    }

    public async Task<PullRequestMetadataDialogData?> LoadSelectedPullRequestMetadataDialogDataAsync()
    {
        if (_navArg is null
            || SelectedPullRequest is null
            || _selectedPullRequestIssue is null
            || !ArePullRequestActionsEnabled
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
            Task<IReadOnlyList<GitHubActor>> reviewersTask = _gitHubClientService.GetCollaboratorsAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name);
            Task<IReadOnlyList<GitHubActor>> assigneesTask = _gitHubClientService.GetAssigneesAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name);
            Task<IReadOnlyList<GitHubLabel>> labelsTask = _gitHubClientService.GetLabelsAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name);
            Task<IReadOnlyList<GitHubMilestone>> milestonesTask = _gitHubClientService.GetMilestonesAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name);
            await Task.WhenAll(reviewersTask, assigneesTask, labelsTask, milestonesTask);

            if (requestId != _detailRequestId || SelectedPullRequest?.Number != pullRequestNumber)
            {
                return null;
            }

            StatusText = previousStatusText;

            return new PullRequestMetadataDialogData(
                await reviewersTask,
                await assigneesTask,
                await labelsTask,
                await milestonesTask);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (requestId == _detailRequestId && SelectedPullRequest?.Number == pullRequestNumber)
            {
                StatusText = ex.Message;
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
            || !ArePullRequestActionsEnabled
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
                _authService.SignOut();
                return;
            }
            catch (GitHubApiException ex)
            {
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
                        ex.Message);
                }

                return;
            }
            catch (HttpRequestException)
            {
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
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
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
            || !ArePullRequestActionsEnabled
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
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
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
            if (IsSelectedPullRequest(currentPullRequest))
            {
                PullRequestCommentDraft = string.Empty;
                StatusText = FormatString("RepoPullRequest.AddedCommentStatus", "Comment added to pull request #{0}.", currentPullRequest.Number);
            }

            await TryRefreshPullRequestSelectionAfterMutationAsync(
                currentPullRequest,
                token,
                GetString("RepoPullRequest.CommentRefreshError", "Comment added, but JitHub could not refresh pull request details."));
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = GetString("RepoPullRequest.CommentNetworkError", "JitHub could not reach GitHub to post this comment.");
        }
        finally
        {
            _isPullRequestCommentSubmissionInProgress = false;
            if (IsSelectedPullRequest(currentPullRequest))
            {
                UpdatePullRequestCommentEnabledState();
            }
        }
    }

    public async Task<IReadOnlyList<GitHubReaction>?> GetSelectedPullRequestReactionsAsync()
    {
        if (_navArg is null || SelectedPullRequest is null || !ArePullRequestActionsEnabled || !TryGetActiveToken(out string token))
        {
            return null;
        }

        try
        {
            return await _gitHubClientService.GetIssueReactionsAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                SelectedPullRequest.Number);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            StatusText = GetString("RepoPullRequest.ReactionsNetworkError", "JitHub could not reach GitHub to update reactions.");
        }

        return null;
    }

    public async Task<IReadOnlyList<GitHubReaction>?> GetPullRequestCommentReactionsAsync(long commentId)
    {
        if (_navArg is null || SelectedPullRequest is null || !ArePullRequestActionsEnabled || !TryGetActiveToken(out string token))
        {
            return null;
        }

        try
        {
            return await _gitHubClientService.GetIssueCommentReactionsAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                commentId);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            StatusText = GetString("RepoPullRequest.ReactionsNetworkError", "JitHub could not reach GitHub to update reactions.");
        }

        return null;
    }

    public async Task<IReadOnlyList<GitHubReaction>?> GetReviewCommentReactionsAsync(long commentId)
    {
        if (_navArg is null || SelectedPullRequest is null || !ArePullRequestActionsEnabled || !TryGetActiveToken(out string token))
        {
            return null;
        }

        try
        {
            return await _gitHubClientService.GetPullRequestReviewCommentReactionsAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                commentId);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            StatusText = ex.Message;
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

    public async Task ReplyToReviewCommentAsync(PullRequestReviewThreadItem threadItem)
    {
        if (_navArg is null || SelectedPullRequest is null || !ArePullRequestActionsEnabled || !TryGetActiveToken(out string token))
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
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
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

    public async Task MergeSelectedPullRequestAsync(
        string mergeMethod,
        string operationTitle,
        string? commitTitle,
        string? commitMessage)
    {
        if (_navArg is null || SelectedPullRequest is null || !ArePullRequestActionsEnabled || !IsMergeEnabled || !TryGetActiveToken(out string token))
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
                if (IsSelectedPullRequest(currentPullRequest))
                {
                    StatusText = string.IsNullOrWhiteSpace(mergeResult.Message)
                        ? GetString("RepoPullRequest.MergeDidNotCompleteStatus", "GitHub did not merge this pull request.")
                        : mergeResult.Message;
                }

                return;
            }

            if (IsSelectedPullRequest(currentPullRequest))
            {
                StatusText = string.IsNullOrWhiteSpace(mergeResult.Message)
                    ? FormatString("RepoPullRequest.MergedStatus", "Merged pull request #{0}.", currentPullRequest.Number)
                    : mergeResult.Message;
            }

            await TryRefreshPullRequestSelectionAfterMutationAsync(
                currentPullRequest,
                token,
                GetString("RepoPullRequest.MergeRefreshError", "Pull request merged, but JitHub could not refresh pull request details."));
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (!IsSelectedPullRequest(currentPullRequest))
            {
                return;
            }

            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
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
        if (value is null)
        {
            _pendingPullRequestSelectionState = null;
            _ = ShowPullRequestAsync(null);
            return;
        }

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
        CancellationTokenSource cancellationTokenSource = new();
        _selectionLoadCancellationTokenSource = cancellationTokenSource;
        _ = ShowPullRequestAfterSelectionDelayAsync(value, cancellationTokenSource.Token);
    }

    private async Task LoadPullRequestsAsync(int preferredPullRequestNumber = 0, bool preservePreferredPullRequestOutsideQuery = true)
    {
        if (_navArg is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        PullRequestPageNavArg navigationArgs = _navArg;

        int requestId = ++_listRequestId;
        bool previousArePullRequestActionsEnabled = ArePullRequestActionsEnabled;
        bool previousIsTogglePullRequestStateEnabled = IsTogglePullRequestStateEnabled;
        bool previousIsPullRequestCommentEnabled = IsPullRequestCommentEnabled;
        bool previousIsMergeEnabled = IsMergeEnabled;
        string? preferredPullRequestLoadFailureStatus = null;
        StatusText = LoadingStatusText;
        CancelPendingSelectionLoad();
        _pendingPullRequestSelectionState = null;
        _detailRequestId++;
        ArePullRequestActionsEnabled = false;
        IsTogglePullRequestStateEnabled = false;
        IsPullRequestCommentEnabled = false;
        IsMergeEnabled = false;

        try
        {
            IReadOnlyList<GitHubPullRequest> pullRequests = await _gitHubClientService.GetPullRequestsAsync(
                token,
                navigationArgs.Repo.Owner.Login,
                navigationArgs.Repo.Name,
                50,
                queryOptions: _pullRequestQuery);

            if (requestId != _listRequestId)
            {
                return;
            }

            List<GitHubPullRequest> loadedPullRequests = [.. pullRequests];
            int pullRequestNumberToSelect = preferredPullRequestNumber > 0 ? preferredPullRequestNumber : navigationArgs.PullRequestId;
            int pinnedPullRequestNumber = 0;
            if (pullRequestNumberToSelect > 0)
            {
                GitHubPullRequest? selectedPullRequest = loadedPullRequests.FirstOrDefault(pullRequest => pullRequest.Number == pullRequestNumberToSelect);
                if (selectedPullRequest is null)
                {
                    try
                    {
                        selectedPullRequest = await _gitHubClientService.GetPullRequestAsync(
                            token,
                            navigationArgs.Repo.Owner.Login,
                            navigationArgs.Repo.Name,
                            pullRequestNumberToSelect);
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

                    if (requestId != _listRequestId)
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

            if (requestId != _listRequestId)
            {
                return;
            }

            _pinnedPullRequestNumber = pinnedPullRequestNumber;
            _loadedPullRequests.Clear();
            _loadedPullRequests.AddRange(loadedPullRequests);
            await ApplyPullRequestListFilterAsync(pullRequestNumberToSelect);
            if (!string.IsNullOrWhiteSpace(preferredPullRequestLoadFailureStatus) && requestId == _listRequestId)
            {
                StatusText = preferredPullRequestLoadFailureStatus;
            }
        }
        catch (GitHubAuthenticationException)
        {
            if (requestId != _listRequestId)
            {
                return;
            }

            ArePullRequestActionsEnabled = previousArePullRequestActionsEnabled;
            IsTogglePullRequestStateEnabled = previousIsTogglePullRequestStateEnabled;
            IsPullRequestCommentEnabled = previousIsPullRequestCommentEnabled;
            IsMergeEnabled = previousIsMergeEnabled;
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (requestId != _listRequestId)
            {
                return;
            }

            ArePullRequestActionsEnabled = previousArePullRequestActionsEnabled;
            IsTogglePullRequestStateEnabled = previousIsTogglePullRequestStateEnabled;
            IsPullRequestCommentEnabled = previousIsPullRequestCommentEnabled;
            IsMergeEnabled = previousIsMergeEnabled;
            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            if (requestId != _listRequestId)
            {
                return;
            }

            ArePullRequestActionsEnabled = previousArePullRequestActionsEnabled;
            IsTogglePullRequestStateEnabled = previousIsTogglePullRequestStateEnabled;
            IsPullRequestCommentEnabled = previousIsPullRequestCommentEnabled;
            IsMergeEnabled = previousIsMergeEnabled;
            StatusText = GetString("RepoPullRequest.LoadNetworkError", "JitHub could not reach GitHub to load pull requests.");
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

        try
        {
            if (!preserveStatusText)
            {
                StatusText = FormatString("RepoPullRequest.LoadDetailStatus", "Loading pull request #{0}...", pullRequest.Number);
            }

            Task<GitHubPullRequest> pullRequestTask = _gitHubClientService.GetPullRequestAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                pullRequest.Number);
            Task<GitHubIssue> issueTask = _gitHubClientService.GetIssueAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                pullRequest.Number);
            Task<IReadOnlyList<GitHubIssueComment>> commentsTask = _gitHubClientService.GetIssueCommentsAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                pullRequest.Number);
            Task<IReadOnlyList<GitHubIssueEvent>> eventsTask = _gitHubClientService.GetIssueEventsAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                pullRequest.Number);
            Task<IReadOnlyList<GitHubPullRequestReview>> reviewsTask = _gitHubClientService.GetPullRequestReviewsAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                pullRequest.Number);
            Task<IReadOnlyList<GitHubPullRequestReviewComment>> reviewCommentsTask = _gitHubClientService.GetPullRequestReviewCommentsAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                pullRequest.Number);
            Task<IReadOnlyList<GitHubCommit>> commitsTask = _gitHubClientService.GetPullRequestCommitsAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                pullRequest.Number);
            await Task.WhenAll(pullRequestTask, issueTask, commentsTask, eventsTask, reviewsTask, reviewCommentsTask, commitsTask);

            GitHubPullRequest latestPullRequest = await pullRequestTask;
            GitHubIssue latestIssue = await issueTask;
            IReadOnlyList<GitHubIssueComment> comments = await commentsTask;
            IReadOnlyList<GitHubIssueEvent> timelineEvents = await eventsTask;
            IReadOnlyList<GitHubPullRequestReview> reviews = await reviewsTask;
            IReadOnlyList<GitHubPullRequestReviewComment> reviewComments = await reviewCommentsTask;
            IReadOnlyList<GitHubCommit> commits = await commitsTask;

            if (requestId != _detailRequestId)
            {
                return;
            }

            ReplacePullRequestInCollection(latestPullRequest);
            _selectedPullRequestIssue = latestIssue;
            SetSelectedPullRequest(latestPullRequest);
            PopulatePullRequest(latestPullRequest);
            if (preserveCurrentState)
            {
                preservedCommentDraft = PullRequestCommentDraft;
                preservedReplyDrafts = CaptureReviewReplyDrafts();
            }

            PullRequestComments.Clear();
            PullRequestCommits.Clear();
            PullRequestReviews.Clear();
            PullRequestTimelineEvents.Clear();

            foreach (GitHubIssueComment comment in comments)
            {
                PullRequestComments.Add(comment);
            }

            IReadOnlyList<PullRequestReviewItem> reviewItems = BuildPullRequestReviewItems(reviews, reviewComments);
            RestoreReviewReplyDrafts(reviewItems, preservedReplyDrafts);
            ApplyReviewReplyInProgressState(reviewItems);
            foreach (PullRequestReviewItem reviewItem in reviewItems)
            {
                PullRequestReviews.Add(reviewItem);
            }

            foreach (GitHubIssueEvent timelineEvent in timelineEvents.OrderBy(item => item.CreatedAt))
            {
                PullRequestTimelineEvents.Add(timelineEvent);
            }

            foreach (GitHubCommit commit in commits)
            {
                PullRequestCommits.Add(commit);
            }

            IsPullRequestCommentsEmptyVisible = PullRequestComments.Count == 0;
            IsPullRequestCommitsEmptyVisible = PullRequestCommits.Count == 0;
            IsPullRequestReviewsEmptyVisible = PullRequestReviews.Count == 0;
            IsPullRequestTimelineEmptyVisible = PullRequestTimelineEvents.Count == 0;
            PullRequestCommentDraft = preserveCurrentState ? preservedCommentDraft : string.Empty;
            StatusText = preserveStatusText
                ? preservedStatusText
                : FormatString("RepoPullRequest.LoadedStatus", "Pull request #{0} loaded.", latestPullRequest.Number);
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
                StatusText = ex.Message;
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
    }

    private void PopulatePullRequest(GitHubPullRequest pullRequest)
    {
        ArePullRequestActionsEnabled = true;
        IsTogglePullRequestStateEnabled = CanTogglePullRequestState(pullRequest);
        UpdatePullRequestCommentEnabledState();
        IsMergeEnabled = CanMergePullRequest(pullRequest);
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
    }

    private void ResetPullRequestDetails()
    {
        _detailRequestId++;
        _selectedPullRequestIssue = null;
        ArePullRequestActionsEnabled = false;
        IsTogglePullRequestStateEnabled = false;
        IsMergeEnabled = false;
        IsPullRequestCommentEnabled = false;
        PullRequestTitleText = GetString("RepoPullRequest.SelectTitle", "Select a pull request");
        PullRequestMetaText = GetString("RepoPullRequest.SelectSubtitle", "Choose a pull request to inspect its details.");
        PullRequestMetadataText = string.Empty;
        PullRequestReactionsText = GetString("RepoPullRequest.NoReactions", "Reactions: none");
        MergeStatusText = GetString("RepoPullRequest.MergeDetailsPlaceholder", "Merge details will appear here.");
        PullRequestBodyText = string.Empty;
        PullRequestCommentDraft = string.Empty;
        PullRequestComments.Clear();
        PullRequestCommits.Clear();
        PullRequestReviews.Clear();
        PullRequestTimelineEvents.Clear();
        IsPullRequestCommentsEmptyVisible = false;
        IsPullRequestCommitsEmptyVisible = false;
        IsPullRequestReviewsEmptyVisible = false;
        IsPullRequestTimelineEmptyVisible = false;
        TogglePullRequestStateButtonText = GetString("RepoPullRequest.CloseButton", "Close pull request");
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
        PullRequestBodyText = GetString("RepoPullRequest.BodyLoading", "Loading pull request details...");
        PullRequestCommentDraft = string.Empty;
        PullRequestComments.Clear();
        PullRequestCommits.Clear();
        PullRequestReviews.Clear();
        PullRequestTimelineEvents.Clear();
        IsPullRequestCommentsEmptyVisible = false;
        IsPullRequestCommitsEmptyVisible = false;
        IsPullRequestReviewsEmptyVisible = false;
        IsPullRequestTimelineEmptyVisible = false;
        TogglePullRequestStateButtonText = GetTogglePullRequestStateButtonText(pullRequest);
        StatusText = FormatString("RepoPullRequest.LoadDetailStatus", "Loading pull request #{0}...", pullRequest.Number);
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
            PullRequestTimelineEvents.ToArray());
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
        PullRequestComments.Clear();
        foreach (GitHubIssueComment comment in snapshot.Comments)
        {
            PullRequestComments.Add(comment);
        }

        PullRequestCommits.Clear();
        foreach (GitHubCommit commit in snapshot.Commits)
        {
            PullRequestCommits.Add(commit);
        }

        PullRequestReviews.Clear();
        foreach (PullRequestReviewItem review in snapshot.Reviews)
        {
            PullRequestReviews.Add(review);
        }

        PullRequestTimelineEvents.Clear();
        foreach (GitHubIssueEvent timelineEvent in snapshot.TimelineEvents)
        {
            PullRequestTimelineEvents.Add(timelineEvent);
        }

        IsPullRequestCommentsEmptyVisible = snapshot.IsCommentsEmptyVisible;
        IsPullRequestCommitsEmptyVisible = snapshot.IsCommitsEmptyVisible;
        IsPullRequestReviewsEmptyVisible = snapshot.IsReviewsEmptyVisible;
        IsPullRequestTimelineEmptyVisible = snapshot.IsTimelineEmptyVisible;
    }

    private async Task ApplyPullRequestListFilterAsync(int preferredPullRequestNumber, bool refreshSelectionDetails = true)
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
            PullRequests.Clear();
            foreach (GitHubPullRequest pullRequest in visiblePullRequests)
            {
                PullRequests.Add(pullRequest);
            }

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
            await ShowPullRequestAsync(selectedPullRequest, preserveCurrentState: refreshSelectionDetails && !selectionChanged);
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
        GitHubPullRequest refreshedPullRequest;
        try
        {
            refreshedPullRequest = await _gitHubClientService.GetPullRequestAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                pullRequest.Number);
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
        }
        _suppressSelectionChanged = false;
    }

    private async Task ShowPullRequestAfterSelectionDelayAsync(GitHubPullRequest pullRequest, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SelectionLoadDebounce, cancellationToken);
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
        await ShowPullRequestAsync(pullRequest);
    }

    private void CancelPendingSelectionLoad()
    {
        _selectionLoadCancellationTokenSource?.Cancel();
        _selectionLoadCancellationTokenSource?.Dispose();
        _selectionLoadCancellationTokenSource = null;
    }

    private static string? NormalizeFilterText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private void UpdatePullRequestCommentEnabledState()
    {
        IsPullRequestCommentEnabled = ArePullRequestActionsEnabled && !_isPullRequestCommentSubmissionInProgress;
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
        Dictionary<long, PullRequestReviewItem> reviewLookup = reviews.ToDictionary(
            review => review.Id,
            review => new PullRequestReviewItem(review, FormatReviewState(review.State), PendingReviewText, UnknownUserText, OpenButtonText));
        Dictionary<long, PullRequestReviewThreadItem> threadLookup = [];
        List<PullRequestReviewItem> syntheticReviews = [];

        foreach (GitHubPullRequestReviewComment comment in reviewComments.OrderBy(item => item.CreatedAt))
        {
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
                ReplyPrefixText);
            threadLookup[comment.Id] = threadItem;

            if (comment.PullRequestReviewId.HasValue && reviewLookup.TryGetValue(comment.PullRequestReviewId.Value, out PullRequestReviewItem? reviewItem))
            {
                reviewItem.Threads.Add(threadItem);
                continue;
            }

            PullRequestReviewItem syntheticReview = PullRequestReviewItem.CreateSynthetic(comment, ReviewCommentStateText, UnknownUserText, OpenButtonText);
            syntheticReview.Threads.Add(threadItem);
            syntheticReviews.Add(syntheticReview);
        }

        return reviewLookup.Values
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
        if (_navArg is null || SelectedPullRequest is null || !TryGetActiveToken(out string token))
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
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (!IsSelectedPullRequest(targetPullRequest))
            {
                return;
            }

            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            if (!IsSelectedPullRequest(targetPullRequest))
            {
                return;
            }

            StatusText = GetString("RepoPullRequest.ReactionsNetworkError", "JitHub could not reach GitHub to update reactions.");
        }
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

    private bool CanMergePullRequest(GitHubPullRequest pullRequest)
    {
        return string.Equals(pullRequest.State, "open", StringComparison.OrdinalIgnoreCase)
            && !pullRequest.Merged
            && !pullRequest.Draft;
    }

    private bool CanTogglePullRequestState(GitHubPullRequest pullRequest)
    {
        return !pullRequest.Merged;
    }

    private string GetTogglePullRequestStateButtonText(GitHubPullRequest pullRequest)
    {
        return pullRequest.Merged
            ? GetString("RepoPullRequest.MergedButton", "Pull request merged")
            : string.Equals(pullRequest.State, "closed", StringComparison.OrdinalIgnoreCase)
                ? GetString("RepoPullRequest.ReopenButton", "Reopen pull request")
                : GetString("RepoPullRequest.CloseButton", "Close pull request");
    }

    public sealed record PullRequestCreateDialogData(string DefaultHead, string DefaultBase);

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
        IReadOnlyList<GitHubIssueEvent> TimelineEvents);

    public sealed record PullRequestMetadataDialogData(
        IReadOnlyList<GitHubActor> AvailableReviewers,
        IReadOnlyList<GitHubActor> AvailableAssignees,
        IReadOnlyList<GitHubLabel> AvailableLabels,
        IReadOnlyList<GitHubMilestone> AvailableMilestones);

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
    public sealed partial class PullRequestReviewItem
    {
        public PullRequestReviewItem(
            GitHubPullRequestReview review,
            string stateText,
            string pendingText,
            string unknownUserText,
            string openButtonText)
        {
            ReviewerLogin = string.IsNullOrWhiteSpace(review.User.Login) ? unknownUserText : review.User.Login;
            StateText = stateText;
            SubmittedAtText = review.SubmittedAt?.LocalDateTime.ToString("g") ?? pendingText;
            BodyText = review.Body ?? string.Empty;
            HtmlUrl = review.HtmlUrl;
            OpenButtonText = openButtonText;
            SortKey = review.SubmittedAt ?? DateTimeOffset.MinValue;
        }

        public string ReviewerLogin { get; }

        public string StateText { get; }

        public string SubmittedAtText { get; }

        public string BodyText { get; }

        public string HtmlUrl { get; }

        public string OpenButtonText { get; }

        public DateTimeOffset SortKey { get; }

        public ObservableCollection<PullRequestReviewThreadItem> Threads { get; } = [];

        public static PullRequestReviewItem CreateSynthetic(
            GitHubPullRequestReviewComment comment,
            string stateText,
            string unknownUserText,
            string openButtonText)
        {
            return new PullRequestReviewItem(comment, stateText, unknownUserText, openButtonText);
        }

        private PullRequestReviewItem(
            GitHubPullRequestReviewComment comment,
            string stateText,
            string unknownUserText,
            string openButtonText)
        {
            ReviewerLogin = string.IsNullOrWhiteSpace(comment.User.Login) ? unknownUserText : comment.User.Login;
            StateText = stateText;
            SubmittedAtText = comment.CreatedAt.LocalDateTime.ToString("g");
            BodyText = string.Empty;
            HtmlUrl = comment.HtmlUrl;
            OpenButtonText = openButtonText;
            SortKey = comment.CreatedAt;
        }
    }

    [WinRT.GeneratedBindableCustomProperty]
    public sealed partial class PullRequestReviewThreadItem : ObservableObject
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
            string replyPrefixText)
        {
            CommentId = comment.Id;
            CommentUserLogin = string.IsNullOrWhiteSpace(comment.User.Login) ? unknownUserText : comment.User.Login;
            CommentBody = comment.Body;
            CommentHtmlUrl = comment.HtmlUrl;
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

        public string CommentUserLogin { get; }

        public string CommentBody { get; }

        public string CommentHtmlUrl { get; }

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

        public void AddReply(
            GitHubPullRequestReviewComment reply,
            string reactionText,
            string unknownUserText,
            string replyPrefixText,
            string openButtonText,
            string reactionsButtonText)
        {
            Replies.Add(new PullRequestReviewReplyItem(reply, reactionText, unknownUserText, replyPrefixText, openButtonText, reactionsButtonText));
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
            string reactionsButtonText)
        {
            Id = comment.Id;
            UserLogin = string.IsNullOrWhiteSpace(comment.User.Login) ? unknownUserText : comment.User.Login;
            CreatedAtText = comment.CreatedAt.LocalDateTime.ToString("g");
            HtmlUrl = comment.HtmlUrl;
            Body = comment.Body;
            ReactionText = reactionText;
            ReplyPrefixText = replyPrefixText;
            OpenButtonText = openButtonText;
            ReactionsButtonText = reactionsButtonText;
        }

        public long Id { get; }

        public string UserLogin { get; }

        public string CreatedAtText { get; }

        public string HtmlUrl { get; }

        public string Body { get; }

        public string ReactionText { get; }

        public string ReplyPrefixText { get; }

        public string OpenButtonText { get; }

        public string ReactionsButtonText { get; }
    }
}
