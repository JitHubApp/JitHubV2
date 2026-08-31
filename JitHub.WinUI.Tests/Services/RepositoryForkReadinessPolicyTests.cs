using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepositoryForkReadinessPolicyTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    [InlineData("main", 0, false)]
    [InlineData("main", 1, true)]
    public void AccessibleRepositoryReadiness_AllowsValidEmptyRepositories(
        string? defaultBranch,
        int? branchCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            RepositoryForkReadinessPolicy.IsAccessibleRepositoryReady(defaultBranch, branchCount));
    }

    [Fact]
    public async Task WaitForReadyAsync_StopsAsSoonAsForkIsReady()
    {
        int probes = 0;
        int delays = 0;

        bool ready = await RepositoryForkReadinessPolicy.WaitForReadyAsync(
            (_, _) => Task.FromResult(++probes == 3),
            CancellationToken.None,
            delay: (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });

        Assert.True(ready);
        Assert.Equal(3, probes);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task WaitForReadyAsync_IsBoundedWhenForkNeverBecomesReady()
    {
        int probes = 0;

        bool ready = await RepositoryForkReadinessPolicy.WaitForReadyAsync(
            (_, _) =>
            {
                probes++;
                return Task.FromResult(false);
            },
            CancellationToken.None,
            maxAttempts: 4,
            delay: static (_, _) => Task.CompletedTask);

        Assert.False(ready);
        Assert.Equal(4, probes);
    }

    [Fact]
    public async Task WaitForReadyAsync_HonorsRateLimitDelayAndCancellation()
    {
        TimeSpan observedDelay = TimeSpan.Zero;
        using CancellationTokenSource cancellation = new();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            RepositoryForkReadinessPolicy.WaitForReadyAsync(
                (_, _) => throw new GitHubRateLimitException(HttpStatusCode.Forbidden, "wait", TimeSpan.FromSeconds(4)),
                cancellation.Token,
                maxAttempts: 3,
                delay: (duration, _) =>
                {
                    observedDelay = duration;
                    cancellation.Cancel();
                    return Task.FromCanceled(cancellation.Token);
                }));

        Assert.Equal(TimeSpan.FromSeconds(4), observedDelay);
    }

    [Fact]
    public async Task WaitForReadyAsync_TreatsTemporaryNotFoundAsForkPreparation()
    {
        int probes = 0;
        int delays = 0;

        bool ready = await RepositoryForkReadinessPolicy.WaitForReadyAsync(
            (_, _) =>
            {
                probes++;
                if (probes < 3)
                {
                    throw new GitHubApiException(HttpStatusCode.NotFound, "Fork is still being prepared.");
                }

                return Task.FromResult(true);
            },
            CancellationToken.None,
            maxAttempts: 4,
            delay: (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            });

        Assert.True(ready);
        Assert.Equal(3, probes);
        Assert.Equal(2, delays);
    }

    [Fact]
    public async Task WaitForReadyAsync_HonorsRetryAfterWithoutShorteningServerDelay()
    {
        TimeSpan observedDelay = TimeSpan.Zero;
        int probes = 0;

        bool ready = await RepositoryForkReadinessPolicy.WaitForReadyAsync(
            (_, _) =>
            {
                probes++;
                if (probes == 1)
                {
                    throw new GitHubRateLimitException(
                        HttpStatusCode.TooManyRequests,
                        "wait",
                        TimeSpan.FromSeconds(30));
                }

                return Task.FromResult(true);
            },
            CancellationToken.None,
            maxAttempts: 2,
            delay: (duration, _) =>
            {
                observedDelay = duration;
                return Task.CompletedTask;
            });

        Assert.True(ready);
        Assert.Equal(TimeSpan.FromSeconds(30), observedDelay);
    }

    [Fact]
    public async Task WaitForReadyAsync_DoesNotShortenRetryAfterThatExceedsOverallBudget()
    {
        int delays = 0;

        bool ready = await RepositoryForkReadinessPolicy.WaitForReadyAsync(
            static (_, _) => throw new GitHubRateLimitException(
                HttpStatusCode.TooManyRequests,
                "wait",
                TimeSpan.FromMinutes(2)),
            CancellationToken.None,
            maxAttempts: 2,
            delay: (_, _) =>
            {
                delays++;
                return Task.CompletedTask;
            },
            maxTotalDelay: TimeSpan.FromSeconds(45));

        Assert.False(ready);
        Assert.Equal(0, delays);
    }

    [Fact]
    public async Task WaitForReadyResultAsync_WallClockBudgetIncludesProbeDuration()
    {
        TaskCompletionSource probeCanceled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RepositoryForkReadinessResult result = await RepositoryForkReadinessPolicy.WaitForReadyResultAsync(
            async (_, token) =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    return true;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    probeCanceled.TrySetResult();
                    throw;
                }
            },
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask,
            maxTotalDelay: TimeSpan.FromMilliseconds(40));

        Assert.False(result.IsReady);
        Assert.Equal(RepositoryForkReadinessFailure.DeadlineExceeded, result.Failure);
        await probeCanceled.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task WaitForReadyResultAsync_ReturnsExactServerRetryUnlockWhenDelayExceedsBudget()
    {
        DateTimeOffset before = DateTimeOffset.UtcNow;
        RepositoryForkReadinessResult result = await RepositoryForkReadinessPolicy.WaitForReadyResultAsync(
            static (_, _) => throw new GitHubRateLimitException(
                HttpStatusCode.TooManyRequests,
                "wait",
                TimeSpan.FromMinutes(2)),
            CancellationToken.None,
            maxAttempts: 2,
            delay: static (_, _) => Task.CompletedTask,
            maxTotalDelay: TimeSpan.FromSeconds(1));

        Assert.False(result.IsReady);
        Assert.Equal(RepositoryForkReadinessFailure.RateLimited, result.Failure);
        Assert.True(result.RetryAvailableAt >= before.AddMinutes(2));
    }
}
