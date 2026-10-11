using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgSourceBufferTests
{
    [Fact]
    public void DetachingNormalizedSegmentsCompactsOnlyThePartialTailAndPreservesBytesAndHash()
    {
        byte[] expected = new byte[(SvgSourceBuffer.SegmentSize * 2) + 37];
        for (int index = 0; index < expected.Length; index++)
            expected[index] = unchecked((byte)(index * 31));

        using var output = new SvgPreflight.BoundedOutputStream(
            maximumBytes: expected.Length + SvgSourceBuffer.SegmentSize,
            CancellationToken.None);
        output.Write(expected);

        using SvgSourceBuffer source = output.DetachSourceBuffer();

        Assert.Equal(expected.Length, source.Length);
        Assert.Equal(expected.Length, source.AllocatedCapacity);
        Assert.Equal(expected, source.CopyToArrayForTest());
        Assert.Equal(SHA256.HashData(expected), source.ComputeSha256());
    }

    [Theory]
    [InlineData("comment-open")]
    [InlineData("comment-close")]
    [InlineData("pi-open")]
    [InlineData("pi-close")]
    [InlineData("doctype-open")]
    [InlineData("quoted-greater-than")]
    public void InertDoctypeScanningAndWhitespaceReplacementCrossSegmentBoundaries(string boundaryCase)
    {
        string authored = CreateBoundarySource(boundaryCase);
        byte[] authoredBytes = Encoding.ASCII.GetBytes(authored);
        int declarationStart = authored.IndexOf("<!DOCTYPE", StringComparison.Ordinal);
        Assert.True(declarationStart >= 0);
        int rootStart = authored.IndexOf("<svg/>", declarationStart, StringComparison.Ordinal);
        Assert.True(rootStart > declarationStart);

        string expectedText = authored[..declarationStart] +
            new string(' ', rootStart - declarationStart) +
            authored[rootStart..];
        byte[] expected = Encoding.ASCII.GetBytes(expectedText);

        using var output = new SvgPreflight.BoundedOutputStream(
            maximumBytes: authoredBytes.Length + SvgSourceBuffer.SegmentSize,
            CancellationToken.None);
        output.Write(authoredBytes);
        using SvgSourceBuffer source = output.DetachSourceBuffer();

        SvgPreflight.StripInertDoctype(source, containsDoctype: true);
        source.Seal();

        Assert.Equal(authoredBytes.Length, source.Length);
        Assert.Equal(authoredBytes.Length, source.AllocatedCapacity);
        Assert.Equal(expected, source.CopyToArrayForTest());
        Assert.Equal(SHA256.HashData(expected), source.ComputeSha256());
    }

    [Fact]
    public void SealedSourceBufferRejectsFurtherPreflightMutation()
    {
        using SvgSourceBuffer source = Encoding.ASCII.GetBytes("<svg/>").CopyIntoBufferForTest();
        source.Seal();

        Assert.Throws<InvalidOperationException>(() => source.FillRange(0, 1, (byte)' '));
        Assert.Equal("<svg/>", Encoding.ASCII.GetString(source.CopyToArrayForTest()));
    }

    [Fact]
    public void LargeSnapshotIsMutationIsolatedAndUsesPrivateNativeStorage()
    {
        byte[] caller = new byte[SvgSourceBuffer.NativeMemoryThresholdBytes + 11];
        "<svg>private</svg>"u8.CopyTo(caller);
        byte[] expected = caller.ToArray();
        using SvgSourceBuffer source = SvgSourceBuffer.CopyFrom(caller);

        Assert.True(source.IsNativeBacked);
        Assert.Equal(caller.Length, source.AllocatedCapacity);
        caller.AsSpan().Fill((byte)'x');

        Assert.Equal(expected, source.CopyToArrayForTest());
    }

    [Fact]
    public void LargeSnapshotCopyDoesNotCreateASecondSourceSizedManagedArray()
    {
        using (SvgSourceBuffer warmup = SvgSourceBuffer.CopyFrom("warm"u8))
        {
            Assert.Equal(4, warmup.Length);
        }

        byte[] input = new byte[4 * 1024 * 1024];
        long before = GC.GetAllocatedBytesForCurrentThread();
        using SvgSourceBuffer source = SvgSourceBuffer.CopyFrom(input);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(source.IsNativeBacked);
        Assert.Equal(input.Length, source.Length);
        Assert.True(allocated < 64 * 1024, $"Copying a 4 MiB source allocated {allocated} managed bytes.");
    }

    [Fact]
    public void SmallSnapshotRemainsArrayBacked()
    {
        using SvgSourceBuffer source = "<svg/>"u8.ToArray().CopyIntoBufferForTest();

        Assert.False(source.IsNativeBacked);
        Assert.Equal("<svg/>", Encoding.ASCII.GetString(source.CopyToArrayForTest()));
    }

    [Fact]
    public void RetainedReadLeaseOutlivesOwnerAndReleasesNativeStorageDeterministically()
    {
        byte[] expected = new byte[SvgSourceBuffer.NativeMemoryThresholdBytes + 31];
        expected.AsSpan().Fill((byte)0x5a);
        SvgSourceBuffer source = SvgSourceBuffer.CopyFrom(expected);
        SvgSourceBuffer.ReadLease lease = source.AcquireReadLease();
        Assert.True(lease.IsNativeBacked);

        source.Dispose();
        source.Dispose();

        Assert.True(source.IsReleased);
        Assert.False(lease.IsStorageReleased);
        Assert.Throws<ObjectDisposedException>(() => source.GetByte(0));
        Assert.True(lease.GetSpanAt(0).SequenceEqual(expected));

        lease.Dispose();
        lease.Dispose();

        Assert.True(lease.IsStorageReleased);
        Assert.Throws<ObjectDisposedException>(() => lease.GetByte(0));
    }

    [Fact]
    public async Task ConcurrentReadAndOwnerDisposeKeepTheLeaseValidUntilReaderFinishes()
    {
        byte[] expected = new byte[SvgSourceBuffer.NativeMemoryThresholdBytes + 4096];
        for (int index = 0; index < expected.Length; index++)
            expected[index] = unchecked((byte)(index * 17));
        SvgSourceBuffer source = SvgSourceBuffer.CopyFrom(expected);
        SvgSourceBuffer.ReadLease lease = source.AcquireReadLease();
        using var start = new ManualResetEventSlim();

        Task<uint> reader = Task.Run(() =>
        {
            start.Wait();
            uint checksum = 0;
            for (int index = 0; index < expected.Length; index++)
                checksum = unchecked((checksum * 16777619) ^ lease.GetByte(index));
            return checksum;
        });
        Task disposer = Task.Run(() =>
        {
            start.Set();
            source.Dispose();
            source.Dispose();
        });

        await Task.WhenAll(reader, disposer);
        uint actualChecksum = await reader;

        uint expectedChecksum = 0;
        foreach (byte value in expected)
            expectedChecksum = unchecked((expectedChecksum * 16777619) ^ value);
        Assert.Equal(expectedChecksum, actualChecksum);
        Assert.False(lease.IsStorageReleased);
        lease.Dispose();
        Assert.True(lease.IsStorageReleased);
    }

    [Fact]
    public void ActiveReadLeasePreventsPreflightMutation()
    {
        using SvgSourceBuffer source = Encoding.ASCII.GetBytes("<svg/>").CopyIntoBufferForTest();
        using (SvgSourceBuffer.ReadLease lease = source.AcquireReadLease())
        {
            Assert.Throws<InvalidOperationException>(() => source.FillRange(0, 1, (byte)' '));
            Assert.Equal((byte)'<', lease.GetByte(0));
        }

        source.FillRange(0, 1, (byte)' ');
        Assert.Equal((byte)' ', source.GetByte(0));
    }

    [Fact]
    public void SnapshotAdmissionStaysChargedUntilOwnerAndLastLeaseRetire()
    {
        var admission = new SvgProcessSnapshotAdmission(capacityBytes: 128, visibleHeadroomBytes: 16);
        Assert.True(admission.TryReserve(64, MarkdownSvgRenderPriority.Visible, out var reservation, out _));
        SvgSourceBuffer source = SvgSourceBuffer.CopyFrom(new byte[SvgSourceBuffer.NativeMemoryThresholdBytes + 1]);
        SvgSourceBuffer.ReadLease lease = source.AcquireReadLease();
        source.Seal();
        source.AttachRetirementResource(reservation!);

        source.Dispose();

        Assert.Equal(64, admission.ReservedBytes);
        Assert.False(lease.IsStorageReleased);
        lease.Dispose();

        Assert.Equal(0, admission.ReservedBytes);
        Assert.True(lease.IsStorageReleased);
    }

    [Fact]
    public void SnapshotAdmissionStaysChargedWhenLeaseRetiresBeforeOwner()
    {
        var admission = new SvgProcessSnapshotAdmission(capacityBytes: 128, visibleHeadroomBytes: 16);
        Assert.True(admission.TryReserve(64, MarkdownSvgRenderPriority.Visible, out var reservation, out _));
        SvgSourceBuffer source = SvgSourceBuffer.CopyFrom(new byte[SvgSourceBuffer.NativeMemoryThresholdBytes + 1]);
        SvgSourceBuffer.ReadLease lease = source.AcquireReadLease();
        source.Seal();
        source.AttachRetirementResource(reservation!);

        lease.Dispose();

        Assert.Equal(64, admission.ReservedBytes);
        Assert.False(lease.IsStorageReleased);
        source.Dispose();

        Assert.Equal(0, admission.ReservedBytes);
        Assert.True(lease.IsStorageReleased);
    }

    [Fact]
    public async Task ConcurrentOwnerAndLeaseRetirementReleaseReservationExactlyOnce()
    {
        var admission = new SvgProcessSnapshotAdmission(capacityBytes: 128, visibleHeadroomBytes: 16);
        Assert.True(admission.TryReserve(64, MarkdownSvgRenderPriority.Visible, out var reservation, out _));
        SvgSourceBuffer source = SvgSourceBuffer.CopyFrom(new byte[SvgSourceBuffer.NativeMemoryThresholdBytes + 1]);
        SvgSourceBuffer.ReadLease lease = source.AcquireReadLease();
        source.Seal();
        source.AttachRetirementResource(reservation!);

        await Task.WhenAll(Task.Run(source.Dispose), Task.Run(lease.Dispose));

        Assert.Equal(0, admission.ReservedBytes);
        Assert.True(lease.IsStorageReleased);
    }

    [Fact]
    public async Task IndependentReadLeasesKeepNativeBytesAndAdmissionUntilTheLastReaderDrains()
    {
        var admission = new SvgProcessSnapshotAdmission(capacityBytes: 128, visibleHeadroomBytes: 16);
        Assert.True(admission.TryReserve(64, MarkdownSvgRenderPriority.Visible, out var reservation, out _));
        byte[] bytes = new byte[SvgSourceBuffer.NativeMemoryThresholdBytes + 19];
        bytes.AsSpan().Fill(0x4d);
        SvgSourceBuffer source = SvgSourceBuffer.CopyFrom(bytes);
        source.Seal();
        source.AttachRetirementResource(reservation!);
        SvgSourceBuffer.ReadLease first = source.AcquireReadLease();
        SvgSourceBuffer.ReadLease second = source.AcquireReadLease();

        Task ownerRetirement = Task.Run(source.Dispose);
        Task firstReaderRetirement = Task.Run(first.Dispose);
        await Task.WhenAll(ownerRetirement, firstReaderRetirement);

        Assert.Equal(64, admission.ReservedBytes);
        Assert.False(second.IsStorageReleased);
        Assert.Equal((byte)0x4d, second.GetByte(bytes.Length - 1));

        second.Dispose();

        Assert.Equal(0, admission.ReservedBytes);
        Assert.True(second.IsStorageReleased);
    }

    [Fact]
    public void PreflightResultDefersSnapshotReservationRefundUntilActiveReadEnds()
    {
        var admission = new SvgProcessSnapshotAdmission(capacityBytes: 128, visibleHeadroomBytes: 16);
        Assert.True(admission.TryReserve(64, MarkdownSvgRenderPriority.Visible, out var reservation, out _));
        SvgSourceBuffer source = SvgSourceBuffer.CopyFrom(new byte[SvgSourceBuffer.NativeMemoryThresholdBytes + 1]);
        source.Seal();
        var result = new SvgPreflightResult(source, new byte[32], new(), 0, 0, 0, 0);
        result.AttachSnapshotAdmission(reservation!);
        SvgSourceBuffer.ReadLease lease = result.Source.AcquireReadLease();

        result.ReleaseSource();

        Assert.Equal(64, result.SourceReservationBytes);
        Assert.Equal(64, admission.ReservedBytes);
        Assert.False(lease.IsStorageReleased);
        lease.Dispose();

        Assert.Equal(0, result.SourceReservationBytes);
        Assert.Equal(0, admission.ReservedBytes);
        Assert.True(lease.IsStorageReleased);
    }

    [Fact]
    public void SegmentOwnershipKeepsLogicalLengthSeparateFromAllocatedCapacity()
    {
        byte[] first = new byte[SvgSourceBuffer.SegmentSize];
        byte[] tail = Enumerable.Repeat((byte)0x7f, 64).ToArray();
        first.AsSpan().Fill(0x2a);
        tail.AsSpan(0, 17).Fill(0x39);
        var segments = new List<byte[]> { first, tail };
        int logicalLength = first.Length + 17;

        using SvgSourceBuffer source = SvgSourceBuffer.TakeOwnedSegments(
            segments,
            logicalLength,
            allocatedCapacity: first.Length + tail.Length);

        Assert.Equal(logicalLength, source.Length);
        Assert.Equal(first.Length + tail.Length, source.AllocatedCapacity);
        Assert.Equal(logicalLength, source.CopyToArrayForTest().Length);
        Assert.Equal(SHA256.HashData(source.CopyToArrayForTest()), source.ComputeSha256());
        using SvgSourceBuffer.ReadLease lease = source.AcquireReadLease();
        Assert.Equal(17, lease.GetSpanAt(first.Length).Length);
        Assert.True(lease.GetSpanAt(logicalLength).IsEmpty);
        Assert.Equal((byte)0x39, lease.GetByte(logicalLength - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => lease.GetByte(logicalLength));
    }

    [Fact]
    public void SegmentOwnershipRejectsDeclaredCapacityThatDoesNotMatchTheBackingSegments()
    {
        var segments = new List<byte[]> { new byte[SvgSourceBuffer.SegmentSize] };

        Assert.Throws<ArgumentException>(() => SvgSourceBuffer.TakeOwnedSegments(
            segments,
            length: SvgSourceBuffer.SegmentSize - 1,
            allocatedCapacity: SvgSourceBuffer.SegmentSize - 1));

        Assert.Throws<ArgumentException>(() => SvgSourceBuffer.TakeOwnedSegments(
            [],
            length: 1,
            allocatedCapacity: 1));
    }

    [Fact]
    public void EmptySnapshotCannotCaptureAReservationThatWouldNeverRetire()
    {
        var admission = new SvgProcessSnapshotAdmission(capacityBytes: 128, visibleHeadroomBytes: 16);
        Assert.True(admission.TryReserve(64, MarkdownSvgRenderPriority.Visible, out var reservation, out _));

        Assert.Throws<InvalidOperationException>(() => SvgSourceBuffer.Empty.AttachRetirementResource(reservation!));

        Assert.Equal(64, admission.ReservedBytes);
        reservation!.Dispose();
        Assert.Equal(0, admission.ReservedBytes);
    }

    [Fact]
    public void RetirementContinuesReleasingCreditsWhenOneDisposerThrows()
    {
        var admission = new SvgProcessSnapshotAdmission(capacityBytes: 128, visibleHeadroomBytes: 16);
        Assert.True(admission.TryReserve(64, MarkdownSvgRenderPriority.Visible, out var reservation, out _));
        SvgSourceBuffer source = SvgSourceBuffer.CopyFrom(new byte[SvgSourceBuffer.NativeMemoryThresholdBytes + 1]);
        source.Seal();
        var throwing = new ThrowingDisposable();
        source.AttachRetirementResource(throwing);
        source.AttachRetirementResource(reservation!);

        Assert.Throws<InvalidOperationException>(source.Dispose);

        Assert.True(throwing.WasDisposed);
        Assert.Equal(0, admission.ReservedBytes);
    }

    [Fact]
    public void RetiredPreflightCannotReattachAReservationToTheImmortalEmptySentinel()
    {
        var admission = new SvgProcessSnapshotAdmission(capacityBytes: 128, visibleHeadroomBytes: 16);
        Assert.True(admission.TryReserve(64, MarkdownSvgRenderPriority.Visible, out var reservation, out _));
        var result = new SvgPreflightResult(
            SvgSourceBuffer.CopyFrom(new byte[SvgSourceBuffer.NativeMemoryThresholdBytes + 1]),
            new byte[32], new(), 0, 0, 0, 0);
        result.Source.Seal();
        result.ReleaseSource();

        Assert.Throws<InvalidOperationException>(() => result.AttachSnapshotAdmission(reservation!));
        Assert.Equal(64, admission.ReservedBytes);
        Assert.Equal(0, result.SourceReservationBytes);
        reservation!.Dispose();
        Assert.Equal(0, admission.ReservedBytes);
    }

    [Fact]
    public async Task CancellationDuringLargeNativeBackedNormalizationRetiresSnapshotBeforeReturningAdmission()
    {
        const int MaximumSourceBytes = 128 * 1024;
        string xml = "<?xml version='1.0' encoding='utf-16'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg'><desc>" +
            new string('x', SvgSourceBuffer.NativeMemoryThresholdBytes + 128) +
            "</desc></svg>";
        byte[] encoded = Encoding.Unicode.GetBytes(xml);
        byte[] preamble = Encoding.Unicode.GetPreamble();
        byte[] sourceBytes = new byte[preamble.Length + encoded.Length];
        preamble.CopyTo(sourceBytes, 0);
        encoded.CopyTo(sourceBytes, preamble.Length);

        var admission = new SvgProcessSnapshotAdmission(capacityBytes: 1024 * 1024, visibleHeadroomBytes: 64 * 1024);
        Assert.True(admission.TryReserve(sourceBytes.Length, MarkdownSvgRenderPriority.Visible, out var reservation, out _));
        using var cancellation = new CancellationTokenSource();
        int allocationCallbacks = 0;
        var request = new MarkdownSvgOpenRequest(sourceBytes);
        var options = new ResvgMarkdownSvgRendererOptions(maxSourceBytes: MaximumSourceBytes);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SvgPreflight.InspectAsync(
                request,
                options,
                cancellation.Token,
                snapshotAdmission: reservation,
                beforeFirstNormalizedSegmentAllocation: () =>
                {
                    allocationCallbacks++;
                    cancellation.Cancel();
                }).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(1, allocationCallbacks);
        Assert.Equal(0, admission.ReservedBytes);
        Assert.Equal(0, reservation!.Bytes);
        GC.KeepAlive(sourceBytes);
    }

    [Fact]
    public void SourceMappingRejectsRetiredStorageBeforeNativeAllocation()
    {
        using SvgSourceBuffer source = Encoding.ASCII.GetBytes("<svg/>").CopyIntoBufferForTest();
        source.Dispose();

        // The retired source must fail before even attempting a native object
        // in this deliberately nonexistent object-manager namespace.
        Assert.Throws<ObjectDisposedException>(() => new SharedMemoryLease(
            "MarkdownRendererMissingNamespace\\Source", source, outputLength: 0));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    public void MarkerSearchMatchesSpanOracleAtSegmentAndTailBoundaries(bool segmented, int split)
    {
        byte[] expected = new byte[(SvgSourceBuffer.SegmentSize * 2) + 37];
        expected.AsSpan().Fill((byte)'x');
        byte[] marker = "<!--"u8.ToArray();
        int first = SvgSourceBuffer.SegmentSize - split;
        marker.CopyTo(expected, first);
        marker.CopyTo(expected, expected.Length - marker.Length);
        // Also exercise a first-byte match with the wrong trailing bytes.
        expected[first - 2] = marker[0];

        using var writer = new SvgPreflight.BoundedOutputStream(expected.Length, CancellationToken.None);
        if (segmented)
            writer.Write(expected);
        using SvgSourceBuffer source = segmented
            ? writer.DetachSourceBuffer()
            : expected.CopyIntoBufferForTest();

        foreach (int start in new[] { 0, first - 2, first, first + 1, expected.Length - marker.Length, expected.Length })
        {
            int relative = expected.AsSpan(start).IndexOf(marker);
            int wanted = relative < 0 ? -1 : start + relative;
            Assert.Equal(wanted, source.IndexOfSequence(start, marker));
        }
        Assert.True(source.StartsWithAt(first, marker));
        Assert.True(source.StartsWithAt(expected.Length - marker.Length, marker));
        Assert.False(source.StartsWithAt(first - 2, marker));
        Assert.False(source.StartsWithAt(-1, marker));
        Assert.False(source.StartsWithAt(expected.Length, marker));
        Assert.Equal(expected.Length, source.IndexOfSequence(expected.Length, ReadOnlySpan<byte>.Empty));
        Assert.Equal(-1, source.IndexOfSequence(expected.Length + 1, marker));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.IndexOfSequence(-1, marker));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmMarkerReadsDoNotAllocate(bool segmented)
    {
        byte[] bytes = new byte[SvgSourceBuffer.SegmentSize + 7];
        bytes.AsSpan().Fill((byte)'x');
        int markerStart = SvgSourceBuffer.SegmentSize - 2;
        "<!--"u8.CopyTo(bytes.AsSpan(markerStart));
        using var writer = new SvgPreflight.BoundedOutputStream(bytes.Length, CancellationToken.None);
        if (segmented)
            writer.Write(bytes);
        using SvgSourceBuffer source = segmented
            ? writer.DetachSourceBuffer()
            : bytes.CopyIntoBufferForTest();

        _ = MeasureMarkerReadAllocations(source, markerStart, out _);
        long allocated = MeasureMarkerReadAllocations(source, markerStart, out int matches);

        Assert.Equal(100_000, matches);
        Assert.Equal(0, allocated);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureMarkerReadAllocations(SvgSourceBuffer source, int markerStart, out int matches)
    {
        matches = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int iteration = 0; iteration < 50_000; iteration++)
        {
            if (source.IndexOfSequence(markerStart - 2, "<!--"u8) == markerStart)
                matches++;
            if (source.StartsWithAt(markerStart, "<!--"u8))
                matches++;
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static string CreateBoundarySource(string boundaryCase)
    {
        var text = new StringBuilder();
        switch (boundaryCase)
        {
            case "comment-open":
                PadForSplit(text, targetOffset: 0, bytesBeforeBoundary: 2);
                text.Append("<!--body-->");
                AppendAlignedDoctype(text, splitMarker: true);
                break;
            case "comment-close":
                text.Append("<!--");
                PadForSplit(text, targetOffset: 0, bytesBeforeBoundary: 1);
                text.Append("-->");
                AppendAlignedDoctype(text, splitMarker: false);
                break;
            case "pi-open":
                text.Append("<!--ready-->");
                PadForSplit(text, targetOffset: 0, bytesBeforeBoundary: 1);
                text.Append("<?audit?>");
                AppendAlignedDoctype(text, splitMarker: false);
                break;
            case "pi-close":
                text.Append("<!--ready--><?audit");
                PadForSplit(text, targetOffset: 0, bytesBeforeBoundary: 1);
                text.Append("?>");
                AppendAlignedDoctype(text, splitMarker: false);
                break;
            case "doctype-open":
                text.Append("<!--ready--><?audit?>");
                AppendAlignedDoctype(text, splitMarker: true);
                break;
            case "quoted-greater-than":
                text.Append("<!--ready--><?audit?><!DOCTYPE svg PUBLIC 'x");
                PadForSplit(text, targetOffset: 0, bytesBeforeBoundary: 1);
                text.Append(">y' 'system identifier'>");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(boundaryCase));
        }

        text.Append("<svg/>");
        return text.ToString();
    }

    private sealed class ThrowingDisposable : IDisposable
    {
        internal bool WasDisposed { get; private set; }

        public void Dispose()
        {
            WasDisposed = true;
            throw new InvalidOperationException("Expected retirement callback failure.");
        }
    }

    private static void AppendAlignedDoctype(StringBuilder text, bool splitMarker)
    {
        if (splitMarker)
            PadForSplit(text, targetOffset: 0, bytesBeforeBoundary: 4);
        text.Append("<!DOCTYPE svg PUBLIC 'public identifier' 'system identifier'>");
    }

    private static void PadForSplit(StringBuilder text, int targetOffset, int bytesBeforeBoundary)
    {
        int desiredOffset = (SvgSourceBuffer.SegmentSize - bytesBeforeBoundary) % SvgSourceBuffer.SegmentSize;
        int currentOffset = (text.Length + targetOffset) % SvgSourceBuffer.SegmentSize;
        int padding = (desiredOffset - currentOffset + SvgSourceBuffer.SegmentSize) % SvgSourceBuffer.SegmentSize;
        text.Append(' ', padding);
    }
}
