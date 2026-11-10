using MarkdownRenderer.Images;
using MarkdownRenderer.Math;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Performance.Internal.Scheduling;
using Markdig;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks the performance session lifecycle: observer notification,
/// scheduled layout work and its drain on disposal, document scope
/// ownership, resolver threading and option validation.
/// </summary>
public sealed class MarkdownPerformanceSessionTests
{
    [Fact]
    public void DisposedSession_NotifiesCurrentAndLateControlObserversExactlyOnce()
    {
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var api = (IMarkdownPerformanceSessionInternal)session;
        int notifications = 0;
        EventHandler currentObserver = (_, _) => Interlocked.Increment(ref notifications);
        api.Disposed += currentObserver;

        session.Dispose();
        session.Dispose();

        Assert.Equal(1, notifications);
        bool lateObserverNotified = false;
        EventHandler lateObserver = (_, _) => lateObserverNotified = true;
        api.Disposed += lateObserver;
        Assert.True(lateObserverNotified);
        api.Disposed -= lateObserver;
        api.Disposed -= currentObserver;
    }

    [Fact]
    public async Task LayoutWork_ReturnsSchedulerTaskAndSessionDisposalDrainsActiveWork()
    {
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var api = (IMarkdownPerformanceSessionInternal)session;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<int> scheduled = api.ScheduleLayoutWorkAsync(
            MarkdownLayoutWorkPriority.Primary,
            _ =>
            {
                entered.Set();
                release.Wait();
                return 42;
            },
            CancellationToken.None);

        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(typeof(Task<int>), scheduled.GetType());

            Task retirement = session.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            release.Set();

            Assert.Equal(42, await scheduled.WaitAsync(TimeSpan.FromSeconds(5)));
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task DisposeAsync_DrainsAnInFlightSessionParseBeforeFinalRetirementDrain()
    {
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var parser = new BlockingLegacyParser();
        var api = (IMarkdownPerformanceSessionInternal)session;

        Task<MarkdownRenderer.Document.MarkdownDocument?> parse = api.ParseAndPrepareDocumentAsync(
            engine: null,
            document: null,
            source: "parse held through shutdown",
            legacyRegistry: new MarkdownExtensionRegistry(),
            documentOwner: parser,
            cancellationToken: CancellationToken.None).AsTask();

        try
        {
            await parser.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task shutdown = session.DisposeAsync().AsTask();
            Assert.False(shutdown.IsCompleted);

            parser.Release.TrySetResult(true);
            MarkdownRenderer.Document.MarkdownDocument? document =
                await parse.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(document);
            Assert.Equal("parse held through shutdown", document!.Source);
            await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            parser.Release.TrySetResult(true);
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task DeferredPlanRelease_AndSessionShutdownCanRaceWithoutLosingRetirement()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .Build();
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        try
        {
            var api = (IMarkdownPerformanceSessionInternal)session;
            var owner = new object();
            const string source = "before $\\frac{1}{2}$ after";
            MarkdownRenderer.Document.MarkdownDocument document =
                Assert.IsType<MarkdownRenderer.Document.MarkdownDocument>(
                    await api.ParseAndPrepareDocumentAsync(
                        engine,
                        document: null,
                        source: source,
                        legacyRegistry: new MarkdownExtensionRegistry(),
                        documentOwner: owner,
                        cancellationToken: CancellationToken.None));
            Assert.True(api.HasRegisteredDeferredSceneWork(owner));

            var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task release = Task.Run(async () =>
            {
                await start.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await api.ReleaseDeferredSceneDocument(owner).WaitAsync(TimeSpan.FromSeconds(5));
            });
            Task shutdown = Task.Run(async () =>
            {
                await start.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            });

            start.TrySetResult(true);
            await Task.WhenAll(release, shutdown).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(api.HasRegisteredDeferredSceneWork(owner));
            Assert.Equal(source, document.Source);
        }
        finally
        {
            await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task LayoutWork_PreCanceledCallerTokenRetiresTrackedAdmission()
    {
        await using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var api = (IMarkdownPerformanceSessionInternal)session;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Task<int> canceled = api.ScheduleLayoutWorkAsync(
            MarkdownLayoutWorkPriority.Primary,
            _ => 42,
            cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => canceled.WaitAsync(TimeSpan.FromSeconds(5)));
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task LayoutWorkRejectedAfterDisposalReturnsCanceledTask()
    {
        var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var api = (IMarkdownPerformanceSessionInternal)session;
        int workRuns = 0;

        Task<int> rejected = api.ScheduleLayoutWorkAsync(
            MarkdownLayoutWorkPriority.Primary,
            _ => Interlocked.Increment(ref workRuns),
            CancellationToken.None);

        Assert.Equal(TaskStatus.Canceled, rejected.Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => rejected.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref workRuns));
    }

    [Fact]
    public void SessionOwnsAndReusesControlDocumentScopesUntilIdentityChangesOrOwnerReleases()
    {
        using var session = new MarkdownPerformanceSession(new MarkdownPerformanceOptions());
        var api = (IMarkdownPerformanceSessionInternal)session;
        var owner = new object();
        var resolver = new RecordingResolver();
        var context = new MarkdownImageResolveContext(null);
        var parsed = Markdown.Parse("![image](image.png)");

        IMarkdownPerformanceDocumentScope first = Assert.IsAssignableFrom<IMarkdownPerformanceDocumentScope>(
            api.PrepareDocumentScope(owner, "source-a", 4, parsed, null, resolver, context));
        IMarkdownPerformanceDocumentScope same = Assert.IsAssignableFrom<IMarkdownPerformanceDocumentScope>(
            api.PrepareDocumentScope(owner, "source-a", 4, parsed, null, resolver, context));
        IMarkdownPerformanceDocumentScope replacement = Assert.IsAssignableFrom<IMarkdownPerformanceDocumentScope>(
            api.PrepareDocumentScope(owner, "source-b", 4, parsed, null, resolver, context));

        Assert.Same(first, same);
        Assert.NotSame(first, replacement);
        Assert.True(first.IsDisposed);
        Assert.False(replacement.IsDisposed);

        api.ReleaseDocumentScope(owner);
        Assert.True(replacement.IsDisposed);
    }

    [Fact]
    public async Task PreparingDocument_NeverRunsSpeculativeResolverOnCallerThread()
    {
        using var session = new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive);
        var api = (IMarkdownPerformanceSessionInternal)session;
        var owner = new object();
        var resolver = new ThreadRecordingResolver();
        var context = new MarkdownImageResolveContext(null);
        var parsed = Markdown.Parse("![image](image.png)");
        int callerThread = 0;
        Exception? callerFailure = null;
        IMarkdownPerformanceDocumentScope? prepared = null;
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            try
            {
                prepared = api.PrepareDocumentScope(
                    owner, "![image](image.png)", 1, parsed, null, resolver, context);
            }
            catch (Exception exception)
            {
                callerFailure = exception;
            }
        });
        caller.Start();
        Assert.True(caller.Join(TimeSpan.FromSeconds(5)));
        Assert.Null(callerFailure);
        Assert.NotNull(prepared);
        int resolverThread = await resolver.CalledOnThread.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEqual(callerThread, resolverThread);
        api.ReleaseDocumentScope(owner);
    }

    [Fact]
    public void Options_RejectUnboundedSettings()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { SceneCacheBudgetBytes = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with
            {
                SceneCacheBudgetBytes = (IntPtr.Size == 4 ? 32L : 64L) * 1024 * 1024 + 1,
            }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { SourceCacheBudgetBytes = 65L * 1024 * 1024 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { ReservedVisibleImageFetches = 16 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentImageFetches = 32 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentCpuPreparations = 4 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentScenePreparations = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxConcurrentScenePreparations = 4 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxRasterOutputPixels = 8_388_609 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxInFlightSourceBytes = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { MaxInFlightSourceBytes = 65L * 1024 * 1024 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { ReservedVisibleSourceBytes = 0 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MarkdownPerformanceSession(
            MarkdownPerformanceOptions.Progressive with { ReservedVisibleSourceBytes = 64L * 1024 * 1024 }));
    }

    private sealed class BlockingLegacyParser : IMarkdownPerformanceLegacyParser
    {
        internal TaskCompletionSource<bool> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ParsedMarkdown?> ParseLegacyMarkdownAsync(
            string source,
            MarkdownExtensionRegistry registry,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return new ParsedMarkdown(source, Markdown.Parse(source));
        }
    }

    private sealed class ThreadRecordingResolver : IMarkdownImageResolver
    {
        internal TaskCompletionSource<int> CalledOnThread { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<MarkdownImageResolution> ResolveAsync(
            string source,
            MarkdownImageResolveContext context,
            CancellationToken cancellationToken)
        {
            CalledOnThread.TrySetResult(Environment.CurrentManagedThreadId);
            return ValueTask.FromResult(MarkdownImageResolution.Resolved(
                new MarkdownImageAsset([1, 2, 3], "image/png", CacheKey: source)));
        }
    }
}
