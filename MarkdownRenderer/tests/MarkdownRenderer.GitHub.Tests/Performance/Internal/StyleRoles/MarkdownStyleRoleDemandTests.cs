using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Document;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Performance.Internal.StyleRoles;
using MarkdownRenderer.Theming.Internal;
using MarkdownDocumentSnapshot = MarkdownRenderer.Document.MarkdownDocument;
using System.Threading;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Performance.Internal.StyleRoles;

public sealed class MarkdownStyleRoleDemandTests
{
    public static TheoryData<string> BuiltInStyleRoles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (string role in ThemeResolver.BuiltInStyleRoleOrder)
                data.Add(role);
            return data;
        }
    }

    [Fact]
    public void EveryDemandFlagNamesTheBuiltInRoleAtItsBitPosition()
    {
        IReadOnlyList<string> roles = ThemeResolver.BuiltInStyleRoleOrder;
        MarkdownStyleRoleDemand[] flags = Enum.GetValues<MarkdownStyleRoleDemand>()
            .Where(static flag => flag != MarkdownStyleRoleDemand.None)
            .ToArray();

        Assert.Equal(roles, flags.Select(static flag => flag.ToString()));
        for (int bit = 0; bit < roles.Count; bit++)
            Assert.Equal(1UL << bit, (ulong)Enum.Parse<MarkdownStyleRoleDemand>(roles[bit]));
    }

    [Theory]
    [MemberData(nameof(BuiltInStyleRoles))]
    public void EachBuiltInRoleResolvesItsResourcesUnderItsOwnDemandFlag(string role)
    {
        Assert.True(Enum.TryParse(role, out MarkdownStyleRoleDemand flag), $"No demand flag for {role}.");

        HashSet<string> resolved = ThemeResolver.GetScopedResourceStyleRoles(
            ThemeResolver.BuiltInStyleRoleOrder,
            (ulong)flag);

        Assert.Equal([role], resolved);
    }

    [Theory]
    [InlineData("- item\n- item", new[] { "Body", "List", "ListMarker" })]
    [InlineData("---", new[] { "Body", "ThematicBreak" })]
    [InlineData("| a |\n| --- |\n| b |", new[] { "Body", "Table", "TableHeader", "TableCell" })]
    public void DocumentDemandResolvesExactlyTheRolesItsBlocksRead(string markdown, string[] expectedRoles)
    {
        var pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();
        MarkdownStyleRoleDemandResult result = MarkdownStyleRoleDemandCollector.Collect(
            MarkdownDocumentSnapshot.FromParsed(markdown, Markdown.Parse(markdown, pipeline)));

        Assert.True(result.IsComplete, result.FirstIncompleteNodeType);
        HashSet<string> resolved = ThemeResolver.GetScopedResourceStyleRoles(
            ThemeResolver.BuiltInStyleRoleOrder,
            result.Mask);
        Assert.Equal(expectedRoles.ToHashSet(StringComparer.Ordinal), resolved);
    }

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
        // Each flag's bit is its role's index in ThemeResolver's built-in role
        // order, which EveryDemandFlagNamesTheBuiltInRoleAtItsBitPosition pins.
        const ulong expectedRoles = (ulong)(
            MarkdownStyleRoleDemand.Heading1 |
            MarkdownStyleRoleDemand.Body |
            MarkdownStyleRoleDemand.CodeInline |
            MarkdownStyleRoleDemand.CodeBlock |
            MarkdownStyleRoleDemand.CodeBlockHeader |
            MarkdownStyleRoleDemand.CodeBlockLanguage |
            MarkdownStyleRoleDemand.CodeBlockGutter |
            MarkdownStyleRoleDemand.CodeBlockLineNumber |
            MarkdownStyleRoleDemand.Quote |
            MarkdownStyleRoleDemand.Link |
            MarkdownStyleRoleDemand.Strong |
            MarkdownStyleRoleDemand.Emphasis |
            MarkdownStyleRoleDemand.Strikethrough |
            MarkdownStyleRoleDemand.List |
            MarkdownStyleRoleDemand.ListMarker |
            MarkdownStyleRoleDemand.ThematicBreak |
            MarkdownStyleRoleDemand.ImageCaption |
            MarkdownStyleRoleDemand.Table |
            MarkdownStyleRoleDemand.TableHeader |
            MarkdownStyleRoleDemand.TableCell |
            MarkdownStyleRoleDemand.AlertNote |
            MarkdownStyleRoleDemand.AlertTip |
            MarkdownStyleRoleDemand.AlertImportant |
            MarkdownStyleRoleDemand.AlertWarning |
            MarkdownStyleRoleDemand.AlertCaution);

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

    [Fact]
    public void SafeHtmlBlockCollectsExactBuiltInRoleDemand()
    {
        const string markdown = """
            <div align="center"><h1>Title</h1><p><a href="/docs"><img src="cover.png" alt="cover"></a></p><h3>Details</h3></div>
            """;
        var parsed = Markdown.Parse(markdown);
        MarkdownDocumentSnapshot document = MarkdownDocumentSnapshot.FromParsed(markdown, parsed);

        MarkdownStyleRoleDemandResult result = MarkdownStyleRoleDemandCollector.Collect(document);

        Assert.True(result.IsComplete, result.FirstIncompleteNodeType);
        Assert.Equal(
            (ulong)(MarkdownStyleRoleDemand.Heading1 |
                MarkdownStyleRoleDemand.Heading3 |
                MarkdownStyleRoleDemand.Body |
                MarkdownStyleRoleDemand.Link |
                MarkdownStyleRoleDemand.ImageCaption),
            result.Mask);
    }

    [Fact]
    public void SafeHtmlBlockCollectsTableCodeAndInlineRolesThroughUnknownWrappers()
    {
        const string markdown = """
            <div><x-custom><h2>Heading</h2><strong>strong</strong><em>em</em><a href="/">link</a><table><tr><th>header</th></tr><tr><td>cell</td></tr></table><pre><code>code</code></pre><img src="x.png" alt="image"><hr></x-custom></div>
            """;
        MarkdownDocumentSnapshot document = MarkdownDocumentSnapshot.FromParsed(
            markdown,
            Markdown.Parse(markdown));

        MarkdownStyleRoleDemandResult result = MarkdownStyleRoleDemandCollector.Collect(document);

        Assert.True(result.IsComplete, result.FirstIncompleteNodeType);
        const ulong expectedRoles = (ulong)(
            MarkdownStyleRoleDemand.Heading2 |
            MarkdownStyleRoleDemand.Body |
            MarkdownStyleRoleDemand.CodeBlock |
            MarkdownStyleRoleDemand.CodeBlockHeader |
            MarkdownStyleRoleDemand.CodeBlockLanguage |
            MarkdownStyleRoleDemand.CodeBlockGutter |
            MarkdownStyleRoleDemand.CodeBlockLineNumber |
            MarkdownStyleRoleDemand.Link |
            MarkdownStyleRoleDemand.Strong |
            MarkdownStyleRoleDemand.Emphasis |
            MarkdownStyleRoleDemand.ThematicBreak |
            MarkdownStyleRoleDemand.ImageCaption |
            MarkdownStyleRoleDemand.Table |
            MarkdownStyleRoleDemand.TableHeader |
            MarkdownStyleRoleDemand.TableCell);
        Assert.Equal(expectedRoles, result.Mask);
    }

    [Fact]
    public void SafeHtmlSupportedRoleDispatchIsCoveredByTheDemandMask()
    {
        const string markdown = """
            <div>
            <h1>one</h1>
            <h2>two</h2>
            <h3>three</h3>
            <h4>four</h4>
            <h5>five</h5>
            <h6>six</h6>
            <p><a href="/">link</a> <b>strong</b> <em>emphasis</em> <del>strike</del> <sub>subscript</sub> <sup>superscript</sup> <ins>inserted</ins> <mark>marked</mark> <code>inline</code> <kbd>keyboard</kbd> <samp>sample</samp></p>
            <blockquote>quote</blockquote>
            <ul><li>unordered</li></ul>
            <ol><li>ordered</li></ol>
            <hr>
            <table><thead><tr><th>heading</th></tr></thead><tbody><tr><td>cell</td></tr></tbody></table>
            <pre><code>block</code></pre>
            <img src="image.png" alt="image">
            <picture><source srcset="image.png" type="image/png"><img src="fallback.png" alt="picture"></picture>
            <svg aria-hidden="true"><text>atomic</text></svg>
            <video src="video.mp4" title="video"></video>
            <audio src="audio.mp3" title="audio"></audio>
            <input type="checkbox">
            </div>
            """;
        MarkdownStyleRoleDemandResult result = MarkdownStyleRoleDemandCollector.Collect(
            MarkdownDocumentSnapshot.FromParsed(markdown, Markdown.Parse(markdown)));

        Assert.True(result.IsComplete, result.FirstIncompleteNodeType);
        const ulong expectedRoles = (ulong)(
            MarkdownStyleRoleDemand.Heading1 |
            MarkdownStyleRoleDemand.Heading2 |
            MarkdownStyleRoleDemand.Heading3 |
            MarkdownStyleRoleDemand.Heading4 |
            MarkdownStyleRoleDemand.Heading5 |
            MarkdownStyleRoleDemand.Heading6 |
            MarkdownStyleRoleDemand.Body |
            MarkdownStyleRoleDemand.CodeInline |
            MarkdownStyleRoleDemand.CodeBlock |
            MarkdownStyleRoleDemand.CodeBlockHeader |
            MarkdownStyleRoleDemand.CodeBlockLanguage |
            MarkdownStyleRoleDemand.CodeBlockGutter |
            MarkdownStyleRoleDemand.CodeBlockLineNumber |
            MarkdownStyleRoleDemand.Quote |
            MarkdownStyleRoleDemand.Link |
            MarkdownStyleRoleDemand.Strong |
            MarkdownStyleRoleDemand.Emphasis |
            MarkdownStyleRoleDemand.Strikethrough |
            MarkdownStyleRoleDemand.Subscript |
            MarkdownStyleRoleDemand.Superscript |
            MarkdownStyleRoleDemand.Inserted |
            MarkdownStyleRoleDemand.Marked |
            MarkdownStyleRoleDemand.ListMarker |
            MarkdownStyleRoleDemand.ThematicBreak |
            MarkdownStyleRoleDemand.ImageCaption |
            MarkdownStyleRoleDemand.Table |
            MarkdownStyleRoleDemand.TableHeader |
            MarkdownStyleRoleDemand.TableCell);
        Assert.Equal(expectedRoles, result.Mask);
    }

    [Fact]
    public void SafeHtmlDetailsCollectsHiddenDescendantRolesIndependentOfDefaultOpenState()
    {
        const string closed = """
            <details><summary><strong>More</strong></summary><h2>Hidden heading</h2><a href="/hidden">hidden link</a></details>
            """;
        const string open = """
            <details open><summary><strong>More</strong></summary><h2>Hidden heading</h2><a href="/hidden">hidden link</a></details>
            """;

        MarkdownStyleRoleDemandResult closedResult = MarkdownStyleRoleDemandCollector.Collect(
            MarkdownDocumentSnapshot.FromParsed(closed, Markdown.Parse(closed)));
        MarkdownStyleRoleDemandResult openResult = MarkdownStyleRoleDemandCollector.Collect(
            MarkdownDocumentSnapshot.FromParsed(open, Markdown.Parse(open)));

        Assert.True(closedResult.IsComplete, closedResult.FirstIncompleteNodeType);
        Assert.True(openResult.IsComplete, openResult.FirstIncompleteNodeType);
        Assert.Equal(openResult.Mask, closedResult.Mask);
        Assert.Equal((1UL << 1) | (1UL << 6) | (1UL << 14) | (1UL << 15), closedResult.Mask);
    }

    [Fact]
    public void SafeHtmlDetailsCollectsHeadingAndCodeRolesFromSpecialSummaryDescendants()
    {
        const string headingInPrunedWrapper = "<details><summary><button><h3>Heading</h3></button></summary></details>";
        const string codeInPrunedWrapper = "<details><summary><form><pre>code</pre></form></summary></details>";

        MarkdownStyleRoleDemandResult headingResult = MarkdownStyleRoleDemandCollector.Collect(
            MarkdownDocumentSnapshot.FromParsed(
                headingInPrunedWrapper,
                Markdown.Parse(headingInPrunedWrapper)));
        MarkdownStyleRoleDemandResult codeResult = MarkdownStyleRoleDemandCollector.Collect(
            MarkdownDocumentSnapshot.FromParsed(
                codeInPrunedWrapper,
                Markdown.Parse(codeInPrunedWrapper)));

        Assert.True(headingResult.IsComplete, headingResult.FirstIncompleteNodeType);
        Assert.True(codeResult.IsComplete, codeResult.FirstIncompleteNodeType);
        Assert.Equal((1UL << 2) | (1UL << 6), headingResult.Mask);
        Assert.Equal(
            (1UL << 6) | (1UL << 8) | (1UL << 9) | (1UL << 10) | (1UL << 11) | (1UL << 12) | (1UL << 15),
            codeResult.Mask);
    }

    [Fact]
    public void SafeHtmlSuppressesPrunedFormButtonAndRawContentSubtrees()
    {
        const string markdown = "<div><form><h1>hidden</h1></form><button><h2>hidden</h2></button><script><h3>hidden</h3></script></div>";
        MarkdownStyleRoleDemandResult result = MarkdownStyleRoleDemandCollector.Collect(
            MarkdownDocumentSnapshot.FromParsed(markdown, Markdown.Parse(markdown)));

        Assert.True(result.IsComplete, result.FirstIncompleteNodeType);
        Assert.Equal(1UL << 6, result.Mask); // Body only.
    }

    [Theory]
    [InlineData("<div>block HTML</div>")]
    [InlineData("<div># nested markdown heading</div>")]
    [InlineData("paragraph with <strong>inline HTML</strong>")]
    [InlineData("<hr>\r\n  \r\n## Following heading\r\n")]
    public void HtmlWithNestedMarkdownOrInlineStateRetainsFullResolutionFallback(string markdown)
    {
        MarkdownDocumentSnapshot document = MarkdownDocumentSnapshot.FromParsed(markdown, Markdown.Parse(markdown));

        MarkdownStyleRoleDemandResult result = MarkdownStyleRoleDemandCollector.Collect(document);

        Assert.False(result.IsComplete);
    }

    [Fact]
    public void MalformedHtmlIsCollectedAsSafeHtmlSuperset()
    {
        const string markdown = "<div><strong>bold<em>emphasis</div>";
        MarkdownDocumentSnapshot document = MarkdownDocumentSnapshot.FromParsed(markdown, Markdown.Parse(markdown));

        MarkdownStyleRoleDemandResult result = MarkdownStyleRoleDemandCollector.Collect(document);

        Assert.True(result.IsComplete, result.FirstIncompleteNodeType);
        Assert.Equal((1UL << 6) | (1UL << 15) | (1UL << 16), result.Mask);
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
