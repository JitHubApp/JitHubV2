using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Performance;

/// <summary>
/// Checks the session source byte budgets: visible and speculative
/// budgets, byte waits canceled on retirement, the in-flight byte bound,
/// oversized reservations, eviction, bounded prefetch and trimming.
/// </summary>
public sealed class MarkdownPerformanceSourceBudgetTests
{
    [Fact]
    public async Task ByteAdmittedResolver_ReceivesVisibleAndSpeculativeBudgets()
    {
        var resolver = new ByteAdmittedResolver();
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxInFlightSourceBytes = 8,
                ReservedVisibleSourceBytes = 2,
            });
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);

        await document.PrefetchAsync(["prefetch.png"], CancellationToken.None);
        await document.ResolveAsync("visible.png", context, CancellationToken.None);

        Assert.Equal(6, resolver.SpeculativeMaximum);
        Assert.Equal(8, resolver.VisibleMaximum);
        Assert.Equal(0, resolver.LegacyCalls);
        Assert.Equal(0, session.ActiveSourceBytes);
        Assert.Equal(3, session.GetSnapshot().PeakInFlightSourceBytes);
        Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
        Assert.Equal(0, session.GetSnapshot().PendingSourceByteRequests);
    }

    [Fact]
    public async Task RetiringSession_CancelsAByteWaitEvenWhenResolverPassesNoToken()
    {
        var resolver = new WaitingByteAdmittedResolver();
        var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxConcurrentImageFetches = 2,
                ReservedVisibleImageFetches = 1,
                MaxInFlightSourceBytes = 8,
                ReservedVisibleSourceBytes = 2,
            });
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        Task<MarkdownImageResolution> occupied = document.ResolveAsync(
            "occupied.png", context, CancellationToken.None).AsTask();
        await resolver.Occupied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<MarkdownImageResolution> waiting = document.ResolveAsync(
            "waiting.png", context, CancellationToken.None).AsTask();
        await resolver.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => occupied);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(0, session.ActiveSourceBytes);
    }

    [Fact]
    public async Task VisibleResolveInAnotherDocument_SupersedesAnActiveSpeculativeRead()
    {
        var resolver = new PromotedByteAdmittedResolver();
        await using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxConcurrentImageFetches = 2,
                ReservedVisibleImageFetches = 1,
                MaxInFlightSourceBytes = 8,
                ReservedVisibleSourceBytes = 2,
            });
        var context = new MarkdownImageResolveContext(null);
        using var backgroundDocument = session.OpenDocument(resolver, context);
        using var visibleDocument = session.OpenDocument(resolver, context);
        Task prefetch = backgroundDocument.PrefetchAsync(["shared.png"], CancellationToken.None);
        await resolver.SpeculativeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        MarkdownImageResolution visible = await visibleDocument.ResolveAsync(
            "shared.png", context, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await prefetch.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotNull(visible.Asset);
        Assert.Equal(2, resolver.CallCount);
        Assert.Equal(0, session.ActiveSourceBytes);
    }

    [Fact]
    public async Task LargeAdmittedSourceStorm_NeverExceedsTheInFlightByteBudget()
    {
        var resolver = new ByteStormResolver();
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxConcurrentImageFetches = 4,
                ReservedVisibleImageFetches = 1,
                MaxInFlightSourceBytes = 8,
                ReservedVisibleSourceBytes = 2,
                SourceCacheBudgetBytes = 0,
            });
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        string[] sources = Enumerable.Range(0, 1_800)
            .Select(index => $"storm-{index:D4}.png")
            .ToArray();

        await document.PrefetchAsync(sources, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        MarkdownPerformanceSnapshot snapshot = session.GetSnapshot();
        Assert.Equal(sources.Length, resolver.CallCount);
        Assert.InRange(snapshot.PeakInFlightSourceBytes, 3, 6);
        Assert.Equal(0, snapshot.InFlightSourceBytes);
        Assert.Equal(0, snapshot.PendingSourceByteRequests);
        Assert.Equal(0, snapshot.SourceCacheBytes);
    }

    [Fact]
    public async Task OversizedSpeculativeReservation_DefersWithoutPoisoningVisibleImage()
    {
        var resolver = new DeferredByteAdmittedResolver();
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxInFlightSourceBytes = 8,
                ReservedVisibleSourceBytes = 2,
            });
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);

        await document.PrefetchAsync(["large.png"], CancellationToken.None);
        MarkdownImageResolution visible = await document.ResolveAsync(
            "large.png", context, CancellationToken.None);

        Assert.NotNull(visible.Asset);
        Assert.Equal(2, resolver.CallCount);
        Assert.Equal(0, session.GetSnapshot().ImageFetchFailures);
        Assert.Equal(0, session.GetSnapshot().InFlightSourceBytes);
    }

    [Fact]
    public async Task SourceBudget_EvictsWithoutReportingAnImageUnavailable()
    {
        var resolver = new RecordingResolver();
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { SourceCacheBudgetBytes = 5 });
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);

        await document.ResolveAsync("first.png", context, CancellationToken.None);
        await document.ResolveAsync("second.png", context, CancellationToken.None);
        MarkdownImageResolution firstAgain = await document.ResolveAsync(
            "first.png", context, CancellationToken.None);

        Assert.True(firstAgain.IsHandled);
        Assert.NotNull(firstAgain.Asset);
        Assert.Equal(3, resolver.CallCount);
        Assert.InRange(session.GetSnapshot().SourceCacheBytes, 0, 5);
        Assert.True(session.GetSnapshot().SourceCacheEvictions >= 1);
    }

    [Fact]
    public async Task LargeDocumentPrefetch_RemainsBoundedAndCompletes()
    {
        var resolver = new ConcurrentResolver();
        using var session = new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                MaxConcurrentImageFetches = 4,
                ReservedVisibleImageFetches = 1,
                SourceCacheBudgetBytes = 1_024,
            });
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        string[] sources = Enumerable.Range(0, 1_800)
            .Select(index => $"image-{index:D4}.png")
            .ToArray();

        await document.PrefetchAsync(sources, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(sources.Length, resolver.CallCount);
        Assert.InRange(resolver.PeakConcurrent, 1, 3);
        Assert.InRange(session.GetSnapshot().SourceCacheBytes, 0, 1_024);
        Assert.Equal(0, session.GetSnapshot().PendingImageFetches);
        Assert.Equal(0, session.GetSnapshot().ActiveImageFetches);
    }

    [Fact]
    public async Task Trim_RemovesRetainedSourceBytes()
    {
        var resolver = new RecordingResolver();
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        await document.ResolveAsync("image.png", context, CancellationToken.None);
        Assert.True(session.GetSnapshot().SourceCacheBytes > 0);

        session.Trim();

        Assert.Equal(0, session.GetSnapshot().SourceCacheBytes);
    }

    private sealed class ByteAdmittedResolver : IMarkdownImageSourceByteAdmittedResolver
    {
        internal long SpeculativeMaximum { get; private set; }
        internal long VisibleMaximum { get; private set; }
        internal int LegacyCalls { get; private set; }

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            LegacyCalls++;
            return ValueTask.FromResult(MarkdownImageResolution.Unavailable);
        }

        public async ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            if (source == "prefetch.png")
                SpeculativeMaximum = admission.MaximumReservationBytes;
            else
                VisibleMaximum = admission.MaximumReservationBytes;
            using (await admission.ReserveAsync(3, cancellationToken))
            {
                return MarkdownImageResolution.Resolved(
                    new MarkdownImageAsset([1, 2, 3], "image/png", CacheKey: source));
            }
        }
    }

    private sealed class WaitingByteAdmittedResolver : IMarkdownImageSourceByteAdmittedResolver
    {
        internal TaskCompletionSource<bool> Occupied { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Waiting { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The admitted path must be used.");

        public async ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            if (source == "occupied.png")
            {
                using IDisposable held = await admission.ReserveAsync(8, CancellationToken.None);
                Occupied.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            else
            {
                Waiting.TrySetResult(true);
                using IDisposable held = await admission.ReserveAsync(1, CancellationToken.None);
            }
            return MarkdownImageResolution.Unavailable;
        }
    }

    private sealed class PromotedByteAdmittedResolver : IMarkdownImageSourceByteAdmittedResolver
    {
        private int _calls;
        internal int CallCount => Volatile.Read(ref _calls);
        internal TaskCompletionSource<bool> SpeculativeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The admitted path must be used.");

        public async ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            using IDisposable held = await admission.ReserveAsync(1, cancellationToken);
            if (admission.MaximumReservationBytes == 6)
            {
                SpeculativeStarted.TrySetResult(true);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1], "image/png", CacheKey: source));
        }
    }

    private sealed class ByteStormResolver : IMarkdownImageSourceByteAdmittedResolver
    {
        private int _calls;
        internal int CallCount => Volatile.Read(ref _calls);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The admitted path must be used.");

        public async ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            using IDisposable held = await admission.ReserveAsync(3, cancellationToken);
            Interlocked.Increment(ref _calls);
            await Task.Delay(1, cancellationToken);
            return MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1, 2, 3], "image/png", CacheKey: source));
        }
    }

    private sealed class DeferredByteAdmittedResolver : IMarkdownImageSourceByteAdmittedResolver
    {
        private int _calls;
        internal int CallCount => Volatile.Read(ref _calls);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The admitted path must be used.");

        public async ValueTask<MarkdownImageResolution> ResolveWithSourceByteAdmissionAsync(
            string source,
            MarkdownImageResolveContext context,
            IMarkdownImageSourceByteAdmission admission,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            using IDisposable held = await admission.ReserveAsync(7, cancellationToken);
            return MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1], "image/png", CacheKey: source));
        }
    }

    private sealed class ConcurrentResolver : IMarkdownImageResolver
    {
        private int _active;
        private int _peak;
        private int _calls;
        internal int PeakConcurrent => Volatile.Read(ref _peak);
        internal int CallCount => Volatile.Read(ref _calls);

        public async ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            int active = Interlocked.Increment(ref _active);
            Interlocked.Increment(ref _calls);
            int observed;
            while ((observed = Volatile.Read(ref _peak)) < active &&
                   Interlocked.CompareExchange(ref _peak, active, observed) != observed)
            {
            }
            try
            {
                await Task.Delay(1, cancellationToken);
                return MarkdownImageResolution.Resolved(
                    new MarkdownImageAsset([1, 2, 3], "image/png", CacheKey: source));
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
