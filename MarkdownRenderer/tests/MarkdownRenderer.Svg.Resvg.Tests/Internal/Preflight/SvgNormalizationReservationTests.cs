using System.Runtime.CompilerServices;
using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Preflight;
using MarkdownRenderer.Svg.Resvg.Internal.Sources;
using MarkdownRenderer.Svg.Resvg.Tests.TestSupport;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests.Internal.Preflight;

/// <summary>
/// Pins how UTF-8 normalization charges the process snapshot reservation:
/// growth before the first output segment, shrink after sealing, typed
/// capacity and scratch denials, promoted demand, and cancellation at the
/// first output allocation.
/// </summary>
public sealed class SvgNormalizationReservationTests
{
    [Fact]
    public async Task NormalizationGrowsReservationBeforeFirstOutputAndShrinksAfterSeal()
    {
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 512);
        var owner = new SvgSnapshotAdmission(capacityBytes: 128 * 1024, visibleHeadroomBytes: 16 * 1024);
        Assert.True(owner.TryReserve(options.MaxSourceBytes, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? reservation, out _));
        long requiredPeak = 2L * options.MaxSourceBytes + SvgSourceBuffer.SegmentSize;
        long reservationAtFirstOutput = 0;
        int observerCalls = 0;
        SvgPreflightResult? preflight = null;
        try
        {
            preflight = await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(CreateLatin1Svg()),
                options,
                CancellationToken.None,
                snapshotAdmission: reservation,
                beforeFirstNormalizedSegmentAllocation: () =>
                {
                    observerCalls++;
                    reservationAtFirstOutput = reservation!.Bytes;
                }).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(1, observerCalls);
            Assert.Equal(requiredPeak, reservationAtFirstOutput);
            Assert.Equal(preflight.Source.AllocatedCapacity, reservation!.Bytes);
            Assert.Equal(preflight.Source.AllocatedCapacity, owner.ReservedBytes);
            Assert.True(preflight.Source.AllocatedCapacity < requiredPeak);
        }
        finally
        {
            preflight?.ReleaseSource();
            reservation?.Dispose();
        }

        Assert.Equal(0, owner.ReservedBytes);
    }

    [Fact]
    public async Task NormalizationCapacityDenialIsTypedBeforeAnyOutputSegmentAndReleasesReservation()
    {
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 512);
        var owner = new SvgSnapshotAdmission(capacityBytes: 4096, visibleHeadroomBytes: 512);
        Assert.True(owner.TryReserve(options.MaxSourceBytes, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? reservation, out long initialVersion));
        int observerCalls = 0;
        long requiredPeak = 2L * options.MaxSourceBytes + SvgSourceBuffer.SegmentSize;

        SvgPreflightCapacityUnavailableException exception = await Assert.ThrowsAsync<SvgPreflightCapacityUnavailableException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(CreateLatin1Svg()),
                options,
                CancellationToken.None,
                snapshotAdmission: reservation,
                beforeFirstNormalizedSegmentAllocation: () => observerCalls++).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(requiredPeak, exception.RequiredSourceReservationBytes);
        Assert.Equal(initialVersion, exception.CapacityVersion);
        Assert.Equal(0, observerCalls);
        Assert.Equal(0, owner.ReservedBytes);
        reservation!.Dispose();
        Assert.Equal(0, owner.ReservedBytes);
    }

    [Fact]
    public async Task NormalizationReadsPromotedDemandBeforeGrowingTheReservation()
    {
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 512);
        // Overscan's available tier is smaller than the normalization peak,
        // while visible demand can fit the same peak.
        var owner = new SvgSnapshotAdmission(capacityBytes: 96 * 1024, visibleHeadroomBytes: 32 * 1024);
        Assert.True(owner.TryReserve(options.MaxSourceBytes, MarkdownSvgRenderPriority.Overscan,
            out SvgSnapshotAdmission.Lease? reservation, out _));
        var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
        using var memory = new AdmissionObservingMemoryManager(
            CreateLatin1Svg(),
            () => demand.PromoteVisible());
        SvgPreflightResult? preflight = null;
        try
        {
            preflight = await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(memory.BorrowedMemory),
                options,
                CancellationToken.None,
                snapshotAdmission: reservation,
                demand: demand).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(MarkdownSvgRenderPriority.Visible, demand.Priority);
            Assert.Equal(preflight.Source.AllocatedCapacity, reservation!.Bytes);
            Assert.Equal(preflight.Source.AllocatedCapacity, owner.ReservedBytes);
        }
        finally
        {
            preflight?.ReleaseSource();
            reservation?.Dispose();
        }

        Assert.Equal(0, owner.ReservedBytes);
    }

    [Fact]
    public async Task CancellationAtFirstNormalizedOutputAllocationReturnsPeakReservation()
    {
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 512);
        var owner = new SvgSnapshotAdmission(capacityBytes: 128 * 1024, visibleHeadroomBytes: 16 * 1024);
        Assert.True(owner.TryReserve(options.MaxSourceBytes, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? reservation, out _));
        using var cancellation = new CancellationTokenSource();
        long reservationAtFirstOutput = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(CreateLatin1Svg()),
                options,
                cancellation.Token,
                snapshotAdmission: reservation,
                beforeFirstNormalizedSegmentAllocation: () =>
                {
                    reservationAtFirstOutput = reservation!.Bytes;
                    cancellation.Cancel();
                }).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(2L * options.MaxSourceBytes + SvgSourceBuffer.SegmentSize, reservationAtFirstOutput);
        Assert.Equal(0, owner.ReservedBytes);
        reservation!.Dispose();
        Assert.Equal(0, owner.ReservedBytes);
    }

    [Fact]
    public async Task NormalizationScratchPressureRemainsTransientAndReleasesHostSource()
    {
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 512);
        var owner = new SvgSnapshotAdmission(capacityBytes: 128 * 1024, visibleHeadroomBytes: 16 * 1024);
        Assert.True(owner.TryReserve(60 * 1024, MarkdownSvgRenderPriority.Overscan,
            out SvgSnapshotAdmission.Lease? competingOwner, out _));
        using var renderer = new ResvgMarkdownSvgRenderer(options, owner);
        long requiredPeak = 2L * options.MaxSourceBytes + SvgSourceBuffer.SegmentSize;
        Assert.True(requiredPeak <= owner.CapacityBytes - owner.VisibleHeadroomBytes);

        byte[]? hostBytes = CreateLatin1Svg();
        var hostSource = new WeakReference<byte[]>(hostBytes);
        Task<MarkdownSvgAdmissionResult> hostOpen = CreateHostNormalizationOpen(renderer, hostBytes);
        hostBytes = null;
        MarkdownSvgAdmissionResult hostResult = await hostOpen.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MarkdownSvgAdmissionStatus.CapacityUnavailable, hostResult.Status);
        Assert.Null(hostResult.Document);
        Assert.Equal(requiredPeak, hostResult.RequiredSourceReservationBytes);
        Assert.Equal(60 * 1024, owner.ReservedBytes);
        Assert.Equal(0, renderer.SourceAttachmentCount);
        AssertSourceReleased(hostSource);
        GC.KeepAlive(hostOpen);

        // The same content can be deferred again. A transient admission miss
        // never enters content quarantine or negative-cache state.
        MarkdownSvgAdmissionResult repeatedResult = await CreateHostNormalizationOpen(
            renderer, CreateLatin1Svg()).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MarkdownSvgAdmissionStatus.CapacityUnavailable, repeatedResult.Status);
        Assert.Equal(requiredPeak, repeatedResult.RequiredSourceReservationBytes);
        Assert.Equal(60 * 1024, owner.ReservedBytes);
        Assert.Equal(0, renderer.SourceAttachmentCount);

        competingOwner!.Dispose();
        Assert.Equal(0, owner.ReservedBytes);
    }

    [Fact]
    public async Task DirectNormalizationScratchDenialUsesTypedResourceLimitFailure()
    {
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 512);
        var owner = new SvgSnapshotAdmission(capacityBytes: 4096, visibleHeadroomBytes: 512);
        using var renderer = new ResvgMarkdownSvgRenderer(options, owner);

        MarkdownSvgException directFailure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await CreateDirectNormalizationOpen(renderer, CreateLatin1Svg()).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, directFailure.Reason);
        Assert.Equal(0, owner.ReservedBytes);
        Assert.Equal(0, renderer.SourceAttachmentCount);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<MarkdownSvgAdmissionResult> CreateHostNormalizationOpen(
        ResvgMarkdownSvgRenderer renderer,
        byte[] bytes) =>
        ((IMarkdownSvgAdmission)renderer).TryOpenAsync(
            new MarkdownSvgOpenRequest(bytes),
            new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan),
            CancellationToken.None).AsTask();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<IMarkdownSvgDocument> CreateDirectNormalizationOpen(
        ResvgMarkdownSvgRenderer renderer,
        byte[] bytes) =>
        renderer.OpenAsync(new MarkdownSvgOpenRequest(bytes)).AsTask();

    private static byte[] CreateLatin1Svg() => Encoding.Latin1.GetBytes(
        "<?xml version='1.0' encoding='iso-8859-1'?><svg xmlns='http://www.w3.org/2000/svg'><desc>café</desc></svg>");

    private static void AssertSourceReleased(WeakReference<byte[]> source)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!source.TryGetTarget(out _))
                return;
        }

        Assert.False(source.TryGetTarget(out _), "A completed open task retained its caller-owned SVG buffer.");
    }
}
