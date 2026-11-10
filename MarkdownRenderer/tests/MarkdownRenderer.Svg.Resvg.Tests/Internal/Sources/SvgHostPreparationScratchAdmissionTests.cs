using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Svg.Resvg.Internal.Sources;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests.Internal.Sources;

/// <summary>
/// Pins the host-preparation scratch reservation in the process snapshot
/// admission domain: its size, demand tier, shrink and dispose accounting.
/// </summary>
public sealed class SvgHostPreparationScratchAdmissionTests
{
    [Fact]
    public void HostPreparationScratchReservesTwiceConfiguredSourceLimitAcrossRendererInstances()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 128, visibleHeadroomBytes: 32);
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 48);
        using var first = new ResvgMarkdownSvgRenderer(options, owner);
        using var second = new ResvgMarkdownSvgRenderer(options, owner);
        var firstHost = (IMarkdownSvgAdmission)first;
        var firstPreparation = (IMarkdownSvgPreparationAdmission)first;
        var secondPreparation = (IMarkdownSvgPreparationAdmission)second;
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);

        Assert.Same(firstHost.AdmissionDomain, ((IMarkdownSvgAdmission)second).AdmissionDomain);
        Assert.True(firstPreparation.TryReservePreparationScratch(
            demand, out IMarkdownSvgPreparationLease? lease,
            out long firstVersion, out long firstRequiredBytes));
        using IMarkdownSvgPreparationLease? leaseOwner = lease;
        Assert.NotNull(lease);
        Assert.Equal(96L, firstRequiredBytes);
        Assert.Equal(0, firstVersion);
        Assert.Equal(96L, owner.ReservedBytes);

        // The same injected process owner backs both renderer instances. The
        // overscan tier is exactly full, so a second reservation is denied
        // without changing the counter or capacity version.
        Assert.False(secondPreparation.TryReservePreparationScratch(
            demand, out IMarkdownSvgPreparationLease? deniedLease,
            out long deniedVersion, out long deniedRequiredBytes));
        Assert.Null(deniedLease);
        Assert.Equal(96L, deniedRequiredBytes);
        Assert.Equal(firstVersion, deniedVersion);
        Assert.Equal(96L, owner.ReservedBytes);

        lease!.Dispose();
        Assert.Equal(0L, owner.ReservedBytes);
        Assert.Equal(1, owner.CapacityVersion);
    }

    [Fact]
    public void HostPreparationScratchUsesDemandTierAndCanRetryAfterPromotion()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 100, visibleHeadroomBytes: 20);
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 30);
        using var renderer = new ResvgMarkdownSvgRenderer(options, owner);
        IMarkdownSvgPreparationAdmission preparation = renderer;
        Assert.True(owner.TryReserve(30, MarkdownSvgRenderPriority.Overscan,
            out SvgSnapshotAdmission.Lease? blocker, out long initialVersion));
        using SvgSnapshotAdmission.Lease? blockerOwner = blocker;
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);

        // 60 bytes do not fit in the overscan remainder (50), but do fit in
        // the visible remainder (70). The failed Try must be side-effect free.
        Assert.False(preparation.TryReservePreparationScratch(
            demand, out IMarkdownSvgPreparationLease? deniedLease,
            out long deniedVersion, out long deniedRequiredBytes));
        Assert.Null(deniedLease);
        Assert.Equal(60L, deniedRequiredBytes);
        Assert.Equal(initialVersion, deniedVersion);
        Assert.Equal(30L, owner.ReservedBytes);
        Assert.Equal(initialVersion, owner.CapacityVersion);

        Assert.True(demand.PromoteVisible());
        Assert.True(preparation.TryReservePreparationScratch(
            demand, out IMarkdownSvgPreparationLease? admittedLease,
            out long admittedVersion, out long admittedRequiredBytes));
        using IMarkdownSvgPreparationLease? admittedLeaseOwner = admittedLease;
        Assert.NotNull(admittedLease);
        Assert.Equal(60L, admittedRequiredBytes);
        Assert.Equal(initialVersion, admittedVersion);
        Assert.Equal(90L, owner.ReservedBytes);

        admittedLease!.Dispose();
        blocker!.Dispose();
        Assert.Equal(0L, owner.ReservedBytes);
    }

    [Fact]
    public async Task HostPreparationScratchLeaseShrinksSignalsAndDisposesExactlyOnce()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 100, visibleHeadroomBytes: 20);
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 20);
        using var renderer = new ResvgMarkdownSvgRenderer(options, owner);
        IMarkdownSvgPreparationAdmission preparation = renderer;
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        Assert.True(preparation.TryReservePreparationScratch(
            demand, out IMarkdownSvgPreparationLease? lease,
            out long initialVersion, out long requiredBytes));
        using IMarkdownSvgPreparationLease? leaseOwner = lease;
        Assert.Equal(40L, requiredBytes);
        Assert.Equal(40L, owner.ReservedBytes);

        Task thresholdWait = ((IMarkdownSvgAdmission)renderer).WaitForCapacityAsync(
            requiredFreeBytes: 60,
            MarkdownSvgRenderPriority.Overscan,
            CancellationToken.None);
        Assert.False(thresholdWait.IsCompleted);

        lease!.SetLength(12);
        Assert.Equal(12L, owner.ReservedBytes);
        Assert.Equal(initialVersion + 1, owner.CapacityVersion);
        await thresholdWait.WaitAsync(TimeSpan.FromSeconds(1));

        // A same-size shrink is a no-op; growth and zero are forbidden by the
        // lease contract. None of these probes changes owner accounting.
        lease.SetLength(12);
        Assert.Equal(initialVersion + 1, owner.CapacityVersion);
        Assert.Throws<InvalidOperationException>(() => lease.SetLength(13));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.SetLength(0));
        Assert.Equal(12L, owner.ReservedBytes);

        lease.Dispose();
        lease.Dispose();
        Assert.Equal(0L, owner.ReservedBytes);
        Assert.Equal(initialVersion + 2, owner.CapacityVersion);
        Assert.Throws<ObjectDisposedException>(() => lease.SetLength(1));
    }

    [Fact]
    public async Task HostPreparationScratchShrinkRacingDisposeCannotUnderflow()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 256, visibleHeadroomBytes: 32);
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 64);
        using var renderer = new ResvgMarkdownSvgRenderer(options, owner);
        IMarkdownSvgPreparationAdmission preparation = renderer;
        Assert.True(preparation.TryReservePreparationScratch(
            new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Visible),
            out IMarkdownSvgPreparationLease? lease,
            out _, out _));
        using IMarkdownSvgPreparationLease? leaseOwner = lease;
        using var start = new ManualResetEventSlim(initialState: false);

        Task<bool> shrink = Task.Run(() =>
        {
            start.Wait();
            try
            {
                lease!.SetLength(32);
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        });
        Task dispose = Task.Run(() =>
        {
            start.Wait();
            lease!.Dispose();
        });
        start.Set();
        bool shrunk = await shrink.WaitAsync(TimeSpan.FromSeconds(2));
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0L, owner.ReservedBytes);
        Assert.Equal(shrunk ? 2L : 1L, owner.CapacityVersion);
        lease!.Dispose();
        Assert.Equal(0L, owner.ReservedBytes);
    }
}
