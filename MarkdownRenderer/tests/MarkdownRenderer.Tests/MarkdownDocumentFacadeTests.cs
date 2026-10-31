using Markdig;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Renderers.Html;
using MarkdownRenderer.Document;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownDocumentFacadeTests
{
    [Fact]
    public void Queries_ReturnExpectedMarkdownElements()
    {
        const string markdown = """
            # Heading

            See [docs](https://example.test "Docs").

            ```csharp
            Console.WriteLine("hi");
            ```

            ![Alt text](https://example.test/image.svg "Image")
            """;

        var parsed = Markdown.Parse(markdown);
        var document = MarkdownRenderer.Document.MarkdownDocument.FromParsed(markdown, parsed);

        var heading = Assert.Single(document.GetHeadings());
        Assert.Equal("Heading", heading.DisplayText);
        Assert.Equal(1, heading.Level);
        Assert.True(heading.SourceSpan.Length > 0);

        var link = Assert.Single(document.GetLinks());
        Assert.Equal("docs", link.DisplayText);
        Assert.Equal("https://example.test", link.Url);
        Assert.Equal("Docs", link.Title);

        var code = Assert.Single(document.GetCodeBlocks());
        Assert.Equal("csharp", code.Language);
        Assert.Contains("Console.WriteLine", code.DisplayText);

        var image = Assert.Single(document.GetImages());
        Assert.Equal("Alt text", image.AltText);
        Assert.Equal("https://example.test/image.svg", image.Source);
        Assert.Equal("Image", image.Title);
        Assert.True(image.IsInline);
    }

    [Fact]
    public void Empty_HasNoQueryResults()
    {
        Assert.Empty(MarkdownRenderer.Document.MarkdownDocument.Empty.GetHeadings());
        Assert.Empty(MarkdownRenderer.Document.MarkdownDocument.Empty.GetLinks());
        Assert.Empty(MarkdownRenderer.Document.MarkdownDocument.Empty.GetCodeBlocks());
        Assert.Empty(MarkdownRenderer.Document.MarkdownDocument.Empty.GetImages());
        Assert.Empty(MarkdownRenderer.Document.MarkdownDocument.Empty.GetFootnotes());
        Assert.Empty(MarkdownRenderer.Document.MarkdownDocument.Empty.GetDefinitionItems());
        Assert.Empty(MarkdownRenderer.Document.MarkdownDocument.Empty.GetAbbreviations());
        Assert.Empty(MarkdownRenderer.Document.MarkdownDocument.Empty.GetFragments());
    }

    [Fact]
    public void MarkdownExtraQueries_ReturnExpectedElements()
    {
        const string markdown = """
            ## Intro {#intro .warning}

            HTML appears here.

            Term
            :   Definition body

            Footnote citation[^note].

            *[HTML]: Hyper Text Markup Language

            [^note]: Footnote body.
            """;

        var pipeline = new MarkdownPipelineBuilder()
            .UseGenericAttributes()
            .UseDefinitionLists()
            .UseAbbreviations()
            .UseFootnotes()
            .Build();
        var parsed = Markdown.Parse(markdown, pipeline);
        var document = MarkdownRenderer.Document.MarkdownDocument.FromParsed(markdown, parsed);

        var fragment = Assert.Single(document.GetFragments(), f => f.Id == "intro");
        Assert.True(fragment.SourceSpan.Length > 0);

        var definition = Assert.Single(document.GetDefinitionItems());
        Assert.Equal("Term", definition.Term);
        Assert.Equal("Definition body", definition.Definition);
        Assert.Equal(':', definition.Marker);
        Assert.True(definition.SourceSpan.Length > 0);

        var abbreviation = Assert.Single(document.GetAbbreviations());
        Assert.Equal("HTML", abbreviation.DisplayText);
        Assert.Equal("Hyper Text Markup Language", abbreviation.Expansion);
        Assert.True(abbreviation.SourceSpan.Length > 0);

        var footnote = Assert.Single(document.GetFootnotes());
        Assert.Equal("note", footnote.Label);
        Assert.Equal("Footnote body.", footnote.DisplayText);
        Assert.True(footnote.Order > 0);
    }
}
