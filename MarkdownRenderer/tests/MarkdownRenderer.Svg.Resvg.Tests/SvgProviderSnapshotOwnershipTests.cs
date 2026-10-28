#if MARKDOWN_LAYOUT_DIAGNOSTICS
using System.Buffers;
using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgProviderSnapshotOwnershipTests
{
    [Fact]
    public void PreCanceledInspectionDoesNotAcquireOwnershipTicket()
    {
        var ledger = new SvgProviderSnapshotLedger();
        var request = new MarkdownSvgOpenRequest(new byte[4096]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // InspectAsync rejects pre-cancellation synchronously, before dispatch.
        Assert.ThrowsAny<OperationCanceledException>(() =>
        {
            _ = SvgPreflight.InspectAsync(
                request,
                new ResvgMarkdownSvgRendererOptions(),
                cancellation.Token,
                ownershipLedger: ledger);
        });

        SvgProviderSnapshotLedgerSnapshot snapshot = ledger.GetSnapshot();
        Assert.Equal(0, snapshot.LiveBytes);
        Assert.Equal(0, snapshot.LiveSnapshots);
        Assert.Equal(0, snapshot.PeakBytes);
        Assert.Equal(0, snapshot.PeakSnapshots);
        Assert.Equal(0, snapshot.Sequence);
    }

    [Fact]
    public async Task MalformedInspectionReleasesItsTicketWhileTaskRemainsRetained()
    {
        var ledger = new SvgProviderSnapshotLedger();
        byte[] source = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'");
        Task<SvgPreflightResult> inspection = SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(source),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None,
            ownershipLedger: ledger);

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await inspection.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, failure.Reason);
        SvgProviderSnapshotLedgerSnapshot snapshot = ledger.GetSnapshot();
        Assert.Equal(0, snapshot.LiveBytes);
        Assert.Equal(0, snapshot.LiveSnapshots);
        Assert.Equal(source.Length, snapshot.PeakBytes);
        Assert.Equal(1, snapshot.PeakSnapshots);
        GC.KeepAlive(inspection);
    }

    [Fact]
    public async Task CancellationDuringBorrowedSnapshotReadReleasesItsTicket()
    {
        var ledger = new SvgProviderSnapshotLedger();
        using var cancellation = new CancellationTokenSource();
        byte[] callerBytes = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>");
        using var memory = new CancelOnReadMemoryManager(callerBytes, cancellation);
        var request = new MarkdownSvgOpenRequest(memory.BorrowedMemory);

        Task<SvgPreflightResult> inspection = SvgPreflight.InspectAsync(
            request,
            new ResvgMarkdownSvgRendererOptions(),
            cancellation.Token,
            ownershipLedger: ledger);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await inspection.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.True(memory.ReadCount > 0);
        SvgProviderSnapshotLedgerSnapshot snapshot = ledger.GetSnapshot();
        Assert.Equal(0, snapshot.LiveBytes);
        Assert.Equal(0, snapshot.LiveSnapshots);
        Assert.Equal(callerBytes.Length, snapshot.PeakBytes);
        Assert.Equal(1, snapshot.PeakSnapshots);
        GC.KeepAlive(inspection);
    }

    [Fact]
    public async Task TrackEventCancellationPreventsSnapshotCloneAndReleasesTicket()
    {
        var ledger = new SvgProviderSnapshotLedger();
        using var cancellation = new CancellationTokenSource();
        byte[] callerBytes = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>");
        using var memory = new CountingMemoryManager(callerBytes);
        ReadOnlyMemory<byte> borrowed = memory.BorrowedMemory;
        memory.ResetReadCount();
        var request = new MarkdownSvgOpenRequest(borrowed);
        using var listener = new TrackCancellationListener(ledger.ScopeId, cancellation);

        Task<SvgPreflightResult>? inspection = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            // Pool callers run the dispatch delegate inline; other callers
            // observe the same cancellation through the dispatched task.
            inspection = SvgPreflight.InspectAsync(
                request,
                new ResvgMarkdownSvgRendererOptions(),
                cancellation.Token,
                ownershipLedger: ledger);
            await inspection.WaitAsync(TimeSpan.FromSeconds(5));
        });

        Assert.True(listener.MatchedTrackEventCount > 0);
        Assert.Equal(0, memory.ReadCount);
        Assert.Equal(0, ledger.GetSnapshot().LiveBytes);
        Assert.Equal(0, ledger.GetSnapshot().LiveSnapshots);
        Assert.Equal(callerBytes.Length, ledger.GetSnapshot().PeakBytes);
        Assert.Equal(1, ledger.GetSnapshot().PeakSnapshots);
        GC.KeepAlive(inspection);
    }

    [Fact]
    public async Task SuccessfulRetainedInspectionKeepsTicketUntilIdempotentSourceRelease()
    {
        var ledger = new SvgProviderSnapshotLedger();
        byte[] callerBytes = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='2' height='1'/>");
        Task<SvgPreflightResult> inspection = SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(callerBytes),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None,
            ownershipLedger: ledger);
        SvgPreflightResult result = await inspection.WaitAsync(TimeSpan.FromSeconds(5));

        byte[] preflightBytes = result.Source.CopyToArrayForTest();
        Assert.Equal(callerBytes, preflightBytes);
        Array.Fill(callerBytes, (byte)0);
        Assert.Equal(preflightBytes, result.Source.CopyToArrayForTest());
        Assert.Equal(preflightBytes.Length, result.Source.Length);
        Assert.Equal(callerBytes.Length, ledger.GetSnapshot().LiveBytes);
        Assert.Equal(1, ledger.GetSnapshot().LiveSnapshots);

        result.ReleaseSource();
        result.ReleaseSource();

        Assert.Same(result, inspection.Result);
        Assert.Equal(0, result.Source.Length);
        Assert.Equal(0, ledger.GetSnapshot().LiveBytes);
        Assert.Equal(0, ledger.GetSnapshot().LiveSnapshots);
        GC.KeepAlive(result);
        GC.KeepAlive(inspection);
    }

    [Fact]
    public async Task NormalizedSnapshotTicketTracksFinalUtf8Length()
    {
        var ledger = new SvgProviderSnapshotLedger();
        string document = "<?xml version='1.0' encoding='iso-8859-1'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg'><desc>" + new string('é', 128) + "</desc></svg>";
        byte[] callerBytes = Encoding.Latin1.GetBytes(document);
        Task<SvgPreflightResult> inspection = SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(callerBytes),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None,
            ownershipLedger: ledger);
        SvgPreflightResult result = await inspection.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(callerBytes.Length, result.Source.Length);
        Assert.Equal(result.Source.Length, result.Source.AllocatedCapacity);
        Assert.Equal(result.Source.AllocatedCapacity, ledger.GetSnapshot().LiveBytes);
        Assert.Equal(1, ledger.GetSnapshot().LiveSnapshots);
        Assert.True(ledger.GetSnapshot().PeakBytes >= System.Math.Max(callerBytes.Length, result.Source.AllocatedCapacity));

        result.ReleaseSource();
        Assert.Equal(0, ledger.GetSnapshot().LiveBytes);
        Assert.Equal(0, ledger.GetSnapshot().LiveSnapshots);
        GC.KeepAlive(inspection);
    }

    [Fact]
    public async Task FailedAndQuarantinedOpenReleaseRendererSnapshotTickets()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();

        byte[] malformed = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'");
        MarkdownSvgException malformedFailure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await renderer.OpenAsync(new MarkdownSvgOpenRequest(malformed)).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, malformedFailure.Reason);
        AssertNoLiveRendererOwnership(renderer);
        Assert.Equal(malformed.Length, renderer.SourceOwnership.GetSnapshot().PeakBytes);

        byte[] quarantined = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><rect width='1' height='1'/></svg>");
        renderer.QuarantineWorkerFailure(Convert.ToHexString(SHA256.HashData(quarantined)));
        MarkdownSvgException quarantineFailure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await renderer.OpenAsync(new MarkdownSvgOpenRequest(quarantined)).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal(MarkdownSvgFailureReason.WorkerFailure, quarantineFailure.Reason);
        AssertNoLiveRendererOwnership(renderer);
        Assert.Equal(1, renderer.SourceOwnership.GetSnapshot().PeakSnapshots);
    }

    [Fact]
    public async Task DocumentLifetimeHoldsTicketUntilActiveRenderDrainCompletes()
    {
        var ledger = new SvgProviderSnapshotLedger();
        byte[] callerBytes = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='2' height='1'/>");
        var request = new MarkdownSvgOpenRequest(callerBytes);
        Task<SvgPreflightResult> inspection = SvgPreflight.InspectAsync(
            request,
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None,
            ownershipLedger: ledger);
        SvgPreflightResult preflight = await inspection.WaitAsync(TimeSpan.FromSeconds(5));
        var sourceSnapshot = new SvgDocumentSourceSnapshot(preflight, request);
        var lifetime = new SvgDocumentLifetime(sourceSnapshot.Release);
        SvgDocumentLifetime.RenderLease activeRender =
            lifetime.TryEnterRender() ?? throw new InvalidOperationException("The test render lease was not admitted.");

        lifetime.Dispose();

        Assert.Equal(preflight.Source.AllocatedCapacity, ledger.GetSnapshot().LiveBytes);
        Assert.Equal(1, ledger.GetSnapshot().LiveSnapshots);
        Assert.Equal(
            0,
            sourceSnapshot.GetForRender().OpenRequest.SanitizedSvgBytes.Length);
        Assert.Equal(callerBytes,
            sourceSnapshot.GetForRender().Preflight.Source.CopyToArrayForTest());

        activeRender.Dispose();

        Assert.Equal(0, ledger.GetSnapshot().LiveBytes);
        Assert.Equal(0, ledger.GetSnapshot().LiveSnapshots);
        Assert.Throws<ObjectDisposedException>(() => sourceSnapshot.GetForRender());
        GC.KeepAlive(lifetime);
        GC.KeepAlive(activeRender);
        GC.KeepAlive(inspection);
    }

    [Fact]
    public async Task IndependentRendererLedgersContributeToOneLocalAggregate()
    {
        var aggregate = new SvgProviderSnapshotLedger();
        var firstLedger = new SvgProviderSnapshotLedger(aggregate);
        var secondLedger = new SvgProviderSnapshotLedger(aggregate);
        byte[] firstInput = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>");
        byte[] secondInput = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg' width='2' height='1'/>");
        Task<SvgPreflightResult> firstTask = SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(firstInput),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None,
            ownershipLedger: firstLedger);
        Task<SvgPreflightResult> secondTask = SvgPreflight.InspectAsync(
            new MarkdownSvgOpenRequest(secondInput),
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None,
            ownershipLedger: secondLedger);
        SvgPreflightResult first = await firstTask.WaitAsync(TimeSpan.FromSeconds(5));
        SvgPreflightResult second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(firstInput.Length, firstLedger.GetSnapshot().LiveBytes);
        Assert.Equal(secondInput.Length, secondLedger.GetSnapshot().LiveBytes);
        Assert.Equal(firstInput.Length + secondInput.Length, aggregate.GetSnapshot().LiveBytes);
        Assert.Equal(2, aggregate.GetSnapshot().LiveSnapshots);

        first.ReleaseSource();
        Assert.Equal(secondInput.Length, aggregate.GetSnapshot().LiveBytes);
        Assert.Equal(1, aggregate.GetSnapshot().LiveSnapshots);
        second.ReleaseSource();
        Assert.Equal(0, aggregate.GetSnapshot().LiveBytes);
        Assert.Equal(0, aggregate.GetSnapshot().LiveSnapshots);
        GC.KeepAlive(firstTask);
        GC.KeepAlive(secondTask);
    }

    private static void AssertNoLiveRendererOwnership(ResvgMarkdownSvgRenderer renderer)
    {
        SvgProviderSnapshotLedgerSnapshot snapshot = renderer.SourceOwnership.GetSnapshot();
        Assert.Equal(0, snapshot.LiveBytes);
        Assert.Equal(0, snapshot.LiveSnapshots);
    }

    private sealed class CancelOnReadMemoryManager(
        byte[] bytes,
        CancellationTokenSource cancellation) : MemoryManager<byte>
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public ReadOnlyMemory<byte> BorrowedMemory => CreateMemory(bytes.Length);

        public override Span<byte> GetSpan()
        {
            if (Interlocked.Increment(ref _readCount) == 1)
                cancellation.Cancel();
            return bytes;
        }

        public override MemoryHandle Pin(int elementIndex = 0) =>
            throw new NotSupportedException("The test memory manager does not support pinning.");

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed class CountingMemoryManager(byte[] bytes) : MemoryManager<byte>
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        public ReadOnlyMemory<byte> BorrowedMemory => CreateMemory(bytes.Length);

        public void ResetReadCount() => Volatile.Write(ref _readCount, 0);

        public override Span<byte> GetSpan()
        {
            Interlocked.Increment(ref _readCount);
            return bytes;
        }

        public override MemoryHandle Pin(int elementIndex = 0) =>
            throw new NotSupportedException("The test memory manager does not support pinning.");

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed class TrackCancellationListener : EventListener
    {
        private readonly long _scopeId;
        private readonly CancellationTokenSource _cancellation;
        private int _initialized;
        private int _matchedTrackEventCount;

        public TrackCancellationListener(long scopeId, CancellationTokenSource cancellation)
        {
            _scopeId = scopeId;
            _cancellation = cancellation;
            Volatile.Write(ref _initialized, 1);
        }

        public int MatchedTrackEventCount => Volatile.Read(ref _matchedTrackEventCount);

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name.Equals(
                "MarkdownRenderer.Svg.Resvg.SourceSnapshots",
                StringComparison.Ordinal))
            {
                EnableEvents(eventSource, EventLevel.Verbose);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (Volatile.Read(ref _initialized) == 0 ||
                eventData.EventId != 1 ||
                eventData.Payload is not { Count: >= 6 } payload)
                return;

            if (Convert.ToInt64(payload[0]) != _scopeId ||
                Convert.ToInt64(payload[1]) <= 0 ||
                Convert.ToInt64(payload[3]) <= 0)
            {
                return;
            }

            Interlocked.Increment(ref _matchedTrackEventCount);
            _cancellation.Cancel();
        }
    }
}
#endif
