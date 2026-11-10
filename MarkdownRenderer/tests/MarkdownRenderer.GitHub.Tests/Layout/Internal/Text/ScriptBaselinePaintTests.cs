using System.Numerics;
using NumericMath = global::System.Math;
using MarkdownRenderer.GitHub.Tests.TestSupport.Assertions;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Text;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ScriptBaselineFixture;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Assertions.ScriptRasterAssertions;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Text;

/// <summary>
/// Checks that script runs paint at their final snapped baseline and that
/// adjacent scripts keep distinct paint baselines and glyph clusters.
/// </summary>
public sealed class ScriptBaselinePaintTests
{
    [Theory]
    [InlineData(96f)]
    [InlineData(144f)]
    [InlineData(192f)]
    public void PaintSnapsTheFinalBaselineAfterScriptOffsetAtDrawingDpi(float dpi)
    {
        Matrix3x2 transform = Matrix3x2.CreateTranslation(0.31f, 0.37f);
        Vector2 nativeBaseline = new(14.2f, 20.63f);
        const float superscriptOffset = -BodyFontSize * 0.30f;
        Vector2 finalBaseline = ScriptBaselineTextRenderer.SnapBaselineToDevicePixel(
            nativeBaseline with { Y = nativeBaseline.Y + superscriptOffset },
            transform,
            dpi);

        Vector2 deviceBaseline = Vector2.Transform(finalBaseline, transform) * (dpi / 96f);
        Vector2 unsnappedDeviceBaseline = Vector2.Transform(
            nativeBaseline with { Y = nativeBaseline.Y + superscriptOffset },
            transform) * (dpi / 96f);
        Assert.InRange(NumericMath.Abs(deviceBaseline.X - unsnappedDeviceBaseline.X), 0, 0.0001);
        Assert.InRange(NumericMath.Abs(deviceBaseline.Y - NumericMath.Round(deviceBaseline.Y)), 0, 0.0001);

        if (dpi == 144f)
        {
            // This fixture lands exactly on the boundary where snapping the
            // unshifted callback first would move the final baseline one pixel.
            Vector2 snappedBeforeOffset = ScriptBaselineTextRenderer.SnapBaselineToDevicePixel(
                nativeBaseline,
                transform,
                dpi);
            Vector2 oldOrderDeviceBaseline = Vector2.Transform(
                snappedBeforeOffset with { Y = snappedBeforeOffset.Y + superscriptOffset },
                transform) * (dpi / 96f);
            Assert.NotEqual(
                NumericMath.Round(deviceBaseline.Y),
                NumericMath.Round(oldOrderDeviceBaseline.Y));
        }
    }

    [Fact]
    public void BaselineSnapLeavesLogicalPointUnchangedWhenTransformCannotBeInverted()
    {
        Vector2 baseline = new(14.2f, 20.63f);
        Matrix3x2 singular = Matrix3x2.CreateScale(0f, 1f);
        Assert.Equal(baseline, ScriptBaselineTextRenderer.SnapBaselineToDevicePixel(baseline, singular, 144f));

        Matrix3x2 nonFinite = Matrix3x2.CreateTranslation(float.NaN, 0f);
        Assert.Equal(baseline, ScriptBaselineTextRenderer.SnapBaselineToDevicePixel(baseline, nonFinite, 144f));
        Assert.Equal(baseline, ScriptBaselineTextRenderer.SnapBaselineToDevicePixel(baseline, Matrix3x2.Identity, float.NaN));
    }

    [Fact]
    public void AdjacentScriptsWithEqualTypographyKeepTheirDistinctPaintBaselines()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        const string source = "<sup>x</sup><sub>y</sub>";
        using LayoutSnapshot snapshot = BuildSnapshot(
            device,
            source,
            FlowDirection.LeftToRight,
            AvailableWidth);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        InlineRun superscript = Assert.Single(
            box.Runs,
            run => run.ElementKey == MarkdownElementKeys.Superscript);
        InlineRun subscript = Assert.Single(
            box.Runs,
            run => run.ElementKey == MarkdownElementKeys.Subscript);
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(box.TextLayout);
        Assert.Equal(11f, layout.GetFontSize(GetBufferStart(box, superscript)));
        Assert.Equal(11f, layout.GetFontSize(GetBufferStart(box, subscript)));

        CanvasTextLayoutRegion superscriptRegion = layout.GetCharacterRegions(
            GetBufferStart(box, superscript),
            superscript.TextLength)![0];
        CanvasTextLayoutRegion subscriptRegion = layout.GetCharacterRegions(
            GetBufferStart(box, subscript),
            subscript.TextLength)![0];
        const float superscriptOffset = -BodyFontSize * 0.30f;
        const float subscriptOffset = BodyFontSize * 0.20f;
        const float topInset = BodyFontSize * 0.30f;
        AssertRangeGeometry(box, layout, superscript, superscriptOffset, topInset);
        AssertRangeGeometry(box, layout, subscript, subscriptOffset, topInset);

        byte[] actual = Render(box, device, AvailableWidth);
        byte[] independent = RenderSeparateScriptFragments(
            device,
            AvailableWidth,
            actual.Length / ((int)AvailableWidth * 4),
            layout,
            (float)ExpectedTextOriginY(box, topInset),
            new ScriptRasterFragment("x", superscriptRegion, superscriptOffset),
            new ScriptRasterFragment("y", subscriptRegion, subscriptOffset));
        AssertRasterRowsMatch(actual, independent, (int)AvailableWidth);
    }

    [Fact]
    public void AdjacentSuperscriptAndSubscriptDoNotShareOneLigatureCluster()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        const string source = "<sup>f</sup><sub>i</sub>";
        using LayoutSnapshot snapshot = BuildSnapshot(
            device,
            source,
            FlowDirection.LeftToRight,
            AvailableWidth);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        InlineRun superscript = Assert.Single(
            box.Runs,
            run => run.ElementKey == MarkdownElementKeys.Superscript);
        InlineRun subscript = Assert.Single(
            box.Runs,
            run => run.ElementKey == MarkdownElementKeys.Subscript);
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(box.TextLayout);
        int superscriptStart = GetBufferStart(box, superscript);
        int subscriptStart = GetBufferStart(box, subscript);

        // The effective script font sizes and colors are deliberately equal.
        // Without a shaping boundary, DirectWrite is free to coalesce "fi"
        // into one glyph cluster, which cannot be assigned two baselines.
        Assert.Equal(11f, layout.GetFontSize(superscriptStart));
        Assert.Equal(11f, layout.GetFontSize(subscriptStart));
        var recorder = new GlyphRunRecorder();
        layout.DrawToTextRenderer(recorder, 0f, 0f);

        bool scriptsShareOneCluster = recorder.Runs.Any(run =>
            run.TryGetSharedLigatureCluster(superscriptStart, subscriptStart));
        Assert.False(
            scriptsShareOneCluster,
            "DirectWrite shaped superscript 'f' and subscript 'i' into one glyph cluster. " +
            "Glyph callbacks: " + string.Join(" | ", recorder.Runs.Select(run => run.ToDiagnosticString())));

        float topInset = BodyFontSize * 0.30f;
        CanvasTextLayoutRegion superscriptRegion = layout.GetCharacterRegions(superscriptStart, 1)![0];
        CanvasTextLayoutRegion subscriptRegion = layout.GetCharacterRegions(subscriptStart, 1)![0];
        byte[] pixels = Render(box, device, AvailableWidth);
        AssertInkInRegion(
            pixels,
            (int)AvailableWidth,
            ExpectedRegionRect(box, superscriptRegion, -BodyFontSize * 0.30f, topInset));
        AssertInkInRegion(
            pixels,
            (int)AvailableWidth,
            ExpectedRegionRect(box, subscriptRegion, BodyFontSize * 0.20f, topInset));
    }

    private static byte[] RenderSeparateScriptFragments(
        CanvasDevice device,
        float width,
        int height,
        CanvasTextLayout referenceLayout,
        double textOriginY,
        params ScriptRasterFragment[] fragments)
    {
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI",
            FontSize = 11f,
        };
        using var target = new CanvasRenderTarget(device, width, height, 96f);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            foreach (ScriptRasterFragment fragment in fragments)
            {
                using var layout = new CanvasTextLayout(
                    device,
                    fragment.Text,
                    format,
                    width,
                    height);
                CanvasTextLayoutRegion isolatedRegion = layout.GetCharacterRegions(
                    0,
                    fragment.Text.Length)![0];
                float originX = (float)(fragment.NativeRegion.LayoutBounds.X -
                    isolatedRegion.LayoutBounds.X);
                // Align baselines, not line-box/selection-region tops. The
                // script fragment uses an 11pt layout while the reference is
                // a 16pt paragraph layout; their region Y values alone differ
                // by the line-height gap and would create a false ~6px error.
                float expectedBaseline = (float)(textOriginY +
                    referenceLayout.LineMetrics[0].Baseline +
                    fragment.OffsetY);
                float originY = expectedBaseline - layout.LineMetrics[0].Baseline;
                drawing.DrawTextLayout(
                    layout,
                    originX,
                    originY,
                    Color.FromArgb(255, 12, 18, 24));
            }
        }

        return target.GetPixelBytes();
    }

    private readonly record struct ScriptRasterFragment(
        string Text,
        CanvasTextLayoutRegion NativeRegion,
        float OffsetY);

    private sealed class GlyphRunRecorder : ICanvasTextRenderer
    {
        internal GlyphRunRecorder(bool pixelSnappingDisabled = false)
            => PixelSnappingDisabled = pixelSnappingDisabled;

        public List<GlyphRunTrace> Runs { get; } = [];
        public float Dpi => 96f;
        public bool PixelSnappingDisabled { get; }
        public Matrix3x2 Transform => Matrix3x2.Identity;

        public void DrawGlyphRun(
            Vector2 point,
            CanvasFontFace fontFace,
            float fontSize,
            CanvasGlyph[] glyphs,
            bool isSideways,
            uint bidiLevel,
            object brush,
            CanvasTextMeasuringMode measuringMode,
            string localeName,
            string textString,
            int[] clusterMapIndices,
            uint characterIndex,
            CanvasGlyphOrientation glyphOrientation)
        {
            Runs.Add(new GlyphRunTrace(
                point,
                textString,
                characterIndex,
                clusterMapIndices is null ? [] : (int[])clusterMapIndices.Clone(),
                glyphs.Length));
        }

        public void DrawStrikethrough(
            Vector2 point,
            float strikethroughWidth,
            float strikethroughThickness,
            float strikethroughOffset,
            CanvasTextDirection textDirection,
            object brush,
            CanvasTextMeasuringMode textMeasuringMode,
            string localeName,
            CanvasGlyphOrientation glyphOrientation)
        {
        }

        public void DrawUnderline(
            Vector2 point,
            float underlineWidth,
            float underlineThickness,
            float underlineOffset,
            float runHeight,
            CanvasTextDirection textDirection,
            object brush,
            CanvasTextMeasuringMode textMeasuringMode,
            string localeName,
            CanvasGlyphOrientation glyphOrientation)
        {
        }

        public void DrawInlineObject(
            Vector2 point,
            ICanvasTextInlineObject inlineObject,
            bool isSideways,
            bool isRightToLeft,
            object brush,
            CanvasGlyphOrientation glyphOrientation)
        {
        }
    }

    private sealed record GlyphRunTrace(
        Vector2 Point,
        string Text,
        uint CharacterIndex,
        int[] ClusterMap,
        int GlyphCount)
    {
        public bool TryGetSharedLigatureCluster(int firstCharacterIndex, int secondCharacterIndex)
        {
            if (firstCharacterIndex < CharacterIndex || secondCharacterIndex < CharacterIndex)
                return false;
            int firstIndex = firstCharacterIndex - (int)CharacterIndex;
            int secondIndex = secondCharacterIndex - (int)CharacterIndex;
            return (uint)firstIndex < (uint)ClusterMap.Length &&
                (uint)secondIndex < (uint)ClusterMap.Length &&
                ClusterMap[firstIndex] == ClusterMap[secondIndex];
        }

        public string ToDiagnosticString()
            => $"text={Text}, char={CharacterIndex}, glyphs={GlyphCount}, cluster=[{string.Join(",", ClusterMap)}]";
    }
}
