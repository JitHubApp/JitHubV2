using System.Runtime.CompilerServices;
using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Documents;
using MarkdownRenderer.Svg.Resvg.Internal.Preflight;
using MarkdownRenderer.Svg.Resvg.Internal.Sources;
using MarkdownRenderer.Svg.Resvg.Tests.TestSupport;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests.Internal.Sources;

public sealed class SvgSnapshotAdmissionTests
{
    [Fact]
    public async Task PreflightReservesBeforeTheFirstProviderOwnedSourceClone()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 4096, visibleHeadroomBytes: 512);
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 256);
        byte[] bytes = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'><rect width='1' height='1'/></svg>");
        Assert.True(owner.TryReserve(options.MaxSourceBytes, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? reservation, out _));
        bool observedReservationAtClone = false;
        using var memory = new AdmissionObservingMemoryManager(bytes,
            () => observedReservationAtClone = owner.ReservedBytes >= options.MaxSourceBytes);

        SvgPreflightResult preflight = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(memory.BorrowedMemory),
            options,
            CancellationToken.None,
            snapshotAdmission: reservation).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(observedReservationAtClone);
        byte[] privateSource = preflight.Source.ReadAllBytes();
        Assert.Equal(Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(privateSource));
        Array.Fill(bytes, (byte)0);
        Assert.Equal(privateSource, preflight.Source.ReadAllBytes());
        Assert.Equal(preflight.Source.AllocatedCapacity, preflight.SourceReservationBytes);
        Assert.Equal(preflight.Source.AllocatedCapacity, owner.ReservedBytes);
        preflight.ReleaseSource();
        Assert.Equal(0, owner.ReservedBytes);
    }

    [Fact]
    public async Task ReservationShrinksToNormalizedDtdFreeSnapshotAndPreservesCallerSource()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 128 * 1024, visibleHeadroomBytes: 16 * 1024);
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 4096);
        string authored = "<?xml version='1.0' encoding='iso-8859-1'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg'><desc>" + new string('é', 32) + "</desc></svg>";
        byte[] callerBytes = Encoding.Latin1.GetBytes(authored);
        byte[] originalCallerBytes = (byte[])callerBytes.Clone();
        string expectedNormalized = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<svg xmlns=\"http://www.w3.org/2000/svg\"><desc>" + new string('é', 32) + "</desc></svg>";
        Assert.True(owner.TryReserve(options.MaxSourceBytes, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? reservation, out _));

        SvgPreflightResult preflight = await SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(callerBytes),
            options,
            CancellationToken.None,
            snapshotAdmission: reservation).WaitAsync(TimeSpan.FromSeconds(5));

        byte[] normalizedSource = preflight.Source.ReadAllBytes();
        Assert.NotEqual(callerBytes.Length, preflight.Source.Length);
        Assert.Equal(originalCallerBytes, callerBytes);
        Assert.Equal(expectedNormalized, Encoding.UTF8.GetString(normalizedSource));
        Assert.Equal(new string('é', 32), preflight.Info.Description);
        Assert.True(preflight.Source.AllocatedCapacity < options.MaxSourceBytes);
        Assert.Equal(preflight.Source.AllocatedCapacity, preflight.SourceReservationBytes);
        Assert.Equal(preflight.Source.AllocatedCapacity, owner.ReservedBytes);
        preflight.ReleaseSource();
        Assert.Equal(0, owner.ReservedBytes);
    }

    [Fact]
    public async Task MalformedPreflightReturnsItsTentativeReservationExactlyOnce()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 4096, visibleHeadroomBytes: 512);
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 1024);
        byte[] malformed = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'");
        Assert.True(owner.TryReserve(options.MaxSourceBytes, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? reservation, out _));

        await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await SvgPreflight.InspectAsync(
                new MarkdownSvgOpenRequest(malformed),
                options,
                CancellationToken.None,
                snapshotAdmission: reservation).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(0, owner.ReservedBytes);
        reservation!.Dispose();
        Assert.Equal(0, owner.ReservedBytes);
    }

    [Fact]
    public async Task SmallInputReservesItsExactCloneBeforePreflightRatherThanThePerDocumentCeiling()
    {
        byte[] malformed = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'");
        var owner = new SvgSnapshotAdmission(capacityBytes: malformed.Length, visibleHeadroomBytes: 0);
        using var renderer = new ResvgMarkdownSvgRenderer(new ResvgMarkdownSvgRendererOptions(), owner);
        long observedReservation = 0;
        using var memory = new AdmissionObservingMemoryManager(malformed,
            () => observedReservation = owner.ReservedBytes);

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await renderer.OpenAsync(new MarkdownSvgOpenRequest(memory.BorrowedMemory))
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5)));

        // A source-sized budget admits the clone and reaches XML validation.
        // The old ceiling-sized reservation rejected this before reading it.
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
        Assert.True(memory.WasRead);
        Assert.Equal(malformed.Length, observedReservation);
        Assert.Equal(0, owner.ReservedBytes);
    }

    [Fact]
    public async Task HostCapacityDenialIsTypedAndDoesNotCloneOrRetainTheRequest()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 512, visibleHeadroomBytes: 128);
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 256);
        Assert.True(owner.TryReserve(384, MarkdownSvgRenderPriority.Overscan,
            out SvgSnapshotAdmission.Lease? overscan, out long observedVersion));
        using (overscan)
        using (var renderer = new ResvgMarkdownSvgRenderer(options, owner))
        using (var memory = new AdmissionObservingMemoryManager(
            Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>"), static () => { }))
        {
            MarkdownSvgOpenRequest request = new(memory.BorrowedMemory,
                Priority: MarkdownSvgRenderPriority.Overscan);
            var demand = new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan);
            var host = (IMarkdownSvgAdmission)renderer;

            MarkdownSvgAdmissionResult result = await host.TryOpenAsync(
                request, demand, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(MarkdownSvgAdmissionStatus.CapacityUnavailable, result.Status);
            Assert.Null(result.Document);
            Assert.Equal(observedVersion, result.CapacityVersion);
            Assert.Equal(request.SanitizedSvgBytes.Length, result.RequiredSourceReservationBytes);
            Assert.False(memory.WasRead);
            Assert.Equal(384, owner.ReservedBytes);

            MarkdownSvgException directFailure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
                await renderer.OpenAsync(request).AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, directFailure.Reason);
            Assert.False(memory.WasRead);
            Assert.Equal(384, owner.ReservedBytes);
        }
    }

    [Fact]
    public async Task DocumentRetirementReturnsSourceReservationOnlyAfterAdmittedRendersDrain()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 4096, visibleHeadroomBytes: 512);
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 1024);
        byte[] bytes = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='2' height='1'/> ");
        var request = new MarkdownSvgOpenRequest(bytes);
        Assert.True(owner.TryReserve(options.MaxSourceBytes, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? reservation, out _));
        SvgPreflightResult preflight = await SvgPreflight.InspectAsync(
            request, options, CancellationToken.None, snapshotAdmission: reservation)
            .WaitAsync(TimeSpan.FromSeconds(5));
        var sourceSnapshot = new SvgDocumentSourceSnapshot(preflight, request);
        var lifetime = new SvgDocumentLifetime(sourceSnapshot.Release);
        SvgDocumentLifetime.RenderLease activeRender =
            lifetime.TryEnterRender() ?? throw new InvalidOperationException("The test render lease was not admitted.");
        long retainedBytes = preflight.Source.Length;

        lifetime.Dispose();

        Assert.Equal(retainedBytes, owner.ReservedBytes);
        Assert.Equal(retainedBytes, sourceSnapshot.GetForRender().Preflight.Source.Length);

        activeRender.Dispose();
        await lifetime.RetirementTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(0, owner.ReservedBytes);
        Assert.Throws<ObjectDisposedException>(() => sourceSnapshot.GetForRender());
    }

    [Fact]
    public async Task CompletedOpenWrappersDoNotRetainBorrowedSourceBytes()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 4096, visibleHeadroomBytes: 512);
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 1024);
        using var renderer = new ResvgMarkdownSvgRenderer(options, owner);

        byte[]? directBytes = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'");
        var directWeak = new WeakReference<byte[]>(directBytes);
        Task<IMarkdownSvgDocument> directOpen = CreateDirectMalformedOpen(renderer, directBytes);
        directBytes = null;
        await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await directOpen.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, owner.ReservedBytes);
        AssertSourceReleased(directWeak);
        GC.KeepAlive(directOpen);

        byte[]? hostBytes = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'");
        var hostWeak = new WeakReference<byte[]>(hostBytes);
        Task<MarkdownSvgAdmissionResult> hostOpen = CreateHostMalformedOpen(renderer, hostBytes);
        hostBytes = null;
        await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await hostOpen.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, owner.ReservedBytes);
        AssertSourceReleased(hostWeak);
        GC.KeepAlive(hostOpen);
    }

    [Fact]
    public async Task CompletedHostCapacityDenialTaskDoesNotRetainBorrowedSourceBytes()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 512, visibleHeadroomBytes: 128);
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 256);
        Assert.True(owner.TryReserve(384, MarkdownSvgRenderPriority.Overscan,
            out SvgSnapshotAdmission.Lease? overscan, out _));
        using (overscan)
        using (var renderer = new ResvgMarkdownSvgRenderer(options, owner))
        {
            byte[]? bytes = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/> ");
            int sourceLength = bytes.Length;
            var weak = new WeakReference<byte[]>(bytes);
            Task<MarkdownSvgAdmissionResult> denied = CreateCapacityDeniedHostOpen(renderer, bytes);
            bytes = null;

            MarkdownSvgAdmissionResult result = await denied.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.Equal(MarkdownSvgAdmissionStatus.CapacityUnavailable, result.Status);
            Assert.Equal(sourceLength, result.RequiredSourceReservationBytes);
            Assert.Equal(384, owner.ReservedBytes);
            AssertSourceReleased(weak);
            GC.KeepAlive(denied);
        }
    }

    [Fact]
    public void EachDefaultRendererOwnsItsAdmissionDomain()
    {
        using var first = new ResvgMarkdownSvgRenderer();
        using var second = new ResvgMarkdownSvgRenderer();
        IMarkdownSvgAdmission firstAdmission = first;
        IMarkdownSvgAdmission secondAdmission = second;

        Assert.NotSame(firstAdmission.AdmissionDomain, secondAdmission.AdmissionDomain);
    }

    [Fact]
    public void RenderersGivenOneAdmissionShareItsDomainAndCapacity()
    {
        SvgSnapshotAdmission shared = SvgSnapshotAdmission.CreateDefault();
        var options = new ResvgMarkdownSvgRendererOptions();
        using var first = new ResvgMarkdownSvgRenderer(options, shared);
        using var second = new ResvgMarkdownSvgRenderer(options, shared);
        IMarkdownSvgAdmission firstAdmission = first;
        IMarkdownSvgAdmission secondAdmission = second;
        Assert.Same(firstAdmission.AdmissionDomain, secondAdmission.AdmissionDomain);

        Assert.True(shared.TryReserve(64, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? lease, out long initialVersion));
        lease!.Dispose();

        Assert.True(firstAdmission.CapacityVersion > initialVersion);
        Assert.Equal(firstAdmission.CapacityVersion, secondAdmission.CapacityVersion);
    }

    [Fact]
    public async Task ImpossibleOverscanWaitIsCancellableAndVisibleWaitUsesRealCapacity()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 100, visibleHeadroomBytes: 20);
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 10);
        using var renderer = new ResvgMarkdownSvgRenderer(options, owner);
        IMarkdownSvgAdmission admission = renderer;
        using var cancellation = new CancellationTokenSource();
        Assert.True(owner.TryReserve(100, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? blocker, out _));
        using SvgSnapshotAdmission.Lease? blockerOwner = blocker;

        // 81 bytes can never fit in the 80-byte overscan tier, even after the
        // blocker releases. The host bridge must remain pending for promotion
        // and then cancel cleanly instead of faulting/removing its waiter row.
        Task impossibleOverscanWait = admission.WaitForCapacityAsync(
            81, MarkdownSvgRenderPriority.Overscan, cancellation.Token);
        Assert.False(impossibleOverscanWait.IsCompleted);
        blocker!.Dispose();
        Assert.False(impossibleOverscanWait.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await impossibleOverscanWait.WaitAsync(TimeSpan.FromSeconds(1)));

        Assert.True(owner.TryReserve(100, MarkdownSvgRenderPriority.Visible,
            out SvgSnapshotAdmission.Lease? visibleBlocker, out _));
        using SvgSnapshotAdmission.Lease? visibleBlockerOwner = visibleBlocker;
        Task visibleWait = admission.WaitForCapacityAsync(
            81, MarkdownSvgRenderPriority.Visible, CancellationToken.None);
        Assert.False(visibleWait.IsCompleted);
        visibleBlocker!.Dispose();
        await visibleWait.WaitAsync(TimeSpan.FromSeconds(1));

        // Preserve the low-level owner's strict tier contract. Only the host
        // bridge converts an impossible overscan threshold into a promotion wait.
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await owner.WaitForCapacityAsync(81, MarkdownSvgRenderPriority.Overscan, CancellationToken.None));
    }

    [Fact]
    public async Task HostPreparationAdmissionRejectsInvalidDemandAndVisibleImpossibleWait()
    {
        var owner = new SvgSnapshotAdmission(capacityBytes: 100, visibleHeadroomBytes: 20);
        using var renderer = new ResvgMarkdownSvgRenderer(
            new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 10), owner);
        IMarkdownSvgPreparationAdmission preparation = renderer;
        Assert.Throws<ArgumentNullException>(() =>
            preparation.TryReservePreparationScratch(
                null!, out _, out _, out _));

        IMarkdownSvgAdmission admission = renderer;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await admission.WaitForCapacityAsync(
                101, MarkdownSvgRenderPriority.Visible, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await admission.WaitForCapacityAsync(
                0, MarkdownSvgRenderPriority.Overscan, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await admission.WaitForCapacityAsync(
                1, (MarkdownSvgRenderPriority)99, CancellationToken.None));
        Assert.Equal(0, owner.ReservedBytes);
    }

    [Theory]
    [InlineData(32, 8, false)]
    [InlineData(64, 16, true)]
    public async Task HostPreparationAndProviderNormalizationPeaksRespect32MiBAnd64MiBAdmissionProfiles(
        int capacityMiB,
        int headroomMiB,
        bool overscanCanFitCombinedPeak)
    {
        long capacityBytes = capacityMiB * 1024L * 1024L;
        long headroomBytes = headroomMiB * 1024L * 1024L;
        var owner = new SvgSnapshotAdmission(capacityBytes, headroomBytes);
        var options = new ResvgMarkdownSvgRendererOptions();
        using var renderer = new ResvgMarkdownSvgRenderer(options, owner);
        IMarkdownSvgAdmission host = renderer;
        IMarkdownSvgPreparationAdmission preparation = renderer;
        long maximumSourceBytes = options.MaxSourceBytes;
        long hostPreparationPeak = checked(2L * maximumSourceBytes);
        long providerNormalizationPeak = checked(
            2L * maximumSourceBytes + SvgSourceBuffer.SegmentSize);
        long combinedPeak = checked(maximumSourceBytes + providerNormalizationPeak);
        long overscanLimit = capacityBytes - headroomBytes;

        Assert.Equal(overscanCanFitCombinedPeak, combinedPeak <= overscanLimit);
        Assert.True(hostPreparationPeak <= overscanLimit);
        Assert.True(combinedPeak <= capacityBytes);

        Assert.True(preparation.TryReservePreparationScratch(
            new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan),
            out IMarkdownSvgPreparationLease? hostLease,
            out _, out long reportedHostPeak));
        using IMarkdownSvgPreparationLease? hostLeaseOwner = hostLease;
        Assert.Equal(hostPreparationPeak, reportedHostPeak);
        hostLease!.SetLength(maximumSourceBytes);
        Assert.Equal(maximumSourceBytes, owner.ReservedBytes);

        Assert.True(owner.TryReserve(maximumSourceBytes, MarkdownSvgRenderPriority.Overscan,
            out SvgSnapshotAdmission.Lease? providerLease, out _));
        using (providerLease)
        {
            bool providerPeakAdmitted = providerLease!.TryGrow(
                providerNormalizationPeak,
                MarkdownSvgRenderPriority.Overscan,
                out _);
            Assert.Equal(overscanCanFitCombinedPeak, providerPeakAdmitted);
            Assert.Equal(providerPeakAdmitted
                    ? combinedPeak
                    : 2L * maximumSourceBytes,
                owner.ReservedBytes);
        }

        hostLease.Dispose();
        Assert.Equal(0L, owner.ReservedBytes);

        if (overscanCanFitCombinedPeak)
        {
            await host.WaitForCapacityAsync(
                combinedPeak, MarkdownSvgRenderPriority.Overscan, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(1));
        }
        else
        {
            // The public-to-host bridge must wait for promotion rather than
            // treating an impossible overscan threshold as a renderer fault.
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
                await owner.WaitForCapacityAsync(
                    combinedPeak, MarkdownSvgRenderPriority.Overscan, CancellationToken.None));
            using var cancellation = new CancellationTokenSource();
            Task promotionWait = host.WaitForCapacityAsync(
                combinedPeak, MarkdownSvgRenderPriority.Overscan, cancellation.Token);
            Assert.False(promotionWait.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await promotionWait.WaitAsync(TimeSpan.FromSeconds(1)));
        }

        // The combined reservation fits the visible total-capacity tier in
        // both profiles, even though the x86 overscan tier excludes headroom.
        await host.WaitForCapacityAsync(
            combinedPeak, MarkdownSvgRenderPriority.Visible, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<IMarkdownSvgDocument> CreateDirectMalformedOpen(
        ResvgMarkdownSvgRenderer renderer,
        byte[] bytes) =>
        renderer.OpenAsync(new MarkdownSvgOpenRequest(bytes)).AsTask();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<MarkdownSvgAdmissionResult> CreateHostMalformedOpen(
        ResvgMarkdownSvgRenderer renderer,
        byte[] bytes) =>
        ((IMarkdownSvgAdmission)renderer).TryOpenAsync(
            new MarkdownSvgOpenRequest(bytes),
            new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Visible),
            CancellationToken.None).AsTask();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Task<MarkdownSvgAdmissionResult> CreateCapacityDeniedHostOpen(
        ResvgMarkdownSvgRenderer renderer,
        byte[] bytes) =>
        ((IMarkdownSvgAdmission)renderer).TryOpenAsync(
            new MarkdownSvgOpenRequest(bytes),
            new MarkdownSvgRenderDemand(MarkdownSvgRenderPriority.Overscan),
            CancellationToken.None).AsTask();

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
