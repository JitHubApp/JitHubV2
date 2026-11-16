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
    public void TypoedParagraphClosersInAHeaderDoNotCenterTheRestOfTheDocument()
    {
        // mermaid-js/mermaid's README header, verbatim: the two bare `<p>` lines
        // were meant to be `</p>`. HTML closes an open paragraph when another
        // paragraph starts, so only the header itself is centered.
        const string source = """
            <p align="center">
            <img src="https://raw.githubusercontent.com/mermaid-js/mermaid/develop/docs/public/favicon.svg" height="150">
            </p>
            <h1 align="center">
            Mermaid
            </h1>
            <p align="center">
            Generate diagrams from Markdown-like text.
            <p>
            <p align="center">
              <a href="https://www.npmjs.com/package/mermaid"><img src="https://img.shields.io/npm/v/mermaid?color=ff3670&label="></a>
            <p>

            <p align="center">
            <a href="https://mermaid.ai/live/"><b>Live Editor!</b></a>
            </p>

            <br>
            <br>

            [![NPM](https://img.shields.io/npm/v/mermaid)](https://www.npmjs.com/package/mermaid)

            ## About

            Mermaid is a JavaScript-based diagramming and charting tool.
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);
        InlineContainerBox tagline = FindTextBox(snapshot, "Generate diagrams from Markdown-like text.");
        InlineContainerBox liveEditor = FindTextBox(snapshot, "Live Editor!");
        InlineContainerBox badge = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.Runs.OfType<InlineImageRun>()
                .Any(static image => image.Url == "https://img.shields.io/npm/v/mermaid"));
        InlineContainerBox heading = Assert.Single(
            FlattenBoxes(snapshot).OfType<InlineContainerBox>(),
            static box => box.ElementKey == MarkdownElementKeys.Heading2 &&
                          box.Runs.Any(static run => run.Text == "About"));
        InlineContainerBox body = FindTextBox(snapshot, "Mermaid is a JavaScript-based diagramming and charting tool.");

        Assert.Equal(CanvasHorizontalAlignment.Center, tagline.TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Center, liveEditor.TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Left, badge.TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Left, heading.TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Left, body.TextAlignment);
    }

    [Fact]
    public void UnclosedCenteredParagraphEndsBeforeTheFollowingMarkdownBlocks()
    {
        // Every Markdown block is an HTML block-level element, which closes an
        // open paragraph, so an unclosed `<p align>` cannot reach past its block.
        const string source = """
            <p align="center">
            Centered tagline

            ## Heading after the paragraph

            Body paragraph.

            - List item
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);

        Assert.Equal(CanvasHorizontalAlignment.Center, FindTextBox(snapshot, "Centered tagline").TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Left, FindTextBox(snapshot, "Heading after the paragraph").TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Left, FindTextBox(snapshot, "Body paragraph.").TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Left, FindTextBox(snapshot, "List item").TextAlignment);
    }

    [Fact]
    public void CenteredDivKeepsCenteringMarkdownAfterAnUnclosedParagraphInsideIt()
    {
        // The implicit paragraph closure ends only the paragraph: the div that
        // contains it, and its Markdown, stay centered until `</div>`.
        const string source = """
            <div align="center">
            <p align="right">
            Right tagline

            ## Centered heading in the div

            Centered paragraph in the div.

            </div>

            Outside paragraph.
            """;

        using LayoutSnapshot snapshot = BuildGitHub(source);

        Assert.Equal(CanvasHorizontalAlignment.Right, FindTextBox(snapshot, "Right tagline").TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Center, FindTextBox(snapshot, "Centered heading in the div").TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Center, FindTextBox(snapshot, "Centered paragraph in the div.").TextAlignment);
        Assert.Equal(CanvasHorizontalAlignment.Left, FindTextBox(snapshot, "Outside paragraph.").TextAlignment);
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

    private static InlineContainerBox FindTextBox(LayoutSnapshot snapshot, string text) =>
        Assert.Single(
            FlattenBoxesAndListItems(snapshot.Blocks).OfType<InlineContainerBox>(),
            box => string.Concat(box.Runs.Select(static run => run.Text)).Trim() == text);

    private static IEnumerable<BlockBox> FlattenBoxesAndListItems(IEnumerable<BlockBox> blocks)
    {
        foreach (BlockBox block in blocks)
        {
            yield return block;
            IEnumerable<BlockBox> children = block switch
            {
                ListItemBox item => FlattenBoxesAndListItems(item.Content.Children),
                StackBox stack => FlattenBoxesAndListItems(stack.Children),
                _ => [],
            };
            foreach (BlockBox child in children)
                yield return child;
        }
    }
}
