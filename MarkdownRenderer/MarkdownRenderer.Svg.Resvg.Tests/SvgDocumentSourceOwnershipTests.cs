using System.Runtime.CompilerServices;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgDocumentSourceOwnershipTests
{
    [Fact]
    public void OpenedDocumentDoesNotPinCallersLargerBackingBuffer()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        using var workers = new WorkerPool(new ResvgMarkdownSvgRendererOptions());
        using ResvgMarkdownSvgDocument document = CreateDocument(renderer, workers, out WeakReference callerBuffer);

        ForceCollection();

        Assert.False(callerBuffer.IsAlive);
        Assert.Equal(16, document.Info.IntrinsicWidthDips);
        GC.KeepAlive(document);
    }

    [Fact]
    public void DisposedRetainedDocumentReleasesOwnedSnapshotAndKeepsMetadata()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        using var workers = new WorkerPool(new ResvgMarkdownSvgRendererOptions());
        (ResvgMarkdownSvgDocument document, WeakReference callerBuffer, WeakReference ownedSnapshot) =
            CreateDocumentWithTrackedSources(renderer, workers);
        MarkdownSvgDocumentInfo info = document.Info;

        document.Dispose();
        ForceCollection();

        Assert.False(callerBuffer.IsAlive);
        Assert.False(ownedSnapshot.IsAlive);
        Assert.Equal(16, info.IntrinsicWidthDips);
        Assert.Equal(8, info.IntrinsicHeightDips);
        GC.KeepAlive(info);
        GC.KeepAlive(document);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ResvgMarkdownSvgDocument CreateDocument(
        ResvgMarkdownSvgRenderer renderer,
        WorkerPool workers,
        out WeakReference callerBuffer)
    {
        byte[] backingBuffer = new byte[1024 * 1024];
        backingBuffer.AsSpan(0, 4).Fill(42);
        var request = new MarkdownSvgOpenRequest(backingBuffer.AsMemory(0, 4), Locale: "ar");
        byte[] ownedSnapshot = request.SanitizedSvgBytes.ToArray();
        var info = new MarkdownSvgDocumentInfo(16, 8);
        var preflight = new SvgPreflightResult(SvgSourceBuffer.TakeOwnedArray(ownedSnapshot), new byte[32], info, 0, 0, 0, 0);
        callerBuffer = new WeakReference(backingBuffer);
        return new ResvgMarkdownSvgDocument(
            renderer, workers, new ResvgMarkdownSvgRendererOptions(), preflight,
            request, "test-source-ownership", info, new WorkerDocumentHandle(1));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (ResvgMarkdownSvgDocument Document, WeakReference CallerBuffer, WeakReference OwnedSnapshot)
        CreateDocumentWithTrackedSources(ResvgMarkdownSvgRenderer renderer, WorkerPool workers)
    {
        byte[] backingBuffer = new byte[1024 * 1024];
        backingBuffer.AsSpan(0, 4).Fill(42);
        var request = new MarkdownSvgOpenRequest(backingBuffer.AsMemory(0, 4), Locale: "ar");
        byte[] ownedSnapshot = request.SanitizedSvgBytes.ToArray();
        var info = new MarkdownSvgDocumentInfo(16, 8);
        var preflight = new SvgPreflightResult(SvgSourceBuffer.TakeOwnedArray(ownedSnapshot), new byte[32], info, 0, 0, 0, 0);
        var callerBuffer = new WeakReference(backingBuffer);
        var ownedSnapshotReference = new WeakReference(ownedSnapshot);
        var document = new ResvgMarkdownSvgDocument(
            renderer, workers, new ResvgMarkdownSvgRendererOptions(), preflight,
            request, "test-disposed-source-ownership", info, new WorkerDocumentHandle(2));
        return (document, callerBuffer, ownedSnapshotReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ForceCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
