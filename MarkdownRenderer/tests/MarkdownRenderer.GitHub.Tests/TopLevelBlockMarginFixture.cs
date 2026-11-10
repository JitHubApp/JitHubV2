using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Building;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Builds the top-level block layouts, test boxes and heading paint captures
/// that <see cref="TopLevelBlockMarginCollapseTests"/> measures, and holds its
/// PrimerLightV4 heading margin assertions.
/// </summary>
internal static class TopLevelBlockMarginFixture
{
    internal const float LayoutWidth = 640;

    /// <summary>Asserts that a collapsed PrimerLightV4 heading measures, flows and paints like authored equivalent spacing.</summary>
    internal static void AssertCollapsedHeadingMatchesEquivalentSpacing(
        MarkdownStyleSheet profile,
        ElementStyle headingStyle,
        ElementStyle paragraphStyle,
        double rasterizationScale)
    {
        // This is the equivalent authored spacing without a collapsed margin:
        // previous bottom 0 + heading top 8 produces the same effective top
        // margin as previous bottom 16 + authored heading top 24.
        MarkdownStyleSheet equivalentCollapsedSpacing = profile
            .WithRule(new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Body),
                new ElementStyleOverride
                {
                    Margin = new Thickness(
                        paragraphStyle.Margin.Left,
                        paragraphStyle.Margin.Top,
                        paragraphStyle.Margin.Right,
                        0),
                }))
            .WithRule(new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Heading2),
                new ElementStyleOverride
                {
                    Margin = new Thickness(
                        headingStyle.Margin.Left,
                        8,
                        headingStyle.Margin.Right,
                        headingStyle.Margin.Bottom),
                }));

        using LayoutSnapshot collapsed = Build(
            "preceding paragraph\n\n## heading\n\nfollowing",
            profile,
            rasterizationScale: rasterizationScale);
        using LayoutSnapshot equivalent = Build(
            "preceding paragraph\n\n## heading\n\nfollowing",
            equivalentCollapsedSpacing,
            rasterizationScale: rasterizationScale);

        InlineContainerBox collapsedHeading = Assert.IsType<InlineContainerBox>(collapsed.Blocks[1]);
        InlineContainerBox equivalentHeading = Assert.IsType<InlineContainerBox>(equivalent.Blocks[1]);
        InlineContainerBox collapsedFollowing = Assert.IsType<InlineContainerBox>(collapsed.Blocks[2]);
        InlineContainerBox equivalentFollowing = Assert.IsType<InlineContainerBox>(equivalent.Blocks[2]);

        AssertClose(24, collapsedHeading.RawPlacementMargin.Top);
        AssertClose(16, collapsedHeading.CollapsedTopMargin);
        AssertClose(8, collapsedHeading.Margin.Top);
        AssertClose(8, equivalentHeading.RawPlacementMargin.Top);
        AssertClose(0, equivalentHeading.CollapsedTopMargin);
        AssertClose(8, equivalentHeading.Margin.Top);

        // Measurement, rule position, and downstream layout must depend on
        // the effective margin, not the authored 24-DIP value that was
        // already collapsed against the preceding paragraph.
        AssertClose(equivalentHeading.Bounds.Height, collapsedHeading.Bounds.Height);
        AssertClose(
            equivalentFollowing.Bounds.Top - equivalentHeading.Bounds.Top,
            collapsedFollowing.Bounds.Top - collapsedHeading.Bounds.Top);
        // The only intentional whole-document delta is the preceding
        // paragraph's authored bottom margin (16 DIPs) removed by the control
        // stylesheet in the equivalent fixture.
        AssertClose(16, collapsed.Size.Height - equivalent.Size.Height);
        Assert.Equal(
            PaintHeadingPixels(equivalentHeading, rasterizationScale),
            PaintHeadingPixels(collapsedHeading, rasterizationScale));
    }

    /// <summary>Asserts that a suppressed leading heading top margin behaves like an authored zero top margin.</summary>
    internal static void AssertSuppressedLeadingHeadingMatchesZeroTopMargin(
        MarkdownStyleSheet profile,
        ElementStyle headingStyle,
        double rasterizationScale)
    {
        MarkdownStyleSheet noLeadingTopMargin = profile.WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.Heading2),
            new ElementStyleOverride
            {
                Margin = new Thickness(
                    headingStyle.Margin.Left,
                    0,
                    headingStyle.Margin.Right,
                    headingStyle.Margin.Bottom),
            }));
        using LayoutSnapshot leadingSuppressed = Build(
            "## heading\n\nfollowing",
            profile,
            rasterizationScale: rasterizationScale);
        using LayoutSnapshot leadingZero = Build(
            "## heading\n\nfollowing",
            noLeadingTopMargin,
            rasterizationScale: rasterizationScale);
        InlineContainerBox suppressedHeading = Assert.IsType<InlineContainerBox>(leadingSuppressed.Blocks[0]);
        InlineContainerBox zeroMarginHeading = Assert.IsType<InlineContainerBox>(leadingZero.Blocks[0]);
        InlineContainerBox suppressedFollowing = Assert.IsType<InlineContainerBox>(leadingSuppressed.Blocks[1]);
        InlineContainerBox zeroMarginFollowing = Assert.IsType<InlineContainerBox>(leadingZero.Blocks[1]);

        Assert.True(suppressedHeading.SuppressTopMargin);
        AssertClose(24, suppressedHeading.RawPlacementMargin.Top);
        AssertClose(0, suppressedHeading.Margin.Top);
        AssertClose(0, zeroMarginHeading.RawPlacementMargin.Top);
        AssertClose(suppressedHeading.Bounds.Height, zeroMarginHeading.Bounds.Height);
        AssertClose(
            zeroMarginFollowing.Bounds.Top - zeroMarginHeading.Bounds.Top,
            suppressedFollowing.Bounds.Top - suppressedHeading.Bounds.Top);
        AssertClose(leadingZero.Size.Height, leadingSuppressed.Size.Height);
        Assert.Equal(
            PaintHeadingPixels(zeroMarginHeading, rasterizationScale),
            PaintHeadingPixels(suppressedHeading, rasterizationScale));
    }

    /// <summary>Asserts that a suppressed trailing heading bottom margin behaves like authored zero outer margins.</summary>
    internal static void AssertSuppressedTrailingHeadingMatchesZeroOuterMargins(
        MarkdownStyleSheet profile,
        double rasterizationScale)
    {
        MarkdownStyleSheet noOuterMargins = profile.WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(MarkdownStyleRole.Heading2),
            new ElementStyleOverride
            {
                Margin = new Thickness(0),
            }));
        using LayoutSnapshot trailingSuppressed = Build(
            "## heading",
            profile,
            rasterizationScale: rasterizationScale);
        using LayoutSnapshot trailingZero = Build(
            "## heading",
            noOuterMargins,
            rasterizationScale: rasterizationScale);
        InlineContainerBox suppressedTrailingHeading = Assert.IsType<InlineContainerBox>(
            Assert.Single(trailingSuppressed.Blocks));
        InlineContainerBox zeroOuterMarginHeading = Assert.IsType<InlineContainerBox>(
            Assert.Single(trailingZero.Blocks));

        Assert.True(suppressedTrailingHeading.SuppressBottomMargin);
        AssertClose(16, suppressedTrailingHeading.RawPlacementMargin.Bottom);
        AssertClose(0, suppressedTrailingHeading.Margin.Bottom);
        AssertClose(0, zeroOuterMarginHeading.RawPlacementMargin.Bottom);
        AssertClose(suppressedTrailingHeading.Bounds.Height, zeroOuterMarginHeading.Bounds.Height);
        AssertClose(trailingZero.Size.Height, trailingSuppressed.Size.Height);
        Assert.Equal(
            PaintHeadingPixels(zeroOuterMarginHeading, rasterizationScale),
            PaintHeadingPixels(suppressedTrailingHeading, rasterizationScale));
    }

    internal static LayoutSnapshot Build(
        string source,
        MarkdownStyleSheet sheet,
        FlowDirection flowDirection = FlowDirection.LeftToRight,
        double rasterizationScale = 1)
    {
        var registry = new MarkdownExtensionRegistry();
        registry.ConfigureGfmRegistry();
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(source, registry.BuildPipeline());
        var document = MarkdownDocument.FromParsed(source, parsed);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            CreateTheme(sheet),
            new MarkdownSourceMap(source),
            registry,
            flowDirection)
        {
            RasterizationScale = rasterizationScale,
        };
        return new LayoutBuilder(context, semanticDocument: document).Build(parsed, LayoutWidth);
    }

    internal static LayoutSnapshot BuildLazy(string source, MarkdownStyleSheet sheet)
    {
        var registry = new MarkdownExtensionRegistry();
        registry.ConfigureGfmRegistry();
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(source, registry.BuildPipeline());
        var document = MarkdownDocument.FromParsed(source, parsed);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            CreateTheme(sheet),
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);
        return new LayoutBuilder(context, semanticDocument: document).BuildLazy(
            parsed,
            LayoutWidth,
            viewportTop: 0,
            viewportHeight: 48,
            overscan: 0,
            CancellationToken.None);
    }

    internal static ThemeSnapshot CreateTheme(MarkdownStyleSheet sheet)
    {
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            new Dictionary<string, ElementStyle>(),
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1,
            styleSheet: sheet);
    }

    internal static bool HasPolicy(MarkdownStyleSheet sheet, byte flag)
        => (sheet.Compile().LayoutPolicy & flag) != 0;

    internal static void Arrange(IReadOnlyList<BlockBox> blocks, float width)
    {
        float y = 0;
        for (int index = 0; index < blocks.Count; index++)
        {
            BlockBox block = blocks[index];
            float height = block.Measure(width);
            block.Arrange(0, y, width);
            y += height;
        }
    }

    internal static double VisualGap(BlockBox previous, BlockBox current)
        => current.Bounds.Top + current.Margin.Top -
           (previous.Bounds.Bottom - previous.Margin.Bottom);

    internal static Color[] PaintHeadingPixels(InlineContainerBox heading, double rasterizationScale)
    {
        float targetHeight = (float)System.Math.Ceiling(heading.Bounds.Height + 2);
        using var target = new CanvasRenderTarget(
            CanvasDevice.GetSharedDevice(),
            LayoutWidth,
            targetHeight,
            (float)(96 * rasterizationScale));
        Color white = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
        {
            drawingSession.Clear(white);
            drawingSession.Transform = System.Numerics.Matrix3x2.CreateTranslation(
                (float)-heading.Bounds.X,
                (float)-heading.Bounds.Y);
            heading.Paint(
                drawingSession,
                new Rect(heading.Bounds.X, heading.Bounds.Y, LayoutWidth, targetHeight));
        }

        return target.GetPixelColors();
    }

    internal static ElementStyle Resolve(MarkdownStyleSheet sheet, MarkdownStyleRole role)
        => sheet.Resolve(new ElementStyle(), new MarkdownStyleContext(role));

    internal static void AssertClose(double expected, double actual)
        => Assert.InRange(actual, expected - 0.01, expected + 0.01);

    internal sealed class FixedHeightBox : BlockBox
    {
        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, 1);
            return 1;
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }

    internal sealed class MutableHeightBox(float contentHeight) : BlockBox
    {
        public float ContentHeight { get; set; } = contentHeight;

        public override float Measure(float availableWidth)
        {
            float height = ContentHeight + (float)(Margin.Top + Margin.Bottom);
            Bounds = new Rect(0, 0, availableWidth, height);
            return height;
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }
}
