using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Models.CodeViewer;
using JitHub.Models.GitHub;
using JitHub.Services;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.ViewModels.CodeViewer;
using NSubstitute;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class RepositoryRoutePrefetchCoordinatorTests
{
    [Fact]
    public async Task RapidCodeRoutes_CancelObsoletePreparationAndKeepLatestOwned()
    {
        TaskCompletionSource releaseLatest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Dictionary<string, CancellationToken> tokens = new(StringComparer.Ordinal);
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
                tokens[repository] = token;
                if (repository == "second")
                {
                    await releaseLatest.Task.WaitAsync(token);
                }
                else
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }

                return CreateResult(repository);
            });
        using ApplicationTaskCoordinator tasks = new();
        RepositoryRoutePrefetchCoordinator prefetch = CreateCoordinator(trees, tasks);

        Task first = prefetch.StartCodeAsync("token", "42", "octo", "first", "main");
        Task second = prefetch.StartCodeAsync("token", "42", "octo", "second", "main");

        await first.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(tokens["first"].IsCancellationRequested);
        Assert.False(tokens["second"].IsCancellationRequested);
        Assert.Equal(1, tasks.ActiveTaskCount);

        releaseLatest.SetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, tasks.ActiveTaskCount);
    }

    [Fact]
    public async Task AccountRemoval_CancelsAndDrainsCommitRoutePrefetch()
    {
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        IGitHubCommitQueryService commits = Substitute.For<IGitHubCommitQueryService>();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken workToken = default;
        commits.GetCommitsAsync(
                "token",
                "42",
                "octo",
                "app",
                Arg.Any<CommitListQueryOptions>(),
                100,
                1,
                Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                workToken = call.ArgAt<CancellationToken>(7);
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, workToken);
                return new CachedResult<GitHubCommit[]>([], CacheState.Miss, null, null);
            });
        using ApplicationTaskCoordinator tasks = new();
        RepoCodeNavigationPreparationCache code = CreateCodeCache(trees);
        RepositoryRoutePrefetchCoordinator prefetch = new(code, commits, tasks);

        Task route = prefetch.StartCommitsAsync("token", "42", "octo", "app", "main");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await tasks.CancelAccountAsync("42").WaitAsync(TimeSpan.FromSeconds(2));
        await route.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(workToken.IsCancellationRequested);
        Assert.Equal(0, tasks.ActiveTaskCount);
    }

    [Fact]
    public async Task Shutdown_CancelsAndDrainsCurrentCodeRoutePrefetch()
    {
        CancellationToken workToken = default;
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        IRepoTreeService trees = Substitute.For<IRepoTreeService>();
        trees.LoadTreeAsync(
                "octo",
                "app",
                "main",
                Arg.Any<CancellationToken>(),
                QueryFetchPolicy.StaleFirst)
            .Returns(async call =>
            {
                workToken = call.ArgAt<CancellationToken>(3);
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, workToken);
                return CreateResult("app");
            });
        using ApplicationTaskCoordinator tasks = new();
        RepositoryRoutePrefetchCoordinator prefetch = CreateCoordinator(trees, tasks);

        Task route = prefetch.StartCodeAsync("token", "42", "octo", "app", "main");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        ApplicationTaskShutdownResult result = await tasks
            .ShutdownAsync(TimeSpan.FromSeconds(2))
            .WaitAsync(TimeSpan.FromSeconds(3));
        await route.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(result.Completed);
        Assert.Equal(0, result.PendingTaskCount);
        Assert.True(workToken.IsCancellationRequested);
    }

    private static RepositoryRoutePrefetchCoordinator CreateCoordinator(
        IRepoTreeService trees,
        IApplicationTaskCoordinator tasks) =>
        new(
            CreateCodeCache(trees),
            Substitute.For<IGitHubCommitQueryService>(),
            tasks);

    private static RepoCodeNavigationPreparationCache CreateCodeCache(IRepoTreeService trees)
    {
        IAccountService account = Substitute.For<IAccountService>();
        account.GetUser().Returns(42);
        return new RepoCodeNavigationPreparationCache(trees, new LanguageIdResolver(), account);
    }

    private static RepoCodeLoadResult<RepoTree> CreateResult(string sha) =>
        new(
            new RepoTree
            {
                Sha = sha,
                Root = new RepoTreeNode
                {
                    Name = string.Empty,
                    Path = string.Empty,
                    IsDirectory = true,
                    Children = []
                }
            },
            CacheState.Fresh,
            FetchedAt: DateTimeOffset.UtcNow,
            StaleAfter: DateTimeOffset.UtcNow.AddMinutes(30));
}
