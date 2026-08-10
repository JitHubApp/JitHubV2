using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.CodeViewer;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.ViewModels.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class RepoFileTreeViewModelTests
{
    [Fact]
    public void Load_AppliesKeyedChangesAndPreservesSelectionByPath()
    {
        RepoFileTreeViewModel viewModel = new(new NoopTreeService(), new LanguageIdResolver());
        viewModel.Load(CreateTree(
            File("README.md", "old-sha"),
            File("obsolete.txt", "obsolete")), "owner", "repo", "main");
        RepoTreeNodeViewModel readme = viewModel.RootNodes[0];
        viewModel.SelectedNode = readme;

        viewModel.Load(CreateTree(
            File("README.md", "new-sha"),
            File("added.txt", "added")), "owner", "repo", "main");

        Assert.Same(readme, viewModel.RootNodes[0]);
        Assert.Same(readme, viewModel.SelectedNode);
        Assert.Equal("new-sha", readme.Sha);
        Assert.DoesNotContain(viewModel.RootNodes, node => node.Path == "obsolete.txt");
        Assert.Contains(viewModel.RootNodes, node => node.Path == "added.txt");
    }

    [Fact]
    public void Load_ReusesNestedDirectoryAndFileInstances()
    {
        RepoTreeNode firstDirectory = Directory("src", File("src/App.cs", "one"));
        RepoFileTreeViewModel viewModel = new(new NoopTreeService(), new LanguageIdResolver());
        viewModel.Load(CreateTree(firstDirectory), "owner", "repo", "main");
        RepoTreeNodeViewModel directory = viewModel.RootNodes[0];
        RepoTreeNodeViewModel file = directory.Children[0];

        viewModel.Load(CreateTree(Directory("src", File("src/App.cs", "two"))), "owner", "repo", "main");

        Assert.Same(directory, viewModel.RootNodes[0]);
        Assert.Same(file, viewModel.RootNodes[0].Children[0]);
        Assert.Equal("two", file.Sha);
    }

    [Fact]
    public async Task DirectoryLoad_FromPreviousRefCannotMutateCurrentTree()
    {
        DeferredDirectoryTreeService service = new();
        RepoFileTreeViewModel viewModel = new(service, new LanguageIdResolver());
        viewModel.Load(CreateTree(Directory("src")), "owner", "repo", "main");
        RepoTreeNodeViewModel oldDirectory = viewModel.RootNodes[0];

        Task pending = viewModel.LoadDirectoryAsync(oldDirectory, default);
        viewModel.Load(CreateTree(Directory("next")), "owner", "repo", "next");
        service.Source.SetResult(new RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>(
            [File("src/late.cs", "late")],
            CacheState.Fresh));
        await pending;

        Assert.Empty(oldDirectory.Children);
        Assert.Equal("next", viewModel.RootNodes[0].Path);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task Load_TruncatedTreeReconcilesRootWithAuthoritativeContents()
    {
        RootDirectoryTreeService service = new([
            Directory("src"),
            File("README.md", "readme")]);
        RepoFileTreeViewModel viewModel = new(service, new LanguageIdResolver());

        viewModel.Load(CreateTree(
            truncated: true,
            Directory("src", File("src/partial.cs", "partial")),
            File("ghost.txt", "ghost")), "owner", "repo", "main");
        await viewModel.RootReconciliationTask;

        Assert.DoesNotContain(viewModel.RootNodes, node => node.Path == "ghost.txt");
        Assert.Contains(viewModel.RootNodes, node => node.Path == "README.md");
        RepoTreeNodeViewModel src = Assert.Single(viewModel.RootNodes, node => node.Path == "src");
        Assert.False(src.ChildrenLoaded);
        Assert.Contains(src.Children, node => node.Path == "src/partial.cs");
        Assert.Contains(service.Requests, request => request.Path == string.Empty);
    }

    [Fact]
    public async Task Load_TruncatedRecursiveChildrenAreNotTreatedAsAuthoritative()
    {
        RootDirectoryTreeService service = new([Directory("src")]);
        RepoFileTreeViewModel viewModel = new(service, new LanguageIdResolver());

        viewModel.Load(CreateTree(
            truncated: true,
            Directory("src", File("src/partial.cs", "partial"))), "owner", "repo", "main");
        await viewModel.RootReconciliationTask;

        RepoTreeNodeViewModel src = Assert.Single(viewModel.RootNodes);
        Assert.False(src.ChildrenLoaded);
    }

    [Fact]
    public async Task CancelPendingRequests_PreventsLateRootReconciliation()
    {
        DeferredRootDirectoryTreeService service = new();
        RepoFileTreeViewModel viewModel = new(service, new LanguageIdResolver());
        viewModel.Load(CreateTree(
            truncated: true,
            File("partial.txt", "partial")), "owner", "repo", "main");
        Task pending = viewModel.RootReconciliationTask;

        viewModel.CancelPendingRequests();
        service.Source.SetResult(new RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>(
            [File("late.txt", "late")],
            CacheState.Fresh));
        await pending;

        Assert.Equal("partial.txt", Assert.Single(viewModel.RootNodes).Path);
    }

    [Fact]
    public async Task LoadIncrementally_RouteCancellationCancelsRootReconciliation()
    {
        RouteCancellationTreeService service = new();
        RepoFileTreeViewModel viewModel = new(service, new LanguageIdResolver());
        RepoFileTreeViewModel.PreparedTree prepared = await viewModel.PrepareLoadAsync(
            CreateTree(truncated: true, File("partial.txt", "partial")),
            default);
        using CancellationTokenSource route = new();

        bool applied = await viewModel.LoadIncrementallyAsync(
            prepared,
            "owner",
            "repo",
            "main",
            viewModel.BeginSourceRequest(),
            sourceIsAuthoritative: true,
            route.Token);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        route.Cancel();
        await viewModel.RootReconciliationTask;

        Assert.True(applied);
        Assert.True(service.Cancelled.Task.IsCompletedSuccessfully);
        Assert.Equal("partial.txt", Assert.Single(viewModel.RootNodes).Path);
    }

    [Fact]
    public async Task LoadDirectory_CachedRowsReturnBeforeBackgroundRefreshCompletes()
    {
        CachedThenRefreshDirectoryTreeService service = new(
            [File("src/cached.cs", "cached")]);
        RepoFileTreeViewModel viewModel = new(service, new LanguageIdResolver());
        viewModel.Load(CreateTree(Directory("src")), "owner", "repo", "main");
        RepoTreeNodeViewModel src = viewModel.RootNodes[0];
        src.ChildrenLoaded = false;

        await viewModel.LoadDirectoryAsync(src, default);

        Assert.Equal("src/cached.cs", Assert.Single(src.Children).Path);
        Assert.False(service.NetworkResult.Task.IsCompleted);

        service.NetworkResult.SetResult(new RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>(
            [File("src/fresh.cs", "fresh")],
            CacheState.Fresh));
        await WaitUntilAsync(() => src.Children.Count == 1 && src.Children[0].Path == "src/fresh.cs");

        Assert.Equal("src/fresh.cs", Assert.Single(src.Children).Path);
    }

    [Fact]
    public async Task LoadDirectory_StaleRowsMergeWithoutDowngradingRecursiveTree()
    {
        CachedThenRefreshDirectoryTreeService service = new([
            File("src/current.cs", "stale-sha"),
            File("src/cached-only.cs", "cached-only")]);
        RepoFileTreeViewModel viewModel = new(service, new LanguageIdResolver());
        viewModel.Load(CreateTree(Directory(
            "src",
            File("src/current.cs", "recursive-sha"),
            File("src/recursive-only.cs", "recursive-only"))), "owner", "repo", "main");
        RepoTreeNodeViewModel src = viewModel.RootNodes[0];
        RepoTreeNodeViewModel current = src.Children[0];
        RepoTreeNodeViewModel recursiveOnly = src.Children[1];
        src.ChildrenLoaded = false;

        await viewModel.LoadDirectoryAsync(src, default);

        Assert.Same(current, src.Children.Single(node => node.Path == "src/current.cs"));
        Assert.Equal("recursive-sha", current.Sha);
        Assert.Same(recursiveOnly, src.Children.Single(node => node.Path == "src/recursive-only.cs"));
        Assert.Contains(src.Children, node => node.Path == "src/cached-only.cs");
        Assert.False(src.ChildrenLoaded);

        service.NetworkResult.SetResult(new RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>(
            [File("src/current.cs", "network-sha")],
            CacheState.Fresh));
        await viewModel.PendingReconciliationTask;

        Assert.Same(current, Assert.Single(src.Children));
        Assert.Equal("network-sha", current.Sha);
        Assert.True(src.ChildrenLoaded);
    }

    [Fact]
    public async Task LoadDirectory_RefreshFailurePreservesRecursiveAndCachedRows()
    {
        CachedThenRefreshDirectoryTreeService service = new([
            File("src/current.cs", "stale-sha"),
            File("src/cached-only.cs", "cached-only")]);
        RepoFileTreeViewModel viewModel = new(service, new LanguageIdResolver());
        viewModel.Load(CreateTree(Directory(
            "src",
            File("src/current.cs", "recursive-sha"),
            File("src/recursive-only.cs", "recursive-only"))), "owner", "repo", "main");
        RepoTreeNodeViewModel src = viewModel.RootNodes[0];
        src.ChildrenLoaded = false;

        await viewModel.LoadDirectoryAsync(src, default);
        service.NetworkResult.SetException(new InvalidOperationException("offline"));
        await viewModel.PendingReconciliationTask;

        Assert.Equal(3, src.Children.Count);
        Assert.Equal("recursive-sha", src.Children.Single(node => node.Path == "src/current.cs").Sha);
        Assert.Contains(src.Children, node => node.Path == "src/recursive-only.cs");
        Assert.Contains(src.Children, node => node.Path == "src/cached-only.cs");
        Assert.Contains("cached folder contents", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("offline", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_TruncatedRootStaleCacheMergesWithoutReplacingNewerPartialTree()
    {
        StaleRootThenFailureTreeService service = new([
            Directory("src"),
            File("cached-root.txt", "cached-root")]);
        RepoFileTreeViewModel viewModel = new(service, new LanguageIdResolver());
        viewModel.Load(CreateTree(
            truncated: true,
            Directory("src", File("src/partial.cs", "partial")),
            File("recursive-root.txt", "recursive-root")), "owner", "repo", "main");

        await service.NetworkRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
        RepoTreeNodeViewModel src = viewModel.RootNodes.Single(node => node.Path == "src");
        Assert.Equal("src-sha", src.Sha);
        Assert.Contains(src.Children, node => node.Path == "src/partial.cs");
        Assert.Contains(viewModel.RootNodes, node => node.Path == "recursive-root.txt");
        Assert.Contains(viewModel.RootNodes, node => node.Path == "cached-root.txt");

        service.NetworkResult.SetException(new InvalidOperationException("offline"));
        await viewModel.PendingReconciliationTask;

        Assert.Contains(src.Children, node => node.Path == "src/partial.cs");
        Assert.Contains(viewModel.RootNodes, node => node.Path == "recursive-root.txt");
        Assert.Contains(viewModel.RootNodes, node => node.Path == "cached-root.txt");
        Assert.False(viewModel.IsRootAuthoritative);
        Assert.Contains("partial repository tree", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("offline", viewModel.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RepeatedContextReplacementCancelsDirectoryWorkWithoutDisposalRaces()
    {
        CancellationAwareTreeService service = new();
        RepoFileTreeViewModel viewModel = new(service, new LanguageIdResolver());
        List<Task> pending = [];

        for (int index = 0; index < 40; index++)
        {
            viewModel.Load(CreateTree(Directory($"src-{index}")), "owner", "repo", $"ref-{index}");
            RepoTreeNodeViewModel directory = viewModel.RootNodes[0];
            directory.ChildrenLoaded = false;
            pending.Add(viewModel.LoadDirectoryAsync(directory, default));
        }

        viewModel.CancelPendingRequests();
        await Task.WhenAll(pending);
        await viewModel.PendingReconciliationTask;

        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task PreviousGenerationRootFailureCannotPublishIntoCurrentTree()
    {
        DeferredRootDirectoryTreeService service = new();
        RepoFileTreeViewModel viewModel = new(service, new LanguageIdResolver());
        viewModel.Load(CreateTree(truncated: true, File("old.txt", "old")), "owner", "repo", "old");
        Task oldReconciliation = viewModel.RootReconciliationTask;

        viewModel.Load(CreateTree(File("new.txt", "new")), "owner", "repo", "new");
        service.Source.SetException(new InvalidOperationException("old failure"));
        await oldReconciliation;

        Assert.Equal("new.txt", Assert.Single(viewModel.RootNodes).Path);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task PrepareLoadAsync_DoesNotProjectLargeTreeInline()
    {
        BlockingLanguageResolver resolver = new();
        RepoFileTreeViewModel viewModel = new(new NoopTreeService(), resolver);
        RepoTree tree = CreateTree(Enumerable.Range(0, 500)
            .Select(index => File($"src/File{index}.cs", $"sha-{index}"))
            .ToArray());

        Task<RepoFileTreeViewModel.PreparedTree> pending = viewModel.PrepareLoadAsync(tree, default);
        await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(pending.IsCompleted);
        resolver.Release.Set();
        RepoFileTreeViewModel.PreparedTree prepared = await pending;

        Assert.Equal(500, prepared.NodesByPath.Count);
        Assert.Equal(500, prepared.LeafNodesByPath.Count);
    }

    [Fact]
    public async Task DelayedTreeCannotReplaceDirectoryRequestThatCommittedLater()
    {
        TimestampDirectoryTreeService service = new(new RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>(
            [File("src/current.cs", "directory-sha")], CacheState.Fresh));
        RepoFileTreeViewModel viewModel = new(service, new LanguageIdResolver());
        viewModel.Load(CreateTree(Directory("src", File("src/current.cs", "initial-sha"))), "owner", "repo", "main");
        long delayedTreeGeneration = viewModel.BeginSourceRequest();
        RepoFileTreeViewModel.PreparedTree delayedTree = await viewModel.PrepareLoadAsync(
            CreateTree(Directory("src", File("src/current.cs", "delayed-tree-sha"), File("src/stale.cs", "stale"))),
            default);
        RepoTreeNodeViewModel directory = viewModel.RootNodes[0];
        directory.ChildrenLoaded = false;

        await viewModel.LoadDirectoryAsync(directory, default);
        await viewModel.PendingReconciliationTask;
        bool delayedApplied = viewModel.Load(delayedTree, "owner", "repo", "main", delayedTreeGeneration);

        Assert.False(delayedApplied);
        Assert.Equal("directory-sha", Assert.Single(directory.Children).Sha);
        Assert.True(directory.ChildrenLoaded);
    }

    [Fact]
    public async Task MutableFreshDirectoryCacheWaitsForAuthoritativeNetworkGeneration()
    {
        TimestampDirectoryTreeService service = new(new RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>(
            [File("src/current.cs", "directory-sha")],
            CacheState.Fresh));
        RepoFileTreeViewModel viewModel = new(service, new LanguageIdResolver());
        RepoTree tree = CreateTree(Directory(
            "src",
            File("src/current.cs", "recursive-sha"),
            File("src/removed.cs", "removed")));
        RepoFileTreeViewModel.PreparedTree prepared = await viewModel.PrepareLoadAsync(tree, default);
        Assert.True(viewModel.Load(prepared, "owner", "repo", "main", viewModel.BeginSourceRequest()));
        RepoTreeNodeViewModel directory = viewModel.RootNodes[0];
        directory.ChildrenLoaded = false;

        await viewModel.LoadDirectoryAsync(directory, default);
        await viewModel.PendingReconciliationTask;

        Assert.Equal("directory-sha", Assert.Single(directory.Children).Sha);
        Assert.True(directory.ChildrenLoaded);
    }

    [Fact]
    public async Task OlderRequestGenerationCannotReplaceNewerVisibleTreeAcrossRefs()
    {
        RepoFileTreeViewModel viewModel = new(new NoopTreeService(), new LanguageIdResolver());
        RepoFileTreeViewModel.PreparedTree current = await viewModel.PrepareLoadAsync(
            CreateTree(File("current.cs", "new")),
            default);
        RepoFileTreeViewModel.PreparedTree older = await viewModel.PrepareLoadAsync(
            CreateTree(File("old.cs", "old")),
            default);

        long newestGeneration = viewModel.BeginSourceRequest();
        Assert.True(viewModel.Load(current, "owner", "repo", "main", newestGeneration));
        Assert.False(viewModel.Load(older, "owner", "repo", "older-ref", newestGeneration - 1));

        Assert.Equal("current.cs", Assert.Single(viewModel.RootNodes).Path);
    }

    [Fact]
    public async Task IncrementalTreeApply_MeetsCachedRenderBudgetForLargeFixture()
    {
        RepoFileTreeViewModel viewModel = new(new NoopTreeService(), new LanguageIdResolver());
        RepoTree tree = CreateTree(Enumerable.Range(0, 2000)
            .Select(index => File($"File{index:0000}.cs", $"sha-{index}"))
            .ToArray());
        RepoFileTreeViewModel.PreparedTree prepared = await viewModel.PrepareLoadAsync(tree, default);
        Stopwatch stopwatch = Stopwatch.StartNew();

        bool applied = await viewModel.LoadIncrementallyAsync(
            prepared,
            "owner",
            "repo",
            "main",
            viewModel.BeginSourceRequest(),
            sourceIsAuthoritative: true,
            default);

        stopwatch.Stop();
        Assert.True(applied);
        Assert.Equal(2000, viewModel.RootNodes.Count);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(150),
            $"Cached tree projection took {stopwatch.Elapsed.TotalMilliseconds:0.0} ms.");
    }

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
        Name = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path,
        Path = path,
        Sha = sha,
        IsDirectory = false
    };

    private static RepoTreeNode Directory(string path, params RepoTreeNode[] children) => new()
    {
        Name = path,
        Path = path,
        Sha = $"{path}-sha",
        IsDirectory = true,
        Children = new List<RepoTreeNode>(children)
    };

    private sealed class NoopTreeService : IRepoTreeService
    {
        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new System.NotSupportedException();

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new System.NotSupportedException();

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new System.NotSupportedException();
    }

    private sealed class DeferredDirectoryTreeService : IRepoTreeService
    {
        public TaskCompletionSource<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new System.NotSupportedException();

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) => Source.Task;

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new System.NotSupportedException();
    }

    private sealed class TimestampDirectoryTreeService : IRepoTreeService
    {
        private readonly RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>> _result;

        public TimestampDirectoryTreeService(RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>> result) =>
            _result = result;

        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new NotSupportedException();

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            Task.FromResult(_result);

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new NotSupportedException();
    }

    private sealed class RootDirectoryTreeService : IRepoTreeService
    {
        private readonly IReadOnlyList<RepoTreeNode> _root;

        public RootDirectoryTreeService(IReadOnlyList<RepoTreeNode> root) => _root = root;

        public List<(string Path, QueryFetchPolicy Policy)> Requests { get; } = [];

        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new System.NotSupportedException();

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
        {
            Requests.Add((path, fetchPolicy));
            return Task.FromResult(new RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>(
                path.Length == 0 ? _root : [],
                CacheState.Fresh));
        }

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new System.NotSupportedException();
    }

    private sealed class CachedThenRefreshDirectoryTreeService : IRepoTreeService
    {
        private readonly IReadOnlyList<RepoTreeNode> _cached;

        public CachedThenRefreshDirectoryTreeService(IReadOnlyList<RepoTreeNode> cached) => _cached = cached;

        public TaskCompletionSource<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> NetworkResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new System.NotSupportedException();

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            fetchPolicy == QueryFetchPolicy.NetworkOnly
                ? NetworkResult.Task
                : Task.FromResult(new RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>(
                    _cached,
                    CacheState.Stale,
                    IsRefreshInProgress: true));

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new System.NotSupportedException();
    }

    private sealed class DeferredRootDirectoryTreeService : IRepoTreeService
    {
        public TaskCompletionSource<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> Source { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new System.NotSupportedException();

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) => Source.Task;

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new System.NotSupportedException();
    }

    private sealed class StaleRootThenFailureTreeService : IRepoTreeService
    {
        private readonly IReadOnlyList<RepoTreeNode> _cached;

        public StaleRootThenFailureTreeService(IReadOnlyList<RepoTreeNode> cached) => _cached = cached;

        public TaskCompletionSource NetworkRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> NetworkResult { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new NotSupportedException();

        public Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
        {
            if (fetchPolicy != QueryFetchPolicy.NetworkOnly)
            {
                return Task.FromResult(new RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>(
                    _cached,
                    CacheState.Stale,
                    IsRefreshInProgress: true));
            }

            NetworkRequested.TrySetResult();
            return NetworkResult.Task;
        }

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new NotSupportedException();
    }

    private sealed class CancellationAwareTreeService : IRepoTreeService
    {
        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new NotSupportedException();

        public async Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("Unreachable.");
        }

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new NotSupportedException();
    }

    private sealed class RouteCancellationTreeService : IRepoTreeService
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RepoCodeLoadResult<RepoTree>> LoadTreeAsync(
            string owner, string name, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new NotSupportedException();

        public async Task<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> LoadDirectoryAsync(
            string owner, string name, string path, string refOrSha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Cancelled.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("Unreachable.");
        }

        public Task<RepoCodeLoadResult<RepoFileBlob>> LoadBlobAsync(
            string owner, string name, string sha, CancellationToken ct,
            QueryFetchPolicy fetchPolicy = QueryFetchPolicy.StaleFirst) =>
            throw new NotSupportedException();
    }

    private sealed class BlockingLanguageResolver : ILanguageIdResolver
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Release { get; } = new(initialState: false);

        public string Resolve(string fileName, ReadOnlySpan<byte> contentSniff = default)
        {
            Started.TrySetResult();
            Release.Wait(TimeSpan.FromSeconds(2));
            return "text";
        }

        public bool IsKnown(string fileName) => true;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }
}
