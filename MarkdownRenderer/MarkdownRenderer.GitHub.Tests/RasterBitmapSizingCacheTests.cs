using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using Microsoft.Graphics.Canvas;
using Windows.Graphics.DirectX;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class RasterBitmapSizingCacheTests
{
    [Fact]
    public void BestPreviewUsesOrientedIntrinsicDimensionsAndLeaseMatchesExactSizeOnly()
    {
        using var device = new CanvasDevice();
        string source = UniqueSource();
        var targetRequest = CreateRequest(availableWidth: 6);
        var previewRequest = CreateRequest(availableWidth: 4);
        Assert.Equal((6, 2), targetRequest.GetSize(8, 2));
        Assert.Equal((2, 8), targetRequest.GetSize(2, 8));
        Assert.Equal(
            targetRequest.GetCacheFamily(source, displaySized: true),
            previewRequest.GetCacheFamily(source, displaySized: true));

        string family = targetRequest.GetCacheFamily(source, displaySized: true);
        (int Width, int Height) previewSize = previewRequest.GetSize(8, 2);
        Assert.Equal((4, 1), previewSize);
        string identity = previewRequest.GetCacheKey(family, previewSize);
        SharedCanvasBitmapCache.Lease? stored = null;
        SharedCanvasBitmapCache.Lease? preview = null;
        try
        {
            stored = Store(device, identity, previewSize.Width, previewSize.Height, new Size(8, 2));

            Assert.True(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device,
                family,
                targetRequest,
                out preview));
            Assert.NotNull(preview);
            Assert.Equal((uint)previewSize.Width, preview.Bitmap.SizeInPixels.Width);
            Assert.Equal((uint)previewSize.Height, preview.Bitmap.SizeInPixels.Height);
            Assert.False(preview.MatchesSizingRequest(targetRequest));
            Assert.False(preview.MatchesSizingRequest(default));
            Assert.True(preview.MatchesSizingRequest(previewRequest));
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
            preview?.Dispose();
            stored?.Dispose();
        }
    }

    [Fact]
    public void BestPreviewDoesNotCrossPixelCapAuthoredSizeOrDisplaySizingFamilies()
    {
        using var device = new CanvasDevice();
        string source = UniqueSource();
        var baseRequest = CreateRequest(availableWidth: 2, maximumPixels: 128);
        var previewSeedRequest = CreateRequest(availableWidth: 1, maximumPixels: 128);
        var cappedRequest = CreateRequest(availableWidth: 6, maximumPixels: 3);
        var authoredWidthRequest = new RasterImageSizingRequest(
            availableWidth: 6,
            rasterizationScale: 1,
            requestedWidth: new SafeHtmlLength(3, IsPercent: false),
            requestedHeight: null,
            maximumPixels: 128);
        string baseFamily = baseRequest.GetCacheFamily(source, displaySized: true);
        string cappedFamily = cappedRequest.GetCacheFamily(source, displaySized: true);
        string authoredFamily = authoredWidthRequest.GetCacheFamily(source, displaySized: true);
        string nondisplayFamily = baseRequest.GetCacheFamily(source, displaySized: false);
        Assert.NotEqual(baseFamily, cappedFamily);
        Assert.NotEqual(baseFamily, authoredFamily);
        Assert.NotEqual(baseFamily, nondisplayFamily);

        (int Width, int Height) cachedSize = previewSeedRequest.GetSize(8, 2);
        Assert.Equal((1, 1), cachedSize);
        string identity = previewSeedRequest.GetCacheKey(baseFamily, cachedSize);
        SharedCanvasBitmapCache.Lease? stored = null;
        try
        {
            stored = Store(device, identity, cachedSize.Width, cachedSize.Height, new Size(8, 2));

            // The candidate is geometrically usable for each request, but its
            // different pixel ceiling, authored sizing rule, or sizing mode
            // makes it a distinct cache family.
            Assert.Equal((3, 1), cappedRequest.GetSize(8, 2));
            Assert.Equal((3, 1), authoredWidthRequest.GetSize(8, 2));
            Assert.False(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device, cappedFamily, cappedRequest, out _));
            Assert.False(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device, authoredFamily, authoredWidthRequest, out _));
            Assert.False(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device, nondisplayFamily, baseRequest, out _));
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
            stored?.Dispose();
        }
    }

    [Fact]
    public void UnknownIntrinsicMetadataCannotBeSelectedAsRasterPreview()
    {
        using var device = new CanvasDevice();
        string source = UniqueSource();
        var request = CreateRequest(availableWidth: 6);
        string family = request.GetCacheFamily(source, displaySized: true);
        (int Width, int Height) previewSize = (4, 1);
        string identity = CreateVariantKey(family, previewSize, requestSuffix: "request:4@1");
        SharedCanvasBitmapCache.Lease? stored = null;
        try
        {
            stored = Store(device, identity, previewSize.Width, previewSize.Height, intrinsicSize: null);
            Assert.False(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device, family, request, out _));
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
            stored?.Dispose();
        }
    }

    [Fact]
    public void NonIntegralIntrinsicMetadataCannotBeSelectedAsRasterPreview()
    {
        using var device = new CanvasDevice();
        string source = UniqueSource();
        var request = CreateRequest(availableWidth: 6);
        string family = request.GetCacheFamily(source, displaySized: true);
        SharedCanvasBitmapCache.Lease? stored = null;
        try
        {
            stored = Store(
                device,
                CreateVariantKey(family, (4, 1), "request:4@1"),
                width: 4,
                height: 1,
                intrinsicSize: new Size(8.5, 2));
            Assert.False(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device, family, request, out _));
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
            stored?.Dispose();
        }
    }

    [Fact]
    public void OversizedAndEqualOutputBitmapsAreNotPreviewCandidates()
    {
        using var device = new CanvasDevice();
        string oversizedSource = UniqueSource();
        var cappedRequest = CreateRequest(availableWidth: 6, maximumPixels: 3);
        string cappedFamily = cappedRequest.GetCacheFamily(oversizedSource, displaySized: true);
        SharedCanvasBitmapCache.Lease? oversized = null;

        string equalSource = UniqueSource();
        var equalRequest = CreateRequest(availableWidth: 4);
        string equalFamily = equalRequest.GetCacheFamily(equalSource, displaySized: true);
        SharedCanvasBitmapCache.Lease? equal = null;
        try
        {
            Assert.Equal((3, 1), cappedRequest.GetSize(8, 2));
            oversized = Store(
                device,
                CreateVariantKey(cappedFamily, (4, 1), "request:4@1"),
                width: 4,
                height: 1,
                intrinsicSize: new Size(8, 2));
            Assert.False(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device, cappedFamily, cappedRequest, out _));

            Assert.Equal((4, 1), equalRequest.GetSize(8, 2));
            equal = Store(
                device,
                CreateVariantKey(equalFamily, (4, 1), "request:4@1"),
                width: 4,
                height: 1,
                intrinsicSize: new Size(8, 2));
            Assert.False(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device, equalFamily, equalRequest, out _));
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
            equal?.Dispose();
            oversized?.Dispose();
        }
    }

    [Fact]
    public void StrictlySmallerDimensionRemainsEligibleWhenOtherDimensionIsEqual()
    {
        using var device = new CanvasDevice();
        string source = UniqueSource();
        var request = CreateRequest(availableWidth: 6);
        string family = request.GetCacheFamily(source, displaySized: true);
        SharedCanvasBitmapCache.Lease? stored = null;
        SharedCanvasBitmapCache.Lease? preview = null;
        try
        {
            stored = Store(
                device,
                CreateVariantKey(family, (6, 1), "request:6@1"),
                width: 6,
                height: 1,
                intrinsicSize: new Size(8, 2));
            Assert.True(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device, family, request, out preview));
            Assert.NotNull(preview);
            Assert.Equal((uint)6, preview.Bitmap.SizeInPixels.Width);
            Assert.Equal((uint)1, preview.Bitmap.SizeInPixels.Height);
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
            preview?.Dispose();
            stored?.Dispose();
        }
    }

    [Theory]
    [InlineData("request:6@not-a-scale")]
    [InlineData("request:0@1")]
    [InlineData("request:6@0")]
    [InlineData("request:NaN@1")]
    [InlineData("request:6@Infinity")]
    [InlineData("request: 6@1")]
    [InlineData("request:6@1 ")]
    [InlineData("request:6@1\u001Fextra")]
    [InlineData("request:6@1@1")]
    [InlineData("request:")]
    [InlineData("")]
    public void ValidRequestSuffixIsAcceptedAndMalformedSuffixIsRejected(string malformedSuffix)
    {
        using var device = new CanvasDevice();
        string validSource = UniqueSource();
        var request = CreateRequest(availableWidth: 6);
        string validFamily = request.GetCacheFamily(validSource, displaySized: true);
        (int Width, int Height) previewSize = (4, 1);
        string validIdentity = request.GetCacheKey(validFamily, previewSize);
        SharedCanvasBitmapCache.Lease? validStored = null;
        SharedCanvasBitmapCache.Lease? validPreview = null;

        string malformedSource = UniqueSource();
        string malformedFamily = request.GetCacheFamily(malformedSource, displaySized: true);
        SharedCanvasBitmapCache.Lease? malformedStored = null;
        try
        {
            Assert.EndsWith("\u001Frequest:6@1", validIdentity, StringComparison.Ordinal);
            validStored = Store(device, validIdentity, previewSize.Width, previewSize.Height, new Size(8, 2));
            Assert.True(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device, validFamily, request, out validPreview));
            Assert.NotNull(validPreview);

            malformedStored = Store(
                device,
                CreateVariantKey(malformedFamily, previewSize, malformedSuffix),
                previewSize.Width,
                previewSize.Height,
                new Size(8, 2));
            Assert.False(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device, malformedFamily, request, out _));
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
            malformedStored?.Dispose();
            validPreview?.Dispose();
            validStored?.Dispose();
        }
    }

    [Theory]
    [InlineData("0x0")]
    [InlineData("0x1")]
    [InlineData("1x0")]
    [InlineData("2147483648x1")]
    [InlineData("1x2147483648")]
    [InlineData("-1x1")]
    [InlineData("1x1suffix")]
    public void InvalidDisplayDimensionsAreRejectedEvenWhenEntryMetadataLooksCompatible(string displayDimensions)
    {
        using var device = new CanvasDevice();
        string source = UniqueSource();
        var request = CreateRequest(availableWidth: 6);
        string family = request.GetCacheFamily(source, displaySized: true);
        SharedCanvasBitmapCache.Lease? stored = null;
        try
        {
            stored = Store(
                device,
                CreateVariantKey(family, displayDimensions, "request:6@1"),
                width: 4,
                height: 1,
                intrinsicSize: new Size(8, 2));

            Assert.False(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device, family, request, out _));
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
            stored?.Dispose();
        }
    }

    [Fact]
    public void DisposedPreviewLeaseNoLongerMatchesOrExposesTheBitmap()
    {
        using var device = new CanvasDevice();
        string source = UniqueSource();
        var targetRequest = CreateRequest(availableWidth: 6);
        var previewRequest = CreateRequest(availableWidth: 4);
        string family = targetRequest.GetCacheFamily(source, displaySized: true);
        (int Width, int Height) size = (4, 1);
        string identity = previewRequest.GetCacheKey(family, size);
        SharedCanvasBitmapCache.Lease? stored = null;
        SharedCanvasBitmapCache.Lease? preview = null;
        try
        {
            stored = Store(device, identity, size.Width, size.Height, new Size(8, 2));
            Assert.True(SharedCanvasBitmapCache.TryAcquireBestRasterPreview(
                device, family, targetRequest, out preview));
            Assert.NotNull(preview);
            preview.Dispose();
            Assert.False(preview.MatchesSizingRequest(targetRequest));
            Assert.Null(preview.TryClone());
            Assert.Throws<ObjectDisposedException>(() => _ = preview.Bitmap);
        }
        finally
        {
            SharedCanvasBitmapCache.ReleaseDevice(device);
            preview?.Dispose();
            stored?.Dispose();
        }
    }

    private static RasterImageSizingRequest CreateRequest(
        double availableWidth,
        long maximumPixels = 128) =>
        new(
            availableWidth,
            rasterizationScale: 1,
            requestedWidth: null,
            requestedHeight: null,
            maximumPixels: maximumPixels);

    private static string UniqueSource() => $"raster-sizing-cache-{Guid.NewGuid():N}";

    private static string CreateVariantKey(
        string family,
        (int Width, int Height) dimensions,
        string requestSuffix) =>
        CreateVariantKey(family, $"{dimensions.Width}x{dimensions.Height}", requestSuffix);

    private static string CreateVariantKey(
        string family,
        string displayDimensions,
        string requestSuffix) =>
        string.Concat(family, "\u001Fdisplay:", displayDimensions, "\u001F", requestSuffix);

    private static SharedCanvasBitmapCache.Lease Store(
        CanvasDevice device,
        string identity,
        int width,
        int height,
        Size? intrinsicSize) =>
        SharedCanvasBitmapCache.StoreAndAcquire(
            device,
            identity,
            CreateBitmap(device, width, height),
            intrinsicSize);

    private static CanvasBitmap CreateBitmap(CanvasDevice device, int width, int height)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        return CanvasBitmap.CreateFromBytes(
            device,
            pixels,
            width,
            height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
    }
}
