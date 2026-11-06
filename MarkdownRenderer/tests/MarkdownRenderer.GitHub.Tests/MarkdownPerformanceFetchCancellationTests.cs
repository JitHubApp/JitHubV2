using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks how session and document disposal cancel source fetches:
/// admitted fetches drain, callers are not blocked, synchronous resolver
/// prefixes and other waiters are unaffected, and retired sessions do not
/// touch disposed semaphores.
/// </summary>
public sealed class MarkdownPerformanceFetchCancellationTests
{
    [Fact]
    public async Task DisposeAsync_CancelsAndDrainsAdmittedFetches()
    {
        var resolver = new BlockingResolver();
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        Task<MarkdownImageResolution> pending = document.ResolveAsync(
            "blocked.png", context, CancellationToken.None).AsTask();
        await resolver.BlockedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task DocumentDisposal_CancelsAnActiveFetchWithoutBlockingTheCaller()
    {
        var resolver = new BlockingResolver();
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        var document = session.OpenDocument(resolver, context);
        Task<MarkdownImageResolution> pending = document.ResolveAsync(
            "blocked.png", context, CancellationToken.None).AsTask();
        await resolver.BlockedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        document.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task SynchronousResolverPrefix_DoesNotBlockOtherSourceFlights()
    {
        using var resolver = new SynchronousBlockingResolver();
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);

        Task<MarkdownImageResolution> blocked = Task.Run(async () =>
            await document.ResolveAsync("blocked.png", context, CancellationToken.None));
        await resolver.BlockedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            MarkdownImageResolution visible = await Task.Run(async () =>
                await document.ResolveAsync("visible.png", context, CancellationToken.None))
                .WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(visible.IsHandled);
            Assert.Equal(1, resolver.BlockedCalls);
            Assert.Equal(1, resolver.VisibleCalls);
        }
        finally
        {
            resolver.ReleaseBlocked();
            await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task OneDocumentCancellation_DoesNotAbortAnotherWaiter()
    {
        var resolver = new BlockingResolver();
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        var first = session.OpenDocument(resolver, context);
        using var second = session.OpenDocument(resolver, context);

        Task<MarkdownImageResolution> canceled = first.ResolveAsync(
            "blocked.png", context, CancellationToken.None).AsTask();
        await resolver.BlockedStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<MarkdownImageResolution> surviving = second.ResolveAsync(
            "blocked.png", context, CancellationToken.None).AsTask();
        first.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceled.WaitAsync(TimeSpan.FromSeconds(5)));

        resolver.ReleaseBlocked.TrySetResult(true);
        Assert.True((await surviving.WaitAsync(TimeSpan.FromSeconds(5))).IsHandled);
        Assert.Equal(1, resolver.BlockedCalls);
    }

    [Fact]
    public async Task RetiredSession_CancelsAnOldDocumentInsteadOfTouchingDisposedSemaphores()
    {
        var resolver = new RecordingResolver();
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var context = new MarkdownImageResolveContext(null);
        using var document = session.OpenDocument(resolver, context);
        await document.ResolveAsync("late.png", context, CancellationToken.None);
        await session.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => document.ResolveAsync("late.png", context, CancellationToken.None).AsTask());
    }

    private sealed class SynchronousBlockingResolver : IMarkdownImageResolver, IDisposable
    {
        private readonly ManualResetEventSlim _releaseBlocked = new();
        private int _visibleCalls;
        private int _blockedCalls;
        internal int VisibleCalls => Volatile.Read(ref _visibleCalls);
        internal int BlockedCalls => Volatile.Read(ref _blockedCalls);
        internal TaskCompletionSource<bool> BlockedStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseBlocked() => _releaseBlocked.Set();
        public void Dispose() => _releaseBlocked.Dispose();

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            if (source == "blocked.png")
            {
                Interlocked.Increment(ref _blockedCalls);
                BlockedStarted.TrySetResult(true);
                _releaseBlocked.Wait(cancellationToken);
            }
            else
            {
                Interlocked.Increment(ref _visibleCalls);
            }

            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1, 2, 3], "image/png", CacheKey: source)));
        }
    }
}
