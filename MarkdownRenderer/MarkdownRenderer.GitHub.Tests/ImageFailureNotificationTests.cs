using System.Collections.Concurrent;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class ImageFailureNotificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodeErrorAfterSuccessfulResolutionReportsOnceAndCachedFailureAlsoReports(bool progressive)
    {
        byte[] bytes = DisplaySizedRasterDecodeTests.CreatePng(8, 4);
        // Zero is not a legal PNG bit depth. Resource admission only reads
        // dimensions/frame counts; the authoritative decoder must reject it.
        // Corrupting an IDAT byte is insufficient: WIC can recover that image.
        bytes[24] = 0;
        Assert.True(RasterImageResourceBudget.Validate(bytes).Accepted);
        string key = $"failure-notification-{Guid.NewGuid():N}";
        using var session = progressive ? new MarkdownPerformanceSession(MarkdownPerformanceOptions.Progressive) : null;
        var reports = new ConcurrentQueue<MarkdownImageUnavailableReason>();
        var resolver = new ByteResolver(bytes, key);
        var context = DisplaySizedRasterDecodeTests.CreateContext(resolver, session, 1,
            imageUnavailable: (_, reason, _) => reports.Enqueue(reason));
        var first = new ImageBox(context, "https://images.example/broken.png", "Banner");
        var cached = new ImageBox(context, "https://images.example/broken.png", "Banner");
        try
        {
            first.Measure(100);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(first);
            Assert.Equal(MarkdownImageAccessibilityState.Error, first.AccessibilityState);
            Assert.Null(first.Bitmap);
            Assert.Equal(MarkdownImageUnavailableReason.Unavailable, Assert.Single(reports));
            first.EnsureLoading();
            first.EnsureLoading();
            Assert.Single(reports);

            cached.Measure(100);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(cached);
            Assert.Equal(MarkdownImageAccessibilityState.Error, cached.AccessibilityState);
            Assert.Equal(2, reports.Count);
            cached.EnsureLoading();
            Assert.Equal(2, reports.Count);
        }
        finally
        {
            first.Dispose();
            cached.Dispose();
        }
    }

    [Theory]
    [InlineData(MarkdownImageUnavailableReason.Offline)]
    [InlineData(MarkdownImageUnavailableReason.RemoteContentBlocked)]
    public async Task ExplicitPolicyFailureIsNotReportedAgainAsGenericUnavailable(MarkdownImageUnavailableReason reason)
    {
        var reports = new ConcurrentQueue<MarkdownImageUnavailableReason>();
        var context = DisplaySizedRasterDecodeTests.CreateContext(new BlockedResolver(reason), null, 1,
            imageUnavailable: (_, value, _) => reports.Enqueue(value));
        var image = new ImageBox(context, "https://images.example/blocked.png", "Banner");
        try
        {
            image.Measure(100);
            await DisplaySizedRasterDecodeTests.WaitForLoadAsync(image);
            image.EnsureLoading();
            Assert.Equal(MarkdownImageAccessibilityState.Error, image.AccessibilityState);
            Assert.Equal(reason, Assert.Single(reports));
        }
        finally { image.Dispose(); }
    }

    [Fact]
    public void InitiallyFailedImageReportsOnlyOnActivationAndNeverAfterDisposal()
    {
        var reports = new ConcurrentQueue<MarkdownImageUnavailableReason>();
        var context = DisplaySizedRasterDecodeTests.CreateContext(
            new BlockedResolver(MarkdownImageUnavailableReason.Offline), null, 1,
            imageUnavailable: (_, value, _) => reports.Enqueue(value));
        var image = new ImageBox(context, string.Empty, "Missing source");
        var retired = new ImageBox(context, string.Empty, "Retired source");
        try
        {
            Assert.Empty(reports);
            image.EnsureLoading();
            image.EnsureLoading();
            Assert.Equal(MarkdownImageUnavailableReason.Unavailable, Assert.Single(reports));
            retired.Dispose();
            retired.EnsureLoading();
            Assert.Single(reports);
        }
        finally { image.Dispose(); retired.Dispose(); }
    }

    private sealed class ByteResolver(byte[] bytes, string key) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(string source,
            MarkdownImageResolveContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Resolved(new MarkdownImageAsset(bytes, "image/png", CacheKey: key)));
    }

    private sealed class BlockedResolver(MarkdownImageUnavailableReason reason) : IMarkdownImageResolver
    {
        public ValueTask<MarkdownImageResolution> ResolveAsync(string source,
            MarkdownImageResolveContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(MarkdownImageResolution.Blocked(reason));
    }
}
