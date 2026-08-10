using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services.Markdown;
using MarkdownRenderer.Images;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.GitHub;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.Helpers;
using JitHub.WinUI.ViewModels.Common;

namespace JitHub.WinUI.ViewModels.Pages;

public sealed partial class ProfilePageViewModel : ViewModelBase
{
    private static readonly TimeSpan InitialOverviewDeferral = TimeSpan.FromMilliseconds(75);

    private readonly IAuthService _authService;
    private readonly IAccountService _accountService;
    private readonly IGitHubProfileQueryService _profileQueryService;
    private readonly IExternalUriLauncher _externalUriLauncher;
    private readonly ITelemetryService _telemetryService;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _sectionLoadCancellation;
    private CancellationTokenSource? _mutationCancellation;
    private Task? _overviewLoadTask;
    private readonly HashSet<ProfileWorkspaceMode> _loadedModes = [];
    private readonly Dictionary<ProfileWorkspaceMode, ProfilePagingState> _sectionPagers = new()
    {
        [ProfileWorkspaceMode.Repositories] = new(),
        [ProfileWorkspaceMode.Stars] = new(),
        [ProfileWorkspaceMode.Activity] = new(),
        [ProfileWorkspaceMode.Followers] = new(),
        [ProfileWorkspaceMode.Following] = new()
    };
    private GitHubUser? _currentUser;
    private string _currentAccessToken = GitHubAuthenticationConstants.PublicAccessToken;
    private string _currentUserPartition = "current";
    private string _currentLogin = string.Empty;

    public ProfilePageViewModel()
    {
        _authService = GetService<IAuthService>();
        _accountService = GetService<IAccountService>();
        _profileQueryService = GetService<IGitHubProfileQueryService>();
        _externalUriLauncher = GetService<IExternalUriLauncher>();
        _telemetryService = SafeTelemetryService.Wrap(GetService<ITelemetryService>());
        _taskCoordinator = CommunityToolkit.Mvvm.DependencyInjection.Ioc.Default
            .GetService<IApplicationTaskCoordinator>() ?? new ApplicationTaskCoordinator();
        StatusText = ProfileText.L("Profile.Status.Loading", "Loading profile...");
        DisplayNameText = ProfileText.L("Profile.Identity.DefaultName", "GitHub user");
        LoginText = "@login";
        ReadmeEmptyText = ProfileText.L("Profile.Readme.Empty", "No profile README.");
    }

    public ObservableCollection<ProfileFactItem> Facts { get; } = [];

    public ObservableCollection<ProfileHighlightViewItem> Highlights { get; } = [];

    public KeyedObservableCollection<ProfileRepositoryViewItem, ProfileRepositoryViewItem> PinnedItems { get; } = [];

    public KeyedObservableCollection<ProfileRepositoryViewItem, ProfileRepositoryViewItem> RecentRepositories { get; } = [];

    public KeyedObservableCollection<ProfileRepositoryViewItem, ProfileRepositoryViewItem> StarredRepositories { get; } = [];

    public KeyedObservableCollection<ProfilePersonItem, ProfilePersonItem> Followers { get; } = [];

    public KeyedObservableCollection<ProfilePersonItem, ProfilePersonItem> Following { get; } = [];

    public KeyedObservableCollection<ProfileActivityItem, ProfileActivityItem> PublicActivity { get; } = [];

    public KeyedObservableCollection<ProfileOrganizationViewItem, ProfileOrganizationViewItem> Organizations { get; } = [];

    public ObservableCollection<ProfileContributionWeekViewItem> ContributionWeeks { get; } = [];

    public string PageTitle => ProfileText.L("Profile.Title", "Profile");

    public Uri? ProfileUri { get; private set; }

    public GitHubUser? CurrentUser => _currentUser;

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DisplayNameText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LoginText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string BioText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AvatarUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessageText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusEmojiText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasStatusMessage { get; set; }

    [ObservableProperty]
    public partial string FollowersText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FollowingText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepositoriesText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GistsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReadmeMarkdown { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReadmeBaseUrl { get; set; } = "https://github.com/";

    [ObservableProperty]
    public partial MarkdownDocumentSource? ReadmeDocumentSource { get; set; }

    [ObservableProperty]
    public partial string ReadmeEmptyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasReadme { get; set; }

    [ObservableProperty]
    public partial string ContributionTotalText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ContributionSubtitleText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsOverviewLoading { get; set; }

    [ObservableProperty]
    public partial bool HasIdentity { get; set; }

    [ObservableProperty]
    public partial bool IsEditVisible { get; set; }

    [ObservableProperty]
    public partial bool IsFollowVisible { get; set; }

    [ObservableProperty]
    public partial bool IsFollowing { get; set; }

    [ObservableProperty]
    public partial bool IsOpenProfileEnabled { get; set; }

    [ObservableProperty]
    public partial bool HasOrganizations { get; set; }

    [ObservableProperty]
    public partial bool HasHighlights { get; set; }

    [ObservableProperty]
    public partial bool HasPinnedItems { get; set; }

    [ObservableProperty]
    public partial bool HasRecentRepositories { get; set; }

    [ObservableProperty]
    public partial bool HasStarredRepositories { get; set; }

    [ObservableProperty]
    public partial bool HasFollowers { get; set; }

    [ObservableProperty]
    public partial bool HasFollowing { get; set; }

    [ObservableProperty]
    public partial bool HasPublicActivity { get; set; }

    [ObservableProperty]
    public partial ProfileWorkspaceMode ActiveMode { get; set; } = ProfileWorkspaceMode.Overview;

    public bool IsStatusVisible => !string.IsNullOrWhiteSpace(StatusText);

    public bool IsAuthenticatedProfile => IsEditVisible;

    public bool IsOverviewMode => ActiveMode == ProfileWorkspaceMode.Overview;

    public bool IsRepositoriesMode => ActiveMode == ProfileWorkspaceMode.Repositories;

    public bool IsStarsMode => ActiveMode == ProfileWorkspaceMode.Stars;

    public bool IsActivityMode => ActiveMode == ProfileWorkspaceMode.Activity;

    public bool IsReadmeMode => ActiveMode == ProfileWorkspaceMode.Readme;

    public bool IsFollowersMode => ActiveMode == ProfileWorkspaceMode.Followers;

    public bool IsFollowingMode => ActiveMode == ProfileWorkspaceMode.Following;

    public bool IsPeopleMode => IsFollowersMode || IsFollowingMode;

    public bool ShowRepositoriesEmptyState => IsRepositoriesMode && !IsLoading && !HasRecentRepositories;

    public bool ShowStarsEmptyState => IsStarsMode && !IsLoading && !HasStarredRepositories;

    public bool ShowActivityEmptyState => IsActivityMode && !IsLoading && !HasPublicActivity;

    public bool ShowFollowersEmptyState => IsFollowersMode && !IsLoading && !HasFollowers;

    public bool ShowFollowingEmptyState => IsFollowingMode && !IsLoading && !HasFollowing;

    public string FollowButtonText => IsFollowing
        ? ProfileText.L("Profile.Action.Following", "Following")
        : ProfileText.L("Profile.Action.Follow", "Follow");

    public string ActiveModeTitle => ActiveMode switch
    {
        ProfileWorkspaceMode.Repositories => ProfileText.L("Profile.Mode.Repositories", "Public repositories"),
        ProfileWorkspaceMode.Stars => ProfileText.L("Profile.Mode.Stars", "Public stars"),
        ProfileWorkspaceMode.Activity => ProfileText.L("Profile.Mode.Activity", "Public activity"),
        ProfileWorkspaceMode.Readme => "README",
        ProfileWorkspaceMode.Followers => ProfileText.L("Profile.Mode.Followers", "Followers"),
        ProfileWorkspaceMode.Following => ProfileText.L("Profile.Mode.Following", "Following"),
        _ => ProfileText.L("Profile.Mode.Overview", "Overview")
    };

    public string PeopleTitle => ActiveMode == ProfileWorkspaceMode.Following
        ? ProfileText.L("Profile.Mode.Following", "Following")
        : ProfileText.L("Profile.Mode.Followers", "Followers");

    public string RepositoriesModeLabel => ProfileText.L("Profile.Mode.Repositories", "Public repositories");

    public string StarsModeLabel => ProfileText.L("Profile.Mode.Stars", "Public stars");

    public string ActivityModeLabel => ProfileText.L("Profile.Mode.Activity", "Public activity");

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsStatusVisible));
    }

    partial void OnIsFollowingChanged(bool value)
    {
        OnPropertyChanged(nameof(FollowButtonText));
    }

    partial void OnIsEditVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(IsAuthenticatedProfile));
    }

    partial void OnIsLoadingChanged(bool value) => NotifyEmptyStates();

    partial void OnHasRecentRepositoriesChanged(bool value) => NotifyEmptyStates();

    partial void OnHasStarredRepositoriesChanged(bool value) => NotifyEmptyStates();

    partial void OnHasPublicActivityChanged(bool value) => NotifyEmptyStates();

    partial void OnHasFollowersChanged(bool value) => NotifyEmptyStates();

    partial void OnHasFollowingChanged(bool value) => NotifyEmptyStates();

    partial void OnActiveModeChanged(ProfileWorkspaceMode value)
    {
        OnPropertyChanged(nameof(ActiveModeTitle));
        OnPropertyChanged(nameof(PeopleTitle));
        OnPropertyChanged(nameof(IsOverviewMode));
        OnPropertyChanged(nameof(IsRepositoriesMode));
        OnPropertyChanged(nameof(IsStarsMode));
        OnPropertyChanged(nameof(IsActivityMode));
        OnPropertyChanged(nameof(IsReadmeMode));
        OnPropertyChanged(nameof(IsFollowersMode));
        OnPropertyChanged(nameof(IsFollowingMode));
        OnPropertyChanged(nameof(IsPeopleMode));
        NotifyEmptyStates();
        _telemetryService.TrackEvent("profile.section.opened", new Dictionary<string, string?>
        {
            ["section"] = TelemetryTaxonomy.EnumValue(value),
            ["source"] = "profile_selector"
        });
        _ = EnsureActiveModeLoadedAsync(value);
    }

    private void NotifyEmptyStates()
    {
        OnPropertyChanged(nameof(ShowRepositoriesEmptyState));
        OnPropertyChanged(nameof(ShowStarsEmptyState));
        OnPropertyChanged(nameof(ShowActivityEmptyState));
        OnPropertyChanged(nameof(ShowFollowersEmptyState));
        OnPropertyChanged(nameof(ShowFollowingEmptyState));
    }

    public async Task InitializeAsync(UserProfilePageArgs? args, bool forceRefresh = false)
    {
        Stopwatch loadStopwatch = Stopwatch.StartNew();
        _telemetryService.TrackEvent("profile.opened", new Dictionary<string, string?>
        {
            ["source"] = NormalizeTelemetrySource(args?.Source),
            ["page"] = string.IsNullOrWhiteSpace(args?.Login) ? "authenticated" : "user"
        });
        CancelCurrentMutation();
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _loadCancellation.Token;

        IsLoading = !HasIdentity;
        IsOverviewLoading = true;
        StatusText = ProfileText.L("Profile.Status.Loading", "Loading profile...");

        try
        {
            GitHubUser? authenticated = _authService.AuthenticatedUser ?? await _authService.RefreshAuthenticatedUserAsync();
            string requestedLogin = args?.Login?.Trim() ?? string.Empty;
            bool forceAuthenticatedUser = string.IsNullOrWhiteSpace(requestedLogin)
                || string.Equals(requestedLogin, authenticated?.Login, StringComparison.OrdinalIgnoreCase);
            string? targetLogin = forceAuthenticatedUser ? authenticated?.Login : requestedLogin;
            long tokenUserId = authenticated?.Id ?? _accountService.GetUser();
            string token = _authService.GetToken(tokenUserId) ?? GitHubAuthenticationConstants.PublicAccessToken;
            _currentAccessToken = token;
            _currentUserPartition = tokenUserId.ToString(CultureInfo.InvariantCulture);
            _loadedModes.Clear();
            foreach (ProfilePagingState pager in _sectionPagers.Values)
            {
                pager.Reset();
            }
            ActiveMode = ProfileWorkspaceMode.Overview;

            if (forceAuthenticatedUser && authenticated is not null)
            {
                ApplyIdentity(authenticated, authenticatedView: true);
                IsLoading = false;
            }

            DashboardSectionResult<GitHubUser> identity = await _profileQueryService.GetIdentityAsync(
                token,
                _currentUserPartition,
                targetLogin,
                forceAuthenticatedUser,
                cancellationToken);

            ApplyIdentity(identity.Value, forceAuthenticatedUser);
            IsLoading = false;
            _loadedModes.Add(ProfileWorkspaceMode.Overview);
            StatusText = identity.HasError
                ? ProfileText.L("Profile.Status.CachedIdentity", "Showing cached profile identity.")
                : string.Empty;
            _telemetryService.TrackEvent("profile.loaded", new Dictionary<string, string?>
            {
                ["section"] = "identity",
                ["result"] = identity.HasError ? "cached_error" : "success",
                ["cache_state"] = identity.CacheState.ToString().ToLowerInvariant(),
                ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(loadStopwatch.Elapsed)
            });

            _overviewLoadTask = _taskCoordinator.RunAsync(
                async ownedToken =>
                {
                    try
                    {
                        // Let the cached identity render before projecting the heavier README,
                        // contribution graph, organizations, and highlights collections.
                        await Task.Delay(InitialOverviewDeferral, ownedToken);
                        await LoadOverviewAsync(
                            token,
                            _currentUserPartition,
                            identity.Value.Login,
                            forceAuthenticatedUser,
                            ownedToken);
                    }
                    catch (OperationCanceledException) when (ownedToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Profile overview refresh failed: {ex}");
                        StatusText = ProfileText.L(
                            "Profile.Status.OverviewLoadFailed",
                            "Some profile details could not be refreshed.");
                        IsOverviewLoading = false;
                        throw;
                    }
                },
                new ApplicationTaskOptions("profile.overview", _currentUserPartition),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TrackError("cancelled", loadStopwatch.Elapsed, TelemetryTaxonomy.Results.Cancelled);
        }
        catch (GitHubAuthenticationException)
        {
            StatusText = ProfileText.L(
                "Profile.Status.AuthenticationExpired",
                "GitHub authentication is no longer valid. Please sign in again.");
            TrackError("authentication", loadStopwatch.Elapsed);
            _authService.SignOut();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Profile load failed: {ex}");
            StatusText = ProfileText.L(
                "Profile.Status.LoadFailed",
                "JitHub could not load this profile. Try again.");
            TrackError(GetTelemetryErrorKind(ex), loadStopwatch.Elapsed);
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    private async Task LoadOverviewAsync(
        string accessToken,
        string userPartition,
        string login,
        bool authenticatedView,
        CancellationToken cancellationToken)
    {
        Stopwatch duration = Stopwatch.StartNew();
        try
        {
            GitHubUserProfileSnapshot snapshot = await _profileQueryService.GetProfileAsync(
                accessToken,
                userPartition,
                login,
                authenticatedView,
                cancellationToken);
            ApplySnapshot(snapshot, authenticatedView);
            StatusText = BuildStatus(snapshot);
            bool hasError = snapshot.User.HasError ||
                snapshot.Readme.HasError ||
                snapshot.Contributions.HasError ||
                snapshot.PinnedItems.HasError ||
                snapshot.Organizations.HasError ||
                snapshot.ViewerState.HasError ||
                snapshot.Highlights.HasError;
            _telemetryService.TrackEvent(
                "profile.loaded",
                new Dictionary<string, string?>
                {
                    ["section"] = "overview",
                    ["result"] = hasError
                        ? TelemetryTaxonomy.Results.CachedError
                        : TelemetryTaxonomy.Results.Success,
                    ["cache_state"] = snapshot.User.CacheState.ToString(),
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration.Elapsed)
                });
        }
        catch (OperationCanceledException)
        {
            TrackSectionFailure(
                ProfileWorkspaceMode.Overview,
                TelemetryTaxonomy.Results.Cancelled,
                "cancelled",
                duration.Elapsed);
            throw;
        }
        catch (Exception ex)
        {
            TrackSectionFailure(
                ProfileWorkspaceMode.Overview,
                TelemetryTaxonomy.Results.Error,
                GetTelemetryErrorKind(ex),
                duration.Elapsed);
            throw;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsOverviewLoading = false;
            }
        }
    }

    public ProfileEditDraft CreateEditDraft()
    {
        GitHubUser user = _currentUser ?? new GitHubUser();
        return new ProfileEditDraft
        {
            Name = user.Name ?? string.Empty,
            Bio = user.Bio ?? string.Empty,
            Company = user.Company ?? string.Empty,
            Location = user.Location ?? string.Empty,
            Blog = user.Blog ?? string.Empty,
            TwitterUsername = user.TwitterUsername ?? string.Empty,
            Hireable = user.Hireable ?? false
        };
    }

    public async Task<bool> SaveProfileAsync(
        ProfileEditDraft draft,
        CancellationToken cancellationToken = default)
    {
        long userId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
        string? token = _authService.GetToken(userId);
        if (string.IsNullOrWhiteSpace(token))
        {
            StatusText = ProfileText.L("Profile.Status.SignInToEdit", "Sign in again to edit your profile.");
            return false;
        }

        using CancellationTokenSource mutation = BeginMutation(cancellationToken);
        CancellationToken mutationToken = mutation.Token;
        IsLoading = true;
        try
        {
            GitHubUser updated = await _profileQueryService.UpdateAuthenticatedProfileAsync(
                token,
                userId.ToString(CultureInfo.InvariantCulture),
                new GitHubUserProfileUpdateRequest
                {
                    Name = NormalizeNullable(draft.Name),
                    Bio = NormalizeNullable(draft.Bio),
                    Blog = NormalizeNullable(draft.Blog),
                    TwitterUsername = NormalizeNullable(draft.TwitterUsername),
                    Company = NormalizeNullable(draft.Company),
                    Location = NormalizeNullable(draft.Location),
                    Hireable = draft.Hireable
                },
                mutationToken);

            _authService.AuthenticatedUser = updated;
            await InitializeAsync(new UserProfilePageArgs(updated.Login, updated.Id, "edit"), forceRefresh: true);
            StatusText = ProfileText.L("Profile.Status.Updated", "Profile updated.");
            TrackAction("edit_profile", "success");
            return true;
        }
        catch (OperationCanceledException) when (mutationToken.IsCancellationRequested)
        {
            TrackAction("edit_profile", TelemetryTaxonomy.Results.Cancelled);
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Profile update failed: {ex}");
            StatusText = ProfileText.L(
                "Profile.Status.UpdateFailed",
                "JitHub could not update your profile. Try again.");
            TrackAction("edit_profile", "failed");
            return false;
        }
        finally
        {
            if (CompleteMutation(mutation))
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private Task ReloadAsync()
    {
        string login = _currentUser?.Login ?? _authService.AuthenticatedUser?.Login ?? string.Empty;
        return InitializeAsync(new UserProfilePageArgs(login, _currentUser?.Id, "reload"), forceRefresh: true);
    }

    public void SetActiveMode(ProfileWorkspaceMode mode)
    {
        if (mode == ProfileWorkspaceMode.Stars
            && ProfileNavigationPolicy.GetStatDestination(IsAuthenticatedProfile, ProfileStatKind.Stars)
                == ProfileStatDestinationKind.CanonicalStars)
        {
            OpenStarsLibrary();
            return;
        }

        ActiveMode = mode;
    }

    public void OpenRepositoriesStat()
    {
        if (ProfileNavigationPolicy.GetStatDestination(IsAuthenticatedProfile, ProfileStatKind.Repositories)
            == ProfileStatDestinationKind.CanonicalRepositories)
        {
            OpenAccountRepositories();
            return;
        }

        ActiveMode = ProfileWorkspaceMode.Repositories;
    }

    public void OpenFollowersStat() => ActiveMode = ProfileWorkspaceMode.Followers;

    public void OpenFollowingStat() => ActiveMode = ProfileWorkspaceMode.Following;

    public async Task OpenGistsStatAsync()
    {
        if (ProfileNavigationPolicy.GetStatDestination(IsAuthenticatedProfile, ProfileStatKind.Gists)
            == ProfileStatDestinationKind.CanonicalGists)
        {
            OpenGists();
            return;
        }

        if (!string.IsNullOrWhiteSpace(_currentLogin))
        {
            bool launched = await _externalUriLauncher.LaunchAsync(
                new Uri($"https://gist.github.com/{Uri.EscapeDataString(_currentLogin)}"));
            TrackAction("open_gists", launched ? TelemetryTaxonomy.Results.Success : TelemetryTaxonomy.Results.Unavailable);
        }
    }

    public void ReturnToOverview() => ActiveMode = ProfileWorkspaceMode.Overview;

    public void NotifyStarLibraryChanged(string userId)
    {
        if (!IsEditVisible ||
            !string.Equals(userId, _currentUserPartition, StringComparison.Ordinal))
        {
            return;
        }

        _loadedModes.Remove(ProfileWorkspaceMode.Stars);
        if (ActiveMode == ProfileWorkspaceMode.Stars)
        {
            _ = EnsureActiveModeLoadedAsync(ProfileWorkspaceMode.Stars);
        }
    }

    private async Task EnsureActiveModeLoadedAsync(ProfileWorkspaceMode mode)
    {
        if (_loadedModes.Contains(mode)
            || mode is ProfileWorkspaceMode.Overview or ProfileWorkspaceMode.Readme
            || string.IsNullOrWhiteSpace(_currentLogin))
        {
            return;
        }

        _sectionLoadCancellation?.Cancel();
        _sectionLoadCancellation?.Dispose();
        _sectionLoadCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _sectionLoadCancellation.Token;

        try
        {
            await LoadNextPageAsync(mode, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowSectionLoadError(ex);
        }
    }

    public void ShowSectionLoadError(Exception exception)
    {
        Debug.WriteLine($"Profile section load failed: {exception}");
        StatusText = ProfileText.L(
            "Profile.Status.SectionLoadFailed",
            "JitHub could not load this profile section. Try again.");
    }

    public Task LoadNextPageAsync(ProfileWorkspaceMode mode) =>
        LoadNextPageAsync(mode, _loadCancellation?.Token ?? CancellationToken.None);

    private async Task LoadNextPageAsync(ProfileWorkspaceMode mode, CancellationToken cancellationToken)
    {
        if (!_sectionPagers.TryGetValue(mode, out ProfilePagingState? pager)
            || !pager.TryBegin(out int page))
        {
            return;
        }

        IsLoading = true;
        StatusText = string.Empty;
        Stopwatch sectionDuration = Stopwatch.StartNew();
        try
        {
            switch (mode)
            {
                case ProfileWorkspaceMode.Repositories:
                    int publishedRepositoryCount = RecentRepositories.Count;
                    DashboardSectionResult<GitHubRepository[]> repositories = await _profileQueryService.GetRepositoriesPageAsync(
                        _currentAccessToken, _currentUserPartition, _currentLogin, page, cancellationToken);
                    ProfileSectionPageDecision repositoryDecision = ProfileSectionLoadPolicy.Decide(
                        page, RecentRepositories.Count, repositories.Value.Length, repositories.HasError);
                    if (repositoryDecision.ApplyPage)
                    {
                        ApplyRepositoryPage(RecentRepositories, repositories.Value, page);
                    }

                    if (repositoryDecision.CompletePage)
                    {
                        pager.Complete(page, repositories.Value.Length, GitHubProfilePageSizes.Repositories);
                    }
                    else
                    {
                        pager.Fail();
                    }

                    HasRecentRepositories = RecentRepositories.Count > 0;
                    StatusText = BuildSectionStatus(
                        "public repositories",
                        RecentRepositories.Count,
                        repositories.Value.Length,
                        repositories,
                        visibleRowsAreCached: publishedRepositoryCount == 0 &&
                            repositoryDecision.ApplyPage && repositories.HasError);
                    TrackSectionLoaded(mode, repositories, RecentRepositories.Count, sectionDuration.Elapsed);
                    if (!repositoryDecision.MarkModeLoaded)
                    {
                        return;
                    }

                    break;
                case ProfileWorkspaceMode.Stars:
                    int publishedStarCount = StarredRepositories.Count;
                    DashboardSectionResult<GitHubRepository[]> stars = await _profileQueryService.GetStarredRepositoriesPageAsync(
                        _currentAccessToken, _currentUserPartition, _currentLogin, page, cancellationToken);
                    ProfileSectionPageDecision starsDecision = ProfileSectionLoadPolicy.Decide(
                        page, StarredRepositories.Count, stars.Value.Length, stars.HasError);
                    if (starsDecision.ApplyPage)
                    {
                        ApplyRepositoryPage(StarredRepositories, stars.Value, page);
                    }

                    if (starsDecision.CompletePage)
                    {
                        pager.Complete(page, stars.Value.Length, GitHubProfilePageSizes.Stars);
                    }
                    else
                    {
                        pager.Fail();
                    }

                    HasStarredRepositories = StarredRepositories.Count > 0;
                    StatusText = BuildSectionStatus(
                        "public stars",
                        StarredRepositories.Count,
                        stars.Value.Length,
                        stars,
                        visibleRowsAreCached: publishedStarCount == 0 && starsDecision.ApplyPage && stars.HasError);
                    TrackSectionLoaded(mode, stars, StarredRepositories.Count, sectionDuration.Elapsed);
                    if (!starsDecision.MarkModeLoaded)
                    {
                        return;
                    }

                    break;
                case ProfileWorkspaceMode.Followers:
                    int publishedFollowerCount = Followers.Count;
                    DashboardSectionResult<GitHubUser[]> followers = await _profileQueryService.GetFollowersPageAsync(
                        _currentAccessToken, _currentUserPartition, _currentLogin, page, cancellationToken);
                    ProfileSectionPageDecision followersDecision = ProfileSectionLoadPolicy.Decide(
                        page, Followers.Count, followers.Value.Length, followers.HasError);
                    if (followersDecision.ApplyPage)
                    {
                        ApplyPeoplePage(Followers, followers.Value, page);
                    }

                    if (followersDecision.CompletePage)
                    {
                        pager.Complete(page, followers.Value.Length, GitHubProfilePageSizes.People);
                    }
                    else
                    {
                        pager.Fail();
                    }

                    HasFollowers = Followers.Count > 0;
                    StatusText = BuildSectionStatus(
                        "followers",
                        Followers.Count,
                        followers.Value.Length,
                        followers,
                        visibleRowsAreCached: publishedFollowerCount == 0
                            && followersDecision.ApplyPage && followers.HasError);
                    TrackSectionLoaded(mode, followers, Followers.Count, sectionDuration.Elapsed);
                    if (!followersDecision.MarkModeLoaded)
                    {
                        return;
                    }

                    break;
                case ProfileWorkspaceMode.Following:
                    int publishedFollowingCount = Following.Count;
                    DashboardSectionResult<GitHubUser[]> following = await _profileQueryService.GetFollowingPageAsync(
                        _currentAccessToken, _currentUserPartition, _currentLogin, page, cancellationToken);
                    ProfileSectionPageDecision followingDecision = ProfileSectionLoadPolicy.Decide(
                        page, Following.Count, following.Value.Length, following.HasError);
                    if (followingDecision.ApplyPage)
                    {
                        ApplyPeoplePage(Following, following.Value, page);
                    }

                    if (followingDecision.CompletePage)
                    {
                        pager.Complete(page, following.Value.Length, GitHubProfilePageSizes.People);
                    }
                    else
                    {
                        pager.Fail();
                    }

                    HasFollowing = Following.Count > 0;
                    StatusText = BuildSectionStatus(
                        "following",
                        Following.Count,
                        following.Value.Length,
                        following,
                        visibleRowsAreCached: publishedFollowingCount == 0
                            && followingDecision.ApplyPage && following.HasError);
                    TrackSectionLoaded(mode, following, Following.Count, sectionDuration.Elapsed);
                    if (!followingDecision.MarkModeLoaded)
                    {
                        return;
                    }

                    break;
                case ProfileWorkspaceMode.Activity:
                    int publishedActivityCount = PublicActivity.Count;
                    DashboardSectionResult<GitHubActivityEvent[]> activity = await _profileQueryService.GetPublicActivityPageAsync(
                        _currentAccessToken, _currentUserPartition, _currentLogin, page, cancellationToken);
                    ProfileSectionPageDecision activityDecision = ProfileSectionLoadPolicy.Decide(
                        page, PublicActivity.Count, activity.Value.Length, activity.HasError);
                    if (activityDecision.ApplyPage)
                    {
                        ApplyActivityPage(activity.Value, page);
                    }

                    if (activityDecision.CompletePage)
                    {
                        pager.Complete(
                            page,
                            activity.Value.Length,
                            GitHubProfilePageSizes.Activity,
                            activity.Completeness == PagedDataCompleteness.ApiLimited);
                    }
                    else
                    {
                        pager.Fail();
                    }

                    HasPublicActivity = PublicActivity.Count > 0;
                    StatusText = BuildSectionStatus(
                        "public activity",
                        PublicActivity.Count,
                        activity.Value.Length,
                        activity,
                        visibleRowsAreCached: publishedActivityCount == 0
                            && activityDecision.ApplyPage && activity.HasError);
                    TrackSectionLoaded(mode, activity, PublicActivity.Count, sectionDuration.Elapsed);
                    if (!activityDecision.MarkModeLoaded)
                    {
                        return;
                    }

                    break;
            }

            _loadedModes.Add(mode);
        }
        catch (OperationCanceledException)
        {
            pager.Fail();
            TrackSectionFailure(
                mode,
                TelemetryTaxonomy.Results.Cancelled,
                "cancelled",
                sectionDuration.Elapsed);
            throw;
        }
        catch (Exception ex)
        {
            pager.Fail();
            TrackSectionFailure(
                mode,
                TelemetryTaxonomy.Results.Error,
                GetTelemetryErrorKind(ex),
                sectionDuration.Elapsed);
            throw;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    public void OpenRepository(ProfileRepositoryViewItem? repository)
    {
        if (repository is null || string.IsNullOrWhiteSpace(repository.FullName))
        {
            return;
        }

        if (repository.Kind.Equals("Repository", StringComparison.OrdinalIgnoreCase)
            && repository.FullName.Contains('/', StringComparison.Ordinal))
        {
            ExecuteNavigationAction(
                "open_repository",
                () => GetService<ShellPageViewModel>().OpenRepositoryPage(repository.FullName, "code", null));
        }
    }

    public void OpenAccountRepositories()
    {
        ExecuteNavigationAction("open_repositories", GetService<ShellPageViewModel>().TryOpenManageRepositories);
    }

    public void OpenStarsLibrary()
    {
        if (IsEditVisible)
        {
            ExecuteNavigationAction("open_stars", GetService<ShellPageViewModel>().TryOpenStarsPage);
        }
    }

    public async Task OpenFactAsync(ProfileFactItem? fact)
    {
        Uri? uri = fact?.LaunchUri;
        if (uri is null || !MarkdownLinkNavigationPolicy.IsAllowedLaunchUri(uri))
        {
            return;
        }

        try
        {
            bool launched = await _externalUriLauncher.LaunchAsync(uri);
            TrackAction("open_fact", launched ? TelemetryTaxonomy.Results.Success : TelemetryTaxonomy.Results.Unavailable);
        }
        catch
        {
            StatusText = ProfileText.LF(
                "Profile.Status.FactOpenFailed",
                "Could not open {0}.",
                fact!.Label.ToLower(CultureInfo.CurrentCulture));
            TrackAction("open_fact", "failed");
        }
    }

    public async Task OpenRepositoryExternallyAsync(ProfileRepositoryViewItem? repository)
    {
        if (repository is null || string.IsNullOrWhiteSpace(repository.Url)
            || !Uri.TryCreate(repository.Url, UriKind.Absolute, out Uri? uri))
        {
            return;
        }

        bool launched = await _externalUriLauncher.LaunchAsync(uri);
        TrackAction(
            "open_repository_external",
            launched ? TelemetryTaxonomy.Results.Success : TelemetryTaxonomy.Results.Unavailable);
    }

    public void OpenPerson(ProfilePersonItem? person, string source)
    {
        if (!string.IsNullOrWhiteSpace(person?.Login))
        {
            ExecuteNavigationAction(
                "open_person",
                () => GetService<ShellPageViewModel>().OpenUserProfile(person.Login, source));
        }
    }

    public void OpenOrganization(ProfileOrganizationViewItem? organization)
    {
        if (!string.IsNullOrWhiteSpace(organization?.Login))
        {
            ExecuteNavigationAction(
                "open_organization",
                () => GetService<ShellPageViewModel>().OpenUserProfile(organization.Login, "profile_organization"));
        }
    }

    public void OpenActivity(ProfileActivityItem? activity)
    {
        if (activity is null || string.IsNullOrWhiteSpace(activity.RepositoryFullName))
        {
            return;
        }

        ExecuteNavigationAction(
            "open_activity_repository",
            () => GetService<ShellPageViewModel>().OpenRepositoryPage(activity.RepositoryFullName, "code", null));
    }

    public void OpenGists()
    {
        ExecuteNavigationAction("open_gists", GetService<ShellPageViewModel>().TryOpenGistsPage);
    }

    [RelayCommand]
    private async Task ToggleFollowAsync()
    {
        if (string.IsNullOrWhiteSpace(_currentLogin) || string.IsNullOrWhiteSpace(_currentAccessToken))
        {
            StatusText = ProfileText.L(
                "Profile.Status.RelationshipUnavailable",
                "Profile relationship is unavailable.");
            return;
        }

        using CancellationTokenSource mutation = BeginMutation();
        CancellationToken mutationToken = mutation.Token;
        IsLoading = true;
        try
        {
            if (IsFollowing)
            {
                await _profileQueryService.UnfollowUserAsync(
                    _currentAccessToken,
                    _currentUserPartition,
                    _currentLogin,
                    mutationToken);
                IsFollowing = false;
                StatusText = ProfileText.L("Profile.Status.Unfollowed", "Unfollowed.");
                TrackAction("unfollow", "success");
            }
            else
            {
                await _profileQueryService.FollowUserAsync(
                    _currentAccessToken,
                    _currentUserPartition,
                    _currentLogin,
                    mutationToken);
                IsFollowing = true;
                StatusText = ProfileText.L("Profile.Status.Following", "Following.");
                TrackAction("follow", "success");
            }
        }
        catch (OperationCanceledException) when (mutationToken.IsCancellationRequested)
        {
            TrackAction(IsFollowing ? "unfollow" : "follow", TelemetryTaxonomy.Results.Cancelled);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Profile relationship change failed: {ex}");
            StatusText = ProfileText.L(
                "Profile.Status.RelationshipChangeFailed",
                "JitHub could not change the follow state. Try again.");
            TrackAction(IsFollowing ? "unfollow" : "follow", "failed");
        }
        finally
        {
            if (CompleteMutation(mutation))
            {
                IsLoading = false;
            }
        }
    }

    private CancellationTokenSource BeginMutation(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource next = _loadCancellation is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _loadCancellation.Token);
        CancellationTokenSource? previous = Interlocked.Exchange(
            ref _mutationCancellation,
            next);
        previous?.Cancel();
        return next;
    }

    private bool CompleteMutation(CancellationTokenSource mutation)
    {
        return ReferenceEquals(
            Interlocked.CompareExchange(
                ref _mutationCancellation,
                null,
                mutation),
            mutation);
    }

    private void CancelCurrentMutation()
    {
        CancellationTokenSource? mutation = Interlocked.Exchange(
            ref _mutationCancellation,
            null);
        mutation?.Cancel();
    }

    private void TrackAction(string action, string result) =>
        _telemetryService.TrackEvent("profile.action.executed", new Dictionary<string, string?>
        {
            ["action"] = action,
            ["result"] = result,
            ["page"] = IsEditVisible ? "authenticated" : "user"
        });

    private void ExecuteNavigationAction(string action, Func<bool> navigate)
    {
        try
        {
            bool accepted = navigate();
            TrackAction(
                action,
                TelemetryTaxonomy.NavigationResult(accepted));
        }
        catch
        {
            TrackAction(action, TelemetryTaxonomy.Results.Error);
            throw;
        }
    }

    private void TrackSectionLoaded<T>(
        ProfileWorkspaceMode mode,
        DashboardSectionResult<T> section,
        int visibleCount,
        TimeSpan duration)
        where T : class
    {
        string result = section.HasError
            ? visibleCount > 0
                ? TelemetryTaxonomy.Results.CachedError
                : TelemetryTaxonomy.Results.Error
            : visibleCount == 0
                ? TelemetryTaxonomy.Results.Empty
                : TelemetryTaxonomy.Results.Success;
        _telemetryService.TrackEvent(
            "profile.loaded",
            new Dictionary<string, string?>
            {
                ["section"] = TelemetryTaxonomy.EnumValue(mode),
                ["result"] = result,
                ["cache_state"] = section.CacheState.ToString(),
                ["count_bucket"] = TelemetryTaxonomy.CountBucket(visibleCount),
                ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
            });
    }

    private void TrackSectionFailure(
        ProfileWorkspaceMode mode,
        string result,
        string errorKind,
        TimeSpan duration)
    {
        _telemetryService.TrackEvent(
            "profile.loaded",
            new Dictionary<string, string?>
            {
                ["section"] = TelemetryTaxonomy.EnumValue(mode),
                ["result"] = result,
                ["error_kind"] = errorKind,
                ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
            });
    }

    private static string GetTelemetryErrorKind(Exception exception) => exception switch
    {
        GitHubAuthenticationException => "authentication",
        GitHubApiException => "api",
        HttpRequestException => "network",
        OperationCanceledException => "cancelled",
        _ => "unexpected"
    };

    private void TrackError(
        string errorKind,
        TimeSpan duration,
        string result = TelemetryTaxonomy.Results.Error) =>
        _telemetryService.TrackEvent("profile.error", new Dictionary<string, string?>
        {
            ["error_kind"] = errorKind,
            ["result"] = result,
            ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
        });

    private static string NormalizeTelemetrySource(string? source) => source switch
    {
        "shell" => "shell",
        "edit" => "edit",
        "reload" => "reload",
        "profile_organization" => "organization",
        "profile_followers" => "followers",
        "profile_following" => "following",
        _ => string.IsNullOrWhiteSpace(source) ? "direct" : "avatar"
    };

    private void ApplySnapshot(GitHubUserProfileSnapshot snapshot, bool authenticatedView)
    {
        GitHubUser user = snapshot.User.Value ?? new GitHubUser();
        ApplyIdentity(user, authenticatedView);

        StatusMessageText = snapshot.ViewerState.Value.StatusMessage;
        StatusEmojiText = snapshot.ViewerState.Value.StatusEmoji;
        HasStatusMessage = !string.IsNullOrWhiteSpace(StatusMessageText);
        IsFollowVisible = !authenticatedView && snapshot.ViewerState.Value.ViewerCanFollow;
        IsFollowing = snapshot.ViewerState.Value.ViewerIsFollowing;

        ApplyReadme(snapshot.Readme.Value, user.Login);
        ApplyContributions(snapshot.Contributions.Value);
        ReplaceHighlights(snapshot.Highlights.Value);
        ReplacePinned(snapshot.PinnedItems.Value);
        ApplyOrganizationSection(snapshot.Organizations);

        HasPinnedItems = PinnedItems.Count > 0;
        HasOrganizations = Organizations.Count > 0;
        HasHighlights = Highlights.Count > 0;
    }

    private void ApplyIdentity(GitHubUser user, bool authenticatedView)
    {
        bool identityChanged = !string.IsNullOrWhiteSpace(_currentLogin)
            && !string.Equals(_currentLogin, user.Login, StringComparison.OrdinalIgnoreCase);
        if (identityChanged)
        {
            ResetProfileSections();
        }

        _currentUser = user;
        _currentLogin = user.Login;
        OnPropertyChanged(nameof(CurrentUser));

        DisplayNameText = string.IsNullOrWhiteSpace(user.Name) ? user.Login : user.Name!;
        LoginText = string.IsNullOrWhiteSpace(user.Login) ? "@login" : $"@{user.Login}";
        BioText = string.IsNullOrWhiteSpace(user.Bio)
            ? ProfileText.L("Profile.Identity.NoBio", "No public bio yet.")
            : user.Bio!;
        AvatarUrl = user.AvatarUrl ?? string.Empty;
        FollowersText = FormatCount(user.Followers);
        FollowingText = FormatCount(user.Following);
        RepositoriesText = FormatCount(user.PublicRepos);
        GistsText = FormatCount(user.PublicGists);
        IsEditVisible = authenticatedView;
        OnPropertyChanged(nameof(ActiveModeTitle));

        ProfileUri = Uri.TryCreate(user.HtmlUrl, UriKind.Absolute, out Uri? profileUri) ? profileUri : null;
        IsOpenProfileEnabled = ProfileUri is not null;
        OnPropertyChanged(nameof(ProfileUri));
        ReplaceFacts(user);
        HasIdentity = !string.IsNullOrWhiteSpace(user.Login);
    }

    private void ResetProfileSections()
    {
        ReadmeMarkdown = string.Empty;
        ReadmeDocumentSource = null;
        HasReadme = false;
        ContributionWeeks.Clear();
        ContributionTotalText = string.Empty;
        ContributionSubtitleText = string.Empty;
        PinnedItems.Clear();
        RecentRepositories.Clear();
        StarredRepositories.Clear();
        Followers.Clear();
        Following.Clear();
        PublicActivity.Clear();
        Organizations.Clear();
        Highlights.Clear();
        HasPinnedItems = false;
        HasRecentRepositories = false;
        HasStarredRepositories = false;
        HasFollowers = false;
        HasFollowing = false;
        HasPublicActivity = false;
        HasOrganizations = false;
        HasHighlights = false;
    }

    private void ApplyReadme(GitHubProfileReadme readme, string login)
    {
        HasReadme = readme.Exists && !string.IsNullOrWhiteSpace(readme.Markdown);
        ReadmeMarkdown = readme.Markdown;
        string fallbackBase = string.IsNullOrWhiteSpace(login)
            ? "https://github.com/"
            : $"https://github.com/{login}/{login}/";
        ReadmeBaseUrl = string.IsNullOrWhiteSpace(readme.HtmlUrl)
            ? fallbackBase
            : readme.HtmlUrl;
        ReadmeDocumentSource = string.IsNullOrWhiteSpace(login)
            ? null
            : MarkdownDocumentSourceFactory.CreateRepositoryDocument(
                "profile-readme",
                login,
                login,
                login,
                "HEAD",
                "README.md");
        ReadmeEmptyText = ProfileText.LF(
            "Profile.Readme.RepositoryEmpty",
            "No profile README in {0}.",
            readme.RepositoryFullName);
    }

    private void ApplyContributions(GitHubContributionCalendar calendar)
    {
        ProfileContributionWeekViewItem[] weeks = calendar.Weeks
            .Select(static week => new ProfileContributionWeekViewItem(
                week.Days.Select(ProfileContributionDayViewItem.FromDay).ToArray()))
            .ToArray();
        ApplyIndexedSnapshot(ContributionWeeks, weeks, ProfileContributionWeeksEqual);

        ContributionTotalText = FormatCount(calendar.TotalContributions);
        ContributionSubtitleText = calendar.TotalContributions == 1
            ? ProfileText.L("Profile.Contributions.TotalOne", "1 contribution in the last year")
            : ProfileText.LF(
                "Profile.Contributions.TotalMany",
                "{0} contributions in the last year",
                FormatCount(calendar.TotalContributions));
    }

    private void ReplaceFacts(GitHubUser user)
    {
        List<ProfileFactItem> facts = [];
        AddFact(
            facts,
            ProfileFactKind.Company,
            "\uE77B",
            ProfileText.L("Profile.Fact.Company", "Company"),
            user.Company);
        AddFact(
            facts,
            ProfileFactKind.Location,
            "\uE707",
            ProfileText.L("Profile.Fact.Location", "Location"),
            user.Location);
        AddFact(
            facts,
            ProfileFactKind.Email,
            "\uE715",
            ProfileText.L("Profile.Fact.Email", "Email"),
            user.Email,
            ProfileFactActionPolicy.CreateEmail(user.Email));
        AddFact(
            facts,
            ProfileFactKind.Website,
            "\uE774",
            ProfileText.L("Profile.Fact.Website", "Website"),
            user.Blog,
            ProfileFactActionPolicy.CreateWebsite(user.Blog));
        string? twitter = string.IsNullOrWhiteSpace(user.TwitterUsername) ? null : $"@{user.TwitterUsername.TrimStart('@')}";
        AddFact(
            facts,
            ProfileFactKind.Twitter,
            "\uE8F2",
            ProfileText.L("Profile.Fact.Twitter", "Twitter"),
            twitter,
            ProfileFactActionPolicy.CreateTwitter(user.TwitterUsername));
        AddFact(
            facts,
            ProfileFactKind.Availability,
            "\uE8D4",
            ProfileText.L("Profile.Fact.Availability", "Availability"),
            user.Hireable == true
                ? ProfileText.L("Profile.Fact.AvailableForHire", "Available for hire")
                : null);
        AddFact(
            facts,
            ProfileFactKind.Joined,
            "\uE8E5",
            ProfileText.L("Profile.Fact.Joined", "Joined"),
            user.CreatedAt is null ? null : user.CreatedAt.Value.ToLocalTime().ToString("MMM yyyy", CultureInfo.CurrentCulture));
        ApplyIndexedSnapshot(Facts, facts, static (current, next) => current == next);
    }

    private static void AddFact(
        ICollection<ProfileFactItem> facts,
        ProfileFactKind kind,
        string glyph,
        string label,
        string? text,
        ProfileFactAction? action = null)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            facts.Add(new ProfileFactItem(kind, glyph, label, text.Trim(), action));
        }
    }

    private void ReplaceHighlights(GitHubProfileHighlight[] highlights)
    {
        ProfileHighlightViewItem[] projected = highlights
            .Select(static highlight => new ProfileHighlightViewItem(highlight.Glyph, highlight.Label, highlight.Tone))
            .ToArray();
        ApplyIndexedSnapshot(Highlights, projected, static (current, next) => current == next);
    }

    private static void ApplyIndexedSnapshot<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> source,
        Func<T, T, bool> equals)
    {
        int sharedCount = Math.Min(target.Count, source.Count);
        for (int index = 0; index < sharedCount; index++)
        {
            if (!equals(target[index], source[index]))
            {
                target[index] = source[index];
            }
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }

        for (int index = target.Count; index < source.Count; index++)
        {
            target.Add(source[index]);
        }
    }

    private static bool ProfileContributionWeeksEqual(
        ProfileContributionWeekViewItem current,
        ProfileContributionWeekViewItem next)
    {
        if (current.Days.Length != next.Days.Length)
        {
            return false;
        }

        for (int index = 0; index < current.Days.Length; index++)
        {
            ProfileContributionDayViewItem currentDay = current.Days[index];
            ProfileContributionDayViewItem nextDay = next.Days[index];
            if (currentDay.Date != nextDay.Date
                || currentDay.ContributionCount != nextDay.ContributionCount
                || !string.Equals(currentDay.ColorHex, nextDay.ColorHex, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void ReplacePinned(GitHubPinnedProfileItem[] items)
    {
        ReplaceRepositories(PinnedItems, items.Select(ProfileRepositoryViewItem.FromPinned));
    }

    private static void ReplaceRepositories(
        KeyedObservableCollection<ProfileRepositoryViewItem, ProfileRepositoryViewItem> target,
        IEnumerable<ProfileRepositoryViewItem> items)
    {
        target.ApplySnapshot(
            items,
            static item => item.Key,
            static item => item.Key,
            static item => item,
            static (existing, next) => existing.UpdateFrom(next));
    }

    private static void ApplyRepositoryPage(
        KeyedObservableCollection<ProfileRepositoryViewItem, ProfileRepositoryViewItem> target,
        IEnumerable<GitHubRepository> pageItems,
        int page)
    {
        IEnumerable<ProfileRepositoryViewItem> projected = pageItems.Select(ProfileRepositoryViewItem.FromRepository);
        if (page > 1)
        {
            projected = target
                .Concat(projected)
                .GroupBy(static item => item.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.Last());
        }

        ReplaceRepositories(target, projected);
    }

    private static void ReplacePeople(
        KeyedObservableCollection<ProfilePersonItem, ProfilePersonItem> target,
        IEnumerable<ProfilePersonItem> items)
    {
        target.ApplySnapshot(
            items,
            static item => item.Login,
            static item => item.Login,
            static item => item,
            static (existing, next) => existing.UpdateFrom(next));
    }

    private static void ApplyPeoplePage(
        KeyedObservableCollection<ProfilePersonItem, ProfilePersonItem> target,
        IEnumerable<GitHubUser> pageItems,
        int page)
    {
        IEnumerable<ProfilePersonItem> projected = pageItems.Select(ProfilePersonItem.FromUser);
        if (page > 1)
        {
            projected = target
                .Concat(projected)
                .GroupBy(static item => item.Login, StringComparer.OrdinalIgnoreCase)
                .Select(static group => group.Last());
        }

        ReplacePeople(target, projected);
    }

    private void ReplaceActivity(IEnumerable<ProfileActivityItem> items)
    {
        PublicActivity.ApplySnapshot(
            items,
            static item => item.Key,
            static item => item.Key,
            static item => item,
            static (existing, next) => existing.UpdateFrom(next));
    }

    private void ApplyActivityPage(IEnumerable<GitHubActivityEvent> pageItems, int page)
    {
        IEnumerable<ProfileActivityItem> projected = pageItems.Select(ProfileActivityItem.FromActivity);
        if (page > 1)
        {
            projected = PublicActivity
                .Concat(projected)
                .GroupBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static group => group.Last());
        }

        ReplaceActivity(projected);
    }

    private void ApplyOrganizationSection(DashboardSectionResult<GitHubOrganization[]> section)
    {
        IEnumerable<ProfileOrganizationViewItem> projected = section.Value.Select(static organization =>
            new ProfileOrganizationViewItem(
                organization.Login,
                organization.AvatarUrl ?? string.Empty,
                organization.Description ?? string.Empty));
        if (section.Completeness == PagedDataCompleteness.Partial && Organizations.Count > 0)
        {
            projected = ProfileSectionLoadPolicy.MergePartialSnapshot(
                Organizations,
                projected,
                static item => item.Login);
        }

        Organizations.ApplySnapshot(
            projected,
            static item => item.Login,
            static item => item.Login,
            static item => item,
            static (existing, next) => existing.UpdateFrom(next));
    }

    private string BuildStatus(GitHubUserProfileSnapshot snapshot)
    {
        string[] failedSections =
        [
            snapshot.User.HasError ? ProfileText.L("Profile.Section.Identity", "profile") : string.Empty,
            snapshot.Readme.HasError ? "README" : string.Empty,
            snapshot.Contributions.HasError
                ? ProfileText.L("Profile.Section.Contributions", "contributions")
                : string.Empty,
            snapshot.PinnedItems.HasError
                ? ProfileText.L("Profile.Section.PinnedItems", "pinned items")
                : string.Empty,
            snapshot.ViewerState.HasError
                ? ProfileText.L("Profile.Section.Relationship", "relationship")
                : string.Empty,
            snapshot.Highlights.HasError
                ? ProfileText.L("Profile.Section.Highlights", "highlights")
                : string.Empty
        ];

        List<string> messages = [];
        string[] errors = failedSections.Where(static section => !string.IsNullOrWhiteSpace(section)).ToArray();
        if (errors.Length > 0)
        {
            messages.Add(ProfileText.LF(
                "Profile.Status.SectionsRefreshFailed",
                "Could not refresh {0}.",
                string.Join(", ", errors)));
        }

        string organizationStatus = BuildSectionStatus(
            "organizations",
            Organizations.Count,
            snapshot.Organizations.Value.Length,
            snapshot.Organizations);
        if (!string.IsNullOrWhiteSpace(organizationStatus))
        {
            messages.Add(organizationStatus);
        }

        return string.Join(' ', messages);
    }

    private static string BuildSectionStatus<T>(
        string section,
        int visibleCount,
        int resultItemCount,
        DashboardSectionResult<T> result,
        bool visibleRowsAreCached = false)
        where T : class =>
        ProfileSectionLoadPolicy.FormatStatus(
            section,
            visibleCount,
            resultItemCount,
            result.CacheState,
            result.HasError,
            result.Completeness,
            visibleRowsAreCached);

    internal static string FormatCount(int value) => DashboardRepositoryCardItem.FormatCount(value);

    private static string? NormalizeNullable(string? value) => value?.Trim();
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ProfileEditDraft : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Bio { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Company { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Location { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Blog { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TwitterUsername { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Hireable { get; set; }
}

public sealed record ProfileFactItem(
    ProfileFactKind Kind,
    string Glyph,
    string Label,
    string Text,
    ProfileFactAction? Action)
{
    public bool IsActionable => Action is not null;

    public bool IsPassive => Action is null;

    public Uri? LaunchUri => Action?.LaunchUri;

    public string CopyValue => Action?.CopyValue ?? string.Empty;

    public string OpenLabel => Action?.OpenLabel ?? string.Empty;

    public string CopyLabel => Action?.CopyLabel ?? string.Empty;

    public string AutomationId => $"ProfileFact_{Kind}";

    public string OpenAutomationId => $"{AutomationId}_Open";

    public string CopyAutomationId => $"{AutomationId}_Copy";

    public string AccessibleName => IsActionable
        ? ProfileText.LF("Profile.Fact.AccessibleOpen", "{0}: {1}. {2}", Label, Text, OpenLabel)
        : ProfileText.LF("Profile.Fact.Accessible", "{0}: {1}", Label, Text);
}

public enum ProfileFactKind
{
    Company,
    Location,
    Email,
    Website,
    Twitter,
    Availability,
    Joined
}

public sealed record ProfileHighlightViewItem(string Glyph, string Label, string Tone);

public enum ProfileWorkspaceMode
{
    Overview,
    Repositories,
    Stars,
    Activity,
    Readme,
    Followers,
    Following
}

public enum ProfileRepositoryListKind
{
    Recent,
    Stars,
    Pinned
}

public sealed record ProfileSectionState(CacheState CacheState, bool IsRefreshing, string ErrorMessage);

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ProfileOrganizationViewItem : ObservableObject
{
    public ProfileOrganizationViewItem(string login, string avatarUrl, string description)
    {
        Login = login;
        AvatarUrl = avatarUrl;
        Description = description;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    [NotifyPropertyChangedFor(nameof(AutomationId))]
    public partial string Login { get; set; }

    [ObservableProperty]
    public partial string AvatarUrl { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string Description { get; set; }

    public string AccessibleName => string.IsNullOrWhiteSpace(Description)
        ? ProfileText.LF(
            "Profile.Organization.Accessible",
            "Open {0} organization profile",
            Login)
        : ProfileText.LF(
            "Profile.Organization.AccessibleWithDescription",
            "Open {0} organization profile. {1}",
            Login,
            Description);

    public string AutomationId => $"ProfileOrganization_{ProfileAutomationIdentity.Sanitize(Login)}";

    public bool UpdateFrom(ProfileOrganizationViewItem next)
    {
        bool changed = !string.Equals(Login, next.Login, StringComparison.Ordinal)
            || !string.Equals(AvatarUrl, next.AvatarUrl, StringComparison.Ordinal)
            || !string.Equals(Description, next.Description, StringComparison.Ordinal);
        Login = next.Login;
        AvatarUrl = next.AvatarUrl;
        Description = next.Description;
        return changed;
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ProfileRepositoryViewItem : ObservableObject
{
    public string Key => string.IsNullOrWhiteSpace(FullName) ? $"{Kind}:{Name}" : $"{Kind}:{FullName}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Key))]
    [NotifyPropertyChangedFor(nameof(AutomationId))]
    public partial string Kind { get; set; } = "Repository";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Key))]
    [NotifyPropertyChangedFor(nameof(AutomationId))]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Key))]
    [NotifyPropertyChangedFor(nameof(OwnerLogin))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    [NotifyPropertyChangedFor(nameof(AutomationId))]
    public partial string FullName { get; set; } = string.Empty;

    public string OwnerLogin => FullName.Contains('/', StringComparison.Ordinal)
        ? FullName[..FullName.IndexOf('/', StringComparison.Ordinal)]
        : string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Language { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LanguageColor { get; set; } = RepositoryLanguageColorPalette.DefaultHex;

    [ObservableProperty]
    public partial string StarsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ForksText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdatedText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibilityText))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial bool IsPrivate { get; set; }

    public string VisibilityText => IsPrivate
        ? ProfileText.L("Profile.Repository.Private", "Private")
        : ProfileText.L("Profile.Repository.Public", "Public");

    [ObservableProperty]
    public partial string OwnerAvatarUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Url { get; set; } = string.Empty;

    public string AccessibleName => ProfileText.LF(
        "Profile.Repository.Accessible",
        "Open repository {0}. {1}. {2}",
        FullName,
        VisibilityText,
        Description);

    public string AutomationId => $"ProfileRepository_{ProfileAutomationIdentity.Sanitize(Key)}";

    public bool UpdateFrom(ProfileRepositoryViewItem next)
    {
        bool changed = !string.Equals(Kind, next.Kind, StringComparison.Ordinal)
            || !string.Equals(Name, next.Name, StringComparison.Ordinal)
            || !string.Equals(FullName, next.FullName, StringComparison.Ordinal)
            || !string.Equals(Description, next.Description, StringComparison.Ordinal)
            || !string.Equals(Language, next.Language, StringComparison.Ordinal)
            || !string.Equals(LanguageColor, next.LanguageColor, StringComparison.Ordinal)
            || !string.Equals(StarsText, next.StarsText, StringComparison.Ordinal)
            || !string.Equals(ForksText, next.ForksText, StringComparison.Ordinal)
            || !string.Equals(UpdatedText, next.UpdatedText, StringComparison.Ordinal)
            || IsPrivate != next.IsPrivate
            || !string.Equals(OwnerAvatarUrl, next.OwnerAvatarUrl, StringComparison.Ordinal)
            || !string.Equals(Url, next.Url, StringComparison.Ordinal);
        Kind = next.Kind;
        Name = next.Name;
        FullName = next.FullName;
        Description = next.Description;
        Language = next.Language;
        LanguageColor = next.LanguageColor;
        StarsText = next.StarsText;
        ForksText = next.ForksText;
        UpdatedText = next.UpdatedText;
        IsPrivate = next.IsPrivate;
        OwnerAvatarUrl = next.OwnerAvatarUrl;
        Url = next.Url;
        return changed;
    }

    public static ProfileRepositoryViewItem FromPinned(GitHubPinnedProfileItem item) => new()
    {
        Kind = item.Kind,
        Name = item.Name,
        FullName = string.IsNullOrWhiteSpace(item.NameWithOwner) ? item.Name : item.NameWithOwner,
        Description = string.IsNullOrWhiteSpace(item.Description)
            ? ProfileText.L("Profile.Repository.NoDescription", "No description provided.")
            : item.Description,
        Language = string.IsNullOrWhiteSpace(item.Language)
            ? ProfileText.L("Profile.Repository.UnknownLanguage", "Unknown")
            : item.Language,
        LanguageColor = string.IsNullOrWhiteSpace(item.LanguageColor)
            ? RepositoryLanguageColorPalette.DefaultHex
            : item.LanguageColor,
        StarsText = FormatMetric(item.Stargazers),
        ForksText = FormatMetric(item.Forks),
        UpdatedText = FormatUpdated(item.UpdatedAt),
        IsPrivate = item.IsPrivate,
        Url = item.Url
    };

    public static ProfileRepositoryViewItem FromRepository(GitHubRepository repository) => new()
    {
        Name = repository.Name,
        FullName = repository.FullName,
        Description = string.IsNullOrWhiteSpace(repository.Description)
            ? ProfileText.L("Profile.Repository.NoDescription", "No description provided.")
            : repository.Description!,
        Language = string.IsNullOrWhiteSpace(repository.Language)
            ? ProfileText.L("Profile.Repository.UnknownLanguage", "Unknown")
            : repository.Language!,
        LanguageColor = RepositoryLanguageColorPalette.GetHex(repository.Language),
        StarsText = FormatMetric(repository.StargazersCount),
        ForksText = FormatMetric(repository.ForksCount),
        UpdatedText = FormatUpdated(repository.UpdatedAt),
        IsPrivate = repository.Private,
        OwnerAvatarUrl = repository.Owner.AvatarUrl ?? string.Empty,
        Url = repository.HtmlUrl ?? string.Empty
    };

    private static string FormatMetric(int value) => DashboardRepositoryCardItem.FormatCount(value);

    private static string FormatUpdated(DateTimeOffset? updatedAt) => DashboardRepositoryCardItem.FormatRelativeTime(updatedAt);

}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ProfilePersonItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    [NotifyPropertyChangedFor(nameof(AutomationId))]
    public partial string Login { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string AvatarUrl { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial string Bio { get; set; } = string.Empty;

    public string Subtitle => string.IsNullOrWhiteSpace(Bio) ? $"@{Login}" : Bio;

    public string AccessibleName => ProfileText.LF(
        "Profile.Person.Accessible",
        "Open {0} profile, @{1}",
        DisplayName,
        Login);

    public string AutomationId => $"ProfilePerson_{ProfileAutomationIdentity.Sanitize(Login)}";

    public bool UpdateFrom(ProfilePersonItem next)
    {
        bool changed = !string.Equals(Login, next.Login, StringComparison.Ordinal)
            || !string.Equals(DisplayName, next.DisplayName, StringComparison.Ordinal)
            || !string.Equals(AvatarUrl, next.AvatarUrl, StringComparison.Ordinal)
            || !string.Equals(Bio, next.Bio, StringComparison.Ordinal);
        Login = next.Login;
        DisplayName = next.DisplayName;
        AvatarUrl = next.AvatarUrl;
        Bio = next.Bio;
        return changed;
    }

    public static ProfilePersonItem FromUser(GitHubUser user) => new()
    {
        Login = user.Login,
        DisplayName = string.IsNullOrWhiteSpace(user.Name) ? user.Login : user.Name!,
        AvatarUrl = user.AvatarUrl ?? string.Empty,
        Bio = user.Bio ?? string.Empty
    };
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ProfileActivityItem : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationId))]
    public partial string Key { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string Summary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RepositoryFullName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    public partial string TimeText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Glyph { get; set; } = "\uE7C3";

    [ObservableProperty]
    public partial string AccentKey { get; set; } = "AppAccentBrush";

    public string AccessibleName => ProfileText.LF(
        "Profile.Activity.Accessible",
        "Open {0}. {1}",
        Summary,
        TimeText);

    public string AutomationId => $"ProfileActivity_{ProfileAutomationIdentity.Sanitize(Key)}";

    public bool UpdateFrom(ProfileActivityItem next)
    {
        bool changed = !string.Equals(Key, next.Key, StringComparison.Ordinal)
            || !string.Equals(Summary, next.Summary, StringComparison.Ordinal)
            || !string.Equals(RepositoryFullName, next.RepositoryFullName, StringComparison.Ordinal)
            || !string.Equals(TimeText, next.TimeText, StringComparison.Ordinal)
            || !string.Equals(Glyph, next.Glyph, StringComparison.Ordinal)
            || !string.Equals(AccentKey, next.AccentKey, StringComparison.Ordinal);
        Key = next.Key;
        Summary = next.Summary;
        RepositoryFullName = next.RepositoryFullName;
        TimeText = next.TimeText;
        Glyph = next.Glyph;
        AccentKey = next.AccentKey;
        return changed;
    }

    public static ProfileActivityItem FromActivity(GitHubActivityEvent activity) => new()
    {
        Key = DashboardActivityMerger.CreateStableActivityId(activity),
        Summary = activity.Summary,
        RepositoryFullName = activity.Repo.Name,
        TimeText = DashboardRepositoryCardItem.FormatRelativeTime(activity.CreatedAt),
        Glyph = activity.Type switch
        {
            "PushEvent" => "\uE74A",
            "WatchEvent" => "\uE734",
            "IssuesEvent" => "\uE8A5",
            "PullRequestEvent" => "\uE8EE",
            "ForkEvent" => "\uE8EE",
            _ => "\uE7C3"
        },
        AccentKey = activity.Type switch
        {
            "PushEvent" => "AppAccentBrush",
            "WatchEvent" => "AppWarmAccentBrush",
            "IssuesEvent" => "AppSuccessBrush",
            "PullRequestEvent" => "AppAccentHoverBrush",
            _ => "AppInkMutedBrush"
        }
    };
}

public sealed record ProfileContributionWeekViewItem(ProfileContributionDayViewItem[] Days);

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ProfileContributionDayViewItem
{
    public DateTimeOffset Date { get; init; }

    public int ContributionCount { get; init; }

    public string ColorHex { get; init; } = "#1f2a22";

    public string ToolTipText => ContributionCount == 1
        ? ProfileText.LF(
            "Profile.Contribution.DayOne",
            "1 contribution on {0}",
            Date.ToString("MMM d, yyyy", CultureInfo.CurrentCulture))
        : ProfileText.LF(
            "Profile.Contribution.DayMany",
            "{0} contributions on {1}",
            ContributionCount,
            Date.ToString("MMM d, yyyy", CultureInfo.CurrentCulture));

    public static ProfileContributionDayViewItem FromDay(GitHubContributionDay day) => new()
    {
        Date = day.Date,
        ContributionCount = day.ContributionCount,
        ColorHex = day.Color
    };

}

internal static class ProfileText
{
    public static string L(string key, string fallback) =>
        LocalizedResourceText.GetString(key, fallback);

    public static string LF(string key, string fallback, params object?[] args) =>
        LocalizedResourceText.Format(key, fallback, args);
}

internal static class ProfileAutomationIdentity
{
    public static string Sanitize(string? value)
    {
        string source = value?.Trim() ?? string.Empty;
        if (source.Length == 0)
        {
            return "Unavailable";
        }

        return new string(source.Select(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '_').ToArray());
    }
}
