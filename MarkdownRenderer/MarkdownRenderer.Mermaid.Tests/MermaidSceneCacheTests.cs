using System.Globalization;
using System.Text;
using MarkdownRenderer.Mermaid;

namespace MarkdownRenderer.Mermaid.Tests;

public sealed class MermaidSceneCacheTests
{
    [Fact]
    public async Task InFlightRequestsAreDeduplicatedAndWaiterCancellationIsIndependent()
    {
        using var cache = new MermaidSceneCache(1024 * 1024);
        MermaidScene scene = CreateScene("shared");
        MermaidSceneCacheKey key = CreateKey("flowchart LR\nA-->B");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int factoryCalls = 0;

        async Task<MermaidRenderResult> Factory(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref factoryCalls);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Success(scene, "flowchart LR\nA-->B");
        }

        using var cancelledWaiter = new CancellationTokenSource();
        Task<MermaidRenderResult> first = cache.GetOrRenderAsync(
            key,
            "flowchart LR\nA-->B",
            Factory,
            () => AdmissionRejected("flowchart LR\nA-->B"),
            () => DeadlineExceeded("flowchart LR\nA-->B"),
            FutureDeadline(),
            cancelledWaiter.Token).AsTask();
        await entered.Task;
        Task<MermaidRenderResult> second = cache.GetOrRenderAsync(
            key,
            "flowchart LR\nA-->B",
            Factory,
            () => AdmissionRejected("flowchart LR\nA-->B"),
            () => DeadlineExceeded("flowchart LR\nA-->B"),
            FutureDeadline(),
            CancellationToken.None).AsTask();

        cancelledWaiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted);
        release.TrySetResult();

        MermaidRenderResult completed = await second;
        Assert.Equal(1, Volatile.Read(ref factoryCalls));
        Assert.Same(scene, completed.Scene);
        Assert.Equal(1, cache.Count);

        MermaidRenderResult cached = await cache.GetOrRenderAsync(
            key,
            "flowchart LR\nA-->B",
            _ => throw new InvalidOperationException("A retained scene must bypass the factory."),
            () => AdmissionRejected("flowchart LR\nA-->B"),
            () => DeadlineExceeded("flowchart LR\nA-->B"),
            FutureDeadline(),
            CancellationToken.None);
        Assert.Same(scene, cached.Scene);
        Assert.Equal("flowchart LR\nA-->B", cached.Fallback.Source);
    }

    [Fact]
    public async Task WeightedLruEvictsTheLeastRecentlyUsedScene()
    {
        MermaidScene sceneA = CreateScene(new string('a', 128));
        MermaidScene sceneB = CreateScene(new string('b', 128));
        long oneSceneBudget = MermaidSceneCache.EstimateWeightBytes(sceneA);
        using var cache = new MermaidSceneCache(oneSceneBudget);
        int factoryCalls = 0;

        await Render("A", sceneA);
        await Render("B", sceneB);
        MermaidRenderResult rerendered = await Render("A", sceneA);

        Assert.Equal(3, factoryCalls);
        Assert.Same(sceneA, rerendered.Scene);
        Assert.Equal(1, cache.Count);
        Assert.InRange(cache.RetainedBytes, 1, oneSceneBudget);

        async ValueTask<MermaidRenderResult> Render(string source, MermaidScene scene)
        {
            return await cache.GetOrRenderAsync(
                CreateKey(source),
                source,
                _ =>
                {
                    factoryCalls++;
                    return Task.FromResult(Success(scene, source));
                },
                () => AdmissionRejected(source),
                () => DeadlineExceeded(source),
                FutureDeadline(),
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task FailuresAreNeverRetained()
    {
        using var cache = new MermaidSceneCache(1024 * 1024);
        MermaidSceneCacheKey key = CreateKey("invalid");
        int factoryCalls = 0;

        for (int i = 0; i < 2; i++)
        {
            MermaidRenderResult result = await cache.GetOrRenderAsync(
                key,
                "invalid",
                _ =>
                {
                    factoryCalls++;
                    return Task.FromResult(new MermaidRenderResult(
                        MermaidRenderStatus.InvalidInput,
                        null,
                        [new MermaidDiagnostic("MMR0010", MermaidDiagnosticSeverity.Error, "invalid")],
                        "invalid"));
                },
                () => AdmissionRejected("invalid"),
                () => DeadlineExceeded("invalid"),
                FutureDeadline(),
                CancellationToken.None);
            Assert.True(result.ShouldUseFallback);
        }

        Assert.Equal(2, factoryCalls);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task LastCancelledWaiterCancelsAndRemovesAbandonedProducer()
    {
        using var cache = new MermaidSceneCache(1024 * 1024);
        MermaidSceneCacheKey key = CreateKey("flowchart LR\nA-->B");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callerCancellation = new CancellationTokenSource();

        Task<MermaidRenderResult> abandoned = cache.GetOrRenderAsync(
            key,
            "flowchart LR\nA-->B",
            async cancellationToken =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    producerCancelled.TrySetResult();
                }
                return new MermaidRenderResult(
                    MermaidRenderStatus.Cancelled,
                    null,
                    [new MermaidDiagnostic("MMR0005", MermaidDiagnosticSeverity.Error, "cancelled")],
                    "flowchart LR\nA-->B");
            },
            () => AdmissionRejected("flowchart LR\nA-->B"),
            () => DeadlineExceeded("flowchart LR\nA-->B"),
            FutureDeadline(),
            callerCancellation.Token).AsTask();
        await entered.Task;

        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        await producerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        MermaidScene replacement = CreateScene("replacement");
        MermaidRenderResult rerendered = await cache.GetOrRenderAsync(
            key,
            "flowchart LR\nA-->B",
            _ => Task.FromResult(Success(replacement, "flowchart LR\nA-->B")),
            () => AdmissionRejected("flowchart LR\nA-->B"),
            () => DeadlineExceeded("flowchart LR\nA-->B"),
            FutureDeadline(),
            CancellationToken.None);
        Assert.Same(replacement, rerendered.Scene);
    }

    [Fact]
    public async Task LastCancelledWaiterDoesNotRunProducerCancellationCallbacksInline()
    {
        const string source = "flowchart LR\nA-->B";
        using var cache = new MermaidSceneCache(1024 * 1024);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var producerExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new ManualResetEventSlim();
        using var callerCancellation = new CancellationTokenSource();

        Task<MermaidRenderResult> request = cache.GetOrRenderAsync(
            CreateKey(source),
            source,
            async cancellationToken =>
            {
                _ = cancellationToken.Register(() =>
                {
                    callbackEntered.TrySetResult();
                    if (!callbackRelease.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Producer cancellation callback was not released.");
                });
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    producerExited.TrySetResult();
                }

                return new MermaidRenderResult(
                    MermaidRenderStatus.Cancelled,
                    null,
                    [new MermaidDiagnostic("MMR0005", MermaidDiagnosticSeverity.Error, "cancelled")],
                    source);
            },
            () => AdmissionRejected(source),
            () => DeadlineExceeded(source),
            FutureDeadline(),
            callerCancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task cancelCaller = Task.Run(callerCancellation.Cancel);
        try
        {
            await cancelCaller.WaitAsync(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => request.WaitAsync(TimeSpan.FromSeconds(1)));
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await producerExited.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            callbackRelease.Set();
        }

        Assert.True(SpinWait.SpinUntil(
            () => cache.OutstandingRenderCount == 0 && cache.OutstandingSourceBytes == 0,
            TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task UniqueAdmissionIsCountBoundedWhileSameKeyCallersJoin()
    {
        const string sourceA = "flowchart LR\nA-->B";
        const string sourceB = "flowchart LR\nC-->D";
        using var cache = new MermaidSceneCache(
            1024 * 1024,
            maxOutstandingRenders: 1,
            maxOutstandingSourceBytes: 1024 * 1024);
        MermaidSceneCacheKey keyA = CreateKey(sourceA);
        MermaidSceneCacheKey keyB = CreateKey(sourceB);
        MermaidScene scene = CreateScene("shared");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int factoryCalls = 0;

        async Task<MermaidRenderResult> Factory(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref factoryCalls);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return Success(scene, sourceA);
        }

        Task<MermaidRenderResult> first = cache.GetOrRenderAsync(
            keyA,
            sourceA,
            Factory,
            () => AdmissionRejected(sourceA),
            () => DeadlineExceeded(sourceA),
            FutureDeadline(),
            CancellationToken.None).AsTask();
        await entered.Task;
        Task<MermaidRenderResult> joined = cache.GetOrRenderAsync(
            keyA,
            sourceA,
            Factory,
            () => AdmissionRejected(sourceA),
            () => DeadlineExceeded(sourceA),
            FutureDeadline(),
            CancellationToken.None).AsTask();

        MermaidRenderResult rejected = await cache.GetOrRenderAsync(
            keyB,
            sourceB,
            _ => throw new InvalidOperationException("Rejected work must not invoke its factory."),
            () => AdmissionRejected(sourceB),
            () => DeadlineExceeded(sourceB),
            FutureDeadline(),
            CancellationToken.None);

        Assert.Equal(MermaidRenderStatus.Overloaded, rejected.Status);
        Assert.Equal("MMR0013", Assert.Single(rejected.Diagnostics).Code);
        Assert.Equal(sourceB, rejected.Fallback.Source);
        Assert.Equal(1, cache.OutstandingRenderCount);
        Assert.Equal(Encoding.UTF8.GetByteCount(sourceA), cache.OutstandingSourceBytes);
        Assert.Equal(1, Volatile.Read(ref factoryCalls));

        release.TrySetResult();
        await Task.WhenAll(first, joined);

        Assert.Equal(0, cache.OutstandingRenderCount);
        Assert.Equal(0, cache.OutstandingSourceBytes);
    }

    [Fact]
    public async Task UniqueAdmissionIsAggregateUtf8ByteBounded()
    {
        const string sourceA = "Aé";
        const string sourceB = "Bé";
        int sourceBytes = Encoding.UTF8.GetByteCount(sourceA);
        using var cache = new MermaidSceneCache(
            1024 * 1024,
            maxOutstandingRenders: 2,
            maxOutstandingSourceBytes: sourceBytes);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<MermaidRenderResult> admitted = cache.GetOrRenderAsync(
            CreateKey(sourceA),
            sourceA,
            async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Success(CreateScene("A"), sourceA);
            },
            () => AdmissionRejected(sourceA),
            () => DeadlineExceeded(sourceA),
            FutureDeadline(),
            CancellationToken.None).AsTask();
        await entered.Task;

        MermaidRenderResult rejected = await cache.GetOrRenderAsync(
            CreateKey(sourceB),
            sourceB,
            _ => throw new InvalidOperationException("Rejected work must not invoke its factory."),
            () => AdmissionRejected(sourceB),
            () => DeadlineExceeded(sourceB),
            FutureDeadline(),
            CancellationToken.None);

        Assert.Equal(MermaidRenderStatus.Overloaded, rejected.Status);
        Assert.Equal(sourceBytes, cache.OutstandingSourceBytes);
        release.TrySetResult();
        await admitted;
    }

    [Fact]
    public async Task ExpiredProducerCannotPublishOrReturnAStaleScene()
    {
        const string source = "flowchart LR\nA-->B";
        using var cache = new MermaidSceneCache(
            1024 * 1024,
            maxOutstandingRenders: 1,
            maxOutstandingSourceBytes: 1024);

        MermaidRenderResult result = await cache.GetOrRenderAsync(
            CreateKey(source),
            source,
            _ => Task.FromResult(Success(CreateScene("late"), source)),
            () => AdmissionRejected(source),
            () => DeadlineExceeded(source),
            producerDeadlineTimestamp: 0,
            cancellationToken: CancellationToken.None);

        Assert.Equal(MermaidRenderStatus.TimedOut, result.Status);
        Assert.Equal("MMR0007", Assert.Single(result.Diagnostics).Code);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.OutstandingRenderCount);
        Assert.Equal(0, cache.OutstandingSourceBytes);
    }

    [Fact]
    public async Task ExpiredProducerOverridesLateFailureAndFaultWithTimeout()
    {
        const string source = "flowchart LR\nA-->B";
        using var cache = new MermaidSceneCache(1024 * 1024);

        MermaidRenderResult lateFailure = await cache.GetOrRenderAsync(
            CreateKey(source),
            source,
            _ => Task.FromResult(new MermaidRenderResult(
                MermaidRenderStatus.NativeFailure,
                null,
                [new MermaidDiagnostic("MMR0012", MermaidDiagnosticSeverity.Error, "late failure")],
                source)),
            () => AdmissionRejected(source),
            () => DeadlineExceeded(source),
            producerDeadlineTimestamp: 0,
            cancellationToken: CancellationToken.None);
        MermaidRenderResult lateFault = await cache.GetOrRenderAsync(
            CreateKey(source + "\nB-->C"),
            source + "\nB-->C",
            _ => Task.FromException<MermaidRenderResult>(
                new InvalidOperationException("late producer fault")),
            () => AdmissionRejected(source),
            () => DeadlineExceeded(source),
            producerDeadlineTimestamp: 0,
            cancellationToken: CancellationToken.None);

        Assert.Equal(MermaidRenderStatus.TimedOut, lateFailure.Status);
        Assert.Equal(MermaidRenderStatus.TimedOut, lateFault.Status);
        Assert.Equal("MMR0007", Assert.Single(lateFailure.Diagnostics).Code);
        Assert.Equal("MMR0007", Assert.Single(lateFault.Diagnostics).Code);
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.OutstandingRenderCount);
        Assert.Equal(0, cache.OutstandingSourceBytes);
    }

    [Fact]
    public async Task LateJoinerRetriesAfterOlderSharedDeadlineExpires()
    {
        const string source = "flowchart LR\nA-->B";
        using var cache = new MermaidSceneCache(1024 * 1024);
        MermaidScene replacement = CreateScene("fresh request");
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstFactories = 0;
        int secondFactories = 0;

        Task<MermaidRenderResult> first = cache.GetOrRenderAsync(
            CreateKey(source),
            source,
            async _ =>
            {
                Interlocked.Increment(ref firstFactories);
                firstEntered.TrySetResult();
                await releaseFirst.Task;
                return Success(CreateScene("expired"), source);
            },
            () => AdmissionRejected(source),
            () => DeadlineExceeded(source),
            producerDeadlineTimestamp: 0,
            cancellationToken: CancellationToken.None).AsTask();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<MermaidRenderResult> lateJoiner = cache.GetOrRenderAsync(
            CreateKey(source),
            source,
            _ =>
            {
                Interlocked.Increment(ref secondFactories);
                return Task.FromResult(Success(replacement, source));
            },
            () => AdmissionRejected(source),
            () => DeadlineExceeded(source),
            FutureDeadline(),
            CancellationToken.None).AsTask();
        releaseFirst.TrySetResult();

        Assert.Equal(MermaidRenderStatus.TimedOut, (await first).Status);
        MermaidRenderResult completed = await lateJoiner;
        Assert.Equal(MermaidRenderStatus.Success, completed.Status);
        Assert.Same(replacement, completed.Scene);
        Assert.Equal(1, Volatile.Read(ref firstFactories));
        Assert.Equal(1, Volatile.Read(ref secondFactories));
        Assert.Equal(0, cache.OutstandingRenderCount);
        Assert.Equal(0, cache.OutstandingSourceBytes);
    }

    [Fact]
    public void KeySeparatesSourceVersionsConfigurationThemeAndFontCatalog()
    {
        const string source = "flowchart LR\nA-->B";
        MermaidSceneCacheKey baseline = CreateKey(source);
        MermaidSceneCacheKey differentSource = CreateKey(source + "\nB-->C");
        MermaidSceneCacheKey differentTheme = CreateKey(
            source,
            MermaidRenderOptions.Default with { Theme = MermaidThemeVariant.Dark });
        MermaidSceneCacheKey differentLayout = CreateKey(
            source,
            MermaidRenderOptions.Default with { Layout = MermaidLayoutMode.Dagre });
        MermaidSceneCacheKey differentFont = MermaidSceneCacheKey.Create(
            Encoding.UTF8.GetBytes(source),
            MermaidRenderOptions.Default,
            new MermaidFontCatalog(["Arial"]),
            CultureInfo.InvariantCulture);
        MermaidSceneCacheKey differentCulture = MermaidSceneCacheKey.Create(
            Encoding.UTF8.GetBytes(source),
            MermaidRenderOptions.Default,
            MermaidFontCatalog.Default,
            CultureInfo.GetCultureInfo("fr-FR"));

        Assert.NotEqual(baseline, differentSource);
        Assert.NotEqual(baseline, differentTheme);
        Assert.NotEqual(baseline, differentLayout);
        Assert.NotEqual(baseline, differentFont);
        if (!string.Equals(CultureInfo.CurrentUICulture.Name, "fr-FR", StringComparison.OrdinalIgnoreCase))
            Assert.NotEqual(baseline, differentCulture);
        Assert.Equal("11.16.1", baseline.MermaidCompatibilityVersion);
        Assert.Equal("ac53f21ba97e5bd8da7f849fbcdffe64695171e6", baseline.MermanCommit);
        Assert.Equal(MermaidSceneVersion.Current, baseline.MmirVersion);
        Assert.Equal(NativeMethods.PackedAbiVersion, baseline.NativeAbiVersion);
    }

    [Fact]
    public void IncrementalUtf8FingerprintMatchesByteBasedKey()
    {
        const string source = "flowchart LR\nα[😀]-->é";
        byte[] sourceBytes = Encoding.UTF8.GetBytes(source);

        MermaidSceneCacheKey byteKey = MermaidSceneCacheKey.Create(
            sourceBytes,
            MermaidRenderOptions.Default,
            MermaidFontCatalog.Default,
            CultureInfo.GetCultureInfo("fr-FR"));
        MermaidSceneCacheKey incrementalKey = MermaidSceneCacheKey.Create(
            source,
            sourceBytes.Length,
            MermaidRenderOptions.Default,
            MermaidFontCatalog.Default,
            CultureInfo.GetCultureInfo("fr-FR"));

        Assert.Equal(byteKey, incrementalKey);
    }

    private static MermaidSceneCacheKey CreateKey(
        string source,
        MermaidRenderOptions? options = null) =>
        MermaidSceneCacheKey.Create(
            Encoding.UTF8.GetBytes(source),
            options ?? MermaidRenderOptions.Default,
            MermaidFontCatalog.Default,
            CultureInfo.InvariantCulture);

    private static MermaidScene CreateScene(string text) => new(
        MermaidSceneVersion.Current,
        new MermaidViewport(0, 0, 100, 40),
        [text],
        [],
        [],
        [],
        [],
        [],
        [],
        []);

    private static MermaidRenderResult Success(MermaidScene scene, string source) =>
        new(MermaidRenderStatus.Success, scene, scene.Diagnostics, source);

    private static MermaidRenderResult AdmissionRejected(string source) =>
        new(
            MermaidRenderStatus.Overloaded,
            null,
            [new MermaidDiagnostic("MMR0013", MermaidDiagnosticSeverity.Error, "overloaded")],
            source);

    private static MermaidRenderResult DeadlineExceeded(string source) =>
        new(
            MermaidRenderStatus.TimedOut,
            null,
            [new MermaidDiagnostic("MMR0007", MermaidDiagnosticSeverity.Error, "timed out")],
            source);

    private static long FutureDeadline() => MermaidDeadline.Start(TimeSpan.FromMinutes(1));
}
