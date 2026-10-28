using System.Globalization;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownStyleNameMappingTests
{
    [Theory]
    [InlineData(MarkdownElementKeys.Body, MarkdownStringKeys.StyleBody, "Body")]
    [InlineData(MarkdownElementKeys.Heading1, MarkdownStringKeys.StyleHeading1, "Heading 1")]
    [InlineData(MarkdownElementKeys.Heading2, MarkdownStringKeys.StyleHeading2, "Heading 2")]
    [InlineData(MarkdownElementKeys.Heading3, MarkdownStringKeys.StyleHeading3, "Heading 3")]
    [InlineData(MarkdownElementKeys.Heading4, MarkdownStringKeys.StyleHeading4, "Heading 4")]
    [InlineData(MarkdownElementKeys.Heading5, MarkdownStringKeys.StyleHeading5, "Heading 5")]
    [InlineData(MarkdownElementKeys.Heading6, MarkdownStringKeys.StyleHeading6, "Heading 6")]
    [InlineData(MarkdownElementKeys.CodeBlock, MarkdownStringKeys.StyleCodeBlock, "Code block")]
    [InlineData(MarkdownElementKeys.CodeInline, MarkdownStringKeys.StyleInlineCode, "Inline code")]
    [InlineData(MarkdownElementKeys.Quote, MarkdownStringKeys.StyleQuote, "Quote")]
    [InlineData(MarkdownElementKeys.Link, MarkdownStringKeys.StyleLink, "Link")]
    [InlineData(MarkdownElementKeys.Strong, MarkdownStringKeys.StyleStrong, "Strong")]
    [InlineData(MarkdownElementKeys.Emphasis, MarkdownStringKeys.StyleEmphasis, "Emphasis")]
    [InlineData(MarkdownElementKeys.Strikethrough, MarkdownStringKeys.StyleStrikethrough, "Strikethrough")]
    [InlineData(MarkdownElementKeys.Subscript, MarkdownStringKeys.StyleSubscript, "Subscript")]
    [InlineData(MarkdownElementKeys.Superscript, MarkdownStringKeys.StyleSuperscript, "Superscript")]
    [InlineData(MarkdownElementKeys.Inserted, MarkdownStringKeys.StyleInserted, "Inserted")]
    [InlineData(MarkdownElementKeys.Marked, MarkdownStringKeys.StyleMarked, "Marked")]
    [InlineData(MarkdownElementKeys.Abbreviation, MarkdownStringKeys.StyleAbbreviation, "Abbreviation")]
    [InlineData(MarkdownElementKeys.DefinitionTerm, MarkdownStringKeys.StyleDefinitionTerm, "Definition term")]
    [InlineData(MarkdownElementKeys.DefinitionDescription, MarkdownStringKeys.StyleDefinitionDescription, "Definition")]
    [InlineData(MarkdownElementKeys.Figure, MarkdownStringKeys.StyleFigure, "Figure")]
    [InlineData(MarkdownElementKeys.FigureCaption, MarkdownStringKeys.StyleFigureCaption, "Figure caption")]
    [InlineData(MarkdownElementKeys.Diagram, MarkdownStringKeys.StyleDiagram, "Diagram")]
    [InlineData(MarkdownElementKeys.ListMarker, MarkdownStringKeys.StyleListMarker, "List marker")]
    [InlineData(MarkdownElementKeys.Table, MarkdownStringKeys.StyleTable, "Table")]
    [InlineData(MarkdownElementKeys.TableHeader, MarkdownStringKeys.StyleTableHeader, "Table header")]
    [InlineData(MarkdownElementKeys.TableCell, MarkdownStringKeys.StyleTableCell, "Table cell")]
    public void StyleDefinitionPreservesResourceKeyAndInvariantFallback(
        string elementKey, string resourceKey, string fallback)
    {
        CultureInfo previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            Assert.Equal(resourceKey, MarkdownLocalizedStrings.StyleNameKey(elementKey));
            Assert.Equal(fallback, MarkdownLocalizedStrings.StyleName(elementKey));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("custom-role")]
    [InlineData("")]
    [InlineData("HEADING1")]
    public void UnknownRolesRemainLiteralAndDoNotAcquireAResourceKey(string elementKey)
    {
        Assert.Null(MarkdownLocalizedStrings.StyleNameKey(elementKey));
        Assert.Same(elementKey, MarkdownLocalizedStrings.StyleName(elementKey));
    }
}
