using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.SafeHtmlRenderingFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.SafeHtml;

/// <summary>
/// Checks how HTML alignment scopes such as a centered div that is split
/// across HTML blocks apply to their own content, nested headings and
/// following Markdown.
/// </summary>
public sealed class SafeHtmlAlignmentScopeTests
{
    [Fact]
    public void SplitCenteredDivScopeAlignsClosingHtmlBlockButNotFollowingMarkdown()
    {
        const string source = """
            <div align="center">

            <h3>Inside centered scope</h3></div>

            Outside paragraph.
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox heading = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Heading3 &&
                          box.Runs.Any(run => run.Text == "Inside centered scope"));
        InlineContainerBox outsideParagraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Body &&
                          box.Runs.Any(run => run.Text == "Outside paragraph."));

        Assert.Equal(CanvasHorizontalAlignment.Center, heading.TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Left, outsideParagraph.TextAlignment);
    }

    [Fact]
    public void SplitCenteredDivScopeDoesNotOverrideNestedHeadingAlignment()
    {
        const string source = """
            <div align="center">

            <h3 align="right">Nested right heading</h3></div>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox heading = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Heading3 &&
                          box.Runs.Any(run => run.Text == "Nested right heading"));

        Assert.Equal(CanvasHorizontalAlignment.Right, heading.TextAlignment);
    }

    [Fact]
    public void SplitCenteredDivScopeKeepsLocalAndInheritedParagraphAlignments()
    {
        const string source = """
            <div align="center">

            <p align="right">Right paragraph</p><p>Inherited centered paragraph</p></div>

            Outside paragraph.
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox rightParagraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Body &&
                          box.Runs.Any(run => run.Text == "Right paragraph"));
        InlineContainerBox inheritedParagraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Body &&
                          box.Runs.Any(run => run.Text == "Inherited centered paragraph"));
        InlineContainerBox outsideParagraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Body &&
                          box.Runs.Any(run => run.Text == "Outside paragraph."));

        Assert.Equal(CanvasHorizontalAlignment.Right, rightParagraph.TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Center, inheritedParagraph.TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Left, outsideParagraph.TextAlignment);
    }

    [Fact]
    public void SplitInlineFragmentsInCenteredDivKeepBadgeRowsTogetherAndIgnoreCommentBoxes()
    {
        const string source = """
            <div align="center">

            <a><img src="https://example.test/first-row.png" width="40" height="20" alt="first row"></a>

            <!-- a source-only separator between badge rows -->

            <br><a><img src="https://example.test/second-row.png" width="40" height="20" alt="second row"></a>

            </div>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox firstRow = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/first-row.png", StringComparison.Ordinal)));
        InlineContainerBox secondRow = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/second-row.png", StringComparison.Ordinal)));
        Rect firstImage = Assert.Single(firstRow.EnumerateInlineImageRects()).Rect;
        Rect secondImage = Assert.Single(secondRow.EnumerateInlineImageRects()).Rect;

        Assert.True(StackBox.SharesSafeHtmlInlineFlow(firstRow, secondRow));
        Assert.True(
            secondImage.Top - firstImage.Bottom <= 6,
            $"Adjacent badge rows in one HTML flow are separated by {secondImage.Top - firstImage.Bottom:F2} DIPs.");
        Assert.DoesNotContain(snapshot.Blocks, static block => block is StackBox { Children.Count: 0 });
    }

    [Fact]
    public void SeparateHtmlParagraphsKeepTheirBlockSpacingInsidePersistentDiv()
    {
        const string source = """
            <div align="center">

            <p>First paragraph</p>

            <p>Second paragraph</p>

            </div>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(
            source,
            new ElementStyle { Margin = new Thickness(0, 0, 0, 8) });
        InlineContainerBox firstParagraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.Any(static run => run.Text == "First paragraph"));
        InlineContainerBox secondParagraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.Any(static run => run.Text == "Second paragraph"));
        InlineRun firstText = Assert.Single(firstParagraph.Runs, static run => run.Text == "First paragraph");
        InlineRun secondText = Assert.Single(secondParagraph.Runs, static run => run.Text == "Second paragraph");
        Rect firstTextBounds = firstParagraph.GetRunRect(firstText.InlineIndex);
        Rect secondTextBounds = secondParagraph.GetRunRect(secondText.InlineIndex);
        double visibleTextGap = secondTextBounds.Top - firstTextBounds.Bottom;

        Assert.False(StackBox.SharesSafeHtmlInlineFlow(firstParagraph, secondParagraph));
        Assert.True(
            visibleTextGap >= 7.5,
            $"Distinct HTML paragraph blocks must retain their normal spacing; the visible text gap was {visibleTextGap:F2} DIPs.");
    }

    [Fact]
    public void InlineDivAndFirstBadgeRowInSameHtmlBlockJoinFollowingFragment()
    {
        const string source = """
            <div align="center"><a><img src="https://example.test/same-block-first.png" width="40" height="20" alt="first"></a>

            <br><a><img src="https://example.test/same-block-second.png" width="40" height="20" alt="second"></a>

            </div>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox firstRow = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/same-block-first.png", StringComparison.Ordinal)));
        InlineContainerBox secondRow = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/same-block-second.png", StringComparison.Ordinal)));
        Rect firstImage = Assert.Single(firstRow.EnumerateInlineImageRects()).Rect;
        Rect secondImage = Assert.Single(secondRow.EnumerateInlineImageRects()).Rect;
        BlockBox firstFlowBlock = Assert.Single(
            snapshot.Blocks,
            static block => FlattenRuns(block).OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/same-block-first.png", StringComparison.Ordinal)));
        BlockBox secondFlowBlock = Assert.Single(
            snapshot.Blocks,
            static block => FlattenRuns(block).OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/same-block-second.png", StringComparison.Ordinal)));

        Assert.True(StackBox.SharesSafeHtmlInlineFlow(firstFlowBlock, secondFlowBlock));
        Assert.True(
            secondImage.Top - firstImage.Bottom <= 6,
            "An inline div opener and its first row in one HtmlBlock must join its next split fragment.");
    }

    [Fact]
    public void ClosingDivBeforeInlineContentDoesNotReuseThePreviousFlowMarker()
    {
        const string source = """
            <div align="center">

            <a><img src="https://example.test/inside.png" width="40" height="20" alt="inside"></a>

            </div><br><a><img src="https://example.test/outside.png" width="40" height="20" alt="outside"></a>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox inside = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/inside.png", StringComparison.Ordinal)));
        InlineContainerBox outside = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url.EndsWith("/outside.png", StringComparison.Ordinal)));

        Assert.NotNull(inside.SafeHtmlInlineFlowScopeId);
        Assert.Null(outside.SafeHtmlInlineFlowScopeId);
        Assert.False(StackBox.SharesSafeHtmlInlineFlow(inside, outside));
    }

    [Fact]
    public void MarkdownEnabledHtmlContainerInheritsAlignmentAtChildEntry()
    {
        const string source = """
            <div align="center" markdown="1">
            ## Centered nested heading

            Centered nested paragraph.

            - Centered nested list item
            </div>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox heading = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Heading2 &&
                          box.Runs.Any(static run => run.Text == "Centered nested heading"));
        InlineContainerBox paragraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.Any(static run => run.Text == "Centered nested paragraph."));

        Assert.Equal(CanvasHorizontalAlignment.Center, heading.TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Center, paragraph.TextAlignment);
    }

    [Fact]
    public void PersistentCenteredHtmlBlockKeepsNestedRightHeadingAndCenteredSibling()
    {
        const string source = """
            <div align="center">
            <h3 align="right">Persistent nested right heading</h3>
            <p>Persistent centered sibling</p>
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox heading = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Heading3 &&
                          box.Runs.Any(run => run.Text == "Persistent nested right heading"));
        InlineContainerBox paragraph = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Body &&
                          box.Runs.Any(run => run.Text == "Persistent centered sibling"));

        Assert.Equal(CanvasHorizontalAlignment.Right, heading.TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Center, paragraph.TextAlignment);
    }
}
