using System.Text;
using MarkdownRenderer.Mermaid;

namespace MarkdownRenderer.Mermaid.Tests;

/// <summary>Scene cache admission bounds and producer deadlines.</summary>
public sealed class MermaidSceneCacheAdmissionTests
{
    [Fact]
    public async Task UniqueAdmissionIsCountBoundedWhileSameKeyCallersJoin()
    {
        const string sourceA = "flowchart LR\nA-->B";
        const string sourceB = "flowchart LR\nC-->D";
        using var cache = new MermaidSceneCache(
            1024 * 1024,
            maxOutstandingRenders: 1,
            maxOutstandingSourceBytes: 1024 * 1024);
        MermaidSceneCacheKey keyA = MermaidSceneCacheTestData.CreateKey(sourceA);
        MermaidSceneCacheKey keyB = MermaidSceneCacheTestData.CreateKey(sourceB);
        MermaidScene scene = MermaidSceneCacheTestData.CreateScene("shared");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int factoryCalls = 0;

        async Task<MermaidRenderResult> Factory(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref factoryCalls);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return MermaidSceneCacheTestData.Success(scene, sourceA);
        }

        Task<MermaidRenderResult> first = cache.GetOrRenderAsync(
            keyA,
            sourceA,
            Factory,
            () => MermaidSceneCacheTestData.AdmissionRejected(sourceA),
            () => MermaidSceneCacheTestData.DeadlineExceeded(sourceA),
            MermaidSceneCacheTestData.FutureDeadline(),
            CancellationToken.None).AsTask();
        await entered.Task;
        Task<MermaidRenderResult> joined = cache.GetOrRenderAsync(
            keyA,
            sourceA,
            Factory,
            () => MermaidSceneCacheTestData.AdmissionRejected(sourceA),
            () => MermaidSceneCacheTestData.DeadlineExceeded(sourceA),
            MermaidSceneCacheTestData.FutureDeadline(),
            CancellationToken.None).AsTask();

        MermaidRenderResult rejected = await cache.GetOrRenderAsync(
            keyB,
            sourceB,
            _ => throw new InvalidOperationException("Rejected work must not invoke its factory."),
            () => MermaidSceneCacheTestData.AdmissionRejected(sourceB),
            () => MermaidSceneCacheTestData.DeadlineExceeded(sourceB),
            MermaidSceneCacheTestData.FutureDeadline(),
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
            MermaidSceneCacheTestData.CreateKey(sourceA),
            sourceA,
            async cancellationToken =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return MermaidSceneCacheTestData.Success(MermaidSceneCacheTestData.CreateScene("A"), sourceA);
            },
            () => MermaidSceneCacheTestData.AdmissionRejected(sourceA),
            () => MermaidSceneCacheTestData.DeadlineExceeded(sourceA),
            MermaidSceneCacheTestData.FutureDeadline(),
            CancellationToken.None).AsTask();
        await entered.Task;

        MermaidRenderResult rejected = await cache.GetOrRenderAsync(
            MermaidSceneCacheTestData.CreateKey(sourceB),
            sourceB,
            _ => throw new InvalidOperationException("Rejected work must not invoke its factory."),
            () => MermaidSceneCacheTestData.AdmissionRejected(sourceB),
            () => MermaidSceneCacheTestData.DeadlineExceeded(sourceB),
            MermaidSceneCacheTestData.FutureDeadline(),
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
            MermaidSceneCacheTestData.CreateKey(source),
            source,
            _ => Task.FromResult(MermaidSceneCacheTestData.Success(MermaidSceneCacheTestData.CreateScene("late"), source)),
            () => MermaidSceneCacheTestData.AdmissionRejected(source),
            () => MermaidSceneCacheTestData.DeadlineExceeded(source),
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
            MermaidSceneCacheTestData.CreateKey(source),
            source,
            _ => Task.FromResult(new MermaidRenderResult(
                MermaidRenderStatus.NativeFailure,
                null,
                [new MermaidDiagnostic("MMR0012", MermaidDiagnosticSeverity.Error, "late failure")],
                source)),
            () => MermaidSceneCacheTestData.AdmissionRejected(source),
            () => MermaidSceneCacheTestData.DeadlineExceeded(source),
            producerDeadlineTimestamp: 0,
            cancellationToken: CancellationToken.None);
        MermaidRenderResult lateFault = await cache.GetOrRenderAsync(
            MermaidSceneCacheTestData.CreateKey(source + "\nB-->C"),
            source + "\nB-->C",
            _ => Task.FromException<MermaidRenderResult>(
                new InvalidOperationException("late producer fault")),
            () => MermaidSceneCacheTestData.AdmissionRejected(source),
            () => MermaidSceneCacheTestData.DeadlineExceeded(source),
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
        MermaidScene replacement = MermaidSceneCacheTestData.CreateScene("fresh request");
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int firstFactories = 0;
        int secondFactories = 0;

        Task<MermaidRenderResult> first = cache.GetOrRenderAsync(
            MermaidSceneCacheTestData.CreateKey(source),
            source,
            async _ =>
            {
                Interlocked.Increment(ref firstFactories);
                firstEntered.TrySetResult();
                await releaseFirst.Task;
                return MermaidSceneCacheTestData.Success(MermaidSceneCacheTestData.CreateScene("expired"), source);
            },
            () => MermaidSceneCacheTestData.AdmissionRejected(source),
            () => MermaidSceneCacheTestData.DeadlineExceeded(source),
            producerDeadlineTimestamp: 0,
            cancellationToken: CancellationToken.None).AsTask();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task<MermaidRenderResult> lateJoiner = cache.GetOrRenderAsync(
            MermaidSceneCacheTestData.CreateKey(source),
            source,
            _ =>
            {
                Interlocked.Increment(ref secondFactories);
                return Task.FromResult(MermaidSceneCacheTestData.Success(replacement, source));
            },
            () => MermaidSceneCacheTestData.AdmissionRejected(source),
            () => MermaidSceneCacheTestData.DeadlineExceeded(source),
            MermaidSceneCacheTestData.FutureDeadline(),
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
}
