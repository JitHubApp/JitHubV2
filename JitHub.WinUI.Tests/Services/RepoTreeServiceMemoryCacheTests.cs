using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.CodeViewer;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepoTreeServiceMemoryCacheTests
{
    [Fact]
    public async Task FreshTree_IsReusedWithoutAnotherQuery()
    {
        Harness harness = CreateHarness(FreshTree("first"));

        RepoCodeLoadResult<RepoTree> first =
            await harness.Service.LoadTreeAsync("octo", "app", "main", CancellationToken.None);
        RepoCodeLoadResult<RepoTree> second =
            await harness.Service.LoadTreeAsync("octo", "app", "main", CancellationToken.None);

        Assert.Equal("first", first.Value.Sha);
        Assert.Same(first.Value, second.Value);
        await harness.Query.Received(1).GetTreeAsync(
            "token",
            "42",
            "octo",
            "app",
            "main",
            QueryFetchPolicy.StaleFirst,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StaleMemoryTree_RendersImmediatelyAndStartsNetworkRefresh()
    {
        CachedResult<GitHubTree> stale = TreeResult(
            "stale",
            CacheState.Stale,
            DateTimeOffset.UtcNow.AddMinutes(-1));
        CachedResult<GitHubTree> fresh = FreshTree("fresh");
        Harness harness = CreateHarness(stale, fresh);

        _ = await harness.Service.LoadTreeAsync("octo", "app", "main", CancellationToken.None);
        RepoCodeLoadResult<RepoTree> visible =
            await harness.Service.LoadTreeAsync("octo", "app", "main", CancellationToken.None);

        Assert.Equal("stale", visible.Value.Sha);
        Assert.Equal(CacheState.Stale, visible.CacheState);
        Assert.True(visible.IsRefreshInProgress);
        await EventuallyAsync(async () =>
        {
            await harness.Query.Received(1).GetTreeAsync(
                "token",
                "42",
                "octo",
                "app",
                "main",
                QueryFetchPolicy.NetworkOnly,
                Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task CancelledPrefetch_DoesNotCancelForegroundWaiterOrDuplicateRequest()
    {
        TaskCompletionSource<CachedResult<GitHubTree>> response =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        Harness harness = CreateHarness();
        harness.Query.GetTreeAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<QueryFetchPolicy>(),
                Arg.Any<CancellationToken>())
            .Returns(response.Task);

        using CancellationTokenSource hover = new();
        Task prefetch = harness.Service.PrefetchTreeAsync("octo", "app", "main", hover.Token);
        hover.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => prefetch);

        Task<RepoCodeLoadResult<RepoTree>> foreground =
            harness.Service.LoadTreeAsync("octo", "app", "main", CancellationToken.None);
        response.SetResult(FreshTree("shared"));

        Assert.Equal("shared", (await foreground).Value.Sha);
        await harness.Query.Received(1).GetTreeAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            "octo",
            "app",
            "main",
            QueryFetchPolicy.StaleFirst,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClearPartition_RemovesMemoryTreeForThatAccount()
    {
        Harness harness = CreateHarness(FreshTree("first"), FreshTree("second"));

        Assert.Equal(
            "first",
            (await harness.Service.LoadTreeAsync("octo", "app", "main", CancellationToken.None)).Value.Sha);
        await harness.Service.ClearMemoryCacheAsync("42");
        Assert.Equal(
            "second",
            (await harness.Service.LoadTreeAsync("octo", "app", "main", CancellationToken.None)).Value.Sha);

        await harness.Query.Received(2).GetTreeAsync(
            Arg.Any<string>(),
            "42",
            "octo",
            "app",
            "main",
            QueryFetchPolicy.StaleFirst,
            Arg.Any<CancellationToken>());
    }

    private static Harness CreateHarness(params CachedResult<GitHubTree>[] results)
    {
        IGitHubRepoCodeQueryService query = Substitute.For<IGitHubRepoCodeQueryService>();
        if (results.Length > 0)
        {
            int resultIndex = -1;
            query.GetTreeAsync(
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<string>(),
                    Arg.Any<QueryFetchPolicy>(),
                    Arg.Any<CancellationToken>())
                .Returns(_ => Task.FromResult(
                    results[Math.Min(Interlocked.Increment(ref resultIndex), results.Length - 1)]));
        }

        IAuthService auth = Substitute.For<IAuthService>();
        auth.AuthenticatedUser.Returns(new GitHubUser { Id = 42, Login = "octo" });
        auth.GetToken(42).Returns("token");
        IAccountService account = Substitute.For<IAccountService>();
        account.GetUser().Returns(42);
        return new Harness(new RepoTreeService(query, auth, account), query);
    }

    private static CachedResult<GitHubTree> FreshTree(string sha) =>
        TreeResult(sha, CacheState.Fresh, DateTimeOffset.UtcNow.AddMinutes(5));

    private static CachedResult<GitHubTree> TreeResult(
        string sha,
        CacheState state,
        DateTimeOffset staleAfter) =>
        new(
            new GitHubTree
            {
                Sha = sha,
                Tree =
                [
                    new GitHubTreeEntry
                    {
                        Path = "src/App.cs",
                        Sha = $"{sha}-file",
                        Type = "blob"
                    }
                ]
            },
            state,
            DateTimeOffset.UtcNow.AddMinutes(-1),
            staleAfter);

    private static async Task EventuallyAsync(Func<Task> assertion)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                await assertion();
                return;
            }
            catch (Exception exception)
            {
                last = exception;
                await Task.Delay(10);
            }
        }

        throw last ?? new TimeoutException("The expected asynchronous condition was not observed.");
    }

    private sealed record Harness(
        RepoTreeService Service,
        IGitHubRepoCodeQueryService Query);
}
