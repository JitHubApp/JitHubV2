using System;
using System.Threading;
using System.Threading.Tasks;
using System.Net;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepositoryForkOperationTests
{
    [Fact]
    public async Task ResumeAsync_DefiniteApiFailureDoesNotEnterUncertainReconciliation()
    {
        RepositoryForkOperation<TestRepository> operation = new();
        int createCalls = 0;
        int reconcileCalls = 0;

        await Assert.ThrowsAsync<GitHubApiException>(() => operation.ResumeAsync(
            "source/one",
            _ =>
            {
                createCalls++;
                throw new GitHubApiException(HttpStatusCode.UnprocessableEntity, "definite rejection");
            },
            static (_, _, _) => Task.FromResult(true),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask,
            reconcileForkAsync: _ =>
            {
                reconcileCalls++;
                return Task.FromResult<TestRepository?>(null);
            }));

        RepositoryForkOperationResult<TestRepository> retry = await operation.ResumeAsync(
            "source/one",
            _ =>
            {
                createCalls++;
                return Task.FromResult<TestRepository?>(new TestRepository(2));
            },
            static (_, _, _) => Task.FromResult(true),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask,
            reconcileForkAsync: _ =>
            {
                reconcileCalls++;
                return Task.FromResult<TestRepository?>(null);
            });

        Assert.Equal(2, createCalls);
        Assert.Equal(0, reconcileCalls);
        Assert.Equal(2, retry.Repository.Id);
    }

    [Fact]
    public async Task AdoptAcceptedFork_ResumesReadinessWithoutPostingAgain()
    {
        RepositoryForkOperation<TestRepository> operation = new();
        TestRepository accepted = new(41);
        operation.AdoptAcceptedFork("source/one", accepted);
        int createCalls = 0;

        RepositoryForkOperationResult<TestRepository> result = await operation.ResumeAsync(
            "source/one",
            _ =>
            {
                createCalls++;
                return Task.FromResult<TestRepository?>(new TestRepository(99));
            },
            static (_, _, _) => Task.FromResult(true),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask);

        Assert.Equal(0, createCalls);
        Assert.Same(accepted, result.Repository);
        Assert.False(result.WasCreated);
    }

    [Fact]
    public async Task ResumeAsync_RetryReusesAcceptedForkWithoutCreatingDuplicate()
    {
        RepositoryForkOperation<TestRepository> operation = new();
        TestRepository fork = new(42);
        int creationCalls = 0;
        int readinessCalls = 0;

        RepositoryForkOperationResult<TestRepository> first = await operation.ResumeAsync(
            "source/one",
            _ =>
            {
                creationCalls++;
                return Task.FromResult<TestRepository?>(fork);
            },
            (_, _, _) => Task.FromResult(++readinessCalls > 1),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask);

        RepositoryForkOperationResult<TestRepository> retry = await operation.ResumeAsync(
            "source/one",
            _ =>
            {
                creationCalls++;
                return Task.FromResult<TestRepository?>(new TestRepository(99));
            },
            (_, _, _) => Task.FromResult(++readinessCalls > 1),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask);

        Assert.False(first.IsReady);
        Assert.True(first.WasCreated);
        Assert.True(retry.IsReady);
        Assert.False(retry.WasCreated);
        Assert.Same(fork, retry.Repository);
        Assert.Equal(1, creationCalls);
        Assert.True(operation.HasPendingFork);

        Assert.True(operation.Complete(retry));

        Assert.False(operation.HasPendingFork);
    }

    [Fact]
    public async Task ResumeAsync_NullForkProducesRecoverableErrorInsteadOfNullDereference()
    {
        RepositoryForkOperation<TestRepository> operation = new();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operation.ResumeAsync(
                "source/one",
                _ => Task.FromResult<TestRepository?>(null),
                static (_, _, _) => Task.FromResult(true),
                CancellationToken.None,
                maxAttempts: 1,
                delay: static (_, _) => Task.CompletedTask));

        Assert.Contains("returned no fork repository", error.Message, StringComparison.Ordinal);
        Assert.False(operation.HasPendingFork);
    }

    [Fact]
    public async Task ResumeAsync_PropagatesCancellationThroughCreation()
    {
        RepositoryForkOperation<TestRepository> operation = new();
        using CancellationTokenSource cancellation = new();
        CancellationToken observedToken = default;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            operation.ResumeAsync(
                "source/one",
                token =>
                {
                    observedToken = token;
                    cancellation.Cancel();
                    return Task.FromCanceled<TestRepository?>(token);
                },
                static (_, _, _) => Task.FromResult(true),
                cancellation.Token,
                maxAttempts: 1,
                delay: static (_, _) => Task.CompletedTask));

        Assert.Equal(cancellation.Token, observedToken);
    }

    [Fact]
    public async Task ResumeAsync_CanceledProbeKeepsAcceptedForkForRetry()
    {
        RepositoryForkOperation<TestRepository> operation = new();
        using CancellationTokenSource cancellation = new();
        int creationCalls = 0;
        CancellationToken observedProbeToken = default;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            operation.ResumeAsync(
                "source/one",
                _ =>
                {
                    creationCalls++;
                    return Task.FromResult<TestRepository?>(new TestRepository(42));
                },
                (_, _, token) =>
                {
                    observedProbeToken = token;
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(false);
                },
                cancellation.Token,
                maxAttempts: 1,
                delay: static (_, _) => Task.CompletedTask));

        RepositoryForkOperationResult<TestRepository> retry = await operation.ResumeAsync(
            "source/one",
            _ =>
            {
                creationCalls++;
                return Task.FromResult<TestRepository?>(new TestRepository(99));
            },
            static (_, _, _) => Task.FromResult(true),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask);

        Assert.True(observedProbeToken.CanBeCanceled);
        Assert.True(observedProbeToken.IsCancellationRequested);
        Assert.Equal(1, creationCalls);
        Assert.True(retry.IsReady);
        Assert.Equal(42, retry.Repository.Id);
    }

    [Fact]
    public async Task ResumeAsync_CancellationAfterSuccessfulCreation_PersistsAcceptedForkBeforeThrowing()
    {
        RepositoryForkOperation<TestRepository> operation = new();
        using CancellationTokenSource cancellation = new();
        TestRepository acceptedFork = new(42);
        int creationCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            operation.ResumeAsync(
                "source/one",
                _ =>
                {
                    creationCalls++;
                    cancellation.Cancel();
                    return Task.FromResult<TestRepository?>(acceptedFork);
                },
                static (_, _, _) => Task.FromResult(true),
                cancellation.Token,
                maxAttempts: 1,
                delay: static (_, _) => Task.CompletedTask));

        Assert.True(operation.HasPendingFork);

        RepositoryForkOperationResult<TestRepository> retry = await operation.ResumeAsync(
            "source/one",
            _ =>
            {
                creationCalls++;
                return Task.FromResult<TestRepository?>(new TestRepository(99));
            },
            static (_, _, _) => Task.FromResult(true),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask);

        Assert.Equal(1, creationCalls);
        Assert.False(retry.WasCreated);
        Assert.Same(acceptedFork, retry.Repository);
        Assert.True(operation.Complete(retry));
    }

    [Fact]
    public async Task ResumeAsync_UncertainCancellationReconcilesBeforeAnySecondPost()
    {
        DateTimeOffset now = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
        RepositoryForkOperation<TestRepository> operation = new(
            () => now,
            uncertainRepostDelay: TimeSpan.FromSeconds(30),
            minimumReconciliationAttempts: 2);
        int creationCalls = 0;
        int reconciliationCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ResumeAsync(
            "source/one",
            _ =>
            {
                creationCalls++;
                throw new OperationCanceledException("transport canceled after send");
            },
            static (_, _, _) => Task.FromResult(true),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask,
            reconcileForkAsync: _ => Task.FromResult<TestRepository?>(null)));

        RepositoryForkReconciliationPendingException pending =
            await Assert.ThrowsAsync<RepositoryForkReconciliationPendingException>(() => operation.ResumeAsync(
                "source/one",
                _ =>
                {
                    creationCalls++;
                    return Task.FromResult<TestRepository?>(new TestRepository(99));
                },
                static (_, _, _) => Task.FromResult(true),
                CancellationToken.None,
                maxAttempts: 1,
                delay: static (_, _) => Task.CompletedTask,
                reconcileForkAsync: _ =>
                {
                    reconciliationCalls++;
                    return Task.FromResult<TestRepository?>(null);
                }));

        Assert.Equal(1, creationCalls);
        Assert.Equal(1, reconciliationCalls);
        Assert.True(pending.RetryAvailableAt > now);

        TestRepository accepted = new(42);
        RepositoryForkOperationResult<TestRepository> reconciled = await operation.ResumeAsync(
            "source/one",
            _ =>
            {
                creationCalls++;
                return Task.FromResult<TestRepository?>(new TestRepository(100));
            },
            static (_, _, _) => Task.FromResult(true),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask,
            reconcileForkAsync: _ =>
            {
                reconciliationCalls++;
                return Task.FromResult<TestRepository?>(accepted);
            });

        Assert.Equal(1, creationCalls);
        Assert.Equal(2, reconciliationCalls);
        Assert.Same(accepted, reconciled.Repository);
        Assert.False(reconciled.WasCreated);
    }

    [Fact]
    public async Task ResumeAsync_ResetDuringInFlightProbe_RejectsStaleResultAndPreservesNewGeneration()
    {
        RepositoryForkOperation<TestRepository> operation = new();
        TaskCompletionSource probeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseProbe = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<RepositoryForkOperationResult<TestRepository>> stale = operation.ResumeAsync(
            "source/one",
            static _ => Task.FromResult<TestRepository?>(new TestRepository(1)),
            async (_, _, _) =>
            {
                probeStarted.TrySetResult();
                await releaseProbe.Task;
                return true;
            },
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask);

        await probeStarted.Task;
        operation.Reset();
        releaseProbe.TrySetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);
        Assert.False(operation.HasPendingFork);

        RepositoryForkOperationResult<TestRepository> current = await operation.ResumeAsync(
            "source/two",
            static _ => Task.FromResult<TestRepository?>(new TestRepository(2)),
            static (_, _, _) => Task.FromResult(true),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask);

        Assert.Equal(2, current.Repository.Id);
        Assert.True(operation.Complete(current));
        Assert.False(operation.HasPendingFork);
    }

    [Fact]
    public async Task ResumeAsync_ResetDuringInFlightCreation_CannotPublishAcceptedForkIntoNewGeneration()
    {
        RepositoryForkOperation<TestRepository> operation = new();
        TaskCompletionSource creationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<TestRepository?> creationResult = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<RepositoryForkOperationResult<TestRepository>> stale = operation.ResumeAsync(
            "source/one",
            _ =>
            {
                creationStarted.TrySetResult();
                return creationResult.Task;
            },
            static (_, _, _) => Task.FromResult(true),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask);

        await creationStarted.Task;
        operation.Reset();
        creationResult.TrySetResult(new TestRepository(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);
        Assert.False(operation.HasPendingFork);
    }

    [Fact]
    public async Task ResumeAsync_ResetAndQueuedReplacement_PublishesOnlyReplacementFork()
    {
        RepositoryForkOperation<TestRepository> operation = new();
        TaskCompletionSource creationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<TestRepository?> creationResult = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<RepositoryForkOperationResult<TestRepository>> stale = operation.ResumeAsync(
            "source/one",
            _ =>
            {
                creationStarted.TrySetResult();
                return creationResult.Task;
            },
            static (_, _, _) => Task.FromResult(true),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask);

        await creationStarted.Task;
        operation.Reset();
        Task<RepositoryForkOperationResult<TestRepository>> replacement = operation.ResumeAsync(
            "source/two",
            static _ => Task.FromResult<TestRepository?>(new TestRepository(2)),
            static (_, _, _) => Task.FromResult(true),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask);

        creationResult.TrySetResult(new TestRepository(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);
        RepositoryForkOperationResult<TestRepository> current = await replacement;
        Assert.Equal(2, current.Repository.Id);
        Assert.True(operation.HasPendingFork);
        Assert.True(operation.Complete(current));
        Assert.False(operation.HasPendingFork);
    }

    [Fact]
    public async Task Complete_StaleResultCannotClearPendingForkOwnedByNewGeneration()
    {
        RepositoryForkOperation<TestRepository> operation = new();
        RepositoryForkOperationResult<TestRepository> old = await operation.ResumeAsync(
            "source/one",
            static _ => Task.FromResult<TestRepository?>(new TestRepository(1)),
            static (_, _, _) => Task.FromResult(false),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask);
        operation.Reset();
        RepositoryForkOperationResult<TestRepository> current = await operation.ResumeAsync(
            "source/two",
            static _ => Task.FromResult<TestRepository?>(new TestRepository(2)),
            static (_, _, _) => Task.FromResult(false),
            CancellationToken.None,
            maxAttempts: 1,
            delay: static (_, _) => Task.CompletedTask);

        Assert.False(operation.Complete(old));
        Assert.True(operation.HasPendingFork);
        Assert.True(operation.Complete(current));
        Assert.False(operation.HasPendingFork);
    }

    private sealed record TestRepository(long Id);
}
