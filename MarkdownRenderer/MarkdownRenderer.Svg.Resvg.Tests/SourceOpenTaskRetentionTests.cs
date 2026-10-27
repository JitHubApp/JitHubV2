using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SourceOpenTaskRetentionTests
{
    [Fact]
    public async Task FaultedPreflightTaskDoesNotRetainCallerSource()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        (Task<IMarkdownSvgDocument> task, WeakReference<byte[]> source) = StartRejectedOpen(renderer);

        MarkdownSvgException exception = await Assert.ThrowsAsync<MarkdownSvgException>(async () => await task);

        Assert.Equal(MarkdownSvgFailureReason.UnsupportedContent, exception.Reason);
        AssertCallerSourceCollected(source);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task CanceledPreflightTaskDoesNotRetainCallerSource()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        (Task<IMarkdownSvgDocument> task, WeakReference<byte[]> source) = StartCanceledOpen(renderer);

        MarkdownSvgException exception = await Assert.ThrowsAsync<MarkdownSvgException>(async () => await task);

        Assert.Equal(MarkdownSvgFailureReason.Canceled, exception.Reason);
        AssertCallerSourceCollected(source);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task QuarantineAfterSuccessfulPreflightDoesNotRetainCallerSource()
    {
        using var renderer = new ResvgMarkdownSvgRenderer();
        (Task<IMarkdownSvgDocument> task, WeakReference<byte[]> source) = StartQuarantinedOpen(renderer);

        MarkdownSvgException exception = await Assert.ThrowsAsync<MarkdownSvgException>(async () => await task);

        Assert.Equal(MarkdownSvgFailureReason.WorkerFailure, exception.Reason);
        AssertCallerSourceCollected(source);
        GC.KeepAlive(task);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<IMarkdownSvgDocument> Task, WeakReference<byte[]> Source) StartRejectedOpen(
        ResvgMarkdownSvgRenderer renderer)
    {
        byte[] source = Encoding.UTF8.GetBytes("<html><body>not an SVG document</body></html>");
        var weakSource = new WeakReference<byte[]>(source);
        Task<IMarkdownSvgDocument> task = renderer.OpenAsync(new MarkdownSvgOpenRequest(source)).AsTask();
        return (task, weakSource);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<IMarkdownSvgDocument> Task, WeakReference<byte[]> Source) StartCanceledOpen(
        ResvgMarkdownSvgRenderer renderer)
    {
        byte[] source = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><rect width='1' height='1'/></svg>");
        var weakSource = new WeakReference<byte[]>(source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task<IMarkdownSvgDocument> task = renderer.OpenAsync(
            new MarkdownSvgOpenRequest(source), cancellation.Token).AsTask();
        return (task, weakSource);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<IMarkdownSvgDocument> Task, WeakReference<byte[]> Source) StartQuarantinedOpen(
        ResvgMarkdownSvgRenderer renderer)
    {
        byte[] source = Encoding.UTF8.GetBytes(
            "<svg xmlns='http://www.w3.org/2000/svg'><rect width='1' height='1'/></svg>");
        var weakSource = new WeakReference<byte[]>(source);
        renderer.QuarantineWorkerFailure(Convert.ToHexString(SHA256.HashData(source)));
        Task<IMarkdownSvgDocument> task = renderer.OpenAsync(new MarkdownSvgOpenRequest(source)).AsTask();
        return (task, weakSource);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertCallerSourceCollected(WeakReference<byte[]> weakSource)
    {
        // Do not read the weak target before collecting: a JIT temporary from
        // TryGetTarget can itself keep the array alive through that collection.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        Assert.False(weakSource.TryGetTarget(out _), "The retained OpenAsync task still roots the caller's SVG array.");
    }
}
