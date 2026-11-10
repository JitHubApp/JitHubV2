using System.Runtime.CompilerServices;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Internal.Images;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.Pipeline.Commit;
using MarkdownRenderer.Controls.Internal.Recovery;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Work that image completion, relayout and the recovery clock leave queued
/// on the UI thread, or held by a timer, reaches its owner only weakly. While
/// such a callback is still held, its owner, the owner's host (the control),
/// the snapshot it worked on, the worker's dirty indices and the completion's
/// sender and event data can all be collected, and the callback that
/// outlives them does nothing.
/// </summary>
public sealed class ImageRelayoutPublicationRetentionTests
{
    [Fact]
    public void QueuedOffThreadCompletionRetainsNeitherItsOwnerHostSnapshotSenderNorEventData()
    {
        using var fixture = new ImageRelayoutFixture();
        var effects = new CompletionEffects();
        var resources = new MarkdownRenderResources();
        fixture.Scheduler.OnUiThread = false;

        CompletionProbe probe = QueueOffThreadCompletion(fixture, resources, effects);

        Assert.Equal(1, fixture.Scheduler.PendingCount);
        RetentionCollector.CollectUntilDead(
            probe.Completion, probe.Host, probe.Snapshot, probe.Image, probe.EventData);
        Assert.False(probe.Completion.IsAlive);
        Assert.False(probe.Host.IsAlive);
        Assert.False(probe.Snapshot.IsAlive);
        Assert.False(probe.Image.IsAlive);
        Assert.False(probe.EventData.IsAlive);
        Assert.Equal(1, fixture.Scheduler.PendingCount);

        fixture.Scheduler.OnUiThread = true;
        Assert.Equal(1, fixture.Scheduler.RunPending());
        Assert.Equal(0, effects.Total);
        Assert.False(fixture.Relayout.HasPendingWork);
    }

    [Fact]
    public void QueuedCompletionWhoseImageWasCollectedIsDroppedRatherThanRebuilt()
    {
        using var fixture = new ImageRelayoutFixture();
        var effects = new CompletionEffects();
        var host = new CompletionHost(effects);
        var completion = new ImageLoadCompletion(
            host, fixture.Lifetime, fixture.Published, fixture.LayoutCommit, fixture.Subscriptions,
            fixture.Relayout, fixture.Scheduler);
        var resources = new MarkdownRenderResources();
        fixture.Scheduler.OnUiThread = false;

        WeakReference image = CompleteOffThreadWithUnreferencedImage(completion, resources);

        RetentionCollector.CollectUntilDead(image);
        Assert.False(image.IsAlive);
        fixture.Scheduler.OnUiThread = true;
        Assert.Equal(1, fixture.Scheduler.RunPending());

        // A collected sender is not mistaken for a sender that is not an image,
        // which would request a full rebuild.
        Assert.Equal(0, effects.Total);
        Assert.False(fixture.Relayout.HasPendingWork);
        GC.KeepAlive(completion);
    }

    [Fact]
    public void QueuedRelayoutDispatchRetainsNeitherTheRelayoutItsHostClockNorThePublishedSnapshot()
    {
        var scheduler = new ManualUiScheduler();

        RelayoutProbe probe = QueueOnUnreferencedRelayoutGraph(scheduler);

        Assert.Equal(1, scheduler.PendingCount);
        ManualUiTimer timer = Assert.Single(scheduler.Timers);
        Assert.True(timer.IsRunning);
        RetentionCollector.CollectUntilDead(probe.Relayout, probe.Host, probe.Clock, probe.Snapshot);
        Assert.False(probe.Relayout.IsAlive);
        Assert.False(probe.Host.IsAlive);
        Assert.False(probe.Clock.IsAlive);
        Assert.False(probe.Snapshot.IsAlive);

        Assert.Equal(1, scheduler.RunPending());
        Assert.Equal(0, scheduler.PendingCount);
        timer.Fire();
        Assert.False(timer.IsRunning);
    }

    [Fact]
    public void WorkerCompletionQueuedForTheUiThreadRetainsNeitherTheRelayoutHostSnapshotNorDirtyIndices()
    {
        var scheduler = new ManualUiScheduler();
        var cancellation = new RetirableCancellationTokenSource();

        WorkerProbe probe = RunWorkerForUnreferencedRelayoutGraph(scheduler, cancellation);

        // The worker has finished and posted its one-shot publication. The
        // completed task and the posted callback both stay reachable here.
        Assert.True(probe.Worker.IsCompletedSuccessfully);
        Assert.Equal(1, scheduler.PendingCount);
        RetentionCollector.CollectUntilDead(
            probe.Relayout, probe.Host, probe.Snapshot, probe.Changed);
        Assert.False(probe.Relayout.IsAlive);
        Assert.False(probe.Host.IsAlive);
        Assert.False(probe.Snapshot.IsAlive);
        Assert.False(probe.Changed.IsAlive);
        Assert.False(cancellation.IsCancellationRequested);

        // With its receiver gone, the publication only retires the operation's
        // cancellation source, which is the one thing it is meant to carry.
        Assert.Equal(1, scheduler.RunPending());
        Assert.True(cancellation.IsCancellationRequested);
        GC.KeepAlive(probe.Worker);
    }

    [Fact]
    public void UiRecoveryTimerRetainsNeitherTheClockNorItsHost()
    {
        var scheduler = new ManualUiScheduler();
        var ticks = new TickCounter();

        (WeakReference clock, WeakReference host) = StartUnreferencedClock(scheduler, ticks);

        ManualUiTimer timer = Assert.Single(scheduler.Timers);
        RetentionCollector.CollectUntilDead(clock, host);
        Assert.False(clock.IsAlive);
        Assert.False(host.IsAlive);
        Assert.True(timer.IsRunning);

        timer.Fire();
        Assert.Equal(0, ticks.Count);
        Assert.False(timer.IsRunning);
    }

    [Fact]
    public void ManagedRecoveryFallbackRetainsNeitherTheClockNorItsHostWhileATickIsQueued()
    {
        var scheduler = new ManualUiScheduler { FailTimerCreation = true };
        var ticks = new TickCounter();

        ClockProbe probe = StartUnreferencedFallbackClock(scheduler, ticks);
        // The managed timer is rooted by the runtime's timer queue. Keep the
        // clock alive until that timer has posted one tick, then let it go.
        scheduler.WaitForPending();
        probe.Keeper.Value = null;

        RetentionCollector.CollectUntilDead(probe.Clock, probe.Host);
        Assert.False(probe.Clock.IsAlive);
        Assert.False(probe.Host.IsAlive);
        Assert.True(scheduler.PendingCount >= 1);

        scheduler.RunPending();
        Assert.Equal(0, ticks.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static CompletionProbe QueueOffThreadCompletion(
        ImageRelayoutFixture fixture,
        MarkdownRenderResources resources,
        CompletionEffects effects)
    {
        var host = new CompletionHost(effects);
        var published = new PublishedLayout { Snapshot = CreateSnapshot(), Generation = 3 };
        var completion = new ImageLoadCompletion(
            host, fixture.Lifetime, published, fixture.LayoutCommit, fixture.Subscriptions,
            fixture.Relayout, fixture.Scheduler);
        ImageBox image = CreateImage(resources, blockIndex: 1);
        var eventData = new LoadCompletedEventArgs(layoutInvalidated: true);

        completion.OnLoadCompleted(image, eventData);

        return new CompletionProbe(
            new WeakReference(completion), new WeakReference(host), new WeakReference(published.Snapshot),
            new WeakReference(image), new WeakReference(eventData));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CompleteOffThreadWithUnreferencedImage(
        ImageLoadCompletion completion,
        MarkdownRenderResources resources)
    {
        ImageBox image = CreateImage(resources, blockIndex: 2);
        completion.OnLoadCompleted(image, new LoadCompletedEventArgs(layoutInvalidated: true));
        return new WeakReference(image);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RelayoutProbe QueueOnUnreferencedRelayoutGraph(ManualUiScheduler scheduler)
    {
        var graph = new RelayoutGraph(scheduler);
        graph.Relayout.Queue(1);
        return new RelayoutProbe(
            new WeakReference(graph.Relayout), new WeakReference(graph.Host),
            new WeakReference(graph.Clock), new WeakReference(graph.Published.Snapshot));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WorkerProbe RunWorkerForUnreferencedRelayoutGraph(
        ManualUiScheduler scheduler,
        RetirableCancellationTokenSource cancellation)
    {
        var graph = new RelayoutGraph(scheduler);
        LayoutSnapshot snapshot = graph.Published.Snapshot!;
        int[] changed = [0, 2];
        var payload = new ImageRelayoutWorkPayload<LayoutSnapshot>(snapshot, changed, width: 100);

        Task worker = ImageRelayoutWork.RunAsync(
            payload, anchor: null, graph.Published.Generation, cancellation, scheduler,
            new WeakReference<ImageRelayout>(graph.Relayout), session: null, rasterPlacement: null,
            snapshot.PointerInputEpoch, capturedScrollOffset: null);
        Assert.True(worker.Wait(TimeSpan.FromSeconds(10)));

        return new WorkerProbe(
            worker, new WeakReference(graph.Relayout), new WeakReference(graph.Host),
            new WeakReference(snapshot), new WeakReference(changed));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Clock, WeakReference Host) StartUnreferencedClock(
        ManualUiScheduler scheduler,
        TickCounter ticks)
    {
        var host = new TickHost(ticks);
        var clock = new RecoveryClock(host, LoadedLifetime(), scheduler);
        clock.Start();
        Assert.True(clock.IsRunning);
        return (new WeakReference(clock), new WeakReference(host));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ClockProbe StartUnreferencedFallbackClock(ManualUiScheduler scheduler, TickCounter ticks)
    {
        var host = new TickHost(ticks);
        var clock = new RecoveryClock(host, LoadedLifetime(), scheduler);
        clock.Start();
        Assert.True(clock.IsRunning);
        Assert.Empty(scheduler.Timers);
        return new ClockProbe(
            new StrongBox<object?>(clock), new WeakReference(clock), new WeakReference(host));
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

    private static ImageBox CreateImage(MarkdownRenderResources resources, int blockIndex)
    {
        MarkdownLayoutContext context = DisplaySizedRasterDecodeTests.CreateContext(
            resources, new BlockedResolver(), session: null, scale: 1);
        return new ImageBox(context, resources.Images, $"https://example.test/{Guid.NewGuid():N}.png", "diagram")
        {
            BlockIndex = blockIndex,
        };
    }

    private static MarkdownViewLifetime LoadedLifetime()
    {
        var lifetime = new MarkdownViewLifetime();
        lifetime.MarkLoaded();
        return lifetime;
    }

    private sealed record CompletionProbe(
        WeakReference Completion,
        WeakReference Host,
        WeakReference Snapshot,
        WeakReference Image,
        WeakReference EventData);

    private sealed record RelayoutProbe(
        WeakReference Relayout,
        WeakReference Host,
        WeakReference Clock,
        WeakReference Snapshot);

    private sealed record WorkerProbe(
        Task Worker,
        WeakReference Relayout,
        WeakReference Host,
        WeakReference Snapshot,
        WeakReference Changed);

    private sealed record ClockProbe(StrongBox<object?> Keeper, WeakReference Clock, WeakReference Host);

    /// <summary>
    /// A loaded view's relayout owners over their own published snapshot, so
    /// the whole graph becomes unreachable once the test drops it.
    /// </summary>
    private sealed class RelayoutGraph
    {
        internal RelayoutGraph(ManualUiScheduler scheduler)
        {
            var lifetime = LoadedLifetime();
            Published = new PublishedLayout { Snapshot = CreateSnapshot(), Generation = 5 };
            var plans = new ImagePlanSet(lifetime, Published);
            var subscriptions = new ImageSubscriptions(new NoClockHost(), lifetime, Published, plans);
            Clock = new RecoveryClock(new TickHost(new TickCounter()), lifetime, scheduler);
            Host = new ImageRelayoutFixture.RelayoutHost(Clock);
            Relayout = new ImageRelayout(
                Host, lifetime, Published, new LayoutCommitState(), subscriptions, Clock, scheduler);
        }

        internal PublishedLayout Published { get; }

        internal RecoveryClock Clock { get; }

        internal ImageRelayoutFixture.RelayoutHost Host { get; }

        internal ImageRelayout Relayout { get; }
    }

    /// <summary>Everything a completion asked of its host, counted.</summary>
    private sealed class CompletionEffects
    {
        internal int StatusNotices { get; set; }

        internal int FullRebuilds { get; set; }

        internal int RejectedCrossThreadRebuilds { get; set; }

        internal int Repaints { get; set; }

        internal int Total => StatusNotices + FullRebuilds + RejectedCrossThreadRebuilds + Repaints;
    }

    /// <summary>
    /// A completion host that records into <see cref="CompletionEffects"/>, which
    /// does not reference the host, so the host itself can be collected.
    /// </summary>
    private sealed class CompletionHost(CompletionEffects effects) : IImageLoadCompletionHost
    {
        public IMarkdownPerformanceSession? PerformanceSession => null;

        public float LayoutWidth => 100;

        public bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
            snapshot?.GeometryRecoveryRequired == true;

        public void NotifyImageStatusChanged(ImageBox image) => effects.StatusNotices++;

        public void RequestFullRebuild() => effects.FullRebuilds++;

        public void RecordRejectedCrossThreadFullRebuild() => effects.RejectedCrossThreadRebuilds++;

        public void InvalidatePaint() => effects.Repaints++;
    }

    private sealed class TickCounter
    {
        internal int Count { get; set; }
    }

    private sealed class TickHost(TickCounter ticks) : IRecoveryClockHost
    {
        public void ProcessRecoveryTick() => ticks.Count++;
    }

    private sealed class NoClockHost : IImageSubscriptionsHost
    {
        public void StartRecoveryClock()
        {
        }
    }

    private sealed class BlockedResolver : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Blocked(MarkdownImageUnavailableReason.Unavailable));
    }
}
