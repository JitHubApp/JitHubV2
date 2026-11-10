using System.Numerics;
using NumericMath = global::System.Math;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Text;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using WinRT;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ScriptBaselineFixture;
using static MarkdownRenderer.GitHub.Tests.ScriptRasterAssertions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks the layout geometry of superscript and subscript ranges: sorted
/// half-open ranges, wrapped and nested HTML scripts, and measured block
/// bounds, compared with independent native regions.
/// </summary>
public sealed class ScriptBaselineGeometryTests
{
    [Fact]
    public void GeometryUsesSortedHalfOpenRangesAndLeavesGapsAtTheBaseLine()
    {
        var builder = new ScriptBaselineGeometry.Builder();
        builder.Add(3, 4, -4.8f);
        builder.Add(11, 2, 3.2f);
        ScriptBaselineGeometry geometry = builder.Build();

        Assert.Equal(2, geometry.Count);
        Assert.Equal(-4.8f, geometry.GetOffsetAtCharacter(3), precision: 3);
        Assert.Equal(-4.8f, geometry.GetOffsetAtCharacter(6), precision: 3);
        Assert.Equal(0f, geometry.GetOffsetAtCharacter(7));
        Assert.Equal(3.2f, geometry.GetOffsetAtCharacter(11), precision: 3);
        Assert.Equal(3.2f, geometry.GetOffsetAtCharacter(12), precision: 3);
        Assert.Equal(0f, geometry.GetOffsetAtCharacter(13));

        Assert.Equal(7, geometry.GetSegmentEnd(4, 13, out float supOffset));
        Assert.Equal(-4.8f, supOffset, precision: 3);
        Assert.Equal(11, geometry.GetSegmentEnd(8, 13, out float plainOffset));
        Assert.Equal(0f, plainOffset);
        Assert.Equal(13, geometry.GetSegmentEnd(12, 13, out float subOffset));
        Assert.Equal(3.2f, subOffset, precision: 3);
        Assert.Equal(4.8f, geometry.TopInset, precision: 3);
        Assert.Equal(3.2f, geometry.BottomInset, precision: 3);
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public void WrappedHtmlScriptGeometryMatchesIndependentNativeRegionsAndWarpInk(
        FlowDirection flowDirection)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        AssertWarpColorGlyphSupport(device);
        const string source =
            "<sup>ordinary script phrase that wraps across several lines</sup> " +
            "*italic* e\u0301 العربية 😀 <sub>subscript phrase also wraps across lines</sub>";
        using LayoutSnapshot snapshot = BuildSnapshot(device, source, flowDirection, AvailableWidth);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        InlineRun superscript = Assert.Single(
            box.Runs,
            run => run.ElementKey == MarkdownElementKeys.Superscript);
        InlineRun subscript = Assert.Single(
            box.Runs,
            run => run.ElementKey == MarkdownElementKeys.Subscript);
        InlineRun italic = Assert.Single(box.Runs, run => run is EmphasisRun);

        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(box.TextLayout);
        int superscriptStart = GetBufferStart(box, superscript);
        int subscriptStart = GetBufferStart(box, subscript);
        Assert.True(layout.GetCharacterRegions(superscriptStart, superscript.TextLength)!.Length > 1);
        Assert.True(layout.GetCharacterRegions(subscriptStart, subscript.TextLength)!.Length > 1);

        AssertRangeGeometry(box, layout, superscript, expectedOffset: -BodyFontSize * 0.30f,
            expectedTopInset: BodyFontSize * 0.30f);
        AssertRangeGeometry(box, layout, subscript, expectedOffset: BodyFontSize * 0.20f,
            expectedTopInset: BodyFontSize * 0.30f);

        CanvasTextLayoutRegion firstScriptRegion =
            layout.GetCharacterRegions(superscriptStart, superscript.TextLength)![0];
        Rect expectedSupRect = ExpectedRegionRect(
            box,
            firstScriptRegion,
            expectedOffset: -BodyFontSize * 0.30f,
            expectedTopInset: BodyFontSize * 0.30f);
        Point scriptPoint = new(expectedSupRect.X + NumericMath.Max(1, expectedSupRect.Width / 2),
            expectedSupRect.Y + NumericMath.Max(1, expectedSupRect.Height / 2));
        Assert.True(box.HitTest(scriptPoint, out DocumentPosition hitPosition));
        Assert.Equal(superscript.InlineIndex, hitPosition.InlineIndex);

        AssertScriptEndCaretKeepsSubscriptOffset(box, layout, subscript, subscriptStart);

        byte[] pixels = Render(box, device, AvailableWidth);
        AssertInkInsideBox(pixels, (int)AvailableWidth, box.Bounds);
        AssertInkInRegion(pixels, (int)AvailableWidth,
            ExpectedRunBounds(box, layout, italic, expectedTopInset: BodyFontSize * 0.30f));
        AssertInkInRegion(pixels, (int)AvailableWidth, expectedSupRect);
        Rect expectedSubscriptBounds = ExpectedRunBounds(
            box,
            layout,
            subscript,
            expectedTopInset: BodyFontSize * 0.30f);
        AssertInkInRegion(
            pixels,
            (int)AvailableWidth,
            Offset(expectedSubscriptBounds, BodyFontSize * 0.20f));

        AssertEmojiNeighborMatchesNativeLayout(box, layout, device, pixels);

        var selection = RangeFor(box, superscript);
        byte[] selectedPixels = RenderSelection(box, device, AvailableWidth, selection);
        AssertInkInRegion(selectedPixels, (int)AvailableWidth, expectedSupRect);
    }

    /// <summary>Asserts that the caret after a wrapped subscript sits on the subscript-shifted line bottom.</summary>
    private static void AssertScriptEndCaretKeepsSubscriptOffset(
        InlineContainerBox box,
        CanvasTextLayout layout,
        InlineRun subscript,
        int subscriptStart)
    {
        var scriptEnd = new DocumentPosition(
            box.BlockIndex,
            subscript.InlineIndex,
            subscript.TextLength);
        Assert.True(box.TryGetCaretPoint(scriptEnd, rangeStart: false, out Point caret));
        int subscriptEndCharacter = subscriptStart + subscript.TextLength - 1;
        MarkdownRenderer.Controls.TouchSelectionCaretQuery query = MarkdownRenderer.Controls.TouchSelectionCaretPolicy.Resolve(
            GetBufferLength(box),
            subscriptEndCharacter + 1,
            rangeStart: false);
        _ = layout.GetCaretPosition(
            query.CharacterIndex,
            query.TrailingSideOfCharacter,
            out CanvasTextLayoutRegion rawCaretRegion);
        double expectedCaretY = ExpectedTextOriginY(box, BodyFontSize * 0.30f) +
            rawCaretRegion.LayoutBounds.Bottom + BodyFontSize * 0.20f;
        Assert.InRange(NumericMath.Abs(caret.Y - expectedCaretY), 0, 0.02);
    }

    /// <summary>Asserts that the unshifted emoji neighbor paints exactly as the native layout renderer paints it.</summary>
    private static void AssertEmojiNeighborMatchesNativeLayout(
        InlineContainerBox box,
        CanvasTextLayout layout,
        CanvasDevice device,
        byte[] pixels)
    {
        // The ordinary emoji neighbor has zero script offset. Compare its
        // pixels with the native layout renderer so a custom callback brush
        // cannot silently replace a color-font palette with the text color.
        string shapedText = string.Concat(box.Runs.Select(run => run.Text));
        int emojiIndex = shapedText.IndexOf("😀", StringComparison.Ordinal);
        Assert.True(emojiIndex >= 0, "The fixture did not preserve its emoji neighbor in the shaped text.");
        CanvasTextLayoutRegion emojiRegion = layout.GetCharacterRegions(emojiIndex, 2)![0];
        Rect expectedEmojiRect = ExpectedRegionRect(
            box,
            emojiRegion,
            expectedOffset: 0,
            expectedTopInset: BodyFontSize * 0.30f);
        byte[] nativeNeighborPixels = RenderNativeLayout(
            layout,
            device,
            AvailableWidth,
            pixels.Length / ((int)AvailableWidth * 4),
            ExpectedTextOriginY(box, BodyFontSize * 0.30f),
            Color.FromArgb(255, 12, 18, 24));
        AssertPixelsMatchWithinRegion(
            pixels,
            nativeNeighborPixels,
            (int)AvailableWidth,
            expectedEmojiRect);
    }

    [Theory]
    [InlineData("sup", MarkdownElementKeys.Superscript, -0.30f)]
    [InlineData("sub", MarkdownElementKeys.Subscript, 0.20f)]
    public void ScriptOnlyWrappedLinesStayWithinMeasuredBlockBounds(
        string tag,
        string role,
        float offsetFactor)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        string source = $"<{tag}>a script-only phrase long enough to wrap over several lines</{tag}>";
        using LayoutSnapshot snapshot = BuildSnapshot(
            device,
            source,
            FlowDirection.LeftToRight,
            AvailableWidth);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        InlineRun script = Assert.Single(box.Runs, run => run.ElementKey == role);
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(box.TextLayout);
        int start = GetBufferStart(box, script);
        CanvasTextLayoutRegion[] nativeRegions = layout.GetCharacterRegions(start, script.TextLength)!;
        Assert.True(nativeRegions.Length > 1);

        float offset = BodyFontSize * offsetFactor;
        float topInset = NumericMath.Max(0, -offset);
        float bottomInset = NumericMath.Max(0, offset);
        AssertRangeGeometry(box, layout, script, offset, topInset);
        Assert.InRange(
            NumericMath.Abs(box.Bounds.Height - (layout.LayoutBounds.Height + topInset + bottomInset)),
            0,
            0.02);

        byte[] pixels = Render(box, device, AvailableWidth);
        AssertInkInsideBox(pixels, (int)AvailableWidth, box.Bounds);
        foreach (CanvasTextLayoutRegion nativeRegion in nativeRegions)
            AssertInkInRegion(
                pixels,
                (int)AvailableWidth,
                ExpectedRegionRect(box, nativeRegion, offset, topInset));

        // Independent raster oracle: capture DirectWrite's exact shaped glyph
        // callbacks at the native layout origin, then manually translate each
        // captured baseline by the known fixed script offset. This avoids
        // differences in line-box placement and pixel snapping between the
        // renderer callback path and DrawTextLayout while still checking the
        // actual painted glyph pixels, not just the geometry API.
        float textOriginY = (float)ExpectedTextOriginY(box, topInset);
        byte[] expectedPixels = RenderCapturedGlyphRuns(
            layout,
            device,
            AvailableWidth,
            pixels.Length / ((int)AvailableWidth * 4),
            textOriginY,
            offset,
            Color.FromArgb(255, 12, 18, 24));
        AssertRasterRowsMatch(pixels, expectedPixels, (int)AvailableWidth);
    }

    [Fact]
    public void NestedHtmlScriptsCompoundOffsetsWithoutChangingLogicalText()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        const string source = "<sup>outer <sub>nested script phrase</sub> tail</sup> normal";
        using LayoutSnapshot snapshot = BuildSnapshot(
            device,
            source,
            FlowDirection.LeftToRight,
            AvailableWidth);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        InlineRun nested = Assert.Single(box.Runs, run => run.Text == "nested script phrase");
        Assert.Equal(
            [MarkdownElementKeys.Superscript, MarkdownElementKeys.Subscript],
            nested.ScriptStyleAncestryKeys);
        Assert.Contains("nested script phrase", string.Concat(box.Runs.Select(run => run.Text)));

        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(box.TextLayout);
        AssertRangeGeometry(
            box,
            layout,
            nested,
            expectedOffset: -BodyFontSize * 0.30f + BodyFontSize * 0.20f,
            expectedTopInset: BodyFontSize * 0.30f);
        byte[] pixels = Render(box, device, AvailableWidth);
        AssertInkInRegion(
            pixels,
            (int)AvailableWidth,
            Offset(
                ExpectedRunBounds(box, layout, nested, BodyFontSize * 0.30f),
                -BodyFontSize * 0.10f));
    }

    private static int GetBufferLength(InlineContainerBox box)
        => box.Runs.Sum(run => run.TextLength);

    private static byte[] RenderCapturedGlyphRuns(
        CanvasTextLayout layout,
        CanvasDevice device,
        float width,
        int height,
        float originY,
        float offsetY,
        Color foreground)
    {
        var recorder = new RasterGlyphRunRecorder();
        layout.DrawToTextRenderer(recorder, 0f, originY);
        using var fallback = new CanvasSolidColorBrush(device, foreground);
        using var target = new CanvasRenderTarget(device, width, height, 96f);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            foreach (CapturedGlyphRun run in recorder.Runs)
            {
                Vector2 shiftedPoint = run.Point;
                shiftedPoint.Y += offsetY;
                shiftedPoint = SnapExpectedBaselineLineNormal(shiftedPoint, drawing.Transform, drawing.Dpi);
                drawing.DrawGlyphRun(
                    shiftedPoint,
                    run.FontFace,
                    run.FontSize,
                    run.Glyphs,
                    run.IsSideways,
                    run.BidiLevel,
                    run.Brush as ICanvasBrush ?? fallback,
                    run.MeasuringMode,
                    run.LocaleName,
                    run.Text,
                    run.ClusterMap,
                    run.CharacterIndex);
            }
        }

        return target.GetPixelBytes();
    }

    private static void AssertInkInsideBox(byte[] pixels, int width, Rect bounds)
    {
        int minY = int.MaxValue;
        int maxY = -1;
        for (int y = 0; y < pixels.Length / (width * 4); y++)
        {
            for (int x = 0; x < width; x++)
            {
                int alpha = pixels[(y * width + x) * 4 + 3];
                if (alpha == 0)
                    continue;
                minY = NumericMath.Min(minY, y);
                maxY = NumericMath.Max(maxY, y);
            }
        }

        Assert.True(maxY >= minY, "The expected WARP raster did not contain any text ink.");
        Assert.True(minY >= NumericMath.Floor(bounds.Top), $"Top ink pixel {minY} escaped box top {bounds.Top:R}.");
        Assert.True(maxY < NumericMath.Ceiling(bounds.Bottom), $"Bottom ink pixel {maxY} escaped box bottom {bounds.Bottom:R}.");
    }
}
