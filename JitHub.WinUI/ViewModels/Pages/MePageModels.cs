using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Common;

namespace JitHub.WinUI.ViewModels.Pages;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class MeWorkItemViewItem : ObservableObject
{
    public GitHubIssue Issue { get; set; } = new();

    public string StableKey => GetStableKey(Issue);

    public string Title => string.IsNullOrWhiteSpace(Issue.Title)
        ? LocalizedResourceText.GetString("MyWorkItems.List.Untitled", "Untitled issue")
        : Issue.Title;

    public string NumberText => $"#{Issue.Number.ToString(CultureInfo.InvariantCulture)}";

    public string RepositoryFullName => ExtractRepositoryFullName(Issue.RepositoryUrl, Issue.HtmlUrl);

    public string Owner => SplitRepositoryName(RepositoryFullName).Owner;

    public string RepositoryName => SplitRepositoryName(RepositoryFullName).Name;

    public string AuthorDisplayName => string.IsNullOrWhiteSpace(Issue.User?.Login)
        ? LocalizedResourceText.GetString("Common.UnknownUser", "unknown")
        : Issue.User.Login;

    public string? AuthenticatedAuthorLogin =>
        UserIdentityNavigationPolicy.GetRoutableLogin(Issue.User?.Login);

    public string AuthorAvatarUrl => Issue.User?.AvatarUrl ?? string.Empty;

    public string MetaText => LocalizedResourceText.Format(
        "MyWorkItems.List.MetaFormat",
        "{0} · {1} · {2}",
        NumberText,
        Issue.State.Equals("closed", StringComparison.OrdinalIgnoreCase)
            ? LocalizedResourceText.GetString("MyIssues.State.ClosedLabel", "Closed")
            : LocalizedResourceText.GetString("MyIssues.State.OpenLabel", "Open"),
        FormatTimeAgo(Issue.UpdatedAt));

    public string CommentsText => LocalizedResourceText.Format(
        Issue.Comments == 1 ? "MyWorkItems.Count.OneComment" : "MyWorkItems.Count.CommentsFormat",
        Issue.Comments == 1 ? "1 comment" : "{0} comments",
        Issue.Comments);

    public string CreatedText => LocalizedResourceText.Format(
        "MyWorkItems.List.OpenedByFormat",
        "opened {0} by {1}",
        FormatTimeAgo(Issue.CreatedAt),
        AuthorDisplayName);

    public string UpdatedText => FormatTimeAgo(Issue.UpdatedAt);

    public string LabelSummary => Issue.Labels.Length == 0
        ? LocalizedResourceText.GetString("MyWorkItems.Inspector.NoLabels", "No labels")
        : string.Join(", ", Issue.Labels.Take(3).Select(static label => label.Name));

    public string AssigneeSummary => Issue.Assignees.Length == 0
        ? LocalizedResourceText.GetString("MyWorkItems.Inspector.Unassigned", "Unassigned")
        : string.Join(", ", Issue.Assignees.Take(3).Select(static assignee => assignee.Login));

    public string KindLabel => Issue.IsPullRequest
        ? LocalizedResourceText.GetString("MyPullRequests.ItemKind", "Pull request")
        : LocalizedResourceText.GetString("MyIssues.ItemKind", "Issue");

    public string AutomationId => $"MyWorkItem_{Issue.Id.ToString(CultureInfo.InvariantCulture)}";

    public string AutomationName => $"{KindLabel} {NumberText}: {Title}, {RepositoryFullName}";

    public string Glyph => Issue.IsPullRequest ? "\uE8EE" : "\uE8A5";

    public ICommand? Command { get; set; }

    public bool ApplyIssue(GitHubIssue issue)
    {
        if (HasSameListProjection(Issue, issue))
        {
            return false;
        }

        Issue = issue;
        NotifyListProjectionChanged();
        return true;
    }

    public static string GetStableKey(GitHubIssue issue)
    {
        string repositoryFullName = ExtractRepositoryFullName(issue.RepositoryUrl, issue.HtmlUrl);
        if (!string.IsNullOrWhiteSpace(repositoryFullName) && issue.Number > 0)
        {
            return $"{repositoryFullName}#{issue.Number.ToString(CultureInfo.InvariantCulture)}";
        }

        return issue.Id > 0
            ? issue.Id.ToString(CultureInfo.InvariantCulture)
            : issue.Number.ToString(CultureInfo.InvariantCulture);
    }

    public static string ExtractRepositoryFullName(string? repositoryUrl, string? htmlUrl)
    {
        string source = !string.IsNullOrWhiteSpace(repositoryUrl) ? repositoryUrl! : htmlUrl ?? string.Empty;
        string marker = "/repos/";
        int index = source.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return source[(index + marker.Length)..].Trim('/');
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri))
        {
            string[] segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length >= 2)
            {
                return $"{segments[0]}/{segments[1]}";
            }
        }

        return string.Empty;
    }

    public static (string Owner, string Name) SplitRepositoryName(string fullName)
    {
        string[] parts = fullName.Split('/', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (parts[0], parts[1])
            : (string.Empty, fullName);
    }

    public static string FormatTimeAgo(DateTimeOffset value)
    {
        TimeSpan age = DateTimeOffset.Now - value.ToLocalTime();
        if (age.TotalMinutes < 1)
        {
            return LocalizedResourceText.GetString("Common.Time.JustNow", "just now");
        }

        if (age.TotalHours < 1)
        {
            return LocalizedResourceText.Format(
                "Common.Time.MinutesAgoFormat",
                "{0}m ago",
                (int)Math.Max(1, age.TotalMinutes));
        }

        if (age.TotalDays < 1)
        {
            return LocalizedResourceText.Format(
                "Common.Time.HoursAgoFormat",
                "{0}h ago",
                (int)Math.Max(1, age.TotalHours));
        }

        return age.TotalDays < 30
            ? LocalizedResourceText.Format(
                "Common.Time.DaysAgoFormat",
                "{0}d ago",
                (int)Math.Max(1, age.TotalDays))
            : value.ToLocalTime().ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    private void NotifyListProjectionChanged()
    {
        OnPropertyChanged(nameof(Issue));
        OnPropertyChanged(nameof(StableKey));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(NumberText));
        OnPropertyChanged(nameof(RepositoryFullName));
        OnPropertyChanged(nameof(Owner));
        OnPropertyChanged(nameof(RepositoryName));
        OnPropertyChanged(nameof(AuthorDisplayName));
        OnPropertyChanged(nameof(AuthenticatedAuthorLogin));
        OnPropertyChanged(nameof(AuthorAvatarUrl));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(CommentsText));
        OnPropertyChanged(nameof(CreatedText));
        OnPropertyChanged(nameof(UpdatedText));
        OnPropertyChanged(nameof(LabelSummary));
        OnPropertyChanged(nameof(AssigneeSummary));
        OnPropertyChanged(nameof(KindLabel));
        OnPropertyChanged(nameof(Glyph));
        OnPropertyChanged(nameof(AutomationId));
        OnPropertyChanged(nameof(AutomationName));
    }

    private static bool HasSameListProjection(GitHubIssue left, GitHubIssue right) =>
        left.Id == right.Id
        && left.Number == right.Number
        && left.Comments == right.Comments
        && left.CreatedAt == right.CreatedAt
        && left.UpdatedAt == right.UpdatedAt
        && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
        && string.Equals(left.State, right.State, StringComparison.Ordinal)
        && string.Equals(left.HtmlUrl, right.HtmlUrl, StringComparison.Ordinal)
        && string.Equals(left.RepositoryUrl, right.RepositoryUrl, StringComparison.Ordinal)
        && string.Equals(left.User?.Login, right.User?.Login, StringComparison.Ordinal)
        && string.Equals(left.User?.AvatarUrl, right.User?.AvatarUrl, StringComparison.Ordinal)
        && left.IsPullRequest == right.IsPullRequest
        && string.Equals(left.PullRequest?.HtmlUrl, right.PullRequest?.HtmlUrl, StringComparison.Ordinal)
        && string.Equals(
            string.Join('\u001f', left.Labels.Select(static label => label.Name)),
            string.Join('\u001f', right.Labels.Select(static label => label.Name)),
            StringComparison.Ordinal)
        && string.Equals(
            string.Join('\u001f', left.Assignees.Select(static assignee => assignee.Login)),
            string.Join('\u001f', right.Assignees.Select(static assignee => assignee.Login)),
            StringComparison.Ordinal);
}
public abstract partial class MeSearchPageViewModelBase : ViewModelBase
{
    private const int PageSize = 100;
    private const int CommentPageSize = 100;
    private const int GitHubSearchResultLimit = 1000;
    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubMeQueryService _meQueryService;
    private readonly IGitHubPullRequestQueryService _pullRequestQueryService;
    private readonly ShellPageViewModel _shell;
    private readonly IIssueNavigationCache _issueNavigationCache;
    private readonly IPullRequestNavigationCache _pullRequestNavigationCache;
    private readonly ITelemetryService _telemetryService;
    private readonly DeferredMarkdownBodyState _detailBodyState = new();
    private bool _initialized;
    private int _detailLoadVersion;
    private int _listLoadVersion;
    private CancellationTokenSource? _listCancellationTokenSource;
    private CancellationTokenSource? _detailCancellationTokenSource;
    private CancellationTokenSource? _pullRequestSectionCancellationTokenSource;
    private IDisposable? _selectedIssueDwellPrefetch;
    private string? _activeListQueryKey;
    private int _pullRequestSectionLoadVersion;
    private GitHubPullRequest? _selectedPullRequest;
    private GitHubPullRequestReview[] _selectedPullRequestReviews = [];
    private GitHubPullRequestReviewComment[] _selectedPullRequestReviewComments = [];
    private readonly HashSet<PullRequestWorkspaceSection> _loadedPullRequestSections = [];
    private PullRequestWorkspaceSection _selectedPullRequestSection = PullRequestWorkspaceSection.Conversation;
    private string? _activeLogin;
    private string? _activeIdentityPartition;
    private int _lastReportedTotalCount;

    public event EventHandler? ListSnapshotApplying;

    public event EventHandler? ListSnapshotApplied;

    protected MeSearchPageViewModelBase()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _meQueryService = GetService<IGitHubMeQueryService>();
        _pullRequestQueryService = GetService<IGitHubPullRequestQueryService>();
        _shell = GetService<ShellPageViewModel>();
        _issueNavigationCache = GetService<IIssueNavigationCache>();
        _pullRequestNavigationCache = GetService<IPullRequestNavigationCache>();
        _telemetryService = SafeTelemetryService.Wrap(GetService<ITelemetryService>());
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenSelectedInRepositoryCommand = new RelayCommand(OpenSelectedIssueInRepository, () => SelectedItem is not null);
        ExpandDetailBodyCommand = new RelayCommand(ExpandDetailBody, () => IsDetailBodyDeferred);
    }

    internal MeSearchPageViewModelBase(
        IAuthService authService,
        IAccountService accountService,
        IGitHubMeQueryService meQueryService,
        IGitHubPullRequestQueryService pullRequestQueryService,
        ShellPageViewModel shell,
        IIssueNavigationCache issueNavigationCache,
        IPullRequestNavigationCache pullRequestNavigationCache,
        ITelemetryService telemetryService)
    {
        _authService = authService;
        _accountService = accountService;
        _meQueryService = meQueryService;
        _pullRequestQueryService = pullRequestQueryService;
        _shell = shell;
        _issueNavigationCache = issueNavigationCache;
        _pullRequestNavigationCache = pullRequestNavigationCache;
        _telemetryService = SafeTelemetryService.Wrap(telemetryService);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        OpenSelectedInRepositoryCommand = new RelayCommand(OpenSelectedIssueInRepository, () => SelectedItem is not null);
        ExpandDetailBodyCommand = new RelayCommand(ExpandDetailBody, () => IsDetailBodyDeferred);
    }

    public KeyedObservableCollection<MeWorkItemViewItem, GitHubIssue> Items { get; } = [];

    public KeyedObservableCollection<MeIssueCommentViewItem, GitHubIssueComment> Comments { get; } = [];

    public KeyedObservableCollection<MeLabelViewItem, GitHubLabel> SelectedLabels { get; } = [];

    public KeyedObservableCollection<MeActorViewItem, GitHubActor> SelectedAssignees { get; } = [];

    public KeyedObservableCollection<MePullRequestCommitViewItem, GitHubCommit> PullRequestCommits { get; } = [];

    public KeyedObservableCollection<MePullRequestReviewViewItem, MePullRequestReviewSnapshot> PullRequestReviews { get; } = [];

    public KeyedObservableCollection<MePullRequestTimelineViewItem, GitHubIssueEvent> PullRequestTimelineEvents { get; } = [];

    public IAsyncRelayCommand RefreshCommand { get; }

    public IRelayCommand OpenSelectedInRepositoryCommand { get; }

    public IRelayCommand ExpandDetailBodyCommand { get; }

    public abstract string Title { get; }

    public abstract string Subtitle { get; }

    protected abstract bool IsPullRequestPage { get; }

    public GitHubMeIssueFilter IssueFilter { get; private set; } = GitHubMeIssueFilter.Assigned;

    public GitHubMeWorkItemState WorkItemState { get; private set; } = GitHubMeWorkItemState.Open;

    public bool IsAssignedFilterSelected => IssueFilter == GitHubMeIssueFilter.Assigned;

    public bool IsCreatedFilterSelected => IssueFilter == GitHubMeIssueFilter.Created;

    public bool IsMentionedFilterSelected => IssueFilter == GitHubMeIssueFilter.Mentioned;

    public bool IsOpenStateSelected => WorkItemState == GitHubMeWorkItemState.Open;

    public bool IsClosedStateSelected => WorkItemState == GitHubMeWorkItemState.Closed;

    public bool IsAllStateSelected => WorkItemState == GitHubMeWorkItemState.All;

    public string AssignedFilterLabel => GetString("MyIssues.Filter.AssignedLabel", "Assigned");

    public string AssignedFilterToolTip => GetString("MyIssues.Filter.AssignedToolTip", "Assigned to me");

    public string CreatedFilterLabel => GetString("MyIssues.Filter.CreatedLabel", "Created");

    public string CreatedFilterToolTip => GetString("MyIssues.Filter.CreatedToolTip", "Created by me");

    public string MentionedFilterLabel => GetString("MyIssues.Filter.MentionedLabel", "Mentioned");

    public string MentionedFilterToolTip => GetString("MyIssues.Filter.MentionedToolTip", "Mentioning me");

    public string OpenStateLabel => GetString("MyIssues.State.OpenLabel", "Open");

    public string ClosedStateLabel => GetString("MyIssues.State.ClosedLabel", "Closed");

    public string AllStateLabel => GetString("MyIssues.State.AllLabel", "All");

    public string ScopePickerAutomationName => GetString("MyIssues.Filter.ScopeAutomationName", "Issue scope");

    public string StatePickerAutomationName => GetString("MyIssues.Filter.StateAutomationName", "Issue state");

    public string PullRequestOpenStateLabel => GetString("MyPullRequests.State.OpenLabel", "Open");

    public string PullRequestClosedStateLabel => GetString("MyPullRequests.State.ClosedLabel", "Closed");

    public string PullRequestAllStateLabel => GetString("MyPullRequests.State.AllLabel", "All");

    public string PullRequestStatePickerAutomationName =>
        GetString("MyPullRequests.Filter.StateAutomationName", "Pull request state");

    public string ConversationSectionLabel => GetString("MyPullRequests.Section.Conversation", "Conversation");

    public string CommitsSectionLabel => GetString("MyPullRequests.Section.Commits", "Commits");

    public string ReviewsSectionLabel => GetString("MyPullRequests.Section.Reviews", "Reviews");

    public string TimelineSectionLabel => GetString("MyPullRequests.Section.Timeline", "Timeline");

    public bool HasSelectedIssue => SelectedIssue is not null;

    public bool IsDetailPlaceholderVisible => SelectedIssue is null;

    public bool HasLabels => SelectedLabels.Count > 0;

    public bool HasNoLabels => SelectedLabels.Count == 0;

    public bool HasAssignees => SelectedAssignees.Count > 0;

    public bool HasNoAssignees => SelectedAssignees.Count == 0;

    public bool HasMilestone => SelectedIssue?.Milestone is not null;

    public bool HasComments => Comments.Count > 0;

    public bool HasNoComments => HasSelectedIssue && !IsDetailLoading && Comments.Count == 0;

    public PullRequestWorkspaceSection SelectedPullRequestSection => _selectedPullRequestSection;

    public bool IsPullRequestConversationSectionVisible =>
        IsPullRequestPage && SelectedPullRequestSection == PullRequestWorkspaceSection.Conversation;

    public bool IsPullRequestCommitsSectionVisible =>
        IsPullRequestPage && SelectedPullRequestSection == PullRequestWorkspaceSection.Commits;

    public bool IsPullRequestReviewsSectionVisible =>
        IsPullRequestPage && SelectedPullRequestSection == PullRequestWorkspaceSection.Reviews;

    public bool IsPullRequestTimelineSectionVisible =>
        IsPullRequestPage && SelectedPullRequestSection == PullRequestWorkspaceSection.Timeline;

    public bool HasNoPullRequestCommits =>
        HasSelectedIssue && !IsPullRequestSectionLoading && PullRequestCommits.Count == 0;

    public bool HasNoPullRequestReviews =>
        HasSelectedIssue && !IsPullRequestSectionLoading && PullRequestReviews.Count == 0;

    public bool HasNoPullRequestTimelineEvents =>
        HasSelectedIssue && !IsPullRequestSectionLoading && PullRequestTimelineEvents.Count == 0;

    public bool IsStatusVisible => !string.IsNullOrWhiteSpace(StatusText);

    public bool IsDetailStatusVisible => !string.IsNullOrWhiteSpace(DetailStatusText);

    public bool IsDetailCollectionStatusVisible => !string.IsNullOrWhiteSpace(DetailCollectionStatusText);

    public string SelectedIssueNumberText => SelectedIssue is null
        ? string.Empty
        : $"#{SelectedIssue.Number.ToString(CultureInfo.InvariantCulture)}";

    public string SelectedIssueTitle => SelectedIssue?.Title ?? string.Empty;

    public string SelectedIssueRepository => SelectedItem?.RepositoryFullName ?? string.Empty;

    public string SelectedIssueAuthorDisplayName => string.IsNullOrWhiteSpace(SelectedIssue?.User?.Login)
        ? GetString("Common.UnknownUser", "unknown")
        : SelectedIssue.User.Login;

    public string? SelectedIssueAuthorLogin =>
        UserIdentityNavigationPolicy.GetRoutableLogin(SelectedIssue?.User?.Login);

    public string SelectedIssueAuthorAvatarUrl => SelectedIssue?.User?.AvatarUrl ?? string.Empty;

    public string SelectedIssueAuthorAutomationId => SelectedIssue?.AutomationId ?? "MyWorkItemSelected_none";

    public string SelectedIssueStateText => SelectedIssue?.State.Equals("closed", StringComparison.OrdinalIgnoreCase) == true
        ? ClosedStateLabel
        : OpenStateLabel;

    public string SelectedIssueMetadataText => SelectedIssue is null
        ? string.Empty
        : FormatString(
            "MyWorkItems.Detail.MetadataFormat",
            "opened {0} · updated {1}",
            MeWorkItemViewItem.FormatTimeAgo(SelectedIssue.CreatedAt),
            MeWorkItemViewItem.FormatTimeAgo(SelectedIssue.UpdatedAt));

    public string SelectedIssueCommentText => SelectedIssue is null
        ? string.Empty
        : FormatString(
            SelectedIssue.Comments == 1 ? "MyWorkItems.Count.OneComment" : "MyWorkItems.Count.CommentsFormat",
            SelectedIssue.Comments == 1 ? "1 comment" : "{0} comments",
            SelectedIssue.Comments);

    public MarkdownDocumentSource? DetailMarkdownSource => SelectedIssue?.MarkdownSource;

    public bool IsDetailBodyDeferred => _detailBodyState.IsDeferred;

    public bool IsDetailBodyMarkdownVisible => _detailBodyState.IsMarkdownRealized;

    public string DetailBodyPreviewText => _detailBodyState.PreviewText;

    public string ShowFullBodyLabel => GetString("MyWorkItems.Detail.ShowFullBody", "Show full content");

    public string MilestoneTitle => SelectedIssue?.Milestone?.Title ??
        GetString("MyWorkItems.Inspector.NoMilestone", "No milestone");

    public string LinkedPullRequestText => SelectedIssue?.PullRequest?.HtmlUrl is { Length: > 0 }
        ? GetString("MyIssues.Inspector.LinkedPullRequestAvailable", "Linked pull request available")
        : GetString("MyIssues.Inspector.NoLinkedPullRequest", "No linked pull request");

    public bool HasLinkedPullRequest => SelectedIssue?.PullRequest?.HtmlUrl is { Length: > 0 };

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultCountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailBodyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailBaseUrl { get; set; } = "https://github.com/";

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsDetailLoading { get; set; }

    [ObservableProperty]
    public partial bool IsPullRequestSectionLoading { get; set; }

    [ObservableProperty]
    public partial string PullRequestSectionStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DetailCollectionStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial MeWorkItemViewItem? SelectedItem { get; set; }

    [ObservableProperty]
    public partial GitHubIssue? SelectedIssue { get; set; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _telemetryService.TrackEvent(
            IsPullRequestPage ? "pull_requests.opened" : "issues.opened",
            new Dictionary<string, string?>
            {
                ["page"] = "my",
                ["source"] = TelemetryTaxonomy.Sources.Shell
            });
        await RefreshAsync(cancellationToken);
    }

    public void SetIssueFilter(GitHubMeIssueFilter filter)
    {
        if (IssueFilter == filter)
        {
            return;
        }

        IssueFilter = filter;
        NotifyFilterPropertiesChanged();
        _ = RefreshAsync();
    }

    public void SetWorkItemState(GitHubMeWorkItemState state)
    {
        if (WorkItemState == state)
        {
            return;
        }

        WorkItemState = state;
        NotifyFilterPropertiesChanged();
        _ = RefreshAsync();
    }

    public void SetPullRequestSection(PullRequestWorkspaceSection section)
    {
        if (!IsPullRequestPage)
        {
            return;
        }

        if (_selectedPullRequestSection == section)
        {
            if (!_loadedPullRequestSections.Contains(section))
            {
                _ = EnsureSelectedPullRequestSectionAsync();
            }

            return;
        }

        _selectedPullRequestSection = section;
        NotifyPullRequestSectionPropertiesChanged();
        PullRequestSectionStatusText = string.Empty;
        _telemetryService.TrackEvent(
            "pull_requests.section.opened",
            new Dictionary<string, string?>
            {
                ["page"] = "my",
                ["section"] = TelemetryTaxonomy.EnumValue(section)
            });
        _ = EnsureSelectedPullRequestSectionAsync();
    }

    protected abstract Task<CachedResult<GitHubSearchIssuesResponse>> QueryAsync(
        string token,
        string userId,
        string login,
        GitHubMeWorkItemState state,
        int page,
        CancellationToken cancellationToken);

    protected abstract Task<CachedResult<GitHubSearchIssuesResponse>> RefreshQueryAsync(
        string token,
        string userId,
        string login,
        GitHubMeWorkItemState state,
        int page,
        CancellationToken cancellationToken);

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _listCancellationTokenSource?.Cancel();
        _listCancellationTokenSource?.Dispose();
        _listCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken loadCancellationToken = _listCancellationTokenSource.Token;
        int loadVersion = Interlocked.Increment(ref _listLoadVersion);

        using IPerformanceTrace trace = _telemetryService.StartTrace(
            IsPullRequestPage ? "pull_requests.list.loaded" : "issues.list.loaded",
            new Dictionary<string, string?>
            {
                ["page"] = "my",
                ["source"] = TelemetryTaxonomy.Sources.Cache
            });

        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            StatusText = GetString(
                "MyWorkItems.Error.AuthenticationUnavailable",
                "GitHub authentication is unavailable.");
            trace.SetProperty("result", TelemetryTaxonomy.Results.AuthError);
            _authService.SignOut();
            return;
        }

        IsLoading = Items.Count == 0;
        StatusText = string.Empty;

        try
        {
            string userPartition = GetActiveUserPartition(token);
            if (string.IsNullOrWhiteSpace(userPartition))
            {
                StatusText = GetString(
                    "MyWorkItems.Status.IdentityLoading",
                    "GitHub account identity is still loading.");
                trace.SetProperty("result", TelemetryTaxonomy.Results.IdentityUnavailable);
                return;
            }

            GitHubUser? user = _authService.AuthenticatedUser;
            bool identityRefreshFailed = false;
            if (string.IsNullOrWhiteSpace(user?.Login))
            {
                try
                {
                    user = await _authService.RefreshAuthenticatedUserAsync();
                }
                catch (Exception ex) when (ex is GitHubApiException or System.Net.Http.HttpRequestException)
                {
                    identityRefreshFailed = true;
                }
            }

            string? login = MeWorkItemIdentityPolicy.ResolveLogin(
                user?.Login,
                _activeLogin,
                _activeIdentityPartition,
                userPartition);
            if (string.IsNullOrWhiteSpace(login))
            {
                StatusText = Items.Count == 0
                    ? GetString(
                        "MyWorkItems.Error.IdentityUnavailable",
                        "Your GitHub identity could not be loaded. Check the connection and try again.")
                    : GetString(
                        "MyWorkItems.Error.IdentityRefreshFailed",
                        "Could not refresh your GitHub identity. Existing results remain available.");
                trace.SetProperty("result", TelemetryTaxonomy.Results.IdentityUnavailable);
                return;
            }

            _activeLogin = login;
            _activeIdentityPartition = userPartition;
            string queryKey = $"{IssueFilter}:{WorkItemState}:{login}";
            bool queryChanged = !string.Equals(_activeListQueryKey, queryKey, StringComparison.Ordinal);

            bool queryCommitted = !queryChanged;
            GitHubPagedLoadResult<GitHubIssue> listResult = await GitHubPagedReconciler.LoadAsync<GitHubSearchIssuesResponse, GitHubIssue>(
                (page, pageCancellationToken) => QueryAsync(
                    token,
                    userPartition,
                    login,
                    WorkItemState,
                    page,
                    pageCancellationToken),
                (page, pageCancellationToken) => RefreshQueryAsync(
                    token,
                    userPartition,
                    login,
                    WorkItemState,
                    page,
                    pageCancellationToken),
                static response => response.Items,
                static response => response.TotalCount,
                MeWorkItemViewItem.GetStableKey,
                PageSize,
                GitHubSearchResultLimit,
                progress =>
                {
                    if (loadVersion != _listLoadVersion || loadCancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    MeWorkItemProjectionDecision decision = MeWorkItemProjectionPolicy.Evaluate(
                        queryChanged,
                        queryCommitted,
                        Items.Count > 0,
                        progress.Items.Count,
                        progress.IsAuthoritative,
                        progress.IsFinal,
                        progress.Completeness);
                    if (!decision.Apply)
                    {
                        return;
                    }

                    ApplyIssueSnapshot(progress.Items, decision.RemoveMissing);
                    _activeListQueryKey = queryKey;
                    queryCommitted = queryCommitted || decision.CommitsQuery;
                    _lastReportedTotalCount = progress.TotalCount ?? progress.Items.Count;
                    UpdateResultCount(
                        progress.Items.Count,
                        _lastReportedTotalCount,
                        progress.Completeness);
                },
                loadCancellationToken);
            if (loadVersion != _listLoadVersion || loadCancellationToken.IsCancellationRequested)
            {
                trace.SetProperty("result", TelemetryTaxonomy.Results.Cancelled);
                return;
            }

            _lastReportedTotalCount = listResult.TotalCount ?? listResult.Items.Count;
            UpdateResultCount(listResult.Items.Count, _lastReportedTotalCount, listResult.Completeness);
            StatusText = listResult.Completeness switch
            {
                PagedDataCompleteness.Partial => GetString(
                    "MyWorkItems.Status.PartialResults",
                    "Some results could not be loaded. The available results remain visible."),
                PagedDataCompleteness.ApiLimited => GetString(
                    "MyWorkItems.Status.ApiLimitedResults",
                    "GitHub limits this search to 1,000 results."),
                _ => string.Empty
            };
            trace.SetProperty(
                "result",
                MeListTelemetryOutcomePolicy.ForCompletedLoad(
                    identityRefreshFailed,
                    listResult.Completeness));
        }
        catch (OperationCanceledException ex)
        {
            trace.SetProperty("result", MeListTelemetryOutcomePolicy.ForException(ex));
        }
        catch (GitHubAuthenticationException ex)
        {
            trace.SetProperty("result", MeListTelemetryOutcomePolicy.ForException(ex));
            _authService.SignOut();
        }
        catch (Exception ex) when (ex is GitHubApiException or System.Net.Http.HttpRequestException)
        {
            trace.SetProperty("result", MeListTelemetryOutcomePolicy.ForException(ex));
            StatusText = Items.Count == 0
                ? JitHub.WinUI.Helpers.UserFacingError.For(
                    ex,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Loading,
                    "my-work-items")
                : GetString(
                    "MyWorkItems.Error.RefreshFailed",
                    "Some results could not be updated. Existing results remain available.");
            UpdateResultCount(
                Items.Count,
                Math.Max(Items.Count, _lastReportedTotalCount),
                PagedDataCompleteness.Partial);
            IsEmpty = Items.Count == 0;
        }
        catch
        {
            trace.SetProperty("result", TelemetryTaxonomy.Results.Error);
            throw;
        }
        finally
        {
            if (loadVersion == _listLoadVersion)
            {
                IsLoading = false;
            }
        }
    }

    partial void OnSelectedItemChanged(MeWorkItemViewItem? value)
    {
        _selectedIssueDwellPrefetch?.Dispose();
        _selectedIssueDwellPrefetch = null;
        OpenSelectedInRepositoryCommand.NotifyCanExecuteChanged();
        _ = LoadSelectedItemAfterInputAsync(value);
    }

    private async Task LoadSelectedItemAfterInputAsync(MeWorkItemViewItem? item)
    {
        await Task.Yield();
        if (!ReferenceEquals(SelectedItem, item))
        {
            return;
        }

        if (item is not null)
        {
            _telemetryService.TrackEvent(
                IsPullRequestPage ? "pull_requests.selected" : "issues.selected",
                new Dictionary<string, string?>
                {
                    ["page"] = "my",
                    ["source"] = TelemetryTaxonomy.Sources.List
                });
        }

        await LoadSelectedIssueAsync(item);
    }

    partial void OnSelectedIssueChanged(GitHubIssue? value)
    {
        OnPropertyChanged(nameof(HasSelectedIssue));
        OnPropertyChanged(nameof(IsDetailPlaceholderVisible));
        OnPropertyChanged(nameof(SelectedIssueNumberText));
        OnPropertyChanged(nameof(SelectedIssueTitle));
        OnPropertyChanged(nameof(SelectedIssueRepository));
        OnPropertyChanged(nameof(SelectedIssueAuthorDisplayName));
        OnPropertyChanged(nameof(SelectedIssueAuthorLogin));
        OnPropertyChanged(nameof(SelectedIssueAuthorAvatarUrl));
        OnPropertyChanged(nameof(SelectedIssueAuthorAutomationId));
        OnPropertyChanged(nameof(SelectedIssueStateText));
        OnPropertyChanged(nameof(SelectedIssueMetadataText));
        OnPropertyChanged(nameof(SelectedIssueCommentText));
        OnPropertyChanged(nameof(DetailMarkdownSource));
        OnPropertyChanged(nameof(HasMilestone));
        OnPropertyChanged(nameof(MilestoneTitle));
        OnPropertyChanged(nameof(LinkedPullRequestText));
        OnPropertyChanged(nameof(HasLinkedPullRequest));
        OnPropertyChanged(nameof(HasNoComments));
        OnPropertyChanged(nameof(HasNoPullRequestCommits));
        OnPropertyChanged(nameof(HasNoPullRequestReviews));
        OnPropertyChanged(nameof(HasNoPullRequestTimelineEvents));
    }

    partial void OnIsDetailLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoComments));
    }

    partial void OnIsPullRequestSectionLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasNoPullRequestCommits));
        OnPropertyChanged(nameof(HasNoPullRequestReviews));
        OnPropertyChanged(nameof(HasNoPullRequestTimelineEvents));
    }

    partial void OnDetailBodyTextChanged(string value)
    {
        _detailBodyState.Update(value);
        NotifyDetailBodyPresentationChanged();
    }

    private async Task LoadSelectedIssueAsync(MeWorkItemViewItem? item)
    {
        _detailCancellationTokenSource?.Cancel();
        _detailCancellationTokenSource?.Dispose();
        _pullRequestSectionCancellationTokenSource?.Cancel();
        _pullRequestSectionCancellationTokenSource?.Dispose();
        _pullRequestSectionCancellationTokenSource = null;
        Interlocked.Increment(ref _pullRequestSectionLoadVersion);
        _loadedPullRequestSections.Clear();
        PullRequestSectionStatusText = string.Empty;
        IsPullRequestSectionLoading = false;
        _selectedPullRequest = null;
        _detailCancellationTokenSource = new CancellationTokenSource();
        CancellationToken cancellationToken = _detailCancellationTokenSource.Token;
        int version = Interlocked.Increment(ref _detailLoadVersion);

        if (item is null)
        {
            ResetSelectedIssue();
            return;
        }

        SelectedIssue = item.Issue;
        ApplySelectedIssue(item.Issue);

        // Selection is an input-critical path. Project the row's already-loaded
        // issue immediately, then let cache reconciliation and background reads
        // begin on the next UI turn so pointer/keyboard input and the first detail
        // render are never held behind section setup.
        await Task.Yield();
        if (version != _detailLoadVersion || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        string fullName = item.RepositoryFullName;
        (string owner, string repositoryName) = MeWorkItemViewItem.SplitRepositoryName(fullName);
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repositoryName))
        {
            DetailStatusText = GetString(
                "MyWorkItems.Error.RepositoryMissing",
                "Repository information is missing from this item.");
            return;
        }

        string? token = GetActiveToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            DetailStatusText = GetString(
                "MyWorkItems.Error.AuthenticationUnavailable",
                "GitHub authentication is unavailable.");
            return;
        }

        string userPartition = GetActiveUserPartition(token);
        if (string.IsNullOrWhiteSpace(userPartition))
        {
            DetailStatusText = GetString(
                "MyWorkItems.Status.IdentityLoading",
                "GitHub account identity is still loading.");
            return;
        }

        ApplyCachedDetailIfAvailable(item, userPartition, owner, repositoryName);
        IsDetailLoading = true;
        DetailStatusText = string.Empty;

        try
        {
            if (IsPullRequestPage)
            {
                await LoadSelectedPullRequestAsync(item, token, userPartition, owner, repositoryName, version, cancellationToken);
                return;
            }

            CachedResult<GitHubIssue> detailResult = await _meQueryService.GetIssueDetailAsync(
                token,
                userPartition,
                owner,
                repositoryName,
                item.Issue.Number,
                cancellationToken);

            if (version != _detailLoadVersion || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (detailResult.Value is not null)
            {
                SelectedIssue = detailResult.Value;
                ApplySelectedIssue(detailResult.Value);
            }

            GitHubPagedLoadResult<GitHubIssueComment> commentsResult = await GitHubPagedReconciler.LoadAsync<GitHubIssueComment[], GitHubIssueComment>(
                (page, pageCancellationToken) => _meQueryService.GetIssueCommentsPageAsync(
                    token,
                    userPartition,
                    owner,
                    repositoryName,
                    item.Issue.Number,
                    CommentPageSize,
                    page,
                    pageCancellationToken),
                (page, pageCancellationToken) => _meQueryService.RefreshIssueCommentsPageAsync(
                    token,
                    userPartition,
                    owner,
                    repositoryName,
                    item.Issue.Number,
                    CommentPageSize,
                    page,
                    pageCancellationToken),
                static comments => comments,
                totalCountSelector: null,
                MeIssueCommentViewItem.GetStableKey,
                CommentPageSize,
                int.MaxValue,
                progress =>
                {
                    if (version == _detailLoadVersion && !cancellationToken.IsCancellationRequested)
                    {
                        ReplaceComments(progress.Items, progress.IsFinal);
                        DetailCollectionStatusText = progress.IsFinal
                            ? FormatLoadedStatus(
                                GetString("MyWorkItems.Noun.Comments", "comments"),
                                progress.Items.Count,
                                progress.Completeness)
                            : FormatLoadedStatus(
                                GetString("MyWorkItems.Noun.Comments", "comments"),
                                progress.Items.Count,
                                PagedDataCompleteness.Loading);
                    }
                },
                cancellationToken);
            DetailCollectionStatusText = FormatLoadedStatus(
                GetString("MyWorkItems.Noun.Comments", "comments"),
                commentsResult.Items.Count,
                commentsResult.Completeness);
            StoreSelectedIssueSnapshot(userPartition, owner, repositoryName, "my-issues-detail");
            ScheduleSelectedIssueDwellPrefetch(token, userPartition, owner, repositoryName, item.Issue.Number);
            DetailStatusText = string.Empty;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is GitHubApiException or System.Net.Http.HttpRequestException)
        {
            DetailStatusText = Comments.Count == 0
                ? JitHub.WinUI.Helpers.UserFacingError.For(
                    ex,
                    JitHub.WinUI.Helpers.UserFacingErrorKind.Loading,
                    "my-issues-detail")
                : GetString(
                    "MyWorkItems.Error.DetailRefreshFailedCached",
                    "Detail refresh failed. Showing cached comments.");
            DetailCollectionStatusText = FormatLoadedStatus(
                GetString("MyWorkItems.Noun.Comments", "comments"),
                Comments.Count,
                PagedDataCompleteness.Partial);
        }
        finally
        {
            if (version == _detailLoadVersion)
            {
                IsDetailLoading = false;
            }
        }
    }

    private async Task LoadSelectedPullRequestAsync(
        MeWorkItemViewItem item,
        string token,
        string userPartition,
        string owner,
        string repositoryName,
        int version,
        CancellationToken cancellationToken)
    {
        PullRequestOverviewAggregate? aggregate = await _pullRequestQueryService.GetPullRequestOverviewAsync(
            token,
            userPartition,
            owner,
            repositoryName,
            item.Issue.Number,
            cancellationToken);

        if (version != _detailLoadVersion || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (aggregate is null)
        {
            DetailStatusText = Comments.Count == 0
                ? GetString(
                    "MyPullRequests.Error.DetailLoadFailed",
                    "Could not load pull request details.")
                : GetString(
                    "MyWorkItems.Error.DetailRefreshFailedCached",
                    "Detail refresh failed. Showing cached comments.");
            return;
        }

        _selectedPullRequest = aggregate.PullRequest;
        GitHubIssue issue = aggregate.Issue ?? SelectedIssue ?? item.Issue;
        SelectedIssue = issue;
        ApplySelectedIssue(issue);
        ScheduleSelectedPullRequestDwellPrefetch(token, userPartition, owner, repositoryName, aggregate.PullRequest.Number);
        DetailStatusText = CreatePullRequestOverviewErrorText(aggregate);
        StoreCurrentPullRequestSnapshot(userPartition, owner, repositoryName, "my-pull-requests-overview");
        await EnsureSelectedPullRequestSectionAsync(version, cancellationToken);
    }

    private Task EnsureSelectedPullRequestSectionAsync()
    {
        CancellationToken cancellationToken = _detailCancellationTokenSource?.Token ?? CancellationToken.None;
        return EnsureSelectedPullRequestSectionAsync(_detailLoadVersion, cancellationToken);
    }

    private async Task EnsureSelectedPullRequestSectionAsync(
        int selectionVersion,
        CancellationToken parentCancellationToken)
    {
        if (!IsPullRequestPage ||
            _selectedPullRequest is null ||
            SelectedItem is not { } selectedItem ||
            _loadedPullRequestSections.Contains(_selectedPullRequestSection))
        {
            return;
        }

        string? token = GetActiveToken();
        string userPartition = GetActiveUserPartition(token ?? string.Empty);
        (string owner, string repositoryName) = MeWorkItemViewItem.SplitRepositoryName(selectedItem.RepositoryFullName);
        if (string.IsNullOrWhiteSpace(token) ||
            string.IsNullOrWhiteSpace(userPartition) ||
            string.IsNullOrWhiteSpace(owner) ||
            string.IsNullOrWhiteSpace(repositoryName))
        {
            PullRequestSectionStatusText = string.IsNullOrWhiteSpace(userPartition)
                ? GetString(
                    "MyWorkItems.Status.IdentityLoading",
                    "GitHub account identity is still loading.")
                : GetString(
                    "MyPullRequests.Error.SectionUnavailable",
                    "Pull request section is unavailable.");
            return;
        }

        _pullRequestSectionCancellationTokenSource?.Cancel();
        _pullRequestSectionCancellationTokenSource?.Dispose();
        CancellationTokenSource sectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(parentCancellationToken);
        _pullRequestSectionCancellationTokenSource = sectionCancellation;
        CancellationToken cancellationToken = sectionCancellation.Token;
        int sectionLoadVersion = Interlocked.Increment(ref _pullRequestSectionLoadVersion);
        PullRequestWorkspaceSection section = _selectedPullRequestSection;
        string selectedKey = selectedItem.StableKey;
        int pullRequestNumber = _selectedPullRequest.Number;
        IsPullRequestSectionLoading = true;
        PullRequestSectionStatusText = string.Empty;

        bool IsCurrent() => MeWorkItemRequestGuard.IsCurrent(
            selectionVersion,
            _detailLoadVersion,
            sectionLoadVersion,
            _pullRequestSectionLoadVersion,
            cancellationToken.IsCancellationRequested,
            section,
            _selectedPullRequestSection,
            selectedKey,
            SelectedItem?.StableKey);

        try
        {
            PullRequestSectionState[] states;
            PagedDataCompleteness sectionCompleteness = PagedDataCompleteness.Complete;
            int sectionItemCount = 0;
            int? sectionApiLimit = null;
            switch (section)
            {
                case PullRequestWorkspaceSection.Commits:
                {
                    PullRequestPagedSection<GitHubCommit> result = await _pullRequestQueryService.GetAllPullRequestCommitsAsync(
                        token, userPartition, owner, repositoryName, pullRequestNumber, cancellationToken);
                    if (!IsCurrent())
                    {
                        return;
                    }

                    PullRequestCommits.ApplySnapshot(
                        result.Items,
                        MePullRequestCommitViewItem.GetStableKey,
                        static item => item.StableKey,
                        static commit => new MePullRequestCommitViewItem(commit),
                        static (item, commit) => item.Apply(commit),
                        result.State.ErrorMessage is null
                            ? KeyedCollectionDiffOptions.Default
                            : KeyedCollectionDiffOptions.PreserveMissing);
                    if (RequiresAuthoritativeRefresh(result.State))
                    {
                        result = await _pullRequestQueryService.RefreshAllPullRequestCommitsAsync(
                            token, userPartition, owner, repositoryName, pullRequestNumber, cancellationToken);
                        if (!IsCurrent())
                        {
                            return;
                        }

                        PullRequestCommits.ApplySnapshot(
                            result.Items,
                            MePullRequestCommitViewItem.GetStableKey,
                            static item => item.StableKey,
                            static commit => new MePullRequestCommitViewItem(commit),
                            static (item, commit) => item.Apply(commit),
                            result.State.ErrorMessage is null
                                ? KeyedCollectionDiffOptions.Default
                                : KeyedCollectionDiffOptions.PreserveMissing);
                    }
                    OnPropertyChanged(nameof(HasNoPullRequestCommits));
                    states = [result.State];
                    sectionCompleteness = result.Completeness;
                    sectionItemCount = result.Items.Length;
                    sectionApiLimit = result.ApiLimit;
                    break;
                }
                case PullRequestWorkspaceSection.Reviews:
                {
                    Task<PullRequestPagedSection<GitHubPullRequestReview>> reviewsTask =
                        _pullRequestQueryService.GetAllPullRequestReviewsAsync(
                            token, userPartition, owner, repositoryName, pullRequestNumber, cancellationToken);
                    Task<PullRequestPagedSection<GitHubPullRequestReviewComment>> commentsTask =
                        _pullRequestQueryService.GetAllPullRequestReviewCommentsAsync(
                            token, userPartition, owner, repositoryName, pullRequestNumber, cancellationToken);
                    await Task.WhenAll(reviewsTask, commentsTask);
                    if (!IsCurrent())
                    {
                        return;
                    }

                    PullRequestPagedSection<GitHubPullRequestReview> reviews = await reviewsTask;
                    PullRequestPagedSection<GitHubPullRequestReviewComment> reviewComments = await commentsTask;
                    ApplyPullRequestReviewSections(reviews, reviewComments);
                    if (RequiresAuthoritativeRefresh(reviews.State) || RequiresAuthoritativeRefresh(reviewComments.State))
                    {
                        Task<PullRequestPagedSection<GitHubPullRequestReview>> refreshReviewsTask =
                            _pullRequestQueryService.RefreshAllPullRequestReviewsAsync(
                                token, userPartition, owner, repositoryName, pullRequestNumber, cancellationToken);
                        Task<PullRequestPagedSection<GitHubPullRequestReviewComment>> refreshCommentsTask =
                            _pullRequestQueryService.RefreshAllPullRequestReviewCommentsAsync(
                                token, userPartition, owner, repositoryName, pullRequestNumber, cancellationToken);
                        await Task.WhenAll(refreshReviewsTask, refreshCommentsTask);
                        if (!IsCurrent())
                        {
                            return;
                        }

                        reviews = await refreshReviewsTask;
                        reviewComments = await refreshCommentsTask;
                        ApplyPullRequestReviewSections(reviews, reviewComments);
                    }
                    states = [reviews.State, reviewComments.State];
                    sectionCompleteness = MergeCompleteness(reviews.Completeness, reviewComments.Completeness);
                    sectionItemCount = reviews.Items.Length + reviewComments.Items.Length;
                    break;
                }
                case PullRequestWorkspaceSection.Timeline:
                {
                    PullRequestPagedSection<GitHubIssueEvent> result = await _pullRequestQueryService.GetAllPullRequestTimelineEventsAsync(
                        token, userPartition, owner, repositoryName, pullRequestNumber, cancellationToken);
                    if (!IsCurrent())
                    {
                        return;
                    }

                    PullRequestTimelineEvents.ApplySnapshot(
                        result.Items,
                        MePullRequestTimelineViewItem.GetStableKey,
                        static item => item.StableKey,
                        static timelineEvent => new MePullRequestTimelineViewItem(timelineEvent),
                        static (item, timelineEvent) => item.Apply(timelineEvent),
                        result.State.ErrorMessage is null
                            ? KeyedCollectionDiffOptions.Default
                            : KeyedCollectionDiffOptions.PreserveMissing);
                    if (RequiresAuthoritativeRefresh(result.State))
                    {
                        result = await _pullRequestQueryService.RefreshAllPullRequestTimelineEventsAsync(
                            token, userPartition, owner, repositoryName, pullRequestNumber, cancellationToken);
                        if (!IsCurrent())
                        {
                            return;
                        }

                        PullRequestTimelineEvents.ApplySnapshot(
                            result.Items,
                            MePullRequestTimelineViewItem.GetStableKey,
                            static item => item.StableKey,
                            static timelineEvent => new MePullRequestTimelineViewItem(timelineEvent),
                            static (item, timelineEvent) => item.Apply(timelineEvent),
                            result.State.ErrorMessage is null
                                ? KeyedCollectionDiffOptions.Default
                                : KeyedCollectionDiffOptions.PreserveMissing);
                    }
                    OnPropertyChanged(nameof(HasNoPullRequestTimelineEvents));
                    states = [result.State];
                    sectionCompleteness = result.Completeness;
                    sectionItemCount = result.Items.Length;
                    break;
                }
                default:
                {
                    PullRequestPagedSection<GitHubIssueComment> result = await _pullRequestQueryService.GetAllPullRequestCommentsAsync(
                        token, userPartition, owner, repositoryName, pullRequestNumber, cancellationToken);
                    if (!IsCurrent())
                    {
                        return;
                    }

                    ReplaceComments(result.Items, result.State.ErrorMessage is null);
                    if (RequiresAuthoritativeRefresh(result.State))
                    {
                        result = await _pullRequestQueryService.RefreshAllPullRequestCommentsAsync(
                            token, userPartition, owner, repositoryName, pullRequestNumber, cancellationToken);
                        if (!IsCurrent())
                        {
                            return;
                        }

                        ReplaceComments(result.Items, result.State.ErrorMessage is null);
                    }
                    states = [result.State];
                    DetailCollectionStatusText = FormatLoadedStatus(
                        GetString("MyWorkItems.Noun.Comments", "comments"),
                        result.Items.Length,
                        result.Completeness);
                    sectionCompleteness = result.Completeness;
                    sectionItemCount = result.Items.Length;
                    break;
                }
            }

            PullRequestSectionStatusText = CreatePullRequestSectionStatus(
                section,
                states,
                sectionItemCount,
                sectionCompleteness,
                sectionApiLimit);
            if (states.All(static state => string.IsNullOrWhiteSpace(state.ErrorMessage)))
            {
                _loadedPullRequestSections.Add(section);
            }

            StoreCurrentPullRequestSnapshot(userPartition, owner, repositoryName, $"my-pull-requests-{section.ToString().ToLowerInvariant()}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is GitHubApiException or System.Net.Http.HttpRequestException)
        {
            if (IsCurrent())
            {
                PullRequestSectionStatusText = FormatString(
                    "MyPullRequests.Status.RefreshIncompleteFormat",
                    "{0} loaded; refresh incomplete.",
                    GetVisiblePullRequestSectionCount(section).ToString(CultureInfo.CurrentCulture));
            }
        }
        finally
        {
            if (sectionLoadVersion == _pullRequestSectionLoadVersion)
            {
                IsPullRequestSectionLoading = false;
            }

            if (ReferenceEquals(_pullRequestSectionCancellationTokenSource, sectionCancellation))
            {
                _pullRequestSectionCancellationTokenSource = null;
                sectionCancellation.Dispose();
            }
        }
    }

    private MeWorkItemViewItem CreateItem(GitHubIssue issue)
    {
        return new MeWorkItemViewItem
        {
            Issue = issue,
            Command = new RelayCommand(() => OpenIssue(issue))
        };
    }

    private void OpenSelectedIssueInRepository()
    {
        if (SelectedIssue is not null)
        {
            OpenIssue(SelectedIssue);
        }
        else if (SelectedItem is not null)
        {
            OpenIssue(SelectedItem.Issue);
        }
    }

    private void OpenIssue(GitHubIssue issue)
    {
        string fullName = MeWorkItemViewItem.ExtractRepositoryFullName(issue.RepositoryUrl, issue.HtmlUrl);
        if (string.IsNullOrWhiteSpace(fullName))
        {
            return;
        }

        GitHubRepository repository = CreateMinimalRepository(fullName);
        (string owner, string repositoryName) = MeWorkItemViewItem.SplitRepositoryName(fullName);
        RepoPageType page = IsPullRequestPage || issue.IsPullRequest ? RepoPageType.PullRequestPage : RepoPageType.IssuePage;
        string userPartition = GetActiveUserPartition(GetActiveToken() ?? string.Empty);
        if (page == RepoPageType.PullRequestPage)
        {
            StorePullRequestSnapshot(
                userPartition,
                owner,
                repositoryName,
                CreateMinimalPullRequest(issue, fullName),
                issue,
                "navigation-handoff",
                PullRequestNavigationStoreMode.PreservePopulatedSections);
        }
        else
        {
            StoreIssueSnapshot(userPartition, owner, repositoryName, issue, "navigation-handoff");
        }

        PageNavArg arg = page == RepoPageType.PullRequestPage
            ? new PullRequestPageNavArg(repository, issue.Number)
            : new IssueNavArg(repository, issue.Number);
        bool accepted = _shell.OpenRepositoryTarget(fullName, page, arg);
        _telemetryService.TrackEvent(
            IsPullRequestPage ? "pull_requests.action.executed" : "issues.action.executed",
            new Dictionary<string, string?>
            {
                ["page"] = "my",
                ["action"] = TelemetryTaxonomy.Actions.OpenRepository,
                ["result"] = TelemetryTaxonomy.NavigationResult(accepted)
            });
    }

    public void PrefetchSelectedIssueForNavigation()
    {
        GitHubIssue? issue = SelectedIssue ?? SelectedItem?.Issue;
        if (issue is null)
        {
            return;
        }

        string fullName = MeWorkItemViewItem.ExtractRepositoryFullName(issue.RepositoryUrl, issue.HtmlUrl);
        (string owner, string repositoryName) = MeWorkItemViewItem.SplitRepositoryName(fullName);
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repositoryName))
        {
            return;
        }

        string? token = GetActiveToken();
        string userPartition = GetActiveUserPartition(token ?? string.Empty);
        if (string.IsNullOrWhiteSpace(token))
        {
            if (IsPullRequestPage || issue.IsPullRequest)
            {
                StorePullRequestSnapshot(
                    userPartition,
                    owner,
                    repositoryName,
                    CreateMinimalPullRequest(issue, fullName),
                    issue,
                    "hover-handoff",
                    PullRequestNavigationStoreMode.PreservePopulatedSections);
            }
            else
            {
                StoreIssueSnapshot(userPartition, owner, repositoryName, issue, "hover-handoff");
            }

            return;
        }

        if (IsPullRequestPage || issue.IsPullRequest)
        {
            _telemetryService.TrackEvent(
                "pull_requests.prefetch.started",
                new Dictionary<string, string?>
                {
                    ["page"] = "my",
                    ["source"] = TelemetryTaxonomy.Sources.Hover
                });
            StorePullRequestSnapshot(
                userPartition,
                owner,
                repositoryName,
                CreateMinimalPullRequest(issue, fullName),
                issue,
                "hover-handoff",
                PullRequestNavigationStoreMode.PreservePopulatedSections);
            _ = PrefetchPullRequestForNavigationAsync(
                token, userPartition, owner, repositoryName, issue.Number, PullRequestPrefetchReason.Hover);
        }
        else
        {
            _telemetryService.TrackEvent(
                "issues.prefetch.started",
                new Dictionary<string, string?>
                {
                    ["page"] = "my",
                    ["source"] = TelemetryTaxonomy.Sources.Hover
                });
            _ = PrefetchIssueForNavigationAsync(
                token, userPartition, owner, repositoryName, issue.Number, IssuePrefetchReason.Hover);
        }
    }

    private async Task PrefetchPullRequestForNavigationAsync(
        string token,
        string userPartition,
        string owner,
        string repositoryName,
        int pullRequestNumber,
        PullRequestPrefetchReason reason)
    {
        using IPerformanceTrace trace = _telemetryService.StartTrace(
            "pull_requests.prefetch.completed",
            new Dictionary<string, string?> { ["page"] = "my", ["source"] = TelemetryTaxonomy.EnumValue(reason) });
        try
        {
            PullRequestPrefetchResult result = await _pullRequestNavigationCache.PrefetchAsync(
                token, userPartition, owner, repositoryName, pullRequestNumber, reason);
            trace.SetProperty("result", result switch
            {
                PullRequestPrefetchResult.Success => TelemetryTaxonomy.Results.Success,
                PullRequestPrefetchResult.Cancelled => TelemetryTaxonomy.Results.Cancelled,
                PullRequestPrefetchResult.Failed => TelemetryTaxonomy.Results.Failed,
                _ => TelemetryTaxonomy.Results.Unavailable
            });
        }
        catch (OperationCanceledException)
        {
            trace.SetProperty("result", TelemetryTaxonomy.Results.Cancelled);
        }
        catch (Exception ex) when (ex is GitHubApiException or System.Net.Http.HttpRequestException)
        {
            trace.SetProperty("result", TelemetryTaxonomy.Results.Failed);
        }
        catch
        {
            trace.SetProperty("result", TelemetryTaxonomy.Results.Failed);
        }
    }

    private async Task PrefetchIssueForNavigationAsync(
        string token,
        string userPartition,
        string owner,
        string repositoryName,
        int issueNumber,
        IssuePrefetchReason reason)
    {
        using IPerformanceTrace trace = _telemetryService.StartTrace(
            "issues.prefetch.completed",
            new Dictionary<string, string?> { ["page"] = "my", ["source"] = TelemetryTaxonomy.EnumValue(reason) });
        try
        {
            IssuePrefetchResult result = await _issueNavigationCache.PrefetchAsync(
                token, userPartition, owner, repositoryName, issueNumber, reason);
            trace.SetProperty("result", result switch
            {
                IssuePrefetchResult.Success => TelemetryTaxonomy.Results.Success,
                IssuePrefetchResult.Cancelled => TelemetryTaxonomy.Results.Cancelled,
                IssuePrefetchResult.Failed => TelemetryTaxonomy.Results.Failed,
                _ => TelemetryTaxonomy.Results.Unavailable
            });
        }
        catch (OperationCanceledException)
        {
            trace.SetProperty("result", TelemetryTaxonomy.Results.Cancelled);
        }
        catch (Exception ex) when (ex is GitHubApiException or System.Net.Http.HttpRequestException)
        {
            trace.SetProperty("result", TelemetryTaxonomy.Results.Failed);
        }
        catch
        {
            trace.SetProperty("result", TelemetryTaxonomy.Results.Failed);
        }
    }

    protected Task<CachedResult<GitHubSearchIssuesResponse>> GetIssuesAsync(string token, string userId, string login, GitHubMeIssueFilter filter, GitHubMeWorkItemState state, int page, CancellationToken cancellationToken) =>
        _meQueryService.GetIssuesPageAsync(token, userId, login, filter, PageSize, page, state, cancellationToken);

    protected Task<CachedResult<GitHubSearchIssuesResponse>> RefreshIssuesAsync(string token, string userId, string login, GitHubMeIssueFilter filter, GitHubMeWorkItemState state, int page, CancellationToken cancellationToken) =>
        _meQueryService.RefreshIssuesPageAsync(token, userId, login, filter, PageSize, page, state, cancellationToken);

    protected Task<CachedResult<GitHubSearchIssuesResponse>> GetPullRequestsAsync(string token, string userId, string login, GitHubMePullRequestFilter filter, GitHubMeWorkItemState state, int page, CancellationToken cancellationToken) =>
        _meQueryService.GetPullRequestsPageAsync(token, userId, login, filter, PageSize, page, state, cancellationToken);

    protected Task<CachedResult<GitHubSearchIssuesResponse>> RefreshPullRequestsAsync(string token, string userId, string login, GitHubMePullRequestFilter filter, GitHubMeWorkItemState state, int page, CancellationToken cancellationToken) =>
        _meQueryService.RefreshPullRequestsPageAsync(token, userId, login, filter, PageSize, page, state, cancellationToken);

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

    private static GitHubRepository CreateMinimalRepository(string fullName)
    {
        string[] parts = fullName.Split('/', 2, StringSplitOptions.TrimEntries);
        string owner = parts.Length == 2 ? parts[0] : string.Empty;
        string name = parts.Length == 2 ? parts[1] : fullName;
        return new GitHubRepository
        {
            Name = name,
            FullName = fullName,
            DefaultBranch = "main",
            HtmlUrl = $"https://github.com/{fullName}",
            Owner = new GitHubRepositoryOwner { Login = owner }
        };
    }

    private static GitHubPullRequest CreateMinimalPullRequest(GitHubIssue issue, string repositoryFullName)
    {
        string htmlUrl = issue.PullRequest?.HtmlUrl ?? issue.HtmlUrl;
        return new GitHubPullRequest
        {
            Id = issue.Id,
            Number = issue.Number,
            Title = issue.Title,
            Body = issue.Body,
            State = issue.State,
            HtmlUrl = htmlUrl,
            Comments = issue.Comments,
            CreatedAt = issue.CreatedAt,
            UpdatedAt = issue.UpdatedAt,
            User = issue.User,
            Head = new GitHubPullRequestBranch { GitRef = "head", Label = "head" },
            Base = new GitHubPullRequestBranch { GitRef = "base", Label = repositoryFullName }
        };
    }

    private void ApplySelectedIssue(GitHubIssue issue)
    {
        DetailBodyText = string.IsNullOrWhiteSpace(issue.Body)
            ? GetString("MyWorkItems.Detail.NoDescriptionMarkdown", "_No description provided._")
            : issue.Body!;
        DetailBaseUrl = issue.HtmlUrl;
        SelectedLabels.ApplySnapshot(
            issue.Labels,
            MeLabelViewItem.GetStableKey,
            static item => item.StableKey,
            static label => new MeLabelViewItem(label),
            static (item, label) => item.Apply(label));
        SelectedAssignees.ApplySnapshot(
            issue.Assignees,
            MeActorViewItem.GetStableKey,
            static item => item.StableKey,
            static actor => new MeActorViewItem(actor),
            static (item, actor) => item.Apply(actor));

        NotifyInspectorCollectionsChanged();
    }

    private void ReplaceComments(IEnumerable<GitHubIssueComment> comments, bool removeMissing = true)
    {
        Comments.ApplySnapshot(
            comments,
            MeIssueCommentViewItem.GetStableKey,
            static item => item.StableKey,
            static comment => new MeIssueCommentViewItem(comment),
            static (item, comment) => item.ApplyComment(comment),
            options: removeMissing ? KeyedCollectionDiffOptions.Default : KeyedCollectionDiffOptions.PreserveMissing);

        OnPropertyChanged(nameof(HasComments));
        OnPropertyChanged(nameof(HasNoComments));
    }

    private void ApplyCachedDetailIfAvailable(
        MeWorkItemViewItem item,
        string userPartition,
        string owner,
        string repositoryName)
    {
        if (IsPullRequestPage &&
            _pullRequestNavigationCache.TryGet(
                userPartition,
                owner,
                repositoryName,
                item.Issue.Number,
                out PullRequestNavigationSnapshot pullRequestSnapshot))
        {
            _selectedPullRequest = pullRequestSnapshot.PullRequest;
            GitHubIssue cachedIssue = pullRequestSnapshot.Issue ?? item.Issue;
            SelectedIssue = cachedIssue;
            ApplySelectedIssue(cachedIssue);
            ReplaceComments(pullRequestSnapshot.Comments);
            PullRequestCommits.ApplySnapshot(
                pullRequestSnapshot.Commits,
                MePullRequestCommitViewItem.GetStableKey,
                static commit => commit.StableKey,
                static commit => new MePullRequestCommitViewItem(commit),
                static (item, commit) => item.Apply(commit));
            _selectedPullRequestReviews = pullRequestSnapshot.Reviews;
            _selectedPullRequestReviewComments = pullRequestSnapshot.ReviewComments;
            PullRequestReviews.ApplySnapshot(
                MePullRequestReviewViewItem.CreateSnapshots(pullRequestSnapshot.Reviews, pullRequestSnapshot.ReviewComments),
                static snapshot => snapshot.StableKey,
                static review => review.StableKey,
                static snapshot => new MePullRequestReviewViewItem(snapshot),
                static (item, snapshot) => item.Apply(snapshot));
            PullRequestTimelineEvents.ApplySnapshot(
                pullRequestSnapshot.TimelineEvents,
                MePullRequestTimelineViewItem.GetStableKey,
                static timelineEvent => timelineEvent.StableKey,
                static timelineEvent => new MePullRequestTimelineViewItem(timelineEvent),
                static (item, timelineEvent) => item.Apply(timelineEvent));
            NotifyPullRequestSectionPropertiesChanged();
            return;
        }

        if (!IsPullRequestPage &&
            _issueNavigationCache.TryGet(
                userPartition,
                owner,
                repositoryName,
                item.Issue.Number,
                out IssueNavigationSnapshot issueSnapshot))
        {
            SelectedIssue = issueSnapshot.Issue;
            ApplySelectedIssue(issueSnapshot.Issue);
            ReplaceComments(issueSnapshot.Comments);
            return;
        }

        ReplaceComments([]);
        _selectedPullRequest = null;
        PullRequestCommits.ApplySnapshot(
            [], MePullRequestCommitViewItem.GetStableKey, static item => item.StableKey,
            static commit => new MePullRequestCommitViewItem(commit), static (item, commit) => item.Apply(commit));
        PullRequestReviews.ApplySnapshot(
            [], static snapshot => snapshot.StableKey, static item => item.StableKey,
            static snapshot => new MePullRequestReviewViewItem(snapshot), static (item, snapshot) => item.Apply(snapshot));
        PullRequestTimelineEvents.ApplySnapshot(
            [], MePullRequestTimelineViewItem.GetStableKey, static item => item.StableKey,
            static timelineEvent => new MePullRequestTimelineViewItem(timelineEvent), static (item, timelineEvent) => item.Apply(timelineEvent));
        _selectedPullRequestReviews = [];
        _selectedPullRequestReviewComments = [];
        NotifyPullRequestSectionPropertiesChanged();
    }

    private void ApplyIssueSnapshot(IEnumerable<GitHubIssue> issues, bool removeMissing)
    {
        ListSnapshotApplying?.Invoke(this, EventArgs.Empty);
        string? selectedKey = SelectedItem?.StableKey;
        try
        {
            Items.ApplySnapshot(
                issues,
                MeWorkItemViewItem.GetStableKey,
                static item => item.StableKey,
                CreateItem,
                static (item, issue) => item.ApplyIssue(issue),
                removeMissing ? KeyedCollectionDiffOptions.Default : KeyedCollectionDiffOptions.PreserveMissing);

            MeWorkItemViewItem? selected = selectedKey is null
                ? null
                : Items.FirstOrDefault(item => string.Equals(item.StableKey, selectedKey, StringComparison.Ordinal));
            if (selected is not null)
            {
                SelectedItem = selected;
            }
            else if (removeMissing || SelectedItem is null)
            {
                SelectedItem = Items.FirstOrDefault();
            }

            IsEmpty = Items.Count == 0;
            if (IsEmpty)
            {
                ResetSelectedIssue();
            }
        }
        finally
        {
            ListSnapshotApplied?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateResultCount(
        int loadedCount,
        int totalCount,
        PagedDataCompleteness completeness)
    {
        ResultCountText = MeWorkItemCountFormatter.Format(
            loadedCount,
            totalCount,
            GitHubSearchResultLimit,
            completeness,
            new MeWorkItemCountFormats(
                GetString("MyWorkItems.Count.LoadingFormat", "{0} loading"),
                GetString("MyWorkItems.Count.LoadingOfTotalFormat", "{0} of {1} loading"),
                GetString("MyWorkItems.Count.PartialFormat", "{0} loaded (partial)"),
                GetString("MyWorkItems.Count.PartialOfTotalFormat", "{0} of {1} loaded (partial)"),
                GetString("MyWorkItems.Count.ApiLimitedFormat", "{0} indexed (GitHub API limit)"),
                GetString("MyWorkItems.Count.ApiLimitedOfTotalFormat", "{0} of {1} indexed (GitHub API limit)")));
    }

    private void ExpandDetailBody()
    {
        if (_detailBodyState.Expand())
        {
            NotifyDetailBodyPresentationChanged();
        }
    }

    private void NotifyDetailBodyPresentationChanged()
    {
        OnPropertyChanged(nameof(IsDetailBodyDeferred));
        OnPropertyChanged(nameof(IsDetailBodyMarkdownVisible));
        OnPropertyChanged(nameof(DetailBodyPreviewText));
        ExpandDetailBodyCommand.NotifyCanExecuteChanged();
    }

    private static void AddUniqueIssues(List<GitHubIssue> target, IEnumerable<GitHubIssue> issues)
    {
        HashSet<string> keys = target.Select(MeWorkItemViewItem.GetStableKey).ToHashSet(StringComparer.Ordinal);
        foreach (GitHubIssue issue in issues)
        {
            if (keys.Add(MeWorkItemViewItem.GetStableKey(issue)))
            {
                target.Add(issue);
            }
        }
    }

    private void StoreSelectedIssueSnapshot(
        string userPartition,
        string owner,
        string repositoryName,
        string source)
    {
        GitHubIssue? issue = SelectedIssue ?? SelectedItem?.Issue;
        if (issue is not null)
        {
            StoreIssueSnapshot(userPartition, owner, repositoryName, issue, source);
        }
    }

    private void StoreIssueSnapshot(
        string userPartition,
        string owner,
        string repositoryName,
        GitHubIssue issue,
        string source)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repositoryName) || issue.Number <= 0)
        {
            return;
        }

        GitHubIssueComment[] comments = SelectedIssue?.Number == issue.Number
            ? Comments.Select(static item => item.Comment).ToArray()
            : [];
        _issueNavigationCache.Store(userPartition, new IssueNavigationSnapshot(
            owner,
            repositoryName,
            issue.Number,
            issue,
            comments,
            DateTimeOffset.UtcNow,
            source));
    }

    private void StorePullRequestSnapshot(
        string userPartition,
        string owner,
        string repositoryName,
        GitHubPullRequest pullRequest,
        GitHubIssue? issue,
        string source,
        PullRequestNavigationStoreMode storeMode = PullRequestNavigationStoreMode.Replace)
    {
        StorePullRequestSnapshot(
            userPartition,
            owner,
            repositoryName,
            pullRequest,
            issue,
            [],
            [],
            [],
            [],
            [],
            source,
            storeMode);
    }

    private void StorePullRequestSnapshot(
        string userPartition,
        string owner,
        string repositoryName,
        GitHubPullRequest pullRequest,
        GitHubIssue? issue,
        IReadOnlyList<GitHubIssueComment> comments,
        IReadOnlyList<GitHubCommit> commits,
        IReadOnlyList<GitHubPullRequestReview> reviews,
        IReadOnlyList<GitHubPullRequestReviewComment> reviewComments,
        IReadOnlyList<GitHubIssueEvent> timelineEvents,
        string source,
        PullRequestNavigationStoreMode storeMode = PullRequestNavigationStoreMode.Replace)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repositoryName) || pullRequest.Number <= 0)
        {
            return;
        }

        _pullRequestNavigationCache.Store(userPartition, new PullRequestNavigationSnapshot(
            owner,
            repositoryName,
            pullRequest.Number,
            pullRequest,
            issue,
            [.. comments],
            [.. commits],
            [.. reviews],
            [.. reviewComments],
            [.. timelineEvents],
            DateTimeOffset.UtcNow,
            source), storeMode);
    }

    private void StoreCurrentPullRequestSnapshot(
        string userPartition,
        string owner,
        string repositoryName,
        string source)
    {
        if (_selectedPullRequest is null)
        {
            return;
        }

        StorePullRequestSnapshot(
            userPartition,
            owner,
            repositoryName,
            _selectedPullRequest,
            SelectedIssue,
            Comments.Select(static item => item.Comment).ToArray(),
            PullRequestCommits.Select(static item => item.Commit).ToArray(),
            _selectedPullRequestReviews,
            _selectedPullRequestReviewComments,
            PullRequestTimelineEvents.Select(static item => item.TimelineEvent).ToArray(),
            source);
    }

    private static T[] MergeFailedSection<T>(
        IReadOnlyList<T> incoming,
        IReadOnlyList<T> existing,
        PullRequestSectionState state,
        Func<T, string> keySelector)
    {
        if (string.IsNullOrWhiteSpace(state.ErrorMessage))
        {
            return [.. incoming];
        }

        List<T> merged = [.. existing];
        Dictionary<string, int> indexes = merged
            .Select((item, index) => (Key: keySelector(item), Index: index))
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(static pair => pair.Key, static pair => pair.Index, StringComparer.Ordinal);
        foreach (T item in incoming)
        {
            string key = keySelector(item);
            if (indexes.TryGetValue(key, out int index))
            {
                merged[index] = item;
            }
            else if (!string.IsNullOrWhiteSpace(key))
            {
                indexes[key] = merged.Count;
                merged.Add(item);
            }
        }

        return [.. merged];
    }

    private void ApplyPullRequestReviewSections(
        PullRequestPagedSection<GitHubPullRequestReview> reviews,
        PullRequestPagedSection<GitHubPullRequestReviewComment> reviewComments)
    {
        _selectedPullRequestReviews = MergeFailedSection(
            reviews.Items,
            _selectedPullRequestReviews,
            reviews.State,
            static review => review.Id.ToString(CultureInfo.InvariantCulture));
        _selectedPullRequestReviewComments = MergeFailedSection(
            reviewComments.Items,
            _selectedPullRequestReviewComments,
            reviewComments.State,
            MePullRequestReviewCommentViewItem.GetStableKey);
        MePullRequestReviewSnapshot[] snapshots = MePullRequestReviewViewItem.CreateSnapshots(
            _selectedPullRequestReviews,
            _selectedPullRequestReviewComments);
        PullRequestReviews.ApplySnapshot(
            snapshots,
            static snapshot => snapshot.StableKey,
            static item => item.StableKey,
            static snapshot => new MePullRequestReviewViewItem(snapshot),
            static (item, snapshot) => item.Apply(snapshot));
        OnPropertyChanged(nameof(HasNoPullRequestReviews));
    }

    private static bool RequiresAuthoritativeRefresh(PullRequestSectionState state) =>
        state.IsRefreshInProgress || state.CacheState is CacheState.Stale or CacheState.Refreshing;

    private static PagedDataCompleteness MergeCompleteness(
        PagedDataCompleteness left,
        PagedDataCompleteness right)
    {
        static int Rank(PagedDataCompleteness value) => value switch
        {
            PagedDataCompleteness.Partial => 3,
            PagedDataCompleteness.ApiLimited => 2,
            PagedDataCompleteness.Loading => 1,
            _ => 0
        };
        return Rank(left) >= Rank(right) ? left : right;
    }

    private string CreatePullRequestSectionStatus(
        PullRequestWorkspaceSection section,
        IReadOnlyList<PullRequestSectionState> states,
        int loadedCount,
        PagedDataCompleteness completeness,
        int? apiLimit)
    {
        string error = PullRequestSectionProjectionPolicy.CreateSectionErrorText(section, [.. states]);
        if (!string.IsNullOrWhiteSpace(error))
        {
            return FormatString(
                "MyPullRequests.Status.RefreshIncompleteFormat",
                "{0} loaded; refresh incomplete.",
                loadedCount.ToString(CultureInfo.CurrentCulture));
        }

        string noun = section switch
        {
            PullRequestWorkspaceSection.Commits => GetString("MyWorkItems.Noun.Commits", "commits"),
            PullRequestWorkspaceSection.Reviews => GetString("MyWorkItems.Noun.ReviewItems", "review items"),
            PullRequestWorkspaceSection.Timeline => GetString("MyWorkItems.Noun.Events", "events"),
            _ => GetString("MyWorkItems.Noun.Comments", "comments")
        };
        return FormatLoadedStatus(noun, loadedCount, completeness, apiLimit);
    }

    private string FormatLoadedStatus(
        string noun,
        int loadedCount,
        PagedDataCompleteness completeness,
        int? apiLimit = null)
    {
        string loaded = FormatString(
            "MyWorkItems.Status.LoadedNounFormat",
            "{0} {1} loaded",
            loadedCount.ToString(CultureInfo.CurrentCulture),
            noun);
        return completeness switch
        {
            PagedDataCompleteness.ApiLimited => apiLimit.HasValue
                ? FormatString(
                    "MyWorkItems.Status.ApiLimitedWithLimitFormat",
                    "{0} (GitHub API limit: {1})",
                    loaded,
                    apiLimit.Value.ToString(CultureInfo.CurrentCulture))
                : FormatString("MyWorkItems.Status.ApiLimitedFormat", "{0} (API-limited)", loaded),
            PagedDataCompleteness.Partial => FormatString(
                "MyWorkItems.Status.PartialFormat",
                "{0} (partial)",
                loaded),
            PagedDataCompleteness.Loading => FormatString(
                "MyWorkItems.Status.LoadingFormat",
                "{0}...",
                loaded),
            _ => loaded
        };
    }

    private int GetVisiblePullRequestSectionCount(PullRequestWorkspaceSection section) => section switch
    {
        PullRequestWorkspaceSection.Commits => PullRequestCommits.Count,
        PullRequestWorkspaceSection.Reviews => PullRequestReviews.Count,
        PullRequestWorkspaceSection.Timeline => PullRequestTimelineEvents.Count,
        _ => Comments.Count
    };

    private string CreatePullRequestOverviewErrorText(PullRequestOverviewAggregate aggregate)
    {
        List<string> failedSections = [];
        if (!string.IsNullOrWhiteSpace(aggregate.PullRequestState.ErrorMessage))
        {
            failedSections.Add(GetString("MyPullRequests.Error.Section.PullRequest", "pull request"));
        }

        if (!string.IsNullOrWhiteSpace(aggregate.IssueState.ErrorMessage))
        {
            failedSections.Add(GetString("MyPullRequests.Error.Section.Metadata", "metadata"));
        }

        return failedSections.Count == 0
            ? string.Empty
            : FormatString(
                "MyPullRequests.Error.SectionsRefreshFailedFormat",
                "Could not refresh {0}. Existing content remains available.",
                string.Join(", ", failedSections));
    }

    private void ScheduleSelectedIssueDwellPrefetch(
        string token,
        string userPartition,
        string owner,
        string repositoryName,
        int issueNumber)
    {
        _selectedIssueDwellPrefetch?.Dispose();
        IssueTelemetry.TrackPrefetchStarted(_telemetryService, IssuePrefetchReason.Dwell, "my");
        _selectedIssueDwellPrefetch = _issueNavigationCache.SchedulePrefetch(
            token,
            userPartition,
            owner,
            repositoryName,
            issueNumber,
            IssuePrefetchReason.Dwell,
            TimeSpan.FromSeconds(5),
            (result, duration) => IssueTelemetry.TrackPrefetchCompleted(
                _telemetryService,
                IssuePrefetchReason.Dwell,
                result,
                duration,
                "my"));
    }

    private void ScheduleSelectedPullRequestDwellPrefetch(
        string token,
        string userPartition,
        string owner,
        string repositoryName,
        int pullRequestNumber)
    {
        _selectedIssueDwellPrefetch?.Dispose();
        PullRequestTelemetry.TrackPrefetchStarted(_telemetryService, PullRequestPrefetchReason.Dwell, "my");
        _selectedIssueDwellPrefetch = _pullRequestNavigationCache.SchedulePrefetch(
            token,
            userPartition,
            owner,
            repositoryName,
            pullRequestNumber,
            PullRequestPrefetchReason.Dwell,
            TimeSpan.FromSeconds(5),
            (result, duration) => PullRequestTelemetry.TrackPrefetchCompleted(
                _telemetryService,
                PullRequestPrefetchReason.Dwell,
                result,
                duration,
                "my"));
    }

    private void ResetSelectedIssue()
    {
        SelectedIssue = null;
        _selectedPullRequest = null;
        DetailBodyText = string.Empty;
        DetailStatusText = IsPullRequestPage
            ? GetString("MyPullRequests.Empty.SelectDetail", "Select a pull request.")
            : GetString("MyIssues.Empty.SelectDetail", "Select an issue.");
        SelectedLabels.ApplySnapshot(
            [], MeLabelViewItem.GetStableKey, static item => item.StableKey,
            static label => new MeLabelViewItem(label), static (item, label) => item.Apply(label));
        SelectedAssignees.ApplySnapshot(
            [], MeActorViewItem.GetStableKey, static item => item.StableKey,
            static actor => new MeActorViewItem(actor), static (item, actor) => item.Apply(actor));
        ReplaceComments([]);
        PullRequestCommits.ApplySnapshot(
            [], MePullRequestCommitViewItem.GetStableKey, static item => item.StableKey,
            static commit => new MePullRequestCommitViewItem(commit), static (item, commit) => item.Apply(commit));
        PullRequestReviews.ApplySnapshot(
            [], static snapshot => snapshot.StableKey, static item => item.StableKey,
            static snapshot => new MePullRequestReviewViewItem(snapshot), static (item, snapshot) => item.Apply(snapshot));
        PullRequestTimelineEvents.ApplySnapshot(
            [], MePullRequestTimelineViewItem.GetStableKey, static item => item.StableKey,
            static timelineEvent => new MePullRequestTimelineViewItem(timelineEvent), static (item, timelineEvent) => item.Apply(timelineEvent));
        _selectedPullRequestReviews = [];
        _selectedPullRequestReviewComments = [];
        _loadedPullRequestSections.Clear();
        PullRequestSectionStatusText = string.Empty;
        DetailCollectionStatusText = string.Empty;
        NotifyInspectorCollectionsChanged();
        NotifyPullRequestSectionPropertiesChanged();
    }

    partial void OnStatusTextChanged(string value) => OnPropertyChanged(nameof(IsStatusVisible));

    partial void OnDetailStatusTextChanged(string value) => OnPropertyChanged(nameof(IsDetailStatusVisible));

    partial void OnDetailCollectionStatusTextChanged(string value) => OnPropertyChanged(nameof(IsDetailCollectionStatusVisible));

    private void NotifyFilterPropertiesChanged()
    {
        OnPropertyChanged(nameof(IsAssignedFilterSelected));
        OnPropertyChanged(nameof(IsCreatedFilterSelected));
        OnPropertyChanged(nameof(IsMentionedFilterSelected));
        OnPropertyChanged(nameof(IsOpenStateSelected));
        OnPropertyChanged(nameof(IsClosedStateSelected));
        OnPropertyChanged(nameof(IsAllStateSelected));
    }

    private void NotifyInspectorCollectionsChanged()
    {
        OnPropertyChanged(nameof(HasLabels));
        OnPropertyChanged(nameof(HasNoLabels));
        OnPropertyChanged(nameof(HasAssignees));
        OnPropertyChanged(nameof(HasNoAssignees));
        OnPropertyChanged(nameof(HasMilestone));
        OnPropertyChanged(nameof(MilestoneTitle));
    }

    private void NotifyPullRequestSectionPropertiesChanged()
    {
        OnPropertyChanged(nameof(SelectedPullRequestSection));
        OnPropertyChanged(nameof(IsPullRequestConversationSectionVisible));
        OnPropertyChanged(nameof(IsPullRequestCommitsSectionVisible));
        OnPropertyChanged(nameof(IsPullRequestReviewsSectionVisible));
        OnPropertyChanged(nameof(IsPullRequestTimelineSectionVisible));
        OnPropertyChanged(nameof(HasNoPullRequestCommits));
        OnPropertyChanged(nameof(HasNoPullRequestReviews));
        OnPropertyChanged(nameof(HasNoPullRequestTimelineEvents));
    }

    private static string GetIssueKey(GitHubIssue issue) => MeWorkItemViewItem.GetStableKey(issue);
}

public sealed partial class MyIssuesPageViewModel : MeSearchPageViewModelBase
{
    public MyIssuesPageViewModel()
    {
    }

    internal MyIssuesPageViewModel(
        IAuthService authService,
        IAccountService accountService,
        IGitHubMeQueryService meQueryService,
        IGitHubPullRequestQueryService pullRequestQueryService,
        ShellPageViewModel shell,
        IIssueNavigationCache issueNavigationCache,
        IPullRequestNavigationCache pullRequestNavigationCache,
        ITelemetryService telemetryService)
        : base(
            authService,
            accountService,
            meQueryService,
            pullRequestQueryService,
            shell,
            issueNavigationCache,
            pullRequestNavigationCache,
            telemetryService)
    {
    }

    public override string Title => GetString("MyIssues.Title", "My Issues");

    public override string Subtitle => GetString("MyIssues.Subtitle", "Open issues assigned to you.");

    protected override bool IsPullRequestPage => false;

    protected override Task<CachedResult<GitHubSearchIssuesResponse>> QueryAsync(
        string token,
        string userId,
        string login,
        GitHubMeWorkItemState state,
        int page,
        CancellationToken cancellationToken) =>
        GetIssuesAsync(token, userId, login, IssueFilter, state, page, cancellationToken);

    protected override Task<CachedResult<GitHubSearchIssuesResponse>> RefreshQueryAsync(
        string token,
        string userId,
        string login,
        GitHubMeWorkItemState state,
        int page,
        CancellationToken cancellationToken) =>
        RefreshIssuesAsync(token, userId, login, IssueFilter, state, page, cancellationToken);
}

public sealed partial class MyPullRequestsPageViewModel : MeSearchPageViewModelBase
{
    public MyPullRequestsPageViewModel()
    {
    }

    internal MyPullRequestsPageViewModel(
        IAuthService authService,
        IAccountService accountService,
        IGitHubMeQueryService meQueryService,
        IGitHubPullRequestQueryService pullRequestQueryService,
        ShellPageViewModel shell,
        IIssueNavigationCache issueNavigationCache,
        IPullRequestNavigationCache pullRequestNavigationCache,
        ITelemetryService telemetryService)
        : base(
            authService,
            accountService,
            meQueryService,
            pullRequestQueryService,
            shell,
            issueNavigationCache,
            pullRequestNavigationCache,
            telemetryService)
    {
    }

    public override string Title => GetString("MyPullRequests.Title", "My Pull Requests");

    public override string Subtitle => GetString(
        "MyPullRequests.Subtitle",
        "Open pull requests involving you.");

    protected override bool IsPullRequestPage => true;

    protected override Task<CachedResult<GitHubSearchIssuesResponse>> QueryAsync(
        string token,
        string userId,
        string login,
        GitHubMeWorkItemState state,
        int page,
        CancellationToken cancellationToken) =>
        GetPullRequestsAsync(token, userId, login, GitHubMePullRequestFilter.Involves, state, page, cancellationToken);

    protected override Task<CachedResult<GitHubSearchIssuesResponse>> RefreshQueryAsync(
        string token,
        string userId,
        string login,
        GitHubMeWorkItemState state,
        int page,
        CancellationToken cancellationToken) =>
        RefreshPullRequestsAsync(token, userId, login, GitHubMePullRequestFilter.Involves, state, page, cancellationToken);
}
