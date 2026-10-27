using System.Collections.Generic;
using System.Linq;
using Markdig;
using MarkdownRenderer.Document;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownDocumentSnapshotTests
{
    [Fact]
    public void Snapshot_PreservesExactUtf16SourceAndSemanticMap()
    {
        const string markdown = "😀 introduction\n\n# Heading\n\n[docs](https://example.test)";
        var parsed = Markdown.Parse(markdown, new MarkdownPipelineBuilder().UsePreciseSourceLocation().Build());

        var document = MarkdownRenderer.Document.MarkdownDocument.FromParsed(markdown, parsed);

        Assert.Same(document.SourceText, document.Source);
        Assert.Equal(markdown, document.Source);
        Assert.Empty(document.Diagnostics);
        Assert.False(document.HasErrors);

        var heading = Assert.Single(document.SourceMap, entry => entry.Kind == MarkdownSourceElementKind.Heading);
        Assert.Equal(markdown.IndexOf('#'), heading.SourceSpan.Start);
        Assert.Equal("# Heading", markdown.Substring(heading.SourceSpan.Start, heading.SourceSpan.Length));

        var link = Assert.Single(document.SourceMap, entry => entry.Kind == MarkdownSourceElementKind.Link);
        Assert.Equal(markdown.IndexOf("[docs]", System.StringComparison.Ordinal), link.SourceSpan.Start);
        Assert.Equal("[docs](https://example.test)", markdown.Substring(link.SourceSpan.Start, link.SourceSpan.Length));
    }

    [Fact]
    public void Snapshot_QueryCollectionsCannotBeMutatedThroughListCasts()
    {
        const string markdown = "# Heading";
        var parsed = Markdown.Parse(markdown);
        var document = MarkdownRenderer.Document.MarkdownDocument.FromParsed(markdown, parsed);

        var headings = Assert.IsAssignableFrom<IList<MarkdownHeading>>(document.GetHeadings());
        Assert.True(headings.IsReadOnly);
        Assert.Throws<System.NotSupportedException>(() => headings.Clear());

        var sourceMap = Assert.IsAssignableFrom<IList<MarkdownSourceMapEntry>>(document.SourceMap);
        Assert.True(sourceMap.IsReadOnly);
        Assert.Throws<System.NotSupportedException>(() => sourceMap.Clear());
    }

    [Fact]
    public void Snapshot_SourceMapIsSortedByUtf16SourceOffset()
    {
        const string markdown = "# [Heading](https://example.test)\n\n![Alt](image.png)";
        var parsed = Markdown.Parse(markdown);
        var document = MarkdownRenderer.Document.MarkdownDocument.FromParsed(markdown, parsed);

        Assert.NotEmpty(document.SourceMap);
        Assert.Equal(
            document.SourceMap.Select(static entry => entry.SourceSpan.Start).OrderBy(static start => start),
            document.SourceMap.Select(static entry => entry.SourceSpan.Start));
    }
}
