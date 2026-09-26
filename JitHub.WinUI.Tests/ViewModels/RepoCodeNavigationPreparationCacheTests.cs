using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.CodeViewer;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.ViewModels.CodeViewer;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class RepoCodeNavigationPreparationCacheTests
{
    [Fact]
    public async Task Prefetch_TransfersPreparedProjectionToForegroundOnce()
    {
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        trees.LoadDirectoryAsync(
                "octo",
                "app",
                string.Empty,
                "main",
                Arg.Any<CancellationToken>(),
                QueryFetchPolicy.StaleFirst)
            .Returns(Task.FromResult(CreateResult("one")));
        RepoCodeNavigationPreparationCache cache = CreateCache(trees);

        await cache.PrefetchAsync("octo", "app", "main");
        RepoCodeNavigationPreparationCache.PreparedRepoCodeNavigation prepared =
            await cache.TakeOrPrepareAsync("octo", "app", "main", CancellationToken.None);

        Assert.Equal("one", Assert.Single(prepared.Result.Value.Root.Children).Sha);
        Assert.Single(prepared.PreparedTree.NodesByPath);
        Assert.Equal(0, cache.Count);
        await trees.Received(1).LoadDirectoryAsync(
            "octo",
            "app",
            string.Empty,
            "main",
            Arg.Any<CancellationToken>(),
            QueryFetchPolicy.StaleFirst);
    }

    [Fact]
    public async Task Prefetch_LoadsCanonicalReadmeBesideRootAndTransfersMatchingBytes()
    {
        TaskCompletionSource<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> root =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<RepoCodeLoadResult<RepoReadmeFile>?> readme =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        trees.LoadDirectoryAsync(
                "octo", "app", string.Empty, "main", Arg.Any<CancellationToken>(), QueryFetchPolicy.StaleFirst)
            .Returns(root.Task);
        trees.LoadReadmeAsync(
                "octo", "app", "main", Arg.Any<CancellationToken>(), QueryFetchPolicy.StaleFirst)
            .Returns(readme.Task);
        RepoCodeNavigationPreparationCache cache = CreateCache(trees);

        Task prefetch = cache.PrefetchAsync("octo", "app", "main");
        await trees.Received(1).LoadDirectoryAsync(
            "octo", "app", string.Empty, "main", Arg.Any<CancellationToken>(), QueryFetchPolicy.StaleFirst);
        await trees.Received(1).LoadReadmeAsync(
            "octo", "app", "main", Arg.Any<CancellationToken>(), QueryFetchPolicy.StaleFirst);

        root.SetResult(CreateReadmeRootResult());
        readme.SetResult(new RepoCodeLoadResult<RepoReadmeFile>(
            new RepoReadmeFile(
                "README.md",
                "README.md",
                new RepoFileBlob
                {
                    Sha = "readme-sha",
                    Encoding = "base64",
                    Bytes = [1, 2, 3],
                    Text = "# Ready"
                }),
            CacheState.Fresh));
        await prefetch;

        RepoCodeNavigationPreparationCache.PreparedRepoCodeNavigation prepared =
            await cache.TakeOrPrepareAsync("octo", "app", "main", CancellationToken.None);
        Assert.NotNull(prepared.Readme);
        Assert.Equal("# Ready", prepared.Readme!.Value.Blob.Text);
        Assert.Contains("README.md", prepared.PreparedTree.NodesByPath.Keys);
    }

    [Fact]
    public async Task PublicReadmeSurvivesAuthenticatedRootListingIpDenialWithoutClaimingCompleteTree()
    {
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        GitHubRateLimitException denial = new(
            HttpStatusCode.Forbidden,
            "The organization has an IP allow list enabled.",
            TimeSpan.Zero);
        trees.LoadDirectoryAsync(
                "octo", "app", string.Empty, "main", Arg.Any<CancellationToken>(), QueryFetchPolicy.StaleFirst)
            .Returns(Task.FromException<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>>(denial));
        trees.LoadReadmeAsync(
                "octo", "app", "main", Arg.Any<CancellationToken>(), QueryFetchPolicy.StaleFirst)
            .Returns(Task.FromResult<RepoCodeLoadResult<RepoReadmeFile>?>(
                new RepoCodeLoadResult<RepoReadmeFile>(
                    new RepoReadmeFile(
                        "README.md",
                        "README.md",
                        new RepoFileBlob
                        {
                            Sha = "readme-sha",
                            Encoding = "base64",
                            Bytes = [1, 2, 3],
                            Text = "# Public"
                        }),
                    CacheState.Fresh)));

        RepoCodeNavigationPreparationCache.PreparedRepoCodeNavigation prepared =
            await CreateCache(trees).TakeOrPrepareAsync("octo", "app", "main", CancellationToken.None);

        Assert.True(prepared.RootListingUnavailable);
        Assert.Equal(CacheState.Error, prepared.Result.CacheState);
        Assert.True(prepared.Result.Value.Truncated);
        Assert.False(prepared.Result.Value.RootIsAuthoritative);
        Assert.NotNull(prepared.Result.RefreshError);
        Assert.Equal("README.md", Assert.Single(prepared.Result.Value.Root.Children).Path);
        Assert.Contains("README.md", prepared.PreparedTree.NodesByPath.Keys);
        Assert.Equal("# Public", prepared.Readme!.Value.Blob.Text);
    }

    [Fact]
    public async Task RootListingDenialWithoutFreshReadmeRemainsFailure()
    {
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        trees.LoadDirectoryAsync(
                "octo", "app", string.Empty, "main", Arg.Any<CancellationToken>(), QueryFetchPolicy.StaleFirst)
            .Returns(Task.FromException<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>>(
                new GitHubRateLimitException(
                    HttpStatusCode.Forbidden,
                    "The organization has an IP allow list enabled.",
                    TimeSpan.Zero)));

        await Assert.ThrowsAsync<GitHubRateLimitException>(() =>
            CreateCache(trees).TakeOrPrepareAsync("octo", "app", "main", CancellationToken.None));
    }

    [Fact]
    public async Task CancelledHover_DoesNotCancelOrDiscardSharedPreparation()
    {
        TaskCompletionSource<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        trees.LoadDirectoryAsync(
                "octo",
                "app",
                string.Empty,
                "main",
                Arg.Any<CancellationToken>(),
                QueryFetchPolicy.StaleFirst)
            .Returns(source.Task);
        RepoCodeNavigationPreparationCache cache = CreateCache(trees);
        using CancellationTokenSource hover = new();

        Task prefetch = cache.PrefetchAsync("octo", "app", "main", hover.Token);
        hover.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prefetch);
        source.SetResult(CreateResult("shared"));
        RepoCodeNavigationPreparationCache.PreparedRepoCodeNavigation prepared =
            await cache.TakeOrPrepareAsync("octo", "app", "main", CancellationToken.None);

        Assert.Equal("shared", Assert.Single(prepared.Result.Value.Root.Children).Sha);
        await trees.Received(1).LoadDirectoryAsync(
            "octo",
            "app",
            string.Empty,
            "main",
            Arg.Any<CancellationToken>(),
            QueryFetchPolicy.StaleFirst);
    }

    [Fact]
    public async Task Prefetch_IsBoundedAndPartitionsByAccount()
    {
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        trees.LoadDirectoryAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                string.Empty,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>(),
                QueryFetchPolicy.StaleFirst)
            .Returns(call => Task.FromResult(CreateResult(call.ArgAt<string>(1))));
        IAccountService account = Substitute.For<IAccountService>();
        account.GetUser().Returns(42);
        RepoCodeNavigationPreparationCache cache =
            new(trees, new LanguageIdResolver(), account);

        for (int index = 0; index < 10; index++)
        {
            await cache.PrefetchAsync("octo", $"repo-{index}", "main");
        }

        Assert.Equal(8, cache.Count);
        account.GetUser().Returns(84);
        await cache.PrefetchAsync("octo", "repo-9", "main");
        Assert.Equal(8, cache.Count);
        await trees.Received(11).LoadDirectoryAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            string.Empty,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            QueryFetchPolicy.StaleFirst);
    }

    [Fact]
    public async Task RouteCancellation_CancelsUnderlyingPreparationAndRemovesEntry()
    {
        TaskCompletionSource<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken workToken = default;
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        trees.LoadDirectoryAsync(
                "octo",
                "app",
                string.Empty,
                "main",
                Arg.Any<CancellationToken>(),
                QueryFetchPolicy.StaleFirst)
            .Returns(call =>
            {
                workToken = call.ArgAt<CancellationToken>(4);
                return source.Task;
            });
        RepoCodeNavigationPreparationCache cache = CreateCache(trees);
        using CancellationTokenSource route = new();

        Task prefetch = cache.PrefetchRouteAsync("octo", "app", "main", route.Token);
        route.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prefetch);
        Assert.True(workToken.IsCancellationRequested);
        Assert.Equal(0, cache.Count);
        source.TrySetCanceled(workToken);
    }

    [Fact]
    public async Task ForegroundClaim_PreservesSharedPreparationWhenRouteIsCancelled()
    {
        TaskCompletionSource<RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>> source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken workToken = default;
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        trees.LoadDirectoryAsync(
                "octo",
                "app",
                string.Empty,
                "main",
                Arg.Any<CancellationToken>(),
                QueryFetchPolicy.StaleFirst)
            .Returns(call =>
            {
                workToken = call.ArgAt<CancellationToken>(4);
                return source.Task;
            });
        RepoCodeNavigationPreparationCache cache = CreateCache(trees);
        using CancellationTokenSource route = new();

        Task prefetch = cache.PrefetchRouteAsync("octo", "app", "main", route.Token);
        Task<RepoCodeNavigationPreparationCache.PreparedRepoCodeNavigation> foreground =
            cache.TakeOrPrepareAsync("octo", "app", "main", CancellationToken.None);
        route.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prefetch);

        Assert.False(workToken.IsCancellationRequested);
        source.SetResult(CreateResult("foreground"));
        RepoCodeNavigationPreparationCache.PreparedRepoCodeNavigation prepared = await foreground;
        Assert.Equal("foreground", Assert.Single(prepared.Result.Value.Root.Children).Sha);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task LruEviction_CancelsObsoletePreparationAndKeepsEightEntries()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dictionary<string, CancellationToken> workTokens = new(StringComparer.Ordinal);
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        trees.LoadDirectoryAsync(
                "octo",
                Arg.Any<string>(),
                string.Empty,
                "main",
                Arg.Any<CancellationToken>(),
                QueryFetchPolicy.StaleFirst)
            .Returns(async call =>
            {
                string repository = call.ArgAt<string>(1);
                CancellationToken token = call.ArgAt<CancellationToken>(4);
                lock (workTokens)
                {
                    workTokens[repository] = token;
                }

                await release.Task.WaitAsync(token);
                return CreateResult(repository);
            });
        RepoCodeNavigationPreparationCache cache = CreateCache(trees);
        List<Task> prefetches = [];

        for (int index = 0; index < 9; index++)
        {
            prefetches.Add(cache.PrefetchAsync("octo", $"repo-{index}", "main"));
        }

        Assert.Equal(8, cache.Count);
        Assert.True(workTokens["repo-0"].IsCancellationRequested);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prefetches[0]);
        await Task.WhenAll(prefetches.GetRange(1, 8));
    }

    private static RepoCodeNavigationPreparationCache CreateCache(IRepoTreeService trees)
    {
        IAccountService account = Substitute.For<IAccountService>();
        account.GetUser().Returns(42);
        return new RepoCodeNavigationPreparationCache(trees, new LanguageIdResolver(), account);
    }

    private static RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>> CreateResult(string sha)
    {
        IReadOnlyList<RepoTreeNode> nodes =
        [
            new RepoTreeNode
            {
                Name = "src",
                Path = "src",
                Sha = sha,
                IsDirectory = true,
                Children = []
            }
        ];
        return new RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>>(
            nodes,
            CacheState.Fresh,
            FetchedAt: DateTimeOffset.UtcNow,
            StaleAfter: DateTimeOffset.UtcNow.AddMinutes(30));
    }

    private static RepoCodeLoadResult<IReadOnlyList<RepoTreeNode>> CreateReadmeRootResult() => new(
        [
            new RepoTreeNode
            {
                Name = "README.md",
                Path = "README.md",
                Sha = "readme-sha",
                Size = 7,
                IsDirectory = false,
                Children = []
            }
        ],
        CacheState.Fresh,
        FetchedAt: DateTimeOffset.UtcNow,
        StaleAfter: DateTimeOffset.UtcNow.AddMinutes(30));
}
