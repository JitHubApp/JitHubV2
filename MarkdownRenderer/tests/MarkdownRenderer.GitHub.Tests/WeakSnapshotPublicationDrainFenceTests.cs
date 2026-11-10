using System.Runtime.CompilerServices;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Internal.LazyLayout;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Threading;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class WeakSnapshotPublicationDrainFenceTests
{
    [Fact]
    public void PublicationCannotBeClaimedUntilItsWorkerHasDrainedAndIsOneShot()
    {
        var fence = new WeakSnapshotPublicationDrainFence<object>();
        object snapshot = new();
        int deliveries = 0;
        object? deliveredSnapshot = null;
        fence.Reset(12);

        Assert.True(fence.TryStage(12, new WeakSnapshotPublication<object>(snapshot, published =>
        {
            deliveries++;
            deliveredSnapshot = published;
        })));
        Assert.True(fence.HasPendingPublication);
        Assert.False(fence.TryPublishAfterWorkDrains(12));
        Assert.Equal(0, deliveries);

        fence.MarkWorkDrained(12);
        Assert.True(fence.TryPublishAfterWorkDrains(12));
        Assert.Same(snapshot, deliveredSnapshot);
        Assert.Equal(1, deliveries);
        Assert.False(fence.HasPendingPublication);
        Assert.False(fence.TryPublishAfterWorkDrains(12));
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public void DrainMayArriveBeforeStageWithoutLosingTheOneShotHandoff()
    {
        var fence = new WeakSnapshotPublicationDrainFence<object>();
        object snapshot = new();
        int deliveries = 0;
        fence.Reset(4);

        fence.MarkWorkDrained(4);
        Assert.True(fence.TryStage(4, new WeakSnapshotPublication<object>(snapshot, _ => deliveries++)));
        Assert.True(fence.TryPublishAfterWorkDrains(4));
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public void ResetAndStaleGenerationsDiscardCallbacksWithoutPublishingThem()
    {
        var fence = new WeakSnapshotPublicationDrainFence<object>();
        object staleSnapshot = new();
        object currentSnapshot = new();
        int staleDeliveries = 0;
        int currentDeliveries = 0;
        fence.Reset(1);

        Assert.True(fence.TryStage(1, new WeakSnapshotPublication<object>(
            staleSnapshot, _ => staleDeliveries++)));
        fence.Reset(2);
        Assert.False(fence.HasPendingPublication);
        fence.MarkWorkDrained(1);
        Assert.False(fence.TryPublishAfterWorkDrains(1));
        Assert.False(fence.TryStage(1, new WeakSnapshotPublication<object>(
            staleSnapshot, _ => staleDeliveries++)));

        Assert.True(fence.TryStage(2, new WeakSnapshotPublication<object>(
            currentSnapshot, _ => currentDeliveries++)));
        fence.MarkWorkDrained(2);
        Assert.True(fence.TryPublishAfterWorkDrains(2));
        Assert.Equal(0, staleDeliveries);
        Assert.Equal(1, currentDeliveries);
    }

    [Fact]
    public void StagedLazyCompletionAndItsQueuedDispatchRetainNeitherTheSnapshotNorTheSceneOwner()
    {
        var scheduler = new ManualUiScheduler();
        var handoff = new WeakSnapshotPublicationDrainFence<LayoutSnapshot>();
        handoff.Reset(RealizationGeneration);

        LazyWorkProbe probe = RunLazyWorkOnUnreferencedSnapshot(scheduler, handoff);

        // The worker has finished: its completion is staged in the handoff and
        // its low-priority dispatch is still queued on the UI thread.
        Assert.True(probe.Worker.IsCompletedSuccessfully);
        Assert.True(handoff.HasPendingPublication);
        Assert.Equal([MarkdownUiPriority.Low], scheduler.Posted);
        Assert.Equal(1, scheduler.PendingCount);
        RetentionCollector.CollectUntilDead(probe.Snapshot, probe.SceneOwner);
        Assert.False(probe.Snapshot.IsAlive);
        Assert.False(probe.SceneOwner.IsAlive);

        // The realization marks the work drained when the worker task ends.
        // The queued dispatch reaches the realization only weakly, so with no
        // realization it neither claims nor publishes the staged completion.
        handoff.MarkWorkDrained(RealizationGeneration);
        Assert.Equal(1, scheduler.RunPending());
        Assert.True(handoff.HasPendingPublication);

        // A recovery-clock claim still delivers it once, harmlessly.
        Assert.True(handoff.TryPublishAfterWorkDrains(RealizationGeneration));
        Assert.False(handoff.HasPendingPublication);
        GC.KeepAlive(probe.Worker);
    }

    private const long RealizationGeneration = 4;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LazyWorkProbe RunLazyWorkOnUnreferencedSnapshot(
        ManualUiScheduler scheduler,
        WeakSnapshotPublicationDrainFence<LayoutSnapshot> handoff)
    {
        LayoutSnapshot snapshot = CreateSnapshot();
        object sceneOwner = new();
        var band = new LazyLayoutBand(0, 160);
        // The worker can reach its realization only through this weak
        // reference; no realization is needed to observe what else it keeps.
        var receiver = new WeakReference<LazyLayoutRealization>(null!);

        Task worker = LazyLayoutWork.RunAsync(
            snapshot, band, band, anchorOffset: 10, pipelineGeneration: 1, RealizationGeneration,
            CancellationToken.None, scheduler, session: null, sceneOwner, receiver, handoff);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(10)));

        return new LazyWorkProbe(worker, new WeakReference(snapshot), new WeakReference(sceneOwner));
    }

    private static LayoutSnapshot CreateSnapshot()
    {
        float top = 0;
        var blocks = new ImageRelayoutFixture.MeasuredBox[4];
        for (int index = 0; index < blocks.Length; index++)
        {
            var block = new ImageRelayoutFixture.MeasuredBox(40) { BlockIndex = index };
            block.Measure(100);
            block.Arrange(0, top, 100);
            top += 40;
            blocks[index] = block;
        }
        return new LayoutSnapshot(blocks, new MarkdownSourceMap(string.Empty), width: 100, height: top);
    }

    private sealed record LazyWorkProbe(Task Worker, WeakReference Snapshot, WeakReference SceneOwner);
}
