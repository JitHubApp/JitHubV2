using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JitHub.Models.CodeViewer;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.Helpers;
using Microsoft.UI.Dispatching;

namespace JitHub.WinUI.ViewModels.CodeViewer;

public sealed partial class RepoCodePageViewModel : ObservableObject
{
    private static readonly TimeSpan TreeNodePrefetchDebounce = TimeSpan.FromMilliseconds(40);
    private readonly IRepoTreeService _treeService;
    private readonly IRepoFileCacheService _cache;
    private readonly IFilePreviewResolver _previewResolver;
    private readonly IAccountService _accountService;
    private readonly ITelemetryService _telemetryService;
    private readonly IApplicationTaskCoordinator _taskCoordinator;
    private readonly IAccountWorkQuiescence _accountWork;
    private readonly RepoCodeNavigationPreparationCache _navigationPreparationCache;
    private readonly LatestWinsPrefetchScheduler _treeNodePrefetch = new();
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly List<RepoTreeNode> _backStack = [];
    private readonly List<RepoTreeNode> _forwardStack = [];
    private readonly object _reconciliationTaskGate = new();
    private readonly object _treeRefreshTaskGate = new();
    private readonly object _defaultPreviewTaskGate = new();

    private string _owner = string.Empty;
    private string _repositoryName = string.Empty;
    private string _ref = string.Empty;
    private long _initializeGeneration;
    private long _selectionGeneration;
    private long _defaultPreviewGeneration;
    private RequestCancellation? _initializeRequest;
    private RequestCancellation? _selectionRequest;
    private RequestCancellation? _defaultPreviewRequest;
    private PendingVisiblePath? _pendingVisiblePath;
    private Task _reconciliationTask = Task.CompletedTask;
    private Task _treeRefreshTask = Task.CompletedTask;
    private Task _defaultPreviewTask = Task.CompletedTask;

    public RepoCodePageViewModel(
        IRepoTreeService treeService,
        IRepoFileCacheService cache,
        IFilePreviewResolver previewResolver,
        ILanguageIdResolver languageResolver,
        IAccountService accountService,
        ITelemetryService telemetryService,
        IApplicationTaskCoordinator? taskCoordinator = null,
        IAccountWorkQuiescence? accountWork = null,
        RepoCodeNavigationPreparationCache? navigationPreparationCache = null)
    {
        _treeService = treeService;
        _cache = cache;
        _previewResolver = previewResolver;
        _accountService = accountService;
        _telemetryService = SafeTelemetryService.Wrap(telemetryService);
        _taskCoordinator = taskCoordinator ?? new ApplicationTaskCoordinator();
        _accountWork = accountWork ?? new AccountWorkQuiescence();
        _navigationPreparationCache = navigationPreparationCache ??
            new RepoCodeNavigationPreparationCache(treeService, languageResolver, accountService);
        _dispatcherQueue = TryGetDispatcherQueue();

        Tree = new RepoFileTreeViewModel(treeService, languageResolver);
        Preview = new RepoFilePreviewViewModel();
        Breadcrumb = new RepoCodeBreadcrumbViewModel();
        Tree.OnSelectNode = NavigateTreeNodeAsync;
        Tree.OnPrefetchNode = PrefetchTreeNodeAsync;
        Tree.OnAuthoritativeTreeChanged = QueueVisibleFileReconciliation;
        Breadcrumb.OnNavigate = NavigateBreadcrumbAsync;
        Breadcrumb.OnActionExecuted = TrackAction;

        InitializeCommand = new AsyncRelayCommand(
            () => InitializeAsync(_owner, _repositoryName, _ref, CancellationToken.None));
        SelectFileCommand = new AsyncRelayCommand<RepoTreeNode>(
            node => node is null ? Task.CompletedTask : SelectFileAsync(node, GetCurrentRouteToken()));
        GoBackCommand = new AsyncRelayCommand(GoBackAsync, () => CanGoBack);
        GoForwardCommand = new AsyncRelayCommand(GoForwardAsync, () => CanGoForward);
    }

    public string Owner => _owner;
    public string RepositoryName => _repositoryName;
    public string Ref => _ref;

    public RepoFileTreeViewModel Tree { get; }
    public RepoFilePreviewViewModel Preview { get; }
    public RepoCodeBreadcrumbViewModel Breadcrumb { get; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? LoadError { get; set; }

    [ObservableProperty]
    public partial bool CanGoBack { get; set; }

    [ObservableProperty]
    public partial bool CanGoForward { get; set; }

    public AsyncRelayCommand InitializeCommand { get; }
    public AsyncRelayCommand<RepoTreeNode> SelectFileCommand { get; }
    public AsyncRelayCommand GoBackCommand { get; }
    public AsyncRelayCommand GoForwardCommand { get; }

    internal Task ReconciliationTask
    {
        get
        {
            lock (_reconciliationTaskGate)
            {
                return _reconciliationTask;
            }
        }
    }

    internal Task TreeRefreshTask
    {
        get
        {
            lock (_treeRefreshTaskGate)
            {
                return _treeRefreshTask;
            }
        }
    }

    internal Task DefaultPreviewTask
    {
        get
        {
            lock (_defaultPreviewTaskGate)
            {
                return _defaultPreviewTask;
            }
        }
    }

    internal bool IsFileSelectionCoherent(string path) =>
        string.Equals(Breadcrumb.CurrentPath, path, StringComparison.Ordinal) &&
        string.Equals(Tree.SelectedNode?.Path, path, StringComparison.Ordinal) &&
        string.Equals(Preview.CurrentFile?.Path, path, StringComparison.Ordinal) &&
        !Preview.IsLoading;

    internal bool IsFileSelectionPresented(string path) =>
        string.Equals(Breadcrumb.CurrentPath, path, StringComparison.Ordinal) &&
        string.Equals(Tree.SelectedNode?.Path, path, StringComparison.Ordinal);

    public async Task InitializeAsync(string owner, string name, string @ref, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(@ref);

        Stopwatch loadTimer = Stopwatch.StartNew();
        _telemetryService.TrackEvent(
            "repo_code.opened",
            new Dictionary<string, string?>
            {
                ["page"] = "code",
                ["source"] = TelemetryTaxonomy.Sources.Route
            });
        long generation = Interlocked.Increment(ref _initializeGeneration);
        RequestCancellation request = new(ct);
        RequestCancellation? previous = Interlocked.Exchange(ref _initializeRequest, request);
        previous?.Cancel();
        previous?.Dispose();
        CancelDefaultPreview();
        CancelSelection();
        bool requestedIdentityChanged = false;
        bool hadCommittedState = false;
        string committedRef = string.Empty;

        try
        {
            await RunOnUiAsync(() =>
            {
                if (!IsCurrentInitialize(generation)) return;
                requestedIdentityChanged = !IsRepositoryIdentity(owner, name, @ref);
                hadCommittedState = Tree.RootNodes.Count > 0 || Preview.CurrentFile is not null;
                committedRef = _ref;
                PendingVisiblePath? existingPending = Volatile.Read(ref _pendingVisiblePath);
                if (requestedIdentityChanged)
                {
                    SetPendingVisiblePath(generation, existingPending?.Path ?? Preview.CurrentFile?.Path);
                }
                else if (existingPending is not null)
                {
                    SetPendingVisiblePath(generation, existingPending.Path);
                }

                IsLoading = true;
                Tree.IsLoading = true;
                LoadError = requestedIdentityChanged && hadCommittedState
                    ? LocalizedResourceText.Format(
                        "RepoCode/LoadingRefShowingPrevious",
                        "Loading {0}; showing {1} until it is ready.",
                        @ref,
                        committedRef)
                    : null;
                Tree.ErrorMessage = LoadError;
            }, request.Token).ConfigureAwait(false);

            long sourceGeneration = Tree.BeginSourceRequest();
            RepoCodeNavigationPreparationCache.PreparedRepoCodeNavigation navigationPreparation =
                await _navigationPreparationCache.TakeOrPrepareAsync(
                owner,
                name,
                @ref,
                request.Token).ConfigureAwait(false);
            RepoCodeLoadResult<RepoTree> result = navigationPreparation.Result;
            if (!IsCurrentInitialize(generation)) return;

            await ApplyTreeAsync(
                result,
                owner,
                name,
                @ref,
                generation,
                sourceGeneration,
                sourceIsAuthoritative: GitReferencePolicy.IsImmutableObjectId(@ref),
                request.Token,
                navigationPreparation.PreparedTree)
                .ConfigureAwait(false);
            TrackLoadResult(result, loadTimer.Elapsed);

            if (result.IsRefreshInProgress && IsCurrentInitialize(generation))
            {
                QueueTreeRefresh(
                    owner,
                    name,
                    @ref,
                    generation,
                    result.CacheState,
                    request.Token);
            }
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
            TrackLoadOutcome(
                TelemetryTaxonomy.Results.Cancelled,
                loadTimer.Elapsed,
                source: TelemetryTaxonomy.Sources.Navigation);
            await RunOnUiAsync(() =>
            {
                if (!IsCurrentInitialize(generation)) return;
                IsLoading = false;
                Tree.IsLoading = false;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TrackError(
                exception,
                TelemetryTaxonomy.Results.Error,
                loadTimer.Elapsed,
                TelemetryTaxonomy.Sources.Navigation);
            TrackLoadOutcome(
                TelemetryTaxonomy.Results.Error,
                loadTimer.Elapsed,
                TelemetryTaxonomy.Sources.Navigation);
            await RunOnUiAsync(() =>
            {
                if (!IsCurrentInitialize(generation)) return;
                IsLoading = false;
                Tree.IsLoading = false;
                ClearPendingVisiblePath(generation);
                string safeError = UserFacingError.For(
                    exception,
                    hadCommittedState || Tree.RootNodes.Count > 0
                        ? UserFacingErrorKind.Refresh
                        : UserFacingErrorKind.Loading,
                    "repository-code");
                LoadError = requestedIdentityChanged && hadCommittedState
                    ? LocalizedResourceText.Format(
                        "RepoCode/Error/RefLoadFailedShowingPreviousSafe",
                        "Could not load {0}. Showing {1}.",
                        @ref,
                        committedRef)
                    : !requestedIdentityChanged && Tree.RootNodes.Count > 0
                        ? LocalizedResourceText.GetString(
                            "RepoCode/Error/RefreshFailedShowingPreviousSafe",
                            "Showing previously loaded repository files. Refresh failed.")
                        : safeError;
                Tree.ErrorMessage = LoadError;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            // The current route token deliberately outlives this initial load. Tree and
            // visible-file reconciliation may continue after InitializeAsync returns.
            // It is retired by the next route or CancelPendingRequests.
        }
    }

    private void QueueTreeRefresh(
        string owner,
        string name,
        string gitRef,
        long generation,
        CacheState cachedState,
        CancellationToken routeToken)
    {
        long accountId = _accountService.GetUser();
        string? accountPartition = accountId > 0
            ? accountId.ToString(CultureInfo.InvariantCulture)
            : null;
        lock (_treeRefreshTaskGate)
        {
            _treeRefreshTask = _taskCoordinator.RunAsync(
                token => RefreshTreeInBackgroundAsync(
                    owner,
                    name,
                    gitRef,
                    generation,
                    cachedState,
                    token),
                new ApplicationTaskOptions("repo_code.tree_refresh", accountPartition),
                routeToken);
        }
    }

    private async Task RefreshTreeInBackgroundAsync(
        string owner,
        string name,
        string gitRef,
        long generation,
        CacheState cachedState,
        CancellationToken routeToken)
    {
        // IApplicationTaskCoordinator invokes delegates synchronously until their
        // first incomplete await. Yield once so stale cached content can finish the
        // navigation frame before refresh work begins, even with a synchronous test
        // transport or an in-memory response.
        await Task.Yield();
        Stopwatch refreshTimer = Stopwatch.StartNew();
        try
        {
            long refreshSourceGeneration = Tree.BeginSourceRequest();
            RepoCodeLoadResult<RepoTree> refreshed = await _treeService.LoadTreeAsync(
                owner,
                name,
                gitRef,
                routeToken,
                QueryFetchPolicy.NetworkOnly).ConfigureAwait(false);
            if (!IsCurrentInitialize(generation)) return;
            await ApplyTreeAsync(
                refreshed,
                owner,
                name,
                gitRef,
                generation,
                refreshSourceGeneration,
                sourceIsAuthoritative: true,
                routeToken).ConfigureAwait(false);
            await AwaitReconciliationSettledAsync(routeToken).ConfigureAwait(false);
            await AwaitDefaultPreviewSettledAsync(routeToken).ConfigureAwait(false);
            TrackLoadResult(refreshed, refreshTimer.Elapsed);
        }
        catch (OperationCanceledException) when (routeToken.IsCancellationRequested)
        {
            TrackLoadOutcome(
                TelemetryTaxonomy.Results.Cancelled,
                refreshTimer.Elapsed,
                source: TelemetryTaxonomy.Sources.Refresh,
                cacheState: cachedState.ToString().ToLowerInvariant());
        }
        catch (Exception exception)
        {
            string cacheState = cachedState.ToString().ToLowerInvariant();
            TrackError(
                exception,
                TelemetryTaxonomy.Results.CachedError,
                refreshTimer.Elapsed,
                TelemetryTaxonomy.Sources.Refresh,
                cacheState);
            TrackLoadOutcome(
                TelemetryTaxonomy.Results.CachedError,
                refreshTimer.Elapsed,
                TelemetryTaxonomy.Sources.Refresh,
                cacheState);
            await RunOnUiAsync(() =>
            {
                if (!IsCurrentInitialize(generation)) return;
                _ = UserFacingError.For(
                    exception,
                    UserFacingErrorKind.Refresh,
                    "repository-code-cached");
                LoadError = LocalizedResourceText.GetString(
                    "RepoCode/Error/CachedTreeRefreshFailedSafe",
                    "Showing cached repository files. Refresh failed.");
                Tree.ErrorMessage = LoadError;
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task SelectFileAsync(RepoTreeNode? node, CancellationToken ct)
    {
        if (node is null || node.IsDirectory) return;
        ProductPerformanceReadiness.RecordTraversalStage("repo_code.selection.started");
        _treeNodePrefetch.Cancel();
        CancelDefaultPreview();
        Volatile.Write(ref _pendingVisiblePath, null);
        (long generation, RequestCancellation request) = ReserveSelection(ct);
        try
        {
            if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
            {
                PrimeFileSelection(node);
            }
            else
            {
                await RunOnUiAsync(() =>
                {
                    PrimeFileSelection(node);
                }, request.Token).ConfigureAwait(false);
            }
            ProductPerformanceReadiness.RecordTraversalStage("repo_code.selection.primed");

            if (TrySelectFileFromMemoryCache(node, push: true, request.Token, generation))
            {
                ProductPerformanceReadiness.RecordTraversalStage("repo_code.cache.memory_hit");
                return;
            }

            ProductPerformanceReadiness.RecordTraversalStage("repo_code.cache.memory_miss");
            await RunOnUiAsync(() => Preview.BeginSelection(node), request.Token).ConfigureAwait(false);
            await SelectFileAsyncInternal(node, request.Token, generation, push: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
            await RunOnUiAsync(() =>
            {
                if (generation == Volatile.Read(ref _selectionGeneration))
                {
                    RestoreSelectionToVisiblePreview();
                }
            }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            CompleteSelectionRequest(request);
        }
    }

    internal bool PrimeTreeNodeSelection(
        RepoTreeNodeViewModel node,
        CancellationToken cancellationToken,
        out long generation)
    {
        generation = 0;
        if (node.IsDirectory || string.IsNullOrWhiteSpace(node.Sha))
        {
            return false;
        }

        RepoTreeNodeViewModel? current = Tree.FindNodeByPath(node.Path);
        if (current is null ||
            current.IsDirectory ||
            !string.Equals(current.Sha, node.Sha, StringComparison.Ordinal))
        {
            return false;
        }

        ProductPerformanceReadiness.RecordTraversalStage("repo_code.selection.started");
        _treeNodePrefetch.Cancel();
        CancelDefaultPreview();
        Volatile.Write(ref _pendingVisiblePath, null);
        (generation, _) = ReserveSelection(cancellationToken);
        RepoTreeNode model = ToModelNode(current);
        PrimeFileSelection(model);
        Preview.IsLoading = true;
        Preview.ErrorMessage = null;
        ProductPerformanceReadiness.RecordTraversalStage("repo_code.selection.primed");
        return true;
    }

    internal async Task HydratePrimedTreeNodeSelectionAsync(
        RepoTreeNodeViewModel node,
        long generation)
    {
        RequestCancellation? request = Volatile.Read(ref _selectionRequest);
        if (request is null || generation != Volatile.Read(ref _selectionGeneration))
        {
            return;
        }

        RepoTreeNode model = ToModelNode(node);
        try
        {
            if (TrySelectFileFromMemoryCache(model, push: true, request.Token, generation))
            {
                ProductPerformanceReadiness.RecordTraversalStage("repo_code.cache.memory_hit");
                return;
            }

            ProductPerformanceReadiness.RecordTraversalStage("repo_code.cache.memory_miss");
            await SelectFileAsyncInternal(model, request.Token, generation, push: true).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested)
        {
            await RunOnUiAsync(() =>
            {
                if (generation == Volatile.Read(ref _selectionGeneration))
                {
                    RestoreSelectionToVisiblePreview();
                }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await RunOnUiAsync(() =>
            {
                if (generation != Volatile.Read(ref _selectionGeneration)) return;
                Preview.ErrorMessage = UserFacingError.For(
                    exception,
                    Preview.CurrentFile is null
                        ? UserFacingErrorKind.Loading
                        : UserFacingErrorKind.Refresh,
                    "repository-file");
                RestoreSelectionToVisiblePreview();
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            CompleteSelectionRequest(request);
        }
    }

    private void PrimeFileSelection(RepoTreeNode node)
    {
        if (!string.Equals(Breadcrumb.CurrentPath, node.Path, StringComparison.Ordinal))
        {
            Breadcrumb.PrimePath(_repositoryName, node.Path);
        }
        Breadcrumb.CurrentRawUrl = GitHubCodeUrlBuilder.BuildRawUrl(
            _owner,
            _repositoryName,
            _ref,
            node.Path);
        Breadcrumb.CurrentGitHubUrl = GitHubCodeUrlBuilder.BuildBlobUrl(
            _owner,
            _repositoryName,
            _ref,
            node.Path);
        Tree.SelectedNode = Tree.FindNodeByPath(node.Path);
    }

    private bool TrySelectFileFromMemoryCache(
        RepoTreeNode node,
        bool push,
        CancellationToken cancellationToken,
        long generation)
    {
        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            return false;
        }

        long authenticatedUserId = _accountService.GetUser();
        string userPartition = authenticatedUserId > 0
            ? authenticatedUserId.ToString(CultureInfo.InvariantCulture)
            : "public";
        using IAccountWorkLease lease = _accountWork.Enter(userPartition, cancellationToken);
        lease.CancellationToken.ThrowIfCancellationRequested();

        if (!IsCurrentSelection(generation, _owner, _repositoryName, _ref))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(node.Sha) ||
            !TryPrepareFilePreviewFromMemory(node, userPartition, out PreparedFilePreview prepared))
        {
            return false;
        }

        RepoTreeNodeViewModel? currentNode = Tree.FindNodeByPath(node.Path);
        if (currentNode is null ||
            currentNode.IsDirectory ||
            !string.Equals(currentNode.Sha, node.Sha, StringComparison.Ordinal))
        {
            return false;
        }

        if (!IsCurrentSelection(generation, _owner, _repositoryName, _ref))
        {
            return false;
        }

        ApplyPreparedFilePreview(node, prepared, push);
        return true;
    }

    public void CancelPendingRequests()
    {
        Interlocked.Increment(ref _initializeGeneration);
        RequestCancellation? request = Interlocked.Exchange(ref _initializeRequest, null);
        request?.Cancel();
        request?.Dispose();
        Tree.CancelPendingRequests();
        _treeNodePrefetch.Cancel();
        CancelDefaultPreview();
        CancelSelection();
        Volatile.Write(ref _pendingVisiblePath, null);
    }

    private async Task ApplyTreeAsync(
        RepoCodeLoadResult<RepoTree> result,
        string owner,
        string name,
        string @ref,
        long generation,
        long sourceGeneration,
        bool sourceIsAuthoritative,
        CancellationToken ct,
        RepoFileTreeViewModel.PreparedTree? preparedTree = null)
    {
        RepoFileTreeViewModel.PreparedTree prepared = preparedTree ??
            await Tree.PrepareLoadAsync(result.Value, ct).ConfigureAwait(false);
        RepoTreeNode? currentFile = null;
        string? visiblePath = null;
        bool identityChanged = false;
        await RunOnUiAsync(() =>
        {
            if (!IsCurrentInitialize(generation)) return;
            currentFile = Preview.CurrentFile;
            visiblePath = GetPendingVisiblePath(generation) ?? currentFile?.Path;
            identityChanged = !IsRepositoryIdentity(owner, name, @ref);
        }, ct).ConfigureAwait(false);

        PreparedFilePreview? preparedVisibleFile = null;
        RepoTreeNode? replacementFile = null;
        if (identityChanged &&
            !string.IsNullOrEmpty(visiblePath) &&
            prepared.NodesByPath.TryGetValue(visiblePath, out RepoTreeNodeViewModel? replacementNode) &&
            !replacementNode.IsDirectory)
        {
            replacementFile = ToModelNode(replacementNode);
            preparedVisibleFile = await PrepareFilePreviewAsync(
                owner,
                name,
                @ref,
                replacementFile,
                ct).ConfigureAwait(false);
        }

        RepoTreeNode? fileToRestore = null;
        bool shouldReloadFile = false;
        bool shouldOpenReadme = false;
        await RunOnUiAsync(async () =>
        {
            if (!IsCurrentInitialize(generation)) return;

            bool treeApplied = await Tree.LoadIncrementallyAsync(
                prepared,
                owner,
                name,
                @ref,
                sourceGeneration,
                sourceIsAuthoritative,
                ct);
            if (!treeApplied)
            {
                Tree.IsLoading = false;
                IsLoading = false;
                return;
            }

            _owner = owner;
            _repositoryName = name;
            _ref = @ref;
            Tree.IsLoading = false;
            IsLoading = false;
            LoadError = result.RefreshError;
            Tree.ErrorMessage = result.RefreshError;

            if (identityChanged)
            {
                _backStack.Clear();
                _forwardStack.Clear();
                UpdateNavigation();

                if (replacementFile is not null && preparedVisibleFile is not null)
                {
                    ApplyPreparedFilePreview(replacementFile, preparedVisibleFile, push: true);
                    ClearPendingVisiblePath(generation);
                    return;
                }
            }
            else
            {
                ReconcileNavigationWithTree();
            }

            if (!string.IsNullOrEmpty(visiblePath) && Tree.FindNodeByPath(visiblePath) is { IsDirectory: false } restored)
            {
                fileToRestore = ToModelNode(restored);
                shouldReloadFile = identityChanged ||
                    currentFile is null ||
                    !string.Equals(currentFile.Path, fileToRestore.Path, StringComparison.Ordinal) ||
                    !string.Equals(currentFile?.Sha, fileToRestore.Sha, StringComparison.Ordinal);
                if (!shouldReloadFile)
                {
                    Preview.CurrentFile = fileToRestore;
                    PushBackStack(fileToRestore);
                    ClearPendingVisiblePath(generation);
                }
            }
            else if (!string.IsNullOrEmpty(visiblePath) && result.Value.Truncated)
            {
                // Recursive tree results may omit a valid path when GitHub truncates them.
                // Keep the requested path until root/directory contents prove it exists or not.
            }
            else
            {
                ClearPendingVisiblePath(generation);
                if (currentFile is not null || identityChanged)
                {
                    CancelSelection();
                    Preview.Reset();
                }

                ResetBreadcrumb(name);
                shouldOpenReadme = true;
            }
        }, ct).ConfigureAwait(false);

        if (!IsCurrentInitialize(generation)) return;
        if (!identityChanged && fileToRestore is not null && shouldReloadFile)
        {
            bool restored = await SelectFileWithNewGenerationAsync(fileToRestore, ct, push: true).ConfigureAwait(false);
            if (restored)
            {
                ClearPendingVisiblePath(generation);
            }
        }
        else if (shouldOpenReadme)
        {
            QueueDefaultReadmePreview(generation, ct);
        }
    }

    private void QueueDefaultReadmePreview(long generation, CancellationToken routeToken)
    {
        long previewGeneration = Interlocked.Increment(ref _defaultPreviewGeneration);
        long selectionGeneration = Volatile.Read(ref _selectionGeneration);
        RequestCancellation request = new(routeToken);
        RequestCancellation? previous = Interlocked.Exchange(ref _defaultPreviewRequest, request);
        previous?.Cancel();
        previous?.Dispose();

        long accountId = _accountService.GetUser();
        string? accountPartition = accountId > 0
            ? accountId.ToString(CultureInfo.InvariantCulture)
            : null;
        lock (_defaultPreviewTaskGate)
        {
            Task previousTask = _defaultPreviewTask;
            _defaultPreviewTask = _taskCoordinator.RunAsync(
                token => RunDefaultReadmePreviewAsync(
                    previousTask,
                    generation,
                    previewGeneration,
                    selectionGeneration,
                    request,
                    token),
                new ApplicationTaskOptions("repo_code.default_readme_preview", accountPartition),
                request.Token);
        }
    }

    private async Task AwaitDefaultPreviewSettledAsync(CancellationToken token)
    {
        while (true)
        {
            Task pending;
            lock (_defaultPreviewTaskGate)
            {
                pending = _defaultPreviewTask;
            }

            await pending.WaitAsync(token).ConfigureAwait(false);

            lock (_defaultPreviewTaskGate)
            {
                if (ReferenceEquals(pending, _defaultPreviewTask))
                {
                    return;
                }
            }
        }
    }

    private async Task AwaitReconciliationSettledAsync(CancellationToken token)
    {
        while (true)
        {
            Task pending;
            lock (_reconciliationTaskGate)
            {
                pending = _reconciliationTask;
            }

            await pending.WaitAsync(token).ConfigureAwait(false);

            lock (_reconciliationTaskGate)
            {
                if (ReferenceEquals(pending, _reconciliationTask))
                {
                    return;
                }
            }
        }
    }

    private async Task RunDefaultReadmePreviewAsync(
        Task previousTask,
        long initializeGeneration,
        long previewGeneration,
        long selectionGeneration,
        RequestCancellation request,
        CancellationToken token)
    {
        try
        {
            await Task.Yield();
            try
            {
                await previousTask.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A superseded preview owns its own failure. Keep this generation's
                // README request runnable after the previous request is cancelled.
            }

            token.ThrowIfCancellationRequested();
            if (!IsCurrentInitialize(initializeGeneration) ||
                previewGeneration != Volatile.Read(ref _defaultPreviewGeneration) ||
                selectionGeneration != Volatile.Read(ref _selectionGeneration))
            {
                return;
            }

            await TryOpenReadmeAsync(
                initializeGeneration,
                previewGeneration,
                selectionGeneration,
                token).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _defaultPreviewRequest, null, request);
            request.Dispose();
        }
    }

    private async Task<bool> SelectFileWithNewGenerationAsync(RepoTreeNode node, CancellationToken ct, bool push)
    {
        (long generation, RequestCancellation request) = ReserveSelection(ct);

        try
        {
            return await SelectFileAsyncInternal(node, request.Token, generation, push).ConfigureAwait(false);
        }
        finally
        {
            CompleteSelectionRequest(request);
        }
    }

    private (long Generation, RequestCancellation Request) ReserveSelection(CancellationToken cancellationToken)
    {
        long generation = Interlocked.Increment(ref _selectionGeneration);
        RequestCancellation request = new(cancellationToken);
        RequestCancellation? previous = Interlocked.Exchange(ref _selectionRequest, request);
        previous?.Cancel();
        previous?.Dispose();
        return (generation, request);
    }

    private void CompleteSelectionRequest(RequestCancellation request)
    {
        Interlocked.CompareExchange(ref _selectionRequest, null, request);
        request.Dispose();
    }

    private async Task<bool> SelectFileAsyncInternal(
        RepoTreeNode node,
        CancellationToken token,
        long generation,
        bool push)
    {
        if (node.IsDirectory || string.IsNullOrWhiteSpace(node.Sha)) return false;

        string owner = _owner;
        string repositoryName = _repositoryName;
        string gitRef = _ref;
        await RunOnUiAsync(() =>
        {
            if (!IsCurrentSelection(generation, owner, repositoryName, gitRef)) return;
            Preview.IsLoading = true;
            Preview.ErrorMessage = null;
        }, token).ConfigureAwait(false);

        try
        {
            PreparedFilePreview prepared = await PrepareFilePreviewAsync(
                owner,
                repositoryName,
                gitRef,
                node,
                token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentSelection(generation, owner, repositoryName, gitRef)) return false;

            bool applied = false;
            await RunOnUiAsync(() =>
            {
                if (!IsCurrentSelection(generation, owner, repositoryName, gitRef)) return;
                RepoTreeNodeViewModel? currentNode = Tree.FindNodeByPath(node.Path);
                if (currentNode is null ||
                    currentNode.IsDirectory ||
                    !string.Equals(currentNode.Sha, node.Sha, StringComparison.Ordinal))
                {
                    Preview.IsLoading = false;
                    return;
                }

                ApplyPreparedFilePreview(node, prepared, push);
                applied = true;
            }, token).ConfigureAwait(false);
            return applied && IsCurrentSelection(generation, owner, repositoryName, gitRef);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await RunOnUiAsync(() =>
            {
                if (generation != Volatile.Read(ref _selectionGeneration)) return;
                RestoreSelectionToVisiblePreview();
            }, CancellationToken.None).ConfigureAwait(false);
            return false;
        }
        catch (Exception exception)
        {
            await RunOnUiAsync(() =>
            {
                if (!IsCurrentSelection(generation, owner, repositoryName, gitRef)) return;
                Preview.IsLoading = false;
                Preview.ErrorMessage = UserFacingError.For(
                    exception,
                    Preview.CurrentFile is null
                        ? UserFacingErrorKind.Loading
                        : UserFacingErrorKind.Refresh,
                    "repository-file");
                RestoreSelectionToVisiblePreview();
            }, CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    private void RestoreSelectionToVisiblePreview()
    {
        Preview.IsLoading = false;
        if (Preview.CurrentFile is { } visibleFile)
        {
            Breadcrumb.BuildFromPath(_repositoryName, visibleFile.Path);
            Breadcrumb.CurrentRawUrl = GitHubCodeUrlBuilder.BuildRawUrl(
                _owner,
                _repositoryName,
                _ref,
                visibleFile.Path);
            Breadcrumb.CurrentGitHubUrl = GitHubCodeUrlBuilder.BuildBlobUrl(
                _owner,
                _repositoryName,
                _ref,
                visibleFile.Path);
            Tree.SelectedNode = Tree.FindNodeByPath(visibleFile.Path);
        }
        else if (Breadcrumb.IsPathTransitioning)
        {
            Breadcrumb.BuildFromPath(_repositoryName, Breadcrumb.CurrentPath);
        }
    }

    private async Task<PreparedFilePreview> PrepareFilePreviewAsync(
        string owner,
        string repositoryName,
        string gitRef,
        RepoTreeNode node,
        CancellationToken token)
    {
        RepoFileCacheEntry entry = await GetFileEntryAsync(owner, repositoryName, node, token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();

        int sniffLength = (int)Math.Min(entry.Bytes.LongLength, 8192L);
        FilePreviewDescriptor descriptor = _previewResolver.Resolve(
            node.Path,
            entry.ByteLength,
            entry.Bytes.AsMemory(0, sniffLength));
        return new PreparedFilePreview(
            entry,
            descriptor,
            GitHubCodeUrlBuilder.BuildBlobUrl(owner, repositoryName, gitRef, node.Path),
            GitHubCodeUrlBuilder.BuildRawUrl(owner, repositoryName, gitRef, node.Path));
    }

    private bool TryPrepareFilePreviewFromMemory(
        RepoTreeNode node,
        string userPartition,
        out PreparedFilePreview prepared)
    {
        prepared = default!;
        RepoFileCacheKey cacheKey = new(_owner, _repositoryName, node.Sha!, userPartition);
        if (!_cache.TryGet(cacheKey, out RepoFileCacheEntry entry))
        {
            return false;
        }

        int sniffLength = (int)Math.Min(entry.Bytes.LongLength, 8192L);
        FilePreviewDescriptor descriptor = _previewResolver.Resolve(
            node.Path,
            entry.ByteLength,
            entry.Bytes.AsMemory(0, sniffLength));
        prepared = new PreparedFilePreview(
            entry,
            descriptor,
            GitHubCodeUrlBuilder.BuildBlobUrl(_owner, _repositoryName, _ref, node.Path),
            GitHubCodeUrlBuilder.BuildRawUrl(_owner, _repositoryName, _ref, node.Path));
        return true;
    }

    private Task PrefetchTreeNodeAsync(
        RepoTreeNodeViewModel node,
        CancellationToken cancellationToken)
    {
        if (node.IsDirectory || string.IsNullOrWhiteSpace(node.Sha))
        {
            return Task.CompletedTask;
        }

        string owner = _owner;
        string repositoryName = _repositoryName;
        string gitRef = _ref;
        long initializeGeneration = Volatile.Read(ref _initializeGeneration);
        RepoTreeNode model = ToModelNode(node);
        long accountId = _accountService.GetUser();
        string? accountPartition = accountId > 0
            ? accountId.ToString(CultureInfo.InvariantCulture)
            : null;
        _treeNodePrefetch.Schedule(
            TreeNodePrefetchDebounce,
            () =>
            {
                CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    GetCurrentRouteToken());
                _ = _taskCoordinator.RunAsync(
                    async token =>
                    {
                        try
                        {
                            _ = await GetFileEntryAsync(
                                    owner,
                                    repositoryName,
                                    model,
                                    token,
                                    GitHubRequestPriority.Prefetch)
                                .ConfigureAwait(false);
                            token.ThrowIfCancellationRequested();
                            if (!IsCurrentInitialize(initializeGeneration) ||
                                !IsRepositoryIdentity(owner, repositoryName, gitRef))
                            {
                                return;
                            }
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                        }
                        catch
                        {
                            // Predictive work is best-effort and never changes page state.
                        }
                    },
                    new ApplicationTaskOptions("repo_code.file_hover_prefetch", accountPartition),
                    request.Token);
                return new CancellationTokenSourceLease(request);
            });
        return Task.CompletedTask;
    }

    private void ApplyPreparedFilePreview(
        RepoTreeNode node,
        PreparedFilePreview prepared,
        bool push)
    {
        RepoFileCacheEntry entry = prepared.Entry;
        FilePreviewDescriptor descriptor = prepared.Descriptor;
        Preview.Kind = descriptor.Kind;
        Preview.LanguageId = descriptor.LanguageId;
        Preview.ByteSize = entry.ByteLength;
        Preview.Encoding = entry.Encoding;
        Preview.ImageMimeType = descriptor.ImageMimeType;
        Preview.GitHubBlobUrl = prepared.GitHubUrl;
        Preview.GitHubRawUrl = prepared.RawUrl;
        Preview.Text = descriptor.Kind is RepoFilePreviewKind.TooLarge or RepoFilePreviewKind.Unsupported || descriptor.IsLikelyBinary
            ? null
            : entry.Text;
        Preview.Bytes = descriptor.Kind is RepoFilePreviewKind.TooLarge or RepoFilePreviewKind.Unsupported
            ? null
            : entry.Bytes;
        Preview.ErrorMessage = null;
        Preview.CurrentFile = node;
        Preview.IsLoading = false;

        if (Breadcrumb.IsPathTransitioning ||
            !string.Equals(Breadcrumb.CurrentPath, node.Path, StringComparison.Ordinal))
        {
            Breadcrumb.BuildFromPath(_repositoryName, node.Path);
        }
        Breadcrumb.CurrentRawUrl = prepared.RawUrl;
        Breadcrumb.CurrentGitHubUrl = prepared.GitHubUrl;
        Tree.SelectedNode = Tree.FindNodeByPath(node.Path);
        if (push) PushBackStack(node);
        _telemetryService.TrackEvent(
            "repo_code.selected",
            new Dictionary<string, string?> { ["page"] = "code", ["source"] = push ? "navigation" : "history" });
    }

    private async Task<RepoFileCacheEntry> GetFileEntryAsync(
        string owner,
        string repositoryName,
        RepoTreeNode node,
        CancellationToken token,
        GitHubRequestPriority priority = GitHubRequestPriority.Visible)
    {
        long authenticatedUserId = _accountService.GetUser();
        string userPartition = authenticatedUserId > 0
            ? authenticatedUserId.ToString(CultureInfo.InvariantCulture)
            : "public";
        using IAccountWorkLease lease = _accountWork.Enter(userPartition, token);
        token = lease.CancellationToken;
        RepoFileCacheKey cacheKey = new(owner, repositoryName, node.Sha!, userPartition);
        if (_cache.TryGet(cacheKey, out RepoFileCacheEntry cached)) return cached;

        RepoFileCacheEntry? diskCached = await _cache.GetAsync(cacheKey, token).ConfigureAwait(false);
        if (diskCached is not null) return diskCached;

        RepoCodeLoadResult<RepoFileBlob> result = await _treeService.LoadBlobAsync(
            owner,
            repositoryName,
            node.Sha!,
            priority,
            token).ConfigureAwait(false);
        RepoFileBlob blob = result.Value;
        byte[] bytes = blob.Bytes ?? [];
        string? text = blob.Text;
        if (text is null && !blob.IsBinary && bytes.Length > 0)
        {
            text = await Task.Run(() => Encoding.UTF8.GetString(bytes), token).ConfigureAwait(false);
        }

        RepoFileCacheEntry entry = new()
        {
            Sha = node.Sha!,
            ByteLength = bytes.LongLength,
            IsBinary = blob.IsBinary,
            Bytes = bytes,
            Text = text,
            Encoding = blob.Encoding,
            CachedAt = DateTimeOffset.UtcNow
        };
        token.ThrowIfCancellationRequested();
        await _cache.PutAsync(cacheKey, entry, token).ConfigureAwait(false);
        return entry;
    }

    private sealed partial class CancellationTokenSourceLease : IDisposable
    {
        private CancellationTokenSource? _source;

        public CancellationTokenSourceLease(CancellationTokenSource source)
        {
            _source = source;
        }

        public void Dispose()
        {
            CancellationTokenSource? source = Interlocked.Exchange(ref _source, null);
            if (source is null)
            {
                return;
            }

            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                source.Dispose();
            }
        }
    }

    private async Task GoBackAsync()
    {
        if (_backStack.Count <= 1) return;
        RepoTreeNode current = _backStack[^1];
        RepoTreeNode target = _backStack[^2];
        CancellationToken routeToken = GetCurrentRouteToken();
        if (!await NavigateHistoryNodeAsync(target, routeToken).ConfigureAwait(false)) return;

        await RunOnUiAsync(() =>
        {
            if (_backStack.Count <= 1) return;
            _backStack.RemoveAt(_backStack.Count - 1);
            _forwardStack.Add(current);
            UpdateNavigation();
        }, routeToken).ConfigureAwait(false);
    }

    private async Task GoForwardAsync()
    {
        if (_forwardStack.Count == 0) return;
        RepoTreeNode target = _forwardStack[^1];
        CancellationToken routeToken = GetCurrentRouteToken();
        if (!await NavigateHistoryNodeAsync(target, routeToken).ConfigureAwait(false)) return;

        await RunOnUiAsync(() =>
        {
            if (_forwardStack.Count == 0) return;
            _forwardStack.RemoveAt(_forwardStack.Count - 1);
            _backStack.Add(target);
            UpdateNavigation();
        }, routeToken).ConfigureAwait(false);
    }

    private async Task TryOpenReadmeAsync(
        long initializeGeneration,
        long previewGeneration,
        long selectionGeneration,
        CancellationToken ct)
    {
        static bool IsReadme(string name) =>
            name.Equals("README.md", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("README.rst", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("README.txt", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("README.adoc", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("README", StringComparison.OrdinalIgnoreCase);

        RepoTreeNode? readme = null;
        await RunOnUiAsync(() =>
        {
            if (!IsCurrentInitialize(initializeGeneration) ||
                previewGeneration != Volatile.Read(ref _defaultPreviewGeneration) ||
                selectionGeneration != Volatile.Read(ref _selectionGeneration) ||
                Preview.CurrentFile is not null ||
                Preview.IsLoading ||
                Tree.SelectedNode is not null)
            {
                return;
            }

            RepoTreeNodeViewModel? candidate = Tree.RootNodes.FirstOrDefault(
                node => !node.IsDirectory && IsReadme(node.Name));
            if (candidate is not null)
            {
                readme = ToModelNode(candidate);
            }
        }, ct).ConfigureAwait(false);

        if (readme is not null)
        {
            if (!IsCurrentInitialize(initializeGeneration) ||
                previewGeneration != Volatile.Read(ref _defaultPreviewGeneration) ||
                selectionGeneration != Volatile.Read(ref _selectionGeneration))
            {
                return;
            }

            await SelectFileWithNewGenerationAsync(readme, ct, push: true).ConfigureAwait(false);
        }
    }

    private Task NavigateTreeNodeAsync(RepoTreeNodeViewModel node, CancellationToken ct) =>
        node.IsDirectory
            ? NavigateDirectoryAsync(node, ct, push: true)
            : SelectFileAsync(ToModelNode(node), ct);

    private async Task NavigateBreadcrumbAsync(BreadcrumbSegment segment, CancellationToken ct)
    {
        TrackAction(segment.IsRoot
            ? RepoCodeTelemetryActions.BreadcrumbRoot
            : RepoCodeTelemetryActions.BreadcrumbPath);
        CancellationToken routeToken = GetCurrentRouteToken();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, routeToken);
        CancellationToken token = linked.Token;

        if (segment.IsRoot)
        {
            await NavigateRootAsync(token, push: true).ConfigureAwait(false);
            return;
        }

        RepoTreeNodeViewModel? target = await Tree.EnsurePathAvailableAsync(segment.Path, token)
            .ConfigureAwait(false);
        if (target is null)
        {
            await RunOnUiAsync(() =>
            {
                if (!token.IsCancellationRequested)
                {
                    Preview.ErrorMessage = LocalizedResourceText.GetString(
                        "RepoCode/Error/PathMissing",
                        "This path is no longer present on the selected ref.");
                }
            }, token).ConfigureAwait(false);
            return;
        }

        if (target.IsDirectory)
        {
            await NavigateDirectoryAsync(target, token, push: true).ConfigureAwait(false);
        }
        else
        {
            await SelectFileWithNewGenerationAsync(ToModelNode(target), token, push: true)
                .ConfigureAwait(false);
        }
    }

    internal void TrackAction(string action)
    {
        if (!RepoCodeTelemetryActions.Allowed.Contains(action))
        {
            return;
        }

        _telemetryService.TrackEvent(
            "repo_code.action.executed",
            new Dictionary<string, string?>
            {
                ["page"] = "code",
                ["action"] = action,
                ["result"] = TelemetryTaxonomy.Results.Success
            });
    }

    private void TrackLoadResult(RepoCodeLoadResult<RepoTree> result, TimeSpan duration)
    {
        string cacheState = result.CacheState.ToString().ToLowerInvariant();
        _telemetryService.TrackEvent(
            "repo_code.loaded",
            new Dictionary<string, string?>
            {
                ["page"] = "code",
                ["result"] = TelemetryTaxonomy.Results.Success,
                ["cache_state"] = cacheState,
                ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
            });
        _telemetryService.TrackEvent(
            "repo_code.cache.observed",
            new Dictionary<string, string?> { ["page"] = "code", ["cache_state"] = cacheState });
        _telemetryService.TrackEvent(
            "repo_code.duration.recorded",
            new Dictionary<string, string?>
            {
                ["page"] = "code",
                ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
            });
    }

    private void TrackLoadOutcome(
        string result,
        TimeSpan duration,
        string source,
        string? cacheState = null) =>
        _telemetryService.TrackEvent(
            "repo_code.loaded",
            new Dictionary<string, string?>
            {
                ["page"] = "code",
                ["source"] = source,
                ["result"] = result,
                ["cache_state"] = cacheState,
                ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
            });

    private void TrackError(
        Exception exception,
        string result,
        TimeSpan duration,
        string source,
        string? cacheState = null) =>
        _telemetryService.TrackEvent(
            "repo_code.error",
            new Dictionary<string, string?>
            {
                ["page"] = "code",
                ["source"] = source,
                ["result"] = result,
                ["cache_state"] = cacheState,
                ["error_kind"] = GetTelemetryErrorKind(exception),
                ["duration_bucket"] = TelemetrySanitizer.CreateDurationBucket(duration)
            });

    private static string GetTelemetryErrorKind(Exception exception) => exception switch
    {
        OperationCanceledException => TelemetryTaxonomy.ErrorKinds.Cancelled,
        GitHubAuthenticationException => TelemetryTaxonomy.ErrorKinds.Authentication,
        GitHubApiException => TelemetryTaxonomy.ErrorKinds.Api,
        HttpRequestException => TelemetryTaxonomy.ErrorKinds.Network,
        IOException => TelemetryTaxonomy.ErrorKinds.Io,
        _ => TelemetryTaxonomy.ErrorKinds.Unexpected
    };

    private async Task<bool> NavigateHistoryNodeAsync(RepoTreeNode node, CancellationToken ct)
    {
        if (!node.IsDirectory)
        {
            return await SelectFileWithNewGenerationAsync(node, ct, push: false).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(node.Path))
        {
            await NavigateRootAsync(ct, push: false).ConfigureAwait(false);
            return true;
        }

        RepoTreeNodeViewModel? directory = await Tree.EnsurePathAvailableAsync(node.Path, ct)
            .ConfigureAwait(false);
        if (directory is not { IsDirectory: true }) return false;
        await NavigateDirectoryAsync(directory, ct, push: false).ConfigureAwait(false);
        return true;
    }

    private Task NavigateRootAsync(CancellationToken ct, bool push) =>
        RunOnUiAsync(() =>
        {
            CancelSelection();
            Preview.Reset();
            Tree.SelectedNode = null;
            ResetBreadcrumb(_repositoryName);
            Breadcrumb.CurrentGitHubUrl = GitHubCodeUrlBuilder.BuildTreeUrl(
                _owner,
                _repositoryName,
                _ref,
                string.Empty);
            if (push)
            {
                PushBackStack(new RepoTreeNode
                {
                    Name = _repositoryName,
                    Path = string.Empty,
                    IsDirectory = true
                });
            }
        }, ct);

    private Task NavigateDirectoryAsync(
        RepoTreeNodeViewModel directory,
        CancellationToken ct,
        bool push) =>
        RunOnUiAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            directory.IsExpanded = true;
            Tree.SelectedNode = directory;
            CancelSelection();
            Preview.Reset();
            Breadcrumb.BuildFromPath(_repositoryName, directory.Path);
            Breadcrumb.CurrentRawUrl = null;
            Breadcrumb.CurrentGitHubUrl = GitHubCodeUrlBuilder.BuildTreeUrl(
                _owner,
                _repositoryName,
                _ref,
                directory.Path);
            if (push) PushBackStack(ToModelNode(directory));
        }, ct);

    private void QueueVisibleFileReconciliation()
    {
        long generation = Volatile.Read(ref _initializeGeneration);
        string owner = _owner;
        string repositoryName = _repositoryName;
        string gitRef = _ref;
        RequestCancellation? routeRequest = Volatile.Read(ref _initializeRequest);
        CancellationToken routeToken = routeRequest?.Token ?? new CancellationToken(canceled: true);
        if (routeToken.IsCancellationRequested) return;

        lock (_reconciliationTaskGate)
        {
            Task previous = _reconciliationTask;
            long accountId = _accountService.GetUser();
            string? accountPartition = accountId > 0
                ? accountId.ToString(CultureInfo.InvariantCulture)
                : null;
            _reconciliationTask = _taskCoordinator.RunAsync(
                token => ReconcileVisibleFileAfterAsync(
                    previous,
                    generation,
                    owner,
                    repositoryName,
                    gitRef,
                    token),
                new ApplicationTaskOptions("repo_code.visible_file_reconciliation", accountPartition),
                routeToken);
        }
    }

    private async Task ReconcileVisibleFileAfterAsync(
        Task previous,
        long generation,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken routeToken)
    {
        // Ensure QueueVisibleFileReconciliation assigns this task before any
        // synchronous directory result can recursively queue the next pass.
        await Task.Yield();
        try
        {
            await previous.WaitAsync(routeToken).ConfigureAwait(false);
        }
        catch
        {
            // Each reconciliation observes its own failure; keep the chain usable defensively.
        }

        try
        {
            routeToken.ThrowIfCancellationRequested();
            await ReconcileVisibleFileAfterTreeChangeAsync(
                generation,
                owner,
                repositoryName,
                gitRef,
                routeToken).ConfigureAwait(false);
        }
        catch
        {
            // Reconciliation publishes current-generation failures itself. A final
            // dispatcher/shutdown failure is observed here so this owned chain never
            // becomes an unobserved fault and later passes can still run.
        }
    }

    private async Task ReconcileVisibleFileAfterTreeChangeAsync(
        long generation,
        string owner,
        string repositoryName,
        string gitRef,
        CancellationToken routeToken)
    {
        try
        {
            RepoTreeNode? fileToReload = null;
            RepoTreeNodeViewModel? directoryToValidate = null;
            bool shouldOpenReadme = false;

            routeToken.ThrowIfCancellationRequested();
            await RunOnUiAsync(() =>
            {
                if (!IsCurrentInitialize(generation) ||
                    !IsRepositoryIdentity(owner, repositoryName, gitRef))
                {
                    return;
                }

                ReconcileNavigationWithTree();
                RepoTreeNode? current = Preview.CurrentFile;
                string? requestedPath = GetPendingVisiblePath(generation) ?? current?.Path;
                if (string.IsNullOrEmpty(requestedPath) || !Tree.IsRootAuthoritative) return;

                directoryToValidate = FindFirstUnvalidatedAncestor(requestedPath);
                if (directoryToValidate is not null)
                {
                    return;
                }

                RepoTreeNodeViewModel? refreshed = Tree.FindNodeByPath(requestedPath);
                if (refreshed is null || refreshed.IsDirectory)
                {
                    CancelSelection();
                    Tree.SelectedNode = null;
                    Preview.Reset();
                    ResetBreadcrumb(repositoryName);
                    ClearPendingVisiblePath(generation);
                    shouldOpenReadme = true;
                    return;
                }

                RepoTreeNode replacement = ToModelNode(refreshed);
                if (current is null ||
                    !string.Equals(current.Path, replacement.Path, StringComparison.Ordinal) ||
                    !string.Equals(current.Sha, replacement.Sha, StringComparison.Ordinal))
                {
                    fileToReload = replacement;
                }
                else
                {
                    Preview.CurrentFile = replacement;
                    PushBackStack(replacement);
                    ClearPendingVisiblePath(generation);
                }
            }, routeToken).ConfigureAwait(false);

            routeToken.ThrowIfCancellationRequested();
            if (!IsCurrentInitialize(generation) ||
                !IsRepositoryIdentity(owner, repositoryName, gitRef))
            {
                return;
            }

            if (directoryToValidate is not null)
            {
                await Tree.LoadDirectoryAsync(directoryToValidate, routeToken).ConfigureAwait(false);
            }
            else if (fileToReload is not null)
            {
                bool restored = await SelectFileWithNewGenerationAsync(
                    fileToReload,
                    routeToken,
                    push: true).ConfigureAwait(false);
                if (restored)
                {
                    ClearPendingVisiblePath(generation);
                }
            }
            else if (shouldOpenReadme)
            {
                QueueDefaultReadmePreview(generation, routeToken);
            }
        }
        catch (OperationCanceledException) when (routeToken.IsCancellationRequested)
        {
            // A newer repository/ref or file selection superseded this reconciliation.
        }
        catch (Exception exception)
        {
            await RunOnUiAsync(() =>
            {
                if (!IsCurrentInitialize(generation) ||
                    !IsRepositoryIdentity(owner, repositoryName, gitRef))
                {
                    return;
                }

                Preview.ErrorMessage = UserFacingError.For(
                    exception,
                    Preview.CurrentFile is null
                        ? UserFacingErrorKind.Loading
                        : UserFacingErrorKind.Refresh,
                    "repository-file");
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private RepoTreeNodeViewModel? FindFirstUnvalidatedAncestor(string filePath)
    {
        string[] parts = filePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;

        string path = string.Empty;
        for (int index = 0; index < parts.Length - 1; index++)
        {
            path = path.Length == 0 ? parts[index] : $"{path}/{parts[index]}";
            RepoTreeNodeViewModel? directory = Tree.FindNodeByPath(path);
            if (directory is null || !directory.IsDirectory) return null;
            if (!directory.ChildrenLoaded) return directory;
        }

        return null;
    }

    private void PushBackStack(RepoTreeNode node)
    {
        if (_backStack.Count > 0 && string.Equals(_backStack[^1].Path, node.Path, StringComparison.Ordinal))
        {
            _backStack[^1] = node;
            UpdateNavigation();
            return;
        }

        _backStack.Add(node);
        _forwardStack.Clear();
        UpdateNavigation();
    }

    private void ReconcileNavigationWithTree()
    {
        ReconcileNavigationStack(_backStack);
        ReconcileNavigationStack(_forwardStack);
        UpdateNavigation();
    }

    private void ReconcileNavigationStack(List<RepoTreeNode> stack)
    {
        for (int index = stack.Count - 1; index >= 0; index--)
        {
            if (stack[index].IsDirectory && string.IsNullOrEmpty(stack[index].Path))
            {
                continue;
            }

            RepoTreeNodeViewModel? refreshed = Tree.FindNodeByPath(stack[index].Path);
            if (refreshed is null)
            {
                stack.RemoveAt(index);
            }
            else
            {
                stack[index] = ToModelNode(refreshed);
            }
        }
    }

    private bool IsRepositoryIdentity(string owner, string name, string @ref) =>
        string.Equals(_owner, owner, StringComparison.Ordinal) &&
        string.Equals(_repositoryName, name, StringComparison.Ordinal) &&
        string.Equals(_ref, @ref, StringComparison.Ordinal);

    private void ResetBreadcrumb(string repositoryName)
    {
        Breadcrumb.BuildFromPath(repositoryName, string.Empty);
        Breadcrumb.CurrentRawUrl = null;
        Breadcrumb.CurrentGitHubUrl = null;
    }

    private static RepoTree CreateEmptyTree() => new()
    {
        Root = new RepoTreeNode
        {
            Name = string.Empty,
            Path = string.Empty,
            IsDirectory = true
        }
    };

    private void UpdateNavigation()
    {
        CanGoBack = _backStack.Count > 1;
        CanGoForward = _forwardStack.Count > 0;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    private void CancelSelection()
    {
        Interlocked.Increment(ref _selectionGeneration);
        RequestCancellation? request = Interlocked.Exchange(ref _selectionRequest, null);
        request?.Cancel();
        request?.Dispose();
    }

    private void CancelDefaultPreview()
    {
        Interlocked.Increment(ref _defaultPreviewGeneration);
        RequestCancellation? request = Interlocked.Exchange(ref _defaultPreviewRequest, null);
        request?.Cancel();
        request?.Dispose();
    }

    private void SetPendingVisiblePath(long generation, string? path)
    {
        PendingVisiblePath? pending = string.IsNullOrEmpty(path)
            ? null
            : new PendingVisiblePath(generation, path);
        Volatile.Write(ref _pendingVisiblePath, pending);
    }

    private string? GetPendingVisiblePath(long generation)
    {
        PendingVisiblePath? pending = Volatile.Read(ref _pendingVisiblePath);
        return pending?.Generation == generation ? pending.Path : null;
    }

    private void ClearPendingVisiblePath(long generation)
    {
        PendingVisiblePath? pending = Volatile.Read(ref _pendingVisiblePath);
        if (pending?.Generation == generation)
        {
            Interlocked.CompareExchange(ref _pendingVisiblePath, null, pending);
        }
    }

    private bool IsCurrentInitialize(long generation) =>
        generation == Volatile.Read(ref _initializeGeneration);

    private bool IsCurrentSelection(long generation, string owner, string repositoryName, string gitRef) =>
        generation == Volatile.Read(ref _selectionGeneration) &&
        string.Equals(owner, _owner, StringComparison.Ordinal) &&
        string.Equals(repositoryName, _repositoryName, StringComparison.Ordinal) &&
        string.Equals(gitRef, _ref, StringComparison.Ordinal);

    private CancellationToken GetCurrentRouteToken()
    {
        RequestCancellation? request = Volatile.Read(ref _initializeRequest);
        return request?.Token ?? new CancellationToken(canceled: true);
    }

    private Task RunOnUiAsync(Action action, CancellationToken cancellationToken)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                action();
                completion.TrySetResult();
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }))
        {
            completion.TrySetException(new InvalidOperationException("The Repo Code UI dispatcher is unavailable."));
        }

        return completion.Task;
    }

    private Task RunOnUiAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }

        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await action();
                completion.TrySetResult();
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }))
        {
            completion.TrySetException(new InvalidOperationException("The Repo Code UI dispatcher is unavailable."));
        }

        return completion.Task;
    }

    private static DispatcherQueue? TryGetDispatcherQueue()
    {
        try
        {
            return DispatcherQueue.GetForCurrentThread();
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static RepoTreeNode ToModelNode(RepoTreeNodeViewModel viewModel) => new()
    {
        Name = viewModel.Name,
        Path = viewModel.Path,
        Sha = viewModel.Sha,
        Size = viewModel.Size,
        IsDirectory = viewModel.IsDirectory
    };

    private sealed record PendingVisiblePath(long Generation, string Path);

    private sealed record PreparedFilePreview(
        RepoFileCacheEntry Entry,
        FilePreviewDescriptor Descriptor,
        string GitHubUrl,
        string RawUrl);

    private sealed partial class RequestCancellation : IDisposable
    {
        private readonly object _gate = new();
        private CancellationTokenSource? _source;
        private bool _cancelInProgress;
        private bool _disposeRequested;

        public RequestCancellation(CancellationToken externalToken)
        {
            _source = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
            Token = _source.Token;
        }

        public CancellationToken Token { get; }

        public void Cancel()
        {
            CancellationTokenSource? source;
            lock (_gate)
            {
                source = _source;
                if (source is null || _cancelInProgress)
                {
                    return;
                }

                _cancelInProgress = true;
            }

            CancellationTokenSource? disposeAfterCancel = null;
            try
            {
                source.Cancel();
            }
            finally
            {
                lock (_gate)
                {
                    _cancelInProgress = false;
                    if (_disposeRequested && ReferenceEquals(_source, source))
                    {
                        _source = null;
                        disposeAfterCancel = source;
                    }
                }

                disposeAfterCancel?.Dispose();
            }
        }

        public void Dispose()
        {
            CancellationTokenSource? source;
            lock (_gate)
            {
                if (_cancelInProgress)
                {
                    _disposeRequested = true;
                    return;
                }

                source = _source;
                _source = null;
            }

            source?.Dispose();
        }
    }
}
