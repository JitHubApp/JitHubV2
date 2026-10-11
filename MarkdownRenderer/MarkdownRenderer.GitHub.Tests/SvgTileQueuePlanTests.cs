using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class SvgTileQueuePlanTests
{
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void ImmutablePlanRetainsExactDocumentIdentityAndPhysicalDimensions()
    {
        var document = new CountingDocument();
        const string identity = "svg-cache-prefix:document-a";
        const int width = 3073;
        const int height = 2051;
        const int generation = 41;

        ImageBox.SvgTileRenderPlan plan = NewPlan(document, identity, width, height, generation);

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
        ImageBox.SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:warm", 2048, 2048, 12);
        ImageBox box = CreateBareBox(plan);
        var cancellation = new CancellationTokenSource();
        try
        {
            var pending = GetField<Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>>(
                box,
                "_svgTilesInFlight");
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
        ImageBox.SvgTileRenderPlan plan = NewPlan(
            document,
            "svg-cache-prefix:maximum-dimensions",
            width,
            height,
            44);
        ImageBox box = CreateBareBox(plan);
        var renderer = new QueueTestSvgRenderer();
        var context = new MarkdownLayoutContext(
            resourceCreator: null!,
            themeSnapshot: null!,
            sourceMap: null!,
            registry: null!,
            flowDirection: FlowDirection.LeftToRight)
        {
            SvgRenderer = renderer,
        };
        SetField(box, "Context", context);
        SetField(box, "_svgRendererIdentity", GetRendererIdentity(renderer));
        SetField(box, "_svgProviderCacheGeneration", renderer.CacheGeneration);
        SetField(box, "_svgInfo", document.Info);

        // A canceled lifetime token does not affect the pending-tile fast path.
        // If a regression requeues an edge tile, RenderSvgTileAsync creates a
        // linked token and its provider-admission wait cancels before device
        // access or provider invocation. Do not mutate the shared static gate.
        var lifetime = new CancellationTokenSource();
        lifetime.Cancel();
        SetField(box, "_imageLifetimeCancellation", lifetime);

        int lastTileX = ((width - 1) / tileSize) * tileSize;
        int lastTileY = ((height - 1) / tileSize) * tileSize;
        int previousTileX = lastTileX - (horizontal ? tileSize : 0);
        int previousTileY = lastTileY - (horizontal ? 0 : tileSize);
        int lastTileWidth = width - lastTileX;
        int lastTileHeight = height - lastTileY;
        (int X, int Y, int Width, int Height)[] expectedKeys =
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
        var pending = GetField<Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>>(
            box,
            "_svgTilesInFlight");
        var originalOwners = new Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>();
        foreach (var key in expectedKeys)
        {
            var owner = new CancellationTokenSource();
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

            lock (GetField<object>(box, "_svgTileLock"))
            {
                Assert.Equal(expectedKeys.Length, pending.Count);
                foreach (var key in expectedKeys)
                    Assert.Same(originalOwners[key], pending[key]);
            }
            Assert.Equal(0, document.RenderCount);
            Assert.Equal(0, renderer.OpenCount);
        }
        finally
        {
            // If this regression is reintroduced, cancel the wrongly queued
            // edge work and wait for its ordinary lifetime barrier before
            // disposing the test-only resources.
            CancellationTokenSource[] liveOwners;
            lock (GetField<object>(box, "_svgTileLock"))
                liveOwners = pending.Values.Distinct().ToArray();
            foreach (CancellationTokenSource owner in liveOwners)
            {
                try { owner.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            TaskCompletionSource? drained = GetNullableField<TaskCompletionSource>(box, "_imageWorkDrained");
            if (drained is not null)
                await drained.Task.WaitAsync(TimeSpan.FromSeconds(10));

            foreach (CancellationTokenSource owner in originalOwners.Values)
                owner.Dispose();
            lifetime.Dispose();
        }
    }

    [Fact]
    public void AcceptedPlanRemainsEligibleWhileAReplacementGenerationIsPending()
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan oldPlan = NewPlan(document, "svg-cache-prefix:old-generation", 2048, 2048, 7);
        ImageBox box = CreateBareBox(oldPlan);
        SetField(box, "_svgRenderGeneration", 8);
        using var currentWork = new CancellationTokenSource();
        var key = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        GetField<Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>>(
            box,
            "_svgTilesInFlight").Add(key, currentWork);

        Assert.True(IsCurrentTilePlan(box, oldPlan));
        Assert.Equal(0, Queue(box, oldPlan, 0, 0, 0, 0, 1, MarkdownSvgRenderPriority.Visible));
        Assert.Same(currentWork, GetField<Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>>(
            box,
            "_svgTilesInFlight")[key]);
        Assert.Equal(0, document.RenderCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task NeverPaintedOverscanPlanCanReleaseItsProviderDocument(int maximumTier)
    {
        var document = new CountingDocument();
        MarkdownSvgDemandTicket demand = new(MarkdownSvgRenderPriority.Overscan);
        SvgHostAdmissionCoordinator.DomainState domain = new();
        SvgHostAdmissionCoordinator.OwnerSlot ownerSlot = domain.TryReserveOwnerSlot(
            new object(),
            out _) ?? throw new Xunit.Sdk.XunitException("Could not reserve test owner slot.");
        ImageBox.SvgTileRenderPlan plan = NewPlan(
            document,
            "svg-cache-prefix:never-painted-overscan",
            2048,
            2048,
            57,
            demand,
            ownerSlot);
        ImageBox box = CreateBareBox(plan);
        SetField(box, "_svgLoadDemandTicket", demand);
        ConfigureQueueVisible(box, document);
        ownerSlot.Commit(box, plan, demand, sourceReservationBytes: 4096);

        Assert.Equal(-1, GetField<int>(box, "_svgCurrentViewportVisible"));
        Assert.True(box.TryGetSvgPressureParkTier(plan, demand.Version, out int tier));
        Assert.Equal(0, tier);

        Assert.True(box.TryParkSvgDocument(plan, demand.Version, maximumTier));
        Assert.Null(GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgTileRenderPlan"));
        Assert.Null(GetNullableField<IMarkdownSvgDocument>(box, "_svgDocument"));
        Assert.Null(GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgParkingPlan"));
        Assert.True(GetField<bool>(box, "_svgPressureParked"));
        await document.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, document.DisposeCount);
    }

    [Fact]
    public async Task OverscanPromotionWhileParkingWaitsForTileLockAbortsTheDetach()
    {
        var document = new CountingDocument();
        MarkdownSvgDemandTicket demand = new(MarkdownSvgRenderPriority.Overscan);
        SvgHostAdmissionCoordinator.DomainState domain = new();
        SvgHostAdmissionCoordinator.OwnerSlot ownerSlot = domain.TryReserveOwnerSlot(
            new object(),
            out _) ?? throw new Xunit.Sdk.XunitException("Could not reserve test owner slot.");
        ImageBox.SvgTileRenderPlan plan = NewPlan(
            document,
            "svg-cache-prefix:promotion-before-park",
            2048,
            2048,
            58,
            demand,
            ownerSlot);
        ImageBox box = CreateBareBox(plan);
        SetField(box, "_svgLoadDemandTicket", demand);
        ConfigureQueueVisible(box, document);
        ownerSlot.Commit(box, plan, demand, sourceReservationBytes: 4096);

        object tileLock = GetField<object>(box, "_svgTileLock");
        Task<bool> parking;
        Monitor.Enter(tileLock);
        try
        {
            parking = Task.Run(() => box.TryParkSvgDocument(plan, demandVersion: 0, maximumTier: 0));
            Assert.True(
                SpinWait.SpinUntil(
                    () => ReferenceEquals(GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgParkingPlan"), plan),
                    TimeSpan.FromSeconds(10)),
                "The parking transaction did not publish its marker before waiting on the tile lock.");
            Assert.True(demand.PromoteVisible());
        }
        finally
        {
            Monitor.Exit(tileLock);
        }

        Assert.False(await parking.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Same(plan, GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgTileRenderPlan"));
        Assert.Same(document, GetNullableField<IMarkdownSvgDocument>(box, "_svgDocument"));
        Assert.Null(GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgParkingPlan"));
        Assert.False(GetField<bool>(box, "_svgPressureParked"));
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
        MarkdownSvgDemandTicket demand = new(
            initiallyVisible ? MarkdownSvgRenderPriority.Visible : MarkdownSvgRenderPriority.Overscan);
        SvgHostAdmissionCoordinator.DomainState domain = new();
        SvgHostAdmissionCoordinator.OwnerSlot ownerSlot = domain.TryReserveOwnerSlot(
            new object(),
            out _) ?? throw new Xunit.Sdk.XunitException("Could not reserve test owner slot.");
        ImageBox.SvgTileRenderPlan plan = NewPlan(
            document,
            "svg-cache-prefix:viewport-observation",
            2048,
            2048,
            59,
            demand,
            ownerSlot);
        ImageBox box = CreateBareBox(plan);
        SetField(box, "_svgLoadDemandTicket", demand);
        ownerSlot.Commit(box, plan, demand, sourceReservationBytes: 4096);
        object? token = box.CaptureSvgViewportObservationToken();
        Assert.Same(plan, token);
        long demandVersionBeforeObservation = demand.Version;

        Assert.True(box.ObserveSvgViewportVisibility(token, visible: false));
        Assert.Equal(0, GetField<int>(box, "_svgCurrentViewportVisible"));
        Assert.False(GetField<bool>(box, "_svgVisibleTileRangeKnown"));
        Assert.Equal(initiallyVisible ? 1 : 0, demandVersionBeforeObservation);
        Assert.Equal(demandVersionBeforeObservation, demand.Version);
        Assert.True(box.TryGetSvgPressureParkTier(plan, demandVersionBeforeObservation, out int offscreenTier));
        Assert.Equal(0, offscreenTier);

        Assert.True(box.ObserveSvgViewportVisibility(token, visible: true));
        Assert.Equal(1, GetField<int>(box, "_svgCurrentViewportVisible"));
        Assert.False(GetField<bool>(box, "_svgVisibleTileRangeKnown"));
        Assert.Equal(demandVersionBeforeObservation, demand.Version);
        Assert.False(box.TryGetSvgPressureParkTier(plan, demandVersionBeforeObservation, out _));

        Assert.True(GetField<long>(box, "_svgViewportObservationVersion") >= 2);
        ownerSlot.Dispose();
        document.Dispose();
    }

    [Fact]
    public void PaintPublishesOnlyChangedExactViewportRangesToTheParkingFence()
    {
        var document = new CountingDocument();
        MarkdownSvgDemandTicket demand = new(MarkdownSvgRenderPriority.Overscan);
        SvgHostAdmissionCoordinator.DomainState domain = new();
        SvgHostAdmissionCoordinator.OwnerSlot ownerSlot = domain.TryReserveOwnerSlot(
            new object(),
            out _) ?? throw new Xunit.Sdk.XunitException("Could not reserve test owner slot.");
        ImageBox.SvgTileRenderPlan plan = NewPlan(
            document,
            "svg-cache-prefix:painted-viewport-range",
            4096,
            1024,
            60,
            demand,
            ownerSlot);
        ImageBox box = CreateBareBox(plan);
        SetField(box, "_svgLoadDemandTicket", demand);
        ownerSlot.Commit(box, plan, demand, sourceReservationBytes: 4096);
        QueueTestSvgRenderer renderer = ConfigureQueueVisible(box, document);

        Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease> tiles =
            GetField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(
                box, "_svgTiles");
        for (int tileX = 0; tileX < 4; tileX++)
            tiles.Add((tileX * ImageBox.SvgTileSizePixels, 0, ImageBox.SvgTileSizePixels, 1024),
                new SharedCanvasBitmapCache.Lease(null!));

        var destination = new Rect(0, 0, 4, 1);
        QueueVisible(box, destination, new Rect(10, 10, 1, 1));
        long afterOffscreenPaint = GetField<long>(box, "_svgViewportObservationVersion");
        Assert.Equal(0, GetField<int>(box, "_svgCurrentViewportVisible"));
        Assert.False(GetField<bool>(box, "_svgVisibleTileRangeKnown"));

        object? token = box.CaptureSvgViewportObservationToken();
        Assert.True(box.ObserveSvgViewportVisibility(token, visible: true));
        long afterCoarseReentry = GetField<long>(box, "_svgViewportObservationVersion");
        Assert.True(afterCoarseReentry > afterOffscreenPaint);
        Assert.False(GetField<bool>(box, "_svgVisibleTileRangeKnown"));

        QueueVisible(box, destination, new Rect(0, 0, 1, 1));
        long afterFirstPaint = GetField<long>(box, "_svgViewportObservationVersion");
        Assert.True(afterFirstPaint > afterCoarseReentry);
        Assert.True(GetField<bool>(box, "_svgVisibleTileRangeKnown"));
        Assert.Equal(1, GetField<int>(box, "_svgCurrentViewportVisible"));
        Assert.Equal(MarkdownSvgRenderPriority.Visible, demand.Priority);

        QueueVisible(box, destination, new Rect(0, 0, 1, 1));
        Assert.Equal(afterFirstPaint, GetField<long>(box, "_svgViewportObservationVersion"));

        // The second range is also fully represented by cached tiles, so this
        // is a viewport-fence update, not a provider/device exercise.
        tiles.Add((2 * ImageBox.SvgTileSizePixels, 0, ImageBox.SvgTileSizePixels, 1024),
            new SharedCanvasBitmapCache.Lease(null!));
        tiles.Add((3 * ImageBox.SvgTileSizePixels, 0, ImageBox.SvgTileSizePixels, 1024),
            new SharedCanvasBitmapCache.Lease(null!));
        QueueVisible(box, destination, new Rect(3, 0, 1, 1));
        Assert.True(GetField<long>(box, "_svgViewportObservationVersion") > afterFirstPaint);
        Assert.True(GetField<bool>(box, "_svgVisibleTileRangeKnown"));
        Assert.Equal(3, GetField<int>(box, "_svgVisibleFirstTileX"));
        Assert.Equal(3, GetField<int>(box, "_svgVisibleLastTileX"));
        Assert.Equal(0, document.RenderCount);
        Assert.Equal(0, renderer.OpenCount);

        foreach (SharedCanvasBitmapCache.Lease lease in tiles.Values)
            lease.Dispose();
        tiles.Clear();
        ownerSlot.Dispose();
    }

    [Fact]
    public void ViewportMutationClaimWinsWhenParkingIsAttempted()
    {
        var document = new CountingDocument();
        MarkdownSvgDemandTicket demand = new(MarkdownSvgRenderPriority.Overscan);
        SvgHostAdmissionCoordinator.DomainState domain = new();
        SvgHostAdmissionCoordinator.OwnerSlot ownerSlot = domain.TryReserveOwnerSlot(
            new object(),
            out _) ?? throw new Xunit.Sdk.XunitException("Could not reserve test owner slot.");
        ImageBox.SvgTileRenderPlan plan = NewPlan(
            document,
            "svg-cache-prefix:observer-owns-claim",
            2048,
            2048,
            61,
            demand,
            ownerSlot);
        ImageBox box = CreateBareBox(plan);
        SetField(box, "_svgLoadDemandTicket", demand);
        ownerSlot.Commit(box, plan, demand, sourceReservationBytes: 4096);

        Assert.True(plan.TryBeginViewportMutation());
        try
        {
            Assert.False(box.TryParkSvgDocument(plan, demand.Version, maximumTier: 0));
            Assert.Same(plan, GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgTileRenderPlan"));
            Assert.Same(document, GetNullableField<IMarkdownSvgDocument>(box, "_svgDocument"));
            Assert.Null(GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgParkingPlan"));
            Assert.False(GetField<bool>(box, "_svgPressureParked"));
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
        MarkdownSvgDemandTicket demand = new(MarkdownSvgRenderPriority.Overscan);
        SvgHostAdmissionCoordinator.DomainState domain = new();
        SvgHostAdmissionCoordinator.OwnerSlot ownerSlot = domain.TryReserveOwnerSlot(
            new object(),
            out _) ?? throw new Xunit.Sdk.XunitException("Could not reserve test owner slot.");
        ImageBox.SvgTileRenderPlan plan = NewPlan(
            document,
            "svg-cache-prefix:parking-owns-claim",
            2048,
            2048,
            62,
            demand,
            ownerSlot);
        ImageBox box = CreateBareBox(plan);
        SetField(box, "_svgLoadDemandTicket", demand);
        ownerSlot.Commit(box, plan, demand, sourceReservationBytes: 4096);
        object? token = box.CaptureSvgViewportObservationToken();

        ConfigureQueueVisible(box, document);

        object tileLock = GetField<object>(box, "_svgTileLock");
        object workLock = GetField<object>(box, "_svgWorkSync");
        Task<bool> parking;
        Monitor.Enter(tileLock);
        try
        {
            parking = Task.Run(() => box.TryParkSvgDocument(plan, demand.Version, maximumTier: 0));
            Assert.True(
                SpinWait.SpinUntil(
                    () => ReferenceEquals(GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgParkingPlan"), plan),
                    TimeSpan.FromSeconds(10)),
                "The parking marker was not published before the tile-lock wait.");

            Monitor.Enter(workLock);
            Monitor.Exit(tileLock);
            Assert.True(
                SpinWait.SpinUntil(() => plan.ViewportMutationState == 2, TimeSpan.FromSeconds(10)),
                "The park transaction did not claim the viewport gate before final owner detachment.");
            Assert.False(box.ObserveSvgViewportVisibility(token, visible: true));
            Assert.Equal(-1, GetField<int>(box, "_svgCurrentViewportVisible"));
        }
        finally
        {
            if (Monitor.IsEntered(tileLock))
                Monitor.Exit(tileLock);
            if (Monitor.IsEntered(workLock))
                Monitor.Exit(workLock);
        }

        Assert.True(await parking.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Null(GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgTileRenderPlan"));
        Assert.Null(GetNullableField<IMarkdownSvgDocument>(box, "_svgDocument"));
        Assert.True(GetField<bool>(box, "_svgPressureParked"));
        Assert.Equal(2, plan.ViewportMutationState);
        ownerSlot.Dispose();
        await document.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ParkingPreallocatesCancellationOwnershipAndAlwaysClearsItsMarker()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Layout", "Boxes", "ImageBox.Residency.cs");
        string park = GetMethodBody(source, "internal bool TryParkSvgDocument(");
        string eligibility = GetMethodBody(source, "private bool IsSvgPressureParkEligibleNoLock(");

        int snapshotAllocation = park.IndexOf("new CancellationTokenSource[ownedWorkCount]", StringComparison.Ordinal);
        int firstOwnerClaim = park.IndexOf("Interlocked.CompareExchange(ref owner.State, 2, 0)", StringComparison.Ordinal);
        int firstMapRemoval = park.IndexOf("_svgTilesInFlight.Remove(work.Key)", StringComparison.Ordinal);
        int finallyBlock = park.IndexOf("finally", StringComparison.Ordinal);
        int markerCleanup = finallyBlock < 0
            ? -1
            : park.IndexOf("Volatile.Write(ref _svgParkingPlan, null)", finallyBlock, StringComparison.Ordinal);

        Assert.True(snapshotAllocation >= 0 && firstOwnerClaim > snapshotAllocation && firstMapRemoval > snapshotAllocation,
            "The cancellation snapshot must be allocated before any owner claim or map removal.");
        Assert.True(finallyBlock >= 0 && markerCleanup > finallyBlock,
            "The exact parking marker must be cleared in a finally path, including retirement/allocation exceptions.");
        Assert.DoesNotContain("IsCurrentSvgTilePlan(plan)", eligibility, StringComparison.Ordinal);
        Assert.Contains("IsAcceptedSvgTilePlan(plan)", eligibility, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(Volatile.Read(ref _svgParkingPlan), plan)", eligibility, StringComparison.Ordinal);
        Assert.Contains("demandVersion == 0", eligibility, StringComparison.Ordinal);
        Assert.Contains("MarkdownSvgRenderPriority.Overscan", eligibility, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewportObserversAndPaintShareThePerPlanParkingClaim()
    {
        string imageSource = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Layout", "Boxes", "ImageBox.cs");
        string residencySource = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Layout", "Boxes", "ImageBox.Residency.cs");
        string observe = GetMethodBody(residencySource, "internal bool ObserveSvgViewportVisibility(");
        string paint = GetMethodBody(imageSource, "private void QueueVisibleSvgTiles(");
        string park = GetMethodBody(residencySource, "internal bool TryParkSvgDocument(");

        Assert.Contains("TryBeginViewportMutation()", observe, StringComparison.Ordinal);
        Assert.Contains("finally", observe, StringComparison.Ordinal);
        Assert.Contains("EndViewportMutation()", observe, StringComparison.Ordinal);
        Assert.Contains("_svgTileCacheTransitionToken", observe, StringComparison.Ordinal);
        Assert.Contains("TryBeginViewportMutation()", paint, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment(ref _svgViewportObservationVersion)", paint, StringComparison.Ordinal);
        Assert.Contains("EndViewportMutation()", paint, StringComparison.Ordinal);

        int claim = park.IndexOf("TryClaimViewportParking()", StringComparison.Ordinal);
        int finalWorkLock = claim < 0
            ? -1
            : park.IndexOf("lock (_svgWorkSync)", claim, StringComparison.Ordinal);
        int versionCheck = finalWorkLock < 0
            ? -1
            : park.IndexOf("_svgViewportObservationVersion) == viewportObservationVersion", finalWorkLock, StringComparison.Ordinal);
        int detachment = park.IndexOf("Volatile.Write(ref _svgTileRenderPlan, null)", versionCheck, StringComparison.Ordinal);
        Assert.True(claim >= 0 && finalWorkLock > claim && versionCheck > finalWorkLock && detachment > versionCheck,
            "Parking must claim the per-plan gate before the final version/owner check and document detach.");
        Assert.Contains("EndViewportParking()", park, StringComparison.Ordinal);

        string plan = GetBlockBody(imageSource, imageSource.IndexOf("internal sealed class SvgTileRenderPlan(", StringComparison.Ordinal));
        Assert.Contains("TryBeginViewportMutation()", plan, StringComparison.Ordinal);
        Assert.Contains("TryClaimViewportParking()", plan, StringComparison.Ordinal);
        Assert.Contains("ViewportMutationState", plan, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulPressureParkQueuesOneSourceFreeExactResidencyVisualUpdate()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Layout", "Boxes", "ImageBox.Residency.cs");
        string park = GetMethodBody(source, "internal bool TryParkSvgDocument(");
        string publish = GetMethodBody(source, "private void QueueSvgPressureParkedVisualUpdate(");
        int disposal = park.IndexOf("ScheduleSvgDocumentDisposal(document, cleanup)", StringComparison.Ordinal);
        int visualUpdate = park.IndexOf("QueueSvgPressureParkedVisualUpdate(", disposal, StringComparison.Ordinal);
        int dispatcherOverride = publish.IndexOf("tryEnqueueOverride: callback => dispatcher.TryEnqueue(() => callback())", StringComparison.Ordinal);
        int eventRaise = publish.IndexOf("RaiseLoadCompleted(layoutInvalidated: false)", StringComparison.Ordinal);
        int workLock = publish.IndexOf("lock (_svgWorkSync)", StringComparison.Ordinal);
        string workLockBody = workLock < 0 ? string.Empty : GetBlockBody(publish, workLock);

        Assert.True(disposal >= 0 && visualUpdate > disposal,
            "The source-free visual re-hit must be queued after document cleanup is transferred, outside park locks.");
        Assert.True(dispatcherOverride >= 0,
            "A successful park must always enqueue its visual update, even if parking completed on the UI thread.");
        Assert.True(eventRaise > workLock);
        Assert.DoesNotContain("RaiseLoadCompleted", workLockBody, StringComparison.Ordinal);
        Assert.Contains("parkedPublicationEpoch", publish, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(demand, Volatile.Read(ref _svgLoadDemandTicket))", publish, StringComparison.Ordinal);
        Assert.Contains("renderGeneration != Volatile.Read(ref _svgRenderGeneration)", publish, StringComparison.Ordinal);
        Assert.Contains("parkedPublicationEpoch != Volatile.Read(ref _svgPressureParkPublicationEpoch)", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("SvgTileRenderPlan", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("IMarkdownSvgDocument", publish, StringComparison.Ordinal);
        Assert.DoesNotContain("byte[]", publish, StringComparison.Ordinal);
    }

    [Fact]
    public void TileCacheReplacementIsPreparedBeforeTheAcceptedPlanAndAppliedAtomically()
    {
        var oldDocument = new CountingDocument();
        var newDocument = new CountingDocument();
        ImageBox.SvgTileRenderPlan oldPlan = NewPlan(
            oldDocument, "svg-cache-prefix:transition-old", 2048, 1536, 51);
        ImageBox.SvgTileRenderPlan newPlan = NewPlan(
            newDocument, "svg-cache-prefix:transition-new", 3072, 1536, 52);
        ImageBox box = CreateBareBox(oldPlan);
        SetField(box, "_svgTileCacheIdentityPrefix", oldPlan.IdentityPrefix);
        SetField(box, "_svgTileCacheWidthPixels", oldPlan.FullWidthPixels);
        SetField(box, "_svgTileCacheHeightPixels", oldPlan.FullHeightPixels);

        Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease> oldTiles =
            GetField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(
                box,
                "_svgTiles");
        oldTiles.Add((0, 0, 1024, 1024), new SharedCanvasBitmapCache.Lease(null!));

        object transition = PrepareCacheTransition(box, newPlan);

        Assert.Same(oldPlan, GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgTileRenderPlan"));
        Assert.Same(oldTiles, GetField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(
            box, "_svgTiles"));
        Assert.Equal(oldPlan.IdentityPrefix, GetNullableField<string>(box, "_svgTileCacheIdentityPrefix"));
        Assert.False(IsCurrentTilePlan(box, oldPlan));

        SetField(box, "_svgDocument", newDocument);
        SetField(box, "_svgTileRenderPlan", newPlan);

        Assert.True(ApplyCacheTransition(box, transition));
        RetireCacheTransition(box, transition);

        Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease> activeTiles =
            GetField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(
                box,
                "_svgTiles");
        Assert.NotSame(oldTiles, activeTiles);
        Assert.Empty(activeTiles);
        Assert.Same(oldTiles, GetNullableField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(
            box, "_svgFallbackTiles"));
        Assert.Equal(oldPlan.IdentityPrefix, GetNullableField<string>(box, "_svgFallbackIdentityPrefix"));
        Assert.Equal(oldPlan.FullWidthPixels, GetField<int>(box, "_svgFallbackWidthPixels"));
        Assert.Equal(oldPlan.FullHeightPixels, GetField<int>(box, "_svgFallbackHeightPixels"));
        Assert.Equal(newPlan.IdentityPrefix, GetNullableField<string>(box, "_svgTileCacheIdentityPrefix"));
        Assert.Equal(newPlan.FullWidthPixels, GetField<int>(box, "_svgTileCacheWidthPixels"));
        Assert.Equal(newPlan.FullHeightPixels, GetField<int>(box, "_svgTileCacheHeightPixels"));
        Assert.Equal(-1, GetField<int>(box, "_svgCurrentViewportVisible"));
        Assert.Null(GetNullableField<object>(box, "_svgTileCacheTransitionToken"));
        Assert.True(IsCurrentTilePlan(box, newPlan));
    }

    [Fact]
    public void AbortedTileCacheTransitionPreservesTheAcceptedPlanAndCacheIdentity()
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan oldPlan = NewPlan(
            document, "svg-cache-prefix:transition-abort-old", 2048, 1536, 53);
        ImageBox.SvgTileRenderPlan candidate = NewPlan(
            new CountingDocument(), "svg-cache-prefix:transition-abort-new", 3072, 1536, 54);
        ImageBox box = CreateBareBox(oldPlan);
        SetField(box, "_svgTileCacheIdentityPrefix", oldPlan.IdentityPrefix);
        SetField(box, "_svgTileCacheWidthPixels", oldPlan.FullWidthPixels);
        SetField(box, "_svgTileCacheHeightPixels", oldPlan.FullHeightPixels);

        object transition = PrepareCacheTransition(box, candidate);
        AbortCacheTransition(box, transition);

        Assert.Null(GetNullableField<object>(box, "_svgTileCacheTransitionToken"));
        Assert.Same(oldPlan, GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgTileRenderPlan"));
        Assert.Equal(oldPlan.IdentityPrefix, GetNullableField<string>(box, "_svgTileCacheIdentityPrefix"));
        Assert.Equal(oldPlan.FullWidthPixels, GetField<int>(box, "_svgTileCacheWidthPixels"));
        Assert.Equal(oldPlan.FullHeightPixels, GetField<int>(box, "_svgTileCacheHeightPixels"));
        Assert.True(IsCurrentTilePlan(box, oldPlan));
    }

    [Fact]
    public void FailedTiledPlanWithoutFallbackDetachesItsTileLeasesAndCacheIdentity()
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan plan = NewPlan(
            document, "svg-cache-prefix:terminal-failure-no-fallback", 2048, 1536, 58);
        ImageBox box = CreateBareBox(plan);
        SetField(box, "_svgDocument", document);
        SetField(box, "_svgTileCacheIdentityPrefix", plan.IdentityPrefix);
        SetField(box, "_svgTileCacheWidthPixels", plan.FullWidthPixels);
        SetField(box, "_svgTileCacheHeightPixels", plan.FullHeightPixels);
        SetField(box, "_svgHasRenderedTile", true);

        var key = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease> failedTiles =
            GetField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(
                box, "_svgTiles");
        failedTiles.Add(key, new SharedCanvasBitmapCache.Lease(null!));

        object transition = PrepareFailureTileCacheTransition(box, plan);
        lock (GetField<object>(box, "_svgPublicationSync"))
        {
            SetField(box, "_svgTileRenderPlan", null);
            Assert.Null(GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgTileRenderPlan"));
            SetField(box, "_svgUsesTiles", false);
            Assert.True(ApplyCacheTransition(box, transition));
        }
        RetireCacheTransition(box, transition);

        Assert.NotSame(failedTiles, GetField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(
            box, "_svgTiles"));
        Assert.Empty(GetField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(
            box, "_svgTiles"));
        Assert.Null(GetNullableField<object>(box, "_svgFallbackTiles"));
        Assert.Null(GetNullableField<string>(box, "_svgTileCacheIdentityPrefix"));
        Assert.Equal(0, GetField<int>(box, "_svgTileCacheWidthPixels"));
        Assert.Equal(0, GetField<int>(box, "_svgTileCacheHeightPixels"));
        Assert.False(GetField<bool>(box, "_svgHasRenderedTile"));
        Assert.Null(GetNullableField<object>(box, "_svgTileCacheTransitionToken"));
    }

    [Fact]
    public void FailedTiledPlanRestoresFallbackIdentityWithoutRetainingFailedTiles()
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan plan = NewPlan(
            document, "svg-cache-prefix:terminal-failure-with-fallback", 2048, 1536, 59);
        ImageBox box = CreateBareBox(plan);
        SetField(box, "_svgDocument", document);
        SetField(box, "_svgTileCacheIdentityPrefix", plan.IdentityPrefix);
        SetField(box, "_svgTileCacheWidthPixels", plan.FullWidthPixels);
        SetField(box, "_svgTileCacheHeightPixels", plan.FullHeightPixels);
        var failedKey = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        var fallbackKey = (ImageBox.SvgTileSizePixels, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        var failedLease = new SharedCanvasBitmapCache.Lease(null!);
        var fallbackLease = new SharedCanvasBitmapCache.Lease(null!);
        Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease> failedTiles =
            GetField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(
                box, "_svgTiles");
        failedTiles.Add(failedKey, failedLease);
        var fallbackTiles = new Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>
        {
            [fallbackKey] = fallbackLease,
        };
        SetField(box, "_svgFallbackTiles", fallbackTiles);
        SetField(box, "_svgFallbackIdentityPrefix", "svg-cache-prefix:previous-good");
        SetField(box, "_svgFallbackWidthPixels", 1024);
        SetField(box, "_svgFallbackHeightPixels", 768);

        object transition = PrepareFailureTileCacheTransition(box, plan);
        lock (GetField<object>(box, "_svgPublicationSync"))
        {
            SetField(box, "_svgTileRenderPlan", null);
            SetField(box, "_svgUsesTiles", false);
            Assert.True(ApplyCacheTransition(box, transition));
        }
        RetireCacheTransition(box, transition);

        Assert.Same(fallbackTiles, GetField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(
            box, "_svgTiles"));
        Assert.Same(fallbackLease, GetField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(
            box, "_svgTiles")[fallbackKey]);
        Assert.Null(GetNullableField<object>(box, "_svgFallbackTiles"));
        Assert.Equal("svg-cache-prefix:previous-good", GetNullableField<string>(box, "_svgTileCacheIdentityPrefix"));
        Assert.Equal(1024, GetField<int>(box, "_svgTileCacheWidthPixels"));
        Assert.Equal(768, GetField<int>(box, "_svgTileCacheHeightPixels"));
        Assert.True(GetField<bool>(box, "_svgHasRenderedTile"));
        Assert.Null(GetNullableField<string>(box, "_svgFallbackIdentityPrefix"));
        Assert.NotSame(failedLease, GetField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(
            box, "_svgTiles").Values.SingleOrDefault());
    }

    [Fact]
    public void BitmapTransitionStagesAndDetachesAllOldTileIdentities()
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan oldPlan = NewPlan(
            document, "svg-cache-prefix:bitmap-transition", 2048, 2048, 55);
        ImageBox box = CreateBareBox(oldPlan);
        SetField(box, "_svgTileCacheIdentityPrefix", oldPlan.IdentityPrefix);
        SetField(box, "_svgTileCacheWidthPixels", oldPlan.FullWidthPixels);
        SetField(box, "_svgTileCacheHeightPixels", oldPlan.FullHeightPixels);
        GetField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(
            box,
            "_svgTiles").Add((0, 0, 1024, 1024), new SharedCanvasBitmapCache.Lease(null!));
        var fallback = new Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>
        {
            [(1024, 0, 1024, 1024)] = new SharedCanvasBitmapCache.Lease(null!),
        };
        SetField(box, "_svgFallbackTiles", fallback);

        object transition = PrepareCacheTransition(box, plan: null);
        SetField(box, "_svgTileRenderPlan", null);
        SetField(box, "_svgDocument", null);
        SetField(box, "_svgUsesTiles", false);

        Assert.True(ApplyCacheTransition(box, transition));
        RetireCacheTransition(box, transition);

        Assert.Empty(GetField<Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>>(
            box, "_svgTiles"));
        Assert.Null(GetNullableField<object>(box, "_svgFallbackTiles"));
        Assert.Null(GetNullableField<string>(box, "_svgTileCacheIdentityPrefix"));
        Assert.Equal(0, GetField<int>(box, "_svgTileCacheWidthPixels"));
        Assert.Equal(0, GetField<int>(box, "_svgTileCacheHeightPixels"));
        Assert.Null(GetNullableField<object>(box, "_svgTileCacheTransitionToken"));
    }

    [Fact]
    public void OldAcceptedPlanFailureDoesNotAdvanceOrRepeatNewRequestGeneration()
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan oldPlan = NewPlan(document, "svg-cache-prefix:old-failure", 2048, 2048, 17);
        ImageBox box = CreateBareBox(oldPlan);
        SetField(box, "_svgRenderGeneration", 18);
        using var work = new CancellationTokenSource();
        var key = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        object workOwner = RegisterWorkOwner(box, oldPlan, key, work, state: 1);

        Assert.True(TryInvalidateFailedTile(box, oldPlan, key, workOwner));
        Assert.Equal(18, GetField<int>(box, "_svgRenderGeneration"));
        Assert.False(GetField<bool>(box, "_svgUsesTiles"));
        Assert.Null(GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgTileRenderPlan"));

        Assert.False(TryInvalidateFailedTile(box, oldPlan, key, workOwner));
        Assert.Equal(18, GetField<int>(box, "_svgRenderGeneration"));
        Assert.Equal(0, document.RenderCount);
    }

    [Fact]
    public void SuspendedResidencyRejectsTileQueueWithoutAdmittingWork()
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:suspended", 2048, 2048, 13);
        ImageBox box = CreateBareBox(plan);
        SetField(box, "_resourcesSuspended", true);

        Assert.Equal(0, Queue(box, plan, 0, 0, 0, 0, 1, MarkdownSvgRenderPriority.Visible));
        Assert.Empty(GetField<Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>>(
            box,
            "_svgTilesInFlight"));
        Assert.Equal(0, document.RenderCount);

        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Layout", "Boxes", "ImageBox.cs");
        string currentPlan = GetExpressionBody(source, "private bool IsCurrentSvgTilePlan(");
        Assert.Contains("IsAcceptedSvgTilePlan(plan)", currentPlan, StringComparison.Ordinal);
        string acceptedPlan = GetExpressionBody(source, "private bool IsAcceptedSvgTilePlan(");
        Assert.Contains("!_resourcesSuspended", acceptedPlan, StringComparison.Ordinal);

        string visibleQueue = GetMethodBody(source, "private void QueueVisibleSvgTiles(");
        int earlyPlanGuard = visibleQueue.IndexOf("!IsCurrentSvgTilePlan(plan)", StringComparison.Ordinal);
        int eviction = visibleQueue.IndexOf("_svgTiles.Remove(key)", StringComparison.Ordinal);
        int lockIndex = visibleQueue.IndexOf("lock (_svgTileLock)", StringComparison.Ordinal);
        Assert.True(earlyPlanGuard >= 0 && lockIndex > earlyPlanGuard && eviction > lockIndex,
            "Suspended/stale plans must be rejected before viewport eviction mutates tile ownership.");
    }

    [Fact]
    public void ContentCachedSvgStagesMetadataAndCarriesTheRequestGenerationToCommit()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Layout", "Boxes", "ImageBox.cs");
        string cachedContent = GetMethodBody(source, "private bool TryPublishContentCachedSvg(");

        string[] acceptedStateWrites =
        [
            "_isSvg =",
            "_svgSourceIdentity =",
            "_svgIntrinsicSize =",
            "_svgTitle =",
            "_svgDesc =",
            "_svgContentHash =",
            "_svgSecurityPartitionHash =",
            "_svgInfo =",
            "_svgRendererIdentity =",
            "_svgProviderCacheGeneration =",
            "RememberSvgSource(",
        ];
        foreach (string write in acceptedStateWrites)
        {
            Assert.DoesNotContain(write, cachedContent, StringComparison.Ordinal);
        }

        Assert.Contains(
            "TryPublishCachedSvgBitmap(layoutInvalidated, entry, renderGeneration, resolvedCacheKey, bitmapOutputWork)",
            cachedContent,
            StringComparison.Ordinal);
        Assert.Contains("if (!string.IsNullOrEmpty(builtInSourceAliasKey))", cachedContent, StringComparison.Ordinal);
        Assert.Contains("_svgCache.Set(builtInSourceAliasKey, entry)", cachedContent, StringComparison.Ordinal);
        Assert.DoesNotContain("_svgCache.Set(resolvedCacheKey", cachedContent, StringComparison.Ordinal);

        string core = GetMethodBody(source, "private async Task<SvgOpenStageResult> OpenSvgSourceCoreAsync(");
        string[] cacheAttempts = GetInvocations(core, "TryPublishContentCachedSvg(");
        Assert.Equal(3, cacheAttempts.Length);
        foreach (string invocation in cacheAttempts)
        {
            Assert.Contains("contentCacheKey", invocation, StringComparison.Ordinal);
            Assert.Contains("resolvedCacheKey", invocation, StringComparison.Ordinal);
            Assert.Contains("builtInSourceAliasKey", invocation, StringComparison.Ordinal);
            Assert.Contains("renderGeneration", invocation, StringComparison.Ordinal);
            Assert.Contains("bitmapOutputWork", invocation, StringComparison.Ordinal);
        }

        string cachedBitmap = GetMethodBody(source, "private bool TryPublishCachedSvgBitmap(");
        Assert.Contains("SvgCacheEntry? sourceEntry = null", source, StringComparison.Ordinal);
        Assert.Contains("int? expectedRenderGeneration = null", source, StringComparison.Ordinal);
        Assert.Contains("string? resolvedCacheKey = null", source, StringComparison.Ordinal);
        Assert.Contains("(sourceEntry?.Info ?? _svgInfo)", cachedBitmap, StringComparison.Ordinal);
        Assert.Contains("(sourceEntry?.ContentHash ?? _svgContentHash)", cachedBitmap, StringComparison.Ordinal);
        Assert.Contains("(sourceEntry?.SecurityPartitionHash ?? _svgSecurityPartitionHash)", cachedBitmap, StringComparison.Ordinal);
        Assert.Contains("sourceEntry?.Intrinsic ?? _svgIntrinsicSize", cachedBitmap, StringComparison.Ordinal);
        Assert.Contains("sourceEntry is null ? _svgTitle : sourceEntry.Title", cachedBitmap, StringComparison.Ordinal);
        Assert.Contains("sourceEntry is null ? _svgDesc : sourceEntry.Desc", cachedBitmap, StringComparison.Ordinal);
        Assert.Contains("sourceEntry?.RawBytes", cachedBitmap, StringComparison.Ordinal);
        Assert.Contains("expectedRenderGeneration ?? AdvanceSvgRenderGeneration()", cachedBitmap, StringComparison.Ordinal);
        Assert.Contains("resolvedCacheKey ?? _activeCacheKey ?? Url", cachedBitmap, StringComparison.Ordinal);
        Assert.Contains("publicationCacheKey,", cachedBitmap, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplySvgPublicationMetadataNoLock(", cachedBitmap, StringComparison.Ordinal);

        string commit = GetMethodBody(source, "private bool TryCommitSvgPublication(");
        int generationFence = commit.IndexOf(
            "metadata.RenderGeneration != Volatile.Read(ref _svgRenderGeneration)",
            StringComparison.Ordinal);
        int metadataApply = commit.IndexOf("ApplySvgPublicationMetadataNoLock(metadata)", StringComparison.Ordinal);
        Assert.True(generationFence >= 0 && metadataApply > generationFence,
            "A staged cached entry must only replace accepted metadata after the captured request generation is current.");
    }

    [Fact]
    public void ProcessCacheFallbackStagesMetadataButKeepsSvgClassificationForAuthorizedLoader()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Layout", "Boxes", "ImageBox.cs");
        string fallback = GetMethodBody(source, "private bool TryLoadFallbackFromProcessCache(");

        Assert.Contains("_isSvg = true", fallback, StringComparison.Ordinal);
        Assert.Contains("IsSvgCacheEntryCurrent(entry) && TryPublishCachedSvgBitmap(", fallback, StringComparison.Ordinal);
        Assert.Contains("sourceEntry: entry", fallback, StringComparison.Ordinal);
        Assert.Contains("resolvedCacheKey: cacheKey", fallback, StringComparison.Ordinal);
        foreach (string acceptedStateWrite in new[]
        {
            "_svgSourceIdentity =",
            "_svgIntrinsicSize =",
            "_svgTitle =",
            "_svgDesc =",
            "_svgContentHash =",
            "_svgSecurityPartitionHash =",
            "_svgInfo =",
            "_svgRendererIdentity =",
            "_svgProviderCacheGeneration =",
            "RememberSvgSource(",
        })
        {
            Assert.DoesNotContain(acceptedStateWrite, fallback, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ReplacedPlanIdentityCannotQueueEvenWhenItsDescriptorValuesMatch()
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan stale = NewPlan(document, "svg-cache-prefix:same-values", 2048, 2048, 19);
        ImageBox.SvgTileRenderPlan current = NewPlan(document, "svg-cache-prefix:same-values", 2048, 2048, 19);
        ImageBox box = CreateBareBox(current);

        Assert.NotSame(stale, current);
        Assert.Equal(0, Queue(box, stale, 0, 0, 0, 0, 1, MarkdownSvgRenderPriority.Visible));
        Assert.Empty(GetField<Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>>(
            box,
            "_svgTilesInFlight"));
        Assert.Equal(0, document.RenderCount);
    }

    [Fact]
    public void NewlyAdoptedDocumentCannotBeQueuedThroughItsPreviousPlan()
    {
        var previousDocument = new CountingDocument();
        var adoptedDocument = new CountingDocument();
        ImageBox.SvgTileRenderPlan previousPlan = NewPlan(
            previousDocument,
            "svg-cache-prefix:previous-document",
            2048,
            2048,
            23);
        ImageBox box = CreateBareBox(previousPlan);
        SetField(box, "_svgDocument", adoptedDocument);

        Assert.Equal(0, Queue(box, previousPlan, 0, 0, 0, 0, 1, MarkdownSvgRenderPriority.Visible));
        Assert.Empty(GetField<Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>>(
            box,
            "_svgTilesInFlight"));
        Assert.Equal(0, previousDocument.RenderCount);
        Assert.Equal(0, adoptedDocument.RenderCount);
    }

    [Fact]
    public void RemovedTileOwnerCannotInvalidateTheCurrentImage()
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:removed-owner", 2048, 2048, 37);
        ImageBox box = CreateBareBox(plan);
        using var work = new CancellationTokenSource();
        var inFlight = GetField<Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>>(
            box,
            "_svgTilesInFlight");
        var key = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        object workOwner = RegisterWorkOwner(box, plan, key, work, state: 1);
        inFlight.Remove(key);
        GetWorkOwnerMap(box).Remove(key);

        Assert.False(TryInvalidateFailedTile(box, plan, key, workOwner));
        Assert.Equal(plan.RenderGeneration, GetField<int>(box, "_svgRenderGeneration"));
        Assert.True(GetField<bool>(box, "_svgUsesTiles"));
        Assert.Same(plan, GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgTileRenderPlan"));
    }

    [Fact]
    public void ReplacedTileOwnerCannotInvalidateTheCurrentImage()
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:replaced-owner", 2048, 2048, 39);
        ImageBox box = CreateBareBox(plan);
        using var staleWork = new CancellationTokenSource();
        using var currentWork = new CancellationTokenSource();
        var inFlight = GetField<Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>>(
            box,
            "_svgTilesInFlight");
        var key = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        object staleOwner = CreateWorkOwner(plan, staleWork, state: 1);
        RegisterWorkOwner(box, plan, key, currentWork);

        Assert.False(TryInvalidateFailedTile(box, plan, key, staleOwner));
        Assert.Same(currentWork, inFlight[key]);
        Assert.Equal(plan.RenderGeneration, GetField<int>(box, "_svgRenderGeneration"));
        Assert.True(GetField<bool>(box, "_svgUsesTiles"));
        Assert.Same(plan, GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgTileRenderPlan"));
    }

    [Fact]
    public void RemovedTileOwnerCannotPublishSuccess()
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:removed-success-owner", 2048, 2048, 40);
        ImageBox box = CreateBareBox(plan);
        using var work = new CancellationTokenSource();
        var key = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        RegisterWorkOwner(box, plan, key, work);
        GetField<Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>>(
            box,
            "_svgTilesInFlight").Remove(key);
        GetWorkOwnerMap(box).Remove(key);

        lock (GetField<object>(box, "_svgTileLock"))
            Assert.False(IsCurrentTileWorkOwner(box, plan, key, work));
    }

    [Fact]
    public void ReassignedTileOwnerCannotPublishSuccess()
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:reassigned-success-owner", 2048, 2048, 42);
        ImageBox box = CreateBareBox(plan);
        using var staleWork = new CancellationTokenSource();
        using var currentWork = new CancellationTokenSource();
        var key = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        CreateWorkOwner(plan, staleWork);
        RegisterWorkOwner(box, plan, key, currentWork);

        lock (GetField<object>(box, "_svgTileLock"))
            Assert.False(IsCurrentTileWorkOwner(box, plan, key, staleWork));
    }

    [Fact]
    public void ExactTileOwnerCanPublishSuccess()
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:current-success-owner", 2048, 2048, 44);
        ImageBox box = CreateBareBox(plan);
        using var work = new CancellationTokenSource();
        var key = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        RegisterWorkOwner(box, plan, key, work);

        lock (GetField<object>(box, "_svgTileLock"))
            Assert.True(IsCurrentTileWorkOwner(box, plan, key, work));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactCurrentTileOwnerCanInvalidateEvenIfItsTokenWasCanceled(bool cancelBeforeInvalidation)
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:current-owner", 2048, 2048, 43);
        ImageBox box = CreateBareBox(plan);
        using var work = new CancellationTokenSource();
        var inFlight = GetField<Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>>(
            box,
            "_svgTilesInFlight");
        var key = (0, 0, ImageBox.SvgTileSizePixels, ImageBox.SvgTileSizePixels);
        object workOwner = RegisterWorkOwner(box, plan, key, work, state: 1);
        if (cancelBeforeInvalidation)
            work.Cancel();

        Assert.True(TryInvalidateFailedTile(box, plan, key, workOwner));
        Assert.Equal(plan.RenderGeneration + 1, GetField<int>(box, "_svgRenderGeneration"));
        Assert.False(GetField<bool>(box, "_svgUsesTiles"));
        Assert.Null(GetNullableField<ImageBox.SvgTileRenderPlan>(box, "_svgTileRenderPlan"));
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
        ImageBox.SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:range", 2048, 2048, 29);
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
        Assert.Empty(GetField<Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>>(
            box,
            "_svgTilesInFlight"));
        Assert.Equal(0, document.RenderCount);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void DisposedOrNonTiledBoxCannotQueue(bool disposed, bool usesTiles)
    {
        var document = new CountingDocument();
        ImageBox.SvgTileRenderPlan plan = NewPlan(document, "svg-cache-prefix:inactive", 2048, 2048, 31);
        ImageBox box = CreateBareBox(plan);
        SetField(box, "_disposed", disposed);
        SetField(box, "_svgUsesTiles", usesTiles);

        Assert.Equal(0, Queue(box, plan, 0, 0, 0, 0, 1, MarkdownSvgRenderPriority.Visible));
        Assert.Empty(GetField<Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>>(
            box,
            "_svgTilesInFlight"));
        Assert.Equal(0, document.RenderCount);
    }

    [Fact]
    public void StaleTileFailureChecksPlanAndProviderBeforeMutatingFailureOrTileState()
    {
        string source = ReadRepositorySource(
            "MarkdownRenderer", "MarkdownRenderer", "Layout", "Boxes", "ImageBox.cs");
        string body = GetMethodBody(source, "private void PublishSvgTileFailure(");

        int planGuard = body.IndexOf("IsCurrentSvgTilePlan(", StringComparison.Ordinal);
        int providerGuard = body.IndexOf("IsCurrentSvgRendererState(", StringComparison.Ordinal);
        int ownerGuard = body.IndexOf("TryInvalidateFailedSvgTile(", StringComparison.Ordinal);
        Assert.True(planGuard >= 0, "Tile failure publication must be fenced by the immutable plan.");
        Assert.True(providerGuard > planGuard,
            "Provider identity must be checked after the plan identity and before failure publication.");
        Assert.True(ownerGuard > providerGuard,
            "Exact tile-request ownership must be checked before failure publication.");

        int preparedState = body.IndexOf(
            "preparedDocumentState = GetSvgDocumentUseStateNoLock(plan.Document)",
            StringComparison.Ordinal);
        int preparedCleanup = body.IndexOf(
            "preparedDocumentCleanup = BeginImageWorkNoLock(CancellationToken.None)",
            StringComparison.Ordinal);
        int invalidation = body.IndexOf("TryInvalidateFailedSvgTile(", StringComparison.Ordinal);
        int transitionApply = body.IndexOf("ApplySvgTileCacheTransition(tileTransition)", StringComparison.Ordinal);
        int committedRetirement = body.IndexOf(
            "documentCleanup = RetireSvgDocumentNoLock(",
            StringComparison.Ordinal);
        Assert.True(preparedState >= 0 && preparedCleanup > preparedState &&
            preparedState < invalidation && preparedCleanup < invalidation,
            "The accepted document state and cleanup work lease must be prepared before exact-owner invalidation.");
        Assert.True(invalidation > preparedCleanup && transitionApply > invalidation &&
            committedRetirement > transitionApply,
            "Document retirement may commit only after the exact failure owner and tile-cache transition succeed.");

        string completion = GetMethodBody(source, "private void CompleteSvgDocumentUse(");
        int reservedCleanup = completion.IndexOf("state.ReservedDisposalWork", StringComparison.Ordinal);
        int fallbackAllocation = completion.IndexOf("BeginImageWorkNoLock(CancellationToken.None)", StringComparison.Ordinal);
        Assert.True(reservedCleanup >= 0 && fallbackAllocation > reservedCleanup,
            "The final document-use drain must consume the pre-reserved cleanup lease before attempting a new allocation.");

        string[] sideEffects =
        [
            "ReleaseSvgTiles(",
            "ReleaseSvgDocument(",
            "_svgFailureReason =",
            "_svgFailureDescription =",
            "_loadFailed =",
        ];

        foreach (string sideEffect in sideEffects)
        {
            int sideEffectIndex = body.IndexOf(sideEffect, StringComparison.Ordinal);
            Assert.True(sideEffectIndex < 0 || ownerGuard < sideEffectIndex,
                $"Plan/provider/owner guards must precede stale failure side effect '{sideEffect}'.");
        }

        string owner = GetMethodBody(source, "private bool TryInvalidateFailedSvgTile(");
        int tileLock = owner.IndexOf("lock (_svgTileLock)", StringComparison.Ordinal);
        int exactCancellation = owner.IndexOf("ReferenceEquals(current, workOwner.Cancellation)", StringComparison.Ordinal);
        int exactMappedOwner = owner.IndexOf("ReferenceEquals(mappedOwner, workOwner)", StringComparison.Ordinal);
        int terminalClaim = owner.IndexOf("CompareExchange(ref workOwner.State, 3, 1)", StringComparison.Ordinal);
        int workLock = owner.IndexOf("lock (_svgWorkSync)", StringComparison.Ordinal);
        int generationMutation = owner.IndexOf("Interlocked.Increment(ref _svgRenderGeneration)", StringComparison.Ordinal);
        int acceptedGenerationCheck = owner.IndexOf(
            "workOwner.Plan.RenderGeneration == Volatile.Read(ref _svgRenderGeneration)",
            StringComparison.Ordinal);
        int transitionFence = owner.IndexOf(
            "Volatile.Read(ref _svgTileCacheTransitionToken)",
            workLock,
            StringComparison.Ordinal);
        Assert.True(tileLock >= 0 && exactCancellation > tileLock && exactMappedOwner > exactCancellation &&
            terminalClaim > exactMappedOwner && workLock > terminalClaim,
            "Failure must claim the exact key/CTS owner under tile lock, then invalidate under work lock without nesting.");
        Assert.True(generationMutation > workLock,
            "Global render generation must not advance until exact in-flight ownership is proven.");
        Assert.True(acceptedGenerationCheck > workLock && generationMutation > acceptedGenerationCheck &&
            transitionFence > workLock,
            "Failure from a retained old accepted plan may detach it but must not advance a newer request generation.");
        Assert.DoesNotContain("IsCurrentSvgRendererState(", owner, StringComparison.Ordinal);

        string sharedOwner = GetMethodBody(source, "private bool IsCurrentSvgTileWorkOwnerNoLock(");
        int planFence = sharedOwner.IndexOf("IsCurrentSvgTilePlan(plan)", StringComparison.Ordinal);
        int requestMapCheck = sharedOwner.IndexOf("_svgTilesInFlight.TryGetValue(key", StringComparison.Ordinal);
        int requestIdentity = sharedOwner.IndexOf("ReferenceEquals(current, workCancellation)", StringComparison.Ordinal);
        Assert.True(planFence >= 0 && requestMapCheck > planFence && requestIdentity > requestMapCheck,
            "A tile success/failure must still own the exact current plan and in-flight key/CTS pair.");

        string render = GetMethodBody(source, "private async Task RenderSvgTileAsync(");
        int successProviderState = render.IndexOf("bool rendererCurrent = IsCurrentSvgRendererState();", StringComparison.Ordinal);
        int successLock = successProviderState < 0
            ? -1
            : render.IndexOf("lock (_svgTileLock)", successProviderState, StringComparison.Ordinal);
        int successOwner = successLock < 0
            ? -1
            : render.IndexOf("IsCurrentSvgTileWorkOwnerNoLock(plan, key, workCancellation)", successLock);
        int successAdd = render.IndexOf("_svgTiles.Add(key, publishedLease)", StringComparison.Ordinal);
        Assert.True(successProviderState >= 0 && successLock > successProviderState &&
            successOwner > successLock && successAdd > successOwner,
            "Provider state must be snapshotted before taking the tile lock; local plan/key ownership must then be checked under the lock before installation.");
        string successLockBody = GetBlockBody(render, successLock);
        Assert.DoesNotContain("IsCurrentSvgRendererState(", successLockBody, StringComparison.Ordinal);
        Assert.Contains("rendererCurrent &&", successLockBody, StringComparison.Ordinal);

        int failureTransitionPrepare = body.IndexOf("PrepareSvgTileFailureTransition(plan)", StringComparison.Ordinal);
        int failureInvalidation = body.IndexOf("TryInvalidateFailedSvgTile(", StringComparison.Ordinal);
        int failureApply = body.IndexOf("ApplySvgTileCacheTransition(tileTransition)", StringComparison.Ordinal);
        int publicationGate = body.IndexOf("lock (_svgPublicationSync)", StringComparison.Ordinal);
        Assert.True(publicationGate >= 0 && failureTransitionPrepare > publicationGate &&
            failureInvalidation > failureTransitionPrepare && failureApply > failureInvalidation,
            "Failure invalidation and fallback/no-fallback tile-map transition must serialize with accepted publication.");
        Assert.Contains("RetireSvgTileCacheTransition(tileTransition!)", body, StringComparison.Ordinal);
    }

    private static ImageBox.SvgTileRenderPlan NewPlan(
        IMarkdownSvgDocument document,
        string identityPrefix,
        int fullWidthPixels,
        int fullHeightPixels,
        int renderGeneration,
        MarkdownSvgDemandTicket? demand = null,
        SvgHostAdmissionCoordinator.OwnerSlot? ownerSlot = null)
        => new(
            document,
            identityPrefix,
            fullWidthPixels,
            fullHeightPixels,
            renderGeneration,
            demand,
            ownerSlot: ownerSlot);

    private static object PrepareCacheTransition(
        ImageBox box,
        ImageBox.SvgTileRenderPlan? plan)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
                "PrepareSvgTileCacheTransition",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ImageBox).FullName, "PrepareSvgTileCacheTransition");
        return method.Invoke(box, [plan])
            ?? throw new Xunit.Sdk.XunitException("Could not prepare SVG tile cache transition.");
    }

    private static object PrepareFailureTileCacheTransition(
        ImageBox box,
        ImageBox.SvgTileRenderPlan plan)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
                "PrepareSvgTileFailureTransition",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ImageBox).FullName, "PrepareSvgTileFailureTransition");
        return method.Invoke(box, [plan])
            ?? throw new Xunit.Sdk.XunitException("Could not prepare SVG tile failure transition.");
    }

    private static bool ApplyCacheTransition(ImageBox box, object transition) =>
        (bool)InvokeTransitionMethod(box, "ApplySvgTileCacheTransition", transition)!;

    private static void AbortCacheTransition(ImageBox box, object transition) =>
        _ = InvokeTransitionMethod(box, "AbortSvgTileCacheTransition", transition);

    private static void RetireCacheTransition(ImageBox box, object transition) =>
        _ = InvokeTransitionMethod(box, "RetireSvgTileCacheTransition", transition);

    private static object? InvokeTransitionMethod(ImageBox box, string name, object transition)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ImageBox).FullName, name);
        return method.Invoke(box, [transition]);
    }

    private static ImageBox CreateBareBox(ImageBox.SvgTileRenderPlan plan)
    {
        // Queue's already-present/pending fast path must not require a Win2D
        // resource creator, XAML dispatcher, or native device.
        var box = (ImageBox)RuntimeHelpers.GetUninitializedObject(typeof(ImageBox));
        SetField(box, "_svgWorkSync", new object());
        SetField(box, "_svgPublicationSync", new object());
        SetField(box, "_svgTileLock", new object());
        SetField(box, "_svgTiles", new Dictionary<(int X, int Y, int Width, int Height), SharedCanvasBitmapCache.Lease>());
        SetField(box, "_svgTilesInFlight", new Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>());
        SetField(box, "_svgTileWorkOwners", CreateWorkOwnerMap());
        SetField(box, "_svgTileCacheTransitionToken", null);
        SetField(box, "_svgFallbackTiles", null);
        SetField(box, "_svgTileCacheIdentityPrefix", null);
        SetField(box, "_svgTileCacheWidthPixels", 0);
        SetField(box, "_svgTileCacheHeightPixels", 0);
        SetField(box, "_svgVisibleTileRangeKnown", false);
        SetField(box, "_svgCurrentViewportVisible", -1);
        SetField(box, "_svgTileRenderPlan", plan);
        SetField(box, "_svgDocument", PlanMember<IMarkdownSvgDocument>(plan, "Document"));
        SetField(box, "_svgLoadDemandTicket", PlanMember<MarkdownSvgDemandTicket>(plan, "Demand"));
        SetField(box, "_svgRenderGeneration", PlanMember<int>(plan, "RenderGeneration"));
        SetField(box, "_svgUsesTiles", true);
        SetField(box, "_resourcesSuspended", false);
        SetField(box, "_disposed", false);
        return box;
    }

    private static QueueTestSvgRenderer ConfigureQueueVisible(ImageBox box, IMarkdownSvgDocument document)
    {
        var renderer = new QueueTestSvgRenderer();
        var context = new MarkdownLayoutContext(
            resourceCreator: null!,
            themeSnapshot: null!,
            sourceMap: null!,
            registry: null!,
            flowDirection: FlowDirection.LeftToRight)
        {
            SvgRenderer = renderer,
        };
        SetField(box, "Context", context);
        SetField(box, "_svgRendererIdentity", GetRendererIdentity(renderer));
        SetField(box, "_svgProviderCacheGeneration", renderer.CacheGeneration);
        SetField(box, "_svgInfo", document.Info);
        return renderer;
    }

    private static object CreateWorkOwner(
        ImageBox.SvgTileRenderPlan plan,
        CancellationTokenSource cancellation,
        int state = 0)
    {
        Type ownerType = typeof(ImageBox).GetNestedType("SvgTileWorkOwner", BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException("Missing ImageBox SVG work-owner type.");
        object owner = Activator.CreateInstance(
                ownerType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [plan, cancellation, plan.Demand],
                culture: null)
            ?? throw new Xunit.Sdk.XunitException("Could not create ImageBox SVG work owner.");
        SetField(owner, "State", state);
        return owner;
    }

    private static object RegisterWorkOwner(
        ImageBox box,
        ImageBox.SvgTileRenderPlan plan,
        (int X, int Y, int Width, int Height) key,
        CancellationTokenSource cancellation,
        int state = 0)
    {
        object owner = CreateWorkOwner(plan, cancellation, state);
        GetField<Dictionary<(int X, int Y, int Width, int Height), CancellationTokenSource>>(
            box,
            "_svgTilesInFlight").Add(key, cancellation);
        GetWorkOwnerMap(box).Add(key, owner);
        return owner;
    }

    private static IDictionary CreateWorkOwnerMap() =>
        (IDictionary)(Activator.CreateInstance(
            typeof(Dictionary<,>).MakeGenericType(
                typeof(ValueTuple<int, int, int, int>),
                typeof(ImageBox).GetNestedType("SvgTileWorkOwner", BindingFlags.NonPublic)!))
            ?? throw new Xunit.Sdk.XunitException("Could not create ImageBox SVG work-owner map."));

    private static IDictionary GetWorkOwnerMap(ImageBox box) =>
        (IDictionary)(typeof(ImageBox).GetField("_svgTileWorkOwners", InstanceFields)?.GetValue(box)
            ?? throw new Xunit.Sdk.XunitException("Missing ImageBox SVG work-owner map."));

    private static bool TryInvalidateFailedTile(
        ImageBox box,
        ImageBox.SvgTileRenderPlan plan,
        (int X, int Y, int Width, int Height) key,
        object workOwner,
        object? transitionToken = null)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
                "TryInvalidateFailedSvgTile",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ImageBox).FullName, "TryInvalidateFailedSvgTile");
        return (bool)method.Invoke(box, [plan, key, workOwner, transitionToken])!;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "IsCurrentSvgTileWorkOwnerNoLock")]
    private static extern bool IsCurrentTileWorkOwner(
        ImageBox box,
        ImageBox.SvgTileRenderPlan plan,
        (int X, int Y, int Width, int Height) key,
        CancellationTokenSource workCancellation);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "IsCurrentSvgTilePlan")]
    private static extern bool IsCurrentTilePlan(
        ImageBox box,
        ImageBox.SvgTileRenderPlan plan);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "QueueSvgTileRange")]
    private static extern int Queue(
        ImageBox box,
        ImageBox.SvgTileRenderPlan plan,
        int firstTileX,
        int firstTileY,
        int lastTileX,
        int lastTileY,
        int capacity,
        MarkdownSvgRenderPriority priority);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "QueueVisibleSvgTiles")]
    private static extern void QueueVisible(ImageBox box, Rect destination, Rect viewport);

    private static long GetRendererIdentity(IMarkdownSvgRenderer renderer)
    {
        MethodInfo method = typeof(ImageBox).GetMethod(
                "GetSvgRendererIdentity",
                BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(ImageBox).FullName, "GetSvgRendererIdentity");
        return (long)method.Invoke(null, [renderer])!;
    }

    private static void AssertPlanMember<T>(ImageBox.SvgTileRenderPlan plan, string memberName, T expected)
    {
        FieldInfo field = FindPlanField(memberName);
        Assert.True(field.IsInitOnly, $"Plan member '{memberName}' must be readonly.");
        Assert.Equal(expected, (T)field.GetValue(plan)!);
    }

    private static T PlanMember<T>(ImageBox.SvgTileRenderPlan plan, string memberName)
        => (T)FindPlanField(memberName).GetValue(plan)!;

    private static FieldInfo FindPlanField(string memberName)
        => typeof(ImageBox.SvgTileRenderPlan)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .SingleOrDefault(candidate =>
                candidate.Name.TrimStart('_').Equals(memberName, StringComparison.OrdinalIgnoreCase) ||
                candidate.Name.Equals($"<{memberName}>k__BackingField", StringComparison.Ordinal))
            ?? throw new Xunit.Sdk.XunitException($"Immutable plan has no stored '{memberName}' field.");

    private static T GetField<T>(object target, string name)
        => (T)(typeof(ImageBox).GetField(name, InstanceFields)?.GetValue(target)
            ?? throw new Xunit.Sdk.XunitException($"Missing or null ImageBox field '{name}'."));

    private static T? GetNullableField<T>(object target, string name)
        => (T?)typeof(ImageBox).GetField(name, InstanceFields)?.GetValue(target);

    private static void SetField(object target, string name, object? value)
    {
        FieldInfo field = target.GetType().GetField(name, InstanceFields)
            ?? throw new Xunit.Sdk.XunitException($"Missing field '{name}' on '{target.GetType().Name}'.");
        field.SetValue(target, value);
    }

    private static string GetMethodBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find method '{signature}'.");
        int bodyStart = source.IndexOf('{', member + signature.Length);
        Assert.True(bodyStart >= 0, $"Could not find a body for '{signature}'.");

        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        throw new InvalidOperationException($"Could not find the end of '{signature}'.");
    }

    private static string GetBlockBody(string source, int blockStart)
    {
        Assert.True(blockStart >= 0 && blockStart < source.Length, "Could not find the block start.");
        int bodyStart = source.IndexOf('{', blockStart);
        Assert.True(bodyStart >= 0, "Could not find a braced block body.");

        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        throw new InvalidOperationException("Could not find the end of the braced block.");
    }

    private static string GetExpressionBody(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find method '{signature}'.");
        int expressionStart = FindCodeToken(source, member + signature.Length, "=>");
        Assert.True(expressionStart >= 0, $"Could not find an expression body for '{signature}'.");
        int expressionEnd = FindCodeToken(source, expressionStart + 2, ";");
        Assert.True(expressionEnd >= 0, $"Could not find the end of expression body '{signature}'.");
        return source[(expressionStart + 2)..expressionEnd];
    }

    [Fact]
    public void ExpressionBodyExtractionIgnoresSemicolonsInCommentsAndLiterals()
    {
        const string source = """
            private bool IsCurrentSvgTilePlan(object plan) => // ; line-comment terminator is ignored
                "semi;colon".Length == 10 /* ; block-comment terminator is ignored */ &&
                !_resourcesSuspended;
            """;

        string expression = GetExpressionBody(source, "private bool IsCurrentSvgTilePlan(");

        Assert.Contains("\"semi;colon\".Length == 10", expression, StringComparison.Ordinal);
        Assert.Contains("// ; line-comment terminator is ignored", expression, StringComparison.Ordinal);
        Assert.Contains("/* ; block-comment terminator is ignored */", expression, StringComparison.Ordinal);
        Assert.EndsWith("!_resourcesSuspended", expression.Trim(), StringComparison.Ordinal);
    }

    private static int FindCodeToken(string source, int start, string token)
    {
        for (int index = start; index <= source.Length - token.Length;)
        {
            if (source[index] == '/' && index + 1 < source.Length)
            {
                if (source[index + 1] == '/')
                {
                    index += 2;
                    while (index < source.Length && source[index] is not '\r' and not '\n')
                        index++;
                    continue;
                }

                if (source[index + 1] == '*')
                {
                    int commentEnd = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                    if (commentEnd < 0)
                        return -1;

                    index = commentEnd + 2;
                    continue;
                }
            }

            if (source[index] is '"' or '\'')
            {
                index = SkipQuotedLiteral(source, index);
                if (index < 0)
                    return -1;
                continue;
            }

            if (source.AsSpan(index).StartsWith(token, StringComparison.Ordinal))
                return index;

            index++;
        }

        return -1;
    }

    private static int SkipQuotedLiteral(string source, int quoteIndex)
    {
        char quote = source[quoteIndex];
        if (quote == '\'')
            return SkipEscapedLiteral(source, quoteIndex + 1, quote);

        int quoteCount = 1;
        while (quoteIndex + quoteCount < source.Length && source[quoteIndex + quoteCount] == '"')
            quoteCount++;
        if (quoteCount >= 3)
        {
            int contentStart = quoteIndex + quoteCount;
            for (int index = contentStart; index <= source.Length - quoteCount; index++)
            {
                if (source[index] != '"')
                    continue;

                int closingCount = 1;
                while (index + closingCount < source.Length && source[index + closingCount] == '"')
                    closingCount++;
                if (closingCount >= quoteCount)
                    return index + quoteCount;
                index += closingCount - 1;
            }

            return -1;
        }

        bool verbatim = quoteIndex > 0 && source[quoteIndex - 1] == '@' ||
            quoteIndex > 1 && source[quoteIndex - 1] == '$' && source[quoteIndex - 2] == '@';
        if (verbatim)
        {
            for (int index = quoteIndex + 1; index < source.Length; index++)
            {
                if (source[index] != '"')
                    continue;
                if (index + 1 < source.Length && source[index + 1] == '"')
                {
                    index++;
                    continue;
                }

                return index + 1;
            }

            return -1;
        }

        return SkipEscapedLiteral(source, quoteIndex + 1, quote);
    }

    private static int SkipEscapedLiteral(string source, int index, char quote)
    {
        while (index < source.Length)
        {
            if (source[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (source[index++] == quote)
                return index;
        }

        return -1;
    }

    private static string[] GetInvocations(string source, string invocationPrefix)
    {
        var invocations = new List<string>();
        int searchFrom = 0;
        while (true)
        {
            int start = source.IndexOf(invocationPrefix, searchFrom, StringComparison.Ordinal);
            if (start < 0)
                return invocations.ToArray();

            int open = source.IndexOf('(', start + invocationPrefix.Length - 1);
            Assert.True(open >= 0, $"Could not find invocation start after '{invocationPrefix}'.");
            int depth = 0;
            for (int index = open; index < source.Length; index++)
            {
                if (source[index] == '(')
                    depth++;
                else if (source[index] == ')' && --depth == 0)
                {
                    invocations.Add(source[start..(index + 1)]);
                    searchFrom = index + 1;
                    break;
                }
            }

            Assert.True(searchFrom > start, $"Could not find the end of invocation '{invocationPrefix}'.");
        }
    }

    private static string ReadRepositorySource(params string[] pathParts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = directory.FullName;
            foreach (string part in pathParts)
                path = Path.Combine(path, part);
            if (File.Exists(path))
                return File.ReadAllText(path);
        }

        throw new FileNotFoundException($"Could not locate repository source '{Path.Combine(pathParts)}'.");
    }

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
