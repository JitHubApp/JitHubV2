using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Document;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;
using MarkdownDocumentSnapshot = MarkdownRenderer.Document.MarkdownDocument;
using System.Threading;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownStyleRoleDemandTests
{
    [Fact]
    public void ParsedDocumentCollectsRolesFromTheWholeAst()
    {
        const string markdown = """
            Introduction.

            # Later heading

            Inline `code`, **strong**, *emphasis*, ~~strike~~, [link](https://example.test).

            ![alt](image.png)

            > [!NOTE]
            > alert content

            - list item

            ---

            | header |
            | --- |
            | cell |

            ```csharp
            var answer = 42;
            ```
            """;
        var pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseEmphasisExtras()
            .Build();
        var parsed = Markdown.Parse(markdown, pipeline);
        MarkdownDocumentSnapshot document = MarkdownDocumentSnapshot.FromParsed(markdown, parsed);

        MarkdownStyleRoleDemandResult result = MarkdownStyleRoleDemandCollector.Collect(document);

        Assert.True(result.IsComplete);
        // Bit positions follow ThemeResolver's fixed BuiltInElementKeys order.
        const ulong expectedRoles =
            (1UL << 0) |  // Heading1
            (1UL << 6) |  // Body
            (1UL << 7) |  // CodeInline
            (1UL << 8) |  // CodeBlock
            (1UL << 9) |  // CodeBlockHeader
            (1UL << 10) | // CodeBlockLanguage
            (1UL << 11) | // CodeBlockGutter
            (1UL << 12) | // CodeBlockLineNumber
            (1UL << 13) | // Quote
            (1UL << 14) | // Link
            (1UL << 15) | // Strong
            (1UL << 16) | // Emphasis
            (1UL << 17) | // Strikethrough
            (1UL << 23) | // ListMarker
            (1UL << 24) | // ThematicBreak
            (1UL << 25) | // ImageCaption
            (1UL << 32) | // Table
            (1UL << 33) | // TableHeader
            (1UL << 34) | // TableCell
            (1UL << 35) | // AlertNote
            (1UL << 36) | // AlertTip
            (1UL << 37) | // AlertImportant
            (1UL << 38) | // AlertWarning
            (1UL << 39);  // AlertCaution

        Assert.Equal(expectedRoles, result.Mask & expectedRoles);
    }

    [Fact]
    public void UnsupportedCustomInlineHtmlDoesNotForceFullRoleResolution()
    {
        const string markdown = "# Build your own <insert-technology-here>";
        var parsed = Markdown.Parse(markdown);
        HeadingBlock heading = Assert.IsType<HeadingBlock>(Assert.Single(parsed));
        Assert.Contains(
            heading.Inline!,
            inline => inline is HtmlInline html && html.Tag == "<insert-technology-here>");
        MarkdownDocumentSnapshot document = MarkdownDocumentSnapshot.FromParsed(markdown, parsed);

        MarkdownStyleRoleDemandResult result = MarkdownStyleRoleDemandCollector.Collect(document);

        Assert.True(result.IsComplete);
        Assert.Equal((1UL << 0) | (1UL << 6), result.Mask);
    }

    [Fact]
    public async Task PerformanceSessionPrecomputesAndCachesRoleMaskOffSnapshotPath()
    {
        const string markdown = "# heading";
        MarkdownDocumentSnapshot document = MarkdownDocumentSnapshot.FromParsed(
            markdown,
            Markdown.Parse(markdown));
        await using var session = new MarkdownPerformanceSession(new MarkdownPerformanceOptions());
        IMarkdownPerformanceSessionInternal internalSession = session;

        Assert.False(internalSession.TryGetStyleRoleDemandMask(
            document,
            CancellationToken.None,
            out _));

        MarkdownDocumentSnapshot? prepared = await internalSession.ParseAndPrepareDocumentAsync(
            engine: null,
            document: document,
            source: null,
            legacyRegistry: new MarkdownExtensionRegistry(),
            documentOwner: new object(),
            cancellationToken: CancellationToken.None);

        Assert.Same(document, prepared);
        Assert.True(internalSession.TryGetStyleRoleDemandMask(
            document,
            CancellationToken.None,
            out ulong mask));
        Assert.Equal((1UL << 0) | (1UL << 6), mask);
    }

    [Fact]
    public async Task DeclarativeCustomRoleKeepsFullResolutionFallback()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseExtension(new CustomRoleExtension())
            .Build();
        MarkdownDocumentSnapshot document = await engine.ParseAsync("custom content");

        MarkdownStyleRoleDemandResult result = MarkdownStyleRoleDemandCollector.Collect(document);

        Assert.False(result.IsComplete);
        Assert.Contains("DemandTestRole", document.GetExtensionStyleRoleNames());
    }

    [Theory]
    [InlineData("<div>block HTML</div>")]
    [InlineData("paragraph with <strong>inline HTML</strong>")]
    public void SupportedHtmlRoleDispatchRetainsFullResolutionFallback(string markdown)
    {
        var parsed = Markdown.Parse(markdown);
        MarkdownDocumentSnapshot document = MarkdownDocumentSnapshot.FromParsed(markdown, parsed);

        MarkdownStyleRoleDemandResult result = MarkdownStyleRoleDemandCollector.Collect(document);

        Assert.False(result.IsComplete);
    }

    private sealed class CustomRoleExtension : MarkdownRenderer.Extensions.IMarkdownExtension
    {
        public string Id => "MarkdownRenderer.Tests.StyleRoleDemand";

        public void Configure(MarkdownExtensionBuilder builder)
            => builder.RegisterBlock(
                MarkdownSyntaxKinds.Block.Paragraph,
                (context, content) => content.AddText(
                    "custom content",
                    context.Node.SourceSpan,
                    new MarkdownStyleRole("DemandTestRole")));
    }
}
