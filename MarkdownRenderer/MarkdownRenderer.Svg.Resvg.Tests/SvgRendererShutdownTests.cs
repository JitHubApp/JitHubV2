using System.Runtime.CompilerServices;
using System.Reflection;
using System.Buffers;
using System.Security.Cryptography;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgRendererShutdownTests
{
    [Fact]
    public void RendererShutdownReleasesSourceOfRetainedDocument()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        using var workers = new WorkerPool(new ResvgMarkdownSvgRendererOptions());
        DocumentFixture fixture = CreateDocument(renderer, workers);

        renderer.Dispose();

        ForceCollection();
        Assert.False(fixture.Source.IsAlive);
        fixture.Document.Dispose();
        GC.KeepAlive(fixture.Document);
        GC.KeepAlive(renderer);
    }

    [Fact]
    public async Task AsyncRendererShutdownReleasesSourceOfRetainedDocument()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        using var workers = new WorkerPool(new ResvgMarkdownSvgRendererOptions());
        DocumentFixture fixture = CreateDocument(renderer, workers);

        await renderer.DisposeAsync();

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(
            async () => await fixture.Document.RenderAsync(new MarkdownSvgRenderRequest(16, 8)));
        Assert.Equal(MarkdownSvgFailureReason.Canceled, failure.Reason);
        ForceCollection();
        Assert.False(fixture.Source.IsAlive);
        fixture.Document.Dispose();
        GC.KeepAlive(fixture.Document);
        GC.KeepAlive(renderer);
    }

    [Fact]
    public async Task SyncThenAsyncShutdownWaitsForActualRenderDrain()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        using var workers = new WorkerPool(new ResvgMarkdownSvgRendererOptions());
        DocumentFixture fixture = CreateDocument(renderer, workers);
        SvgDocumentLifetime lifetime = GetLifetime(fixture.Document);
        SvgDocumentLifetime.RenderLease lease = lifetime.TryEnterRender()!;

        renderer.Dispose();
        Task firstShutdown = renderer.DisposeAsync().AsTask();
        Task repeatedShutdown = renderer.DisposeAsync().AsTask();

        Assert.True(lease.Token.IsCancellationRequested);
        Assert.False(firstShutdown.IsCompleted);
        Assert.False(repeatedShutdown.IsCompleted);
        Assert.False(lifetime.RetirementTask.IsCompleted);
        ForceCollection();
        Assert.True(fixture.Source.IsAlive);

        lease.Dispose();
        await Task.WhenAll(firstShutdown, repeatedShutdown).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(lifetime.RetirementTask.IsCompletedSuccessfully);
        ForceCollection();
        Assert.False(fixture.Source.IsAlive);
        fixture.Document.Dispose();
        GC.KeepAlive(fixture.Document);
        GC.KeepAlive(lease);
        GC.KeepAlive(lifetime);
    }

    [Fact]
    public async Task AsyncShutdownBeforeSyncShutdownUsesTheSameDrain()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        using var workers = new WorkerPool(new ResvgMarkdownSvgRendererOptions());
        DocumentFixture fixture = CreateDocument(renderer, workers);
        SvgDocumentLifetime lifetime = GetLifetime(fixture.Document);
        SvgDocumentLifetime.RenderLease lease = lifetime.TryEnterRender()!;

        Task shutdown = renderer.DisposeAsync().AsTask();
        renderer.Dispose();
        Assert.False(shutdown.IsCompleted);
        Assert.True(lease.Token.IsCancellationRequested);

        lease.Dispose();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        ForceCollection();
        Assert.False(fixture.Source.IsAlive);
        fixture.Document.Dispose();
        GC.KeepAlive(fixture.Document);
        GC.KeepAlive(lease);
    }

    [Fact]
    public void LateDocumentRegistrationIsCanceledAndReleasesItsSource()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        using var workers = new WorkerPool(new ResvgMarkdownSvgRendererOptions());
        renderer.Dispose();

        (MarkdownSvgException failure, WeakReference source) = CreateLateDocument(renderer, workers);

        Assert.Equal(MarkdownSvgFailureReason.Canceled, failure.Reason);
        ForceCollection();
        Assert.False(source.IsAlive);
        GC.KeepAlive(failure);
    }

    [Fact]
    public void HealthyRendererDoesNotRootAbandonedDocumentOrSource()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        using var workers = new WorkerPool(new ResvgMarkdownSvgRendererOptions());
        (WeakReference document, WeakReference source) = CreateAbandonedDocument(renderer, workers);

        ForceCollection();

        Assert.False(document.IsAlive);
        Assert.False(source.IsAlive);
        GC.KeepAlive(renderer);
    }

    [Fact]
    public async Task CleanupFailureDoesNotSkipOtherDocumentsAndAsyncShutdownObservesIt()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        var failure = new InvalidOperationException("Injected provider child cleanup failure.");
        var first = new SvgDocumentLifetime(() => throw failure);
        int otherReleased = 0;
        var second = new SvgDocumentLifetime(() => Interlocked.Increment(ref otherReleased));
        Assert.True(renderer.TryRegisterDocument(first));
        Assert.True(renderer.TryRegisterDocument(second));

        Assert.Throws<AggregateException>(renderer.Dispose);

        Assert.Equal(1, otherReleased);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await renderer.DisposeAsync()));
        renderer.Dispose();
        GC.KeepAlive(first);
        GC.KeepAlive(second);
    }

    [Fact]
    public async Task ConcurrentAsyncShutdownDoesNotReturnBeforeSyncCancellationFinishes()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        using var callbackEntered = new ManualResetEventSlim();
        using var allowCallback = new ManualResetEventSlim();
        var lifetime = new SvgDocumentLifetime(static () => { });
        SvgDocumentLifetime.RenderLease lease = lifetime.TryEnterRender()!;
        using CancellationTokenRegistration registration = lease.Token.Register(() =>
        {
            callbackEntered.Set();
            allowCallback.Wait();
        });
        Assert.True(renderer.TryRegisterDocument(lifetime));

        Task synchronous = Task.Run(renderer.Dispose);
        Task? asynchronous = null;
        try
        {
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(10)));
            asynchronous = renderer.DisposeAsync().AsTask();
            lease.Dispose();
            Assert.False(asynchronous.IsCompleted);
            Assert.False(lifetime.RetirementTask.IsCompleted);
        }
        finally
        {
            allowCallback.Set();
        }
        await synchronous.WaitAsync(TimeSpan.FromSeconds(10));
        await asynchronous!.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task CancellationCanReenterRendererWithoutHoldingDocumentRegistryGate()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        var lifetime = new SvgDocumentLifetime(static () => { });
        SvgDocumentLifetime.RenderLease lease = lifetime.TryEnterRender()!;
        using CancellationTokenRegistration registration = lease.Token.Register(() =>
        {
            renderer.Dispose();
            using var late = new SvgDocumentLifetime(static () => { });
            Assert.False(renderer.TryRegisterDocument(late));
        });
        Assert.True(renderer.TryRegisterDocument(lifetime));

        renderer.Dispose();
        lease.Dispose();

        await renderer.DisposeAsync();
        Assert.True(lifetime.RetirementTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task AsyncShutdownWaitsForActualBackgroundPreflightToLeaveCallerMemory()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        (Task<IMarkdownSvgDocument> open, WeakReference memory, WeakReference source) =
            StartGatedOpen(renderer, entered, release);
        Task? shutdown = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "The background source copy did not start.");
            renderer.Dispose();
            shutdown = renderer.DisposeAsync().AsTask();
            Assert.False(open.IsCompleted);
            Assert.False(shutdown.IsCompleted);
            ForceCollection();
            Assert.True(memory.IsAlive);
            Assert.True(source.IsAlive);
        }
        finally
        {
            release.Set();
        }

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(
            async () => await open.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(MarkdownSvgFailureReason.Canceled, failure.Reason);
        await shutdown!.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, renderer.SourceAttachmentCount);
        ForceCollection();
        Assert.False(memory.IsAlive);
        Assert.False(source.IsAlive);
        GC.KeepAlive(open);
        GC.KeepAlive(shutdown);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<IMarkdownSvgDocument> Open, WeakReference Memory, WeakReference Source)
        StartGatedOpen(ResvgMarkdownSvgRenderer renderer, ManualResetEventSlim entered, ManualResetEventSlim release)
    {
        // No native memory or handles belong to this manager. The caller owns
        // the two gate events; retain only weak manager/buffer identities once
        // the real OpenAsync operation has taken its source borrow.
        var memory = new GatedMemory(entered, release);
        Task<IMarkdownSvgDocument> open = renderer.OpenAsync(new MarkdownSvgOpenRequest(memory.Source)).AsTask();
        return (open, new WeakReference(memory), memory.WeakSource);
    }

    [Fact]
    public async Task ShutdownAfterPreflightIsCanceledWithoutQuarantiningValidContent()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        WorkerPool workers = (WorkerPool)typeof(ResvgMarkdownSvgRenderer)
            .GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(renderer)!;
        object admissionGate = typeof(WorkerPool)
            .GetField("_stateLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(workers)!;
        FieldInfo admittedIdentity = typeof(WorkerPool)
            .GetField("_nextDocumentId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        byte[] source = "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='8'/>"u8.ToArray();
        Task<IMarkdownSvgDocument>? open = null;

        // Hold the real pool gate before starting preflight. The identity is
        // advanced only after preflight, immediately before worker admission.
        // Closing on this thread is reentrant; the opener cannot obtain the
        // gate until close has both canceled its token and disposed the pool.
        Monitor.Enter(admissionGate);
        try
        {
            open = renderer.OpenAsync(new MarkdownSvgOpenRequest(source)).AsTask();
            Assert.True(SpinWait.SpinUntil(
                () => (long)admittedIdentity.GetValue(workers)! > 0,
                TimeSpan.FromSeconds(10)), "Open did not finish preflight and reach worker admission.");
            renderer.Dispose();
            Assert.False(renderer.DisposeAsync().IsCompleted);
        }
        finally
        {
            Monitor.Exit(admissionGate);
        }

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(
            async () => await open!.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(MarkdownSvgFailureReason.Canceled, failure.Reason);
        renderer.ThrowIfQuarantined(Convert.ToHexString(SHA256.HashData(source)));
        Assert.Equal(0, renderer.SourceAttachmentCount);
        await renderer.DisposeAsync();
    }

    private sealed class GatedMemory(ManualResetEventSlim entered, ManualResetEventSlim release) : MemoryManager<byte>
    {
        private readonly byte[] _source = "<svg xmlns='http://www.w3.org/2000/svg' width='16' height='8'/>"u8.ToArray();
        internal Memory<byte> Source => CreateMemory(_source.Length);
        internal WeakReference WeakSource => new(_source);

        public override Span<byte> GetSpan()
        {
            entered.Set();
            release.Wait();
            return _source;
        }

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }

    private static SvgDocumentLifetime GetLifetime(ResvgMarkdownSvgDocument document) =>
        (SvgDocumentLifetime)typeof(ResvgMarkdownSvgDocument)
            .GetField("_lifetime", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(document)!;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Document, WeakReference Source) CreateAbandonedDocument(
        ResvgMarkdownSvgRenderer renderer, WorkerPool workers)
    {
        DocumentFixture fixture = CreateDocument(renderer, workers);
        return (new WeakReference(fixture.Document), fixture.Source);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (MarkdownSvgException Failure, WeakReference Source) CreateLateDocument(
        ResvgMarkdownSvgRenderer renderer, WorkerPool workers)
    {
        byte[] source = new byte[4096];
        var weakSource = new WeakReference(source);
        var info = new MarkdownSvgDocumentInfo(16, 8);
        var preflight = new SvgPreflightResult(SvgSourceBuffer.TakeOwnedArray(source), new byte[32], info, 0, 0, 0, 0);
        MarkdownSvgException failure = Assert.Throws<MarkdownSvgException>(() =>
            new ResvgMarkdownSvgDocument(renderer, workers, new ResvgMarkdownSvgRendererOptions(), preflight,
                new MarkdownSvgOpenRequest(source), "late-renderer-shutdown-source-test", info,
                new WorkerDocumentHandle(18)));
        return (failure, weakSource);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DocumentFixture CreateDocument(ResvgMarkdownSvgRenderer renderer, WorkerPool workers)
    {
        byte[] source = new byte[4096];
        var info = new MarkdownSvgDocumentInfo(16, 8);
        var preflight = new SvgPreflightResult(SvgSourceBuffer.TakeOwnedArray(source), new byte[32], info, 0, 0, 0, 0);
        var document = new ResvgMarkdownSvgDocument(
            renderer, workers, new ResvgMarkdownSvgRendererOptions(), preflight,
            new MarkdownSvgOpenRequest(source), "renderer-shutdown-source-test", info,
            new WorkerDocumentHandle(17));
        return new DocumentFixture(document, new WeakReference(source));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed record DocumentFixture(ResvgMarkdownSvgDocument Document, WeakReference Source);
}
