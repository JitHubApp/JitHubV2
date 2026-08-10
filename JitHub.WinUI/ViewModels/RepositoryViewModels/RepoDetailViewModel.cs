using JitHub.WinUI.Helpers;
using JitHub.Models.NavArgs;
using JitHub.Services;
using JitHub.WinUI.ViewModels.Base;
using JitHub.WinUI.ViewModels.Common;
using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.GitHub;
using JitHub.Models.LegacyGitHub;
using NewRepositoryFork = JitHub.Models.LegacyGitHub.NewRepositoryFork;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Linq;
using System.Diagnostics;

namespace JitHub.WinUI.ViewModels.RepositoryViewModels
{
    public enum RepositoryWorkspaceSection
    {
        Code,
        Issues,
        PullRequests,
        Commits
    }

    public enum RepositoryActionStatusKind
    {
        Informational,
        Warning,
        Error,
        Success
    }

    public sealed record RepositoryNavigationRequest(
        RepositoryWorkspaceSection Section,
        object Parameter);

    public class RepoDetailViewModel : RepoViewModel
    {
        private bool _starred;
        private bool _watching;
        private readonly NavigationService _navigationService;
        private RepositoryWorkspaceSection? _selectedSection;
        private Branch? _selectedBranch;
        private string _branchSearchText = string.Empty;
        private bool _isBranchVisible;
        private bool _isActionStatusVisible;
        private bool _canRetryFork;
        private bool _isForkRetryEnabled = true;
        private string _actionStatusMessage = string.Empty;
        private RepositoryActionStatusKind _actionStatusKind = RepositoryActionStatusKind.Informational;
        private CancellationTokenSource? _repositoryOperationCancellationTokenSource;
        private readonly RepositoryForkOperation<Repository> _forkOperation = new();
        private readonly RepositoryLoadCoordinator _repositoryLoadCoordinator = new();
        private readonly LatestOperationCoordinator _loadingOperationCoordinator = new();
        private string? _forkSourceKey;
        private CancellationTokenSource? _forkRetryCooldownCancellationTokenSource;
        private readonly IGitHubRepositoryQueryService _repositoryQueryService;
        private readonly IAuthService _authService;
        private readonly IAccountService _accountService;
        private readonly IGitHubStarLibraryService _starLibraryService;
        private readonly IGitHubClientService _gitHubClientService;
        private readonly IRepositoryForkOwnershipStore _forkOwnershipStore;
        private readonly ITelemetryService _telemetryService;
        private long _starMutationVersion;
        private long _watchMutationVersion;

        public event EventHandler<RepositoryNavigationRequest>? RepositoryNavigationRequested;

        public bool IsBranchVisible
        {
            get => _isBranchVisible;
            set
            {
                if (SetProperty(ref _isBranchVisible, value))
                {
                    OnPropertyChanged(nameof(IsBranchPickerVisible));
                    OnPropertyChanged(nameof(IsBranchStatusVisible));
                }
            }
        }

        public KeyedObservableCollection<Branch, GitHubBranch> Branches { get; } = [];

        public KeyedObservableCollection<Branch, Branch> FilteredBranches { get; } = [];

        public string BranchSearchText
        {
            get => _branchSearchText;
            set
            {
                if (SetProperty(ref _branchSearchText, value))
                {
                    RefreshFilteredBranches();
                }
            }
        }

        public Branch? SelectedBranch
        {
            get => _selectedBranch;
            set
            {
                if (SetProperty(ref _selectedBranch, value))
                {
                    OnPropertyChanged(nameof(SelectedBranchName));
                }
            }
        }

        public string SelectedBranchName => SelectedBranch?.Name ??
            LocalizedResourceText.GetString("RepoDetail.ChooseBranch", "Choose branch");
        public bool IsStarred
        {
            get => _starred;
            set
            {
                if (SetProperty(ref _starred, value))
                {
                    OnPropertyChanged(nameof(StarActionLabel));
                    OnPropertyChanged(nameof(StarValueText));
                }
            }
        }
        public bool IsWatching
        {
            get => _watching;
            set
            {
                if (SetProperty(ref _watching, value))
                {
                    OnPropertyChanged(nameof(WatchActionLabel));
                    OnPropertyChanged(nameof(WatchValueText));
                }
            }
        }

        public bool IsStarStateKnown
            => _repositoryLoadCoordinator.StarState == RepositoryDataAvailability.Available;

        public bool IsWatchStateKnown
            => _repositoryLoadCoordinator.WatchState == RepositoryDataAvailability.Available;

        public bool IsStarStateUnavailable =>
            _repositoryLoadCoordinator.StarState == RepositoryDataAvailability.Unavailable;

        public bool IsWatchStateUnavailable =>
            _repositoryLoadCoordinator.WatchState == RepositoryDataAvailability.Unavailable;

        public bool IsBranchStateKnown =>
            _repositoryLoadCoordinator.BranchState == RepositoryDataAvailability.Available;

        public bool IsBranchStateUnavailable =>
            _repositoryLoadCoordinator.BranchState == RepositoryDataAvailability.Unavailable;

        public string StarActionLabel =>
            RepositoryActionPresentation.StarLabel(_repositoryLoadCoordinator.StarState, IsStarred);

        public string WatchActionLabel =>
            RepositoryActionPresentation.WatchLabel(_repositoryLoadCoordinator.WatchState, IsWatching);

        public string StarValueText => RepositoryActionPresentation.ValueText(
            _repositoryLoadCoordinator.StarState,
            Model?.StargazersCount ?? 0);

        public string WatchValueText => RepositoryActionPresentation.ValueText(
            _repositoryLoadCoordinator.WatchState,
            Model?.SubscribersCount ?? 0);

        public string BranchStatusText => RepositoryActionPresentation.BranchStatus(
            _repositoryLoadCoordinator.BranchState,
            Branches.Count);

        public bool IsBranchPickerVisible =>
            IsBranchVisible && IsBranchStateKnown && Branches.Count > 0;

        public bool IsBranchStatusVisible =>
            IsBranchVisible && (!IsBranchStateKnown || Branches.Count == 0);

        public bool CanToggleStar => !Loading && _repositoryLoadCoordinator.CanToggleStar;

        public bool CanToggleWatch => !Loading && _repositoryLoadCoordinator.CanToggleWatch;

        public bool CanForkRepository =>
            !Loading &&
            _repositoryLoadCoordinator.CanFork &&
            Model is { Id: > 0 } &&
            (!CanRetryFork || IsForkRetryEnabled);

        public bool CanChangeBranch =>
            !Loading &&
            IsBranchStateKnown &&
            SelectedSection == RepositoryWorkspaceSection.Code &&
            Branches.Count > 0;

        public bool IsActionStatusVisible
        {
            get => _isActionStatusVisible;
            private set => SetProperty(ref _isActionStatusVisible, value);
        }

        public bool CanRetryFork
        {
            get => _canRetryFork;
            private set
            {
                if (SetProperty(ref _canRetryFork, value))
                {
                    OnPropertyChanged(nameof(CanForkRepository));
                }
            }
        }

        public bool IsForkRetryEnabled
        {
            get => _isForkRetryEnabled;
            private set
            {
                if (SetProperty(ref _isForkRetryEnabled, value))
                {
                    OnPropertyChanged(nameof(CanForkRepository));
                }
            }
        }

        public string ActionStatusMessage
        {
            get => _actionStatusMessage;
            private set => SetProperty(ref _actionStatusMessage, value);
        }

        public RepositoryActionStatusKind ActionStatusKind
        {
            get => _actionStatusKind;
            private set => SetProperty(ref _actionStatusKind, value);
        }

        public string RepositoryFullName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Model?.FullName))
                {
                    return Model.FullName;
                }

                if (!string.IsNullOrWhiteSpace(Model?.Owner?.Login) &&
                    !string.IsNullOrWhiteSpace(Model?.Name))
                {
                    return $"{Model.Owner.Login}/{Model.Name}";
                }

                return Model?.Name ?? string.Empty;
            }
        }

        public string RepositoryAutomationStatusText => RepositoryActionAutomationScenario.IsEnabled
            ? $"fork_posts={RepositoryActionAutomationScenario.ForkPostCount}"
            : string.Empty;

        public bool IsRepositoryPrivate => Model?.Private == true;

        public string RepositoryStatusText
        {
            get
            {
                if (Model?.Archived == true)
                {
                    return L("RepoDetail.Status.Archived", "Archived");
                }

                if (Model?.Fork == true)
                {
                    return L("RepoDetail.Status.Fork", "Fork");
                }

                return Model?.Private == true
                    ? L("RepoDetail.Status.Private", "Private")
                    : L("RepoDetail.Status.Public", "Public");
            }
        }

        public bool IsRepositoryIdentityVisible => !string.IsNullOrWhiteSpace(RepositoryFullName);

        public RepositoryWorkspaceSection? SelectedSection
        {
            get => _selectedSection;
            set
            {
                if (SetProperty(ref _selectedSection, value))
                {
                    IsBranchVisible = value == RepositoryWorkspaceSection.Code;
                    OnPropertyChanged(nameof(CanChangeBranch));
                }
            }
        }

        public ICommand ToggleStarCommand { get; }
        public ICommand ForkCommand { get; }
        public ICommand ToggleWatchCommand { get; }
        public RepoDetailViewModel()
        {
            _navigationService = Ioc.Default.GetService<NavigationService>()
                ?? throw new InvalidOperationException("NavigationService is not registered.");
            _repositoryQueryService = Ioc.Default.GetService<IGitHubRepositoryQueryService>()
                ?? throw new InvalidOperationException("IGitHubRepositoryQueryService is not registered.");
            _authService = Ioc.Default.GetService<IAuthService>()
                ?? throw new InvalidOperationException("IAuthService is not registered.");
            _accountService = Ioc.Default.GetService<IAccountService>()
                ?? throw new InvalidOperationException("IAccountService is not registered.");
            _starLibraryService = Ioc.Default.GetService<IGitHubStarLibraryService>()
                ?? throw new InvalidOperationException("IGitHubStarLibraryService is not registered.");
            _gitHubClientService = Ioc.Default.GetService<IGitHubClientService>()
                ?? throw new InvalidOperationException("IGitHubClientService is not registered.");
            _forkOwnershipStore = Ioc.Default.GetService<IRepositoryForkOwnershipStore>()
                ?? throw new InvalidOperationException("IRepositoryForkOwnershipStore is not registered.");
            _telemetryService = SafeTelemetryService.Wrap(
                Ioc.Default.GetService<ITelemetryService>()
                    ?? throw new InvalidOperationException("ITelemetryService is not registered."));
            ToggleStarCommand = new AsyncRelayCommand(ToggleStar);
            ForkCommand = new AsyncRelayCommand(ForkRepo);
            ToggleWatchCommand = new AsyncRelayCommand(ToggleWatch);
        }

        private void GoToCodePage(CodeViewerNavArg arg)
        {
            NavigateRepositoryView(RepositoryWorkspaceSection.Code, arg.WithRepo(Model));
        }

        private void GoToCodePage()
        {
            NavigateRepositoryView(
                RepositoryWorkspaceSection.Code,
                CodeViewerNavArg.CreateWithBranch(Model, SelectedBranch?.Name ?? Model.DefaultBranch));
        }

        private void GoToCodePageWithBranch(string branch)
        {
            NavigateRepositoryView(
                RepositoryWorkspaceSection.Code,
                CodeViewerNavArg.CreateWithBranch(Model, branch));
        }

        private void GoToIssuesPage(IssueNavArg arg)
        {
            NavigateRepositoryView(RepositoryWorkspaceSection.Issues, arg.WithRepo(Model));
        }
        
        private void GoToIssuesPage()
        {
            NavigateRepositoryView(RepositoryWorkspaceSection.Issues, new IssueNavArg(Model, 0));
        }

        private void GoToPullRequestPage()
        {
            NavigateRepositoryView(
                RepositoryWorkspaceSection.PullRequests,
                new PullRequestPageNavArg(Model, 0));
        }

        private void GoToPullRequestPage(PullRequestPageNavArg arg)
        {
            NavigateRepositoryView(RepositoryWorkspaceSection.PullRequests, arg.WithRepo(Repo));
        }

        private void GoToCommitsPage()
        {
            NavigateRepositoryView(
                RepositoryWorkspaceSection.Commits,
                CommitPageNavArg.CreateWithBranch(Model, Model.DefaultBranch));
        }

        private void GoToCommitsPage(CommitPageNavArg arg)
        {
            NavigateRepositoryView(RepositoryWorkspaceSection.Commits, arg.WithRepo(Model));
        }

        private void NavigateRepositoryView(RepositoryWorkspaceSection section, object parameter)
        {
            SelectedSection = section;
            RepositoryNavigationRequested?.Invoke(this, new RepositoryNavigationRequest(section, parameter));
        }

        public void ReportChildNavigationFailure(Exception exception)
        {
            App.LogHandledException(exception, "repository-navigation");
            ShowActionStatus(
                LocalizedResourceText.GetString(
                    "RepoDetail.ChildNavigationFailed",
                    "Could not open this repository view."),
                RepositoryActionStatusKind.Error);
        }

        public async Task InitializeAsync(RepoDetailPageArgs args)
        {
            ResetOperationCancellation();
            try
            {
                await NavigateToRepositoryAsync(
                    args,
                    _repositoryOperationCancellationTokenSource!.Token,
                    resetPendingFork: true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to navigate repository detail: {ex}");
                App.LogHandledException(ex, "repository-navigation");
                ShowActionStatus(
                    LocalizedResourceText.GetString(
                        "RepoDetail.ChildNavigationFailed",
                        "Could not open this repository view."),
                    RepositoryActionStatusKind.Error);
            }
        }

        private async Task<long> NavigateToRepositoryAsync(
            RepoDetailPageArgs args,
            CancellationToken cancellationToken,
            bool resetPendingFork)
        {
            long generation = BeginRepositoryTransition(args, resetPendingFork);
            long loadingOwner = BeginLoadingState();
            try
            {
                await HandleNavigatedTo(args, generation, cancellationToken);
                return generation;
            }
            finally
            {
                if (_repositoryLoadCoordinator.Complete(generation))
                {
                    CompleteLoadingState(loadingOwner);
                    NotifyRepositoryActionAvailabilityChanged();
                }
            }
        }

        private async Task HandleNavigatedTo(
            RepoDetailPageArgs args,
            long generation,
            CancellationToken cancellationToken)
        {
            ProductPerformanceReadiness.RecordTraversalStage("repo_detail.resolve.begin");
            RepositoryQueryContext queryContext = GetRepositoryQueryContext();
            ResolvedRepository resolved;
            if (args.Repo != null)
            {
                resolved = await ResolveRepositoryAsync(
                    args.Repo,
                    queryContext,
                    cancellationToken);
                Repository resolvedRepositoryValue = resolved.Value;
                if (string.IsNullOrWhiteSpace(resolvedRepositoryValue.DefaultBranch) &&
                    !string.IsNullOrWhiteSpace(args.Repo.DefaultBranch))
                {
                    resolvedRepositoryValue.DefaultBranch = args.Repo.DefaultBranch;
                }
            }
            else
            {
                if (!TryCreateRepositoryReference(args.FullName, out GitHubRepository? repositoryReference))
                {
                    _navigationService.GoHome();
                    return;
                }

                resolved = await ResolveRepositoryAsync(
                    repositoryReference!,
                    queryContext,
                    cancellationToken);
            }

            _repositoryLoadCoordinator.ThrowIfStale(generation, cancellationToken);
            ProductPerformanceReadiness.RecordTraversalStage("repo_detail.resolve.end");
            Repository resolvedRepository = resolved.Value;
            if (IsSameRepository(resolvedRepository, Model))
            {
                // BeginRepositoryTransition already published a complete navigation
                // fallback. Replacing it with an equivalent copy invalidates every
                // repository-header binding before the primary child can paint.
                // The existing promotion path merges richer metadata in place later.
                resolvedRepository = Model;
            }
            else
            {
                SetRepositoryModel(resolvedRepository);
            }
            ProductPerformanceReadiness.RecordTraversalStage("repo_detail.model.ready");
            if (!_repositoryLoadCoordinator.MarkRepositoryAvailable(generation))
            {
                throw new OperationCanceledException(
                    "A newer repository navigation superseded this load.",
                    cancellationToken);
            }

            NotifyRepositoryActionAvailabilityChanged();

            args.Ref.WithRepo(resolvedRepository);

            string owner = resolvedRepository.Owner.Login;
            string name = resolvedRepository.Name;
            switch (args.Page)
            {
                case RepoPageType.CodePage:
                    ProductPerformanceReadiness.RecordTraversalStage("repo_detail.child.begin");
                    GoToCodePage(ResolveInitialCodeViewerArg((CodeViewerNavArg)args.Ref));
                    ProductPerformanceReadiness.RecordTraversalStage("repo_detail.child.end");
                    break;
                //TODO: add cases for other page types
                case RepoPageType.IssuePage:
                    GoToIssuesPage((IssueNavArg)args.Ref);
                    break;
                case RepoPageType.PullRequestPage:
                    GoToPullRequestPage((PullRequestPageNavArg)args.Ref);
                    if (JitHub.WinUI.Program.CurrentLaunchOptions.IsPublicPreviewOverride)
                    {
                        _ = EnsurePreviewPullRequestPageAsync((PullRequestPageNavArg)args.Ref);
                    }
                    break;
                case RepoPageType.CommitPage:
                    GoToCommitsPage((CommitPageNavArg)args.Ref);
                    break;
                default:
                    break;
            }

            if (resolved.ShouldPromote)
            {
                // Promotion is ancillary to navigation. Its cache/query setup can run
                // synchronously until the first await, so schedule it beyond the first
                // composition window instead of occupying the frame that paints the child.
                _ = PromoteRepositoryAfterFirstFrameAsync(
                    queryContext,
                    owner,
                    name,
                    resolvedRepository.Id,
                    generation,
                    Volatile.Read(ref _starMutationVersion),
                    Volatile.Read(ref _watchMutationVersion),
                    cancellationToken);
            }

            // The destination owns perceived navigation readiness. Let its first frame
            // render before starting ancillary branch/star/watch projections, whose cache
            // lookups may perform synchronous SQLite work before their first await.
            await Task.Yield();
            _repositoryLoadCoordinator.ThrowIfStale(generation, cancellationToken);

            long starMutationVersion = Volatile.Read(ref _starMutationVersion);
            long watchMutationVersion = Volatile.Read(ref _watchMutationVersion);
            Task<CachedResult<GitHubBranch[]>> branchesTask = _repositoryQueryService.GetBranchesPageAsync(
                queryContext.AccessToken,
                queryContext.UserId,
                owner,
                name,
                1,
                QueryFetchPolicy.StaleFirst,
                GitHubRequestPriority.Visible,
                cancellationToken);
            Task<CachedResult<GitHubResourceState>> starredTask = _repositoryQueryService.GetStarStateAsync(
                queryContext.AccessToken,
                queryContext.UserId,
                owner,
                name,
                QueryFetchPolicy.StaleFirst,
                cancellationToken);
            Task<CachedResult<GitHubRepositorySubscription>> watchingTask = _repositoryQueryService.GetWatchStateAsync(
                queryContext.AccessToken,
                queryContext.UserId,
                owner,
                name,
                QueryFetchPolicy.StaleFirst,
                cancellationToken);

            try
            {
                CachedResult<GitHubBranch[]> branchResult = await branchesTask;
                GitHubBranch[] firstPage = branchResult.Value ?? [];
                _repositoryLoadCoordinator.ThrowIfStale(generation, cancellationToken);
                ApplyBranchSnapshot(firstPage, removeMissing: firstPage.Length < GitHubRepositoryQueryService.BranchPageSize);
                SelectedBranch = ResolveSelectedBranch(args);
                _repositoryLoadCoordinator.MarkBranchStateKnown(generation);
                NotifyRepositoryDataStatesChanged();
                bool promoteBranches = RepositoryQueryRefreshPolicy.ShouldPromote(branchResult);
                if (promoteBranches)
                {
                    _ = PromoteAllBranchesAsync(queryContext, owner, name, generation, cancellationToken);
                }
                else if (firstPage.Length == GitHubRepositoryQueryService.BranchPageSize)
                {
                    _ = LoadRemainingBranchesAsync(
                        queryContext,
                        owner,
                        name,
                        generation,
                        firstPage
                            .Where(branch => !string.IsNullOrWhiteSpace(branch.Name))
                            .Select(branch => branch.Name)
                            .ToHashSet(StringComparer.Ordinal),
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                _repositoryLoadCoordinator.ThrowIfStale(generation, cancellationToken);
                bool preservedBranches = IsBranchStateKnown;
                if (!preservedBranches)
                {
                    _repositoryLoadCoordinator.MarkBranchStateUnavailable(generation);
                }
                NotifyRepositoryDataStatesChanged();
                ShowActionStatus(
                    preservedBranches
                        ? L(
                            "RepoDetail.Branches.RefreshFailedCached",
                            "Could not refresh branches. Showing the previous branch list.")
                        : L(
                            "RepoDetail.Branches.Unavailable",
                            "Branches are temporarily unavailable. Existing repository content remains visible."),
                    RepositoryActionStatusKind.Warning);
            }
            EnsureCodePageBranchAlignment(args);

            try
            {
                CachedResult<GitHubResourceState> result = await starredTask;
                bool isStarred = result.Value?.Exists == true;
                _repositoryLoadCoordinator.ThrowIfStale(generation, cancellationToken);
                if (starMutationVersion == Volatile.Read(ref _starMutationVersion) &&
                    _repositoryLoadCoordinator.MarkStarStateKnown(generation))
                {
                    ApplyStarDisplayState(Model, isStarred, Model.StargazersCount);
                    NotifyRepositoryDataStatesChanged();
                }
                if (RepositoryQueryRefreshPolicy.ShouldPromote(result))
                {
                    _ = PromoteStarStateAsync(
                        queryContext,
                        owner,
                        name,
                        generation,
                        starMutationVersion,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                _repositoryLoadCoordinator.ThrowIfStale(generation, cancellationToken);
                bool preservedStarState = IsStarStateKnown;
                if (!preservedStarState)
                {
                    _repositoryLoadCoordinator.MarkStarStateUnavailable(generation);
                }
                NotifyRepositoryDataStatesChanged();
                ShowActionStatus(
                    preservedStarState
                        ? L(
                            "RepoDetail.Star.RefreshFailedCached",
                            "Could not refresh star state. Showing the previous state.")
                        : L("RepoDetail.Star.Unavailable", "Star state is temporarily unavailable."),
                    RepositoryActionStatusKind.Warning);
            }

            try
            {
                CachedResult<GitHubRepositorySubscription> result = await watchingTask;
                GitHubRepositorySubscription? subscription = result.Value;
                bool isWatching = subscription?.Subscribed == true && subscription.Ignored != true;
                _repositoryLoadCoordinator.ThrowIfStale(generation, cancellationToken);
                if (watchMutationVersion == Volatile.Read(ref _watchMutationVersion) &&
                    _repositoryLoadCoordinator.MarkWatchStateKnown(generation))
                {
                    ApplyWatchDisplayState(Model, isWatching, Model.SubscribersCount);
                    NotifyRepositoryDataStatesChanged();
                }
                if (RepositoryQueryRefreshPolicy.ShouldPromote(result))
                {
                    _ = PromoteWatchStateAsync(
                        queryContext,
                        owner,
                        name,
                        generation,
                        watchMutationVersion,
                        cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                _repositoryLoadCoordinator.ThrowIfStale(generation, cancellationToken);
                bool preservedWatchState = IsWatchStateKnown;
                if (!preservedWatchState)
                {
                    _repositoryLoadCoordinator.MarkWatchStateUnavailable(generation);
                }
                NotifyRepositoryDataStatesChanged();
                ShowActionStatus(
                    preservedWatchState
                        ? L(
                            "RepoDetail.Watch.RefreshFailedCached",
                            "Could not refresh watch state. Showing the previous state.")
                        : L("RepoDetail.Watch.Unavailable", "Watch state is temporarily unavailable."),
                    RepositoryActionStatusKind.Warning);
            }

            if (JitHub.WinUI.Program.CurrentLaunchOptions.IsPublicPreviewOverride &&
                args.Page == RepoPageType.PullRequestPage &&
                args.Ref is PullRequestPageNavArg pullRequestPageArg)
            {
                GoToPullRequestPage(pullRequestPageArg);
            }
        }

        private long BeginRepositoryTransition(RepoDetailPageArgs args, bool resetPendingFork)
        {
            bool preserveAvailableState = IsSameRepository(args, Model);
            long generation = _repositoryLoadCoordinator.Begin(preserveAvailableState);
            if (resetPendingFork)
            {
                _forkOperation.Reset();
                _forkSourceKey = null;
            }

            CancelForkRetryCooldown();
            IsActionStatusVisible = false;
            CanRetryFork = false;
            IsForkRetryEnabled = true;
            if (!preserveAvailableState)
            {
                // Clear action selection without mutating the repository object that belongs to
                // the previous route. That object may still be visible in shell/history caches.
                ApplyStarDisplayState(repository: null, isStarred: false, count: 0);
                ApplyWatchDisplayState(repository: null, isWatching: false, count: 0);

                Branches.Clear();
                FilteredBranches.Clear();
                SelectedBranch = null;
                SelectedSection = null;

                GitHubRepository? repositoryReference = args.Repo;
                if (repositoryReference is null)
                {
                    TryCreateRepositoryReference(args.FullName, out repositoryReference);
                }

                SetRepositoryModel(repositoryReference is null
                    ? new Repository()
                    : CreateFallbackRepository(repositoryReference));
            }
            NotifyRepositoryDataStatesChanged();
            NotifyRepositoryActionAvailabilityChanged();
            return generation;
        }

        private async Task<ResolvedRepository> ResolveRepositoryAsync(
            GitHubRepository repo,
            RepositoryQueryContext queryContext,
            CancellationToken cancellationToken)
        {
            if (RepositoryNavigationMetadataPolicy.CanNavigateImmediately(repo))
            {
                return new ResolvedRepository(CreateFallbackRepository(repo), ShouldPromote: true);
            }

            if (TryGetRepositoryParts(repo, out string owner, out string name))
            {
                try
                {
                    CachedResult<GitHubRepository> result = await _repositoryQueryService.GetRepositoryAsync(
                        queryContext.AccessToken,
                        queryContext.UserId,
                        owner,
                        name,
                        QueryFetchPolicy.StaleFirst,
                        cancellationToken);
                    if (result.Value is not null)
                    {
                        return new ResolvedRepository(CreateFallbackRepository(result.Value), RepositoryQueryRefreshPolicy.ShouldPromote(result));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to resolve repository by name: {ex}");
                }
            }

            if (repo.Id > 0)
            {
                try
                {
                    CachedResult<GitHubRepository> result = await _repositoryQueryService.GetRepositoryAsync(
                        queryContext.AccessToken,
                        queryContext.UserId,
                        repo.Id,
                        QueryFetchPolicy.StaleFirst,
                        cancellationToken);
                    if (result.Value is not null)
                    {
                        return new ResolvedRepository(CreateFallbackRepository(result.Value), RepositoryQueryRefreshPolicy.ShouldPromote(result));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to resolve repository by id: {ex}");
                }
            }

            if (IsSameRepository(repo, Model) && Model is { Id: > 0 })
            {
                return new ResolvedRepository(Model, ShouldPromote: true);
            }

            return new ResolvedRepository(CreateFallbackRepository(repo), ShouldPromote: true);
        }

        private async Task LoadRemainingBranchesAsync(
            RepositoryQueryContext queryContext,
            string owner,
            string name,
            long generation,
            HashSet<string> seen,
            CancellationToken cancellationToken)
        {
            try
            {
                for (int page = 2; ; page++)
                {
                    CachedResult<GitHubBranch[]> result = await _repositoryQueryService.GetBranchesPageAsync(
                        queryContext.AccessToken,
                        queryContext.UserId,
                        owner,
                        name,
                        page,
                        QueryFetchPolicy.StaleFirst,
                        GitHubRequestPriority.BackgroundRefresh,
                        cancellationToken);
                    GitHubBranch[] pageBranches = result.Value ?? [];
                    _repositoryLoadCoordinator.ThrowIfStale(generation, cancellationToken);
                    if (pageBranches.Length > 0)
                    {
                        ApplyBranchSnapshot(pageBranches, removeMissing: false);
                        SelectedBranch ??= ResolveSelectedBranchForCurrentModel();
                    }

                    if (RepositoryQueryRefreshPolicy.ShouldPromote(result))
                    {
                        Task<CachedResult<GitHubBranch[]>> networkRefresh = StartBranchPageNetworkRefresh(
                            queryContext,
                            owner,
                            name,
                            page,
                            cancellationToken);
                        CachedResult<GitHubBranch[]> refreshed = await networkRefresh;
                        _repositoryLoadCoordinator.ThrowIfStale(generation, cancellationToken);
                        pageBranches = refreshed.Value ?? [];
                        ApplyBranchSnapshot(pageBranches, removeMissing: false);
                        SelectedBranch ??= ResolveSelectedBranchForCurrentModel();
                    }

                    foreach (GitHubBranch branch in pageBranches)
                    {
                        if (!string.IsNullOrWhiteSpace(branch.Name))
                        {
                            seen.Add(branch.Name);
                        }
                    }

                    if (pageBranches.Length < GitHubRepositoryQueryService.BranchPageSize)
                    {
                        RemoveMissingBranches(seen);
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Progressive branch paging stopped: {ex}");
            }
        }

        private Task<CachedResult<GitHubBranch[]>> StartBranchPageNetworkRefresh(
            RepositoryQueryContext queryContext,
            string owner,
            string name,
            int page,
            CancellationToken cancellationToken) =>
            _repositoryQueryService.GetBranchesPageAsync(
                queryContext.AccessToken,
                queryContext.UserId,
                owner,
                name,
                page,
                QueryFetchPolicy.NetworkOnly,
                GitHubRequestPriority.BackgroundRefresh,
                cancellationToken);

        private async Task PromoteRepositoryAsync(
            RepositoryQueryContext queryContext,
            string owner,
            string name,
            long repositoryId,
            long generation,
            long starMutationVersion,
            long watchMutationVersion,
            CancellationToken cancellationToken)
        {
            try
            {
                CachedResult<GitHubRepository> result = !string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(name)
                    ? await _repositoryQueryService.GetRepositoryAsync(
                        queryContext.AccessToken,
                        queryContext.UserId,
                        owner,
                        name,
                        QueryFetchPolicy.NetworkOnly,
                        cancellationToken)
                    : await _repositoryQueryService.GetRepositoryAsync(
                        queryContext.AccessToken,
                        queryContext.UserId,
                        repositoryId,
                        QueryFetchPolicy.NetworkOnly,
                        cancellationToken);
                if (result.Value is null)
                {
                    return;
                }

                Repository refreshed = CreateFallbackRepository(result.Value);
                _repositoryLoadCoordinator.PublishIfCurrent(
                    generation,
                    refreshed,
                    value => MergeRepositoryModel(
                        value,
                        preserveStarState: starMutationVersion != Volatile.Read(ref _starMutationVersion),
                        preserveWatchState: watchMutationVersion != Volatile.Read(ref _watchMutationVersion)),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Repository metadata refresh failed; cached data remains visible: {ex}");
            }
        }

        private async Task PromoteRepositoryAfterFirstFrameAsync(
            RepositoryQueryContext queryContext,
            string owner,
            string name,
            long repositoryId,
            long generation,
            long starMutationVersion,
            long watchMutationVersion,
            CancellationToken cancellationToken)
        {
            try
            {
                // Two ordinary 60 Hz frames leave room for layout and composition even
                // when navigation begins immediately after an input dispatch.
                await Task.Delay(TimeSpan.FromMilliseconds(34), cancellationToken);
                await PromoteRepositoryAsync(
                    queryContext,
                    owner,
                    name,
                    repositoryId,
                    generation,
                    starMutationVersion,
                    watchMutationVersion,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task PromoteAllBranchesAsync(
            RepositoryQueryContext queryContext,
            string owner,
            string name,
            long generation,
            CancellationToken cancellationToken)
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            try
            {
                for (int page = 1; ; page++)
                {
                    CachedResult<GitHubBranch[]> result = await _repositoryQueryService.GetBranchesPageAsync(
                        queryContext.AccessToken,
                        queryContext.UserId,
                        owner,
                        name,
                        page,
                        QueryFetchPolicy.NetworkOnly,
                        GitHubRequestPriority.BackgroundRefresh,
                        cancellationToken);
                    GitHubBranch[] pageBranches = result.Value ?? [];
                    _repositoryLoadCoordinator.ThrowIfStale(generation, cancellationToken);
                    foreach (GitHubBranch branch in pageBranches)
                    {
                        if (!string.IsNullOrWhiteSpace(branch.Name))
                        {
                            seen.Add(branch.Name);
                        }
                    }

                    ApplyBranchSnapshot(pageBranches, removeMissing: false);
                    if (pageBranches.Length < GitHubRepositoryQueryService.BranchPageSize)
                    {
                        string? selectedName = SelectedBranch?.Name;
                        RemoveMissingBranches(seen);
                        SelectedBranch = Branches.FirstOrDefault(branch =>
                                string.Equals(branch.Name, selectedName, StringComparison.Ordinal))
                            ?? ResolveSelectedBranchForCurrentModel();
                        _repositoryLoadCoordinator.MarkBranchStateKnown(generation);
                        NotifyRepositoryDataStatesChanged();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Branch refresh failed; cached branches remain visible: {ex}");
            }
        }

        private async Task PromoteStarStateAsync(
            RepositoryQueryContext queryContext,
            string owner,
            string name,
            long generation,
            long mutationVersion,
            CancellationToken cancellationToken)
        {
            try
            {
                CachedResult<GitHubResourceState> result = await _repositoryQueryService.GetStarStateAsync(
                    queryContext.AccessToken,
                    queryContext.UserId,
                    owner,
                    name,
                    QueryFetchPolicy.NetworkOnly,
                    cancellationToken);
                _repositoryLoadCoordinator.ThrowIfStale(generation, cancellationToken);
                if (mutationVersion != Volatile.Read(ref _starMutationVersion))
                {
                    return;
                }

                ApplyStarDisplayState(Model, result.Value?.Exists == true, Model.StargazersCount);
                _repositoryLoadCoordinator.MarkStarStateKnown(generation);
                NotifyRepositoryDataStatesChanged();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Star refresh failed; cached state remains visible: {ex}");
            }
        }

        private async Task PromoteWatchStateAsync(
            RepositoryQueryContext queryContext,
            string owner,
            string name,
            long generation,
            long mutationVersion,
            CancellationToken cancellationToken)
        {
            try
            {
                CachedResult<GitHubRepositorySubscription> result = await _repositoryQueryService.GetWatchStateAsync(
                    queryContext.AccessToken,
                    queryContext.UserId,
                    owner,
                    name,
                    QueryFetchPolicy.NetworkOnly,
                    cancellationToken);
                _repositoryLoadCoordinator.ThrowIfStale(generation, cancellationToken);
                if (mutationVersion != Volatile.Read(ref _watchMutationVersion))
                {
                    return;
                }

                bool watching = result.Value?.Subscribed == true && result.Value.Ignored != true;
                ApplyWatchDisplayState(Model, watching, Model.SubscribersCount);
                _repositoryLoadCoordinator.MarkWatchStateKnown(generation);
                NotifyRepositoryDataStatesChanged();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Watch refresh failed; cached state remains visible: {ex}");
            }
        }

        private static bool TryGetRepositoryParts(GitHubRepository repo, out string owner, out string name)
        {
            string fullName = repo.FullName;
            if (string.IsNullOrWhiteSpace(fullName) &&
                !string.IsNullOrWhiteSpace(repo.Owner.Login) &&
                !string.IsNullOrWhiteSpace(repo.Name))
            {
                fullName = $"{repo.Owner.Login}/{repo.Name}";
            }

            string[] parts = fullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                owner = parts[0];
                name = parts[1];
                return true;
            }

            owner = string.Empty;
            name = string.Empty;
            return false;
        }

        private RepositoryQueryContext GetRepositoryQueryContext()
        {
            long accountId = _authService.AuthenticatedUser?.Id ?? _accountService.GetUser();
            string? accessToken = accountId > 0 ? _authService.GetToken(accountId) : null;
            if (string.IsNullOrWhiteSpace(accessToken) &&
                JitHub.WinUI.Program.CurrentLaunchOptions.IsPublicPreviewOverride)
            {
                accessToken = GitHubClientService.PublicAccessToken;
            }

            if (string.IsNullOrWhiteSpace(accessToken))
            {
                throw new InvalidOperationException("GitHub authentication is unavailable for repository queries.");
            }

            return new RepositoryQueryContext(
                accessToken,
                GitHubAuthenticationConstants.IsPublicAccessToken(accessToken)
                    ? "public"
                    : accountId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                RepositoryMutationOwnership.CreateSessionFingerprint(accessToken));
        }

        private static Branch CreateLegacyBranch(GitHubBranch branch) => new()
        {
            Name = branch.Name
        };

        private void ApplyBranchSnapshot(IEnumerable<GitHubBranch> branches, bool removeMissing)
        {
            Branches.ApplySnapshot(
                branches,
                branch => branch.Name,
                branch => branch.Name,
                CreateLegacyBranch,
                options: removeMissing
                    ? KeyedCollectionDiffOptions.Default
                    : KeyedCollectionDiffOptions.PreserveMissing);
            RefreshFilteredBranches();
            NotifyBranchCollectionChanged();
        }

        private void RemoveMissingBranches(IReadOnlySet<string> names)
        {
            for (int index = Branches.Count - 1; index >= 0; index--)
            {
                if (!names.Contains(Branches[index].Name))
                {
                    Branches.RemoveAt(index);
                }
            }

            RefreshFilteredBranches();
            NotifyBranchCollectionChanged();
        }

        private void RefreshFilteredBranches()
        {
            string filter = BranchSearchText.Trim();
            IEnumerable<Branch> matches = string.IsNullOrWhiteSpace(filter)
                ? Branches
                : Branches.Where(branch => branch.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));
            FilteredBranches.ApplySnapshot(
                matches,
                branch => branch.Name,
                branch => branch.Name,
                branch => branch,
                options: KeyedCollectionDiffOptions.Default);
        }

        private void NotifyBranchCollectionChanged()
        {
            OnPropertyChanged(nameof(CanChangeBranch));
            OnPropertyChanged(nameof(IsBranchPickerVisible));
            OnPropertyChanged(nameof(IsBranchStatusVisible));
            OnPropertyChanged(nameof(BranchStatusText));
        }

        private Branch? ResolveSelectedBranchForCurrentModel() =>
            Branches.FirstOrDefault(branch => string.Equals(branch.Name, Model.DefaultBranch, StringComparison.Ordinal))
            ?? Branches.FirstOrDefault(branch => string.Equals(branch.Name, "main", StringComparison.Ordinal))
            ?? Branches.FirstOrDefault(branch => string.Equals(branch.Name, "master", StringComparison.Ordinal))
            ?? Branches.FirstOrDefault();

        private static bool IsSameRepository(RepoDetailPageArgs args, Repository? current)
        {
            if (current is null)
            {
                return false;
            }

            if (args.Repo is { } repository)
            {
                return IsSameRepository(repository, current);
            }

            return !string.IsNullOrWhiteSpace(args.FullName) &&
                string.Equals(args.FullName, current.FullName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameRepository(GitHubRepository repository, Repository? current)
        {
            if (current is null)
            {
                return false;
            }

            if (repository.Id > 0 && current.Id > 0)
            {
                return repository.Id == current.Id;
            }

            string fullName = repository.FullName;
            if (string.IsNullOrWhiteSpace(fullName) &&
                !string.IsNullOrWhiteSpace(repository.Owner.Login) &&
                !string.IsNullOrWhiteSpace(repository.Name))
            {
                fullName = $"{repository.Owner.Login}/{repository.Name}";
            }

            return !string.IsNullOrWhiteSpace(fullName) &&
                string.Equals(fullName, current.FullName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSameRepository(Repository repository, Repository? current)
        {
            if (current is null)
            {
                return false;
            }

            if (repository.Id > 0 && current.Id > 0)
            {
                return repository.Id == current.Id;
            }

            return !string.IsNullOrWhiteSpace(repository.FullName) &&
                string.Equals(repository.FullName, current.FullName, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryCreateRepositoryReference(string? fullName, out GitHubRepository? repository)
        {
            repository = null;
            if (string.IsNullOrWhiteSpace(fullName))
            {
                return false;
            }

            string[] parts = fullName.Split('/', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                return false;
            }

            repository = new GitHubRepository
            {
                Name = parts[1],
                FullName = $"{parts[0]}/{parts[1]}",
                DefaultBranch = "main",
                HtmlUrl = $"https://github.com/{parts[0]}/{parts[1]}",
                Owner = new GitHubRepositoryOwner
                {
                    Login = parts[0]
                }
            };
            return true;
        }

        private static Repository CreateFallbackRepository(GitHubRepository repo)
        {
            TryGetRepositoryParts(repo, out string owner, out string name);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = repo.Name;
            }

            string fullName = !string.IsNullOrWhiteSpace(repo.FullName)
                ? repo.FullName
                : string.IsNullOrWhiteSpace(owner) ? name : $"{owner}/{name}";

            return new Repository
            {
                Id = repo.Id,
                Name = name,
                FullName = fullName,
                Description = repo.Description ?? string.Empty,
                DefaultBranch = string.IsNullOrWhiteSpace(repo.DefaultBranch) ? "main" : repo.DefaultBranch,
                HtmlUrl = repo.HtmlUrl,
                Private = repo.Private,
                Fork = repo.Fork,
                Archived = repo.Archived,
                ForksCount = repo.ForksCount,
                StargazersCount = repo.StargazersCount,
                WatchersCount = repo.WatchersCount,
                SubscribersCount = repo.SubscribersCount,
                OpenIssuesCount = repo.OpenIssuesCount,
                Language = repo.Language ?? string.Empty,
                UpdatedAt = repo.UpdatedAt ?? default,
                Visibility = Enum.TryParse(repo.Visibility, ignoreCase: true, out RepositoryVisibility visibility)
                    ? visibility
                    : repo.Private ? RepositoryVisibility.Private : RepositoryVisibility.Public,
                Topics = repo.Topics.ToList(),
                Owner = new User
                {
                    Login = string.IsNullOrWhiteSpace(repo.Owner.Login) ? owner : repo.Owner.Login,
                    AvatarUrl = repo.Owner.AvatarUrl ?? string.Empty,
                    HtmlUrl = repo.Owner.HtmlUrl ?? string.Empty
                }
            };
        }

        private readonly record struct RepositoryQueryContext(
            string AccessToken,
            string UserId,
            string AuthSessionFingerprint);

        private readonly record struct ResolvedRepository(Repository Value, bool ShouldPromote);

        private async Task EnsurePreviewPullRequestPageAsync(PullRequestPageNavArg arg)
        {
            await Task.Delay(800);
            GoToPullRequestPage(arg);
        }

        private CodeViewerNavArg ResolveInitialCodeViewerArg(CodeViewerNavArg arg)
        {
            if (arg.IsGitRef)
            {
                return CodeViewerNavArg.CreateWithGitRef(Model, arg.GitRef);
            }

            string originalDefaultBranch = arg.Repo.DefaultBranch;
            string targetBranch = arg.Branch ?? string.Empty;

            if (string.IsNullOrWhiteSpace(targetBranch) ||
                string.Equals(targetBranch, originalDefaultBranch, StringComparison.OrdinalIgnoreCase))
            {
                targetBranch = Model.DefaultBranch;
            }

            return CodeViewerNavArg.CreateWithBranch(Model, targetBranch);
        }

        private Branch? ResolveSelectedBranch(RepoDetailPageArgs args)
        {
            string? requestedBranch = GetRequestedCodeBranch(args);
            string? fallbackDefaultBranch = args.Repo?.DefaultBranch;

            return Branches.FirstOrDefault(branch => string.Equals(branch.Name, requestedBranch, StringComparison.Ordinal))
                ?? Branches.FirstOrDefault(branch => string.Equals(branch.Name, Model.DefaultBranch, StringComparison.Ordinal))
                ?? Branches.FirstOrDefault(branch => string.Equals(branch.Name, fallbackDefaultBranch, StringComparison.Ordinal))
                ?? Branches.FirstOrDefault(branch => string.Equals(branch.Name, "master", StringComparison.Ordinal))
                ?? Branches.FirstOrDefault(branch => string.Equals(branch.Name, "main", StringComparison.Ordinal))
                ?? Branches.FirstOrDefault();
        }

        private void EnsureCodePageBranchAlignment(RepoDetailPageArgs args)
        {
            if (args.Page != RepoPageType.CodePage ||
                args.Ref is not CodeViewerNavArg arg ||
                arg.IsGitRef ||
                SelectedSection != RepositoryWorkspaceSection.Code)
            {
                return;
            }

            string? resolvedBranch = SelectedBranch?.Name ?? Model.DefaultBranch;
            if (string.IsNullOrWhiteSpace(resolvedBranch))
            {
                return;
            }

            string incomingBranch = arg.Branch ?? string.Empty;
            string requestedDefaultBranch = args.Repo?.DefaultBranch ?? string.Empty;
            bool isDefaultBranchNavigation =
                string.IsNullOrWhiteSpace(incomingBranch) ||
                string.Equals(incomingBranch, requestedDefaultBranch, StringComparison.OrdinalIgnoreCase);

            if (!isDefaultBranchNavigation ||
                string.Equals(incomingBranch, resolvedBranch, StringComparison.Ordinal))
            {
                return;
            }

            GoToCodePage(CodeViewerNavArg.CreateWithBranch(Model, resolvedBranch));
        }

        private static string? GetRequestedCodeBranch(RepoDetailPageArgs args)
        {
            if (args.Page != RepoPageType.CodePage ||
                args.Ref is not CodeViewerNavArg { IsBranch: true } codeArg ||
                string.IsNullOrWhiteSpace(codeArg.Branch))
            {
                return null;
            }

            return codeArg.Branch;
        }

        private void SetRepositoryModel(Repository repository)
        {
            Model = repository;
            NotifyRepositoryIdentityChanged();
        }

        private void MergeRepositoryModel(
            Repository refreshed,
            bool preserveStarState,
            bool preserveWatchState)
        {
            Repository current = Model;
            int starCount = current.StargazersCount;
            int subscriberCount = current.SubscribersCount;
            current.Url = refreshed.Url;
            current.HtmlUrl = refreshed.HtmlUrl;
            current.CloneUrl = refreshed.CloneUrl;
            current.GitUrl = refreshed.GitUrl;
            current.SshUrl = refreshed.SshUrl;
            current.SvnUrl = refreshed.SvnUrl;
            current.MirrorUrl = refreshed.MirrorUrl;
            current.Homepage = refreshed.Homepage;
            current.Id = refreshed.Id;
            current.NodeId = refreshed.NodeId;
            current.Owner = refreshed.Owner;
            current.Name = refreshed.Name;
            current.FullName = refreshed.FullName;
            current.Archived = refreshed.Archived;
            current.Description = refreshed.Description;
            current.Language = refreshed.Language;
            current.Private = refreshed.Private;
            current.Fork = refreshed.Fork;
            current.ForksCount = refreshed.ForksCount;
            current.StargazersCount = preserveStarState ? starCount : refreshed.StargazersCount;
            current.DefaultBranch = refreshed.DefaultBranch;
            current.OpenIssuesCount = refreshed.OpenIssuesCount;
            current.CreatedAt = refreshed.CreatedAt;
            current.UpdatedAt = refreshed.UpdatedAt;
            current.WatchersCount = refreshed.WatchersCount;
            current.SubscribersCount = preserveWatchState ? subscriberCount : refreshed.SubscribersCount;
            current.Visibility = refreshed.Visibility;
            current.Topics = refreshed.Topics;
            OnPropertyChanged(nameof(Model));
            NotifyRepositoryIdentityChanged();
            NotifyRepositoryDataStatesChanged();
        }

        private void ApplyStarDisplayState(Repository? repository, bool isStarred, int count)
        {
            RepositoryActionDisplayMutation.Publish(
                count,
                isStarred,
                value =>
                {
                    if (repository is not null)
                    {
                        repository.StargazersCount = value;
                    }
                },
                value => SetProperty(ref _starred, value, nameof(IsStarred)),
                () =>
                {
                    OnPropertyChanged(nameof(Model));
                    NotifyRepositoryDataStatesChanged();
                });
        }

        private void ApplyWatchDisplayState(Repository? repository, bool isWatching, int count)
        {
            RepositoryActionDisplayMutation.Publish(
                count,
                isWatching,
                value =>
                {
                    if (repository is not null)
                    {
                        repository.SubscribersCount = value;
                    }
                },
                value => SetProperty(ref _watching, value, nameof(IsWatching)),
                () =>
                {
                    OnPropertyChanged(nameof(Model));
                    NotifyRepositoryDataStatesChanged();
                });
        }

        private void NotifyRepositoryIdentityChanged()
        {
            OnPropertyChanged(nameof(RepositoryFullName));
            OnPropertyChanged(nameof(IsRepositoryPrivate));
            OnPropertyChanged(nameof(RepositoryStatusText));
            OnPropertyChanged(nameof(IsRepositoryIdentityVisible));
        }

        private async Task<bool> ToggleStar()
        {
            if (!CanToggleStar || Model is null)
            {
                ShowActionStatus(
                    L("RepoDetail.Star.ActionUnavailable", "Star state is unavailable. Reopen the repository to retry."),
                    RepositoryActionStatusKind.Warning);
                return false;
            }

            Repository repository = Model;
            RepositoryQueryContext queryContext = GetRepositoryQueryContext();
            long generation = _repositoryLoadCoordinator.CurrentGeneration;
            long mutationVersion = Interlocked.Increment(ref _starMutationVersion);
            bool previous = IsStarred;
            int previousCount = repository.StargazersCount;
            bool desired = !previous;
            Stopwatch stopwatch = Stopwatch.StartNew();
            ApplyStarDisplayState(
                repository,
                desired,
                RepositoryActionDisplayMutation.CalculateOptimisticCount(previousCount, desired));
            GitHubRepository libraryRepository = CreateStarLibraryRepository(repository);
            try
            {
                if (RepositoryActionAutomationScenario.IsEnabled)
                {
                    bool accepted = previous
                        ? await GitHubService.UnstarRepo(repository.Owner.Login, repository.Name)
                        : await GitHubService.StarRepo(repository.Owner.Login, repository.Name);
                    if (!accepted)
                    {
                        throw new InvalidOperationException("GitHub did not accept the star change.");
                    }
                }
                else if (previous)
                {
                    await _gitHubClientService.UnstarRepositoryAsync(
                        queryContext.AccessToken,
                        repository.Owner.Login,
                        repository.Name,
                        CancellationToken.None);
                }
                else
                {
                    await _gitHubClientService.StarRepositoryAsync(
                        queryContext.AccessToken,
                        repository.Owner.Login,
                        repository.Name,
                        CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                if (IsMutationUiCurrent(repository, queryContext, generation, mutationVersion, _starMutationVersion))
                {
                    ApplyStarDisplayState(repository, previous, previousCount);
                    ShowActionStatus(
                        L(
                            "RepoDetail.Star.ChangeFailed",
                            "The star change could not be saved. Your previous state was restored."),
                        RepositoryActionStatusKind.Warning);
                }
                TrackRepositoryAction(desired ? "star" : "unstar", "error", stopwatch.Elapsed);
                System.Diagnostics.Debug.WriteLine($"Repository star mutation failed: {ex}");
                return false;
            }
            bool localProjectionWarning = false;
            try
            {
                if (!GitHubAuthenticationConstants.IsPublicAccessToken(queryContext.AccessToken))
                {
                    await _starLibraryService.NotifyRepositoryStarStateChangedAsync(
                        queryContext.AccessToken,
                        queryContext.UserId,
                        libraryRepository,
                        desired,
                        CancellationToken.None);
                }
            }
            catch (StarLibraryDegradedException ex)
            {
                localProjectionWarning = true;
                if (IsMutationUiCurrent(repository, queryContext, generation, mutationVersion, _starMutationVersion))
                {
                    ShowActionStatus(
                        JitHub.WinUI.Helpers.UserFacingError.For(
                            ex,
                            JitHub.WinUI.Helpers.UserFacingErrorKind.Action,
                            "repository-action"),
                        RepositoryActionStatusKind.Warning);
                }
            }
            catch (Exception ex)
            {
                localProjectionWarning = true;
                System.Diagnostics.Debug.WriteLine($"Stars library update failed: {ex}");
                if (IsMutationUiCurrent(repository, queryContext, generation, mutationVersion, _starMutationVersion))
                {
                    ShowActionStatus(
                        L(
                            "RepoDetail.Star.LibraryRefreshFailed",
                            "GitHub updated the star, but the local Stars library could not refresh yet."),
                        RepositoryActionStatusKind.Warning);
                }
            }
            try
            {
                await _repositoryQueryService.InvalidateStarStateAsync(
                    queryContext.UserId,
                    repository.Owner.Login,
                    repository.Name,
                    repository.Id,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Repository star cache invalidation failed: {ex}");
            }
            if (IsMutationUiCurrent(repository, queryContext, generation, mutationVersion, _starMutationVersion))
            {
                if (!localProjectionWarning)
                {
                    IsActionStatusVisible = false;
                }
            }
            TrackRepositoryAction(
                desired ? "star" : "unstar",
                localProjectionWarning ? "degraded" : "success",
                stopwatch.Elapsed);
            return true;
        }

        private async Task<bool> ToggleWatch()
        {
            if (!CanToggleWatch || Model is null)
            {
                ShowActionStatus(
                    L("RepoDetail.Watch.ActionUnavailable", "Watch state is unavailable. Reopen the repository to retry."),
                    RepositoryActionStatusKind.Warning);
                return false;
            }

            Repository repository = Model;
            RepositoryQueryContext queryContext = GetRepositoryQueryContext();
            long generation = _repositoryLoadCoordinator.CurrentGeneration;
            long mutationVersion = Interlocked.Increment(ref _watchMutationVersion);
            bool previous = IsWatching;
            int previousCount = repository.SubscribersCount;
            bool desired = !previous;
            Stopwatch stopwatch = Stopwatch.StartNew();
            ApplyWatchDisplayState(
                repository,
                desired,
                RepositoryActionDisplayMutation.CalculateOptimisticCount(previousCount, desired));
            try
            {
                if (RepositoryActionAutomationScenario.IsEnabled)
                {
                    bool accepted;
                    if (previous)
                    {
                        accepted = await GitHubService.UnwatchRepo(repository.Id);
                    }
                    else
                    {
                        var subscription = await GitHubService.WatchRepo(repository.Id);
                        accepted = subscription.Subscribed;
                    }

                    if (!accepted)
                    {
                        throw new InvalidOperationException("GitHub did not accept the watch change.");
                    }
                }
                else if (previous)
                {
                    await _gitHubClientService.UnwatchRepositoryAsync(
                        queryContext.AccessToken,
                        repository.Owner.Login,
                        repository.Name,
                        CancellationToken.None);
                }
                else
                {
                    await _gitHubClientService.WatchRepositoryAsync(
                        queryContext.AccessToken,
                        repository.Owner.Login,
                        repository.Name,
                        CancellationToken.None);
                }

                if (IsMutationUiCurrent(repository, queryContext, generation, mutationVersion, _watchMutationVersion))
                {
                    IsActionStatusVisible = false;
                }
                try
                {
                    await _repositoryQueryService.InvalidateWatchStateAsync(
                        queryContext.UserId,
                        repository.Owner.Login,
                        repository.Name,
                        repository.Id,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Repository watch cache invalidation failed: {ex}");
                }
                TrackRepositoryAction(desired ? "watch" : "unwatch", "success", stopwatch.Elapsed);
                return true;
            }
            catch (Exception ex)
            {
                if (IsMutationUiCurrent(repository, queryContext, generation, mutationVersion, _watchMutationVersion))
                {
                    ApplyWatchDisplayState(repository, previous, previousCount);
                    ShowActionStatus(
                        L(
                            "RepoDetail.Watch.ChangeFailed",
                            "The watch change could not be saved. Your previous state was restored."),
                        RepositoryActionStatusKind.Warning);
                }
                TrackRepositoryAction(desired ? "watch" : "unwatch", "error", stopwatch.Elapsed);
                System.Diagnostics.Debug.WriteLine($"Repository watch mutation failed: {ex}");
                return false;
            }
        }

        private bool IsMutationUiCurrent(
            Repository repository,
            RepositoryQueryContext queryContext,
            long generation,
            long mutationVersion,
            long currentMutationVersion)
        {
            if (!ReferenceEquals(Model, repository) ||
                !_repositoryLoadCoordinator.IsCurrent(generation) ||
                mutationVersion != currentMutationVersion)
            {
                return false;
            }

            try
            {
                RepositoryQueryContext currentContext = GetRepositoryQueryContext();
                RepositoryMutationOwnership ownership = new(
                    queryContext.UserId,
                    queryContext.AuthSessionFingerprint,
                    repository.Id,
                    generation,
                    mutationVersion);
                return ownership.CanPublish(
                    currentContext.UserId,
                    currentContext.AuthSessionFingerprint,
                    Model.Id,
                    _repositoryLoadCoordinator.CurrentGeneration,
                    currentMutationVersion);
            }
            catch
            {
                return false;
            }
        }

        public void NavigateToCompactSection(string section)
        {
            if (Loading)
            {
                return;
            }

            switch (section)
            {
                case "code": GoToCodePage(); break;
                case "issues": GoToIssuesPage(); break;
                case "pull-requests": GoToPullRequestPage(); break;
                case "commits": GoToCommitsPage(); break;
            }
        }

        public void SetActiveSection(RepositoryWorkspaceSection? section) => SelectedSection = section;

        public void SelectCompactBranch(Branch branch)
        {
            if (!CanChangeBranch || string.IsNullOrWhiteSpace(branch.Name))
            {
                return;
            }

            SelectedBranch = branch;
            GoToCodePageWithBranch(branch.Name);
            TrackRepositoryAction("branch", "success", TimeSpan.Zero, "branch_picker");
        }

        private async Task ForkRepo()
        {
            if (!CanForkRepository || Model is not { Id: > 0 } sourceRepository)
            {
                ShowActionStatus(
                    L(
                        "RepoDetail.Actions.Loading",
                        "Repository actions are still loading. Try again in a moment."),
                    RepositoryActionStatusKind.Warning);
                return;
            }

            RepositoryQueryContext queryContext = GetRepositoryQueryContext();
            long sourceGeneration = _repositoryLoadCoordinator.CurrentGeneration;
            string? forkOwner = RepositoryActionAutomationScenario.IsEnabled
                ? "automation-user"
                : _authService.AuthenticatedUser?.Login;
            if (string.IsNullOrWhiteSpace(forkOwner))
            {
                ShowActionStatus(
                    L(
                        "RepoDetail.Fork.IdentityUnavailable",
                        "Your GitHub account identity is unavailable. Sign in again before forking."),
                    RepositoryActionStatusKind.Warning);
                return;
            }

            string ownershipKey = RepositoryForkOwnershipKey.Create(
                queryContext.UserId,
                sourceRepository.Id,
                sourceRepository.Owner.Login,
                sourceRepository.Name,
                forkOwner);
            _forkSourceKey = ownershipKey;
            bool retryAction = CanRetryFork;
            Stopwatch stopwatch = Stopwatch.StartNew();
            ResetOperationCancellation();
            CancellationToken cancellationToken = _repositoryOperationCancellationTokenSource!.Token;
            long loadingOwner = BeginLoadingState();
            CanRetryFork = false;
            ShowActionStatus(
                _forkOperation.HasPendingFork
                    ? L("RepoDetail.Fork.Checking", "Checking whether your fork is ready...")
                    : L("RepoDetail.Fork.Creating", "Creating your fork in GitHub..."),
                RepositoryActionStatusKind.Informational);
            try
            {
                RepositoryForkOwnershipState? ownership = await _forkOwnershipStore.GetAsync(
                    ownershipKey,
                    cancellationToken);
                if (ownership is not null)
                {
                    Repository? existingFork = await ResolveDurableForkAsync(
                        ownership,
                        queryContext,
                        cancellationToken);
                    if (existingFork is not null)
                    {
                        _forkOperation.AdoptAcceptedFork(ownershipKey, existingFork);
                        await _forkOwnershipStore.UpsertAsync(
                            ownership with
                            {
                                Status = RepositoryForkOwnershipStatus.Accepted,
                                TargetRepositoryId = existingFork.Id,
                                UpdatedAt = DateTimeOffset.UtcNow
                            },
                            CancellationToken.None);
                    }
                    else
                    {
                        RepositoryForkOwnershipState checkedOwnership = ownership with
                        {
                            ReconciliationAttempts = ownership.ReconciliationAttempts + 1,
                            UpdatedAt = DateTimeOffset.UtcNow
                        };
                        bool mayCreateAgain = checkedOwnership.Status == RepositoryForkOwnershipStatus.Uncertain &&
                            checkedOwnership.ReconciliationAttempts >= 3 &&
                            DateTimeOffset.UtcNow - checkedOwnership.CreatedAt >= TimeSpan.FromSeconds(30);
                        if (!mayCreateAgain)
                        {
                            await _forkOwnershipStore.UpsertAsync(checkedOwnership, CancellationToken.None);
                            DateTimeOffset retryAt = DateTimeOffset.UtcNow.AddSeconds(2);
                            ConfigureForkRetry(retryAt);
                            ShowActionStatus(
                                L(
                                    "RepoDetail.Fork.Reconciling",
                                    "GitHub may already be preparing this fork. JitHub will reconcile it before sending another request."),
                                RepositoryActionStatusKind.Warning);
                            TrackRepositoryAction(retryAction ? "fork_retry" : "fork", "pending", stopwatch.Elapsed);
                            return;
                        }

                        await _forkOwnershipStore.RemoveAsync(ownershipKey, CancellationToken.None);
                    }
                }

                RepositoryForkOperationResult<Repository> result = await _forkOperation.ResumeAsync(
                    ownershipKey,
                    async token =>
                    {
                        DateTimeOffset now = DateTimeOffset.UtcNow;
                        RepositoryForkOwnershipState uncertain = new(
                            ownershipKey,
                            queryContext.UserId,
                            sourceRepository.Id,
                            sourceRepository.Owner.Login,
                            sourceRepository.Name,
                            forkOwner,
                            sourceRepository.Name,
                            RepositoryForkOwnershipStatus.Uncertain,
                            TargetRepositoryId: null,
                            ReconciliationAttempts: 0,
                            CreatedAt: now,
                            UpdatedAt: now);
                        await _forkOwnershipStore.UpsertAsync(uncertain, CancellationToken.None);
                        try
                        {
                            Repository created;
                            if (RepositoryActionAutomationScenario.IsEnabled)
                            {
                                try
                                {
                                    created = await GitHubService.ForkRepo(
                                        sourceRepository.Id,
                                        new NewRepositoryFork(),
                                        token);
                                }
                                finally
                                {
                                    OnPropertyChanged(nameof(RepositoryAutomationStatusText));
                                }
                            }
                            else
                            {
                                GitHubRepository apiRepository = await _gitHubClientService.ForkRepositoryAsync(
                                    queryContext.AccessToken,
                                    sourceRepository.Owner.Login,
                                    sourceRepository.Name,
                                    token);
                                created = CreateFallbackRepository(apiRepository);
                            }

                            await _forkOwnershipStore.UpsertAsync(
                                uncertain with
                                {
                                    Status = RepositoryForkOwnershipStatus.Accepted,
                                    TargetRepositoryId = created.Id,
                                    TargetOwner = created.Owner.Login,
                                    TargetName = created.Name,
                                    UpdatedAt = DateTimeOffset.UtcNow
                                },
                                CancellationToken.None);
                            return created;
                        }
                        catch (Exception ex)
                        {
                            if (!RepositoryForkOperation<Repository>.IsTransportOutcomeUncertain(ex))
                            {
                                await _forkOwnershipStore.RemoveAsync(ownershipKey, CancellationToken.None);
                            }

                            throw;
                        }
                    },
                    async (forkedRepository, _, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (forkedRepository.Owner is null ||
                            string.IsNullOrWhiteSpace(forkedRepository.Owner.Login) ||
                            string.IsNullOrWhiteSpace(forkedRepository.Name))
                        {
                            throw new InvalidOperationException("GitHub returned a fork without a repository identity.");
                        }

                        CachedResult<GitHubRepository> accessible = await _repositoryQueryService.GetRepositoryAsync(
                            queryContext.AccessToken,
                            queryContext.UserId,
                            forkedRepository.Owner.Login,
                            forkedRepository.Name,
                            QueryFetchPolicy.NetworkOnly,
                            token);
                        if (accessible.Value is null)
                        {
                            return false;
                        }

                        if (RepositoryForkReadinessPolicy.IsAccessibleRepositoryReady(accessible.Value.DefaultBranch))
                        {
                            return true;
                        }

                        CachedResult<GitHubBranch[]> branches = await _repositoryQueryService.GetBranchesPageAsync(
                            queryContext.AccessToken,
                            queryContext.UserId,
                            forkedRepository.Owner.Login,
                            forkedRepository.Name,
                            1,
                            QueryFetchPolicy.NetworkOnly,
                            GitHubRequestPriority.BackgroundRefresh,
                            token);
                        return RepositoryForkReadinessPolicy.IsAccessibleRepositoryReady(
                            accessible.Value.DefaultBranch,
                            branches.Value?.Length);
                    },
                    cancellationToken,
                    maxAttempts: RepositoryActionAutomationScenario.IsEnabled ? 2 : RepositoryForkReadinessPolicy.DefaultMaxAttempts,
                    reconcileForkAsync: async token =>
                    {
                        GitHubRepository? existing = await _repositoryQueryService.FindExistingForkAsync(
                            queryContext.AccessToken,
                            queryContext.UserId,
                            sourceRepository.Owner.Login,
                            sourceRepository.Name,
                            forkOwner,
                            token);
                        return existing is null ? null : CreateFallbackRepository(existing);
                    },
                    maxElapsed: RepositoryActionAutomationScenario.IsEnabled
                        ? TimeSpan.FromSeconds(2)
                        : RepositoryForkReadinessPolicy.DefaultMaxTotalDelay);
                if (!result.IsReady)
                {
                    ConfigureForkRetry(result.RetryAvailableAt);
                    ShowActionStatus(
                        result.ReadinessFailure == RepositoryForkReadinessFailure.RateLimited
                            ? L(
                                "RepoDetail.Fork.RateLimited",
                                "GitHub asked JitHub to wait before checking the fork again. Retry will unlock automatically.")
                            : L(
                                "RepoDetail.Fork.StillPreparing",
                                "GitHub is still preparing the fork. You can retry without creating another fork."),
                        RepositoryActionStatusKind.Warning);
                    TrackRepositoryAction(retryAction ? "fork_retry" : "fork", "pending", stopwatch.Elapsed);
                    return;
                }

                Repository newRepo = result.Repository;
                var navArgs = new RepoDetailPageArgs(RepoPageType.CodePage, newRepo);
                long navigationGeneration = await NavigateToRepositoryAsync(
                    navArgs,
                    cancellationToken,
                    resetPendingFork: false);
                if (!_repositoryLoadCoordinator.IsCurrent(navigationGeneration) ||
                    !string.Equals(Model.FullName, newRepo.FullName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _navigationService.ChangeTabTitle(newRepo.GetRepositoryFullName());
                if (_forkOperation.Complete(result))
                {
                    await _forkOwnershipStore.RemoveAsync(ownershipKey, CancellationToken.None);
                    _forkSourceKey = null;
                    IsActionStatusVisible = false;
                }
                TrackRepositoryAction(retryAction ? "fork_retry" : "fork", "success", stopwatch.Elapsed);
            }
            catch (OperationCanceledException)
            {
                if (ReferenceEquals(Model, sourceRepository) &&
                    _repositoryLoadCoordinator.IsCurrent(sourceGeneration))
                {
                    ConfigureForkRetry(DateTimeOffset.UtcNow.AddMilliseconds(250));
                    ShowActionStatus(
                        L(
                            "RepoDetail.Fork.CancelledUncertain",
                            "GitHub may have accepted the fork. Retry will reconcile it before sending another request."),
                        RepositoryActionStatusKind.Warning);
                    TrackRepositoryAction(retryAction ? "fork_retry" : "fork", "pending", stopwatch.Elapsed);
                }
                else
                {
                    TrackRepositoryAction(retryAction ? "fork_retry" : "fork", "canceled", stopwatch.Elapsed);
                }
            }
            catch (GitHubRateLimitException ex)
            {
                ConfigureForkRetry(DateTimeOffset.UtcNow.Add(ex.RetryDelay));
                System.Diagnostics.Debug.WriteLine($"Fork operation rate limited: {ex}");
                ShowActionStatus(
                    L(
                        "RepoDetail.Fork.RetryRateLimited",
                        "GitHub asked JitHub to wait before retrying the fork. Retry will unlock automatically."),
                    RepositoryActionStatusKind.Warning);
                TrackRepositoryAction(retryAction ? "fork_retry" : "fork", "rate_limited", stopwatch.Elapsed);
            }
            catch (RepositoryForkReconciliationPendingException ex)
            {
                ConfigureForkRetry(ex.RetryAvailableAt);
                ShowActionStatus(
                    L(
                        "RepoDetail.Fork.PendingReconciliation",
                        "GitHub may already be preparing this fork. JitHub will check for it again before creating anything else."),
                    RepositoryActionStatusKind.Warning);
                TrackRepositoryAction(retryAction ? "fork_retry" : "fork", "pending", stopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                ConfigureForkRetry(retryAvailableAt: null);
                System.Diagnostics.Debug.WriteLine($"Fork operation failed: {ex}");
                ShowActionStatus(
                    L(
                        "RepoDetail.Fork.NotReady",
                        "The fork is not ready yet. Check your connection or try again."),
                    RepositoryActionStatusKind.Warning);
                TrackRepositoryAction(retryAction ? "fork_retry" : "fork", "error", stopwatch.Elapsed);
            }
            finally
            {
                CompleteLoadingState(loadingOwner);
            }
        }

        private async Task<Repository?> ResolveDurableForkAsync(
            RepositoryForkOwnershipState ownership,
            RepositoryQueryContext queryContext,
            CancellationToken cancellationToken)
        {
            if (ownership.Status == RepositoryForkOwnershipStatus.Accepted)
            {
                try
                {
                    CachedResult<GitHubRepository> accepted = await _repositoryQueryService.GetRepositoryAsync(
                        queryContext.AccessToken,
                        queryContext.UserId,
                        ownership.TargetOwner,
                        ownership.TargetName,
                        QueryFetchPolicy.NetworkOnly,
                        cancellationToken);
                    if (accepted.Value is not null)
                    {
                        return CreateFallbackRepository(accepted.Value);
                    }
                }
                catch (GitHubApiException)
                {
                    // The fork can remain temporarily inaccessible after GitHub accepts it.
                }
            }

            GitHubRepository? reconciled = await _repositoryQueryService.FindExistingForkAsync(
                queryContext.AccessToken,
                queryContext.UserId,
                ownership.SourceOwner,
                ownership.SourceName,
                ownership.TargetOwner,
                cancellationToken);
            return reconciled is null ? null : CreateFallbackRepository(reconciled);
        }

        public void CancelPendingOperations()
        {
            _repositoryOperationCancellationTokenSource?.Cancel();
            _repositoryOperationCancellationTokenSource?.Dispose();
            _repositoryOperationCancellationTokenSource = null;
            CancelForkRetryCooldown();
        }

        private void ResetOperationCancellation()
        {
            CancelPendingOperations();
            _repositoryOperationCancellationTokenSource = new CancellationTokenSource();
        }

        private void ShowActionStatus(string message, RepositoryActionStatusKind kind)
        {
            ActionStatusMessage = message;
            ActionStatusKind = kind;
            IsActionStatusVisible = true;
        }

        private static string L(string key, string fallback) =>
            LocalizedResourceText.GetString(key, fallback);

        private void TrackRepositoryAction(
            string action,
            string result,
            TimeSpan duration,
            string source = "repository_header")
        {
            _telemetryService.TrackEvent(
                "repository.action.executed",
                new Dictionary<string, string?>
                {
                    ["action"] = action,
                    ["result"] = result,
                    ["source"] = source,
                    ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
                });
        }

        private void ConfigureForkRetry(DateTimeOffset? retryAvailableAt)
        {
            CancelForkRetryCooldown();
            CanRetryFork = true;
            TimeSpan remaining = retryAvailableAt is DateTimeOffset retryAt
                ? retryAt - DateTimeOffset.UtcNow
                : TimeSpan.Zero;
            if (remaining <= TimeSpan.Zero)
            {
                IsForkRetryEnabled = true;
                NotifyRepositoryActionAvailabilityChanged();
                return;
            }

            IsForkRetryEnabled = false;
            _forkRetryCooldownCancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = _forkRetryCooldownCancellationTokenSource.Token;
            Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue =
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(remaining, token);
                    if (!token.IsCancellationRequested)
                    {
                        _ = dispatcherQueue.TryEnqueue(() =>
                        {
                            IsForkRetryEnabled = true;
                            NotifyRepositoryActionAvailabilityChanged();
                        });
                    }
                }
                catch (OperationCanceledException)
                {
                }
            }, token);
        }

        private void CancelForkRetryCooldown()
        {
            _forkRetryCooldownCancellationTokenSource?.Cancel();
            _forkRetryCooldownCancellationTokenSource?.Dispose();
            _forkRetryCooldownCancellationTokenSource = null;
        }

        private long BeginLoadingState()
        {
            long owner = _loadingOperationCoordinator.Begin();
            Loading = true;
            NotifyRepositoryActionAvailabilityChanged();
            return owner;
        }

        private void CompleteLoadingState(long owner)
        {
            if (!_loadingOperationCoordinator.Complete(owner))
            {
                return;
            }

            Loading = false;
            NotifyRepositoryActionAvailabilityChanged();
        }

        private void NotifyRepositoryActionAvailabilityChanged()
        {
            OnPropertyChanged(nameof(CanToggleStar));
            OnPropertyChanged(nameof(CanToggleWatch));
            OnPropertyChanged(nameof(CanForkRepository));
            OnPropertyChanged(nameof(CanChangeBranch));
        }

        private void NotifyRepositoryDataStatesChanged()
        {
            OnPropertyChanged(nameof(IsStarStateKnown));
            OnPropertyChanged(nameof(IsWatchStateKnown));
            OnPropertyChanged(nameof(IsStarStateUnavailable));
            OnPropertyChanged(nameof(IsWatchStateUnavailable));
            OnPropertyChanged(nameof(IsBranchStateKnown));
            OnPropertyChanged(nameof(IsBranchStateUnavailable));
            OnPropertyChanged(nameof(StarActionLabel));
            OnPropertyChanged(nameof(WatchActionLabel));
            OnPropertyChanged(nameof(StarValueText));
            OnPropertyChanged(nameof(WatchValueText));
            OnPropertyChanged(nameof(BranchStatusText));
            OnPropertyChanged(nameof(IsBranchPickerVisible));
            OnPropertyChanged(nameof(IsBranchStatusVisible));
            NotifyRepositoryActionAvailabilityChanged();
        }

        private static GitHubRepository CreateStarLibraryRepository(Repository repository) => new()
        {
            Id = repository.Id,
            Name = repository.Name,
            FullName = string.IsNullOrWhiteSpace(repository.FullName)
                ? $"{repository.Owner.Login}/{repository.Name}"
                : repository.FullName,
            Description = repository.Description,
            DefaultBranch = repository.DefaultBranch,
            HtmlUrl = repository.HtmlUrl,
            Private = repository.Private,
            Fork = repository.Fork,
            Archived = repository.Archived,
            StargazersCount = repository.StargazersCount,
            WatchersCount = repository.WatchersCount,
            SubscribersCount = repository.SubscribersCount,
            ForksCount = repository.ForksCount,
            OpenIssuesCount = repository.OpenIssuesCount,
            Language = repository.Language,
            UpdatedAt = repository.UpdatedAt,
            Visibility = repository.Visibility.ToString().ToLowerInvariant(),
            Topics = repository.Topics.ToArray(),
            Owner = new GitHubRepositoryOwner
            {
                Login = repository.Owner.Login,
                AvatarUrl = repository.Owner.AvatarUrl,
                HtmlUrl = repository.Owner.HtmlUrl
            }
        };
    }
}





