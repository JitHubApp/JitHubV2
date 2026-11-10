using MarkdownRenderer.Controls;
using MarkdownRenderer.Controls.Images;
using MarkdownRenderer.Controls.Pipeline;
using MarkdownRenderer.Document;
using MarkdownRenderer.Images;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Performance;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A loaded view publishing a four-block snapshot (blocks 0 to 3, 40 DIPs
/// each, 100 DIPs wide) with image relayout, image subscriptions and a
/// recovery clock wired to a <see cref="ManualUiScheduler"/> and a recording host.
/// </summary>
internal sealed class ImageRelayoutFixture : IDisposable
{
    private readonly LayoutCommitState _layoutCommit = new();
    private readonly MarkdownRenderResources _imageResources = new();
    private readonly List<ImageBox> _images = [];

    internal ImageRelayoutFixture(bool clockHasScheduler = true)
    {
        Lifetime.MarkLoaded();
        float top = 0;
        Blocks = new MeasuredBox[4];
        for (int index = 0; index < Blocks.Length; index++)
        {
            var block = new MeasuredBox(40) { BlockIndex = index };
            block.Measure(100);
            block.Arrange(0, top, 100);
            top += 40;
            Blocks[index] = block;
        }
        Snapshot = new LayoutSnapshot(Blocks, new MarkdownSourceMap(string.Empty), width: 100, height: top);
        Published.Snapshot = Snapshot;
        Published.Generation = 7;
        Plans = new ImagePlanSet(Lifetime, Published);
        Subscriptions = new ImageSubscriptions(
            new NoClockHost(), Lifetime, Published, Plans);
        Clock = new RecoveryClock(new NoTickHost(), Lifetime, clockHasScheduler ? Scheduler : null);
        Host = new RelayoutHost(Clock);
        Relayout = CreateRelayout();
    }

    internal ManualUiScheduler Scheduler { get; } = new();

    internal MarkdownViewLifetime Lifetime { get; } = new();

    internal PublishedLayout Published { get; } = new();

    internal LayoutCommitState LayoutCommit => _layoutCommit;

    internal MeasuredBox[] Blocks { get; }

    internal LayoutSnapshot Snapshot { get; }

    internal ImageSubscriptions Subscriptions { get; }

    internal ImagePlanSet Plans { get; }

    internal RecoveryClock Clock { get; }

    internal RelayoutHost Host { get; }

    internal ImageRelayout Relayout { get; }

    internal ImageRelayout CreateRelayout() =>
        new(Host, Lifetime, Published, _layoutCommit, Subscriptions, Clock, Scheduler);

    /// <summary>Creates an image at <paramref name="blockIndex"/> and subscribes to it.</summary>
    internal ImageBox SubscribeImage(int blockIndex)
    {
        ImageBox image = CreateImage(blockIndex);
        Assert.True(Subscriptions.Subscribe(image));
        return image;
    }

    /// <summary>Creates an image at <paramref name="blockIndex"/> that the fixture disposes.</summary>
    internal ImageBox CreateImage(int blockIndex)
    {
        MarkdownLayoutContext context = DisplaySizedRasterDecodeTests.CreateContext(
            _imageResources, new BlockedResolver(), session: null, scale: 1);
        var image = new ImageBox(context, _imageResources.Images, $"https://example.test/{Guid.NewGuid():N}.png", "diagram")
        {
            BlockIndex = blockIndex,
        };
        _images.Add(image);
        return image;
    }

    /// <summary>Runs the queued dispatch, waits for the worker's completion post, then runs it.</summary>
    internal void RunDispatchAndWorker()
    {
        Assert.Equal(1, Scheduler.RunPending());
        Scheduler.WaitForPending();
        Assert.Equal(1, Scheduler.RunPending());
    }

    public void Dispose()
    {
        Relayout.Retire();
        Subscriptions.UnsubscribeAll();
        foreach (ImageBox image in _images)
            image.Dispose();
        Snapshot.Dispose();
    }

    /// <summary>A block of fixed height that counts its measures.</summary>
    internal sealed class MeasuredBox(float height) : BlockBox
    {
        internal int Measures { get; private set; }

        public override float Measure(float availableWidth)
        {
            Measures++;
            Bounds = new Rect(0, 0, availableWidth, height);
            return height;
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }

    /// <summary>Records what relayout asks of the control.</summary>
    internal sealed class RelayoutHost(RecoveryClock clock) : IImageRelayoutHost
    {
        private readonly PipelineCancellationRetirement _retirement = new(new SnapshotRetirementTracker());

        internal List<(LayoutSnapshot Snapshot, (int BlockIndex, double OffsetFromTop)? Anchor)> Published { get; } = [];

        internal List<bool> ClockStops { get; } = [];

        internal int FullRebuilds { get; private set; }

        internal int DefersCalls { get; private set; }

        internal bool Defers { get; set; }

        public IMarkdownPerformanceSession? PerformanceSession => null;

        public float LayoutWidth { get; set; } = 100;

        public double? VerticalScrollOffset { get; set; }

        public bool HasSnapshotInteraction { get; set; }

        public bool DefersToRebuild()
        {
            DefersCalls++;
            return Defers;
        }

        public bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot) =>
            snapshot?.GeometryRecoveryRequired == true;

        public void RequestFullRebuild() => FullRebuilds++;

        public void StopRecoveryClock(bool force)
        {
            ClockStops.Add(force);
            clock.Stop();
        }

        public Task RetireCancellationTokenSource(RetirableCancellationTokenSource? cts, Task? lifetime) =>
            _retirement.Retire(cts, lifetime);

        /// <summary>How many relayouts started; each asks for raster placement inputs once.</summary>
        internal int StartedRelayouts { get; private set; }

        public RasterPlacementInputs? TryCreateRasterPlacementInputs(LayoutSnapshot snapshot, bool optedIn)
        {
            StartedRelayouts++;
            return null;
        }

        public bool TryPublishRasterPlacement(LayoutSnapshot snapshot, RasterPlacementPlan candidate, long generation) =>
            throw new InvalidOperationException("No raster placement inputs were created.");

        public void PublishRelayout(LayoutSnapshot snapshot, (int BlockIndex, double OffsetFromTop)? anchor) =>
            Published.Add((snapshot, anchor));
    }

    private sealed class NoClockHost : IImageSubscriptionsHost
    {
        public void StartRecoveryClock()
        {
        }
    }

    private sealed class NoTickHost : IRecoveryClockHost
    {
        public void ProcessRecoveryTick()
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
