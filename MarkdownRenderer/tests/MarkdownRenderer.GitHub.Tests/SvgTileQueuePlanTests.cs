using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <remarks>
/// This file is longer than 500 lines because it is the single test class for
/// the SVG tile queue's render plan, pending-work and viewport-visibility
/// rules, and splitting it would rename its tests.
/// </remarks>
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class SvgTileQueuePlanTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public void ImmutablePlanRetainsExactDocumentIdentityAndPhysicalDimensions()
    {
        var document = new CountingDocument();
        const string identity = "svg-cache-prefix:document-a";
        const int width = 3073;
        const int height = 2051;
        const int generation = 41;

        SvgTileRenderPlan plan = NewPlan(document, identity, width, height, generation);

        AssertPlanMember(plan, "Document", document);
        AssertPlanMember(plan, "IdentityPrefix", identity);
        AssertPlanMember(plan, "FullWidthPixels", width);
        AssertPlanMember(plan, "FullHeightPixels", height);
        AssertPlanMember(plan, "RenderGeneration", generation);
    }

    [Fact]
    public void WarmPendingTileIsAZeroAllocationNoOpAndDoesNotCallProvider()
    {
        var document = new CountingDocument();
        SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:warm", 2048, 2048, 12);
        ImageBox box = CreateBareBox(plan);
        var cancellation = new RetirableCancellationTokenSource();
        try
        {
            RegisterWorkOwner(
                box,
                plan,
                (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels),
                cancellation);

            for (int i = 0; i < 1000; i++)
            {
                Assert.Equal(0, Queue(box, plan, 0, 0, 0, 0, 1, MarkdownSvgRenderPriority.Visible));
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            int admitted = 0;
            for (int i = 0; i < 10_000; i++)
            {
                admitted += Queue(box, plan, 0, 0, 0, 0, 1, MarkdownSvgRenderPriority.Visible);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, admitted);
            Assert.Equal(0, allocated);
            Assert.Equal(0, document.RenderCount);
            Assert.Equal(0, document.DisposeCount);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExtremeViewportAtIntMaxPreservesPendingEdgeTilesWithoutRequeue(bool horizontal)
    {
        const int edge = int.MaxValue;
        const int tileSize = ImageBox.SvgTileSizePixels;
        int width = horizontal ? edge : tileSize;
        int height = horizontal ? tileSize : edge;
        var document = new CountingDocument();
        SvgTileRenderPlan plan = NewPlan(
            document,
            "svg-cache-prefix:maximum-dimensions",
            width,
            height,
            44);
        var renderer = new QueueTestSvgRenderer();
        ImageBox box = CreateBareBox(plan, renderer);
        AdoptRendererMetadata(box, renderer, document);

        // A canceled lifetime token does not affect the pending-tile fast path.
        // If a regression requeues an edge tile, RenderSvgTileAsync creates a
        // linked token and its provider-admission wait cancels before device
        // access or provider invocation. Do not mutate the shared static gate.
        box.Lifetime.LifetimeCancellation.Cancel();

        (int X, int Y, int Width, int Height)[] expectedKeys = GetLastTwoEdgeTileKeys(width, height, horizontal);
        var originalOwners = new Dictionary<(int X, int Y, int Width, int Height), RetirableCancellationTokenSource>();
        foreach (var key in expectedKeys)
        {
            var owner = new RetirableCancellationTokenSource();
            originalOwners.Add(key, owner);
            RegisterWorkOwner(box, plan, key, owner);
        }

        double destinationWidth = width / 1000d;
        double destinationHeight = height / 1000d;
        var destination = new Rect(0, 0, destinationWidth, destinationHeight);
        var viewport = horizontal
            ? new Rect(destination.Right - 1, 0, 1, destination.Height)
            : new Rect(0, destination.Bottom - 1, destination.Width, 1);
        try
        {
            QueueVisible(box, destination, viewport);

            IReadOnlyDictionary<(int X, int Y, int Width, int Height), RetirableCancellationTokenSource> pending =
                Snapshot(box).Work;
            Assert.Equal(expectedKeys.Length, pending.Count);
            foreach (var key in expectedKeys)
                Assert.Same(originalOwners[key], pending[key]);
            Assert.Equal(0, document.RenderCount);
            Assert.Equal(0, renderer.OpenCount);
        }
        finally
        {
            await CancelEdgeWorkAndDisposeOwnersAsync(box, originalOwners.Values);
        }
    }

    private static (int X, int Y, int Width, int Height)[] GetLastTwoEdgeTileKeys(
        int width,
        int height,
        bool horizontal)
    {
        const int tileSize = ImageBox.SvgTileSizePixels;
        int lastTileX = ((width - 1) / tileSize) * tileSize;
        int lastTileY = ((height - 1) / tileSize) * tileSize;
        int previousTileX = lastTileX - (horizontal ? tileSize : 0);
        int previousTileY = lastTileY - (horizontal ? 0 : tileSize);
        int lastTileWidth = width - lastTileX;
        int lastTileHeight = height - lastTileY;
        return
            horizontal
                ?
                [
                    (previousTileX, 0, tileSize, tileSize),
                    (lastTileX, 0, lastTileWidth, tileSize),
                ]
                :
                [
                    (0, previousTileY, tileSize, tileSize),
                    (0, lastTileY, tileSize, lastTileHeight),
                ];
    }

    private static async Task CancelEdgeWorkAndDisposeOwnersAsync(
        ImageBox box,
        IEnumerable<RetirableCancellationTokenSource> originalOwners)
    {
        // If this regression is reintroduced, cancel the wrongly queued
        // edge work and wait for its ordinary lifetime barrier before
        // disposing the test-only resources.
        RetirableCancellationTokenSource[] liveOwners = Snapshot(box).Work.Values.Distinct().ToArray();
        foreach (RetirableCancellationTokenSource owner in liveOwners)
        {
            try { owner.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        await ImageBoxTestState.WaitForImageWorkDrainAsync(box, TimeSpan.FromSeconds(10));

        foreach (RetirableCancellationTokenSource owner in originalOwners)
            owner.Dispose();
    }

    [Fact]
    public void AcceptedPlanRemainsEligibleWhileAReplacementGenerationIsPending()
    {
        var document = new CountingDocument();
        SvgTileRenderPlan oldPlan = NewPlan(document, "svg-cache-prefix:old-generation", 2048, 2048, 7);
        ImageBox box = CreateBareBox(oldPlan);
        ImageBoxTestState.AdvanceRenderGenerationTo(box, 8);
        using var currentWork = new RetirableCancellationTokenSource();
        var key = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        RegisterWorkOwner(box, oldPlan, key, currentWork);

        Assert.True(IsCurrentTilePlan(box, oldPlan));
        Assert.Equal(0, Queue(box, oldPlan, 0, 0, 0, 0, 1, MarkdownSvgRenderPriority.Visible));
        Assert.Same(currentWork, Snapshot(box).Work[key]);
        Assert.Equal(0, document.RenderCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task NeverPaintedOverscanPlanCanReleaseItsProviderDocument(int maximumTier)
    {
        var document = new CountingDocument();
        MarkdownSvgRenderDemand demand = new(MarkdownSvgRenderPriority.Overscan);
        SvgHostAdmissionDomain domain = new();
        SvgHostOwnerSlot ownerSlot = domain.Owners.TryReserveOwnerSlot(
            new object(),
            out _) ?? throw new Xunit.Sdk.XunitException("Could not reserve test owner slot.");
        SvgTileRenderPlan plan = NewPlan(
            document,
            "svg-cache-prefix:never-painted-overscan",
            2048,
            2048,
            57,
            demand,
            ownerSlot);
        var renderer = new QueueTestSvgRenderer();
        ImageBox box = CreateBareBox(plan, renderer);
        box.LoadState.BeginDemand(demand);
        AdoptRendererMetadata(box, renderer, document);
        ownerSlot.Commit(box, plan, demand, sourceReservationBytes: 4096);

        Assert.Equal(-1, Snapshot(box).ViewportVisibility);
        Assert.True(box.TryGetSvgPressureParkTier(plan, demand.Version, out int tier));
        Assert.Equal(0, tier);

        Assert.True(box.TryParkSvgDocument(plan, demand.Version, maximumTier));
        Assert.Null(box.Svg.AcceptedPlan);
        Assert.Null(box.SvgDocuments.Current);
        Assert.Null(box.Svg.ParkingPlan);
        Assert.True(box.Svg.IsPressureParked);
        await document.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, document.DisposeCount);
    }

    [Fact]
    public async Task OverscanPromotionWhileParkingWaitsForTileLockAbortsTheDetach()
    {
        var document = new CountingDocument();
        MarkdownSvgRenderDemand demand = new(MarkdownSvgRenderPriority.Overscan);
        SvgHostAdmissionDomain domain = new();
        SvgHostOwnerSlot ownerSlot = domain.Owners.TryReserveOwnerSlot(
            new object(),
            out _) ?? throw new Xunit.Sdk.XunitException("Could not reserve test owner slot.");
        SvgTileRenderPlan plan = NewPlan(
            document,
            "svg-cache-prefix:promotion-before-park",
            2048,
            2048,
            58,
            demand,
            ownerSlot);
        var renderer = new QueueTestSvgRenderer();
        ImageBox box = CreateBareBox(plan, renderer);
        box.LoadState.BeginDemand(demand);
        AdoptRendererMetadata(box, renderer, document);
        ownerSlot.Commit(box, plan, demand, sourceReservationBytes: 4096);

        object tileLock = TileLock(box);
        Task<bool> parking;
        Monitor.Enter(tileLock);
        try
        {
            parking = Task.Run(() => box.TryParkSvgDocument(plan, demandVersion: 0, maximumTier: 0));
            Assert.True(
                SpinWait.SpinUntil(
                    () => ReferenceEquals(box.Svg.ParkingPlan, plan),
                    TimeSpan.FromSeconds(10)),
                "The parking transaction did not publish its marker before waiting on the tile lock.");
            Assert.True(demand.PromoteVisible());
        }
        finally
        {
            Monitor.Exit(tileLock);
        }

        Assert.False(await parking.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(plan, box.Svg.AcceptedPlan);
        Assert.Same(document, box.SvgDocuments.Current);
        Assert.Null(box.Svg.ParkingPlan);
        Assert.False(box.Svg.IsPressureParked);
        Assert.Equal(0, document.DisposeCount);
        ownerSlot.Dispose();
        document.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ViewportObservationReleasesOffscreenPlansAndPinsReentryUntilPaint(bool initiallyVisible)
    {
        var document = new CountingDocument();
        MarkdownSvgRenderDemand demand = new(
            initiallyVisible ? MarkdownSvgRenderPriority.Visible : MarkdownSvgRenderPriority.Overscan);
        SvgHostAdmissionDomain domain = new();
        SvgHostOwnerSlot ownerSlot = domain.Owners.TryReserveOwnerSlot(
            new object(),
            out _) ?? throw new Xunit.Sdk.XunitException("Could not reserve test owner slot.");
        SvgTileRenderPlan plan = NewPlan(
            document,
            "svg-cache-prefix:viewport-observation",
            2048,
            2048,
            59,
            demand,
            ownerSlot);
        ImageBox box = CreateBareBox(plan);
        box.LoadState.BeginDemand(demand);
        ownerSlot.Commit(box, plan, demand, sourceReservationBytes: 4096);
        object? token = box.CaptureSvgViewportObservationToken();
        Assert.Same(plan, token);
        long demandVersionBeforeObservation = demand.Version;

        Assert.True(box.ObserveSvgViewportVisibility(token, visible: false));
        Assert.Equal(0, Snapshot(box).ViewportVisibility);
        Assert.False(Snapshot(box).VisibleRangeKnown);
        Assert.Equal(initiallyVisible ? 1 : 0, demandVersionBeforeObservation);
        Assert.Equal(demandVersionBeforeObservation, demand.Version);
        Assert.True(box.TryGetSvgPressureParkTier(plan, demandVersionBeforeObservation, out int offscreenTier));
        Assert.Equal(0, offscreenTier);

        Assert.True(box.ObserveSvgViewportVisibility(token, visible: true));
        Assert.Equal(1, Snapshot(box).ViewportVisibility);
        Assert.False(Snapshot(box).VisibleRangeKnown);
        Assert.Equal(demandVersionBeforeObservation, demand.Version);
        Assert.False(box.TryGetSvgPressureParkTier(plan, demandVersionBeforeObservation, out _));

        Assert.True(Snapshot(box).ViewportObservationVersion >= 2);
        ownerSlot.Dispose();
        document.Dispose();
    }

    [Fact]
    public void PaintPublishesOnlyChangedExactViewportRangesToTheParkingFence()
    {
        var document = new CountingDocument();
        MarkdownSvgRenderDemand demand = new(MarkdownSvgRenderPriority.Overscan);
        SvgHostAdmissionDomain domain = new();
        SvgHostOwnerSlot ownerSlot = domain.Owners.TryReserveOwnerSlot(
            new object(),
            out _) ?? throw new Xunit.Sdk.XunitException("Could not reserve test owner slot.");
        SvgTileRenderPlan plan = NewPlan(
            document,
            "svg-cache-prefix:painted-viewport-range",
            4096,
            1024,
            60,
            demand,
            ownerSlot);
        var renderer = new QueueTestSvgRenderer();
        ImageBox box = CreateBareBox(plan, renderer);
        box.LoadState.BeginDemand(demand);
        ownerSlot.Commit(box, plan, demand, sourceReservationBytes: 4096);
        AdoptRendererMetadata(box, renderer, document);

        for (int tileX = 0; tileX < 4; tileX++)
            PublishTile(box, plan, (tileX * ImageBox.SvgTileSizePixels, 0, ImageBox.SvgTileSizePixels, 1024));

        var destination = new Rect(0, 0, 4, 1);
        QueueVisible(box, destination, new Rect(10, 10, 1, 1));
        long afterOffscreenPaint = Snapshot(box).ViewportObservationVersion;
        Assert.Equal(0, Snapshot(box).ViewportVisibility);
        Assert.False(Snapshot(box).VisibleRangeKnown);

        object? token = box.CaptureSvgViewportObservationToken();
        Assert.True(box.ObserveSvgViewportVisibility(token, visible: true));
        long afterCoarseReentry = Snapshot(box).ViewportObservationVersion;
        Assert.True(afterCoarseReentry > afterOffscreenPaint);
        Assert.False(Snapshot(box).VisibleRangeKnown);

        QueueVisible(box, destination, new Rect(0, 0, 1, 1));
        long afterFirstPaint = Snapshot(box).ViewportObservationVersion;
        Assert.True(afterFirstPaint > afterCoarseReentry);
        Assert.True(Snapshot(box).VisibleRangeKnown);
        Assert.Equal(1, Snapshot(box).ViewportVisibility);
        Assert.Equal(MarkdownSvgRenderPriority.Visible, demand.Priority);

        QueueVisible(box, destination, new Rect(0, 0, 1, 1));
        Assert.Equal(afterFirstPaint, Snapshot(box).ViewportObservationVersion);

        // The second range is also fully represented by cached tiles, so this
        // is a viewport-fence update, not a provider/device exercise.
        PublishTile(box, plan, (2 * ImageBox.SvgTileSizePixels, 0, ImageBox.SvgTileSizePixels, 1024));
        PublishTile(box, plan, (3 * ImageBox.SvgTileSizePixels, 0, ImageBox.SvgTileSizePixels, 1024));
        QueueVisible(box, destination, new Rect(3, 0, 1, 1));
        Assert.True(Snapshot(box).ViewportObservationVersion > afterFirstPaint);
        Assert.True(Snapshot(box).VisibleRangeKnown);
        Assert.Equal(3, Snapshot(box).FirstVisibleTileX);
        Assert.Equal(3, Snapshot(box).LastVisibleTileX);
        Assert.Equal(0, document.RenderCount);
        Assert.Equal(0, renderer.OpenCount);

        ReleaseTiles(box);
        ownerSlot.Dispose();
    }

    [Fact]
    public void ViewportMutationClaimWinsWhenParkingIsAttempted()
    {
        var document = new CountingDocument();
        MarkdownSvgRenderDemand demand = new(MarkdownSvgRenderPriority.Overscan);
        SvgHostAdmissionDomain domain = new();
        SvgHostOwnerSlot ownerSlot = domain.Owners.TryReserveOwnerSlot(
            new object(),
            out _) ?? throw new Xunit.Sdk.XunitException("Could not reserve test owner slot.");
        SvgTileRenderPlan plan = NewPlan(
            document,
            "svg-cache-prefix:observer-owns-claim",
            2048,
            2048,
            61,
            demand,
            ownerSlot);
        ImageBox box = CreateBareBox(plan);
        box.LoadState.BeginDemand(demand);
        ownerSlot.Commit(box, plan, demand, sourceReservationBytes: 4096);

        Assert.True(plan.TryBeginViewportMutation());
        try
        {
            Assert.False(box.TryParkSvgDocument(plan, demand.Version, maximumTier: 0));
            Assert.Same(plan, box.Svg.AcceptedPlan);
            Assert.Same(document, box.SvgDocuments.Current);
            Assert.Null(box.Svg.ParkingPlan);
            Assert.False(box.Svg.IsPressureParked);
        }
        finally
        {
            plan.EndViewportMutation();
        }

        ownerSlot.Dispose();
        document.Dispose();
    }

    [Fact]
    public async Task ParkingClaimWinsAndRejectsAConcurrentViewportObservation()
    {
        var document = new CountingDocument();
        MarkdownSvgRenderDemand demand = new(MarkdownSvgRenderPriority.Overscan);
        SvgHostAdmissionDomain domain = new();
        SvgHostOwnerSlot ownerSlot = domain.Owners.TryReserveOwnerSlot(
            new object(),
            out _) ?? throw new Xunit.Sdk.XunitException("Could not reserve test owner slot.");
        SvgTileRenderPlan plan = NewPlan(
            document,
            "svg-cache-prefix:parking-owns-claim",
            2048,
            2048,
            62,
            demand,
            ownerSlot);
        var renderer = new QueueTestSvgRenderer();
        ImageBox box = CreateBareBox(plan, renderer);
        box.LoadState.BeginDemand(demand);
        ownerSlot.Commit(box, plan, demand, sourceReservationBytes: 4096);
        object? token = box.CaptureSvgViewportObservationToken();

        AdoptRendererMetadata(box, renderer, document);

        object tileLock = TileLock(box);
        object workLock = box.Lifetime.Sync;
        Task<bool> parking;
        Monitor.Enter(tileLock);
        try
        {
            parking = Task.Run(() => box.TryParkSvgDocument(plan, demand.Version, maximumTier: 0));
            Assert.True(
                SpinWait.SpinUntil(
                    () => ReferenceEquals(box.Svg.ParkingPlan, plan),
                    TimeSpan.FromSeconds(10)),
                "The parking marker was not published before the tile-lock wait.");

            Monitor.Enter(workLock);
            Monitor.Exit(tileLock);
            Assert.True(
                SpinWait.SpinUntil(() => plan.ViewportMutationState == 2, TimeSpan.FromSeconds(10)),
                "The park transaction did not claim the viewport gate before final owner detachment.");
            Assert.False(box.ObserveSvgViewportVisibility(token, visible: true));
            Assert.Equal(-1, Snapshot(box).ViewportVisibility);
        }
        finally
        {
            if (Monitor.IsEntered(tileLock))
                Monitor.Exit(tileLock);
            if (Monitor.IsEntered(workLock))
                Monitor.Exit(workLock);
        }

        Assert.True(await parking.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(box.Svg.AcceptedPlan);
        Assert.Null(box.SvgDocuments.Current);
        Assert.True(box.Svg.IsPressureParked);
        Assert.Equal(2, plan.ViewportMutationState);
        ownerSlot.Dispose();
        await document.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void OldAcceptedPlanFailureDoesNotAdvanceOrRepeatNewRequestGeneration()
    {
        var document = new CountingDocument();
        SvgTileRenderPlan oldPlan = NewPlan(document, "svg-cache-prefix:old-failure", 2048, 2048, 17);
        ImageBox box = CreateBareBox(oldPlan);
        ImageBoxTestState.AdvanceRenderGenerationTo(box, 18);
        using var work = new RetirableCancellationTokenSource();
        var key = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        SvgTileWorkOwner workOwner = RegisterWorkOwner(box, oldPlan, key, work, state: 1);

        Assert.True(TryInvalidateFailedTile(box, oldPlan, key, workOwner));
        Assert.Equal(18, box.Svg.RenderGeneration);
        Assert.False(box.Svg.UsesTiles);
        Assert.Null(box.Svg.AcceptedPlan);

        Assert.False(TryInvalidateFailedTile(box, oldPlan, key, workOwner));
        Assert.Equal(18, box.Svg.RenderGeneration);
        Assert.Equal(0, document.RenderCount);
    }

    [Fact]
    public void SuspendedResidencyRejectsTileQueueWithoutAdmittingWork()
    {
        var document = new CountingDocument();
        SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:suspended", 2048, 2048, 13);
        // Offscreen release is what lets an image's residency be suspended.
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                PrefetchDocumentImages = false,
                ReleaseOffscreenImageResources = true,
            });
        ImageBox box = CreateBareBox(plan, session: session);
        // Close admission only, as the first step of SuspendResources does, so
        // the plan's render generation stays current and suspension alone must
        // reject the queue.
        Assert.True(box.Lifetime.TrySuspend(out RetirableCancellationTokenSource? _, out ImageWorkLease? suspensionWork));
        suspensionWork?.Dispose();

        Assert.Equal(0, Queue(box, plan, 0, 0, 0, 0, 1, MarkdownSvgRenderPriority.Visible));
        Assert.Empty(Snapshot(box).Work);
        Assert.Equal(0, document.RenderCount);
    }

    [Fact]
    public void ReplacedPlanIdentityCannotQueueEvenWhenItsDescriptorValuesMatch()
    {
        var document = new CountingDocument();
        SvgTileRenderPlan stale = NewPlan(document, "svg-cache-prefix:same-values", 2048, 2048, 19);
        SvgTileRenderPlan current = NewPlan(document, "svg-cache-prefix:same-values", 2048, 2048, 19);
        ImageBox box = CreateBareBox(current);

        Assert.NotSame(stale, current);
        Assert.Equal(0, Queue(box, stale, 0, 0, 0, 0, 1, MarkdownSvgRenderPriority.Visible));
        Assert.Empty(Snapshot(box).Work);
        Assert.Equal(0, document.RenderCount);
    }

    [Fact]
    public void NewlyAdoptedDocumentCannotBeQueuedThroughItsPreviousPlan()
    {
        var previousDocument = new CountingDocument();
        var adoptedDocument = new CountingDocument();
        SvgTileRenderPlan previousPlan = NewPlan(
            previousDocument,
            "svg-cache-prefix:previous-document",
            2048,
            2048,
            23);
        ImageBox box = CreateBareBox(previousPlan);
        box.SvgDocuments.SetCurrentNoLock(adoptedDocument);

        Assert.Equal(0, Queue(box, previousPlan, 0, 0, 0, 0, 1, MarkdownSvgRenderPriority.Visible));
        Assert.Empty(Snapshot(box).Work);
        Assert.Equal(0, previousDocument.RenderCount);
        Assert.Equal(0, adoptedDocument.RenderCount);
    }

    [Fact]
    public void RemovedTileOwnerCannotInvalidateTheCurrentImage()
    {
        var document = new CountingDocument();
        SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:removed-owner", 2048, 2048, 37);
        ImageBox box = CreateBareBox(plan);
        using var work = new RetirableCancellationTokenSource();
        var key = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        SvgTileWorkOwner workOwner = RegisterWorkOwner(box, plan, key, work, state: 1);
        box.SvgTileCache.AbandonUnscheduledTileWork(key, workOwner);

        Assert.False(TryInvalidateFailedTile(box, plan, key, workOwner));
        Assert.Equal(plan.RenderGeneration, box.Svg.RenderGeneration);
        Assert.True(box.Svg.UsesTiles);
        Assert.Same(plan, box.Svg.AcceptedPlan);
    }

    [Fact]
    public void ReplacedTileOwnerCannotInvalidateTheCurrentImage()
    {
        var document = new CountingDocument();
        SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:replaced-owner", 2048, 2048, 39);
        ImageBox box = CreateBareBox(plan);
        using var staleWork = new RetirableCancellationTokenSource();
        using var currentWork = new RetirableCancellationTokenSource();
        var key = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        SvgTileWorkOwner staleOwner = new(plan, staleWork, plan.Demand) { State = 1 };
        RegisterWorkOwner(box, plan, key, currentWork);

        Assert.False(TryInvalidateFailedTile(box, plan, key, staleOwner));
        Assert.Same(currentWork, Snapshot(box).Work[key]);
        Assert.Equal(plan.RenderGeneration, box.Svg.RenderGeneration);
        Assert.True(box.Svg.UsesTiles);
        Assert.Same(plan, box.Svg.AcceptedPlan);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactCurrentTileOwnerCanInvalidateEvenIfItsTokenWasCanceled(bool cancelBeforeInvalidation)
    {
        var document = new CountingDocument();
        SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:current-owner", 2048, 2048, 43);
        ImageBox box = CreateBareBox(plan);
        using var work = new RetirableCancellationTokenSource();
        var key = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        SvgTileWorkOwner workOwner = RegisterWorkOwner(box, plan, key, work, state: 1);
        if (cancelBeforeInvalidation)
            work.Cancel();

        Assert.True(TryInvalidateFailedTile(box, plan, key, workOwner));
        Assert.Equal(plan.RenderGeneration + 1, box.Svg.RenderGeneration);
        Assert.False(box.Svg.UsesTiles);
        Assert.Null(box.Svg.AcceptedPlan);
    }

    [Theory]
    [InlineData(0, 0, -1, 0, 1)] // Empty horizontal range.
    [InlineData(-1, 0, -1, 0, 1)] // Negative tile coordinate.
    [InlineData(2, 0, 2, 0, 1)] // Past the right edge of a 2048px image.
    [InlineData(0, 2, 0, 2, 1)] // Past the bottom edge of a 2048px image.
    [InlineData(0, 0, 0, 0, 0)] // No queue capacity.
    public void EmptyOutOfBoundsOrZeroCapacityRangeDoesNotQueue(
        int firstX,
        int firstY,
        int lastX,
        int lastY,
        int capacity)
    {
        var document = new CountingDocument();
        SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:range", 2048, 2048, 29);
        ImageBox box = CreateBareBox(plan);

        Assert.Equal(0, Queue(
            box,
            plan,
            firstX,
            firstY,
            lastX,
            lastY,
            capacity,
            MarkdownSvgRenderPriority.Visible));
        Assert.Empty(Snapshot(box).Work);
        Assert.Equal(0, document.RenderCount);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void DisposedOrNonTiledBoxCannotQueue(bool disposed, bool usesTiles)
    {
        var document = new CountingDocument();
        SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:inactive", 2048, 2048, 31);
        ImageBox box = CreateBareBox(plan);
        // Begin disposal only, as the first step of Dispose does, so the plan's
        // render generation stays current and disposal alone must reject it.
        if (disposed)
            Assert.True(box.Lifetime.TryBeginDispose(out TaskCompletionSource? _));
        box.Svg.SetUsesTilesNoLock(usesTiles);

        Assert.Equal(0, Queue(box, plan, 0, 0, 0, 0, 1, MarkdownSvgRenderPriority.Visible));
        Assert.Empty(Snapshot(box).Work);
        Assert.Equal(0, document.RenderCount);
    }

    private static SvgTileRenderPlan NewPlan(
        IMarkdownSvgDocument document,
        string identityPrefix,
        int fullWidthPixels,
        int fullHeightPixels,
        int renderGeneration,
        MarkdownSvgRenderDemand? demand = null,
        SvgHostOwnerSlot? ownerSlot = null)
        => new(
            document,
            identityPrefix,
            fullWidthPixels,
            fullHeightPixels,
            renderGeneration,
            demand,
            ownerSlot: ownerSlot);

    private ImageBox CreateBareBox(
        SvgTileRenderPlan plan,
        IMarkdownSvgRenderer? renderer = null,
        MarkdownPerformanceSession? session = null)
    {
        // The already-present/pending fast path needs no Win2D creator, dispatcher or device.
        var box = new ImageBox(
            new MarkdownLayoutContext(
                resourceCreator: null!,
                themeSnapshot: null!,
                sourceMap: null!,
                registry: null!,
                flowDirection: FlowDirection.LeftToRight)
            {
                RenderResources = _renderResources,
                SvgRenderer = renderer,
                PerformanceSession = session,
                PerformanceDocumentOwner = session is null ? null : new object(),
            },
            _renderResources.Images,
            "https://example.test/tiled.svg",
            "tiled svg");
        ImageBoxTestState.AcceptTiledPlan(box, plan);
        return box;
    }

    // Queue-visible paths check that the accepted metadata still names the
    // context's renderer and its cache generation.
    private void AdoptRendererMetadata(
        ImageBox box,
        QueueTestSvgRenderer renderer,
        IMarkdownSvgDocument document) =>
        ImageBoxTestState.AdoptSvgMetadata(
            box,
            renderer,
            rendererIdentity: GetRendererIdentity(renderer),
            providerCacheGeneration: renderer.CacheGeneration,
            info: document.Info);

    private static SvgTileWorkOwner RegisterWorkOwner(
        ImageBox box,
        SvgTileRenderPlan plan,
        (int X, int Y, int Width, int Height) key,
        RetirableCancellationTokenSource cancellation,
        int state = 0) =>
        SvgTileCacheTestOperations.AdmitWork(
            box.SvgTileCache, plan, key, cancellation, claimFailure: state == 1);

    private static void PublishTile(
        ImageBox box,
        SvgTileRenderPlan plan,
        (int X, int Y, int Width, int Height) key) =>
        SvgTileCacheTestOperations.PublishTile(
            box.SvgTileCache, plan, key, new SharedCanvasBitmapCache.Lease(null!));

    private static void ReleaseTiles(ImageBox box)
    {
        (SharedCanvasBitmapCache.Lease[] tiles, RetirableCancellationTokenSource[] work) = box.SvgTileCache.DetachAll();
        box.SvgTileCache.RetireDetached(tiles, work);
    }

    private static SvgTileCacheSnapshot Snapshot(ImageBox box) => box.SvgTileCache.CaptureSnapshot();

    // The pressure-park transaction is still the box's, so its lock-order
    // races hold the cache's tile lock directly until parking has an owner
    // over a test host.
    private static object TileLock(ImageBox box) => box.SvgTileCache.Sync;

    private static bool TryInvalidateFailedTile(
        ImageBox box,
        SvgTileRenderPlan plan,
        (int X, int Y, int Width, int Height) key,
        SvgTileWorkOwner workOwner,
        object? transitionToken = null)
        => box.TileHost.TryInvalidateFailedSvgTile(plan, key, workOwner, transitionToken);

    private static bool IsCurrentTilePlan(ImageBox box, SvgTileRenderPlan plan) =>
        box.SvgTileCache.IsCurrentSvgTilePlan(plan);

    private static int Queue(
        ImageBox box,
        SvgTileRenderPlan plan,
        int firstTileX,
        int firstTileY,
        int lastTileX,
        int lastTileY,
        int capacity,
        MarkdownSvgRenderPriority priority) =>
        box.SvgTileRenderer.QueueSvgTileRange(
            plan, firstTileX, firstTileY, lastTileX, lastTileY, capacity, priority);

    private static void QueueVisible(ImageBox box, Rect destination, Rect viewport) =>
        box.SvgTileRenderer.QueueVisibleSvgTiles(destination, viewport);

    private long GetRendererIdentity(IMarkdownSvgRenderer renderer) =>
        _renderResources.Images.ProviderIdentities.GetSvgRendererIdentity(renderer);

    private static void AssertPlanMember<T>(SvgTileRenderPlan plan, string memberName, T expected)
    {
        FieldInfo field = FindPlanField(memberName);
        Assert.True(field.IsInitOnly, $"Plan member '{memberName}' must be readonly.");
        Assert.Equal(expected, (T)field.GetValue(plan)!);
    }

    private static FieldInfo FindPlanField(string memberName)
        => typeof(SvgTileRenderPlan)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
                candidate.Name.TrimStart('_').Equals(memberName, StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.Equals($"<{memberName}>k__BackingField", StringComparison.Ordinal))
            ?? throw new Xunit.Sdk.XunitException($"Immutable plan has no stored '{memberName}' field.");

    private sealed class CountingDocument : IMarkdownSvgDocument
    {
        public MarkdownSvgDocumentInfo Info { get; } = new(
            IntrinsicWidthDips: 2048,
            IntrinsicHeightDips: 2048,
            IntrinsicAspectRatio: 1);

        internal int RenderCount;
        internal int DisposeCount;
        internal readonly TaskCompletionSource Disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MarkdownSvgRaster> RenderAsync(
            MarkdownSvgRenderRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref RenderCount);
            return ValueTask.FromException<MarkdownSvgRaster>(
                new InvalidOperationException("Unexpected provider render in a queue-no-op test."));
        }

        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            Disposed.TrySetResult();
        }
    }

    private sealed class QueueTestSvgRenderer : IMarkdownSvgRenderer
    {
        internal int OpenCount;

        public long CacheGeneration => 0;

        public MarkdownSvgSourcePreparation PrepareSource(
            byte[] source,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Queue-only regression must not preflight SVG content.");

        public ValueTask<IMarkdownSvgDocument> OpenAsync(
            MarkdownSvgOpenRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref OpenCount);
            return ValueTask.FromException<IMarkdownSvgDocument>(
                new InvalidOperationException("Queue-only regression must not open SVG content."));
        }
    }
}
