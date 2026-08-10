using System;
using System.Collections.Generic;
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
        trees.LoadTreeAsync(
                "octo",
                "app",
                "main",
                Arg.Any<CancellationToken>(),
                QueryFetchPolicy.StaleFirst)
            .Returns(Task.FromResult(CreateResult("one")));
        RepoCodeNavigationPreparationCache cache = CreateCache(trees);

        await cache.PrefetchAsync("octo", "app", "main");
        RepoCodeNavigationPreparationCache.PreparedRepoCodeNavigation prepared =
            await cache.TakeOrPrepareAsync("octo", "app", "main", CancellationToken.None);

        Assert.Equal("one", prepared.Result.Value.Sha);
        Assert.Equal(2, prepared.PreparedTree.NodesByPath.Count);
        Assert.Equal(0, cache.Count);
        await trees.Received(1).LoadTreeAsync(
            "octo",
            "app",
            "main",
            Arg.Any<CancellationToken>(),
            QueryFetchPolicy.StaleFirst);
    }

    [Fact]
    public async Task CancelledHover_DoesNotCancelOrDiscardSharedPreparation()
    {
        TaskCompletionSource<RepoCodeLoadResult<RepoTree>> source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        trees.LoadTreeAsync(
                "octo",
                "app",
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

        Assert.Equal("shared", prepared.Result.Value.Sha);
        await trees.Received(1).LoadTreeAsync(
            "octo",
            "app",
            "main",
            Arg.Any<CancellationToken>(),
            QueryFetchPolicy.StaleFirst);
    }

    [Fact]
    public async Task Prefetch_IsBoundedAndPartitionsByAccount()
    {
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        trees.LoadTreeAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
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
        await trees.Received(11).LoadTreeAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>(),
            QueryFetchPolicy.StaleFirst);
    }

    [Fact]
    public async Task RouteCancellation_CancelsUnderlyingPreparationAndRemovesEntry()
    {
        TaskCompletionSource<RepoCodeLoadResult<RepoTree>> source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken workToken = default;
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        trees.LoadTreeAsync(
                "octo",
                "app",
                "main",
                Arg.Any<CancellationToken>(),
                QueryFetchPolicy.StaleFirst)
            .Returns(call =>
            {
                workToken = call.ArgAt<CancellationToken>(3);
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
        TaskCompletionSource<RepoCodeLoadResult<RepoTree>> source =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken workToken = default;
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        trees.LoadTreeAsync(
                "octo",
                "app",
                "main",
                Arg.Any<CancellationToken>(),
                QueryFetchPolicy.StaleFirst)
            .Returns(call =>
            {
                workToken = call.ArgAt<CancellationToken>(3);
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
        Assert.Equal("foreground", prepared.Result.Value.Sha);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task LruEviction_CancelsObsoletePreparationAndKeepsEightEntries()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dictionary<string, CancellationToken> workTokens = new(StringComparer.Ordinal);
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        trees.LoadTreeAsync(
                "octo",
                Arg.Any<string>(),
                "main",
                Arg.Any<CancellationToken>(),
                QueryFetchPolicy.StaleFirst)
            .Returns(async call =>
            {
                string repository = call.ArgAt<string>(1);
                CancellationToken token = call.ArgAt<CancellationToken>(3);
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

    private static RepoCodeLoadResult<RepoTree> CreateResult(string sha)
    {
        RepoTree tree = new()
        {
            Sha = sha,
            Root = new RepoTreeNode
            {
                Name = string.Empty,
                Path = string.Empty,
                IsDirectory = true,
                Children = new List<RepoTreeNode>
                {
                    new()
                    {
                        Name = "src",
                        Path = "src",
                        Sha = "directory",
                        IsDirectory = true,
                        Children = new List<RepoTreeNode>
                        {
                            new()
                            {
                                Name = "App.cs",
                                Path = "src/App.cs",
                                Sha = "file"
                            }
                        }
                    }
                }
            }
        };
        return new RepoCodeLoadResult<RepoTree>(
            tree,
            CacheState.Fresh,
            FetchedAt: DateTimeOffset.UtcNow,
            StaleAfter: DateTimeOffset.UtcNow.AddMinutes(30));
    }
}
