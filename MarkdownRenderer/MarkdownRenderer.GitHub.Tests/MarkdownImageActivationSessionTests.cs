using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownImageActivationSessionTests
{
    [Fact]
    public async Task SessionDisposalWaitsForActiveImageStarterAndDrainsItsWork()
    {
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var api = (IMarkdownPerformanceSessionInternal)session;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        Assert.True(api.TryScheduleImageActivation(
            new MarkdownSvgDemandTicket(MarkdownSvgRenderPriority.Visible),
            startAsyncWork: () =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            },
            onNotStarted: static () => { },
            CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        Task shutdown = session.DisposeAsync().AsTask();
        Assert.False(shutdown.IsCompleted);

        try
        {
            release.Set();
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SessionForwardsLiveDemandUntilQueuedSourceActivationStarts()
    {
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var api = (IMarkdownPerformanceSessionInternal)session;
        int workerCount = BoundedMarkdownImageActivationScheduler.ResolveWorkerCount(
            Environment.ProcessorCount, IntPtr.Size);
        using var entered = new CountdownEvent(workerCount);
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queuedDemand = new MarkdownSvgDemandTicket(MarkdownSvgRenderPriority.Overscan);
        int rejected = 0;
        try
        {
            for (int index = 0; index < workerCount; index++)
            {
                Assert.True(api.TryScheduleImageActivation(
                    new MarkdownSvgDemandTicket(MarkdownSvgRenderPriority.Visible),
                    () =>
                    {
                        entered.Signal();
                        release.Wait(TimeSpan.FromSeconds(5));
                    },
                    static () => { }, CancellationToken.None));
            }
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(api.TryScheduleImageActivation(
                queuedDemand,
                () => started.TrySetResult(((IMarkdownImageActivationDemand)queuedDemand).IsVisible),
                () => Interlocked.Increment(ref rejected), CancellationToken.None));

            // Both worker lanes are held: the real ticket must still own the
            // queued activation's promotion registration, not a copied bool.
            Assert.Equal(1, queuedDemand.PendingPromotionCount);
            Assert.True(queuedDemand.PromoteVisible());
            release.Set();
            Assert.True(await started.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Volatile.Read(ref rejected));
            Assert.Equal(0, queuedDemand.PendingPromotionCount);
        }
        finally
        {
            release.Set();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

}
