using System.Globalization;
using System.Text;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Mermaid.Internal.Caching;
using MarkdownRenderer.Mermaid.Internal.Rendering;
using MarkdownRenderer.Mermaid.Internal.Scenes;
using MarkdownRenderer.Mermaid.Native.Internal;
using MarkdownRenderer.Mermaid.Tests.TestSupport;

namespace MarkdownRenderer.Mermaid.Tests.Internal.Caching;

public sealed class MermaidSceneCacheTests
{
    [Fact]
    public async Task InFlightRequestsAreDeduplicatedAndWaiterCancellationIsIndependent()
    {
        using var cache = new MermaidSceneCache(1024 * 1024);
        MermaidScene scene = MermaidSceneCacheTestData.CreateScene("shared");
        MermaidSceneCacheKey key = MermaidSceneCacheTestData.CreateKey("flowchart LR\nA-->B");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int factoryCalls = 0;

        async Task<MermaidRenderResult> Factory(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref factoryCalls);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return MermaidSceneCacheTestData.Success(scene, "flowchart LR\nA-->B");
        }

        using var cancelledWaiter = new CancellationTokenSource();
        Task<MermaidRenderResult> first = cache.GetOrRenderAsync(
            key,
            "flowchart LR\nA-->B",
            Factory,
            () => MermaidSceneCacheTestData.AdmissionRejected("flowchart LR\nA-->B"),
            () => MermaidSceneCacheTestData.DeadlineExceeded("flowchart LR\nA-->B"),
            MermaidSceneCacheTestData.FutureDeadline(),
            cancelledWaiter.Token).AsTask();
        await entered.Task;
        Task<MermaidRenderResult> second = cache.GetOrRenderAsync(
            key,
            "flowchart LR\nA-->B",
            Factory,
            () => MermaidSceneCacheTestData.AdmissionRejected("flowchart LR\nA-->B"),
            () => MermaidSceneCacheTestData.DeadlineExceeded("flowchart LR\nA-->B"),
            MermaidSceneCacheTestData.FutureDeadline(),
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
            () => MermaidSceneCacheTestData.AdmissionRejected("flowchart LR\nA-->B"),
            () => MermaidSceneCacheTestData.DeadlineExceeded("flowchart LR\nA-->B"),
            MermaidSceneCacheTestData.FutureDeadline(),
            CancellationToken.None);
        Assert.Same(scene, cached.Scene);
        Assert.Equal("flowchart LR\nA-->B", cached.Fallback.Source);
    }

    [Fact]
    public async Task WeightedLruEvictsTheLeastRecentlyUsedScene()
    {
        MermaidScene sceneA = MermaidSceneCacheTestData.CreateScene(new string('a', 128));
        MermaidScene sceneB = MermaidSceneCacheTestData.CreateScene(new string('b', 128));
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
                MermaidSceneCacheTestData.CreateKey(source),
                source,
                _ =>
                {
                    factoryCalls++;
                    return Task.FromResult(MermaidSceneCacheTestData.Success(scene, source));
                },
                () => MermaidSceneCacheTestData.AdmissionRejected(source),
                () => MermaidSceneCacheTestData.DeadlineExceeded(source),
                MermaidSceneCacheTestData.FutureDeadline(),
                CancellationToken.None);
        }
    }

    [Fact]
    public async Task FailuresAreNeverRetained()
    {
        using var cache = new MermaidSceneCache(1024 * 1024);
        MermaidSceneCacheKey key = MermaidSceneCacheTestData.CreateKey("invalid");
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
                () => MermaidSceneCacheTestData.AdmissionRejected("invalid"),
                () => MermaidSceneCacheTestData.DeadlineExceeded("invalid"),
                MermaidSceneCacheTestData.FutureDeadline(),
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
        MermaidSceneCacheKey key = MermaidSceneCacheTestData.CreateKey("flowchart LR\nA-->B");
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
            () => MermaidSceneCacheTestData.AdmissionRejected("flowchart LR\nA-->B"),
            () => MermaidSceneCacheTestData.DeadlineExceeded("flowchart LR\nA-->B"),
            MermaidSceneCacheTestData.FutureDeadline(),
            callerCancellation.Token).AsTask();
        await entered.Task;

        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        await producerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));

        MermaidScene replacement = MermaidSceneCacheTestData.CreateScene("replacement");
        MermaidRenderResult rerendered = await cache.GetOrRenderAsync(
            key,
            "flowchart LR\nA-->B",
            _ => Task.FromResult(MermaidSceneCacheTestData.Success(replacement, "flowchart LR\nA-->B")),
            () => MermaidSceneCacheTestData.AdmissionRejected("flowchart LR\nA-->B"),
            () => MermaidSceneCacheTestData.DeadlineExceeded("flowchart LR\nA-->B"),
            MermaidSceneCacheTestData.FutureDeadline(),
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
            MermaidSceneCacheTestData.CreateKey(source),
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
            () => MermaidSceneCacheTestData.AdmissionRejected(source),
            () => MermaidSceneCacheTestData.DeadlineExceeded(source),
            MermaidSceneCacheTestData.FutureDeadline(),
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
    public void KeySeparatesSourceVersionsConfigurationThemeAndFontCatalog()
    {
        const string source = "flowchart LR\nA-->B";
        MermaidSceneCacheKey baseline = MermaidSceneCacheTestData.CreateKey(source);
        MermaidSceneCacheKey differentSource = MermaidSceneCacheTestData.CreateKey(source + "\nB-->C");
        MermaidSceneCacheKey differentTheme = MermaidSceneCacheTestData.CreateKey(
            source,
            MermaidRenderOptions.Default with { Theme = MermaidThemeVariant.Dark });
        MermaidSceneCacheKey differentLayout = MermaidSceneCacheTestData.CreateKey(
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
}
