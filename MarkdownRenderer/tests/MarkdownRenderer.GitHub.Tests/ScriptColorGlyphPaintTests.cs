using System.Numerics;
using NumericMath = global::System.Math;
using MarkdownRenderer.GitHub.Tests.TestSupport.Assertions;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
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
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ScriptBaselineFixture;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Assertions.ScriptRasterAssertions;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks that color font glyphs inside scripts keep their palette at the
/// script offset, including the selection overlay and the Context4
/// fallback path.
/// </summary>
public sealed class ScriptColorGlyphPaintTests
{
    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public void ColorFontInsideScriptRetainsPaletteAtUniformBaselineOffset(
        FlowDirection flowDirection)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        AssertWarpColorGlyphSupport(device);
        const string source = "<sup>normal 😀 phrase</sup> *italic* e\u0301 العربية";
        using LayoutSnapshot snapshot = BuildSnapshot(device, source, flowDirection, AvailableWidth);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        InlineRun script = Assert.Single(
            box.Runs,
            run => run.ElementKey == MarkdownElementKeys.Superscript);
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(box.TextLayout);
        float topInset = BodyFontSize * 0.30f;
        int sourceEmojiIndex = string.Concat(box.Runs.Select(run => run.Text))
            .IndexOf("😀", StringComparison.Ordinal);
        Assert.True(sourceEmojiIndex >= 0, "The fixture did not preserve its emoji in the shaped text.");

        float scriptOffset = -BodyFontSize * 0.30f;
        byte[] actualPixels = Render(box, device, AvailableWidth);
        byte[] expectedPalettePixels = RenderNativeLayout(
            layout,
            device,
            AvailableWidth,
            actualPixels.Length / ((int)AvailableWidth * 4),
            ExpectedTextOriginY(box, topInset) + scriptOffset,
            Color.FromArgb(255, 12, 18, 24));

        // Compare all of the script run, including ordinary monochrome text
        // and the palette-colored emoji, against the original shaped layout
        // translated by the fixed baseline offset. This exercises both glyph
        // classes without fragment reshaping or clipping.
        Rect compareRegion = Offset(
            ExpectedRunBounds(box, layout, script, topInset),
            scriptOffset);
        AssertInkInRegion(expectedPalettePixels, (int)AvailableWidth, compareRegion);
        AssertPixelsMatchWithinRegion(actualPixels, expectedPalettePixels, (int)AvailableWidth, compareRegion);

        CanvasTextLayoutRegion sourceEmojiRegion = layout.GetCharacterRegions(sourceEmojiIndex, 2)![0];
        Rect emojiRegion = ExpectedRegionRect(box, sourceEmojiRegion, scriptOffset, topInset);
        AssertPixelsMatchWithinRegion(actualPixels, expectedPalettePixels, (int)AvailableWidth, emojiRegion);
    }

    [Fact]
    public void SelectionOverlayPreservesSelectedColorGlyphAndOmitsUnselectedColorGlyph()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        AssertWarpColorGlyphSupport(device);
        const string source = "<sup>😀</sup> normal";
        using LayoutSnapshot snapshot = BuildSnapshot(device, source, FlowDirection.LeftToRight, AvailableWidth);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        InlineRun script = Assert.Single(box.Runs, run => run.ElementKey == MarkdownElementKeys.Superscript);
        InlineRun normal = Assert.Single(box.Runs, run => run.Text.Contains("normal", StringComparison.Ordinal));
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(box.TextLayout);
        int emojiIndex = GetBufferStart(box, script);
        float topInset = BodyFontSize * 0.30f;
        Rect emojiRegion = ExpectedRegionRect(
            box,
            layout.GetCharacterRegions(emojiIndex, script.TextLength)![0],
            expectedOffset: -BodyFontSize * 0.30f,
            expectedTopInset: topInset);

        byte[] normalPixels = Render(box, device, AvailableWidth);
        byte[] selectedScriptPixels = RenderSelection(box, device, AvailableWidth, RangeFor(box, script));
        AssertPixelsMatchWithinRegion(
            selectedScriptPixels,
            normalPixels,
            (int)AvailableWidth,
            emojiRegion);

        byte[] selectedNormalPixels = RenderSelection(box, device, AvailableWidth, RangeFor(box, normal));
        AssertNoInkInRegion(selectedNormalPixels, (int)AvailableWidth, emojiRegion);
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public void Context4Factory4FallbackPreservesShiftedAndUnshiftedColorGlyphs(
        FlowDirection flowDirection)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        AssertWarpColorGlyphSupport(device, forceLegacyColorSupport: true);
        const string source = "<sup>normal 😀 phrase</sup> العربية 😀";
        using LayoutSnapshot snapshot = BuildSnapshot(device, source, flowDirection, AvailableWidth);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        InlineRun script = Assert.Single(
            box.Runs,
            run => run.ElementKey == MarkdownElementKeys.Superscript);
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(box.TextLayout);
        string shapedText = string.Concat(box.Runs.Select(run => run.Text));
        const string expectedScriptText = "normal 😀 phrase";
        Assert.Equal(expectedScriptText, script.Text);
        Assert.Equal(expectedScriptText + " العربية 😀", shapedText);
        int scriptEmojiIndex = shapedText.IndexOf("😀", StringComparison.Ordinal);
        int unshiftedEmojiIndex = shapedText.LastIndexOf("😀", StringComparison.Ordinal);
        Assert.True(scriptEmojiIndex >= 0 && unshiftedEmojiIndex > scriptEmojiIndex,
            "The fixture did not preserve both shifted and unshifted emoji runs.");

        byte[] context4Pixels = RenderWithScriptBaselineRenderer(
            box,
            device,
            AvailableWidth,
            forceLegacyColorSupport: true);

        float topInset = BodyFontSize * 0.30f;
        float scriptOffset = -BodyFontSize * 0.30f;
        Rect shiftedScriptBounds = Offset(
            ExpectedRunBounds(box, layout, script, topInset),
            scriptOffset);
        var expectedScriptRanges = new[]
        {
            new ExpectedScriptRange(0, expectedScriptText.Length, scriptOffset),
        };
        byte[] legacyReference = RenderCapturedLegacyColorGlyphRuns(
            layout,
            device,
            AvailableWidth,
            context4Pixels.Length / ((int)AvailableWidth * 4),
            (float)ExpectedTextOriginY(box, topInset),
            expectedScriptRanges,
            Color.FromArgb(255, 12, 18, 24));
        AssertInkInRegion(context4Pixels, (int)AvailableWidth, shiftedScriptBounds);
        AssertPixelsMatchWithinRegion(
            context4Pixels,
            legacyReference,
            (int)AvailableWidth,
            shiftedScriptBounds);

        CanvasTextLayoutRegion unshiftedEmoji = layout.GetCharacterRegions(unshiftedEmojiIndex, 2)![0];
        Rect expectedUnshiftedEmoji = ExpectedRegionRect(
            box,
            unshiftedEmoji,
            expectedOffset: 0,
            expectedTopInset: topInset);
        AssertInkInRegion(context4Pixels, (int)AvailableWidth, expectedUnshiftedEmoji);
        AssertPixelsMatchWithinRegion(
            context4Pixels,
            legacyReference,
            (int)AvailableWidth,
            expectedUnshiftedEmoji);
    }

    [Fact]
    public void Context4FallbackDoesNotPaintTransparentSelectionColorGlyphs()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        AssertWarpColorGlyphSupport(device, forceLegacyColorSupport: true);
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Emoji",
            FontSize = BodyFontSize,
        };
        using var layout = new CanvasTextLayout(device, "😀", format, AvailableWidth, 64f);
        using var transparent = new CanvasSolidColorBrush(device, Color.FromArgb(0, 40, 90, 160));
        using var foreground = new CanvasSolidColorBrush(device, Color.FromArgb(255, 12, 18, 24));
        layout.SetBrush(0, 2, transparent);
        var geometryBuilder = new ScriptBaselineGeometry.Builder();
        geometryBuilder.Add(0, 2, -BodyFontSize * 0.30f);
        ScriptBaselineGeometry geometry = geometryBuilder.Build();

        using var target = new CanvasRenderTarget(device, AvailableWidth, 96f, 96f);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        using (var renderer = new ScriptBaselineTextRenderer(
                   drawing,
                   geometry,
                   foreground,
                   forceLegacyColorSupport: true))
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            layout.DrawToTextRenderer(renderer, 0f, 24f + geometry.TopInset);
        }

        AssertNoInkInRegion(
            target.GetPixelBytes(),
            (int)AvailableWidth,
            new Rect(0, 0, AvailableWidth, 96));
    }

    private static byte[] RenderWithScriptBaselineRenderer(
        InlineContainerBox box,
        CanvasDevice device,
        float width,
        bool forceLegacyColorSupport)
    {
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(box.TextLayout);
        ScriptBaselineGeometry geometry = Assert.IsType<ScriptBaselineGeometry>(box.ScriptBaselineGeometry);
        float height = NumericMath.Max(128f, (float)box.Bounds.Bottom + 16f);
        using var target = new CanvasRenderTarget(device, width, height, 96f);
        using var foreground = new CanvasSolidColorBrush(device, Color.FromArgb(255, 12, 18, 24));
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        using (var renderer = new ScriptBaselineTextRenderer(
                   drawing,
                   geometry,
                   foreground,
                   forceLegacyColorSupport: forceLegacyColorSupport))
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            layout.DrawToTextRenderer(
                renderer,
                0f,
                (float)ExpectedTextOriginY(box, geometry.TopInset));
        }

        return target.GetPixelBytes();
    }

    private static byte[] RenderCapturedLegacyColorGlyphRuns(
        CanvasTextLayout layout,
        CanvasDevice device,
        float width,
        int height,
        float originY,
        ExpectedScriptRange[] scriptRanges,
        Color foregroundColor)
    {
        var recorder = new RasterGlyphRunRecorder();
        layout.DrawToTextRenderer(recorder, 0f, originY);
        using var foreground = new CanvasSolidColorBrush(device, foregroundColor);
        using var target = new CanvasRenderTarget(device, width, height, 96f);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        using (var reference = new LegacyDirectWriteColorReference(drawing))
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            foreach (CapturedGlyphRun captured in recorder.Runs)
            {
                ICanvasBrush brush = captured.Brush as ICanvasBrush ?? foreground;
                (Vector2 point, CanvasGlyph[] glyphs) = ApplyExpectedScriptOffsets(captured, scriptRanges);
                point = SnapExpectedBaselineLineNormal(point, drawing.Transform, drawing.Dpi);
                if (!reference.TryDraw(point, captured, glyphs, brush, drawing.Transform, drawing.Dpi))
                {
                    drawing.DrawGlyphRun(
                        point,
                        captured.FontFace,
                        captured.FontSize,
                        glyphs,
                        captured.IsSideways,
                        captured.BidiLevel,
                        brush,
                        captured.MeasuringMode,
                        captured.LocaleName,
                        captured.Text,
                        captured.ClusterMap,
                        captured.CharacterIndex);
                }
            }
        }

        return target.GetPixelBytes();
    }

    private static (Vector2 Point, CanvasGlyph[] Glyphs) ApplyExpectedScriptOffsets(
        CapturedGlyphRun run,
        ExpectedScriptRange[] scriptRanges)
    {
        int characterIndex = run.CharacterIndex <= int.MaxValue ? (int)run.CharacterIndex : -1;
        if (characterIndex < 0 || string.IsNullOrEmpty(run.Text) || run.Glyphs.Length == 0)
            return (run.Point, run.Glyphs);

        int characterCount = System.Math.Min(run.Text.Length, run.ClusterMap.Length);
        if (characterCount == 0)
        {
            Vector2 point = run.Point;
            point.Y += ExpectedScriptOffset(characterIndex, scriptRanges);
            return (point, run.Glyphs);
        }

        var firstCharacterByGlyph = new int[run.Glyphs.Length];
        Array.Fill(firstCharacterByGlyph, -1);
        for (int characterOffset = 0; characterOffset < characterCount; characterOffset++)
        {
            int glyphIndex = run.ClusterMap[characterOffset];
            if ((uint)glyphIndex < (uint)firstCharacterByGlyph.Length && firstCharacterByGlyph[glyphIndex] < 0)
                firstCharacterByGlyph[glyphIndex] = characterOffset;
        }

        int precedingCharacterOffset = 0;
        var glyphOffsets = new float[run.Glyphs.Length];
        bool hasMixedOffsets = false;
        for (int glyphIndex = 0; glyphIndex < run.Glyphs.Length; glyphIndex++)
        {
            if (firstCharacterByGlyph[glyphIndex] >= 0)
                precedingCharacterOffset = firstCharacterByGlyph[glyphIndex];
            else
                firstCharacterByGlyph[glyphIndex] = precedingCharacterOffset;

            glyphOffsets[glyphIndex] = ExpectedScriptOffset(
                characterIndex + firstCharacterByGlyph[glyphIndex],
                scriptRanges);
            if (glyphIndex > 0 && glyphOffsets[glyphIndex] != glyphOffsets[0])
                hasMixedOffsets = true;
        }

        if (!hasMixedOffsets)
        {
            Vector2 point = run.Point;
            point.Y += glyphOffsets[0];
            return (point, run.Glyphs);
        }

        CanvasGlyph[] adjustedGlyphs = (CanvasGlyph[])run.Glyphs.Clone();
        for (int glyphIndex = 0; glyphIndex < adjustedGlyphs.Length; glyphIndex++)
            adjustedGlyphs[glyphIndex].AscenderOffset -= glyphOffsets[glyphIndex];
        return (run.Point, adjustedGlyphs);
    }

    private static float ExpectedScriptOffset(int characterIndex, ExpectedScriptRange[] ranges)
    {
        foreach (ExpectedScriptRange range in ranges)
        {
            if (characterIndex >= range.Start && characterIndex < range.Start + range.Length)
                return range.Offset;
        }

        return 0f;
    }

    private static void AssertNoInkInRegion(byte[] pixels, int width, Rect region)
    {
        int pixelHeight = pixels.Length / checked(width * 4);
        int startX = NumericMath.Clamp((int)NumericMath.Floor(region.Left), 0, width);
        int endX = NumericMath.Clamp((int)NumericMath.Ceiling(region.Right), 0, width);
        int startY = NumericMath.Clamp((int)NumericMath.Floor(region.Top), 0, pixelHeight);
        int endY = NumericMath.Clamp((int)NumericMath.Ceiling(region.Bottom), 0, pixelHeight);
        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                int alpha = pixels[checked((y * width + x) * 4 + 3)];
                Assert.Equal(0, alpha);
            }
        }
    }

    private readonly record struct ExpectedScriptRange(int Start, int Length, float Offset);
}
