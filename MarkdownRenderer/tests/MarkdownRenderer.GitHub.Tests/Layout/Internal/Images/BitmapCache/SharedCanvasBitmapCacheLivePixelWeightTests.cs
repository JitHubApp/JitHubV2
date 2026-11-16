using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Windows.Foundation;
using Xunit;
using MarkdownRenderer.Layout.Internal.Images.BitmapCache;
using MarkdownRenderer.Layout.Internal.Images.BitmapOutput;
using MarkdownRenderer.Layout.Internal.RenderResources;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.BitmapCache;

/// <remarks>
/// This file is longer than 500 lines because every case asserts the same two
/// ledgers, the bitmap output charge and the live pixel weight, across all
/// <see cref="SharedCanvasBitmapCache"/> lease factories, and they share the
/// reservation, bitmap and precommit-failure scenario helpers at the end of
/// the class.
/// </remarks>
[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class SharedCanvasBitmapCacheLivePixelWeightTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public void CommittedBitmapOutputReservationTransfersOnceAcrossAllCacheFactories()
    {
        using var device = new CanvasDevice();
        long baseline = _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes;
        string identity = $"admitted-factory-{Guid.NewGuid():N}";

        for (int factory = 0; factory < 4; factory++)
        {
            int activeBaseline = _renderResources.Images.BitmapOutput.GetSnapshot().ActiveReservationCount;
            BitmapOutputAdmission.Reservation reservation = CreateCommittedReservation(16);
            CanvasBitmap? bitmap = CreateBitmap(device, width: 2, height: 1);
            SharedCanvasBitmapCache.Lease? owner = null;
            SharedCanvasBitmapCache.Lease? clone = null;
            try
            {
                owner = CreateAdmittedLease(factory, device, identity, bitmap, reservation);
                bitmap = null; // Lease now owns the native bitmap.

                Assert.True(reservation.IsTransferred);
                Assert.Equal(8L, reservation.Bytes);
                Assert.True(owner.IsBitmapOutputAdmitted);
                Assert.Equal(8L, owner.BitmapOutputAdmissionBytes);
                Assert.Equal(baseline + 8, _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes);
                Assert.Equal(activeBaseline + 1, _renderResources.Images.BitmapOutput.GetSnapshot().ActiveReservationCount);

                clone = owner.TryClone();
                Assert.NotNull(clone);
                Assert.True(clone.IsBitmapOutputAdmitted);
                Assert.Equal(8L, clone.BitmapOutputAdmissionBytes);
                Assert.Equal(baseline + 8, _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes);

                // Cached factories lose only their cache reference here; the
                // clone remains the final native owner in every case.
                _renderResources.Images.BitmapCache.ReleaseDevice(device);
                owner.Dispose();
                owner = null;
                Assert.Equal(baseline + 8, _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes);

                clone.Dispose();
                clone = null;
                Assert.Equal(baseline, _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes);
                Assert.Equal(activeBaseline, _renderResources.Images.BitmapOutput.GetSnapshot().ActiveReservationCount);
            }
            finally
            {
                _renderResources.Images.BitmapCache.ReleaseDevice(device);
                owner?.Dispose();
                clone?.Dispose();
                if (bitmap is not null)
                {
                    bitmap.Dispose();
                    if (reservation.IsCommitted && !reservation.IsTransferred)
                        reservation.ReleaseAfterSuccessfulCleanup();
                }
            }
        }
    }

    [Fact]
    public async Task LegacyBitmapEntryAdmissionIsAtomicAcrossClonedLeases()
    {
        using var device = new CanvasDevice();
        long baseline = _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes;
        var leases = new List<SharedCanvasBitmapCache.Lease>();
        SharedCanvasBitmapCache.Lease? owner = null;
        try
        {
            owner = _renderResources.Images.BitmapCache.CreateUncachedLease(
                CreateBitmap(device, width: 2, height: 1),
                pixelWidth: 2,
                pixelHeight: 1);
            for (int i = 0; i < 8; i++)
            {
                SharedCanvasBitmapCache.Lease? clone = owner.TryClone();
                Assert.NotNull(clone);
                leases.Add(clone);
            }

            bool[] results = await Task.WhenAll(leases.Select(lease => Task.Run(() =>
                lease.TryAdmitBitmapOutput(
                    isVisible: true,
                    isReplacement: false,
                    out _,
                    out _))));

            Assert.All(results, result => Assert.True(result));
            Assert.True(owner.IsBitmapOutputAdmitted);
            Assert.Equal(8L, owner.BitmapOutputAdmissionBytes);
            Assert.All(leases, lease => Assert.True(lease.IsBitmapOutputAdmitted));
            Assert.Equal(baseline + 8, _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes);

            owner.Dispose();
            owner = null;
            Assert.Equal(baseline + 8, _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes);
            SharedCanvasBitmapCache.Lease disposedLease = leases[0];
            disposedLease.Dispose();
            Assert.False(disposedLease.TryAdmitBitmapOutput(
                isVisible: true,
                isReplacement: false,
                out long requiredBytes,
                out _));
            Assert.Equal(0L, requiredBytes);
            leases.RemoveAt(0);
            foreach (SharedCanvasBitmapCache.Lease lease in leases)
                lease.Dispose();
            leases.Clear();
            Assert.Equal(baseline, _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes);
        }
        finally
        {
            owner?.Dispose();
            foreach (SharedCanvasBitmapCache.Lease lease in leases)
                lease.Dispose();
        }
    }

    [Fact]
    public void UndersizedBitmapReservationRemainsCallerOwnedUntilSuccessfulNativeCleanup()
    {
        using var device = new CanvasDevice();
        long baseline = _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes;
        BitmapOutputAdmission.Reservation reservation = CreateCommittedReservation(4);
        CanvasBitmap? bitmap = CreateBitmap(device, width: 2, height: 1);
            CanvasBitmap candidateBitmap = bitmap!;
        SharedCanvasBitmapCache.Lease? owner = null;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                owner = _renderResources.Images.BitmapCache.StoreSvgAndAcquire(
                    device,
                    $"undersized-admission-{Guid.NewGuid():N}",
                    candidateBitmap,
                    new Size(2, 1),
                    pixelWidth: 2,
                    pixelHeight: 1,
                    svgWorkId: 0,
                    svgSourceBytes: 0,
                    svgTargetPixels: 0,
                    bitmapReservation: reservation);
                bitmap = null;
            });

            Assert.NotNull(bitmap);
            Assert.Equal(2u, bitmap.SizeInPixels.Width);
            Assert.True(reservation.IsCommitted);
            Assert.False(reservation.IsTransferred);
            Assert.Equal(baseline + 4, _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes);

            bitmap.Dispose();
            bitmap = null;
            Assert.True(reservation.ReleaseAfterSuccessfulCleanup());
            Assert.Equal(baseline, _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes);
        }
        finally
        {
            owner?.Dispose();
            if (bitmap is not null)
            {
                bitmap.Dispose();
                reservation.ReleaseAfterSuccessfulCleanup();
            }
        }
    }

    [Fact]
    public async Task ReservationTokenCannotBeReusedByASecondBitmapFactory()
    {
        using var device = new CanvasDevice();
        long baseline = _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes;
        long livePixelBaseline = _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes;
        BitmapOutputAdmission.Reservation reservation = CreateCommittedReservation(16);
        CanvasBitmap? firstBitmap = CreateBitmap(device, width: 2, height: 1);
        CanvasBitmap? secondBitmap = CreateBitmap(device, width: 1, height: 1);
        SharedCanvasBitmapCache.Lease? firstLease = null;
        SharedCanvasBitmapCache.Lease? secondLease = null;
        try
        {
            string identity = $"single-reservation-owner-{Guid.NewGuid():N}";
            CanvasBitmap firstCandidate = firstBitmap!;
            CanvasBitmap secondCandidate = secondBitmap!;
            Task<(SharedCanvasBitmapCache.Lease? Lease, Exception? Error)> firstFactory = RunRacingFactory(
                reservation,
                () => _renderResources.Images.BitmapCache.StoreAndAcquire(
                    device, identity, firstCandidate, bitmapReservation: reservation));
            Task<(SharedCanvasBitmapCache.Lease? Lease, Exception? Error)> secondFactory = RunRacingFactory(
                reservation,
                () => _renderResources.Images.BitmapCache.CreateUncachedLease(
                    secondCandidate, pixelWidth: 1, pixelHeight: 1, bitmapReservation: reservation));

            (SharedCanvasBitmapCache.Lease? Lease, Exception? Error)[] results =
                await Task.WhenAll(firstFactory, secondFactory);
            firstLease = results[0].Lease;
            secondLease = results[1].Lease;
            if (firstLease is not null)
                firstBitmap = null;
            if (secondLease is not null)
                secondBitmap = null;
            Assert.Null(results[0].Error);
            Assert.Null(results[1].Error);
            Assert.Equal(1, (firstLease is null ? 0 : 1) + (secondLease is null ? 0 : 1));
            bool firstWon = firstLease is not null;

            long winningPixelWeight = firstWon ? 8L : 4L;
            SharedCanvasBitmapCache.Lease winner = firstLease ?? secondLease!;
            CanvasBitmap losingBitmap = firstWon ? secondBitmap! : firstBitmap!;
            uint losingWidth = firstWon ? 1u : 2u;

            Assert.True(reservation.IsTransferred);
            Assert.Equal(winningPixelWeight, reservation.Bytes);
            Assert.Equal(winningPixelWeight, winner.BitmapOutputAdmissionBytes);
            Assert.Equal(baseline + winningPixelWeight, _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes);
            Assert.Equal(livePixelBaseline + winningPixelWeight, _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes);
            Assert.Equal(losingWidth, losingBitmap.SizeInPixels.Width);

            losingBitmap.Dispose();
            if (firstWon)
                secondBitmap = null;
            else
                firstBitmap = null;
            _renderResources.Images.BitmapCache.ReleaseDevice(device);
            firstLease?.Dispose();
            firstLease = null;
            secondLease?.Dispose();
            secondLease = null;
            Assert.Equal(baseline, _renderResources.Images.BitmapOutput.GetSnapshot().ChargedBytes);
        }
        finally
        {
            _renderResources.Images.BitmapCache.ReleaseDevice(device);
            firstLease?.Dispose();
            secondLease?.Dispose();
            if (firstBitmap is not null)
            {
                firstBitmap.Dispose();
                if (reservation.IsCommitted && !reservation.IsTransferred)
                    reservation.ReleaseAfterSuccessfulCleanup();
            }
            secondBitmap?.Dispose();
        }
    }

    [Fact]
    public void PrecommitCacheInsertionFailureReturnsAdmittedUncachedLeasesForRasterAndSvg()
    {
        using var device = new CanvasDevice();
        var scenario = new PrecommitCacheFailureScenario(device);
        SharedCanvasBitmapCache cache = scenario.Cache;
        BitmapOutputAdmission admission = scenario.Admission;
        long baseline = admission.GetSnapshot().ChargedBytes;

        try
        {
            scenario.Incumbent = cache.StoreAndAcquire(
                device,
                scenario.IncumbentIdentity,
                CreateBitmap(device, width: 1, height: 1));
            long retainedWithIncumbent = cache.GetSnapshot().RetainedBytes;

            scenario.StoreRasterAndSvgWhileWeighingFails();
            AssertFailedInsertionsReturnedAdmittedUncachedLeases(scenario, retainedWithIncumbent);

            scenario.RasterClone = scenario.Raster!.TryClone();
            scenario.SvgClone = scenario.Svg!.TryClone();
            Assert.NotNull(scenario.RasterClone);
            Assert.NotNull(scenario.SvgClone);
            Assert.Equal(baseline + 16, admission.GetSnapshot().ChargedBytes);

            scenario.ReleaseCacheAndOriginalLeases();
            Assert.Equal(baseline + 16, admission.GetSnapshot().ChargedBytes);

            scenario.ReleaseClones();
            Assert.Equal(baseline, admission.GetSnapshot().ChargedBytes);
        }
        finally
        {
            scenario.CleanUp();
        }
    }

    private static void AssertFailedInsertionsReturnedAdmittedUncachedLeases(
        PrecommitCacheFailureScenario scenario,
        long retainedWithIncumbent)
    {
        SharedCanvasBitmapCache cache = scenario.Cache;
        CanvasDevice device = scenario.Device;
        Assert.True(scenario.RasterReservation!.IsTransferred);
        Assert.True(scenario.SvgReservation!.IsTransferred);
        Assert.NotNull(scenario.Raster);
        Assert.NotNull(scenario.Svg);
        Assert.True(scenario.Raster.IsBitmapOutputAdmitted);
        Assert.True(scenario.Svg.IsBitmapOutputAdmitted);
        Assert.Equal(8L, scenario.Raster.BitmapOutputAdmissionBytes);
        Assert.Equal(8L, scenario.Svg.BitmapOutputAdmissionBytes);
        Assert.Equal(retainedWithIncumbent, cache.GetSnapshot().RetainedBytes);
        Assert.False(cache.TryAcquire(device, scenario.RasterIdentity, out _));
        Assert.False(cache.TryAcquire(device, scenario.SvgIdentity, out _));
        Assert.NotNull(scenario.Incumbent);
        Assert.True(cache.TryAcquire(device, scenario.IncumbentIdentity, out scenario.IncumbentLookup));
        Assert.NotNull(scenario.IncumbentLookup);
        Assert.Same(scenario.Incumbent.Bitmap, scenario.IncumbentLookup!.Bitmap);
    }

    [Fact]
    public void CachedBitmapPixelWeightCountsOneEntryAcrossCloneEvictionAndFinalRelease()
    {
        using var device = new CanvasDevice();
        long before = _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes;
        SharedCanvasBitmapCache.Lease? owner = null;
        SharedCanvasBitmapCache.Lease? clone = null;
        try
        {
            owner = _renderResources.Images.BitmapCache.StoreAndAcquire(
                device,
                $"live-pixel-weight-{Guid.NewGuid():N}",
                CreateBitmap(device, width: 2, height: 1));
            clone = owner.TryClone();

            Assert.NotNull(clone);
            Assert.Equal(before + 8, _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes);

            // Removing the LRU reference must not remove the unique Entry
            // charge while either independent lease still owns that Entry.
            _renderResources.Images.BitmapCache.ReleaseDevice(device);
            Assert.Equal(before + 8, _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes);
            owner.Dispose();
            owner = null;
            Assert.Equal(before + 8, _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes);
        }
        finally
        {
            _renderResources.Images.BitmapCache.ReleaseDevice(device);
            owner?.Dispose();
            clone?.Dispose();
        }

        Assert.Equal(before, _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes);
    }

    [Fact]
    public void UncachedBitmapPixelWeightCountsOneEntryAcrossClones()
    {
        using var device = new CanvasDevice();
        long before = _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes;
        long cacheBefore = _renderResources.Images.BitmapCache.GetSnapshot().RetainedBytes;
        SharedCanvasBitmapCache.Lease? owner = null;
        SharedCanvasBitmapCache.Lease? clone = null;
        try
        {
            owner = _renderResources.Images.BitmapCache.CreateUncachedLease(
                CreateBitmap(device, width: 2, height: 1),
                pixelWidth: 2,
                pixelHeight: 1);
            clone = owner.TryClone();

            Assert.NotNull(clone);
            Assert.Equal(before + 8, _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes);
            Assert.Equal(cacheBefore, _renderResources.Images.BitmapCache.GetSnapshot().RetainedBytes);

            owner.Dispose();
            owner = null;
            Assert.Equal(before + 8, _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes);
            Assert.Equal(2u, clone.Bitmap.SizeInPixels.Width);
        }
        finally
        {
            owner?.Dispose();
            clone?.Dispose();
        }

        Assert.Equal(before, _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes);
        Assert.Equal(cacheBefore, _renderResources.Images.BitmapCache.GetSnapshot().RetainedBytes);
    }

    [Fact]
    public void UncachedKnownDimensionsRejectInvalidValuesBeforeTakingOwnership()
    {
        using var device = new CanvasDevice();
        using var bitmap = CreateBitmap(device, width: 2, height: 1);
        long before = _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _renderResources.Images.BitmapCache.CreateUncachedLease(bitmap, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _renderResources.Images.BitmapCache.CreateUncachedLease(bitmap, 2, -1));
        Assert.Equal(before, _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes);
        Assert.Equal(2u, bitmap.SizeInPixels.Width);
    }

    [Fact]
    public void AbandonedUnpublishedOwnerRestoresAccountingWithoutConsumingTheBitmap()
    {
        using var device = new CanvasDevice();
        using var bitmap = CreateBitmap(device, width: 2, height: 1);
        long before = _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes;
        var lease = new SharedCanvasBitmapCache.Lease();
        var entry = new SharedCanvasBitmapCacheEntry(_renderResources.Images.BitmapCache, bitmap, 2, 1);
        lease.Attach(entry);
        entry.AddCacheReference();
        Assert.Equal(before + 8, _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes);
        lease.AbandonUnpublishedOwnership();
        lease.AbandonUnpublishedOwnership();
        lease.Dispose();
        Assert.Equal(before, _renderResources.Images.BitmapCache.GetSnapshot().LivePixelWeightBytes);
        Assert.Equal(2u, bitmap.SizeInPixels.Width);
        Assert.False(entry.TryAcquire());
    }

    [Fact]
    public void UncachedDisplaySizedBitmapPreservesOriginalIntrinsicGeometry()
    {
        using var device = new CanvasDevice();
        using SharedCanvasBitmapCache.Lease lease = _renderResources.Images.BitmapCache.CreateUncachedLease(
            CreateBitmap(device, width: 2, height: 1), 2, 1, new Size(800, 400));
        Assert.Equal(new Size(800, 400), lease.IntrinsicSize);
        Assert.Equal(2u, lease.Bitmap.SizeInPixels.Width);
        Assert.True(lease.IsWithinPixelBudget(2));
    }

    [Fact]
    public void FailedBitmapDisposeRetainsChargeAsUncertainWithoutRetainingTheResource()
    {
        var ledger = new LiveBitmapPixelWeightLedger();
        const long weight = 64;
        ledger.Register(weight);
        var failure = new InvalidOperationException("unknown native disposal state");
        var resource = new ThrowingDisposable(failure);
        int referenceCount = 1;

        Assert.Same(
            failure,
            Assert.Throws<InvalidOperationException>(() =>
                ledger.ReleaseFinalReference(ref referenceCount, resource, weight)));

        Assert.Equal(0, referenceCount);
        Assert.Equal(weight, ledger.LivePixelWeightBytes);
        Assert.Equal(weight, ledger.UncertainDisposePixelWeightBytes);
        Assert.Equal(1, resource.DisposeCount);
    }

    private SharedCanvasBitmapCache.Lease CreateAdmittedLease(
        int factory,
        CanvasDevice device,
        string identity,
        CanvasBitmap bitmap,
        BitmapOutputAdmission.Reservation reservation) => factory switch
    {
        0 => _renderResources.Images.BitmapCache.StoreAndAcquire(
            device,
            identity + "-raster",
            bitmap,
            new Size(4, 2),
            bitmapReservation: reservation),
        1 => _renderResources.Images.BitmapCache.StoreSvgAndAcquire(
            device,
            identity + "-svg",
            bitmap,
            new Size(4, 2),
            pixelWidth: 2,
            pixelHeight: 1,
            svgWorkId: 0,
            svgSourceBytes: 0,
            svgTargetPixels: 0,
            bitmapReservation: reservation),
        2 => _renderResources.Images.BitmapCache.CreateUncachedLease(
            bitmap,
            bitmapReservation: reservation),
        _ => _renderResources.Images.BitmapCache.CreateUncachedLease(
            bitmap,
            pixelWidth: 2,
            pixelHeight: 1,
            intrinsicSize: new Size(4, 2),
            bitmapReservation: reservation),
    };

    // A factory that loses the race for an already-transferred reservation
    // reports no lease and no error; any other exception is the test failure.
    private static Task<(SharedCanvasBitmapCache.Lease? Lease, Exception? Error)> RunRacingFactory(
        BitmapOutputAdmission.Reservation reservation,
        Func<SharedCanvasBitmapCache.Lease> factory) =>
        Task.Run<(SharedCanvasBitmapCache.Lease? Lease, Exception? Error)>(() =>
        {
            try
            {
                return (factory(), null);
            }
            catch (InvalidOperationException) when (reservation.IsTransferred)
            {
                return (null, null);
            }
            catch (Exception exception)
            {
                return (null, exception);
            }
        });

    private BitmapOutputAdmission.Reservation CreateCommittedReservation(long bytes) =>
        CreateCommittedReservation(_renderResources.Images.BitmapOutput, bytes);

    private static BitmapOutputAdmission.Reservation CreateCommittedReservation(
        BitmapOutputAdmission admission, long bytes)
    {
        if (!admission.TryReserve(
                bytes,
                isVisible: true,
                isReplacement: true,
                out BitmapOutputAdmission.Reservation? reservation,
                out _) || reservation is null)
            throw new InvalidOperationException("The test could not reserve bitmap output capacity.");

        try
        {
            if (!reservation.Commit())
                throw new InvalidOperationException("The test reservation could not be committed.");
            return reservation;
        }
        catch
        {
            reservation.Dispose();
            throw;
        }
    }

    private static CanvasBitmap CreateBitmap(CanvasDevice device, int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        return CanvasBitmap.CreateFromBytes(
            device,
            pixels,
            width,
            height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
    }

    /// <summary>
    /// The cache, leases, reservations and bitmaps of the precommit cache
    /// insertion failure case. Each nullable slot is cleared as soon as its
    /// owner is released so <see cref="CleanUp"/> disposes only what is left.
    /// </summary>
    private sealed class PrecommitCacheFailureScenario
    {
        private bool _failWeighing;

        public PrecommitCacheFailureScenario(CanvasDevice device)
        {
            Device = device;
            Admission = BitmapOutputAdmission.CreateDefault();
            Cache = new SharedCanvasBitmapCache(
                Admission,
                SharedCanvasBitmapCache.DefaultBudgetBytes,
                entry => _failWeighing
                    ? throw new InvalidOperationException("Injected precommit cache weighing failure.")
                    : entry.WeightBytes);
        }

        public CanvasDevice Device { get; }
        public BitmapOutputAdmission Admission { get; }
        public SharedCanvasBitmapCache Cache { get; }
        public string IncumbentIdentity { get; } = $"cache-failure-incumbent-{Guid.NewGuid():N}";
        public string RasterIdentity { get; } = $"cache-failure-raster-{Guid.NewGuid():N}";
        public string SvgIdentity { get; } = $"cache-failure-svg-{Guid.NewGuid():N}";

        public SharedCanvasBitmapCache.Lease? Incumbent;
        public SharedCanvasBitmapCache.Lease? IncumbentLookup;
        public SharedCanvasBitmapCache.Lease? Raster;
        public SharedCanvasBitmapCache.Lease? RasterClone;
        public SharedCanvasBitmapCache.Lease? Svg;
        public SharedCanvasBitmapCache.Lease? SvgClone;
        public BitmapOutputAdmission.Reservation? RasterReservation;
        public BitmapOutputAdmission.Reservation? SvgReservation;
        public CanvasBitmap? RasterBitmap;
        public CanvasBitmap? SvgBitmap;

        public void StoreRasterAndSvgWhileWeighingFails()
        {
            RasterReservation = CreateCommittedReservation(Admission, 16);
            SvgReservation = CreateCommittedReservation(Admission, 16);
            RasterBitmap = CreateBitmap(Device, width: 2, height: 1);
            SvgBitmap = CreateBitmap(Device, width: 2, height: 1);
            _failWeighing = true;
            try
            {
                Raster = Cache.StoreAndAcquire(
                    Device,
                    RasterIdentity,
                    RasterBitmap,
                    bitmapReservation: RasterReservation);
                RasterBitmap = null;
                Svg = Cache.StoreSvgAndAcquire(
                    Device,
                    SvgIdentity,
                    SvgBitmap,
                    intrinsicSize: new Size(2, 1),
                    pixelWidth: 2,
                    pixelHeight: 1,
                    svgWorkId: 0,
                    svgSourceBytes: 0,
                    svgTargetPixels: 0,
                    bitmapReservation: SvgReservation);
                SvgBitmap = null;
            }
            finally
            {
                // Restore the normal selector before any cleanup or asserts
                // that might themselves consult it.
                _failWeighing = false;
            }
        }

        public void ReleaseCacheAndOriginalLeases()
        {
            Cache.ReleaseDevice(Device);
            IncumbentLookup!.Dispose();
            IncumbentLookup = null;
            Incumbent!.Dispose();
            Incumbent = null;
            Raster!.Dispose();
            Raster = null;
            Svg!.Dispose();
            Svg = null;
        }

        public void ReleaseClones()
        {
            RasterClone!.Dispose();
            RasterClone = null;
            SvgClone!.Dispose();
            SvgClone = null;
        }

        public void CleanUp()
        {
            _failWeighing = false;
            Cache.ReleaseDevice(Device);
            IncumbentLookup?.Dispose();
            Incumbent?.Dispose();
            Raster?.Dispose();
            RasterClone?.Dispose();
            Svg?.Dispose();
            SvgClone?.Dispose();
            if (RasterBitmap is not null)
            {
                RasterBitmap.Dispose();
                if (RasterReservation is { IsCommitted: true, IsTransferred: false })
                    RasterReservation.ReleaseAfterSuccessfulCleanup();
            }
            if (SvgBitmap is not null)
            {
                SvgBitmap.Dispose();
                if (SvgReservation is { IsCommitted: true, IsTransferred: false })
                    SvgReservation.ReleaseAfterSuccessfulCleanup();
            }
        }
    }

    private sealed class ThrowingDisposable(Exception failure) : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            throw failure;
        }
    }
}
