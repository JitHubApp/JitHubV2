using System;
using System.Threading.Tasks;
using JitHub.Markdown.Lifetime.Internal;
using Xunit;

namespace JitHub.Markdown.Tests.Lifetime.Internal;

/// <summary>
/// Covers the renderer disposal commit: a renderer stays tracked until its
/// disposal completes and its retired resources drain.
/// </summary>
public sealed class MarkdownRendererDisposalCommitTests
{
    [Fact]
    public async Task RendererTrackingRemainsActiveUntilSnapshotRetirementDrains()
    {
        var retirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int activeRenderers = 1;
        int providerShutdowns = 0;
        bool rendererTracked = true;

        Task completion = MarkdownRendererDisposalCommit.Execute(
            detachAndDispose: static () => { },
            disposalCompletedSuccessfully: static () => true,
            retirementBegan: static () => { },
            drainRetiredResources: () => retirement.Task,
            releaseTracking: () =>
            {
                rendererTracked = false;
                activeRenderers--;
                if (activeRenderers == 0)
                    providerShutdowns++;
                return Task.CompletedTask;
            });

        Assert.True(rendererTracked);
        Assert.Equal(1, activeRenderers);
        Assert.Equal(0, providerShutdowns);
        Assert.False(completion.IsCompleted);

        retirement.SetResult(true);
        await completion;

        Assert.False(rendererTracked);
        Assert.Equal(0, activeRenderers);
        Assert.Equal(1, providerShutdowns);
    }

    [Fact]
    public void RendererWithoutDisposeCompletionDoesNotStartRetirementOrReleaseTracking()
    {
        int activeRenderers = 1;
        bool retirementStarted = false;
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = MarkdownRendererDisposalCommit.Execute(
                detachAndDispose: static () => { },
                disposalCompletedSuccessfully: static () => false,
                retirementBegan: () => retirementStarted = true,
                drainRetiredResources: () =>
                {
                    retirementStarted = true;
                    return Task.CompletedTask;
                },
                releaseTracking: () =>
                {
                    activeRenderers--;
                    return Task.CompletedTask;
                });
        });

        Assert.False(retirementStarted);
        Assert.Equal(1, activeRenderers);
    }

    [Fact]
    public async Task FaultedSnapshotRetirementKeepsRendererTrackingUnresolved()
    {
        var retirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidOperationException("Simulated snapshot retirement failure.");
        int activeRenderers = 1;
        bool rendererTracked = true;
        Task completion = MarkdownRendererDisposalCommit.Execute(
            detachAndDispose: static () => { },
            disposalCompletedSuccessfully: static () => true,
            retirementBegan: static () => { },
            drainRetiredResources: () => retirement.Task,
            releaseTracking: () =>
            {
                rendererTracked = false;
                activeRenderers--;
                return Task.CompletedTask;
            });

        retirement.SetException(failure);
        InvalidOperationException observed = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await completion);

        Assert.Same(failure, observed);
        Assert.True(rendererTracked);
        Assert.Equal(1, activeRenderers);
    }

    [Fact]
    public void IncompleteSnapshotRetirementKeepsRendererTrackingUnresolved()
    {
        var retirement = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int activeRenderers = 1;
        bool rendererTracked = true;
        Task completion = MarkdownRendererDisposalCommit.Execute(
            detachAndDispose: static () => { },
            disposalCompletedSuccessfully: static () => true,
            retirementBegan: static () => { },
            drainRetiredResources: () => retirement.Task,
            releaseTracking: () =>
            {
                rendererTracked = false;
                activeRenderers--;
                return Task.CompletedTask;
            });

        Assert.False(retirement.Task.IsCompleted);
        Assert.False(completion.IsCompleted);
        Assert.True(rendererTracked);
        Assert.Equal(1, activeRenderers);
    }

    [Fact]
    public void MarkdownRendererDisposeFailureRetainsTrackingUntilSuccessfulRetry()
    {
        int disposeAttempts = 0;
        int activeRendererCount = 1;
        bool rendererTracked = true;
        bool disposalCompleted = false;
        Action detachAndDispose = () =>
        {
            if (++disposeAttempts == 1)
                throw new InvalidOperationException("Simulated renderer disposal failure.");
            disposalCompleted = true;
        };
        Action releaseTracking = () =>
        {
            rendererTracked = false;
            activeRendererCount--;
        };

        Assert.Throws<InvalidOperationException>(() =>
            MarkdownRendererDisposalCommit.Execute(detachAndDispose, () => disposalCompleted, releaseTracking));
        Assert.True(rendererTracked);
        Assert.Equal(1, activeRendererCount);

        MarkdownRendererDisposalCommit.Execute(detachAndDispose, () => disposalCompleted, releaseTracking);
        Assert.False(rendererTracked);
        Assert.Equal(0, activeRendererCount);
    }

    [Fact]
    public void MarkdownRendererNoOpRetryCannotConfirmIncompleteDisposal()
    {
        int activeRendererCount = 1;
        bool disposedLatch = false;
        bool disposalCompleted = false;
        Action detachAndDispose = () =>
        {
            if (disposedLatch)
                return;

            disposedLatch = true;
            throw new InvalidOperationException("Teardown failed after latching Dispose.");
        };
        Action releaseTracking = () => activeRendererCount--;

        Assert.Throws<InvalidOperationException>(() =>
            MarkdownRendererDisposalCommit.Execute(detachAndDispose, () => disposalCompleted, releaseTracking));
        Assert.Throws<InvalidOperationException>(() =>
            MarkdownRendererDisposalCommit.Execute(detachAndDispose, () => disposalCompleted, releaseTracking));
        Assert.Equal(1, activeRendererCount);
    }
}
