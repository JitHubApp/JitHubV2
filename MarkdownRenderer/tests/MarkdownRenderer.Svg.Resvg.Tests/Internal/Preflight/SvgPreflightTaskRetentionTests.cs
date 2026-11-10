using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using MarkdownRenderer.Images;
using MarkdownRenderer.Svg.Resvg.Internal.Preflight;
using MarkdownRenderer.Svg.Resvg.Internal.Sources;
using MarkdownRenderer.Svg.Resvg.Tests.TestSupport;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests.Internal.Preflight;

/// <summary>
/// Pins that completed, faulted and canceled UTF-8 normalization and
/// preflight inspection tasks do not keep the original source array alive.
/// </summary>
public sealed class SvgPreflightTaskRetentionTests
{
    [Fact]
    public async Task CompletedNormalizationTaskDoesNotRetainOriginalSourceBytes()
    {
        (Task<BoundedOutputStream> task, WeakReference<byte[]> source) = StartUtf16Normalization();

        using BoundedOutputStream normalizedOutput = await task.WaitAsync(TimeSpan.FromSeconds(5));
        AssertCollected(source);
        using SvgSourceBuffer normalizedSource = normalizedOutput.DetachSourceBuffer();
        byte[] normalized = normalizedSource.ReadAllBytes();

        Assert.StartsWith("<?xml", Encoding.UTF8.GetString(normalized), StringComparison.Ordinal);
        GC.KeepAlive(normalized);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task FaultedNormalizationTaskDoesNotRetainOriginalSourceBytes()
    {
        (Task<BoundedOutputStream> task, WeakReference<byte[]> source) = StartFaultedUtf16Normalization();

        await Assert.ThrowsAsync<XmlException>(async () =>
            await task.WaitAsync(TimeSpan.FromSeconds(5)));

        AssertCollected(source);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task CanceledNormalizationTaskDoesNotRetainOriginalSourceBytes()
    {
        (Task<BoundedOutputStream> task, WeakReference<byte[]> source) = StartCanceledUtf16Normalization();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await task.WaitAsync(TimeSpan.FromSeconds(5)));

        AssertCollected(source);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task FailedInspectionTaskDoesNotRetainOriginalSourceBytes()
    {
        (Task<SvgPreflightResult> task, WeakReference<byte[]> source) = StartFaultedInspection();

        await Assert.ThrowsAsync<MarkdownSvgException>(async () =>
            await task.WaitAsync(TimeSpan.FromSeconds(5)));

        AssertCollected(source);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task CanceledInspectionTaskDoesNotRetainOriginalSourceBytes()
    {
        (Task<SvgPreflightResult> task, WeakReference<byte[]> source) = StartCanceledInspection();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await task.WaitAsync(TimeSpan.FromSeconds(5)));

        AssertCollected(source);
        GC.KeepAlive(task);
    }

    private static byte[] EncodeWithPreamble(Encoding encoding, string value)
    {
        byte[] preamble = encoding.GetPreamble();
        byte[] contents = encoding.GetBytes(value);
        byte[] result = new byte[preamble.Length + contents.Length];
        preamble.CopyTo(result, 0);
        contents.CopyTo(result, preamble.Length);
        return result;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<BoundedOutputStream> Task, WeakReference<byte[]> Source) StartUtf16Normalization()
    {
        string sourceText = "<?xml version='1.0' encoding='utf-16'?><svg xmlns='http://www.w3.org/2000/svg'/>";
        byte[] source = EncodeWithPreamble(new UnicodeEncoding(false, true), sourceText);
        WeakReference<byte[]> sourceReference = new(source);
        Task<BoundedOutputStream> task = SvgUtf8Normalization.NormalizeToUtf8SegmentsAsync(
            source,
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);
        return (task, sourceReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<BoundedOutputStream> Task, WeakReference<byte[]> Source) StartFaultedUtf16Normalization()
    {
        string sourceText = "<?xml version='1.0' encoding='utf-16'?><svg xmlns='http://www.w3.org/2000/svg'>";
        byte[] source = EncodeWithPreamble(new UnicodeEncoding(false, true), sourceText);
        WeakReference<byte[]> sourceReference = new(source);
        Task<BoundedOutputStream> task = SvgUtf8Normalization.NormalizeToUtf8SegmentsAsync(
            source,
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);
        return (task, sourceReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<BoundedOutputStream> Task, WeakReference<byte[]> Source) StartCanceledUtf16Normalization()
    {
        string sourceText = "<?xml version='1.0' encoding='utf-16'?><svg xmlns='http://www.w3.org/2000/svg'/>";
        byte[] source = EncodeWithPreamble(new UnicodeEncoding(false, true), sourceText);
        WeakReference<byte[]> sourceReference = new(source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task<BoundedOutputStream> task = SvgUtf8Normalization.NormalizeToUtf8SegmentsAsync(
            source,
            new ResvgMarkdownSvgRendererOptions(),
            cancellation.Token);
        return (task, sourceReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<SvgPreflightResult> Task, WeakReference<byte[]> Source) StartFaultedInspection()
    {
        byte[] source = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'");
        WeakReference<byte[]> sourceReference = new(source);
        Task<SvgPreflightResult> task = SvgPreflight.InspectCoreAsync(
            source,
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);
        return (task, sourceReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<SvgPreflightResult> Task, WeakReference<byte[]> Source) StartCanceledInspection()
    {
        byte[] source = Encoding.UTF8.GetBytes("<svg xmlns='http://www.w3.org/2000/svg'/>");
        WeakReference<byte[]> sourceReference = new(source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task<SvgPreflightResult> task = SvgPreflight.InspectCoreAsync(
            source,
            new ResvgMarkdownSvgRendererOptions(),
            cancellation.Token);
        return (task, sourceReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertCollected(WeakReference<byte[]> reference)
    {
        // A TryGetTarget loop condition can keep its JIT temporary strongly
        // rooted across the very collection being tested. Collect first, with
        // no target obtained in this frame, then inspect the weak reference.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(reference.TryGetTarget(out _), "The completed normalization task retained its input byte array.");
    }
}
