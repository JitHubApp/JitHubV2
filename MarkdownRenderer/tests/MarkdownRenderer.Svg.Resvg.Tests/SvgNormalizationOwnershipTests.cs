using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using MarkdownRenderer.Svg.Resvg.Internal;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgNormalizationOwnershipTests
{
    [Fact]
    public async Task CompletedSegmentProducerReleasesInputBeforeCallerFlattens()
    {
        (Task<SvgPreflight.BoundedOutputStream> task, WeakReference<byte[]> source) =
            StartUtf16Segments();

        using SvgPreflight.BoundedOutputStream segments = await task.WaitAsync(TimeSpan.FromSeconds(5));

        AssertCollected(source);

        using SvgSourceBuffer normalizedSource = segments.DetachSourceBuffer();
        byte[] normalized = normalizedSource.CopyToArrayForTest();
        Assert.NotEmpty(normalized);
        Assert.StartsWith("<?xml", Encoding.UTF8.GetString(normalized), StringComparison.Ordinal);
        Assert.False(normalized.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));

        using var input = new MemoryStream(normalized, writable: false);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
        bool foundComment = false;
        bool foundRoot = false;
        while (reader.Read())
        {
            foundComment |= reader.NodeType == XmlNodeType.Comment && reader.Value == "retained";
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "svg")
            {
                foundRoot = true;
                Assert.Equal("1", reader.GetAttribute("width"));
            }
        }

        Assert.True(foundComment);
        Assert.True(foundRoot);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task FaultedSegmentProducerReleasesInput()
    {
        (Task<SvgPreflight.BoundedOutputStream> task, WeakReference<byte[]> source) =
            StartFaultedUtf16Segments();

        await Assert.ThrowsAsync<XmlException>(async () =>
            await task.WaitAsync(TimeSpan.FromSeconds(5)));

        AssertCollected(source);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task PreCanceledSegmentProducerReleasesInput()
    {
        (Task<SvgPreflight.BoundedOutputStream> task, WeakReference<byte[]> source) =
            StartPreCanceledUtf16Segments();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await task.WaitAsync(TimeSpan.FromSeconds(5)));

        AssertCollected(source);
        GC.KeepAlive(task);
    }

    [Fact]
    public async Task CancellationAfterProductionPreventsFlattenAndCallerDisposesSegments()
    {
        (Task<SvgPreflight.BoundedOutputStream> task,
            WeakReference<byte[]> source,
            CancellationTokenSource cancellation) = StartCancelableUtf16Segments();
        using (cancellation)
        using (SvgPreflight.BoundedOutputStream segments = await task.WaitAsync(TimeSpan.FromSeconds(5)))
        {
            AssertCollected(source);
            Assert.True(segments.Length > 0);

            cancellation.Cancel();
            Assert.ThrowsAny<OperationCanceledException>(() => segments.DetachSourceBuffer());
        }

        Assert.Equal(0, await ReadDisposedLengthAsync(task));
        GC.KeepAlive(task);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<SvgPreflight.BoundedOutputStream> Task, WeakReference<byte[]> Source) StartUtf16Segments()
    {
        const string sourceText = "<?xml version='1.0' encoding='utf-16' standalone='yes'?><!--retained--><svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>";
        byte[] source = EncodeWithPreamble(new UnicodeEncoding(bigEndian: false, byteOrderMark: true), sourceText);
        WeakReference<byte[]> sourceReference = new(source);
        Task<SvgPreflight.BoundedOutputStream> task = SvgPreflight.NormalizeToUtf8SegmentsAsync(
            source,
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);
        return (task, sourceReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<SvgPreflight.BoundedOutputStream> Task, WeakReference<byte[]> Source) StartFaultedUtf16Segments()
    {
        const string sourceText = "<?xml version='1.0' encoding='utf-16'?><svg xmlns='http://www.w3.org/2000/svg'>";
        byte[] source = EncodeWithPreamble(new UnicodeEncoding(bigEndian: false, byteOrderMark: true), sourceText);
        WeakReference<byte[]> sourceReference = new(source);
        Task<SvgPreflight.BoundedOutputStream> task = SvgPreflight.NormalizeToUtf8SegmentsAsync(
            source,
            new ResvgMarkdownSvgRendererOptions(),
            CancellationToken.None);
        return (task, sourceReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Task<SvgPreflight.BoundedOutputStream> Task, WeakReference<byte[]> Source) StartPreCanceledUtf16Segments()
    {
        const string sourceText = "<?xml version='1.0' encoding='utf-16'?><svg xmlns='http://www.w3.org/2000/svg'/>";
        byte[] source = EncodeWithPreamble(new UnicodeEncoding(bigEndian: false, byteOrderMark: true), sourceText);
        WeakReference<byte[]> sourceReference = new(source);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task<SvgPreflight.BoundedOutputStream> task = SvgPreflight.NormalizeToUtf8SegmentsAsync(
            source,
            new ResvgMarkdownSvgRendererOptions(),
            cancellation.Token);
        return (task, sourceReference);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (
        Task<SvgPreflight.BoundedOutputStream> Task,
        WeakReference<byte[]> Source,
        CancellationTokenSource Cancellation) StartCancelableUtf16Segments()
    {
        const string sourceText = "<?xml version='1.0' encoding='utf-16'?><svg xmlns='http://www.w3.org/2000/svg' width='1' height='1'/>";
        byte[] source = EncodeWithPreamble(new UnicodeEncoding(bigEndian: false, byteOrderMark: true), sourceText);
        WeakReference<byte[]> sourceReference = new(source);
        var cancellation = new CancellationTokenSource();
        Task<SvgPreflight.BoundedOutputStream> task = SvgPreflight.NormalizeToUtf8SegmentsAsync(
            source,
            new ResvgMarkdownSvgRendererOptions(),
            cancellation.Token);
        return (task, sourceReference, cancellation);
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
    private static void AssertCollected(WeakReference<byte[]> reference)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(reference.TryGetTarget(out _), "The normalization task retained its input byte array.");
    }

    private static async Task<long> ReadDisposedLengthAsync(Task<SvgPreflight.BoundedOutputStream> task)
    {
        SvgPreflight.BoundedOutputStream result = await task;
        return result.Length;
    }
}
