using System.Numerics;
using NumericMath = global::System.Math;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Builds and renders the script layouts that the script baseline tests
/// check, computes their expected ranges, offsets and regions, and asserts
/// range geometry and WARP color glyph support.
/// </summary>
internal static class ScriptBaselineFixture
{
    internal const float AvailableWidth = 124f;

    internal const float BodyFontSize = 16f;

    internal static void AssertWarpColorGlyphSupport(
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

    internal static void AssertRangeGeometry(
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

    internal static Rect ExpectedRegionRect(
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

    internal static Rect ExpectedRunBounds(
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

    internal static Rect Offset(Rect rect, double offsetY)
        => new(rect.X, rect.Y + offsetY, rect.Width, rect.Height);

    internal static double ExpectedTextOriginY(InlineContainerBox box, float expectedTopInset)
    {
        double unsnappedY = box.Bounds.Y + box.Margin.Top + expectedTopInset;
        double independentlySnappedY = NumericMath.Round(unsnappedY, MidpointRounding.AwayFromZero);
        Assert.InRange(NumericMath.Abs(box.GetTextOriginY() - independentlySnappedY), 0, 0.02);
        return independentlySnappedY;
    }

    internal static DocumentRange RangeFor(InlineContainerBox box, InlineRun run)
        => new(
            new DocumentPosition(box.BlockIndex, run.InlineIndex, 0),
            new DocumentPosition(box.BlockIndex, run.InlineIndex, run.TextLength));

    internal static int GetBufferStart(InlineContainerBox box, InlineRun expected)
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

    internal static byte[] Render(InlineContainerBox box, CanvasDevice device, float width)
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

    internal static byte[] RenderSelection(
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

    internal static byte[] RenderNativeLayout(
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

    internal static Vector2 SnapExpectedBaselineLineNormal(
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

    internal static LayoutSnapshot BuildSnapshot(
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

    internal static ThemeSnapshot CreateTheme()
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
