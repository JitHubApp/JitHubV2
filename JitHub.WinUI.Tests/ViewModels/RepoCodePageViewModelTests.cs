using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.CodeViewer;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.Tests.TestDoubles;
using JitHub.WinUI.ViewModels.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class RepoCodePageViewModelTests
{
    [Fact]
    public async Task Initialize_OlderCompletionCannotOverwriteNewerRef()
    {
        DeferredTreeService service = new();
        TaskCompletionSource<RepoCodeLoadResult<RepoTree>> firstSource = service.DeferTree("first");
        TaskCompletionSource<RepoCodeLoadResult<RepoTree>> secondSource = service.DeferTree("second");
        RepoCodePageViewModel viewModel = CreateViewModel(service);

        Task first = viewModel.InitializeAsync("owner", "repo", "first", default);
        Task second = viewModel.InitializeAsync("owner", "repo", "second", default);
        secondSource.SetResult(Fresh(CreateTree(File("second.cs", "second-sha"))));
        await second;
        firstSource.SetResult(Fresh(CreateTree(File("first.cs", "first-sha"))));
        await first;

        Assert.Equal("second", viewModel.Ref);
        Assert.Single(viewModel.Tree.RootNodes);
        Assert.Equal("second.cs", viewModel.Tree.RootNodes[0].Path);
    }

    [Fact]
    public async Task Initialize_KeepsVisibleTreeUntilReplacementIsReady()
    {
        DeferredTreeService service = new();
        service.CompleteTree("main", CreateTree(File("visible.cs", "visible")));
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        RepoTreeNodeViewModel visible = viewModel.Tree.RootNodes[0];

        TaskCompletionSource<RepoCodeLoadResult<RepoTree>> nextSource = service.DeferTree("next");
        Task pending = viewModel.InitializeAsync("owner", "repo", "next", default);

        Assert.True(viewModel.IsLoading);
        Assert.Same(visible, viewModel.Tree.RootNodes[0]);

        nextSource.SetResult(Fresh(CreateTree(File("next.cs", "next"))));
        await pending;
        Assert.False(viewModel.IsLoading);
        Assert.Equal("next.cs", viewModel.Tree.RootNodes[0].Path);
    }

    [Fact]
    public async Task SelectFile_OlderBlobCannotOverwriteNewerSelection()
    {
        DeferredTreeService service = new();
        service.CompleteTree("main", CreateTree(File("a.cs", "a"), File("b.cs", "b")));
        TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>> aSource = service.DeferBlob("a");
        TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>> bSource = service.DeferBlob("b");
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);

        Task first = viewModel.SelectFileAsync(File("a.cs", "a"), default);
        Task second = viewModel.SelectFileAsync(File("b.cs", "b"), default);
        bSource.SetResult(Fresh(Blob("b", "second")));
        await second;
        aSource.SetResult(Fresh(Blob("a", "first")));
        await first;

        Assert.Equal("b.cs", viewModel.Preview.CurrentFile!.Path);
        Assert.Equal("second", viewModel.Preview.Text);
    }

    [Fact]
    public async Task SelectFile_AccountQuiescenceDrainsFetchAndPreventsLateCacheWrite()
    {
        DeferredTreeService service = new();
        RepoTreeNode file = File("private.cs", "private-sha");
        service.CompleteTree("main", CreateTree(file));
        TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>> blob = service.DeferBlob("private-sha");
        MemoryRepoFileCache cache = new();
        using ApplicationTaskCoordinator coordinator = new();
        AccountWorkQuiescence accountWork = new(coordinator);
        LanguageIdResolver languageResolver = new();
        RepoCodePageViewModel viewModel = new(
            service,
            cache,
            new FilePreviewResolver(languageResolver),
            languageResolver,
            new TestAccountService(),
            new NoopTelemetryService(),
            coordinator,
            accountWork);
        await viewModel.InitializeAsync("owner", "repo", "main", default);

        Task selection = viewModel.SelectFileAsync(file, default);
        await Task.Delay(20);
        Assert.False(selection.IsCompleted);
        Task quiesce = accountWork.QuiesceAsync("42");
        await Task.Delay(20);
        Assert.False(quiesce.IsCompleted);

        blob.SetResult(Fresh(Blob("private-sha", "private content")));
        await Task.WhenAll(selection, quiesce).WaitAsync(TimeSpan.FromSeconds(2));
        accountWork.Activate("42");

        Assert.False(cache.TryGet(new RepoFileCacheKey("owner", "repo", "private-sha", "42"), out _));
    }

    [Fact]
    public async Task Initialize_ErrorPreservesExistingTreeAndSurfacesContext()
    {
        MutableTreeService service = new(CreateTree(File("visible.cs", "visible")));
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        RepoTreeNodeViewModel visible = viewModel.Tree.RootNodes[0];

        service.TreeError = new InvalidOperationException("offline");
        await viewModel.InitializeAsync("owner", "repo", "main", default);

        Assert.Same(visible, viewModel.Tree.RootNodes[0]);
        Assert.Contains("previously loaded", viewModel.LoadError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("offline", viewModel.LoadError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Initialize_EmitsTimedSuccessAndTimedFailureTelemetryFromReachableLoads()
    {
        MutableTreeService service = new(CreateTree(File("visible.cs", "visible")));
        RecordingTelemetryService telemetry = new();
        RepoCodePageViewModel viewModel = CreateViewModel(service, telemetry);

        await viewModel.InitializeAsync("private-owner", "private-repository", "main", default);

        Assert.Contains(telemetry.Events, static entry => entry.Name == "repo_code.opened");
        (string Name, IReadOnlyDictionary<string, string?> Properties) success = Assert.Single(
            telemetry.Events,
            static entry => entry.Name == "repo_code.loaded" && entry.Properties["result"] == "success");
        Assert.False(string.IsNullOrWhiteSpace(success.Properties["duration_bucket"]));
        Assert.DoesNotContain("private-owner", success.Properties.Values);
        Assert.DoesNotContain("private-repository", success.Properties.Values);

        service.TreeError = new InvalidOperationException("offline");
        await viewModel.InitializeAsync("private-owner", "private-repository", "main", default);

        (string Name, IReadOnlyDictionary<string, string?> Properties) failure = Assert.Single(
            telemetry.Events,
            static entry => entry.Name == "repo_code.loaded" && entry.Properties["result"] == "error");
        Assert.False(string.IsNullOrWhiteSpace(failure.Properties["duration_bucket"]));
        Assert.Contains(
            telemetry.Events,
            static entry => entry.Name == "repo_code.error"
                && entry.Properties["result"] == "error"
                && !string.IsNullOrWhiteSpace(entry.Properties["duration_bucket"]));
        Assert.Single(viewModel.Tree.RootNodes);
    }

    [Fact]
    public async Task ThrowingTelemetry_DoesNotPreventTreeInitialization()
    {
        RepoCodePageViewModel viewModel = CreateViewModel(
            new MutableTreeService(CreateTree(File("visible.cs", "visible"))),
            new ThrowingTelemetryService());

        await viewModel.InitializeAsync("owner", "repo", "main", default);

        Assert.Single(viewModel.Tree.RootNodes);
        Assert.True(string.IsNullOrWhiteSpace(viewModel.LoadError));
    }

    [Fact]
    public async Task Initialize_DifferentRefFailureKeepsPreviousReadableContentAndLabelsItsRef()
    {
        MutableTreeService service = new(CreateTree(File("visible.cs", "visible")));
        service.Blobs["visible"] = Blob("visible", "old content");
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.SelectFileAsync(File("visible.cs", "visible"), default);

        service.TreeError = new InvalidOperationException("offline");
        await viewModel.InitializeAsync("owner", "repo", "next", default);

        Assert.Equal("main", viewModel.Ref);
        Assert.Equal("visible.cs", Assert.Single(viewModel.Tree.RootNodes).Path);
        Assert.Equal("visible.cs", viewModel.Preview.CurrentFile?.Path);
        Assert.Equal("old content", viewModel.Preview.Text);
        Assert.Contains("next", viewModel.LoadError, StringComparison.Ordinal);
        Assert.Contains("main", viewModel.LoadError, StringComparison.Ordinal);
        Assert.DoesNotContain("offline", viewModel.LoadError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Initialize_DifferentRefSamePathBlobFailureRollsBackToPreviousReadableRef()
    {
        MutableTreeService service = new(CreateTree(File("visible.cs", "old-sha")));
        service.Blobs["old-sha"] = Blob("old-sha", "old content");
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.SelectFileAsync(File("visible.cs", "old-sha"), default);

        service.Tree = CreateTree(File("visible.cs", "new-sha"));
        service.BlobError = new InvalidOperationException("blob unavailable");
        await viewModel.InitializeAsync("owner", "repo", "next", default);

        Assert.Equal("main", viewModel.Ref);
        Assert.Equal("old-sha", viewModel.Preview.CurrentFile?.Sha);
        Assert.Equal("old content", viewModel.Preview.Text);
        Assert.Contains("next", viewModel.LoadError, StringComparison.Ordinal);
        Assert.Contains("main", viewModel.LoadError, StringComparison.Ordinal);
        Assert.DoesNotContain("blob unavailable", viewModel.LoadError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Initialize_CachedTreeRefreshReloadsReadmeWhenShaChanges()
    {
        RepoTree cached = CreateTree(File("README.md", "readme-old"));
        RepoTree refreshed = CreateTree(File("README.md", "readme-new"));
        StaleThenRefreshTreeService service = new(cached, refreshed);
        service.Blobs["readme-old"] = Blob("readme-old", "cached readme");
        service.Blobs["readme-new"] = Blob("readme-new", "fresh readme");
        RepoCodePageViewModel viewModel = CreateViewModel(service);

        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.TreeRefreshTask;
        await viewModel.DefaultPreviewTask;

        Assert.Equal("readme-new", viewModel.Preview.CurrentFile?.Sha);
        Assert.Equal("fresh readme", viewModel.Preview.Text);
        Assert.Equal(2, service.TreeRequestCount);
    }

    [Fact]
    public async Task Initialize_CachedRefreshFailureEmitsCachedErrorWithoutClearingTree()
    {
        RepoTree cached = CreateTree(File("visible.cs", "visible"));
        StaleThenRefreshTreeService service = new(cached, cached)
        {
            RefreshError = new HttpRequestException("offline")
        };
        RecordingTelemetryService telemetry = new();
        RepoCodePageViewModel viewModel = CreateViewModel(service, telemetry);

        await viewModel.InitializeAsync("private-owner", "private-repository", "main", default);
        await viewModel.TreeRefreshTask;

        Assert.Single(viewModel.Tree.RootNodes);
        Assert.Contains("cached", viewModel.LoadError, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            telemetry.Events,
            static entry => entry.Name == "repo_code.error"
                && entry.Properties["result"] == TelemetryTaxonomy.Results.CachedError
                && entry.Properties["source"] == TelemetryTaxonomy.Sources.Refresh
                && entry.Properties["error_kind"] == TelemetryTaxonomy.ErrorKinds.Network);
        Assert.Contains(
            telemetry.Events,
            static entry => entry.Name == "repo_code.loaded"
                && entry.Properties["result"] == TelemetryTaxonomy.Results.CachedError
                && entry.Properties["source"] == TelemetryTaxonomy.Sources.Refresh);
    }

    [Fact]
    public async Task Initialize_RefreshReplacesBackStackEntriesWithCurrentShas()
    {
        MutableTreeService service = new(CreateTree(
            File("a.cs", "a-old"),
            File("b.cs", "b-old")));
        service.Blobs["a-old"] = Blob("a-old", "a old");
        service.Blobs["b-old"] = Blob("b-old", "b old");
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.SelectFileAsync(File("a.cs", "a-old"), default);
        await viewModel.SelectFileAsync(File("b.cs", "b-old"), default);

        service.Tree = CreateTree(File("a.cs", "a-new"), File("b.cs", "b-new"));
        service.RequireNetworkRefresh();
        service.Blobs["a-new"] = Blob("a-new", "a new");
        service.Blobs["b-new"] = Blob("b-new", "b new");
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.TreeRefreshTask;
        await viewModel.DefaultPreviewTask;
        await viewModel.GoBackCommand.ExecuteAsync(null);

        Assert.Equal("a-new", viewModel.Preview.CurrentFile!.Sha);
        Assert.Equal("a new", viewModel.Preview.Text);
        Assert.Contains("a-new", service.RequestedBlobShas);
        Assert.DoesNotContain("a-old", service.RequestedBlobShas.GetRange(2, service.RequestedBlobShas.Count - 2));
    }

    [Fact]
    public async Task Initialize_RemovedVisiblePathClearsItAndOpensCurrentReadme()
    {
        MutableTreeService service = new(CreateTree(File("removed.cs", "removed")));
        service.Blobs["removed"] = Blob("removed", "obsolete");
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.SelectFileAsync(File("removed.cs", "removed"), default);

        service.Tree = CreateTree(File("README.md", "readme"));
        service.RequireNetworkRefresh();
        service.Blobs["readme"] = Blob("readme", "current readme");
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.TreeRefreshTask;
        await viewModel.DefaultPreviewTask;

        Assert.Equal("README.md", viewModel.Preview.CurrentFile!.Path);
        Assert.Equal("readme", viewModel.Preview.CurrentFile.Sha);
        Assert.Equal("current readme", viewModel.Preview.Text);
    }

    [Fact]
    public async Task TruncatedRootReconciliation_RemovesObsoleteVisibleFile()
    {
        TruncatedRootTreeService service = new(CreateTree(
            truncated: true,
            File("removed.cs", "removed")));
        service.Blobs["removed"] = Blob("removed", "obsolete");
        service.Blobs["readme"] = Blob("readme", "current readme");
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.SelectFileAsync(File("removed.cs", "removed"), default);

        service.RootResult.SetResult(Fresh<IReadOnlyList<RepoTreeNode>>([
            File("README.md", "readme")]
        ));
        await WaitUntilAsync(() => viewModel.Preview.CurrentFile?.Path == "README.md");

        Assert.Equal("readme", viewModel.Preview.CurrentFile!.Sha);
        Assert.Equal("current readme", viewModel.Preview.Text);
        Assert.DoesNotContain(viewModel.Tree.RootNodes, node => node.Path == "removed.cs");
    }

    [Fact]
    public async Task TruncatedRootReconciliation_PreventsLateRemovedBlobFromBecomingVisible()
    {
        TruncatedRootTreeService service = new(CreateTree(
            truncated: true,
            File("removed.cs", "removed")));
        TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>> blob =
            service.DeferBlob("removed");
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        Task selection = viewModel.SelectFileAsync(File("removed.cs", "removed"), default);

        service.RootResult.SetResult(Fresh<IReadOnlyList<RepoTreeNode>>([]));
        await viewModel.Tree.RootReconciliationTask;
        blob.SetResult(Fresh(Blob("removed", "obsolete")));
        await selection;

        Assert.Empty(viewModel.Tree.RootNodes);
        Assert.Null(viewModel.Preview.CurrentFile);
        Assert.Null(viewModel.Preview.Text);
    }

    [Fact]
    public async Task TruncatedRootReconciliation_ValidatesVisibleNestedFileAndReloadsChangedSha()
    {
        TruncatedNestedTreeService service = new(CreateTree(
            truncated: true,
            Directory("src", "src-old", File("src/App.cs", "app-old"))));
        service.Blobs["app-old"] = Blob("app-old", "old content");
        service.Blobs["app-new"] = Blob("app-new", "new content");
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.SelectFileAsync(File("src/App.cs", "app-old"), default);

        service.RootResult.SetResult(Fresh<IReadOnlyList<RepoTreeNode>>([
            Directory("src", "src-new")]
        ));
        await WaitUntilAsync(() => viewModel.Preview.CurrentFile?.Sha == "app-new");

        Assert.Equal("new content", viewModel.Preview.Text);
        Assert.Contains(string.Empty, service.RequestedDirectories);
        Assert.Contains("src", service.RequestedDirectories);
    }

    [Fact]
    public async Task ReconciliationTask_OwnsSynchronouslyQueuedDirectoryPassAndFileReload()
    {
        TruncatedNestedTreeService service = new(CreateTree(
            truncated: true,
            Directory("src", "src-old", File("src/App.cs", "app-old"))));
        service.Blobs["app-old"] = Blob("app-old", "old content");
        TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>> freshBlob =
            service.DeferBlob("app-new");
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.SelectFileAsync(File("src/App.cs", "app-old"), default);

        service.RootResult.SetResult(Fresh<IReadOnlyList<RepoTreeNode>>([
            Directory("src", "src-new")
        ]));
        await WaitUntilAsync(() => service.RequestedBlobShas.Contains("app-new"));

        Task reconciliation = viewModel.ReconciliationTask;
        Assert.False(reconciliation.IsCompleted);

        freshBlob.SetResult(Fresh(Blob("app-new", "new content")));
        await reconciliation;

        Assert.Equal("app-new", viewModel.Preview.CurrentFile!.Sha);
        Assert.Equal("new content", viewModel.Preview.Text);
    }

    [Fact]
    public async Task Initialize_TruncatedRefChangeRestoresRequestedNestedPathAfterAuthoritativeReconciliation()
    {
        TruncatedRefChangeTreeService service = new(includeRequestedFile: true);
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.SelectFileAsync(File("src/App.cs", "app-old"), default);

        await viewModel.InitializeAsync("owner", "repo", "next", default);
        await WaitUntilAsync(() => viewModel.Preview.CurrentFile?.Sha == "app-new");
        await viewModel.ReconciliationTask;

        Assert.Equal("next", viewModel.Ref);
        Assert.Equal("src/App.cs", viewModel.Preview.CurrentFile!.Path);
        Assert.Equal("app-new", viewModel.Preview.CurrentFile.Sha);
        Assert.Equal("new content", viewModel.Preview.Text);
        Assert.Contains(("next", string.Empty), service.RequestedDirectories);
        Assert.Contains(("next", "src"), service.RequestedDirectories);
    }

    [Fact]
    public async Task Initialize_TruncatedRefChangeFallsBackToCurrentReadmeAfterAuthoritativeAbsence()
    {
        TruncatedRefChangeTreeService service = new(includeRequestedFile: false);
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.SelectFileAsync(File("src/App.cs", "app-old"), default);

        await viewModel.InitializeAsync("owner", "repo", "next", default);
        await WaitUntilAsync(() => viewModel.Preview.CurrentFile?.Path == "README.md");
        await viewModel.ReconciliationTask;

        Assert.Equal("next", viewModel.Ref);
        Assert.Equal("readme-next", viewModel.Preview.CurrentFile!.Sha);
        Assert.Equal("next readme", viewModel.Preview.Text);
        Assert.DoesNotContain(("next", "src"), service.RequestedDirectories);
    }

    [Fact]
    public async Task RepeatedInitializeAndSelectionCancellationDoesNotRaceDisposedSources()
    {
        CancellationAwarePageTreeService service = new();
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        List<Task> initializationTasks = [];

        for (int index = 0; index < 40; index++)
        {
            initializationTasks.Add(viewModel.InitializeAsync("owner", "repo", $"ref-{index}", default));
        }

        viewModel.CancelPendingRequests();
        await Task.WhenAll(initializationTasks);

        service.CompleteRef = "ready";
        await viewModel.InitializeAsync("owner", "repo", "ready", default);
        List<Task> selectionTasks = [];
        for (int index = 0; index < 40; index++)
        {
            selectionTasks.Add(viewModel.SelectFileAsync(File("file.cs", "file-sha"), default));
        }

        viewModel.CancelPendingRequests();
        await Task.WhenAll(selectionTasks);

        Assert.Null(viewModel.Preview.ErrorMessage);
    }

    [Fact]
    public async Task BreadcrumbFolderNavigationChangesLocationAndParticipatesInHistory()
    {
        MutableTreeService service = new(CreateTree(
            Directory("src", "src-sha", File("src/App.cs", "app-sha")),
            File("README.md", "readme-sha")));
        service.Blobs["app-sha"] = Blob("app-sha", "public sealed class App {}");
        service.Blobs["readme-sha"] = Blob("readme-sha", "readme");
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.SelectFileAsync(File("src/App.cs", "app-sha"), default);
        BreadcrumbSegment folder = Assert.Single(viewModel.Breadcrumb.Segments, item => item.Path == "src");

        await viewModel.Breadcrumb.NavigateToSegmentCommand.ExecuteAsync(folder);

        Assert.Equal("src", viewModel.Breadcrumb.CurrentPath);
        Assert.Null(viewModel.Preview.CurrentFile);
        Assert.Equal("src", viewModel.Tree.SelectedNode?.Path);
        Assert.True(viewModel.CanGoBack);

        await viewModel.GoBackCommand.ExecuteAsync(null);
        Assert.Equal("src/App.cs", viewModel.Breadcrumb.CurrentPath);
        Assert.Equal("app-sha", viewModel.Preview.CurrentFile?.Sha);
    }

    [Fact]
    public async Task CancelledRouteReconciliationDoesNotLaunchDirectoryOrBlobFollowUp()
    {
        TruncatedNestedTreeService service = new(CreateTree(
            truncated: true,
            Directory("src", "src-old", File("src/App.cs", "app-old"))));
        service.Blobs["app-old"] = Blob("app-old", "old content");
        service.Blobs["app-new"] = Blob("app-new", "new content");
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.SelectFileAsync(File("src/App.cs", "app-old"), default);

        viewModel.CancelPendingRequests();
        service.RootResult.SetResult(Fresh<IReadOnlyList<RepoTreeNode>>([
            Directory("src", "src-new")
        ]));
        await viewModel.Tree.PendingReconciliationTask;
        await viewModel.ReconciliationTask;

        Assert.DoesNotContain("src", service.RequestedDirectories);
        Assert.DoesNotContain("app-new", service.RequestedBlobShas);
    }

    [Fact]
    public async Task CachedFileTraversal_CompletesWithinFiftyMillisecondsWithoutBlanking()
    {
        MutableTreeService service = new(CreateTree(
            File("a.cs", "a-sha"),
            File("b.cs", "b-sha")));
        service.Blobs["a-sha"] = Blob("a-sha", "class A {}");
        service.Blobs["b-sha"] = Blob("b-sha", "class B {}");
        RepoCodePageViewModel viewModel = CreateViewModel(service);
        await viewModel.InitializeAsync("owner", "repo", "main", default);
        await viewModel.SelectFileAsync(File("a.cs", "a-sha"), default);
        await viewModel.SelectFileAsync(File("b.cs", "b-sha"), default);
        Stopwatch stopwatch = Stopwatch.StartNew();

        Task traversal = viewModel.SelectFileAsync(File("a.cs", "a-sha"), default);
        Assert.NotNull(viewModel.Preview.CurrentFile);
        await traversal;

        stopwatch.Stop();
        Assert.Equal("a.cs", viewModel.Preview.CurrentFile?.Path);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(50),
            $"Cached file traversal took {stopwatch.Elapsed.TotalMilliseconds:0.0} ms.");
    }

    [Fact]
    public void TrackAction_EmitsIdentifierFreeCoverageForEveryRepoCodeInteraction()
    {
        RecordingTelemetryService telemetry = new();
        RepoCodePageViewModel viewModel = CreateViewModel(
            new MutableTreeService(CreateTree()),
            telemetry);
        string[] requiredActions =
        [
            RepoCodeTelemetryActions.Find,
            RepoCodeTelemetryActions.Outline,
            RepoCodeTelemetryActions.CopyPath,
            RepoCodeTelemetryActions.CopyRaw,
            RepoCodeTelemetryActions.CopyLineLink,
            RepoCodeTelemetryActions.Drawer,
            RepoCodeTelemetryActions.ExternalOpen
        ];

        foreach (string action in requiredActions)
        {
            viewModel.TrackAction(action);
        }
        viewModel.TrackAction("owner/repository");

        Assert.Equal(requiredActions.Length, telemetry.Events.Count);
        Assert.Equal(requiredActions, telemetry.Events.Select(entry => entry.Properties["action"]));
        Assert.All(telemetry.Events, entry =>
        {
            Assert.Equal("repo_code.action.executed", entry.Name);
            Assert.Equal("code", entry.Properties["page"]);
            Assert.Equal("success", entry.Properties["result"]);
            Assert.Equal(3, entry.Properties.Count);
        });
    }

    private static RepoCodePageViewModel CreateViewModel(
        IRepoTreeService service,
        ITelemetryService? telemetryService = null)
    {
        LanguageIdResolver languageResolver = new();
        return new RepoCodePageViewModel(
            service,
            new MemoryRepoFileCache(),
            new FilePreviewResolver(languageResolver),
            languageResolver,
            new TestAccountService(),
            telemetryService ?? new NoopTelemetryService());
    }

    private sealed class NoopTelemetryService : ITelemetryService
    {
        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null) { }
        public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null) { }
        public IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null) => new NoopTrace();

        private sealed class NoopTrace : IPerformanceTrace
        {
            public void Dispose() { }
            public void SetProperty(string key, string? value) { }
        }
    }

    private sealed class RecordingTelemetryService : ITelemetryService
    {
        public List<(string Name, IReadOnlyDictionary<string, string?> Properties)> Events { get; } = [];

        public void TrackEvent(string name, IReadOnlyDictionary<string, string?>? properties = null) =>
            Events.Add((name, properties ?? new Dictionary<string, string?>()));

        public void TrackMetric(string name, double value, IReadOnlyDictionary<string, string?>? properties = null) { }

        public IPerformanceTrace StartTrace(string name, IReadOnlyDictionary<string, string?>? properties = null) =>
            new RecordingTrace();

        private sealed class RecordingTrace : IPerformanceTrace
        {
            public void Dispose() { }
            public void SetProperty(string key, string? value) { }
        }
    }

    private static RepoCodeLoadResult<T> Fresh<T>(T value) where T : class =>
        new(value, CacheState.Fresh);

    private static RepoTree CreateTree(params RepoTreeNode[] nodes) => CreateTree(false, nodes);

    private static RepoTree CreateTree(bool truncated, params RepoTreeNode[] nodes) => new()
    {
        Sha = "tree",
        Truncated = truncated,
        Root = new RepoTreeNode
        {
            Name = string.Empty,
            Path = string.Empty,
            IsDirectory = true,
            Children = new List<RepoTreeNode>(nodes)
        }
    };

    private static RepoTreeNode File(string path, string sha) => new()
    {
        Name = path,
        Path = path,
        Sha = sha,
        Size = 10,
        IsDirectory = false
    };

    private static RepoTreeNode Directory(string path, string sha, params RepoTreeNode[] children) => new()
    {
        Name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path,
        Path = path,
        Sha = sha,
        IsDirectory = true,
        Children = new List<RepoTreeNode>(children)
    };

    private static RepoFileBlob Blob(string sha, string text) => new()
    {
        Sha = sha,
        Encoding = "utf-8",
        Bytes = System.Text.Encoding.UTF8.GetBytes(text),
        Text = text,
        IsBinary = false
    };

    private sealed class DeferredTreeService : IRepoTreeService
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<RepoCodeLoadResult<RepoTree>>> _trees = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>>> _blobs = new();

        public TaskCompletionSource<RepoCodeLoadResult<RepoTree>> DeferTree(string gitRef) =>
            _trees.GetOrAdd(gitRef, _ => NewSource<RepoTree>());

        public void CompleteTree(string gitRef, RepoTree tree) => DeferTree(gitRef).SetResult(Fresh(tree));

        public void FailTree(string gitRef, Exception exception) => DeferTree(gitRef).SetException(exception);

        public TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>> DeferBlob(string sha) =>
            _blobs.GetOrAdd(sha, _ => NewSource<RepoFileBlob>());

        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            DeferTree(refOrSha).Task;

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            Task.FromResult(Fresh<IReadOnlyList<RepoTreeNode>>([]));

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            DeferBlob(sha).Task;

        private static TaskCompletionSource<RepoCodeLoadResult<T>> NewSource<T>() where T : class =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class MutableTreeService : IRepoTreeService
    {
        private bool _isRefreshInProgress;

        public MutableTreeService(RepoTree tree) => Tree = tree;

        public RepoTree Tree { get; set; }
        public Exception? TreeError { get; set; }
        public Exception? BlobError { get; set; }
        public Dictionary<string, RepoFileBlob> Blobs { get; } = new(StringComparer.Ordinal);
        public List<string> RequestedBlobShas { get; } = [];

        public void RequireNetworkRefresh() => _isRefreshInProgress = true;

        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
        {
            ct.ThrowIfCancellationRequested();
            if (fetchPolicy == QueryFetchPolicy.NetworkOnly)
            {
                _isRefreshInProgress = false;
                return TreeError is null
                    ? Task.FromResult(Fresh(Tree))
                    : Task.FromException<RepoCodeLoadResult<RepoTree>>(TreeError);
            }

            return TreeError is null
                ? Task.FromResult(_isRefreshInProgress
                    ? new RepoCodeLoadResult<RepoTree>(Tree, CacheState.Stale, IsRefreshInProgress: true)
                    : Fresh(Tree))
                : Task.FromException<RepoCodeLoadResult<RepoTree>>(TreeError);
        }

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            Task.FromResult(Fresh<IReadOnlyList<RepoTreeNode>>([]));

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
        {
            ct.ThrowIfCancellationRequested();
            RequestedBlobShas.Add(sha);
            return BlobError is null
                ? Task.FromResult(Fresh(Blobs[sha]))
                : Task.FromException<RepoCodeLoadResult<RepoFileBlob>>(BlobError);
        }
    }

    private sealed class StaleThenRefreshTreeService : IRepoTreeService
    {
        private readonly RepoTree _cached;
        private readonly RepoTree _refreshed;

        public StaleThenRefreshTreeService(RepoTree cached, RepoTree refreshed)
        {
            _cached = cached;
            _refreshed = refreshed;
        }

        public Dictionary<string, RepoFileBlob> Blobs { get; } = new(StringComparer.Ordinal);
        public int TreeRequestCount { get; private set; }
        public Exception? RefreshError { get; init; }

        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
        {
            ct.ThrowIfCancellationRequested();
            TreeRequestCount++;
            if (fetchPolicy == QueryFetchPolicy.NetworkOnly && RefreshError is not null)
            {
                return Task.FromException<RepoCodeLoadResult<RepoTree>>(RefreshError);
            }

            return Task.FromResult(fetchPolicy == QueryFetchPolicy.NetworkOnly
                ? Fresh(_refreshed)
                : new RepoCodeLoadResult<RepoTree>(_cached, CacheState.Stale, IsRefreshInProgress: true));
        }

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            Task.FromResult(Fresh<IReadOnlyList<RepoTreeNode>>([]));

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            Task.FromResult(Fresh(Blobs[sha]));
    }

    private sealed class TruncatedRootTreeService : IRepoTreeService
    {
        private readonly RepoTree _tree;

        public TruncatedRootTreeService(RepoTree tree) => _tree = tree;

        public Dictionary<string, RepoFileBlob> Blobs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>>> DeferredBlobs { get; } =
            new(StringComparer.Ordinal);
        public TaskCompletionSource<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> RootResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>> DeferBlob(string sha)
        {
            TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>> source =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            DeferredBlobs[sha] = source;
            return source;
        }

        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            Task.FromResult(Fresh(_tree));

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) => RootResult.Task;

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            DeferredBlobs.TryGetValue(sha, out TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>>? source)
                ? source.Task
                : Task.FromResult(Fresh(Blobs[sha]));
    }

    private sealed class TruncatedNestedTreeService : IRepoTreeService
    {
        private readonly RepoTree _tree;

        public TruncatedNestedTreeService(RepoTree tree) => _tree = tree;

        public Dictionary<string, RepoFileBlob> Blobs { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>>> DeferredBlobs { get; } =
            new(StringComparer.Ordinal);
        public List<string> RequestedDirectories { get; } = [];
        public List<string> RequestedBlobShas { get; } = [];
        public TaskCompletionSource<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> RootResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>> DeferBlob(string sha)
        {
            TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>> source =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            DeferredBlobs[sha] = source;
            return source;
        }

        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            Task.FromResult(Fresh(_tree));

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
        {
            RequestedDirectories.Add(path);
            return path.Length == 0
                ? RootResult.Task
                : Task.FromResult(Fresh<IReadOnlyList<RepoTreeNode>>([
                    File("src/App.cs", "app-new")]
                ));
        }

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
        {
            RequestedBlobShas.Add(sha);
            return DeferredBlobs.TryGetValue(sha, out TaskCompletionSource<RepoCodeLoadResult<RepoFileBlob>>? source)
                ? source.Task
                : Task.FromResult(Fresh(Blobs[sha]));
        }
    }

    private sealed class TruncatedRefChangeTreeService : IRepoTreeService
    {
        private readonly bool _includeRequestedFile;

        public TruncatedRefChangeTreeService(bool includeRequestedFile)
        {
            _includeRequestedFile = includeRequestedFile;
            Blobs["app-old"] = Blob("app-old", "old content");
            Blobs["app-new"] = Blob("app-new", "new content");
            Blobs["readme-next"] = Blob("readme-next", "next readme");
        }

        public Dictionary<string, RepoFileBlob> Blobs { get; } = new(StringComparer.Ordinal);
        public List<(string Ref, string Path)> RequestedDirectories { get; } = [];

        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
        {
            ct.ThrowIfCancellationRequested();
            RepoTree tree = refOrSha == "main"
                ? CreateTree(Directory("src", "src-old", File("src/App.cs", "app-old")))
                : CreateTree(truncated: true, Directory("src", "src-partial"));
            return Task.FromResult(Fresh(tree));
        }

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
        {
            ct.ThrowIfCancellationRequested();
            RequestedDirectories.Add((refOrSha, path));
            if (path.Length == 0)
            {
                IReadOnlyList<RepoTreeNode> root = _includeRequestedFile
                    ? [Directory("src", "src-new"), File("README.md", "readme-next")]
                    : [File("README.md", "readme-next")];
                return Task.FromResult(Fresh(root));
            }

            return Task.FromResult(Fresh<IReadOnlyList<RepoTreeNode>>(
                _includeRequestedFile ? [File("src/App.cs", "app-new")] : []));
        }

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(Fresh(Blobs[sha]));
        }
    }

    private sealed class CancellationAwarePageTreeService : IRepoTreeService
    {
        public string? CompleteRef { get; set; }

        public async Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
        {
            if (string.Equals(refOrSha, CompleteRef, StringComparison.Ordinal))
            {
                return Fresh(CreateTree(File("file.cs", "file-sha")));
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable.");
        }

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            Task.FromResult(Fresh<IReadOnlyList<RepoTreeNode>>([]));

        public async Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class MemoryRepoFileCache : IRepoFileCacheService
    {
        private readonly ConcurrentDictionary<RepoFileCacheKey, RepoFileCacheEntry> _entries = new();
        public string RootPath => "memory";
        public long DiskSoftCapBytes => 0;
        public TimeSpan Ttl => TimeSpan.FromDays(7);

        public bool TryGet(RepoFileCacheKey key, out RepoFileCacheEntry entry) => _entries.TryGetValue(key, out entry!);
        public Task<RepoFileCacheEntry?> GetAsync(RepoFileCacheKey key, CancellationToken ct) =>
            Task.FromResult(_entries.TryGetValue(key, out RepoFileCacheEntry? entry) ? entry : null);
        public Task PutAsync(RepoFileCacheKey key, RepoFileCacheEntry entry, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _entries[key] = entry;
            return Task.CompletedTask;
        }
        public Task PurgeAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<long> GetTotalBytesAsync(CancellationToken ct = default) => Task.FromResult(0L);
        public Task ClearAllAsync(CancellationToken ct = default)
        {
            _entries.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class TestAccountService : IAccountService
    {
        private long _userId = 42;
        public void RemoveUser() => _userId = 0;
        public void SaveUser(long userId) => _userId = userId;
        public long GetUser() => _userId;
    }
}
