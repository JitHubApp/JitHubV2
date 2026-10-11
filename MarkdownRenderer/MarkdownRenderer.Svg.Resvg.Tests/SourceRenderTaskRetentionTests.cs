using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SourceRenderTaskRetentionTests
{
    [Fact]
    public async Task RetainedFailedRenderTaskDoesNotPinSourceAfterDocumentDispose()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        using var workers = new WorkerPool(new ResvgMarkdownSvgRendererOptions());
        (ResvgMarkdownSvgDocument document, WeakReference callerBuffer, WeakReference ownedSource) =
            CreateDocument(renderer, workers);
        Task<MarkdownSvgRaster> renderTask = document.RenderAsync(new MarkdownSvgRenderRequest(
            16,
            8,
            new MarkdownSvgTileRegion(15, 0, 2, 1))).AsTask();

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await renderTask);
        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, failure.Reason);

        document.Dispose();
        ForceCollection();

        Assert.False(callerBuffer.IsAlive);
        Assert.False(ownedSource.IsAlive);
        GC.KeepAlive(renderTask);
        GC.KeepAlive(document);
    }

    [Fact]
    public async Task RetainedCanceledRenderTaskDoesNotPinSourceAfterDocumentDispose()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        using var workers = new WorkerPool(new ResvgMarkdownSvgRendererOptions());
        (ResvgMarkdownSvgDocument document, WeakReference callerBuffer, WeakReference ownedSource) =
            CreateDocument(renderer, workers);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task<MarkdownSvgRaster> renderTask = document.RenderAsync(
            new MarkdownSvgRenderRequest(16, 8),
            cancellation.Token).AsTask();

        MarkdownSvgException failure = await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await renderTask);
        Assert.Equal(MarkdownSvgFailureReason.Canceled, failure.Reason);

        document.Dispose();
        ForceCollection();

        Assert.False(callerBuffer.IsAlive);
        Assert.False(ownedSource.IsAlive);
        GC.KeepAlive(renderTask);
        GC.KeepAlive(document);
    }

    [Fact]
    public async Task RetainedPreflightTaskResultDoesNotRetainSourceAfterSnapshotRetirement()
    {
        SourceFixture fixture = CreateSourceFixture(withActiveRender: false);

        fixture.Snapshot.Release();
        fixture.Snapshot.Release();

        Assert.Equal(0, (await fixture.InspectionTask).Source.Length);
        ForceCollection();
        Assert.False(IsAlive(fixture.SourceBytes));
        GC.KeepAlive(fixture.Snapshot);
        GC.KeepAlive(fixture.InspectionTask);
    }

    [Fact]
    public async Task RetainedPreflightTaskResultKeepsSourceUntilAdmittedRenderRetires()
    {
        SourceFixture fixture = CreateSourceFixture(withActiveRender: true);

        fixture.Lifetime.Dispose();
        ForceCollection();
        Assert.True(IsAlive(fixture.SourceBytes));

        fixture.RenderLease!.Dispose();

        Assert.Equal(0, (await fixture.InspectionTask).Source.Length);
        ForceCollection();
        Assert.False(IsAlive(fixture.SourceBytes));
        GC.KeepAlive(fixture.Snapshot);
        GC.KeepAlive(fixture.Lifetime);
        GC.KeepAlive(fixture.RenderLease);
        GC.KeepAlive(fixture.InspectionTask);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SourceFixture CreateSourceFixture(bool withActiveRender)
    {
        byte[] sourceBytes = new byte[4096];
        var preflight = new SvgPreflightResult(
            SvgSourceBuffer.TakeOwnedArray(sourceBytes),
            new byte[32],
            new MarkdownSvgDocumentInfo(IntrinsicWidthDips: 16, IntrinsicHeightDips: 16),
            StructuralCost: 1,
            EmbeddedBytes: 0,
            EmbeddedPixels: 0,
            FilterPrimitives: 0);
        var inspectionTask = Task.FromResult(preflight);
        var snapshot = new SvgDocumentSourceSnapshot(
            preflight,
            new MarkdownSvgOpenRequest(sourceBytes));
        var weakSource = new WeakReference<byte[]>(sourceBytes);
        var lifetime = new SvgDocumentLifetime(snapshot.Release);
        SvgDocumentLifetime.RenderLease? renderLease = withActiveRender
            ? lifetime.TryEnterRender()
            : null;

        return new SourceFixture(snapshot, lifetime, renderLease, inspectionTask, weakSource);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ResvgMarkdownSvgDocument Document, WeakReference CallerBuffer, WeakReference OwnedSource)
        CreateDocument(ResvgMarkdownSvgRenderer renderer, WorkerPool workers)
    {
        byte[] callerBuffer = new byte[1024 * 1024];
        callerBuffer.AsSpan(0, 4).Fill(42);
        var request = new MarkdownSvgOpenRequest(callerBuffer.AsMemory(0, 4));
        byte[] ownedSource = request.SanitizedSvgBytes.ToArray();
        var info = new MarkdownSvgDocumentInfo(16, 8);
        var preflight = new SvgPreflightResult(SvgSourceBuffer.TakeOwnedArray(ownedSource), new byte[32], info, 0, 0, 0, 0);
        var callerWeak = new WeakReference(callerBuffer);
        var sourceWeak = new WeakReference(ownedSource);
        var document = new ResvgMarkdownSvgDocument(
            renderer,
            workers,
            new ResvgMarkdownSvgRendererOptions(),
            preflight,
            request,
            "retained-render-task-source-test",
            info,
            new WorkerDocumentHandle(17));
        return (document, callerWeak, sourceWeak);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive(WeakReference<byte[]> sourceBytes) => sourceBytes.TryGetTarget(out _);

    private sealed record SourceFixture(
        SvgDocumentSourceSnapshot Snapshot,
        SvgDocumentLifetime Lifetime,
        SvgDocumentLifetime.RenderLease? RenderLease,
        Task<SvgPreflightResult> InspectionTask,
        WeakReference<byte[]> SourceBytes);
}
