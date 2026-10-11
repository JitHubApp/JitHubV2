using JitHub.WinUI.Automation;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ReadmeAuditVisualComparisonPolicyTests
{
    private const string ReadmeSha256 = "74e76291461d508843704572ea6cff7f799a58a4d43add9defc54b974059f262";

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    [InlineData(2.25)]
    [InlineData(3.0)]
    [InlineData(4.0)]
    public void FractionalDipCaptureSizeDefinesTheCanonicalEdgeViewport(double rasterScale)
    {
        ReadmeAuditVisualCapturedViewportSize[] captures = [new(161, 611), new(161, 611)];
        int physicalWidth = (int)Math.Round(160.75 * rasterScale);
        int physicalHeight = (int)Math.Round(610.5 * rasterScale);

        Assert.True(ReadmeAuditVisualComparisonPolicy.TryResolveCapturedViewportDipSize(
            physicalWidth,
            physicalHeight,
            rasterScale,
            captures,
            out int widthDip,
            out int heightDip));
        Assert.Equal(161, widthDip);
        Assert.Equal(611, heightDip);
    }

    [Fact]
    public void CapturedViewportMustStayStableAndMatchThePhysicalHost()
    {
        ReadmeAuditVisualCapturedViewportSize[] captures = [new(161, 611), new(161, 611)];
        Assert.False(ReadmeAuditVisualComparisonPolicy.TryResolveCapturedViewportDipSize(
            643, 2444, 4, [new(161, 611), new(162, 611)], out _, out _));
        Assert.False(ReadmeAuditVisualComparisonPolicy.TryResolveCapturedViewportDipSize(
            620, 2444, 4, captures, out _, out _));
        Assert.False(ReadmeAuditVisualComparisonPolicy.TryResolveCapturedViewportDipSize(
            643, 2444, 4, [new(161.25, 611)], out _, out _));
        Assert.False(ReadmeAuditVisualComparisonPolicy.TryResolveCapturedViewportDipSize(
            double.NaN, 2444, 4, captures, out _, out _));
        Assert.False(ReadmeAuditVisualComparisonPolicy.TryResolveCapturedViewportDipSize(
            643, 2444, double.NaN, captures, out _, out _));
        Assert.False(ReadmeAuditVisualComparisonPolicy.TryResolveCapturedViewportDipSize(
            643, 2444, 4, [], out _, out _));
    }

    [Theory]
    [InlineData(643, 2442, 643, 2442, true)]
    [InlineData(644, 2441, 643, 2442, true)]
    [InlineData(645, 2442, 643, 2442, false)]
    [InlineData(643, 2444, 643, 2442, false)]
    [InlineData(0, 2442, 643, 2442, false)]
    public void NativeCaptureMustMatchMeasuredPhysicalViewportWithinOnePixel(
        int capturedWidthPixels,
        int capturedHeightPixels,
        int measuredWidthPixels,
        int measuredHeightPixels,
        bool expected)
    {
        Assert.Equal(
            expected,
            ReadmeAuditVisualComparisonPolicy.IsCaptureWithinMeasuredHostViewport(
                capturedWidthPixels,
                capturedHeightPixels,
                measuredWidthPixels,
                measuredHeightPixels));
    }

    [Fact]
    public void ExactRendererDocumentTopsMustBeginAtStartAndCoverEveryViewportStep()
    {
        ReadmeAuditVisualCapturedViewport[] valid =
        [
            new(0, 611),
            new(564, 611),
            new(1_120, 611),
        ];
        Assert.True(ReadmeAuditVisualComparisonPolicy.HasMonotonicNativeViewportCoverage(valid));
        Assert.False(ReadmeAuditVisualComparisonPolicy.HasMonotonicNativeViewportCoverage([]));
        Assert.False(ReadmeAuditVisualComparisonPolicy.HasMonotonicNativeViewportCoverage(
            [new(0, 611), new(null, 611)]));
        Assert.False(ReadmeAuditVisualComparisonPolicy.HasMonotonicNativeViewportCoverage(
            [new(0.6, 611), new(564, 611)]));
        Assert.False(ReadmeAuditVisualComparisonPolicy.HasMonotonicNativeViewportCoverage(
            [new(0, 611), new(0, 611)]));
        Assert.False(ReadmeAuditVisualComparisonPolicy.HasMonotonicNativeViewportCoverage(
            [new(0, 611), new(612, 611)]));
        Assert.False(ReadmeAuditVisualComparisonPolicy.HasMonotonicNativeViewportCoverage(
            [new(0, 611), new(double.NaN, 611)]));
    }

    [Fact]
    public void AbsoluteViewportOffsetDoesNotDriftWhenLazyExtentChangesReportedPercent()
    {
        var beforeGrowth = new ReadmeAuditVisualViewportPosition(
            ScrollTopViewportUnits: 0.9,
            ScrollPercent: 2.99270938296485,
            VerticalViewSize: 3.21821936668196);
        var afterGrowth = new ReadmeAuditVisualViewportPosition(
            ScrollTopViewportUnits: 0.9,
            ScrollPercent: 1.128601,
            VerticalViewSize: 2.44072221013705);

        Assert.True(ReadmeAuditVisualComparisonPolicy.TryGetViewportPixelBounds(
            beforeGrowth,
            viewportHeightDip: 561,
            referencePixelsPerDip: 1,
            out int beforeGrowthTop,
            out int beforeGrowthHeight));
        Assert.True(ReadmeAuditVisualComparisonPolicy.TryGetViewportPixelBounds(
            afterGrowth,
            viewportHeightDip: 561,
            referencePixelsPerDip: 1,
            out int afterGrowthTop,
            out int afterGrowthHeight));

        Assert.Equal(505, beforeGrowthTop);
        Assert.Equal(beforeGrowthTop, afterGrowthTop);
        Assert.Equal(561, beforeGrowthHeight);
        Assert.Equal(beforeGrowthHeight, afterGrowthHeight);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(1.75)]
    [InlineData(2.0)]
    [InlineData(2.25)]
    [InlineData(3.0)]
    [InlineData(4.0)]
    public void SameByteTerminalViewportUsesTheActualValidatedEdgeClamp(double rasterScale)
    {
        // Native is 0.95% taller, so its last requested viewport starts at
        // 6.99. Edge actually stops at 6.91653 and still has a complete final
        // viewport within its 4,837-pixel source screenshot.
        const int viewportHeight = 611;
        const int edgeDocumentHeight = 4_837;
        double[] edgeCaptureOffsets = [0, 6.916530278232406];
        ReadmeAuditVisualTile[] edgePage =
        [
            new(0, 0, 660, edgeDocumentHeight, "same-byte-source-tile-0000.png"),
        ];

        Assert.True(ReadmeAuditVisualComparisonPolicy.TryGetReplayViewportPixelBounds(
            nativeViewportIndex: 1,
            nativeViewportCount: 2,
            edgeCaptureOffsets,
            nativeRendererDocumentTopDip: 4_272,
            edgeDocumentHeightDip: edgeDocumentHeight,
            terminalClampObserved: true,
            viewportHeight,
            viewportHeightPixels: (int)Math.Round(viewportHeight * rasterScale),
            referencePixelsPerDip: rasterScale,
            out int edgeTop,
            out int edgeHeight));
        Assert.Equal((int)Math.Round((edgeDocumentHeight - viewportHeight) * rasterScale), edgeTop);
        Assert.True(ReadmeAuditVisualComparisonPolicy.TryCreateTileSegments(
            edgePage,
            edgeTop,
            viewportWidthPixels: (int)Math.Round(660 * rasterScale),
            edgeHeight,
            pixelsPerMetadataUnit: rasterScale,
            out IReadOnlyList<ReadmeAuditVisualTileSegment> covered));
        Assert.Single(covered);
        Assert.Equal(edgeHeight, covered[0].Height);

        Assert.True(ReadmeAuditVisualComparisonPolicy.TryGetViewportPixelBounds(
            new ReadmeAuditVisualViewportPosition(6.99, 100, 12.51),
            viewportHeight,
            referencePixelsPerDip: rasterScale,
            out int nativeRequestedTop,
            out int nativeRequestedHeight));
        Assert.False(ReadmeAuditVisualComparisonPolicy.TryCreateTileSegments(
            edgePage,
            nativeRequestedTop,
            viewportWidthPixels: (int)Math.Round(660 * rasterScale),
            nativeRequestedHeight,
            pixelsPerMetadataUnit: rasterScale,
            out _));

        Assert.False(ReadmeAuditVisualComparisonPolicy.TryGetReplayViewportPixelBounds(
            nativeViewportIndex: 1,
            nativeViewportCount: 3,
            edgeCaptureOffsets,
            nativeRendererDocumentTopDip: 4_272,
            edgeDocumentHeightDip: edgeDocumentHeight,
            terminalClampObserved: true,
            viewportHeight,
            viewportHeightPixels: (int)Math.Round(viewportHeight * rasterScale),
            referencePixelsPerDip: rasterScale,
            out _,
            out _));
        Assert.False(ReadmeAuditVisualComparisonPolicy.TryGetReplayViewportPixelBounds(
            nativeViewportIndex: 1,
            nativeViewportCount: 2,
            edgeCaptureOffsets,
            nativeRendererDocumentTopDip: 4_272,
            edgeDocumentHeightDip: edgeDocumentHeight,
            terminalClampObserved: false,
            viewportHeight,
            viewportHeightPixels: (int)Math.Round(viewportHeight * rasterScale),
            referencePixelsPerDip: rasterScale,
            out _,
            out _));
    }

    [Fact]
    public void SameByteNonterminalViewportUsesExactRendererTopNotInferredUiaTop()
    {
        double[] edgeCaptureOffsets = [0, 0.9];
        Assert.True(ReadmeAuditVisualComparisonPolicy.TryGetReplayViewportPixelBounds(
            nativeViewportIndex: 1,
            nativeViewportCount: 2,
            edgeCaptureOffsets,
            nativeRendererDocumentTopDip: 564,
            edgeDocumentHeightDip: 4_837,
            terminalClampObserved: false,
            viewportHeightDip: 611,
            viewportHeightPixels: 611,
            referencePixelsPerDip: 1,
            out int top,
            out int height));
        Assert.Equal(564, top);
        Assert.Equal(611, height);
        Assert.NotEqual((int)Math.Round(edgeCaptureOffsets[1] * 611), top);

        Assert.False(ReadmeAuditVisualComparisonPolicy.TryGetReplayViewportPixelBounds(
            nativeViewportIndex: 1,
            nativeViewportCount: 2,
            edgeCaptureOffsets,
            nativeRendererDocumentTopDip: double.NaN,
            edgeDocumentHeightDip: 4_837,
            terminalClampObserved: false,
            viewportHeightDip: 611,
            viewportHeightPixels: 611,
            referencePixelsPerDip: 1,
            out _,
            out _));
    }

    [Fact]
    public void FourHundredPercentTerminalViewportUsesMeasuredPhysicalHeightAndCropsEdgeTile()
    {
        const int viewportHeightDip = 611;
        const int viewportHeightPixels = 2442; // 610.5 measured DIPs at 400%.
        const int viewportWidthPixels = 643;
        const int edgeDocumentHeightDip = 4_837;
        double[] edgeCaptureOffsets = [0, 6.916530278232406];

        Assert.True(ReadmeAuditVisualComparisonPolicy.TryGetReplayViewportPixelBounds(
            nativeViewportIndex: 1,
            nativeViewportCount: 2,
            edgeCaptureOffsets,
            nativeRendererDocumentTopDip: 4_272,
            edgeDocumentHeightDip,
            terminalClampObserved: true,
            viewportHeightDip,
            viewportHeightPixels,
            referencePixelsPerDip: 4,
            out int edgeTop,
            out int edgeHeight));
        Assert.Equal((int)Math.Round((edgeDocumentHeightDip - viewportHeightDip) * 4d), edgeTop);
        Assert.Equal(viewportHeightPixels, edgeHeight);

        ReadmeAuditVisualTile[] edgePage =
        [
            new(0, 0, 161, edgeDocumentHeightDip, "same-byte-source-tile-0000.png"),
        ];
        Assert.True(ReadmeAuditVisualComparisonPolicy.TryCreateTileSegments(
            edgePage,
            edgeTop,
            viewportWidthPixels,
            edgeHeight,
            pixelsPerMetadataUnit: 4,
            out IReadOnlyList<ReadmeAuditVisualTileSegment> cropped));
        ReadmeAuditVisualTileSegment segment = Assert.Single(cropped);
        Assert.Equal(edgeTop, segment.SourceY);
        Assert.Equal(0, segment.DestinationY);
        Assert.Equal(viewportHeightPixels, segment.Height);
    }

    [Fact]
    public void SameByteVisualComparisonRequiresIdenticalBytesViewportDpiAndTheme()
    {
        var native = new ReadmeAuditVisualViewportProfile(660, 561, 1, "light", 660, 660, 660);
        var replay = new ReadmeAuditVisualViewportProfile(660, 561, 1, "light", 660, 660, 660);

        Assert.True(ReadmeAuditVisualComparisonPolicy.HasMatchedSourceProfile(
            ReadmeSha256,
            ReadmeSha256.ToUpperInvariant(),
            ReadmeSha256,
            native,
            replay,
            out string matchedReason));
        Assert.Equal(string.Empty, matchedReason);

        Assert.False(ReadmeAuditVisualComparisonPolicy.HasMatchedSourceProfile(
            ReadmeSha256,
            ReadmeSha256,
            new string('0', 64),
            native,
            replay,
            out string sourceReason));
        Assert.Equal("source-hash-mismatch", sourceReason);

        Assert.False(ReadmeAuditVisualComparisonPolicy.HasMatchedSourceProfile(
            ReadmeSha256,
            ReadmeSha256,
            ReadmeSha256,
            native,
            replay with { WidthDip = 659 },
            out string viewportReason));
        Assert.Equal("viewport-size-mismatch", viewportReason);

        Assert.False(ReadmeAuditVisualComparisonPolicy.HasMatchedSourceProfile(
            ReadmeSha256,
            ReadmeSha256,
            ReadmeSha256,
            native,
            replay with { DocumentClientWidthDip = 645, BodyClientWidthDip = 645, ArticleWidthDip = 645 },
            out string contentReason));
        Assert.Equal("content-width-mismatch", contentReason);

        Assert.False(ReadmeAuditVisualComparisonPolicy.HasMatchedSourceProfile(
            ReadmeSha256,
            ReadmeSha256,
            ReadmeSha256,
            native,
            replay with { ArticleWidthDip = 645 },
            out contentReason));
        Assert.Equal("content-width-mismatch", contentReason);

        Assert.False(ReadmeAuditVisualComparisonPolicy.HasMatchedSourceProfile(
            ReadmeSha256,
            ReadmeSha256,
            ReadmeSha256,
            native,
            replay with { DeviceScaleFactor = 1.5 },
            out string scaleReason));
        Assert.Equal("device-scale-mismatch", scaleReason);

        Assert.False(ReadmeAuditVisualComparisonPolicy.HasMatchedSourceProfile(
            ReadmeSha256,
            ReadmeSha256,
            ReadmeSha256,
            native,
            replay with { ColorScheme = "dark" },
            out string schemeReason));
        Assert.Equal("color-scheme-mismatch", schemeReason);
    }

    [Fact]
    public void SameByteViewportCanBeComposedAcrossContiguousReferenceTiles()
    {
        ReadmeAuditVisualTile[] tiles =
        [
            new(0, 0, 660, 8192, "tile-0000.png"),
            new(1, 8192, 660, 8192, "tile-0001.png"),
            new(2, 13977, 660, 8192, "tile-0002.png"),
        ];

        Assert.True(ReadmeAuditVisualComparisonPolicy.TryCreateTileSegments(
            tiles,
            viewportTopPixel: 8078,
            viewportWidthPixels: 660,
            viewportHeightPixels: 561,
            pixelsPerMetadataUnit: 1,
            out IReadOnlyList<ReadmeAuditVisualTileSegment> segments));

        Assert.Collection(
            segments,
            first =>
            {
                Assert.Equal(0, first.TileIndex);
                Assert.Equal(8078, first.SourceY);
                Assert.Equal(0, first.DestinationY);
                Assert.Equal(114, first.Height);
            },
            second =>
            {
                Assert.Equal(1, second.TileIndex);
                Assert.Equal(0, second.SourceY);
                Assert.Equal(114, second.DestinationY);
                Assert.Equal(447, second.Height);
            });
    }

    [Fact]
    public void SameByteViewportCoverageFailsClosedOnGapsAndWrongTileWidth()
    {
        ReadmeAuditVisualTile[] gap =
        [
            new(0, 0, 660, 400, "tile-0000.png"),
            new(1, 450, 660, 400, "tile-0001.png"),
        ];
        ReadmeAuditVisualTile[] wrongWidth =
        [
            new(0, 0, 659, 561, "tile-0000.png"),
        ];

        Assert.False(ReadmeAuditVisualComparisonPolicy.TryCreateTileSegments(
            gap,
            viewportTopPixel: 0,
            viewportWidthPixels: 660,
            viewportHeightPixels: 561,
            pixelsPerMetadataUnit: 1,
            out _));
        Assert.False(ReadmeAuditVisualComparisonPolicy.TryCreateTileSegments(
            wrongWidth,
            viewportTopPixel: 0,
            viewportWidthPixels: 660,
            viewportHeightPixels: 561,
            pixelsPerMetadataUnit: 1,
            out _));
    }

    [Fact]
    public void SameByteViewportTileGeometryScalesFromRecordedMetadataToScreenshotPixels()
    {
        ReadmeAuditVisualTile[] tiles =
        [
            new(0, 0, 660, 8192, "tile-0000.png"),
            new(1, 8192, 660, 8192, "tile-0001.png"),
        ];

        Assert.True(ReadmeAuditVisualComparisonPolicy.TryCreateTileSegments(
            tiles,
            viewportTopPixel: 12_326,
            viewportWidthPixels: 990,
            viewportHeightPixels: 842,
            pixelsPerMetadataUnit: 1.5,
            out IReadOnlyList<ReadmeAuditVisualTileSegment> segments));

        Assert.Single(segments);
        Assert.Equal(1, segments[0].TileIndex);
        Assert.Equal(38, segments[0].SourceY);
        Assert.Equal(0, segments[0].DestinationY);
        Assert.Equal(842, segments[0].Height);
    }
}
