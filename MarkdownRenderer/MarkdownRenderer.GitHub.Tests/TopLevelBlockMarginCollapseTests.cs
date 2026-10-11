using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class TopLevelBlockMarginCollapseTests
{
    private const float LayoutWidth = 640;

    [Fact]
    public void Apply_CollapsesOnlyFinitePositiveMarginsOutsideSharedSafeHtmlInlineFlows()
    {
        var previous = new FixedHeightBox { Margin = new Thickness(1, -3, 2, 16) };
        var collapsed = new FixedHeightBox { Margin = new Thickness(3, 24, 4, -2) };
        var zero = new FixedHeightBox { Margin = new Thickness(5, 0, 6, 0) };
        var negative = new FixedHeightBox { Margin = new Thickness(7, -8, 9, 0) };
        var largerBottom = new FixedHeightBox { Margin = new Thickness(0, 0, 0, 30) };
        var smallerTop = new FixedHeightBox { Margin = new Thickness(0, 10, 0, 0) };
        var safeHtmlPrevious = new FixedHeightBox
        {
            Margin = new Thickness(0, 0, 0, 16),
            SafeHtmlInlineFlowScopeId = 42,
        };
        var safeHtmlNext = new FixedHeightBox
        {
            Margin = new Thickness(0, 24, 0, 0),
            SafeHtmlInlineFlowScopeId = 42,
        };

        TopLevelBlockMarginCollapse.Apply(
        [
            previous,
            collapsed,
            zero,
            negative,
            largerBottom,
            smallerTop,
            safeHtmlPrevious,
            safeHtmlNext,
        ]);

        Assert.Equal(new Thickness(3, 8, 4, -2), collapsed.Margin);
        Assert.Equal(new Thickness(5, 0, 6, 0), zero.Margin);
        Assert.Equal(new Thickness(7, -8, 9, 0), negative.Margin);
        Assert.Equal(new Thickness(0, 0, 0, 0), smallerTop.Margin);
        Assert.Equal(new Thickness(0, 24, 0, 0), safeHtmlNext.Margin);
        Assert.Equal(new Thickness(1, -3, 2, 16), previous.Margin);
        Assert.Equal(new Thickness(0, 0, 0, 16), safeHtmlPrevious.Margin);
    }

    [Fact]
    public void SharedMarginOverlap_PreservesDoublePrecisionAndRejectsInvalidInputs()
    {
        const double previousBottom = 0.123456789123;
        const double currentTop = 0.876543210987;

        Assert.Equal(
            previousBottom,
            StackBox.GetPositiveCollapsedMargin(previousBottom, currentTop));
        Assert.Equal(0, StackBox.GetPositiveCollapsedMargin(double.PositiveInfinity, 8));
        Assert.Equal(0, StackBox.GetPositiveCollapsedMargin(8, double.NaN));
        Assert.Equal(0, StackBox.GetPositiveCollapsedMargin(-1, 8));
    }

    [Fact]
    public void StylesheetPolicy_IsOptInPreservedByWithRuleAndIncludedInLayoutFingerprint()
    {
        MarkdownStyleSheet profileSheet = GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet;
        MarkdownStyleSheet defaultOff = new(profileSheet.Rules);
        Assert.False(HasPolicy(MarkdownStyleSheet.Empty, 1));
        Assert.False(HasPolicy(MarkdownStyleSheet.Empty, 4));
        Assert.False(HasPolicy(defaultOff, 1));
        Assert.False(HasPolicy(defaultOff, 4));
        Assert.True(HasPolicy(profileSheet, 1));
        Assert.False(HasPolicy(profileSheet, 4));
        Assert.False(HasPolicy(profileSheet, 2));

        MarkdownStyleSheet extended = profileSheet.WithRule(new MarkdownStyleRule(
            new MarkdownStyleSelector(new MarkdownStyleRole("test-extra")),
            new ElementStyleOverride { FontSize = 13 }));
        Assert.True(HasPolicy(extended, 1));
        MarkdownStyleSheet withTrailingReset = extended.WithZeroTrailingTopLevelBlockMargin();
        Assert.True(HasPolicy(withTrailingReset, 1));
        Assert.False(HasPolicy(withTrailingReset, 4));
        Assert.True(HasPolicy(withTrailingReset, 2));
        Assert.False(HasPolicy(extended, 2));
        MarkdownStyleSheet withLeadingReset = extended.WithZeroLeadingTopLevelBlockTopMargin();
        Assert.True(HasPolicy(withLeadingReset, 4));
        Assert.False(HasPolicy(withLeadingReset, 2));

        ThemeSnapshot enabled = CreateTheme(profileSheet);
        ThemeSnapshot disabled = CreateTheme(defaultOff);
        Assert.NotEqual(enabled.LayoutFingerprint, disabled.LayoutFingerprint);
        Assert.NotEqual(
            CreateTheme(withTrailingReset).LayoutFingerprint,
            CreateTheme(extended).LayoutFingerprint);
        Assert.NotEqual(
            CreateTheme(withLeadingReset).LayoutFingerprint,
            CreateTheme(extended).LayoutFingerprint);
    }

    [Fact]
    public void PrimerLightV4_ResetsFirstTopAndLastTopLevelMargins()
    {
        const string source = "## first heading\n\n## final heading";
        using LayoutSnapshot v1 = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet);
        using LayoutSnapshot v2 = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV2.StyleSheet);
        using LayoutSnapshot v3 = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV3.StyleSheet);
        using LayoutSnapshot v4 = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet);

        InlineContainerBox v1First = Assert.IsType<InlineContainerBox>(v1.Blocks[0]);
        InlineContainerBox v2First = Assert.IsType<InlineContainerBox>(v2.Blocks[0]);
        InlineContainerBox v3First = Assert.IsType<InlineContainerBox>(v3.Blocks[0]);
        InlineContainerBox v4First = Assert.IsType<InlineContainerBox>(v4.Blocks[0]);
        InlineContainerBox v1Last = Assert.IsType<InlineContainerBox>(v1.Blocks[1]);
        InlineContainerBox v2Last = Assert.IsType<InlineContainerBox>(v2.Blocks[1]);
        InlineContainerBox v3Last = Assert.IsType<InlineContainerBox>(v3.Blocks[1]);
        InlineContainerBox v4Last = Assert.IsType<InlineContainerBox>(v4.Blocks[1]);

        AssertClose(16, v1First.Margin.Bottom);
        AssertClose(16, v2First.Margin.Bottom);
        AssertClose(16, v3First.Margin.Bottom);
        AssertClose(16, v4First.Margin.Bottom);
        AssertClose(24, v1First.Margin.Top);
        AssertClose(24, v2First.Margin.Top);
        AssertClose(24, v3First.Margin.Top);
        AssertClose(0, v4First.Margin.Top);
        AssertClose(16, v1Last.Margin.Bottom);
        AssertClose(16, v2Last.Margin.Bottom);
        AssertClose(16, v3Last.Margin.Bottom);
        AssertClose(0, v4Last.Margin.Bottom);
    }

    [Fact]
    public void PrimerLightV4_FinalQuoteResetKeepsCollapsedTrailingParagraphMargin()
    {
        using LayoutSnapshot snapshot = Build(
            "> final quote",
            GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet);

        StackBox quote = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
        InlineContainerBox paragraph = Assert.IsType<InlineContainerBox>(Assert.Single(quote.Children));

        AssertClose(0, quote.Margin.Bottom);
        AssertClose(16, paragraph.Margin.Bottom);
        AssertClose(40, quote.Bounds.Height);
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(2d)]
    public void PrimerLightV4_FinalQuoteAccentBarStopsBeforeCollapsedChildMargin(double rasterizationScale)
    {
        using LayoutSnapshot snapshot = Build(
            "> final quote",
            GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet,
            rasterizationScale: rasterizationScale);
        StackBox quote = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
        InlineContainerBox paragraph = Assert.IsType<InlineContainerBox>(Assert.Single(quote.Children));
        Color white = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        Color accent = Assert.IsType<Color>(quote.AccentBar);
        float targetHeight = (float)System.Math.Ceiling(snapshot.Size.Height + 2);
        using var target = new CanvasRenderTarget(
            CanvasDevice.GetSharedDevice(),
            LayoutWidth,
            targetHeight,
            (float)(96 * rasterizationScale));

        using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
        {
            drawingSession.Clear(white);
            quote.Paint(drawingSession, new Rect(0, 0, LayoutWidth, targetHeight));
        }

        int pixelWidth = (int)target.SizeInPixels.Width;
        int sampleX = (int)System.Math.Round((quote.Bounds.X + quote.Margin.Left + 1) * rasterizationScale);
        int chromeBottom = (int)System.Math.Round(
            (quote.Bounds.Bottom - quote.Margin.Bottom - paragraph.Margin.Bottom) * rasterizationScale);
        Color[] pixels = target.GetPixelColors();

        AssertClose(24, quote.Bounds.Height - quote.Margin.Top - quote.Margin.Bottom - paragraph.Margin.Bottom);
        Assert.Equal(accent, pixels[(chromeBottom - 1) * pixelWidth + sampleX]);
        Assert.Equal(white, pixels[chromeBottom * pixelWidth + sampleX]);
    }

    [Theory]
    [InlineData("paragraph\n\n## following heading", "paragraph")]
    [InlineData("```text\ncode\n```\n\n## following heading", "code")]
    [InlineData("| name |\n| --- |\n| item |\n\n## following heading", "table")]
    [InlineData("> quoted text\n\n## following heading", "quote")]
    public void PrimerProfile_CollapsesPositiveMarginsForCommonTopLevelBlockPairs(
        string source,
        string expectedPreviousKind)
    {
        using LayoutSnapshot snapshot = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet);
        BlockBox previous = snapshot.Blocks[0];
        InlineContainerBox following = Assert.IsType<InlineContainerBox>(snapshot.Blocks[1]);

        switch (expectedPreviousKind)
        {
            case "paragraph":
                Assert.IsType<InlineContainerBox>(previous);
                break;
            case "code":
                Assert.IsType<CodeBlockBox>(previous);
                break;
            case "table":
                Assert.IsType<TableBox>(previous);
                break;
            case "quote":
                Assert.IsType<StackBox>(previous);
                break;
        }

        AssertClose(16, previous.Margin.Bottom);
        AssertClose(8, following.Margin.Top);
        AssertClose(24, VisualGap(previous, following));
    }

    [Fact]
    public void DefaultOff_PreservesAdditiveMarginsAndDoesNotMutateParsedBlockStyles()
    {
        MarkdownStyleSheet disabled = new(
            GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet.Rules);
        using LayoutSnapshot snapshot = Build("before\n\n## following", disabled);
        BlockBox previous = snapshot.Blocks[0];
        InlineContainerBox following = Assert.IsType<InlineContainerBox>(snapshot.Blocks[1]);

        AssertClose(16, previous.Margin.Bottom);
        AssertClose(24, following.Margin.Top);
        AssertClose(40, VisualGap(previous, following));
    }

    [Fact]
    public void EagerAndFullyRealizedLazyLayoutHaveIdenticalCollapsedGeometry()
    {
        const string source = "before\n\n## middle\n\n```text\ncode\n```\n\n| a |\n| - |\n| b |";
        MarkdownStyleSheet sheet = GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet;
        using LayoutSnapshot eager = Build(source, sheet);
        using LayoutSnapshot lazy = BuildLazy(source, sheet);

        Assert.True(lazy.IsLazyLayoutEnabled);
        lazy.EnsureMeasuredBand(new LazyLayoutBand(0, 100_000), CancellationToken.None);
        Assert.Equal(eager.Blocks.Count, lazy.Blocks.Count);
        Assert.Equal(eager.Size.Height, lazy.Size.Height, precision: 3);

        for (int index = 0; index < eager.Blocks.Count; index++)
        {
            Assert.Equal(eager.Blocks[index].Bounds.Top, lazy.Blocks[index].Bounds.Top, precision: 3);
            Assert.Equal(eager.Blocks[index].Bounds.Bottom, lazy.Blocks[index].Bounds.Bottom, precision: 3);
            Assert.Equal(eager.Blocks[index].Margin, lazy.Blocks[index].Margin);
        }
    }

    [Fact]
    public void IntrinsicHeightRelayout_PreservesCollapsedGapAndHitSelectionGeometry()
    {
        var preceding = new MutableHeightBox(20)
        {
            BlockIndex = 1,
            Margin = new Thickness(0, 0, 0, 16),
        };
        var following = new MutableHeightBox(30)
        {
            BlockIndex = 2,
            Margin = new Thickness(0, 24, 0, 0),
        };
        var blocks = new List<BlockBox> { preceding, following };
        TopLevelBlockMarginCollapse.Apply(blocks);
        Arrange(blocks, width: 160);

        using var snapshot = new LayoutSnapshot(
            blocks,
            new MarkdownSourceMap("preceding following"),
            width: 160,
            height: (float)(preceding.Bounds.Height + following.Bounds.Height),
            blockSpacing: 0);
        preceding.ContentHeight = 70;
        snapshot.RelayoutChangedBlocks([preceding.BlockIndex], 160, CancellationToken.None);

        AssertClose(8, following.Margin.Top);
        AssertClose(24, VisualGap(preceding, following));
        Assert.Equal(preceding.Bounds.Bottom, following.Bounds.Top);
        Assert.True(snapshot.HitTest(
            new Point(12, following.Bounds.Top + following.Margin.Top + 4),
            out DocumentPosition hit));
        Assert.Equal(following.BlockIndex, hit.BlockIndex);
        Assert.Equal(following.Bounds, Assert.Single(following.GetSelectionRects(
            new DocumentRange(new DocumentPosition(1, 0, 0), new DocumentPosition(2, 0, 1)))));
    }

    [Fact]
    public void TightListAndNestedQuoteGeometryRemainOwnedByTheirContainers()
    {
        const string source = "- first\n- second\n\n> quote\n>\n> next\n\n## ending";
        using LayoutSnapshot snapshot = Build(
            source,
            GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet);

        StackBox list = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        AssertClose(4, list.InterChildSpacing);
        ListItemBox[] items = list.Children.Cast<ListItemBox>().ToArray();
        Assert.Equal(2, items.Length);
        foreach (ListItemBox item in items)
        {
            InlineContainerBox paragraph = Assert.Single(
                item.Content.Children.OfType<InlineContainerBox>());
            AssertClose(0, paragraph.Margin.Top);
            AssertClose(0, paragraph.Margin.Bottom);
        }

        StackBox quote = Assert.IsType<StackBox>(snapshot.Blocks[1]);
        AssertClose(16, quote.Margin.Bottom);
        Assert.Equal(2, quote.Children.Count);
        AssertClose(16, Assert.IsType<InlineContainerBox>(quote.Children[0]).Margin.Bottom);
        AssertClose(0, Assert.IsType<InlineContainerBox>(quote.Children[1]).Margin.Bottom);
        AssertClose(8, Assert.IsType<InlineContainerBox>(snapshot.Blocks[2]).Margin.Top);
    }

    [Fact]
    public void OptInQuoteTailMargin_CollapsesBeforeFollowingList()
    {
        using LayoutSnapshot snapshot = Build(
            "> quoted text\n\n- following item",
            GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet);

        StackBox quote = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        InlineContainerBox paragraph = Assert.IsType<InlineContainerBox>(Assert.Single(quote.Children));
        StackBox list = Assert.IsType<StackBox>(snapshot.Blocks[1]);

        AssertClose(0, paragraph.Margin.Bottom);
        AssertClose(16, quote.Margin.Bottom);
        AssertClose(24, paragraph.Bounds.Height);
        AssertClose(40, quote.Bounds.Height);
        Assert.Equal(quote.Bounds.Bottom, list.Bounds.Top);
        AssertClose(16, VisualGap(quote, list));
    }

    [Fact]
    public void PrimerLightV4_ListBottomMarginIsOptInAndCollapsesWithFollowingParagraph()
    {
        const string source = "- first\n- second\n\nfollowing paragraph";

        using LayoutSnapshot v1 = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet);
        using LayoutSnapshot v2 = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV2.StyleSheet);
        using LayoutSnapshot v3 = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV3.StyleSheet);
        using LayoutSnapshot v4 = Build(source, GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet);

        StackBox v1List = Assert.IsType<StackBox>(v1.Blocks[0]);
        StackBox v2List = Assert.IsType<StackBox>(v2.Blocks[0]);
        StackBox v3List = Assert.IsType<StackBox>(v3.Blocks[0]);
        StackBox v4List = Assert.IsType<StackBox>(v4.Blocks[0]);
        InlineContainerBox v4Paragraph = Assert.IsType<InlineContainerBox>(v4.Blocks[1]);

        AssertClose(0, v1List.Margin.Bottom);
        AssertClose(0, v2List.Margin.Bottom);
        AssertClose(0, v3List.Margin.Bottom);
        AssertClose(16, v4List.Margin.Bottom);
        AssertClose(0, v4Paragraph.Margin.Top);
        AssertClose(16, VisualGap(v4List, v4Paragraph));
    }

    [Fact]
    public void PrimerLightV4_ListMarginUsesTopLevelCollapseWithHeadingMargin()
    {
        using LayoutSnapshot snapshot = Build(
            "- item\n\n## following heading",
            GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet);

        StackBox list = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        InlineContainerBox heading = Assert.IsType<InlineContainerBox>(snapshot.Blocks[1]);

        AssertClose(16, list.Margin.Bottom);
        AssertClose(8, heading.Margin.Top);
        AssertClose(24, VisualGap(list, heading));
    }

    [Theory]
    [InlineData("# heading", 32f, 10.6f)]
    [InlineData("## heading", 24f, 8.2f)]
    public void PrimerLightV4_HeadingRuleReservesCssPaddingInsideMeasuredAndHitTestBounds(
        string source,
        float fontSize,
        float expectedHeightIncrease)
    {
        string sourceWithFollowingBlock = "preceding paragraph\n\n" + source + "\n\nfollowing";
        using LayoutSnapshot v3 = Build(
            sourceWithFollowingBlock,
            GitHubReadmeVisualStyleProfiles.PrimerLightV3.StyleSheet);
        using LayoutSnapshot v4 = Build(
            sourceWithFollowingBlock,
            GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet);
        InlineContainerBox oldHeading = Assert.IsType<InlineContainerBox>(v3.Blocks[1]);
        InlineContainerBox heading = Assert.IsType<InlineContainerBox>(v4.Blocks[1]);

        // A preceding block isolates the heading's own CSS padding/rule band
        // from V4's separate first-child margin reset.
        AssertClose(expectedHeightIncrease, heading.Bounds.Height - oldHeading.Bounds.Height);
        AssertClose(oldHeading.Margin.Top, heading.Margin.Top);
        AssertClose(fontSize * 0.3f + 1f, Resolve(
            GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet,
            source.StartsWith("# ", StringComparison.Ordinal) && !source.StartsWith("## ", StringComparison.Ordinal)
                ? MarkdownStyleRole.Heading1
                : MarkdownStyleRole.Heading2).Padding.Bottom);

        var pointInsideReservedBottomInset = new Point(
            heading.Bounds.Left + 10,
            heading.Bounds.Bottom - heading.Margin.Bottom - 0.5);
        Assert.True(heading.HitTest(pointInsideReservedBottomInset, out DocumentPosition hit));
        Assert.Equal(heading.BlockIndex, hit.BlockIndex);
    }

    [Theory]
    [InlineData(1d)]
    [InlineData(1.5d)]
    [InlineData(2d)]
    public void PrimerLightV4_HeadingMeasurementUsesEffectiveCollapsedAndSuppressedMargins(
        double rasterizationScale)
    {
        MarkdownStyleSheet profile = GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet;
        ElementStyle headingStyle = Resolve(profile, MarkdownStyleRole.Heading2);
        ElementStyle paragraphStyle = Resolve(profile, MarkdownStyleRole.Body);

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

    [Theory]
    [InlineData(1d)]
    [InlineData(2d)]
    public void PrimerLightV4_HeadingRulePaintsAsOnlyABottomDevicePixelStripe(double rasterizationScale)
    {
        using LayoutSnapshot snapshot = Build(
            "# Pixel rule",
            GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet,
            rasterizationScale: rasterizationScale);
        InlineContainerBox heading = Assert.IsType<InlineContainerBox>(Assert.Single(snapshot.Blocks));
        float targetHeight = (float)System.Math.Ceiling(snapshot.Size.Height + 2);
        using var target = new CanvasRenderTarget(
            CanvasDevice.GetSharedDevice(),
            LayoutWidth,
            targetHeight,
            (float)(96 * rasterizationScale));
        Color white = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        Color border = Color.FromArgb(0xFF, 0xD1, 0xD9, 0xE0);

        using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
        {
            drawingSession.Clear(white);
            heading.Paint(drawingSession, new Rect(0, 0, LayoutWidth, targetHeight));
        }

        int pixelWidth = (int)target.SizeInPixels.Width;
        int sampleX = (int)System.Math.Round((LayoutWidth - 10) * rasterizationScale);
        int bottomPixel = (int)System.Math.Round(
            (heading.Bounds.Bottom - heading.Margin.Bottom) * rasterizationScale);
        int strokePixelHeight = (int)System.Math.Round(rasterizationScale);
        Color[] pixels = target.GetPixelColors();
        for (int y = bottomPixel - strokePixelHeight; y < bottomPixel; y++)
            Assert.Equal(border, pixels[y * pixelWidth + sampleX]);

        int topPixel = (int)System.Math.Round((heading.Bounds.Top + heading.Margin.Top + 2) * rasterizationScale);
        int rightEdgeX = pixelWidth - 1;
        Assert.Equal(white, pixels[topPixel * pixelWidth + sampleX]);
        Assert.Equal(white, pixels[topPixel * pixelWidth + rightEdgeX]);
    }

    [Fact]
    public void CustomV4HeadingOutlineIsNotImplicitlyConvertedToBottomRule()
    {
        Color white = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        Color customBorder = Color.FromArgb(0xFF, 0xC0, 0x20, 0x20);
        MarkdownStyleSheet sheet = GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet.WithRule(
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Heading1),
                new ElementStyleOverride { BorderBrush = customBorder, BorderThickness = 1 }));
        using LayoutSnapshot snapshot = Build("# custom heading", sheet);
        InlineContainerBox heading = Assert.IsType<InlineContainerBox>(Assert.Single(snapshot.Blocks));
        heading.DrawContainerChrome = true;
        float targetHeight = (float)System.Math.Ceiling(snapshot.Size.Height + 2);
        using var target = new CanvasRenderTarget(
            CanvasDevice.GetSharedDevice(),
            LayoutWidth,
            targetHeight,
            96);

        using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
        {
            drawingSession.Clear(white);
            heading.Paint(drawingSession, new Rect(0, 0, LayoutWidth, targetHeight));
        }

        int pixelWidth = (int)target.SizeInPixels.Width;
        int sampleX = (int)System.Math.Round(LayoutWidth - 10);
        int topPixel = (int)System.Math.Round(heading.Bounds.Top + heading.Margin.Top);
        Color[] pixels = target.GetPixelColors();

        Assert.False(Resolve(sheet, MarkdownStyleRole.Heading1).HeadingBorderBottomOnly);
        Assert.Equal(customBorder, pixels[topPixel * pixelWidth + sampleX]);
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public void PrimerLightV4_QuoteUsesFourDipAccentBorderOutsideSixteenDipPadding(
        FlowDirection flowDirection)
    {
        using LayoutSnapshot snapshot = Build(
            "> quoted text",
            GitHubReadmeVisualStyleProfiles.PrimerLightV4.StyleSheet,
            flowDirection);
        StackBox quote = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
        InlineContainerBox paragraph = Assert.IsType<InlineContainerBox>(Assert.Single(quote.Children));

        AssertClose(16, quote.ContentPadding.Left);
        AssertClose(16, quote.ContentPadding.Right);
        AssertClose(4, quote.AccentBarThickness);
        Assert.True(quote.AccentBarAffectsContentLayout);
        if (flowDirection == FlowDirection.RightToLeft)
            AssertClose(LayoutWidth - 20, paragraph.Bounds.Right);
        else
            AssertClose(20, paragraph.Bounds.Left);
    }

    [Fact]
    public void PrimerLightV4_QuoteBarGeometryDoesNotChangeOlderProfiles()
    {
        using LayoutSnapshot v1 = Build("> quoted text", GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet);
        using LayoutSnapshot v2 = Build("> quoted text", GitHubReadmeVisualStyleProfiles.PrimerLightV2.StyleSheet);
        using LayoutSnapshot v3 = Build("> quoted text", GitHubReadmeVisualStyleProfiles.PrimerLightV3.StyleSheet);
        StackBox v1Quote = Assert.IsType<StackBox>(Assert.Single(v1.Blocks));
        StackBox v2Quote = Assert.IsType<StackBox>(Assert.Single(v2.Blocks));
        StackBox v3Quote = Assert.IsType<StackBox>(Assert.Single(v3.Blocks));
        InlineContainerBox v1Paragraph = Assert.IsType<InlineContainerBox>(Assert.Single(v1Quote.Children));
        InlineContainerBox v2Paragraph = Assert.IsType<InlineContainerBox>(Assert.Single(v2Quote.Children));
        InlineContainerBox v3Paragraph = Assert.IsType<InlineContainerBox>(Assert.Single(v3Quote.Children));

        AssertClose(3, v1Quote.AccentBarThickness);
        AssertClose(3, v2Quote.AccentBarThickness);
        AssertClose(3, v3Quote.AccentBarThickness);
        Assert.False(v1Quote.AccentBarAffectsContentLayout);
        Assert.False(v2Quote.AccentBarAffectsContentLayout);
        Assert.False(v3Quote.AccentBarAffectsContentLayout);
        AssertClose(16, v1Paragraph.Bounds.Left);
        AssertClose(16, v2Paragraph.Bounds.Left);
        AssertClose(16, v3Paragraph.Bounds.Left);
    }

    [Fact]
    public void OptInQuoteTailMargin_CollapsesFinalMarkdownImageParagraphBeforeFollowingList()
    {
        using LayoutSnapshot snapshot = Build(
            "> ![](https://example.test/quote-divider.png)\n\n- following item",
            GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet);

        StackBox quote = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        ImageBox imageParagraph = Assert.IsType<ImageBox>(Assert.Single(quote.Children));
        StackBox list = Assert.IsType<StackBox>(snapshot.Blocks[1]);

        AssertClose(0, imageParagraph.Margin.Bottom);
        AssertClose(16, quote.Margin.Bottom);
        AssertClose(32, imageParagraph.Bounds.Height);
        AssertClose(48, quote.Bounds.Height);
        Assert.Equal(quote.Bounds.Bottom, list.Bounds.Top);
    }

    [Fact]
    public void OptInQuoteTailMargin_PreservesInternalMultiParagraphSpacing()
    {
        using LayoutSnapshot snapshot = Build(
            "> first paragraph\n>\n> final paragraph\n\n- following item",
            GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet);

        StackBox quote = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        InlineContainerBox first = Assert.IsType<InlineContainerBox>(quote.Children[0]);
        InlineContainerBox last = Assert.IsType<InlineContainerBox>(quote.Children[1]);
        StackBox list = Assert.IsType<StackBox>(snapshot.Blocks[1]);

        AssertClose(16, first.Margin.Bottom);
        AssertClose(0, last.Margin.Bottom);
        AssertClose(24, first.Bounds.Height - first.Margin.Bottom);
        AssertClose(24, last.Bounds.Height);
        AssertClose(80, quote.Bounds.Height);
        Assert.Equal(quote.Bounds.Bottom, list.Bounds.Top);
    }

    [Fact]
    public void OptInQuoteTailMargin_DoesNotCollapseAcrossVerticalPaddingOrBorder()
    {
        MarkdownStyleSheet padded = GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet.WithRule(
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Quote),
                new ElementStyleOverride { Padding = new Thickness(16, 0, 16, 4) }));
        using LayoutSnapshot paddedSnapshot = Build("> quoted text", padded);
        StackBox paddedQuote = Assert.IsType<StackBox>(Assert.Single(paddedSnapshot.Blocks));
        InlineContainerBox paddedParagraph = Assert.IsType<InlineContainerBox>(Assert.Single(paddedQuote.Children));

        AssertClose(4, paddedQuote.ContentPadding.Bottom);
        AssertClose(16, paddedParagraph.Margin.Bottom);
        AssertClose(60, paddedQuote.Bounds.Height);

        MarkdownStyleSheet bordered = GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet.WithRule(
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Quote),
                new ElementStyleOverride
                {
                    BorderBrush = Color.FromArgb(0xFF, 0x1F, 0x23, 0x28),
                    BorderThickness = 1,
                }));
        using LayoutSnapshot borderedSnapshot = Build("> quoted text", bordered);
        StackBox borderedQuote = Assert.IsType<StackBox>(Assert.Single(borderedSnapshot.Blocks));
        InlineContainerBox borderedParagraph = Assert.IsType<InlineContainerBox>(Assert.Single(borderedQuote.Children));

        AssertClose(1, borderedQuote.BorderThickness);
        AssertClose(16, borderedParagraph.Margin.Bottom);
    }

    [Fact]
    public void OptInQuoteTailMargin_CollapsesUnequalPositiveMarginsToTheirMaximum()
    {
        MarkdownStyleSheet sheet = GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet
            .WithRule(new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Body),
                new ElementStyleOverride { Margin = new Thickness(0, 0, 0, 24) }))
            .WithRule(new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Quote),
                new ElementStyleOverride { Margin = new Thickness(0, 0, 0, 8) }));
        using LayoutSnapshot snapshot = Build("> quoted text", sheet);

        StackBox quote = Assert.IsType<StackBox>(Assert.Single(snapshot.Blocks));
        InlineContainerBox paragraph = Assert.IsType<InlineContainerBox>(Assert.Single(quote.Children));

        AssertClose(16, paragraph.Margin.Bottom);
        AssertClose(8, quote.Margin.Bottom);
        AssertClose(48, quote.Bounds.Height);

        using LayoutSnapshot imageSnapshot = Build(
            "> ![](https://example.test/quote-divider.png)",
            sheet);
        StackBox imageQuote = Assert.IsType<StackBox>(Assert.Single(imageSnapshot.Blocks));
        ImageBox imageParagraph = Assert.IsType<ImageBox>(Assert.Single(imageQuote.Children));

        AssertClose(16, imageParagraph.Margin.Bottom);
        AssertClose(8, imageQuote.Margin.Bottom);
        AssertClose(56, imageQuote.Bounds.Height);
    }

    [Fact]
    public void QuoteTailMarginCollapse_RemainsDisabledWithoutOptInPolicy()
    {
        MarkdownStyleSheet additiveProfile = new(
            GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet.Rules);
        using LayoutSnapshot snapshot = Build("> quoted text\n\n- following item", additiveProfile);

        StackBox quote = Assert.IsType<StackBox>(snapshot.Blocks[0]);
        InlineContainerBox paragraph = Assert.IsType<InlineContainerBox>(Assert.Single(quote.Children));

        AssertClose(16, paragraph.Margin.Bottom);
        AssertClose(16, quote.Margin.Bottom);
        AssertClose(56, quote.Bounds.Height);
    }

    [Fact]
    public void SelectionAndHitTestingRemainAlignedAfterProfileCollapse()
    {
        using LayoutSnapshot snapshot = Build(
            "before\n\n## selected heading",
            GitHubReadmeVisualStyleProfiles.PrimerLightV1.StyleSheet);
        BlockBox previous = snapshot.Blocks[0];
        InlineContainerBox heading = Assert.IsType<InlineContainerBox>(snapshot.Blocks[1]);
        var point = new Point(
            heading.Bounds.Left + 12,
            heading.Bounds.Top + heading.Margin.Top + 8);

        Assert.True(snapshot.HitTest(point, out DocumentPosition hit));
        Assert.Equal(heading.BlockIndex, hit.BlockIndex);
        Rect[] selection = heading.GetSelectionRects(new DocumentRange(
            new DocumentPosition(previous.BlockIndex, 0, 0),
            new DocumentPosition(heading.BlockIndex, 0, "selected heading".Length))).ToArray();
        Assert.NotEmpty(selection);
        Assert.All(selection, rect => Assert.True(rect.Top >= heading.Bounds.Top));
        AssertClose(24, VisualGap(previous, heading));
    }

    private static LayoutSnapshot Build(
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

    private static LayoutSnapshot BuildLazy(string source, MarkdownStyleSheet sheet)
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

    private static ThemeSnapshot CreateTheme(MarkdownStyleSheet sheet)
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

    private static bool HasPolicy(MarkdownStyleSheet sheet, byte flag)
        => (sheet.Compile().LayoutPolicy & flag) != 0;

    private static void Arrange(IReadOnlyList<BlockBox> blocks, float width)
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

    private static double VisualGap(BlockBox previous, BlockBox current)
        => current.Bounds.Top + current.Margin.Top -
           (previous.Bounds.Bottom - previous.Margin.Bottom);

    private static Color[] PaintHeadingPixels(InlineContainerBox heading, double rasterizationScale)
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

    private static ElementStyle Resolve(MarkdownStyleSheet sheet, MarkdownStyleRole role)
        => sheet.Resolve(new ElementStyle(), new MarkdownStyleContext(role));

    private static void AssertClose(double expected, double actual)
        => Assert.InRange(actual, expected - 0.01, expected + 0.01);

    private sealed class FixedHeightBox : BlockBox
    {
        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(0, 0, availableWidth, 1);
            return 1;
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }

    private sealed class MutableHeightBox(float contentHeight) : BlockBox
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
