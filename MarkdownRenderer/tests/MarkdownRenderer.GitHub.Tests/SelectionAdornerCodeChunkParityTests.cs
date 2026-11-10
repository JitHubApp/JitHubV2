using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.RenderResources;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.SelectionAdornerViewportFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Compares the selection rectangles of clipped and horizontally scrolled
/// code chunks with DirectWrite in left-to-right, right-to-left and
/// wrapped code.
/// </summary>
public sealed class SelectionAdornerCodeChunkParityTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    [Fact]
    public void CodeBlockClipPreservesUntouchedAxisAndReconstructsClippedAxis()
    {
        var source = new Rect(62, 114, 142.94922, 18.199999);

        Rect horizontal = CodeBlockBox.Intersect(source, new Rect(0, 100, 176, 100));

        Assert.Equal(source.Y, horizontal.Y);
        Assert.Equal(
            BitConverter.DoubleToInt64Bits(source.Height),
            BitConverter.DoubleToInt64Bits(horizontal.Height));
        Assert.Equal(new Rect(62, 114, 114, source.Height), horizontal);

        Rect vertical = CodeBlockBox.Intersect(source, new Rect(0, 120, 256, 20));

        Assert.Equal(120, vertical.Y);
        Assert.Equal(source.Bottom - vertical.Top, vertical.Height);
        Assert.True(vertical.Height < source.Height);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public async Task RtlUnequalWidthCodeChunksMatchDirectWriteAtZeroMiddleAndMaximumScroll(
        float rasterizationScale)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        await using RtlNativeCodeFixture fixture = RtlNativeCodeFixture.Create(
            device,
            _renderResources,
            rasterizationScale,
            CanvasWordWrapping.NoWrap,
            includeUnbreakableToken: true);

        Assert.True(fixture.Code.CanScrollHorizontally);
        Assert.True(fixture.Code.IsRightToLeft);
        Assert.True(fixture.Code.Chunks.Select(chunk => chunk.CodeBlockTextLength).Distinct().Count() > 1);
        Assert.True(fixture.Code.Chunks.Select(chunk => chunk.ContentWidth).Distinct().Count() > 1);

        double maximum = fixture.Code.HorizontalExtent - fixture.Code.HorizontalViewport;
        Assert.True(maximum > 0.5);
        foreach (double offset in new[] { 0, maximum / 2, maximum })
        {
            fixture.Code.SetHorizontalOffset(offset);
            Assert.Equal(offset, fixture.Code.HorizontalOffset);
            AssertCodeChunksMatchDirectWrite(fixture, rasterizationScale);
        }
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(1.5f)]
    [InlineData(2f)]
    public async Task LtrUnequalWidthCodeChunksMatchDirectWriteAtZeroMiddleAndMaximumScroll(
        float rasterizationScale)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        await using RtlNativeCodeFixture fixture = RtlNativeCodeFixture.Create(
            device,
            _renderResources,
            rasterizationScale,
            CanvasWordWrapping.NoWrap,
            includeUnbreakableToken: true,
            flowDirection: FlowDirection.LeftToRight);

        Assert.True(fixture.Code.CanScrollHorizontally);
        Assert.False(fixture.Code.IsRightToLeft);
        Assert.True(fixture.Code.Chunks.Select(chunk => chunk.CodeBlockTextLength).Distinct().Count() > 1);
        Assert.True(fixture.Code.Chunks.Select(chunk => chunk.ContentWidth).Distinct().Count() > 1);

        double maximum = fixture.Code.HorizontalExtent - fixture.Code.HorizontalViewport;
        Assert.True(maximum > 0.5);
        foreach (double offset in new[] { 0, maximum / 2, maximum })
        {
            fixture.Code.SetHorizontalOffset(offset);
            Assert.Equal(offset, fixture.Code.HorizontalOffset);
            AssertCodeChunksMatchDirectWrite(fixture, rasterizationScale);
        }
    }

    [Theory]
    [InlineData(true, 1f)]
    [InlineData(true, 1.5f)]
    [InlineData(true, 2f)]
    [InlineData(false, 1f)]
    [InlineData(false, 1.5f)]
    [InlineData(false, 2f)]
    public async Task RtlWrappedCodeWithLongTokenAndOrdinaryLinesMatchesDirectWriteViewport(
        bool includeUnbreakableToken,
        float rasterizationScale)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        Assert.True(device.ForceSoftwareRenderer);
        await using RtlNativeCodeFixture fixture = RtlNativeCodeFixture.Create(
            device,
            _renderResources,
            rasterizationScale,
            CanvasWordWrapping.Wrap,
            includeUnbreakableToken);

        Assert.All(fixture.Code.Chunks, chunk => Assert.Equal(CanvasWordWrapping.Wrap, chunk.WordWrapping));
        if (!includeUnbreakableToken)
            Assert.False(fixture.Code.CanScrollHorizontally);

        double maximum = global::System.Math.Max(
            0,
            fixture.Code.HorizontalExtent - fixture.Code.HorizontalViewport);
        double[] offsets = maximum > 0.5
            ? [0, maximum / 2, maximum]
            : [0];
        foreach (double offset in offsets)
        {
            fixture.Code.SetHorizontalOffset(offset);
            Assert.Equal(offset, fixture.Code.HorizontalOffset);
            AssertCodeChunksMatchDirectWrite(fixture, rasterizationScale);
        }
    }

    private static void AssertCodeChunksMatchDirectWrite(
        RtlNativeCodeFixture fixture,
        float rasterizationScale)
    {
        CodeBlockBox code = fixture.Code;
        Rect viewport = code.HorizontalViewportBounds;
        AssertChunksStartAtTheScrolledViewportEdge(code, viewport, rasterizationScale);

        Rect[] directWriteOracle = GetDirectWriteCodeSelectionRects(fixture, rasterizationScale);
        Rect[] actualCodeRects = code.GetSelectionRects(fixture.Selection.Range).ToArray();
        Assert.NotEmpty(directWriteOracle);
        Assert.True(
            directWriteOracle.SequenceEqual(actualCodeRects),
            DescribeFirstCodeSelectionMismatch(fixture, rasterizationScale, directWriteOracle, actualCodeRects));

        List<Rect> legacyViewportRects = GetLegacyViewportRects(
            fixture.Selection,
            fixture.Snapshot,
            viewport);
        List<Rect> visibleViewportRects = CollectVisible(
            fixture.Selection,
            fixture.Snapshot,
            viewport);
        Assert.NotEmpty(legacyViewportRects);
        Assert.Equal(legacyViewportRects, visibleViewportRects);
        Assert.Equal(directWriteOracle, visibleViewportRects);

        Color[] legacyPixels = RasterizeSelectionRects(
            fixture.Device,
            legacyViewportRects,
            viewport,
            rasterizationScale,
            fixture.Snapshot,
            fixture.Selection.Range);
        Color[] viewportPixels = RasterizeSelectionRects(
            fixture.Device,
            visibleViewportRects,
            viewport,
            rasterizationScale,
            fixture.Snapshot,
            fixture.Selection.Range);
        Assert.Equal(legacyPixels, viewportPixels);

        Color[] foregroundOnly = RasterizeSelectionForegroundOnly(
            fixture.Device,
            fixture.Snapshot,
            fixture.Selection.Range,
            viewport,
            rasterizationScale);
        Assert.Contains(foregroundOnly, pixel => pixel.A != 0);

        AssertDirectWriteRectanglesHitTestToFiniteCarets(fixture, directWriteOracle);
    }

    private static void AssertChunksStartAtTheScrolledViewportEdge(
        CodeBlockBox code,
        Rect viewport,
        float rasterizationScale)
    {
        float horizontalDirection = code.IsRightToLeft ? 1f : -1f;
        float expectedChunkX = (float)viewport.Left + horizontalDirection * (float)code.HorizontalOffset;
        Assert.All(code.Chunks, chunk =>
        {
            Assert.InRange(global::System.Math.Abs((float)chunk.Bounds.X - expectedChunkX), 0, 0.01f);
            Assert.Equal(default(Thickness), chunk.Margin);

            // The ordinary inline hit-test remains constrained by its own X
            // bounds; CodeBlockBox alone may route clipped overflow hits.
            var outsideInlineBounds = new Point(
                chunk.Bounds.Right + global::System.Math.Max(1, 1 / rasterizationScale),
                chunk.Bounds.Top + chunk.Bounds.Height / 2);
            Assert.False(chunk.HitTest(outsideInlineBounds, out _));
        });
    }

    private static void AssertDirectWriteRectanglesHitTestToFiniteCarets(
        RtlNativeCodeFixture fixture,
        Rect[] directWriteOracle)
    {
        CodeBlockBox code = fixture.Code;
        Assert.True(fixture.Snapshot.TryBeginInteraction());
        try
        {
            foreach (Rect rect in directWriteOracle)
            {
                Point point = new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
                Assert.True(code.HitTest(point, out DocumentPosition hit));
                InlineContainerBox chunk = Assert.Single(code.Chunks, item => item.BlockIndex == hit.BlockIndex);
                Assert.InRange(hit.CharacterOffset, 0, chunk.CodeBlockTextLength);
                Assert.True(chunk.TryGetCaretPoint(hit, rangeStart: false, out Point caret));
                Assert.True(double.IsFinite(caret.X) && double.IsFinite(caret.Y));
                Assert.InRange(global::System.Math.Abs(caret.Y - point.Y), 0, global::System.Math.Max(rect.Height, 16));
            }
        }
        finally
        {
            fixture.Snapshot.EndInteraction();
        }
    }

    private static Rect[] GetDirectWriteCodeSelectionRects(
        RtlNativeCodeFixture fixture,
        float rasterizationScale)
    {
        Rect viewport = fixture.Code.HorizontalViewportBounds;
        float scale = (float)fixture.Code.Context.RasterizationScale;
        if (scale <= 0)
            scale = 1;

        var result = new List<Rect>();
        foreach (InlineContainerBox chunk in fixture.Code.Chunks)
        {
            CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(chunk.TextLayout);
            CanvasTextLayoutRegion[] regions = layout.GetCharacterRegions(0, chunk.CodeBlockTextLength);
            float originX = SnapIndependentTextOrigin((float)(chunk.Bounds.X + chunk.Margin.Left), scale);
            float originY = SnapIndependentTextOrigin((float)(chunk.Bounds.Y + chunk.Margin.Top), scale);
            foreach (CanvasTextLayoutRegion region in regions)
            {
                Rect native = new(
                    originX + region.LayoutBounds.X,
                    originY + region.LayoutBounds.Y,
                    region.LayoutBounds.Width,
                    region.LayoutBounds.Height);
                Rect clipped = IntersectForTest(native, viewport);
                if (clipped.Width > 0 && clipped.Height > 0)
                    result.Add(clipped);
            }
        }

        Assert.Equal(rasterizationScale, (float)fixture.Code.Context.RasterizationScale);
        return result.ToArray();
    }

    private static string DescribeFirstCodeSelectionMismatch(
        RtlNativeCodeFixture fixture,
        float rasterizationScale,
        IReadOnlyList<Rect> expected,
        IReadOnlyList<Rect> actual)
    {
        int count = System.Math.Min(expected.Count, actual.Count);
        int mismatch = 0;
        while (mismatch < count && expected[mismatch].Equals(actual[mismatch]))
            mismatch++;
        if (mismatch == count)
            return $"Selection rectangle count differs: expected={expected.Count}; actual={actual.Count}.";

        Rect expectedRect = expected[mismatch];
        Rect actualRect = actual[mismatch];
        string chunks = string.Join(
            " || ",
            fixture.Code.Chunks.Select(chunk =>
            {
                CanvasTextLayout layout = chunk.TextLayout!;
                float oracleOriginY = SnapIndependentTextOrigin(
                    (float)(chunk.Bounds.Y + chunk.Margin.Top),
                    (float)fixture.Code.Context.RasterizationScale);
                int from = GetDiagnosticBufferOffset(fixture.Selection.Range.Start, chunk);
                int to = GetDiagnosticBufferOffset(fixture.Selection.Range.End, chunk);
                CanvasTextLayoutRegion[] regions = layout.GetCharacterRegions(0, chunk.CodeBlockTextLength);
                CanvasTextLayoutRegion[] selectedRegions = layout.GetCharacterRegions(from, to - from);
                return $"block={chunk.BlockIndex}; bounds={chunk.Bounds}; oracleOriginY={oracleOriginY:R}; " +
                    $"query={from}+{to - from}; regionRects=[{string.Join(";", regions.Select(region => FormatRect(region.LayoutBounds)))}]; " +
                    $"selectedRects=[{string.Join(";", selectedRegions.Select(region => FormatRect(region.LayoutBounds)))}]; " +
                    $"actualRaw=[{string.Join(";", chunk.GetSelectionRects(fixture.Selection.Range).Select(FormatRect))}]";
            }));
        return $"DirectWrite selection rectangles differ at {mismatch}/{count}; " +
            $"expected={expectedRect}; actual={actualRect}; " +
            $"expectedBottom={expectedRect.Bottom:R}; actualBottom={actualRect.Bottom:R}; " +
            $"scale={rasterizationScale:R}; clip={fixture.Code.HorizontalViewportBounds}; chunks={chunks}";
    }

    private static int GetDiagnosticBufferOffset(DocumentPosition position, InlineContainerBox chunk)
    {
        if (position.BlockIndex < chunk.BlockIndex)
            return 0;
        if (position.BlockIndex > chunk.BlockIndex)
            return chunk.CodeBlockTextLength;
        return System.Math.Clamp(position.CharacterOffset, 0, chunk.CodeBlockTextLength);
    }

    private static string FormatRect(Rect rect)
        => $"{rect.X:R},{rect.Y:R},{rect.Width:R},{rect.Height:R}";

    private static float SnapIndependentTextOrigin(float value, float scale)
        => global::System.MathF.Round(value * scale) / scale;

    private static Color[] RasterizeSelectionForegroundOnly(
        CanvasDevice device,
        LayoutSnapshot snapshot,
        DocumentRange range,
        Rect viewport,
        float rasterizationScale)
    {
        if (!snapshot.TryBeginPaint())
            throw new InvalidOperationException("The native code-selection paint snapshot was unexpectedly busy.");
        try
        {
            using var target = new CanvasRenderTarget(
                device,
                (float)viewport.Width,
                (float)viewport.Height,
                96 * rasterizationScale);
            using (CanvasDrawingSession drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Color.FromArgb(0, 0, 0, 0));
                drawing.Transform = System.Numerics.Matrix3x2.CreateTranslation(
                    (float)-viewport.X,
                    (float)-viewport.Y);
                snapshot.PaintSelectionForeground(
                    drawing,
                    range,
                    Color.FromArgb(255, 225, 238, 251),
                    viewport);
            }

            return target.GetPixelColors();
        }
        finally
        {
            snapshot.EndPaint();
        }
    }
}
