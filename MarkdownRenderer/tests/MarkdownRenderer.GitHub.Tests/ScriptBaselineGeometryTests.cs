using System.Reflection;
using System.Numerics;
using System.Runtime.InteropServices;
using NumericMath = global::System.Math;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using WinRT;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed partial class ScriptBaselineGeometryTests
{
    private const float AvailableWidth = 124f;
    private const float BodyFontSize = 16f;
    private static readonly FieldInfo LayoutField = typeof(InlineContainerBox).GetField(
        "_layout",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not find InlineContainerBox._layout.");
    private static readonly FieldInfo ScriptGeometryField = typeof(InlineContainerBox).GetField(
        "_scriptBaselineGeometry",
        BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Could not find InlineContainerBox._scriptBaselineGeometry.");

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
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(LayoutField.GetValue(box));
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
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(LayoutField.GetValue(box));
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

        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(LayoutField.GetValue(box));
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

        var selection = RangeFor(box, superscript);
        byte[] selectedPixels = RenderSelection(box, device, AvailableWidth, selection);
        AssertInkInRegion(selectedPixels, (int)AvailableWidth, expectedSupRect);
    }

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
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(LayoutField.GetValue(box));
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
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(LayoutField.GetValue(box));
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
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(LayoutField.GetValue(box));
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

    [Fact]
    public void ScriptTextRendererRejectsNullForegroundBeforeCreatingNativeState()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var target = new CanvasRenderTarget(device, 64, 64, 96f);
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        var geometry = new ScriptBaselineGeometry.Builder().Build();

        Assert.Throws<ArgumentNullException>(() => new ScriptBaselineTextRenderer(
            drawing,
            geometry,
            defaultForeground: null!));
    }

    [Fact]
    public void NativeColorGlyphContextIsReleasedWhenScriptRendererIsDisposed()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var target = new CanvasRenderTarget(device, 64, 64, 96f);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            var renderer = new ScriptBaselineNativeColorGlyphRenderer(drawing);
            renderer.Dispose();
            Assert.False(renderer.IsSupported);
            renderer.Dispose();

            var legacyRenderer = new ScriptBaselineNativeColorGlyphRenderer(
                drawing,
                forceLegacyColorSupport: true);
            Assert.True(legacyRenderer.IsLegacyPath);
            legacyRenderer.Dispose();
            Assert.False(legacyRenderer.IsSupported);
            legacyRenderer.Dispose();
        }
    }

    [Fact]
    public void NativeGlyphRunAndOffsetLayoutsMatchDirectWriteAbi()
    {
        Assert.Equal(
            122,
            ScriptBaselineNativeColorGlyphRenderer.DrawGlyphRunWithColorSupportVtableSlot);
        Assert.Equal(40, ScriptBaselineNativeColorGlyphRenderer.Factory4TranslateColorGlyphRunVtableSlot);
        Assert.Equal(3, ScriptBaselineNativeColorGlyphRenderer.ColorGlyphRunEnumeratorMoveNextVtableSlot);
        Assert.Equal(5, ScriptBaselineNativeColorGlyphRenderer.ColorGlyphRunEnumerator1GetCurrentRunVtableSlot);
        Assert.Equal(82, ScriptBaselineNativeColorGlyphRenderer.DeviceContextDrawGlyphRunVtableSlot);
        Assert.Equal(8, ScriptBaselineNativeColorGlyphRenderer.RenderTargetCreateSolidColorBrushVtableSlot);
        Assert.Equal(8, ScriptBaselineNativeColorGlyphRenderer.SolidColorBrushSetColorVtableSlot);
        Assert.Equal(111, ScriptBaselineNativeColorGlyphRenderer.DeviceContext4DrawColorBitmapGlyphRunVtableSlot);
        Assert.Equal(112, ScriptBaselineNativeColorGlyphRenderer.DeviceContext4DrawSvgGlyphRunVtableSlot);
        Assert.Equal(8, Marshal.SizeOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphOffset>());
        Assert.Equal(0, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphOffset>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphOffset.AdvanceOffset)));
        Assert.Equal(4, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphOffset>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphOffset.AscenderOffset)));

        int pointerSize = IntPtr.Size;
        Assert.Equal(pointerSize == 8 ? 48 : 32,
            Marshal.SizeOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>());
        Assert.Equal(0, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.FontFace)));
        Assert.Equal(pointerSize, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.FontEmSize)));
        Assert.Equal(pointerSize + 4, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.GlyphCount)));
        Assert.Equal(pointerSize + 8, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.GlyphIndices)));
        Assert.Equal(pointerSize + 8 + pointerSize, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.GlyphAdvances)));
        Assert.Equal(pointerSize + 8 + (pointerSize * 2), OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.GlyphOffsets)));
        Assert.Equal(pointerSize + 8 + (pointerSize * 3), OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.IsSideways)));
        Assert.Equal(pointerSize + 12 + (pointerSize * 3), OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeGlyphRun.BidiLevel)));

        // The test target is x64 today, but preserve explicit x86 ABI coverage
        // even when this suite is not executable as a 32-bit WinUI process.
        Assert.Equal(32, Marshal.SizeOf<NativeGlyphRun32>());
        Assert.Equal(0, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.FontFace)));
        Assert.Equal(4, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.FontEmSize)));
        Assert.Equal(8, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.GlyphCount)));
        Assert.Equal(12, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.GlyphIndices)));
        Assert.Equal(16, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.GlyphAdvances)));
        Assert.Equal(20, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.GlyphOffsets)));
        Assert.Equal(24, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.IsSideways)));
        Assert.Equal(28, OffsetOf<NativeGlyphRun32>(nameof(NativeGlyphRun32.BidiLevel)));

        Assert.Equal(16, Marshal.SizeOf<ScriptBaselineNativeColorGlyphRenderer.NativeColor>());
        Assert.Equal(24, Marshal.SizeOf<ScriptBaselineNativeColorGlyphRenderer.NativeDWriteMatrix>());
        Assert.Equal(pointerSize == 8 ? 88 : 64,
            Marshal.SizeOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun>());
        Assert.Equal(0, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun.GlyphRun)));
        Assert.Equal(pointerSize == 8 ? 48 : 32,
            OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun>(
                nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun.GlyphRunDescription)));
        Assert.Equal(pointerSize == 8 ? 56 : 36,
            OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun>(
                nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun.BaselineOriginX)));
        Assert.Equal(pointerSize == 8 ? 64 : 44,
            OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun>(
                nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun.RunColor)));
        Assert.Equal(pointerSize == 8 ? 80 : 60,
            OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun>(
                nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun.PaletteIndex)));
        Assert.Equal(pointerSize == 8 ? 96 : 72,
            Marshal.SizeOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun1>());
        Assert.Equal(0, OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun1>(
            nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun1.Base)));
        Assert.Equal(pointerSize == 8 ? 88 : 64,
            OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun1>(
                nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun1.GlyphImageFormat)));
        Assert.Equal(pointerSize == 8 ? 92 : 68,
            OffsetOf<ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun1>(
                nameof(ScriptBaselineNativeColorGlyphRenderer.NativeColorGlyphRun1.MeasuringMode)));

        Assert.Equal(72, Marshal.SizeOf<NativeColorGlyphRun32>());
        Assert.Equal(32, OffsetOf<NativeColorGlyphRun32>(nameof(NativeColorGlyphRun32.GlyphRunDescription)));
        Assert.Equal(36, OffsetOf<NativeColorGlyphRun32>(nameof(NativeColorGlyphRun32.BaselineOriginX)));
        Assert.Equal(44, OffsetOf<NativeColorGlyphRun32>(nameof(NativeColorGlyphRun32.RunColor)));
        Assert.Equal(60, OffsetOf<NativeColorGlyphRun32>(nameof(NativeColorGlyphRun32.PaletteIndex)));
        Assert.Equal(64, OffsetOf<NativeColorGlyphRun32>(nameof(NativeColorGlyphRun32.GlyphImageFormat)));
        Assert.Equal(68, OffsetOf<NativeColorGlyphRun32>(nameof(NativeColorGlyphRun32.MeasuringMode)));
    }

    [Fact]
    public void LegacyColorRendererAcceptsOnlyBoundedOutlineFormatFlags()
    {
        foreach (int format in new[] { 0x1, 0x2, 0x4, 0x3, 0x5, 0x6, 0x7 })
            Assert.True(ScriptBaselineNativeColorGlyphRenderer.IsSupportedOutlineImageFormat(format));

        foreach (int format in new[] { 0, 0x8, 0x10, 0x80, 0x100, 0x105, -1 })
            Assert.False(ScriptBaselineNativeColorGlyphRenderer.IsSupportedOutlineImageFormat(format));
    }

    private static int OffsetOf<T>(string fieldName) where T : struct
        => Marshal.OffsetOf<T>(fieldName).ToInt32();

    private static void AssertWarpColorGlyphSupport(
        CanvasDevice device,
        bool forceLegacyColorSupport = false)
    {
        using var target = new CanvasRenderTarget(device, 8, 8, 96f);
        using CanvasDrawingSession drawing = target.CreateDrawingSession();
        using var renderer = new ScriptBaselineNativeColorGlyphRenderer(
            drawing,
            forceLegacyColorSupport);
        Assert.True(renderer.IsSupported);
        if (forceLegacyColorSupport)
            Assert.True(renderer.IsLegacyPath);
        else
            Assert.Equal(
                ScriptBaselineNativeColorGlyphRenderer.IsContext7Available(drawing),
                !renderer.IsLegacyPath);
    }

    private static byte[] RenderWithScriptBaselineRenderer(
        InlineContainerBox box,
        CanvasDevice device,
        float width,
        bool forceLegacyColorSupport)
    {
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(LayoutField.GetValue(box));
        ScriptBaselineGeometry geometry = Assert.IsType<ScriptBaselineGeometry>(ScriptGeometryField.GetValue(box));
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeGlyphRun32
    {
        public int FontFace;
        public float FontEmSize;
        public uint GlyphCount;
        public int GlyphIndices;
        public int GlyphAdvances;
        public int GlyphOffsets;
        public int IsSideways;
        public uint BidiLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeColorGlyphRun32
    {
        public NativeGlyphRun32 GlyphRun;
        public int GlyphRunDescription;
        public float BaselineOriginX;
        public float BaselineOriginY;
        public ScriptBaselineNativeColorGlyphRenderer.NativeColor RunColor;
        public ushort PaletteIndex;
        public ushort Reserved;
        public int GlyphImageFormat;
        public int MeasuringMode;
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
        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(LayoutField.GetValue(box));
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

        CanvasTextLayout layout = Assert.IsType<CanvasTextLayout>(LayoutField.GetValue(box));
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

    private static void AssertRangeGeometry(
        InlineContainerBox box,
        CanvasTextLayout layout,
        InlineRun run,
        float expectedOffset,
        float expectedTopInset)
    {
        int start = GetBufferStart(box, run);
        CanvasTextLayoutRegion[] nativeRegions = layout.GetCharacterRegions(start, run.TextLength)!;
        Rect[] actualRegions = box.GetRangeRects(RangeFor(box, run)).ToArray();
        Assert.Equal(nativeRegions.Length, actualRegions.Length);

        double textOriginY = ExpectedTextOriginY(box, expectedTopInset);
        for (int index = 0; index < nativeRegions.Length; index++)
        {
            double expectedY = textOriginY + nativeRegions[index].LayoutBounds.Y + expectedOffset;
            Assert.InRange(NumericMath.Abs(actualRegions[index].Y - expectedY), 0, 0.02);
            Assert.InRange(
                NumericMath.Abs(actualRegions[index].Height - nativeRegions[index].LayoutBounds.Height),
                0,
                0.02);
            Assert.InRange(
                NumericMath.Abs(actualRegions[index].Width - nativeRegions[index].LayoutBounds.Width),
                0,
                0.02);
        }
    }

    private static Rect ExpectedRegionRect(
        InlineContainerBox box,
        CanvasTextLayoutRegion region,
        float expectedOffset,
        float expectedTopInset)
    {
        Rect bounds = region.LayoutBounds;
        return new Rect(
            bounds.X,
            ExpectedTextOriginY(box, expectedTopInset) + bounds.Y + expectedOffset,
            bounds.Width,
            bounds.Height);
    }

    private static Rect ExpectedRunBounds(
        InlineContainerBox box,
        CanvasTextLayout layout,
        InlineRun run,
        float expectedTopInset)
    {
        int start = GetBufferStart(box, run);
        CanvasTextLayoutRegion[] regions = layout.GetCharacterRegions(start, run.TextLength)!;
        double left = regions.Min(region => region.LayoutBounds.Left);
        double top = regions.Min(region => region.LayoutBounds.Top);
        double right = regions.Max(region => region.LayoutBounds.Right);
        double bottom = regions.Max(region => region.LayoutBounds.Bottom);
        return new Rect(
            left,
            ExpectedTextOriginY(box, expectedTopInset) + top,
            right - left,
            bottom - top);
    }

    private static Rect Offset(Rect rect, double offsetY)
        => new(rect.X, rect.Y + offsetY, rect.Width, rect.Height);

    private static double ExpectedTextOriginY(InlineContainerBox box, float expectedTopInset)
    {
        double unsnappedY = box.Bounds.Y + box.Margin.Top + expectedTopInset;
        double independentlySnappedY = NumericMath.Round(unsnappedY, MidpointRounding.AwayFromZero);
        Assert.InRange(NumericMath.Abs(box.GetTextOriginY() - independentlySnappedY), 0, 0.02);
        return independentlySnappedY;
    }

    private static DocumentRange RangeFor(InlineContainerBox box, InlineRun run)
        => new(
            new DocumentPosition(box.BlockIndex, run.InlineIndex, 0),
            new DocumentPosition(box.BlockIndex, run.InlineIndex, run.TextLength));

    private static int GetBufferStart(InlineContainerBox box, InlineRun expected)
    {
        int start = 0;
        foreach (InlineRun run in box.Runs)
        {
            if (ReferenceEquals(run, expected))
                return start;
            start = checked(start + run.TextLength);
        }

        throw new InvalidOperationException("The expected run is not part of the measured inline buffer.");
    }

    private static int GetBufferLength(InlineContainerBox box)
        => box.Runs.Sum(run => run.TextLength);

    private static byte[] Render(InlineContainerBox box, CanvasDevice device, float width)
    {
        float height = NumericMath.Max(128f, (float)box.Bounds.Bottom + 16f);
        using var target = new CanvasRenderTarget(device, width, height, 96f);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            box.Paint(drawing, new Rect(0, 0, width, height));
        }

        return target.GetPixelBytes();
    }

    private static byte[] RenderSelection(
        InlineContainerBox box,
        CanvasDevice device,
        float width,
        DocumentRange range)
    {
        float height = NumericMath.Max(128f, (float)box.Bounds.Bottom + 16f);
        using var target = new CanvasRenderTarget(device, width, height, 96f);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            box.PaintSelectionForeground(
                drawing,
                range,
                Color.FromArgb(255, 245, 31, 191),
                new Rect(0, 0, width, height));
        }

        return target.GetPixelBytes();
    }

    private static byte[] RenderNativeLayout(
        CanvasTextLayout layout,
        CanvasDevice device,
        float width,
        int height,
        double originY,
        Color foreground)
    {
        using var target = new CanvasRenderTarget(device, width, height, 96f);
        using (CanvasDrawingSession drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Color.FromArgb(0, 0, 0, 0));
            drawing.DrawTextLayout(layout, 0f, (float)originY, foreground);
        }

        return target.GetPixelBytes();
    }

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

    /// <summary>
    /// Independent same-era oracle for the documented pre-Context7 color-font
    /// path. It deliberately owns its ABI declarations and vtable calls instead
    /// of calling the production renderer, so this test can catch production
    /// translation, palette, and color-layer drawing defects.
    /// </summary>
    private sealed unsafe partial class LegacyDirectWriteColorReference : IDisposable
    {
        private static readonly Guid IidCanvasResourceWrapperNative = new("5f10688d-ea55-4d55-a3b0-4ddb55c0c20a");
        private static readonly Guid IidD2D1DeviceContext4 = new("8c427831-3d90-4476-b647-c4fae349e4db");
        private static readonly Guid IidD2D1Brush = new("2cd906a8-12e2-11dc-9fed-001143a055f9");
        private static readonly Guid IidDWriteFontFaceReference = new("5e7fa7ca-dde3-424c-89f0-9fcd6fed58cd");
        private static readonly Guid IidDWriteFactory4 = new("4b0b5bd3-0797-4549-8ac5-fe915cc53856");

        // Header-derived ID2D1DeviceContext4/ID2D1RenderTarget and DirectWrite
        // slots. These are intentionally local to this test oracle.
        private const int Factory4TranslateColorGlyphRun = 40;
        private const int EnumeratorMoveNext = 3;
        private const int Enumerator1GetCurrentRun = 5;
        private const int ContextDrawGlyphRun = 82;
        private const int ContextCreateSolidColorBrush = 8;
        private const int SolidColorBrushSetColor = 8;
        private const int Context4DrawColorBitmapGlyphRun = 111;
        private const int Context4DrawSvgGlyphRun = 112;
        private const int DWriteNoColor = unchecked((int)0x8898500C);
        private const uint LegacyImageFormats = 0xFF;
        private const ushort NoPalette = 0xFFFF;

        private nint _context4;
        private nint _factory4;
        private nint _paletteBrush;

        public LegacyDirectWriteColorReference(CanvasDrawingSession drawing)
        {
            _context4 = GetNativeResource(drawing, IidD2D1DeviceContext4);
            try
            {
                Guid iid = IidDWriteFactory4;
                int hr = DWriteCreateFactory(0, ref iid, out _factory4);
                ThrowIfFailed(hr);
                if (_factory4 == 0)
                    throw new InvalidOperationException("DWriteCreateFactory returned a null IDWriteFactory4.");
            }
            catch
            {
                Release(ref _factory4);
                Release(ref _context4);
                throw;
            }
        }

        public bool TryDraw(
            Vector2 baseline,
            CapturedGlyphRun source,
            CanvasGlyph[] glyphs,
            ICanvasBrush foreground,
            Matrix3x2 transform,
            float dpi)
        {
            if (glyphs.Length == 0 || IsTransparent(foreground))
                return true;

            nint fontFaceReference = 0;
            nint fontFace = 0;
            nint foregroundBrush = 0;
            nint enumerator = 0;
            ushort[] indices = new ushort[glyphs.Length];
            float[] advances = new float[glyphs.Length];
            ReferenceGlyphOffset[] offsets = new ReferenceGlyphOffset[glyphs.Length];
            try
            {
                fontFaceReference = GetNativeResource(source.FontFace, IidDWriteFontFaceReference);
                var referenceVtable = *(nint**)fontFaceReference;
                var createFontFace = (delegate* unmanaged[Stdcall]<nint, nint*, int>)referenceVtable[3];
                int createHr = createFontFace(fontFaceReference, &fontFace);
                ThrowIfFailed(createHr);
                if (fontFace == 0)
                    throw new InvalidOperationException("IDWriteFontFaceReference returned a null face.");

                foregroundBrush = GetNativeResource(foreground, IidD2D1Brush);
                for (int index = 0; index < glyphs.Length; index++)
                {
                    if ((uint)glyphs[index].Index > ushort.MaxValue)
                        throw new InvalidOperationException("DirectWrite returned an invalid glyph index.");
                    indices[index] = (ushort)glyphs[index].Index;
                    advances[index] = glyphs[index].Advance;
                    offsets[index] = new ReferenceGlyphOffset(
                        glyphs[index].AdvanceOffset,
                        glyphs[index].AscenderOffset);
                }

                var nativeRun = new ReferenceGlyphRun
                {
                    FontFace = fontFace,
                    FontEmSize = source.FontSize,
                    GlyphCount = checked((uint)glyphs.Length),
                    IsSideways = source.IsSideways ? 1 : 0,
                    BidiLevel = source.BidiLevel,
                };

                fixed (ushort* indicesPointer = indices)
                fixed (float* advancesPointer = advances)
                fixed (ReferenceGlyphOffset* offsetsPointer = offsets)
                {
                    nativeRun.GlyphIndices = indicesPointer;
                    nativeRun.GlyphAdvances = advancesPointer;
                    nativeRun.GlyphOffsets = offsetsPointer;
                    int measuringMode = source.MeasuringMode switch
                    {
                        CanvasTextMeasuringMode.Natural => 0,
                        CanvasTextMeasuringMode.GdiClassic => 1,
                        CanvasTextMeasuringMode.GdiNatural => 2,
                        _ => throw new ArgumentOutOfRangeException(nameof(source)),
                    };
                    ReferenceMatrix worldAndDpi = ToWorldAndDpi(transform, dpi);
                    var factoryVtable = *(nint**)_factory4;
                    var translate = (delegate* unmanaged[Stdcall]<
                        nint,
                        Vector2,
                        ReferenceGlyphRun*,
                        nint,
                        uint,
                        int,
                        ReferenceMatrix*,
                        uint,
                        nint*,
                        int>)factoryVtable[Factory4TranslateColorGlyphRun];
                    int translateHr = translate(
                        _factory4,
                        baseline,
                        &nativeRun,
                        0,
                        LegacyImageFormats,
                        measuringMode,
                        &worldAndDpi,
                        0,
                        &enumerator);
                    if (translateHr == DWriteNoColor)
                        return false;
                    ThrowIfFailed(translateHr);
                    if (enumerator == 0)
                        throw new InvalidOperationException("TranslateColorGlyphRun returned no enumerator.");

                    DrawTranslatedRuns(enumerator, foregroundBrush);
                    return true;
                }
            }
            finally
            {
                Release(ref enumerator);
                Release(ref foregroundBrush);
                Release(ref fontFace);
                Release(ref fontFaceReference);
            }
        }

        public void Dispose()
        {
            Release(ref _paletteBrush);
            Release(ref _factory4);
            Release(ref _context4);
        }

        private void DrawTranslatedRuns(nint enumerator, nint foregroundBrush)
        {
            var enumeratorVtable = *(nint**)enumerator;
            var moveNext = (delegate* unmanaged[Stdcall]<nint, int*, int>)enumeratorVtable[EnumeratorMoveNext];
            var getCurrentRun = (delegate* unmanaged[Stdcall]<nint, ReferenceColorGlyphRun1**, int>)enumeratorVtable[Enumerator1GetCurrentRun];
            while (true)
            {
                int hasRun = 0;
                ThrowIfFailed(moveNext(enumerator, &hasRun));
                if (hasRun == 0)
                    return;

                ReferenceColorGlyphRun1* current = null;
                ThrowIfFailed(getCurrentRun(enumerator, &current));
                if (current is null)
                    throw new InvalidOperationException("The DirectWrite enumerator returned a null run.");

                nint brush = foregroundBrush;
                if (current->Base.PaletteIndex != NoPalette)
                {
                    if (_paletteBrush == 0)
                        _paletteBrush = CreatePaletteBrush(current->Base.RunColor);
                    else
                        SetPaletteBrushColor(_paletteBrush, current->Base.RunColor);
                    brush = _paletteBrush;
                }

                DrawTranslatedRun(current, brush);
            }
        }

        private void DrawTranslatedRun(ReferenceColorGlyphRun1* colorRun, nint brush)
        {
            int format = colorRun->GlyphImageFormat;
            Vector2 baseline = new(colorRun->Base.BaselineOriginX, colorRun->Base.BaselineOriginY);
            var vtable = *(nint**)_context4;
            if (format == 0 || (format & ~0x7) == 0)
            {
                var drawGlyphRun = (delegate* unmanaged[Stdcall]<
                    nint,
                    Vector2,
                    ReferenceGlyphRun*,
                    nint,
                    nint,
                    int,
                    void>)vtable[ContextDrawGlyphRun];
                drawGlyphRun(
                    _context4,
                    baseline,
                    &colorRun->Base.GlyphRun,
                    colorRun->Base.GlyphRunDescription,
                    brush,
                    colorRun->MeasuringMode);
                return;
            }

            if (format is 0x10 or 0x20 or 0x40 or 0x80)
            {
                var drawBitmap = (delegate* unmanaged[Stdcall]<
                    nint,
                    int,
                    Vector2,
                    ReferenceGlyphRun*,
                    int,
                    int,
                    void>)vtable[Context4DrawColorBitmapGlyphRun];
                drawBitmap(
                    _context4,
                    format,
                    baseline,
                    &colorRun->Base.GlyphRun,
                    colorRun->MeasuringMode,
                    0);
                return;
            }

            if (format == 0x8)
            {
                var drawSvg = (delegate* unmanaged[Stdcall]<
                    nint,
                    Vector2,
                    ReferenceGlyphRun*,
                    nint,
                    nint,
                    uint,
                    int,
                    void>)vtable[Context4DrawSvgGlyphRun];
                drawSvg(
                    _context4,
                    baseline,
                    &colorRun->Base.GlyphRun,
                    brush,
                    0,
                    0,
                    colorRun->MeasuringMode);
                return;
            }

            throw new NotSupportedException($"The test oracle does not cover legacy glyph format 0x{format:X}.");
        }

        private nint CreatePaletteBrush(ReferenceColor color)
        {
            nint brush = 0;
            var vtable = *(nint**)_context4;
            var create = (delegate* unmanaged[Stdcall]<nint, ReferenceColor*, nint, nint*, int>)vtable[ContextCreateSolidColorBrush];
            int hr = create(_context4, &color, 0, &brush);
            if (hr < 0)
            {
                Release(ref brush);
                Marshal.ThrowExceptionForHR(hr);
            }
            if (brush == 0)
                throw new InvalidOperationException("Direct2D returned a null palette brush.");
            return brush;
        }

        private static void SetPaletteBrushColor(nint brush, ReferenceColor color)
        {
            var vtable = *(nint**)brush;
            var setColor = (delegate* unmanaged[Stdcall]<nint, ReferenceColor*, void>)vtable[SolidColorBrushSetColor];
            setColor(brush, &color);
        }

        private static bool IsTransparent(ICanvasBrush brush)
            => brush.Opacity <= 0f || brush is CanvasSolidColorBrush solid && solid.Color.A == 0;

        private static ReferenceMatrix ToWorldAndDpi(Matrix3x2 transform, float dpi)
        {
            if (!float.IsFinite(dpi) || dpi <= 0f)
                throw new ArgumentOutOfRangeException(nameof(dpi));
            Matrix3x2 transformed = Matrix3x2.Multiply(transform, Matrix3x2.CreateScale(dpi / 96f));
            return new ReferenceMatrix(
                transformed.M11,
                transformed.M12,
                transformed.M21,
                transformed.M22,
                transformed.M31,
                transformed.M32);
        }

        private static nint GetNativeResource(object wrapper, Guid requestedIid)
        {
            if (wrapper is not IWinRTObject winrtObject)
                throw new InvalidOperationException("The Win2D wrapper did not expose its projected WinRT object.");

            nint inspectable = winrtObject.NativeObject.GetRef();
            nint nativeWrapper = 0;
            try
            {
                nativeWrapper = QueryInterface(inspectable, IidCanvasResourceWrapperNative);
                var vtable = *(nint**)nativeWrapper;
                var getResource = (delegate* unmanaged[Stdcall]<nint, nint, float, Guid*, nint*, int>)vtable[3];
                nint resource = 0;
            Guid iid = requestedIid;
            int hr = getResource(nativeWrapper, 0, 0f, &iid, &resource);
                try
                {
                    ThrowIfFailed(hr);
                    if (resource == 0)
                        throw new InvalidOperationException("ICanvasResourceWrapperNative returned a null resource.");
                    return resource;
                }
                catch
                {
                    Release(ref resource);
                    throw;
                }
            }
            finally
            {
                Release(ref nativeWrapper);
                Release(ref inspectable);
            }
        }

        private static nint QueryInterface(nint source, Guid iid)
        {
            nint result = 0;
            var vtable = *(nint**)source;
            var queryInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[0];
            Guid requestedIid = iid;
            int hr = queryInterface(source, &requestedIid, &result);
            try
            {
                ThrowIfFailed(hr);
                if (result == 0)
                    throw new InvalidOperationException("QueryInterface succeeded with a null result.");
                return result;
            }
            catch
            {
                Release(ref result);
                throw;
            }
        }

        private static void ThrowIfFailed(int hr)
        {
            if (hr < 0)
                Marshal.ThrowExceptionForHR(hr);
        }

        private static void Release(ref nint value)
        {
            nint pointer = value;
            value = 0;
            if (pointer == 0)
                return;
            var vtable = *(nint**)pointer;
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtable[2];
            _ = release(pointer);
        }

        [LibraryImport("dwrite.dll", EntryPoint = "DWriteCreateFactory")]
        private static partial int DWriteCreateFactory(uint factoryType, ref Guid iid, out nint factory);

        [StructLayout(LayoutKind.Sequential)]
        private struct ReferenceGlyphOffset(float advanceOffset, float ascenderOffset)
        {
            public float AdvanceOffset = advanceOffset;
            public float AscenderOffset = ascenderOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ReferenceGlyphRun
        {
            public nint FontFace;
            public float FontEmSize;
            public uint GlyphCount;
            public ushort* GlyphIndices;
            public float* GlyphAdvances;
            public ReferenceGlyphOffset* GlyphOffsets;
            public int IsSideways;
            public uint BidiLevel;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ReferenceColorGlyphRun
        {
            public ReferenceGlyphRun GlyphRun;
            public nint GlyphRunDescription;
            public float BaselineOriginX;
            public float BaselineOriginY;
            public ReferenceColor RunColor;
            public ushort PaletteIndex;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ReferenceColorGlyphRun1
        {
            // This test-only declaration follows the SDK's derived-structure
            // inheritance padding and is not shared with the production ABI.
            public ReferenceColorGlyphRun Base;
            public int GlyphImageFormat;
            public int MeasuringMode;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ReferenceColor
        {
            public float Red;
            public float Green;
            public float Blue;
            public float Alpha;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ReferenceMatrix(float m11, float m12, float m21, float m22, float dx, float dy)
        {
            public float M11 = m11;
            public float M12 = m12;
            public float M21 = m21;
            public float M22 = m22;
            public float Dx = dx;
            public float Dy = dy;
        }
    }

    private static Vector2 SnapExpectedBaselineLineNormal(
        Vector2 baseline,
        Matrix3x2 transform,
        float dpi)
    {
        // Independent oracle for the measured DirectWrite contract: disable
        // its pre-callback snapping, translate the baseline, then snap only
        // the line-normal device coordinate before drawing the captured run.
        float pixelsPerDip = dpi / 96f;
        Vector2 transformed = Vector2.Transform(baseline, transform);
        transformed.Y = MathF.Round(
            transformed.Y * pixelsPerDip,
            MidpointRounding.AwayFromZero) / pixelsPerDip;
        if (!Matrix3x2.Invert(transform, out Matrix3x2 inverse))
            throw new InvalidOperationException("The raster oracle requires an invertible drawing transform.");
        return Vector2.Transform(transformed, inverse);
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

    private static void AssertRasterRowsMatch(byte[] actual, byte[] expected, int width)
    {
        Assert.Equal(expected.Length, actual.Length);
        int bytesPerRow = checked(width * 4);
        int rowCount = actual.Length / bytesPerRow;
        bool sawInk = false;
        for (int y = 0; y < rowCount; y++)
        {
            int actualLeft = width;
            int actualRight = -1;
            int expectedLeft = width;
            int expectedRight = -1;
            int rowStart = y * bytesPerRow;
            for (int x = 0; x < width; x++)
            {
                int alphaIndex = rowStart + (x * 4) + 3;
                if (actual[alphaIndex] != 0)
                {
                    actualLeft = NumericMath.Min(actualLeft, x);
                    actualRight = x;
                }

                if (expected[alphaIndex] != 0)
                {
                    expectedLeft = NumericMath.Min(expectedLeft, x);
                    expectedRight = x;
                }
            }

            bool actualHasInk = actualRight >= 0;
            bool expectedHasInk = expectedRight >= 0;
            Assert.True(
                expectedHasInk == actualHasInk,
                $"Ink presence differs at row {y}. Actual row extents: {FormatInkRows(actual, width)}. " +
                $"Expected row extents: {FormatInkRows(expected, width)}.");
            if (!actualHasInk)
                continue;

            sawInk = true;
            Assert.InRange(NumericMath.Abs(actualLeft - expectedLeft), 0, 1);
            Assert.InRange(NumericMath.Abs(actualRight - expectedRight), 0, 1);
        }

        Assert.True(sawInk, "The independent native text-layout raster contained no ink.");
    }

    private static void AssertPixelsMatchWithinRegion(
        byte[] actual,
        byte[] expected,
        int width,
        Rect region)
    {
        Assert.Equal(expected.Length, actual.Length);
        int height = actual.Length / checked(width * 4);
        int startX = NumericMath.Clamp((int)NumericMath.Floor(region.Left) - 1, 0, width);
        int endX = NumericMath.Clamp((int)NumericMath.Ceiling(region.Right) + 1, 0, width);
        int startY = NumericMath.Clamp((int)NumericMath.Floor(region.Top) - 1, 0, height);
        int endY = NumericMath.Clamp((int)NumericMath.Ceiling(region.Bottom) + 1, 0, height);
        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                int pixel = checked((y * width + x) * 4);
                for (int channel = 0; channel < 4; channel++)
                {
                    int delta = NumericMath.Abs(actual[pixel + channel] - expected[pixel + channel]);
                    Assert.True(
                        delta <= 1,
                        $"Raster differs at ({x},{y}) channel {channel}: actual={actual[pixel + channel]}, " +
                        $"expected={expected[pixel + channel]}, delta={delta}. " +
                        $"Actual region bytes={FormatRegionPixels(actual, width, region)}; " +
                        $"expected region bytes={FormatRegionPixels(expected, width, region)}.");
                }
            }
        }
    }

    private static string FormatInkRows(byte[] pixels, int width)
    {
        var rows = new List<string>();
        int bytesPerRow = checked(width * 4);
        for (int y = 0; y < pixels.Length / bytesPerRow; y++)
        {
            int left = width;
            int right = -1;
            for (int x = 0; x < width; x++)
            {
                if (pixels[(y * width + x) * 4 + 3] == 0)
                    continue;
                left = NumericMath.Min(left, x);
                right = x;
            }

            if (right >= 0)
                rows.Add($"{y}:{left}-{right}");
        }

        return string.Join(",", rows);
    }

    private static string FormatRegionPixels(byte[] pixels, int width, Rect region)
    {
        int height = pixels.Length / checked(width * 4);
        int startX = NumericMath.Clamp((int)NumericMath.Floor(region.Left) - 1, 0, width - 1);
        int endX = NumericMath.Clamp((int)NumericMath.Ceiling(region.Right) + 1, 0, width);
        int startY = NumericMath.Clamp((int)NumericMath.Floor(region.Top) - 1, 0, height - 1);
        int endY = NumericMath.Clamp((int)NumericMath.Ceiling(region.Bottom) + 1, 0, height);
        var values = new List<string>();
        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                int pixel = checked((y * width + x) * 4);
                if (pixels[pixel + 3] == 0)
                    continue;
                values.Add($"{x},{y}={pixels[pixel]}/{pixels[pixel + 1]}/{pixels[pixel + 2]}/{pixels[pixel + 3]}");
            }
        }

        return string.Join(";", values);
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

    private static void AssertInkInRegion(byte[] pixels, int width, Rect region)
    {
        int startX = NumericMath.Clamp((int)NumericMath.Floor(region.Left), 0, width - 1);
        int endX = NumericMath.Clamp((int)NumericMath.Ceiling(region.Right), 0, width);
        int startY = NumericMath.Max(0, (int)NumericMath.Floor(region.Top));
        int endY = NumericMath.Max(startY + 1, (int)NumericMath.Ceiling(region.Bottom));
        int pixelHeight = pixels.Length / (width * 4);
        endY = NumericMath.Min(endY, pixelHeight);
        int ink = 0;
        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                if (pixels[(y * width + x) * 4 + 3] != 0)
                    ink++;
            }
        }

        Assert.True(ink > 0, $"Expected rendered ink inside independent region {region}.");
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

    private static LayoutSnapshot BuildSnapshot(
        CanvasDevice device,
        string source,
        FlowDirection flowDirection,
        float availableWidth)
    {
        MarkdownExtensionRegistry registry = new MarkdownExtensionRegistry()
            .ConfigureGitHubReadmeRegistry();
        var pipeline = new MarkdownPipelineBuilder()
            .UseEmphasisExtras(
                EmphasisExtraOptions.Subscript |
                EmphasisExtraOptions.Superscript |
                EmphasisExtraOptions.Strikethrough)
            .Build();
        Markdig.Syntax.MarkdownDocument document = Markdown.Parse(source, pipeline);
        var context = new MarkdownLayoutContext(
            device,
            CreateTheme(),
            new MarkdownSourceMap(source),
            registry,
            flowDirection);
        return new LayoutBuilder(context).Build(document, availableWidth);
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

    private sealed class RasterGlyphRunRecorder : ICanvasTextRenderer
    {
        public List<CapturedGlyphRun> Runs { get; } = [];
        public float Dpi => 96f;
        public bool PixelSnappingDisabled => true;
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
            Runs.Add(new CapturedGlyphRun(
                point,
                fontFace,
                fontSize,
                (CanvasGlyph[])glyphs.Clone(),
                isSideways,
                bidiLevel,
                brush,
                measuringMode,
                localeName,
                textString,
                clusterMapIndices is null ? [] : (int[])clusterMapIndices.Clone(),
                characterIndex));
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

    private sealed record CapturedGlyphRun(
        Vector2 Point,
        CanvasFontFace FontFace,
        float FontSize,
        CanvasGlyph[] Glyphs,
        bool IsSideways,
        uint BidiLevel,
        object? Brush,
        CanvasTextMeasuringMode MeasuringMode,
        string LocaleName,
        string Text,
        int[] ClusterMap,
        uint CharacterIndex);

    private readonly record struct ExpectedScriptRange(int Start, int Length, float Offset);

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

    private static ThemeSnapshot CreateTheme()
    {
        Color black = Color.FromArgb(255, 12, 18, 24);
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = new() { FontFamily = "Segoe UI", FontSize = BodyFontSize, Foreground = black },
            [MarkdownElementKeys.Strong] = new() { FontFamily = "Segoe UI", FontSize = BodyFontSize, FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 }, Foreground = black },
            [MarkdownElementKeys.Emphasis] = new() { FontFamily = "Segoe UI", FontSize = BodyFontSize, FontStyle = Windows.UI.Text.FontStyle.Italic, Foreground = black },
            [MarkdownElementKeys.Link] = new() { FontFamily = "Segoe UI", FontSize = BodyFontSize, Foreground = black, Underline = true },
            [MarkdownElementKeys.Superscript] = new() { FontFamily = "Segoe UI", FontSize = 11f, Foreground = black },
            [MarkdownElementKeys.Subscript] = new() { FontFamily = "Segoe UI", FontSize = 11f, Foreground = black },
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            Color.FromArgb(255, 255, 255, 255),
            transparent,
            black,
            black,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1f);
    }
}
