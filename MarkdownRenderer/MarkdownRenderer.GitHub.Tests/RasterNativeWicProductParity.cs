using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>Exact Windows oracle for the shipping decoder, not its prototype.</summary>
internal static class RasterNativeWicProductParity
{
    internal static async Task AssertExactAsync(
        byte[] source, RasterImageBudgetResult budget, byte[] expected, int width, int height)
    {
        var sizing = new RasterImageSizingRequest(width, 1,
            new SafeHtmlLength(width, IsPercent: false),
            new SafeHtmlLength(height, IsPercent: false), checked((long)width * height));
        Assert.True(RasterImageDecodePlan.TryCreate(source, budget, sizing, out var plan));
        Assert.Equal(width, plan.OutputWidth);
        Assert.Equal(height, plan.OutputHeight);
        Assert.True(RasterNativeWicDecoder.IsQualifiedPlan(plan));
        var credit = new CountingCredit();
        RasterDecodedPixelLease pixels = await Task.Run(() =>
            RasterNativeWicDecoder.Decode(source, plan, credit, CancellationToken.None));
        byte[] borrowed = pixels.Pixels;
        try
        {
            Assert.Equal(0, credit.DisposeCount); // Credit survives native retirement and awaits upload.
            Assert.Equal(width, pixels.Width);
            Assert.Equal(height, pixels.Height);
            Assert.Equal(expected.Length, borrowed.Length);
            Assert.True(expected.AsSpan().SequenceEqual(borrowed), "Shipping WIC pixels differ from Windows oracle.");
        }
        finally
        {
            pixels.Dispose();
        }
        Assert.Equal(1, credit.DisposeCount);
        Assert.True(borrowed.AsSpan().IndexOfAnyExcept((byte)0) < 0);
        Assert.Throws<ObjectDisposedException>(() => pixels.Pixels);
        pixels.Dispose();
        Assert.Equal(1, credit.DisposeCount);
    }

    internal sealed class CountingCredit : IDisposable
    {
        private int _disposeCount;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
