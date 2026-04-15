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

public sealed partial class RepoIssuePageViewModel : ViewModelBase
{
    private static readonly TimeSpan SelectionLoadDebounce = TimeSpan.FromMilliseconds(150);
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubClientService _gitHubClientService;
    private IssueNavArg? _navArg;
    private int _detailRequestId;
    private int _listRequestId;
    private bool _suppressSelectionChanged;
    private CancellationTokenSource? _selectionLoadCancellationTokenSource;
    private readonly List<GitHubIssue> _loadedIssues = [];
    private readonly GitHubIssueQueryOptions _issueQuery = new();
    private int _pinnedIssueNumber;
    private int _lastFocusedIssueNumber;
    private IssueDetailSnapshot? _pendingIssueSelectionState;
    private bool _isIssueCommentSubmissionInProgress;

    public RepoIssuePageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _gitHubClientService = GetService<IGitHubClientService>();

        StateOptions =
        [
            new QueryOption("open", GetString("RepoIssue.StateOpen", "Open")),
            new QueryOption("closed", GetString("RepoIssue.StateClosed", "Closed")),
            new QueryOption("all", GetString("RepoIssue.StateAll", "All"))
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

    public IReadOnlyList<QueryOption> StateOptions { get; }

    public IReadOnlyList<QueryOption> SortOptions { get; }

    public IReadOnlyList<QueryOption> DirectionOptions { get; }

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
    public partial QueryOption? SelectedStateOption { get; set; }

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
    public partial bool IsIssueCommentsEmptyVisible { get; set; }

    public async Task InitializeAsync(IssueNavArg? navArg)
    {
        _navArg = navArg;
        _lastFocusedIssueNumber = 0;
        _pendingIssueSelectionState = null;
        _loadedIssues.Clear();
        Issues.Clear();
        SetSelectedIssue(null);
        ResetIssueDetails();

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

        ResetFilters();
        await LoadIssuesAsync(navArg.IssueId);
    }

    public async Task ReloadAsync()
    {
        if (Issues.Count == 0 && _lastFocusedIssueNumber > 0)
        {
            await LoadIssuesAsync(_lastFocusedIssueNumber, preservePreferredIssueOutsideQuery: false);
            return;
        }

        await LoadIssuesAsync(SelectedIssue?.Number ?? _pinnedIssueNumber);
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
        if (_navArg is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        try
        {
            StatusText = GetString("RepoIssue.CreatingStatus", "Creating issue...");
            GitHubIssue issue = await _gitHubClientService.CreateIssueAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                title,
                body);
            StatusText = FormatString("RepoIssue.CreatedStatus", "Created issue #{0}.", issue.Number);
            await LoadIssuesAsync(issue.Number);
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
            StatusText = GetString("RepoIssue.CreateNetworkError", "JitHub could not reach GitHub to create this issue.");
        }
    }

    public async Task UpdateSelectedIssueAsync(string title, string? body)
    {
        if (_navArg is null || SelectedIssue is null || !AreIssueActionsEnabled || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubIssue currentIssue = SelectedIssue;

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
            await TryRefreshIssueSelectionAfterMutationAsync(
                updatedIssue,
                token,
                GetString("RepoIssue.UpdateRefreshError", "Issue updated, but JitHub could not refresh issue details."));
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = GetString("RepoIssue.UpdateNetworkError", "JitHub could not reach GitHub to update this issue.");
        }
    }

    public async Task<IssueMetadataDialogData?> LoadSelectedIssueMetadataDialogDataAsync()
    {
        if (_navArg is null || SelectedIssue is null || !AreIssueActionsEnabled || !TryGetActiveToken(out string token))
        {
            return null;
        }

        int requestId = _detailRequestId;
        int issueNumber = SelectedIssue.Number;
        string previousStatusText = StatusText;
        try
        {
            StatusText = FormatString("RepoIssue.LoadMetadataStatus", "Loading issue #{0} metadata...", issueNumber);
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
            await Task.WhenAll(assigneesTask, labelsTask, milestonesTask);

            if (requestId != _detailRequestId || SelectedIssue?.Number != issueNumber)
            {
                return null;
            }

            StatusText = previousStatusText;

            return new IssueMetadataDialogData(
                assigneesTask.Result,
                labelsTask.Result,
                milestonesTask.Result);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (requestId == _detailRequestId && SelectedIssue?.Number == issueNumber)
            {
                StatusText = ex.Message;
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
        if (_navArg is null || SelectedIssue is null || !AreIssueActionsEnabled || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubIssue currentIssue = SelectedIssue;

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
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = GetString("RepoIssue.MetadataNetworkError", "JitHub could not reach GitHub to update this issue metadata.");
        }
    }

    public async Task ToggleSelectedIssueStateAsync()
    {
        if (_navArg is null || SelectedIssue is null || !AreIssueActionsEnabled || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubIssue currentIssue = SelectedIssue;

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
            await TryRefreshIssueSelectionAfterMutationAsync(
                updatedIssue,
                token,
                GetString("RepoIssue.StateRefreshError", "Issue state updated, but JitHub could not refresh issue details."));
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
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
            if (IsSelectedIssue(currentIssue))
            {
                IssueCommentDraft = string.Empty;
                StatusText = FormatString("RepoIssue.AddedCommentStatus", "Comment added to issue #{0}.", currentIssue.Number);
            }

            await TryRefreshIssueSelectionAfterMutationAsync(
                currentIssue,
                token,
                GetString("RepoIssue.CommentRefreshError", "Comment added, but JitHub could not refresh issue details."));
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = GetString("RepoIssue.CommentNetworkError", "JitHub could not reach GitHub to post this comment.");
        }
        finally
        {
            _isIssueCommentSubmissionInProgress = false;
            if (IsSelectedIssue(currentIssue))
            {
                UpdateIssueCommentEnabledState();
            }
        }
    }

    public async Task<IReadOnlyList<GitHubReaction>?> GetSelectedIssueReactionsAsync()
    {
        if (_navArg is null || SelectedIssue is null || !AreIssueActionsEnabled || !TryGetActiveToken(out string token))
        {
            return null;
        }

        try
        {
            return await _gitHubClientService.GetIssueReactionsAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                SelectedIssue.Number);
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
            StatusText = GetString("RepoIssue.ReactionsNetworkError", "JitHub could not reach GitHub to update reactions.");
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
            StatusText = GetString("RepoIssue.ReactionsNetworkError", "JitHub could not reach GitHub to update reactions.");
        }

        return null;
    }

    public async Task ApplySelectedIssueReactionSelectionAsync(
        HashSet<string> selectedContents,
        Dictionary<string, long> existingReactionIds)
    {
        if (_navArg is null || SelectedIssue is null || !AreIssueActionsEnabled || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubIssue currentIssue = SelectedIssue;

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

            await RefreshIssueSelectionAsync(_loadedIssues.FirstOrDefault(issue => issue.Number == currentIssue.Number) ?? currentIssue, token);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
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
        if (_navArg is null || SelectedIssue is null || !AreIssueActionsEnabled || !TryGetActiveToken(out string token))
        {
            return;
        }

        GitHubIssue currentIssue = SelectedIssue;

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

            await RefreshIssueSelectionAsync(_loadedIssues.FirstOrDefault(issue => issue.Number == currentIssue.Number) ?? currentIssue, token);
        }
        catch (GitHubAuthenticationException)
        {
            _authService.SignOut();
        }
        catch (GitHubApiException ex)
        {
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            if (!IsSelectedIssue(currentIssue))
            {
                return;
            }

            StatusText = GetString("RepoIssue.ReactionsNetworkError", "JitHub could not reach GitHub to update reactions.");
        }
    }

    partial void OnSelectedIssueChanged(GitHubIssue? value)
    {
        if (_suppressSelectionChanged)
        {
            return;
        }

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
        CancellationTokenSource cancellationTokenSource = new();
        _selectionLoadCancellationTokenSource = cancellationTokenSource;
        _ = ShowIssueAfterSelectionDelayAsync(value, cancellationTokenSource.Token);
    }

    private async Task LoadIssuesAsync(int preferredIssueNumber = 0, bool preservePreferredIssueOutsideQuery = true)
    {
        if (_navArg is null || !TryGetActiveToken(out string token))
        {
            return;
        }

        IssueNavArg navigationArgs = _navArg;

        int requestId = ++_listRequestId;
        bool previousAreIssueActionsEnabled = AreIssueActionsEnabled;
        bool previousIsAddCommentEnabled = IsAddCommentEnabled;
        string? preferredIssueLoadFailureStatus = null;
        StatusText = LoadingStatusText;
        CancelPendingSelectionLoad();
        _pendingIssueSelectionState = null;
        _detailRequestId++;
        AreIssueActionsEnabled = false;
        IsAddCommentEnabled = false;

        try
        {
            IReadOnlyList<GitHubIssue> issues = await _gitHubClientService.GetIssuesAsync(
                token,
                navigationArgs.Repo.Owner.Login,
                navigationArgs.Repo.Name,
                50,
                queryOptions: _issueQuery);

            if (requestId != _listRequestId)
            {
                return;
            }

            List<GitHubIssue> loadedIssues = [.. issues];
            int issueNumberToSelect = preferredIssueNumber > 0 ? preferredIssueNumber : navigationArgs.IssueId;
            int pinnedIssueNumber = 0;
            if (issueNumberToSelect > 0)
            {
                GitHubIssue? selectedIssue = loadedIssues.FirstOrDefault(issue => issue.Number == issueNumberToSelect);
                if (selectedIssue is null)
                {
                    try
                    {
                        selectedIssue = await _gitHubClientService.GetIssueAsync(
                            token,
                            navigationArgs.Repo.Owner.Login,
                            navigationArgs.Repo.Name,
                            issueNumberToSelect);
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
            await ApplyIssueListFilterAsync(issueNumberToSelect);
            if (!string.IsNullOrWhiteSpace(preferredIssueLoadFailureStatus) && requestId == _listRequestId)
            {
                StatusText = preferredIssueLoadFailureStatus;
            }
        }
        catch (GitHubAuthenticationException)
        {
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
            if (requestId != _listRequestId)
            {
                return;
            }

            AreIssueActionsEnabled = previousAreIssueActionsEnabled;
            IsAddCommentEnabled = previousIsAddCommentEnabled;
            StatusText = ex.Message;
        }
        catch (HttpRequestException)
        {
            if (requestId != _listRequestId)
            {
                return;
            }

            AreIssueActionsEnabled = previousAreIssueActionsEnabled;
            IsAddCommentEnabled = previousIsAddCommentEnabled;
            StatusText = GetString("RepoIssue.LoadNetworkError", "JitHub could not reach GitHub to load issues.");
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
            ResetIssueDetails();
            return;
        }

        IssueNavArg navigationArgs = _navArg;

        if (!preserveCurrentState)
        {
            PrepareIssueForSelectionLoad(issue);
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
                StatusText = FormatString("RepoIssue.LoadIssueStatus", "Loading issue #{0}...", issue.Number);
            }

            GitHubIssue latestIssue = await _gitHubClientService.GetIssueAsync(
                token,
                navigationArgs.Repo.Owner.Login,
                navigationArgs.Repo.Name,
                issue.Number);
            IReadOnlyList<GitHubIssueComment> comments = await _gitHubClientService.GetIssueCommentsAsync(
                token,
                navigationArgs.Repo.Owner.Login,
                navigationArgs.Repo.Name,
                issue.Number);

            if (requestId != _detailRequestId)
            {
                return;
            }

            ReplaceIssueInCollection(latestIssue);
            SetSelectedIssue(latestIssue);
            PopulateIssue(latestIssue);
            IssueComments.Clear();

            foreach (GitHubIssueComment comment in comments)
            {
                IssueComments.Add(comment);
            }

            IsIssueCommentsEmptyVisible = IssueComments.Count == 0;
            StatusText = preserveStatusText
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
                StatusText = GetString("RepoIssue.LoadDetailNetworkError", "JitHub could not reach GitHub to load issue details.");
            }
        }
    }

    private void PopulateIssue(GitHubIssue issue)
    {
        AreIssueActionsEnabled = true;
        UpdateIssueCommentEnabledState();
        IssueTitleText = FormatString("RepoIssue.DetailTitleFormat", "#{0} {1}", issue.Number, issue.Title);
        IssueMetaText = FormatString(
            "RepoIssue.DetailMetaFormat",
            "{0}  •  @{1}  •  Updated {2:g}  •  {3}",
            GetIssueStateDisplay(issue.State),
            issue.User.Login,
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
    }

    private void ResetIssueDetails()
    {
        _detailRequestId++;
        AreIssueActionsEnabled = false;
        IsAddCommentEnabled = false;
        IssueTitleText = GetString("RepoIssue.SelectIssueTitle", "Select an issue");
        IssueMetaText = GetString("RepoIssue.SelectIssueSubtitle", "Choose an issue to inspect its details.");
        IssueMetadataText = string.Empty;
        IssueReactionsText = GetString("RepoIssue.NoReactions", "Reactions: none");
        IssueBodyText = string.Empty;
        IssueCommentDraft = string.Empty;
        IssueComments.Clear();
        IsIssueCommentsEmptyVisible = false;
        ToggleIssueStateButtonText = GetString("RepoIssue.CloseButton", "Close issue");
    }

    private void PrepareIssueForSelectionLoad(GitHubIssue issue)
    {
        AreIssueActionsEnabled = false;
        IsAddCommentEnabled = false;
        IssueTitleText = FormatString("RepoIssue.DetailTitleFormat", "#{0} {1}", issue.Number, issue.Title);
        IssueMetaText = FormatString(
            "RepoIssue.DetailMetaFormat",
            "{0}  •  @{1}  •  Updated {2:g}  •  {3}",
            GetIssueStateDisplay(issue.State),
            issue.User.Login,
            issue.UpdatedAt.LocalDateTime,
            FormatCommentCount(issue.Comments));
        IssueMetadataText = FormatIssueMetadataSummary(issue);
        IssueReactionsText = issue.Reactions.DisplayText;
        IssueBodyText = GetString("RepoIssue.LoadingBodyPlaceholder", "Loading issue details...");
        IssueCommentDraft = string.Empty;
        IssueComments.Clear();
        IsIssueCommentsEmptyVisible = false;
        ToggleIssueStateButtonText = string.Equals(issue.State, "closed", StringComparison.OrdinalIgnoreCase)
            ? GetString("RepoIssue.ReopenButton", "Reopen issue")
            : GetString("RepoIssue.CloseButton", "Close issue");
        StatusText = FormatString("RepoIssue.LoadIssueStatus", "Loading issue #{0}...", issue.Number);
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
            [.. IssueComments]);
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
            : visibleIssues.Count == 1
                ? FormatString("RepoIssue.ShowingSingleStatus", "Showing {0} issue.", visibleIssues.Count)
                : FormatString("RepoIssue.ShowingPluralStatus", "Showing {0} issues.", visibleIssues.Count);

        bool selectionChanged = previousSelectedIssue?.Number != selectedIssue?.Number;
        if (SelectedIssue?.Number != selectedIssue?.Number)
        {
            CancelPendingSelectionLoad();
        }

        _suppressSelectionChanged = true;
        try
        {
            Issues.Clear();
            foreach (GitHubIssue issue in visibleIssues)
            {
                Issues.Add(issue);
            }

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
    }

    private void ResetFilters()
    {
        SearchText = string.Empty;
        SelectedStateOption = StateOptions[0];
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

        int requestId = _listRequestId;
        GitHubIssue refreshedIssue;
        try
        {
            refreshedIssue = await _gitHubClientService.GetIssueAsync(
                token,
                _navArg.Repo.Owner.Login,
                _navArg.Repo.Name,
                issue.Number);
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

        ReplaceIssueInCollection(refreshedIssue);
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

    private async Task ShowIssueAfterSelectionDelayAsync(GitHubIssue issue, CancellationToken cancellationToken)
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

        _pendingIssueSelectionState = null;
        await ShowIssueAsync(issue);
    }

    private void CancelPendingSelectionLoad()
    {
        _selectionLoadCancellationTokenSource?.Cancel();
        _selectionLoadCancellationTokenSource?.Dispose();
        _selectionLoadCancellationTokenSource = null;
    }

    private static bool MatchesIssueSearch(GitHubIssue issue, string searchText)
    {
        return issue.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(issue.Body) && issue.Body.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            || issue.User.Login.Contains(searchText, StringComparison.OrdinalIgnoreCase)
            || issue.Number.ToString().Contains(searchText, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateIssueCommentEnabledState()
    {
        IsAddCommentEnabled = AreIssueActionsEnabled && !_isIssueCommentSubmissionInProgress;
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

        return !_issueQuery.Since.HasValue || issue.UpdatedAt >= _issueQuery.Since.Value;
    }

    private static bool MatchesStateFilter(string state, string filter)
    {
        return string.IsNullOrWhiteSpace(filter)
            || string.Equals(filter, "all", StringComparison.OrdinalIgnoreCase)
            || string.Equals(state, filter, StringComparison.OrdinalIgnoreCase);
    }

    private static DateTimeOffset CreateLocalMidnight(DateTimeOffset date)
    {
        DateTime localMidnight = new(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Unspecified);
        TimeSpan localOffset = TimeZoneInfo.Local.GetUtcOffset(localMidnight);
        return new DateTimeOffset(localMidnight, localOffset);
    }

    private void ReplaceIssueInCollection(GitHubIssue updatedIssue)
    {
        _suppressSelectionChanged = true;
        try
        {
            for (int index = 0; index < _loadedIssues.Count; index++)
            {
                if (_loadedIssues[index].Number == updatedIssue.Number)
                {
                    _loadedIssues[index] = updatedIssue;
                    break;
                }
            }

            for (int index = 0; index < Issues.Count; index++)
            {
                if (Issues[index].Number == updatedIssue.Number)
                {
                    Issues[index] = updatedIssue;
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
