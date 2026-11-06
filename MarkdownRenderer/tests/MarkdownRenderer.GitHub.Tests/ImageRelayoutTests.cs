using System.Runtime.CompilerServices;
using MarkdownRenderer.Controls.Threading;
using MarkdownRenderer.Layout;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Image relayout coalesces completed images into one low-priority UI dispatch
/// guarded by the recovery clock, relayouts the published snapshot off the UI
/// thread, and publishes the result only while that exact snapshot, generation
/// and uncanceled operation are current. Work it cannot do yet stays pending.
/// </summary>
public sealed class ImageRelayoutTests
{
    [Fact]
    public void CompletionsCoalesceIntoOneLowPriorityDispatchThatRelayoutsThePublishedSnapshot()
    {
        using var fixture = new ImageRelayoutFixture();
        int[] measuresBefore = fixture.Blocks.Select(static block => block.Measures).ToArray();

        fixture.Relayout.Queue(0);
        fixture.Relayout.Queue(2);

        Assert.Equal([MarkdownUiPriority.Low], fixture.Scheduler.Posted);
        Assert.True(fixture.Clock.IsRunning);
        Assert.True(fixture.Relayout.HasPendingWork);

        fixture.RunDispatchAndWorker();

        (LayoutSnapshot published, (int, double)? anchor) = Assert.Single(fixture.Host.Published);
        Assert.Same(fixture.Snapshot, published);
        Assert.Null(anchor);
        Assert.Equal([MarkdownUiPriority.Low, MarkdownUiPriority.Normal], fixture.Scheduler.Posted);
        Assert.True(fixture.Blocks[0].Measures > measuresBefore[0]);
        Assert.True(fixture.Blocks[2].Measures > measuresBefore[2]);
        Assert.False(fixture.Relayout.HasPendingWork);
        Assert.False(fixture.Clock.IsRunning);
        Assert.Equal(0, fixture.Host.FullRebuilds);
    }

    [Fact]
    public void RelayoutRestoresTheScrollAnchorItCapturedOnTheUiThread()
    {
        using var fixture = new ImageRelayoutFixture();
        fixture.Host.VerticalScrollOffset = 50;

        fixture.Relayout.Queue(1);
        fixture.RunDispatchAndWorker();

        (_, (int BlockIndex, double OffsetFromTop)? anchor) = Assert.Single(fixture.Host.Published);
        Assert.True(fixture.Snapshot.TryCaptureScrollAnchor(50, out var expected));
        Assert.Equal((expected.BlockIndex!.Value, expected.OffsetFromTop), anchor);
    }

    [Fact]
    public void MissingRecoveryClockRequestsAFullRebuildAndKeepsThePendingRoots()
    {
        using var fixture = new ImageRelayoutFixture(clockHasScheduler: false);

        fixture.Relayout.Queue(2);

        Assert.Equal(1, fixture.Host.FullRebuilds);
        Assert.Empty(fixture.Scheduler.Posted);
        Assert.True(fixture.Relayout.HasPendingWork);
    }

    [Fact]
    public void RejectedDispatchKeepsThePendingRootsForTheRecoveryTick()
    {
        using var fixture = new ImageRelayoutFixture();
        fixture.Scheduler.RejectPosts = true;

        fixture.Relayout.Queue(1);

        Assert.Equal([MarkdownUiPriority.Low], fixture.Scheduler.Posted);
        Assert.True(fixture.Clock.IsRunning);
        Assert.True(fixture.Relayout.HasPendingWork);

        fixture.Scheduler.RejectPosts = false;
        fixture.Relayout.ProcessQueued();
        fixture.Scheduler.WaitForPending();
        fixture.Scheduler.RunPending();

        Assert.Single(fixture.Host.Published);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void RebuildOrSnapshotGestureDefersTheRelayoutAndKeepsItsPendingRoots(bool rebuild, bool gesture)
    {
        using var fixture = new ImageRelayoutFixture();
        fixture.Host.Defers = rebuild;
        fixture.Host.HasSnapshotInteraction = gesture;

        fixture.Relayout.Queue(3);
        Assert.Equal(1, fixture.Scheduler.RunPending());

        Assert.Single(fixture.Scheduler.Posted);
        Assert.Equal(0, fixture.Host.StartedRelayouts);
        Assert.True(fixture.Relayout.HasPendingWork);
        Assert.True(fixture.Clock.IsRunning);

        fixture.Host.Defers = false;
        fixture.Host.HasSnapshotInteraction = false;
        fixture.Relayout.ProcessQueued();
        fixture.Scheduler.WaitForPending();
        fixture.Scheduler.RunPending();

        Assert.Equal(1, fixture.Host.StartedRelayouts);
        Assert.Single(fixture.Host.Published);
    }

    [Fact]
    public void DispatchQueuedForAReplacedSnapshotIsIgnored()
    {
        using var fixture = new ImageRelayoutFixture();
        // Deferring keeps the dispatch queued, so a stale callback that got
        // past the ticket fence would show up as a second deferral check.
        fixture.Host.Defers = true;
        fixture.Relayout.Queue(0);
        fixture.Relayout.ClearForSnapshotReplacement();
        fixture.Relayout.Queue(1);

        Assert.Equal(2, fixture.Scheduler.RunPending());

        Assert.Equal(1, fixture.Host.DefersCalls);
    }

    [Fact]
    public void QueuedDispatchDoesNotKeepTheRelayoutAlive()
    {
        using var fixture = new ImageRelayoutFixture();
        WeakReference relayout = QueueOnUnreferencedRelayout(fixture);

        for (int attempt = 0; attempt < 10 && relayout.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(relayout.IsAlive);
        Assert.Equal(1, fixture.Scheduler.RunPending());
        Assert.Equal(0, fixture.Host.DefersCalls);
    }

    [Fact]
    public void CompletionForASupersededGenerationIsNotPublished()
    {
        using var fixture = new ImageRelayoutFixture();
        fixture.Relayout.Queue(0);
        fixture.Scheduler.RunPending();
        fixture.Scheduler.WaitForPending();

        fixture.Published.Generation++;
        fixture.Scheduler.RunPending();

        Assert.Empty(fixture.Host.Published);
        Assert.Equal(0, fixture.Host.FullRebuilds);
        Assert.False(fixture.Relayout.HasPendingWork);
    }

    [Fact]
    public void CanceledRelayoutIsNotPublishedAndRequeuesTheSubscribedImages()
    {
        using var fixture = new ImageRelayoutFixture();
        fixture.SubscribeImage(blockIndex: 2);
        fixture.Relayout.Queue(0);

        // The held lease keeps the worker at the geometry gate, so it observes
        // the cancellation before it mutates any geometry.
        Assert.True(fixture.Snapshot.TryBeginInteraction());
        try
        {
            fixture.Scheduler.RunPending();
            fixture.Relayout.CancelActive();
        }
        finally
        {
            fixture.Snapshot.EndInteraction();
        }
        fixture.Scheduler.WaitForPending();
        fixture.Scheduler.RunPending();

        Assert.Empty(fixture.Host.Published);
        Assert.Equal(0, fixture.Host.FullRebuilds);
        Assert.False(fixture.Snapshot.GeometryRecoveryRequired);
        Assert.Equal(
            [MarkdownUiPriority.Low, MarkdownUiPriority.Normal, MarkdownUiPriority.Low],
            fixture.Scheduler.Posted);
        Assert.True(fixture.Relayout.HasPendingWork);
    }

    [Theory]
    [InlineData(100, 0)]
    [InlineData(120, 1)]
    public void PointerInputDuringTheRelayoutRetriesTheRootInsteadOfPublishing(float layoutWidth, int fullRebuilds)
    {
        using var fixture = new ImageRelayoutFixture();
        fixture.Relayout.Queue(3);
        fixture.Host.LayoutWidth = layoutWidth;

        // Holding the interaction lease keeps the worker at the geometry gate
        // until the input below has advanced the snapshot's pointer epoch.
        Assert.True(fixture.Snapshot.TryBeginInteraction());
        try
        {
            fixture.Scheduler.RunPending();
            fixture.Snapshot.NotifyPointerInput();
        }
        finally
        {
            fixture.Snapshot.EndInteraction();
        }
        fixture.Scheduler.WaitForPending();
        fixture.Scheduler.RunPending();

        Assert.Empty(fixture.Host.Published);
        Assert.Equal(fullRebuilds, fixture.Host.FullRebuilds);
        Assert.Equal(
            [MarkdownUiPriority.Low, MarkdownUiPriority.Normal, MarkdownUiPriority.Low],
            fixture.Scheduler.Posted);
        Assert.True(fixture.Relayout.HasPendingWork);
    }

    [Fact]
    public void RetiredRelayoutDropsItsWorkAndNeverPublishes()
    {
        using var fixture = new ImageRelayoutFixture();
        fixture.Relayout.Queue(0);
        fixture.Scheduler.RunPending();
        fixture.Relayout.Queue(1);

        fixture.Relayout.Retire();

        Assert.False(fixture.Relayout.HasPendingWork);
        Assert.Contains(true, fixture.Host.ClockStops);
        Thread.Sleep(50);
        fixture.Scheduler.RunPending();
        Assert.Empty(fixture.Host.Published);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference QueueOnUnreferencedRelayout(ImageRelayoutFixture fixture)
    {
        var relayout = fixture.CreateRelayout();
        relayout.Queue(0);
        return new WeakReference(relayout);
    }
}
