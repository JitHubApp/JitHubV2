using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using MarkdownRenderer.Layout.Boxes;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class SharedCanvasBitmapCacheLivePixelWeightTests
{
    [Fact]
    public void CommittedBitmapOutputReservationTransfersOnceAcrossAllCacheFactories()
    {
        using var device = new CanvasDevice();
        long baseline = BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes;
        string identity = $"admitted-factory-{Guid.NewGuid():N}";

        for (int factory = 0; factory < 4; factory++)
        {
            int activeBaseline = BitmapOutputAdmission.Shared.GetSnapshot().ActiveReservationCount;
            BitmapOutputAdmission.Reservation reservation = CreateCommittedReservation(16);
            CanvasBitmap? bitmap = CreateBitmap(device, width: 2, height: 1);
            SharedCanvasBitmapCache.Lease? owner = null;
            SharedCanvasBitmapCache.Lease? clone = null;
            try
            {
                owner = factory switch
                {
                    0 => SharedCanvasBitmapCache.StoreAndAcquire(
                        device,
                        identity + "-raster",
                        bitmap,
                        new Size(4, 2),
                        bitmapReservation: reservation),
                    1 => SharedCanvasBitmapCache.StoreSvgAndAcquire(
                        device,
                        identity + "-svg",
                        bitmap,
                        new Size(4, 2),
                        pixelWidth: 2,
                        pixelHeight: 1,
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                        svgWorkId: 0,
                        svgSourceBytes: 0,
                        svgTargetPixels: 0,
#endif
                        bitmapReservation: reservation),
                    2 => SharedCanvasBitmapCache.CreateUncachedLease(
                        bitmap,
                        bitmapReservation: reservation),
                    _ => SharedCanvasBitmapCache.CreateUncachedLease(
                        bitmap,
                        pixelWidth: 2,
                        pixelHeight: 1,
                        intrinsicSize: new Size(4, 2),
                        bitmapReservation: reservation),
                };
                bitmap = null; // Lease now owns the native bitmap.

                Assert.True(reservation.IsTransferred);
                Assert.Equal(8L, reservation.Bytes);
                Assert.True(owner.IsBitmapOutputAdmitted);
                Assert.Equal(8L, owner.BitmapOutputAdmissionBytes);
                Assert.Equal(baseline + 8, BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes);
                Assert.Equal(activeBaseline + 1, BitmapOutputAdmission.Shared.GetSnapshot().ActiveReservationCount);

                clone = owner.TryClone();
                Assert.NotNull(clone);
                Assert.True(clone.IsBitmapOutputAdmitted);
                Assert.Equal(8L, clone.BitmapOutputAdmissionBytes);
                Assert.Equal(baseline + 8, BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes);

                // Cached factories lose only their cache reference here; the
                // clone remains the final native owner in every case.
                SharedCanvasBitmapCache.ReleaseDevice(device);
                owner.Dispose();
                owner = null;
                Assert.Equal(baseline + 8, BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes);

                clone.Dispose();
                clone = null;
                Assert.Equal(baseline, BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes);
                Assert.Equal(activeBaseline, BitmapOutputAdmission.Shared.GetSnapshot().ActiveReservationCount);
            }
            finally
            {
                SharedCanvasBitmapCache.ReleaseDevice(device);
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
        long baseline = BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes;
        var leases = new List<SharedCanvasBitmapCache.Lease>();
        SharedCanvasBitmapCache.Lease? owner = null;
        try
        {
            owner = SharedCanvasBitmapCache.CreateUncachedLease(
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
            Assert.Equal(baseline + 8, BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes);

            owner.Dispose();
            owner = null;
            Assert.Equal(baseline + 8, BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes);
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
            Assert.Equal(baseline, BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes);
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
        long baseline = BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes;
        BitmapOutputAdmission.Reservation reservation = CreateCommittedReservation(4);
        CanvasBitmap? bitmap = CreateBitmap(device, width: 2, height: 1);
            CanvasBitmap candidateBitmap = bitmap!;
        SharedCanvasBitmapCache.Lease? owner = null;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
            {
                owner = SharedCanvasBitmapCache.StoreSvgAndAcquire(
                    device,
                    $"undersized-admission-{Guid.NewGuid():N}",
                    candidateBitmap,
                    new Size(2, 1),
                    pixelWidth: 2,
                    pixelHeight: 1,
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    svgWorkId: 0,
                    svgSourceBytes: 0,
                    svgTargetPixels: 0,
#endif
                    bitmapReservation: reservation);
                bitmap = null;
            });

            Assert.NotNull(bitmap);
            Assert.Equal(2u, bitmap.SizeInPixels.Width);
            Assert.True(reservation.IsCommitted);
            Assert.False(reservation.IsTransferred);
            Assert.Equal(baseline + 4, BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes);

            bitmap.Dispose();
            bitmap = null;
            Assert.True(reservation.ReleaseAfterSuccessfulCleanup());
            Assert.Equal(baseline, BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes);
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
        long baseline = BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes;
        long livePixelBaseline = SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests;
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
            Task<(SharedCanvasBitmapCache.Lease? Lease, Exception? Error)> firstFactory = Task.Run<
                (SharedCanvasBitmapCache.Lease? Lease, Exception? Error)>(() =>
            {
                try
                {
                    return (SharedCanvasBitmapCache.StoreAndAcquire(
                        device,
                        identity,
                        firstCandidate,
                        bitmapReservation: reservation), null);
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
            Task<(SharedCanvasBitmapCache.Lease? Lease, Exception? Error)> secondFactory = Task.Run<
                (SharedCanvasBitmapCache.Lease? Lease, Exception? Error)>(() =>
            {
                try
                {
                    return (SharedCanvasBitmapCache.CreateUncachedLease(
                        secondCandidate,
                        pixelWidth: 1,
                        pixelHeight: 1,
                        bitmapReservation: reservation), null);
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
            Assert.Equal(baseline + winningPixelWeight, BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes);
            Assert.Equal(livePixelBaseline + winningPixelWeight, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
            Assert.Equal(losingWidth, losingBitmap.SizeInPixels.Width);

            losingBitmap.Dispose();
            if (firstWon)
                secondBitmap = null;
            else
                firstBitmap = null;
            SharedCanvasBitmapCache.ReleaseDevice(device);
            firstLease?.Dispose();
            firstLease = null;
            secondLease?.Dispose();
            secondLease = null;
            Assert.Equal(baseline, BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes);
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
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
        long baseline = BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes;
        string incumbentIdentity = $"cache-failure-incumbent-{Guid.NewGuid():N}";
        string rasterIdentity = $"cache-failure-raster-{Guid.NewGuid():N}";
        string svgIdentity = $"cache-failure-svg-{Guid.NewGuid():N}";
        SharedCanvasBitmapCache.Lease? incumbent = null;
        SharedCanvasBitmapCache.Lease? incumbentLookup = null;
        SharedCanvasBitmapCache.Lease? raster = null;
        SharedCanvasBitmapCache.Lease? rasterClone = null;
        SharedCanvasBitmapCache.Lease? svg = null;
        SharedCanvasBitmapCache.Lease? svgClone = null;
        BitmapOutputAdmission.Reservation? rasterReservation = null;
        BitmapOutputAdmission.Reservation? svgReservation = null;
        CanvasBitmap? rasterBitmap = null;
        CanvasBitmap? svgBitmap = null;

        FieldInfo cacheField = typeof(SharedCanvasBitmapCache).GetField(
            "Cache",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        object cache = cacheField.GetValue(null)!;
        FieldInfo weightSelectorField = cache.GetType().GetField(
            "_weightSelector",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        object originalWeightSelector = weightSelectorField.GetValue(cache)!;

        try
        {
            incumbent = SharedCanvasBitmapCache.StoreAndAcquire(
                device,
                incumbentIdentity,
                CreateBitmap(device, width: 1, height: 1));
            long retainedWithIncumbent = SharedCanvasBitmapCache.RetainedBytesForTests;

            rasterReservation = CreateCommittedReservation(16);
            svgReservation = CreateCommittedReservation(16);
            rasterBitmap = CreateBitmap(device, width: 2, height: 1);
            svgBitmap = CreateBitmap(device, width: 2, height: 1);
            weightSelectorField.SetValue(
                cache,
                new Func<SharedCanvasBitmapCache.Entry, long>(static _ =>
                    throw new InvalidOperationException("Injected precommit cache weighing failure.")));
            try
            {
                raster = SharedCanvasBitmapCache.StoreAndAcquire(
                    device,
                    rasterIdentity,
                    rasterBitmap,
                    bitmapReservation: rasterReservation);
                rasterBitmap = null;
                svg = SharedCanvasBitmapCache.StoreSvgAndAcquire(
                    device,
                    svgIdentity,
                    svgBitmap,
                    intrinsicSize: new Size(2, 1),
                    pixelWidth: 2,
                    pixelHeight: 1,
#if MARKDOWN_LAYOUT_DIAGNOSTICS
                    svgWorkId: 0,
                    svgSourceBytes: 0,
                    svgTargetPixels: 0,
#endif
                    bitmapReservation: svgReservation);
                svgBitmap = null;
            }
            finally
            {
                // Restore the process-wide cache before any cleanup or asserts
                // that might themselves consult its normal value selector.
                weightSelectorField.SetValue(cache, originalWeightSelector);
            }

            Assert.True(rasterReservation.IsTransferred);
            Assert.True(svgReservation.IsTransferred);
            Assert.NotNull(raster);
            Assert.NotNull(svg);
            Assert.True(raster.IsBitmapOutputAdmitted);
            Assert.True(svg.IsBitmapOutputAdmitted);
            Assert.Equal(8L, raster.BitmapOutputAdmissionBytes);
            Assert.Equal(8L, svg.BitmapOutputAdmissionBytes);
            Assert.Equal(retainedWithIncumbent, SharedCanvasBitmapCache.RetainedBytesForTests);
            Assert.False(SharedCanvasBitmapCache.TryAcquire(device, rasterIdentity, out _));
            Assert.False(SharedCanvasBitmapCache.TryAcquire(device, svgIdentity, out _));
            Assert.NotNull(incumbent);
            Assert.True(SharedCanvasBitmapCache.TryAcquire(device, incumbentIdentity, out incumbentLookup));
            Assert.NotNull(incumbentLookup);
            Assert.Same(incumbent.Bitmap, incumbentLookup!.Bitmap);

            rasterClone = raster.TryClone();
            svgClone = svg.TryClone();
            Assert.NotNull(rasterClone);
            Assert.NotNull(svgClone);
            Assert.Equal(baseline + 16, BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes);

            SharedCanvasBitmapCache.ReleaseDevice(device);
            incumbentLookup.Dispose();
            incumbentLookup = null;
            incumbent.Dispose();
            incumbent = null;
            raster.Dispose();
            raster = null;
            svg.Dispose();
            svg = null;
            Assert.Equal(baseline + 16, BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes);

            rasterClone.Dispose();
            rasterClone = null;
            svgClone.Dispose();
            svgClone = null;
            Assert.Equal(baseline, BitmapOutputAdmission.Shared.GetSnapshot().ChargedBytes);
        }
        finally
        {
            weightSelectorField.SetValue(cache, originalWeightSelector);
            SharedCanvasBitmapCache.ReleaseDevice(device);
            incumbentLookup?.Dispose();
            incumbent?.Dispose();
            raster?.Dispose();
            rasterClone?.Dispose();
            svg?.Dispose();
            svgClone?.Dispose();
            if (rasterBitmap is not null)
            {
                rasterBitmap.Dispose();
                if (rasterReservation is { IsCommitted: true, IsTransferred: false })
                    rasterReservation.ReleaseAfterSuccessfulCleanup();
            }
            if (svgBitmap is not null)
            {
                svgBitmap.Dispose();
                if (svgReservation is { IsCommitted: true, IsTransferred: false })
                    svgReservation.ReleaseAfterSuccessfulCleanup();
            }
        }
    }

    [Fact]
    public void CachedBitmapPixelWeightCountsOneEntryAcrossCloneEvictionAndFinalRelease()
    {
        using var device = new CanvasDevice();
        long before = SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests;
        SharedCanvasBitmapCache.Lease? owner = null;
        SharedCanvasBitmapCache.Lease? clone = null;
        try
        {
            owner = SharedCanvasBitmapCache.StoreAndAcquire(
                device,
                $"live-pixel-weight-{Guid.NewGuid():N}",
                CreateBitmap(device, width: 2, height: 1));
            clone = owner.TryClone();

            Assert.NotNull(clone);
            Assert.Equal(before + 8, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);

            // Removing the LRU reference must not remove the unique Entry
            // charge while either independent lease still owns that Entry.
            SharedCanvasBitmapCache.ReleaseDevice(device);
            Assert.Equal(before + 8, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
            owner.Dispose();
            owner = null;
            Assert.Equal(before + 8, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
            owner?.Dispose();
            clone?.Dispose();
        }

        Assert.Equal(before, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
    }

    [Fact]
    public void UncachedBitmapPixelWeightCountsOneEntryAcrossClones()
    {
        using var device = new CanvasDevice();
        long before = SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests;
        long cacheBefore = SharedCanvasBitmapCache.RetainedBytesForTests;
        SharedCanvasBitmapCache.Lease? owner = null;
        SharedCanvasBitmapCache.Lease? clone = null;
        try
        {
            owner = SharedCanvasBitmapCache.CreateUncachedLease(
                CreateBitmap(device, width: 2, height: 1),
                pixelWidth: 2,
                pixelHeight: 1);
            clone = owner.TryClone();

            Assert.NotNull(clone);
            Assert.Equal(before + 8, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
            Assert.Equal(cacheBefore, SharedCanvasBitmapCache.RetainedBytesForTests);

            owner.Dispose();
            owner = null;
            Assert.Equal(before + 8, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
            Assert.Equal(2u, clone.Bitmap.SizeInPixels.Width);
        }
        finally
        {
            owner?.Dispose();
            clone?.Dispose();
        }

        Assert.Equal(before, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
        Assert.Equal(cacheBefore, SharedCanvasBitmapCache.RetainedBytesForTests);
    }

    [Fact]
    public void UncachedKnownDimensionsRejectInvalidValuesBeforeTakingOwnership()
    {
        using var device = new CanvasDevice();
        using var bitmap = CreateBitmap(device, width: 2, height: 1);
        long before = SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests;
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SharedCanvasBitmapCache.CreateUncachedLease(bitmap, 0, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SharedCanvasBitmapCache.CreateUncachedLease(bitmap, 2, -1));
        Assert.Equal(before, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
        Assert.Equal(2u, bitmap.SizeInPixels.Width);
    }

    [Fact]
    public void UncachedLeaseIsPreparedBeforeDispatcherPublication_SourceContractOnly()
    {
        string source = ReadImageSource();
        int decode = source.IndexOf("The decoded raster output exceeds the consumer's pixel budget.", StringComparison.Ordinal);
        int lease = source.IndexOf("SharedCanvasBitmapCache.CreateUncachedLease(", decode, StringComparison.Ordinal);
        int publication = source.IndexOf("PublishOnUiThread(() =>", decode, StringComparison.Ordinal);
        int end = source.IndexOf("private bool TryAcquireRasterBitmap(", publication, StringComparison.Ordinal);
        Assert.True(decode >= 0 && lease > decode && publication > lease && end > publication);
        string callback = source[publication..end];
        Assert.DoesNotContain("CreateUncachedLease", callback, StringComparison.Ordinal);
        Assert.DoesNotContain("SizeInPixels", callback, StringComparison.Ordinal);
        Assert.Contains("ReplaceBitmap(publishedLease.Bitmap, publishedLease);", callback, StringComparison.Ordinal);
    }

    [Fact]
    public void AbandonedUnpublishedOwnerRestoresAccountingWithoutConsumingTheBitmap()
    {
        using var device = new CanvasDevice();
        using var bitmap = CreateBitmap(device, width: 2, height: 1);
        long before = SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests;
        var lease = new SharedCanvasBitmapCache.Lease();
        var entry = new SharedCanvasBitmapCache.Entry(bitmap, 2, 1);
        lease.Attach(entry);
        entry.AddCacheReference();
        Assert.Equal(before + 8, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
        lease.AbandonUnpublishedOwnership();
        lease.AbandonUnpublishedOwnership();
        lease.Dispose();
        Assert.Equal(before, SharedCanvasBitmapCache.LiveBitmapPixelWeightBytesForTests);
        Assert.Equal(2u, bitmap.SizeInPixels.Width);
        Assert.False(entry.TryAcquire());
    }

    [Fact]
    public void UncachedDisplaySizedBitmapPreservesOriginalIntrinsicGeometry()
    {
        using var device = new CanvasDevice();
        using SharedCanvasBitmapCache.Lease lease = SharedCanvasBitmapCache.CreateUncachedLease(
            CreateBitmap(device, width: 2, height: 1), 2, 1, new Size(800, 400));
        Assert.Equal(new Size(800, 400), lease.IntrinsicSize);
        Assert.Equal(2u, lease.Bitmap.SizeInPixels.Width);
        Assert.True(lease.IsWithinPixelBudget(2));
    }

    [Fact]
    public void FailedBitmapDisposeRetainsChargeAsUncertainWithoutRetainingTheResource()
    {
        var ledger = new SharedCanvasBitmapCache.LiveBitmapPixelWeightLedger();
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

    private static BitmapOutputAdmission.Reservation CreateCommittedReservation(long bytes)
    {
        if (!BitmapOutputAdmission.Shared.TryReserve(
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

    private static string ReadImageSource()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Layout", "Boxes", "ImageBox.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);
        }
        throw new FileNotFoundException("ImageBox source was not found from the test base directory.");
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
