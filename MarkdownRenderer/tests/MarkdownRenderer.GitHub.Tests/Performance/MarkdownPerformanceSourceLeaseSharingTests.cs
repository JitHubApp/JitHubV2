using System.Collections.Concurrent;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Images;
using MarkdownRenderer.Images.Internal;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.MarkdownPerformanceSourceLeaseFixture;

namespace MarkdownRenderer.GitHub.Tests.Performance;

/// <summary>
/// Checks how consumers share leased sources: one reservation and lease
/// root across documents and requests, cache waits and eviction during
/// admission, fetch slot and consumer lease release, and oversized
/// sources.
/// </summary>
public sealed class MarkdownPerformanceSourceLeaseSharingTests
{
    [Fact]
    public async Task ConcurrentDocumentsShareOneReservationUntilTheLastConsumerReleasesIt()
    {
        var resolver = new GateLeasedResolver();
        await using var session = CreateSession(maxSourceBytes: 16, sourceCacheBytes: 0);
        var context = new MarkdownImageResolveContext(null);
        IMarkdownPerformanceSessionInternal internalSession = session;
        using IMarkdownPerformanceDocumentScope first = internalSession.OpenDocument(resolver, context);
        using IMarkdownPerformanceDocumentScope second = internalSession.OpenDocument(resolver, context);
        Task<IMarkdownImageResolutionLease> firstTask = first.ResolveWithSourceLeaseAsync(
            "shared-flight.png", context, CancellationToken.None).AsTask();
        Task<IMarkdownImageResolutionLease>? secondTask = null;

        try
        {
            await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            secondTask = second.ResolveWithSourceLeaseAsync(
                "shared-flight.png", context, CancellationToken.None).AsTask();
            Assert.Equal(1, resolver.CallCount);
            resolver.Release.TrySetResult(true);
            IMarkdownImageResolutionLease[] leases = await Task.WhenAll(firstTask, secondTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(4, session.GetSnapshot().InFlightSourceBytes);
            leases[0].Dispose();
            Assert.Equal(4, session.GetSnapshot().InFlightSourceBytes);
            leases[1].Dispose();
            Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
        }
        finally
        {
            resolver.Release.TrySetResult(true);
            await DisposeWhenCompleteAsync(firstTask);
            if (secondTask is not null)
                await DisposeWhenCompleteAsync(secondTask);
        }
    }

    [Fact]
    public async Task ConcurrentRequestsInOneDocumentShareOneResolutionLeaseRoot()
    {
        var resolver = new GateLeasedResolver();
        await using var session = CreateSession(maxSourceBytes: 16, sourceCacheBytes: 0);
        var context = new MarkdownImageResolveContext(null);
        using IMarkdownPerformanceDocumentScope document =
            ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver, context);
        Task<IMarkdownImageResolutionLease> firstTask = document.ResolveWithSourceLeaseAsync(
            "document-flight.png", context, CancellationToken.None).AsTask();
        Task<IMarkdownImageResolutionLease>? secondTask = null;

        try
        {
            await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            secondTask = document.ResolveWithSourceLeaseAsync(
                "document-flight.png", context, CancellationToken.None).AsTask();
            Assert.Equal(1, resolver.CallCount);
            resolver.Release.TrySetResult(true);
            IMarkdownImageResolutionLease[] leases = await Task.WhenAll(firstTask, secondTask)
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(4, session.GetSnapshot().InFlightSourceBytes);
            leases[0].Dispose();
            Assert.Equal(4, session.GetSnapshot().InFlightSourceBytes);
            leases[1].Dispose();
            Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
        }
        finally
        {
            resolver.Release.TrySetResult(true);
            await DisposeWhenCompleteAsync(firstTask);
            if (secondTask is not null)
                await DisposeWhenCompleteAsync(secondTask);
        }
    }

    [Fact]
    public async Task CancelingOneDocumentWaiterDoesNotCancelTheRemainingConsumer()
    {
        var resolver = new GateLeasedResolver();
        await using var session = CreateSession(maxSourceBytes: 16, sourceCacheBytes: 0);
        var context = new MarkdownImageResolveContext(null);
        using IMarkdownPerformanceDocumentScope document =
            ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver, context);
        using var canceledToken = new RetirableCancellationTokenSource();
        Task<IMarkdownImageResolutionLease> canceled = document.ResolveWithSourceLeaseAsync(
            "canceled-waiter.png", context, canceledToken.Token).AsTask();
        Task<IMarkdownImageResolutionLease>? surviving = null;

        try
        {
            await resolver.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            surviving = document.ResolveWithSourceLeaseAsync(
                "canceled-waiter.png", context, CancellationToken.None).AsTask();
            canceledToken.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => canceled.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(4, session.GetSnapshot().InFlightSourceBytes);

            resolver.Release.TrySetResult(true);
            IMarkdownImageResolutionLease lease = await surviving.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, resolver.CallCount);
            Assert.Equal(4, session.GetSnapshot().InFlightSourceBytes);
            lease.Dispose();
            Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
        }
        finally
        {
            resolver.Release.TrySetResult(true);
            canceledToken.Cancel();
            await DisposeWhenCompleteAsync(canceled);
            if (surviving is not null)
                await DisposeWhenCompleteAsync(surviving);
        }
    }

    [Fact]
    public async Task CacheWaitDoesNotExposeBytesUntilAdmissionAndGrantIsReleasedByConsumer()
    {
        var resolver = new LeasedResolver(async (source, admission, token) =>
            await CreateLeasedResultAsync(source, 8, admission, token, () => [1, 2, 3, 4, 5, 6, 7, 8]));
        await using var session = CreateSession(maxSourceBytes: 8, sourceCacheBytes: 32);
        var context = new MarkdownImageResolveContext(null);
        IMarkdownPerformanceSessionInternal internalSession = session;
        using IMarkdownPerformanceDocumentScope first = internalSession.OpenDocument(resolver, context);
        using IMarkdownPerformanceDocumentScope second = internalSession.OpenDocument(resolver, context);
        IMarkdownImageResolutionLease? firstLease = null;
        IMarkdownImageResolutionLease? secondLease = null;
        Task<IMarkdownImageResolutionLease>? waitingCacheHit = null;
        try
        {
            firstLease = await first.ResolveWithSourceLeaseAsync(
                "cached.png", context, CancellationToken.None);
            waitingCacheHit = second.ResolveWithSourceLeaseAsync(
                "cached.png", context, CancellationToken.None).AsTask();

            await WaitUntilAsync(() => session.GetSnapshot().PendingSourceByteRequests == 1);
            Assert.False(waitingCacheHit.IsCompleted);
            Assert.Equal(0, session.GetSnapshot().SourceCacheHits);
            Assert.Equal(8, session.GetSnapshot().InFlightSourceBytes);

            firstLease.Dispose();
            firstLease = null;
            secondLease = await waitingCacheHit.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, resolver.CallCount);
            Assert.Equal(1, session.GetSnapshot().SourceCacheHits);
            Assert.Equal(8, session.GetSnapshot().InFlightSourceBytes);

            secondLease.Dispose();
            secondLease = null;
            Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
        }
        finally
        {
            firstLease?.Dispose();
            secondLease?.Dispose();
            if (waitingCacheHit is not null)
                await DisposeWhenCompleteAsync(waitingCacheHit);
        }
    }

    [Fact]
    public async Task EvictionDuringCacheAdmissionDoesNotPinStaleArrayAndFallsBackToResolver()
    {
        WeakReference<byte[]>? firstBytes = null;
        int allocations = 0;
        var resolver = new LeasedResolver(async (source, admission, token) =>
            await CreateLeasedResultAsync(source, 4, admission, token, () =>
            {
                byte[] bytes = [1, 2, 3, 4];
                if (Interlocked.Increment(ref allocations) == 1)
                    firstBytes = new WeakReference<byte[]>(bytes);
                return bytes;
            }));
        await using var session = CreateSession(maxSourceBytes: 4, sourceCacheBytes: 16);
        var context = new MarkdownImageResolveContext(null);
        IMarkdownPerformanceSessionInternal internalSession = session;
        using IMarkdownPerformanceDocumentScope first = internalSession.OpenDocument(resolver, context);
        using IMarkdownPerformanceDocumentScope second = internalSession.OpenDocument(resolver, context);
        IMarkdownImageResolutionLease? original = null;
        IDisposable? blocker = null;
        Task<IMarkdownImageResolutionLease>? pending = null;
        try
        {
            original = await first.ResolveWithSourceLeaseAsync(
                "evicted.png", context, CancellationToken.None);
            WeakReference<byte[]> oldBytes = firstBytes ?? throw new InvalidOperationException(
                "The first resolver call did not expose its source buffer to the test.");
            original.Dispose();
            original = null;

            blocker = await internalSession.ReserveImageSourceBytesAsync(
                first, 4, CancellationToken.None);
            pending = second.ResolveWithSourceLeaseAsync(
                "evicted.png", context, CancellationToken.None).AsTask();
            await WaitUntilAsync(() => session.GetSnapshot().PendingSourceByteRequests == 1);
            Assert.False(pending.IsCompleted);

            session.Trim();
            await AssertCollectedAsync(oldBytes);

            blocker.Dispose();
            blocker = null;
            IMarkdownImageResolutionLease replacement = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                Assert.Equal(2, resolver.CallCount);
                Assert.Equal(2, allocations);
                Assert.Equal(0, session.GetSnapshot().PendingSourceByteRequests);
                Assert.Equal(0, session.GetSnapshot().SourceCacheHits);
                Assert.Equal(4, session.GetSnapshot().InFlightSourceBytes);
                Assert.NotNull(replacement.Resolution.Asset);
            }
            finally
            {
                replacement.Dispose();
            }
        }
        finally
        {
            original?.Dispose();
            blocker?.Dispose();
            if (pending is not null)
                await DisposeWhenCompleteAsync(pending);
        }

        Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
        Assert.Equal(0, session.GetSnapshot().PendingSourceByteRequests);
    }

    [Fact]
    public async Task FetchSlotIsReleasedBeforeConsumerReleasesItsSourceLease()
    {
        var resolver = new MultiSourceLeasedResolver();
        await using var session = CreateSession(
            maxFetches: 2,
            maxSourceBytes: 16,
            sourceCacheBytes: 0);
        var context = new MarkdownImageResolveContext(null);
        using IMarkdownPerformanceDocumentScope document =
            ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver, context);
        IMarkdownImageResolutionLease? first = null;
        IMarkdownImageResolutionLease? blockedLease = null;
        IMarkdownImageResolutionLease? concurrentLease = null;
        Task<IMarkdownImageResolutionLease>? blocked = null;
        Task<IMarkdownImageResolutionLease>? concurrent = null;
        try
        {
            first = await document.ResolveWithSourceLeaseAsync(
                "held.png", context, CancellationToken.None);
            blocked = document.ResolveWithSourceLeaseAsync(
                "blocked.png", context, CancellationToken.None).AsTask();
            await resolver.StartedFor("blocked.png").Task.WaitAsync(TimeSpan.FromSeconds(5));

            concurrent = document.ResolveWithSourceLeaseAsync(
                "concurrent.png", context, CancellationToken.None).AsTask();
            await resolver.StartedFor("concurrent.png").Task.WaitAsync(TimeSpan.FromSeconds(5));
            concurrentLease = await concurrent.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(first);
            Assert.Equal(6, session.GetSnapshot().InFlightSourceBytes);
            concurrentLease!.Dispose();
            concurrentLease = null;
            Assert.Equal(4, session.GetSnapshot().InFlightSourceBytes);

            resolver.ReleaseBlocked.TrySetResult(true);
            blockedLease = await blocked!.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(4, session.GetSnapshot().InFlightSourceBytes);
            first!.Dispose();
            first = null;
            Assert.Equal(2, session.GetSnapshot().InFlightSourceBytes);
            blockedLease.Dispose();
            blockedLease = null;
            Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
        }
        finally
        {
            resolver.ReleaseBlocked.TrySetResult(true);
            concurrentLease?.Dispose();
            first?.Dispose();
            blockedLease?.Dispose();
            if (blocked is not null)
                await DisposeWhenCompleteAsync(blocked);
            if (concurrent is not null)
                await DisposeWhenCompleteAsync(concurrent);
        }
    }

    [Fact]
    public async Task SessionDisposeWaitsForReturnedConsumerLeaseAndBuiltInReservation()
    {
        var resolver = new LeasedResolver(async (source, admission, token) =>
            await CreateLeasedResultAsync(source, 4, admission, token, () => [1, 2, 3, 4]));
        var session = CreateSession(maxSourceBytes: 16, sourceCacheBytes: 0);
        var context = new MarkdownImageResolveContext(null);
        using IMarkdownPerformanceDocumentScope document =
            ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver, context);
        IMarkdownImageResolutionLease? consumer = null;
        Task? consumerShutdown = null;
        try
        {
            consumer = await document.ResolveWithSourceLeaseAsync(
                "shutdown-consumer.png", context, CancellationToken.None);
            consumerShutdown = session.DisposeAsync().AsTask();
            Assert.False(consumerShutdown.IsCompleted);
            Assert.Equal(4, session.GetSnapshot().InFlightSourceBytes);
        }
        finally
        {
            consumer?.Dispose();
            if (consumerShutdown is not null)
                await consumerShutdown.WaitAsync(TimeSpan.FromSeconds(5));
            else
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        var builtInSession = CreateSession(maxSourceBytes: 16, sourceCacheBytes: 0);
        IDisposable? builtInReservation = null;
        Task? builtInShutdown = null;
        try
        {
            builtInReservation = await ((IMarkdownPerformanceSessionInternal)builtInSession)
                .ReserveImageSourceBytesAsync(new object(), 4, CancellationToken.None);
            builtInShutdown = builtInSession.DisposeAsync().AsTask();
            Assert.False(builtInShutdown.IsCompleted);
            Assert.Equal(4, builtInSession.GetSnapshot().InFlightSourceBytes);
        }
        finally
        {
            builtInReservation?.Dispose();
            if (builtInShutdown is not null)
                await builtInShutdown.WaitAsync(TimeSpan.FromSeconds(5));
            else
                await builtInSession.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
        Assert.Equal(0, builtInSession.GetSnapshot().InFlightSourceBytes);
    }

    [Fact]
    public async Task OversizedLeasedSourceFailsBeforePayloadAllocation()
    {
        int allocations = 0;
        var resolver = new LeasedResolver(async (source, admission, token) =>
            await CreateLeasedResultAsync(source, 9, admission, token, () =>
            {
                Interlocked.Increment(ref allocations);
                return new byte[9];
            }));
        await using var session = CreateSession(maxSourceBytes: 8, sourceCacheBytes: 0);
        var context = new MarkdownImageResolveContext(null);
        using IMarkdownPerformanceDocumentScope document =
            ((IMarkdownPerformanceSessionInternal)session).OpenDocument(resolver, context);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
        {
            _ = await document.ResolveWithSourceLeaseAsync(
                "oversized.png", context, CancellationToken.None);
        });

        Assert.Equal(1, resolver.CallCount);
        Assert.Equal(0, allocations);
        Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
        Assert.Equal(0, session.GetSnapshot().PendingSourceByteRequests);
    }

    private sealed class GateLeasedResolver(Action<byte[]>? onBytesCreated = null)
        : IMarkdownLeasedImageResolver
    {
        private int _callCount;
        internal int CallCount => Volatile.Read(ref _callCount);
        internal TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.NotHandled);

        public async ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            IDisposable? reservation = await admission.ReserveAsync(4, cancellationToken);
            MarkdownImageResolutionLease? result = null;
            try
            {
                byte[] bytes = [1, 2, 3, 4];
                onBytesCreated?.Invoke(bytes);
                result = new MarkdownImageResolutionLease(
                    MarkdownImageResolution.Resolved(
                        new MarkdownImageAsset(bytes, "image/png", CacheKey: source)),
                    reservation);
                reservation = null;
                Started.TrySetResult(true);
                await Release.Task;
                return result!;
            }
            catch
            {
                reservation?.Dispose();
                result?.Dispose();
                throw;
            }
        }
    }

    private sealed class MultiSourceLeasedResolver : IMarkdownLeasedImageResolver
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _started = new();
        internal TaskCompletionSource<bool> ReleaseBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource<bool> StartedFor(string source) =>
            _started.GetOrAdd(source, static _ => new(TaskCreationOptions.RunContinuationsAsynchronously));

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.NotHandled);

        public async ValueTask<MarkdownImageResolutionLease> ResolveWithSourceLeaseAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            IDisposable? reservation = await admission.ReserveAsync(2, cancellationToken);
            MarkdownImageResolutionLease? result = null;
            try
            {
                result = new MarkdownImageResolutionLease(
                    MarkdownImageResolution.Resolved(
                        new MarkdownImageAsset([1, 2], "image/png", CacheKey: source)),
                    reservation);
                reservation = null;
                StartedFor(source).TrySetResult(true);
                if (source == "blocked.png")
                    await ReleaseBlocked.Task;
                return result!;
            }
            catch
            {
                reservation?.Dispose();
                result?.Dispose();
                throw;
            }
        }
    }
}
