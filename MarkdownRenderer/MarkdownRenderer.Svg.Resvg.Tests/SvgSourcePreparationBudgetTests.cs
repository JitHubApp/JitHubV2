using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Images;
using Xunit;

namespace MarkdownRenderer.Svg.Resvg.Tests;

public sealed class SvgSourcePreparationBudgetTests
{
    [Fact]
    public void LoweredSourceCeilingAppliesToUnchangedSanitizedBytes()
    {
        byte[] source = Svg("<svg xmlns='http://www.w3.org/2000/svg' data-value='" + new string('x', 512) + "'/>");
        using var renderer = new ResvgMarkdownSvgRenderer(new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 256));

        MarkdownSvgSourcePreparation result = renderer.PrepareSource(source, CancellationToken.None);

        Assert.Null(result.SanitizedBytes);
        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, result.FailureReason);
        Assert.Contains("input-bytes", result.FailureDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void LoweredSourceCeilingDoesNotRejectRemovedRawScriptBytes()
    {
        byte[] source = Svg("<svg xmlns='http://www.w3.org/2000/svg'><script><![CDATA[" +
            new string('x', 4096) + "]]></script><rect width='1' height='1'/></svg>");
        byte[] original = (byte[])source.Clone();
        using var renderer = new ResvgMarkdownSvgRenderer(new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 256));

        MarkdownSvgSourcePreparation result = renderer.PrepareSource(source, CancellationToken.None);

        Assert.Null(result.FailureReason);
        Assert.NotNull(result.SanitizedBytes);
        Assert.InRange(result.SanitizedBytes.Length, 1, 256);
        Assert.DoesNotContain("script", Encoding.UTF8.GetString(result.SanitizedBytes), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, source);
    }

    [Fact]
    public void SerializationCeilingReturnsTypedRejectionAndContentFreeAuditMetadata()
    {
        byte[] source = Encoding.Latin1.GetBytes("<?xml version='1.0' encoding='iso-8859-1'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg' data-value='" + new string('\u00ff', 512) +
            "'><script/></svg>");
        byte[] original = (byte[])source.Clone();
        using var listener = new PreparationListener();
        using var renderer = new ResvgMarkdownSvgRenderer(new ResvgMarkdownSvgRendererOptions(maxSourceBytes: 800));

        MarkdownSvgSourcePreparation result = renderer.PrepareSource(source, CancellationToken.None);

        Assert.True(source.Length < 800);
        Assert.Null(result.SanitizedBytes);
        Assert.Equal(MarkdownSvgFailureReason.ResourceLimitExceeded, result.FailureReason);
        Assert.Contains("static-snapshot-output-bytes", result.FailureDescription, StringComparison.Ordinal);
        Assert.Contains(listener.Rejections, entry => entry == (
            "static-snapshot-output-bytes", source.Length, Convert.ToHexString(SHA256.HashData(source))));
        Assert.Equal(original, source);
    }

    [Fact]
    public void PreCanceledPreparationDoesNotAllocateASourceSizedOutput()
    {
        byte[] source = Svg("<svg xmlns='http://www.w3.org/2000/svg'><script>" +
            new string('x', 1024 * 1024) + "</script></svg>");
        using var renderer = new ResvgMarkdownSvgRenderer();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => renderer.PrepareSource(source, cancellation.Token));

        long before = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            _ = renderer.PrepareSource(source, cancellation.Token);
            Assert.Fail("A canceled preparation was admitted.");
        }
        catch (OperationCanceledException)
        {
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 64 * 1024, $"Canceled preparation allocated {allocated} bytes.");
    }

    private static byte[] Svg(string source) => Encoding.UTF8.GetBytes(source);

    private sealed class PreparationListener : EventListener
    {
        internal ConcurrentQueue<(string Reason, int Length, string Hash)> Rejections { get; } = new();

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "MarkdownRenderer.Svg.Resvg.Preflight")
                EnableEvents(source, EventLevel.Warning);
        }

        protected override void OnEventWritten(EventWrittenEventArgs args)
        {
            if (args.EventId == 1 && args.Payload is { Count: 3 } payload &&
                payload[0] is string reason && payload[1] is int length && payload[2] is string hash)
            {
                Rejections.Enqueue((reason, length, hash));
            }
        }
    }
}
