using Markdig.Syntax;
using MarkdownRenderer.Layout.Internal.SafeHtml;
using MarkdownRenderer.Parsing.Internal.SafeHtml;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.SafeHtml;

/// <summary>
/// Checks that the cross-block safe-HTML scope stack closes an open paragraph
/// the way HTML does, so a paragraph's alignment cannot outlive it.
/// </summary>
public sealed class SafeHtmlBlockScopeTrackerTests
{
    private static readonly IReadOnlyDictionary<string, bool> NoDisclosureStates =
        new Dictionary<string, bool>();

    [Theory]
    [InlineData("<p>")]
    [InlineData("<div>")]
    [InlineData("<h1>")]
    [InlineData("<ul>")]
    [InlineData("<table>")]
    [InlineData("<details>")]
    public void BlockStartClosesAnOpenCenteredParagraph(string blockStart)
    {
        SafeHtmlBlockScopeTracker tracker = new();

        tracker.Process("<p align=\"center\">\ntext\n" + blockStart, 0, NoDisclosureStates);

        Assert.Equal(SafeHtmlAlignment.Inherit, tracker.CurrentAlignment);
    }

    [Fact]
    public void InlineContentKeepsTheParagraphOpen()
    {
        SafeHtmlBlockScopeTracker tracker = new();

        tracker.Process("<p align=\"center\">\n<a href=\"x\"><img src=\"y\"></a><br>", 0, NoDisclosureStates);

        Assert.Equal(SafeHtmlAlignment.Center, tracker.CurrentAlignment);
    }

    [Fact]
    public void ParagraphClosureLeavesTheEnclosingDivOpen()
    {
        SafeHtmlBlockScopeTracker tracker = new();

        tracker.Process("<div align=\"center\">\n<p align=\"right\">\ntext\n<p>", 0, NoDisclosureStates);
        Assert.Equal(SafeHtmlAlignment.Center, tracker.CurrentAlignment);

        tracker.Process("</div>", 60, NoDisclosureStates);
        Assert.Equal(SafeHtmlAlignment.Inherit, tracker.CurrentAlignment);
    }

    [Fact]
    public void MarkdownBlockClosesAnOpenParagraphButNotAnOpenDiv()
    {
        SafeHtmlBlockScopeTracker paragraphTracker = new();
        paragraphTracker.Process("<p align=\"center\">\ntagline", 0, NoDisclosureStates);
        Assert.Equal(SafeHtmlAlignment.Center, paragraphTracker.CurrentAlignment);

        paragraphTracker.CloseParagraphBeforeMarkdownBlock(new LinkReferenceDefinitionGroup());
        Assert.Equal(SafeHtmlAlignment.Center, paragraphTracker.CurrentAlignment);
        paragraphTracker.CloseParagraphBeforeMarkdownBlock(new ParagraphBlock());
        Assert.Equal(SafeHtmlAlignment.Inherit, paragraphTracker.CurrentAlignment);

        SafeHtmlBlockScopeTracker divTracker = new();
        divTracker.Process("<div align=\"center\">", 0, NoDisclosureStates);

        divTracker.CloseParagraphBeforeMarkdownBlock(new ParagraphBlock());
        Assert.Equal(SafeHtmlAlignment.Center, divTracker.CurrentAlignment);
    }

    [Fact]
    public void WellFormedSplitParagraphScopeStillTracksItsOwnClosingTag()
    {
        SafeHtmlBlockScopeTracker tracker = new();

        Assert.True(tracker.Process("<p align=\"center\">", 0, NoDisclosureStates));
        Assert.Equal(SafeHtmlAlignment.Center, tracker.CurrentAlignment);
        Assert.True(tracker.Process("</p>", 30, NoDisclosureStates));
        Assert.Equal(SafeHtmlAlignment.Inherit, tracker.CurrentAlignment);
    }
}
