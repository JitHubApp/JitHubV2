using System.Globalization;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class RasterImageSizingRequestTests
{
    [Fact]
    public void DefaultRequestIsNotAValidSizingPolicy()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => default(RasterImageSizingRequest).GetSize(1, 1));
    }

    [Theory]
    [InlineData(0.5, 100, 50)]
    [InlineData(0.75, 150, 75)]
    [InlineData(0.9, 180, 90)]
    [InlineData(1.0, 200, 100)]
    [InlineData(1.25, 250, 125)]
    [InlineData(1.5, 300, 150)]
    [InlineData(1.75, 350, 175)]
    [InlineData(2.0, 400, 200)]
    [InlineData(2.25, 450, 225)]
    [InlineData(3.0, 600, 300)]
    [InlineData(4.0, 800, 400)]
    public void PlansPhysicalDisplayDimensions(double scale, int width, int height)
    {
        var request = new RasterImageSizingRequest(200, scale, null, null, 4_194_304);
        Assert.Equal((width, height), request.GetSize(1024, 512));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void InvalidScaleFallsBackToOne(double scale)
    {
        var request = new RasterImageSizingRequest(200, scale, null, null, 4_194_304);
        Assert.Equal((200, 100), request.GetSize(1024, 512));
    }

    [Fact]
    public void OrientedSourceUsesTheSameDetachedPolicyAndItsCeiling()
    {
        var request = new RasterImageSizingRequest(800, 1, null, null, 400_000);
        Assert.Equal((800, 400), request.GetSize(1600, 800));
        Assert.Equal((447, 894), request.GetSize(800, 1600));
        Assert.True(447L * 894 <= request.MaximumPixels);
    }

    [Theory]
    [InlineData(16384, 1, 1L)]
    [InlineData(1, 16384, 1L)]
    [InlineData(16384, 1, 10000L)]
    [InlineData(1, 16384, 10000L)]
    [InlineData(16384, 2, 10L)]
    [InlineData(2, 16384, 10L)]
    public void OnePixelShortEdgeCannotExceedTheOutputCeiling(int width, int height, long cap)
    {
        var request = new RasterImageSizingRequest(16384, 4, null, null, cap);
        var size = request.GetSize(width, height);
        Assert.InRange(size.Width, 1, width);
        Assert.InRange(size.Height, 1, height);
        Assert.True((long)size.Width * size.Height <= cap);
    }

    [Fact]
    public void CacheFamilySeparatesPoliciesButPreservesWidthAndDpiPreviewReuse()
    {
        var first = new RasterImageSizingRequest(200, 1, null, null, 400_000);
        var larger = new RasterImageSizingRequest(400, 2, null, null, 400_000);
        var otherCap = new RasterImageSizingRequest(200, 1, null, null, 500_000);
        string family = first.GetCacheFamily("partitioned-source", true);
        Assert.Equal(family, larger.GetCacheFamily("partitioned-source", true));
        Assert.NotEqual(family, otherCap.GetCacheFamily("partitioned-source", true));
        Assert.NotEqual(family, first.GetCacheFamily("partitioned-source", false));
        Assert.NotEqual(first.GetCacheKey(family, (200, 100)), larger.GetCacheKey(family, (200, 100)));
    }

    [Fact]
    public void RawAxisClampingDoesNotAliasDifferentOrientedRequests()
    {
        var first = new RasterImageSizingRequest(1000, 1, null, null, 4_194_304);
        var second = new RasterImageSizingRequest(1500, 1, null, null, 4_194_304);
        Assert.Equal(first.GetSize(800, 1600), second.GetSize(800, 1600));
        string family = first.GetCacheFamily("portrait", true);
        Assert.NotEqual(first.GetCacheKey(family, first.GetSize(800, 1600)),
            second.GetCacheKey(family, second.GetSize(800, 1600)));
        Assert.Equal((1000, 500), first.GetSize(1600, 800));
        Assert.Equal((1500, 750), second.GetSize(1600, 800));
    }

    [Fact]
    public void AuthoredPercentWidthAndAbsoluteHeightRemainDistinctSizingRules()
    {
        var percent = new RasterImageSizingRequest(800, 1,
            new SafeHtmlLength(50, true), null, 4_194_304);
        var absolute = new RasterImageSizingRequest(800, 1,
            new SafeHtmlLength(50, false), null, 4_194_304);
        var height = new RasterImageSizingRequest(800, 1,
            null, new SafeHtmlLength(100, false), 4_194_304);
        Assert.Equal((400, 200), percent.GetSize(1600, 800));
        Assert.Equal((50, 25), absolute.GetSize(1600, 800));
        Assert.Equal((200, 100), height.GetSize(1600, 800));
        Assert.NotEqual(percent.GetCacheFamily("source", true), absolute.GetCacheFamily("source", true));
    }

    [Theory]
    [InlineData(800f, 400f, false, 200, 100)]
    [InlineData(800f, 0f, false, 200, 100)]
    [InlineData(0f, 800f, false, 200, 100)]
    [InlineData(0f, 0f, false, 200, 100)]
    [InlineData(0f, 0f, true, 200, 100)]
    [InlineData(-2f, -2f, false, 200, 100)]
    [InlineData(100f, 100f, false, 100, 100)]
    [InlineData(50f, 0f, false, 50, 25)]
    [InlineData(50f, 0f, true, 100, 50)]
    [InlineData(800f, 800f, false, 200, 200)]
    [InlineData(0f, 100f, false, 200, 100)]
    public void AuthoredSizingMatchesCellContainmentAndIgnoresNonpositiveDimensions(
        float width, float height, bool percentWidth, int expectedWidth, int expectedHeight)
    {
        var request = new RasterImageSizingRequest(200, 1,
            new SafeHtmlLength(width, percentWidth), new SafeHtmlLength(height, false), 4_194_304);
        Assert.Equal((expectedWidth, expectedHeight), request.GetSize(1600, 800));
    }

    [Theory]
    [InlineData(1.0, 200, 100)]
    [InlineData(1.25, 250, 125)]
    [InlineData(1.5, 300, 150)]
    [InlineData(1.75, 350, 175)]
    [InlineData(2.0, 400, 200)]
    [InlineData(2.25, 450, 225)]
    [InlineData(3.0, 600, 300)]
    [InlineData(4.0, 800, 400)]
    public void OversizedAuthoredDimensionsStayDisplaySizedAtEveryDpi(double scale, int width, int height)
    {
        var request = new RasterImageSizingRequest(200, scale,
            new SafeHtmlLength(800, false), new SafeHtmlLength(400, false), 4_194_304);
        Assert.Equal((width, height), request.GetSize(1600, 800));
    }

    [Fact]
    public void NonpositiveAuthoredDimensionsShareTheUnspecifiedCacheFamily()
    {
        var unspecified = new RasterImageSizingRequest(200, 1, null, null, 4_194_304);
        var ignored = new RasterImageSizingRequest(200, 1,
            new SafeHtmlLength(0, false), new SafeHtmlLength(-1, false), 4_194_304);
        Assert.Equal(unspecified.GetCacheFamily("source", true), ignored.GetCacheFamily("source", true));
    }

    [Fact]
    public void CacheSerializationIsInvariantAndNormalizesEquivalentInvalidInputs()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var request = new RasterImageSizingRequest(100.5, 1.25,
                new SafeHtmlLength(-0f, false), null, 10000);
            string family = request.GetCacheFamily("source", true);
            string key = request.GetCacheKey(family, (125, 63));
            Assert.Contains("100.5@1.25", key);
            var equivalent = new RasterImageSizingRequest(100.5, 1.25,
                new SafeHtmlLength(0f, false), null, 10000);
            Assert.Equal(family, equivalent.GetCacheFamily("source", true));
            var invalid = new RasterImageSizingRequest(double.NaN, double.NaN,
                new SafeHtmlLength(float.NaN, false), null, 10000);
            var fallback = new RasterImageSizingRequest(1, 1, null, null, 10000);
            Assert.Equal(invalid.GetCacheKey(invalid.GetCacheFamily("source", true), (1, 1)),
                fallback.GetCacheKey(fallback.GetCacheFamily("source", true), (1, 1)));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
