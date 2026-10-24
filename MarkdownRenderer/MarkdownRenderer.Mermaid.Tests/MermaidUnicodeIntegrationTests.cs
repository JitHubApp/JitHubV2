using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Mermaid.Tests;

public sealed class MermaidUnicodeIntegrationTests
{
    private const string LongUnknownEntityName =
        "unknown_abcdefghijklmnopqrstuvwxyz_abcdefghijklmnopqrstuvwxyz_abcdefghijklmnopqrstuvwxyz";

    [Theory]
    [InlineData("stadium")]
    [InlineData("manual-file")]
    [InlineData("manual-input")]
    [InlineData("curv-trap")]
    [InlineData("notch-pent")]
    [InlineData("doc")]
    [InlineData("docs")]
    [InlineData("tag-rect")]
    public async Task EntityLabelShapeGeometryMatchesIndependentlyAuthoredUnicode(string shape)
    {
        // These shapes recompute label metrics outside ordinary rectangular-node layout.
        // The independent literal must define both visible text and final shape geometry;
        // measuring an opaque projection token would make a larger, incorrect shape.
        const string literalText = "left😀right";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownVectorScene entity = await ParseAsync("left#128512;right");
        MarkdownVectorScene literal = await ParseAsync(literalText);
        Assert.Equal(literal.Viewport, entity.Viewport);
        Assert.Equal(
            Assert.Single(literal.Semantics, static item => item.Role == MarkdownVectorSemanticRole.Node).Bounds,
            Assert.Single(entity.Semantics, static item => item.Role == MarkdownVectorSemanticRole.Node).Bounds);

        async Task<MarkdownVectorScene> ParseAsync(string label)
        {
            MarkdownDocument document = await engine.ParseAsync(
                $"```mermaid\nflowchart LR\nA@{{ shape: {shape}, label: \"{label}\" }}\n```\n");
            Assert.Empty(document.Diagnostics);
            MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
            Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
            MarkdownContent content = Assert.Single(fragment!.Items);
            MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
            Assert.Equal(literalText, content.SemanticText);
            Assert.Equal(literalText, string.Concat(scene.Commands
                .Where(static command => command.Kind == MarkdownVectorCommandKind.DrawText)
                .Select(static command => command.Text)));
            return scene;
        }
    }

    [Theory]
    [InlineData("#quot;", "\"")]
    [InlineData("#quot;middle#quot;", "\"middle\"")]
    [InlineData("#35;", "#")]
    [InlineData("#128512;", "😀")]
    [InlineData("e#769;", "é")]
    [InlineData("مرحبا #quot;API#quot;", "مرحبا \"API\"")]
    [InlineData("a#amp;b", "a&b")]
    [InlineData("a#lt;br#gt;b", "a<br>b")]
    [InlineData("#doesnotexist;", "&doesnotexist;")]
    [InlineData("#amp;quot;", "&quot;")]
    [InlineData("a#lt;b#gt;text#lt;/b#gt;b", "a<b>text</b>b")]
    [InlineData("a<b>#lt;b#gt;text#lt;/b#gt;</b>b", "a<b>text</b>b")]
    [InlineData("literal ﬂ°quot¶ß", "literal ﬂ°quot¶ß")]
    [InlineData("literal ﬂ°", "literal ﬂ°")]
    [InlineData("literal ¶ß", "literal ¶ß")]
    [InlineData("#" + LongUnknownEntityName + ";", "&" + LongUnknownEntityName + ";")]
    public async Task EntityExpandedLabelMapsToTheWholeAuthoredEntityToken(string authoredLabel, string expectedText)
    {
        string markdown = $"```mermaid\nflowchart LR\nescaped[\"{authoredLabel}\"]\n```\n";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync(markdown);
        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        (MarkdownVectorSemanticItem node, int semanticIndex) = Assert.Single(
            scene.Semantics.Select(static (item, index) => (item, index)),
            pair => pair.item.Role == MarkdownVectorSemanticRole.Node && pair.item.Name == expectedText);
        string renderedText = string.Concat(scene.Commands
            .Where(command => command.SemanticIndex == semanticIndex &&
                command.Kind == MarkdownVectorCommandKind.DrawText)
            .Select(static command => command.Text));
        Assert.Equal(expectedText, renderedText);

        int tokenStart = markdown.IndexOf(authoredLabel, StringComparison.Ordinal);
        Assert.True(tokenStart >= 0);
        Assert.Equal(new SourceSpan(tokenStart, authoredLabel.Length), node.SourceSpan);
    }

    [Theory]
    [InlineData("#35;", "#")]
    [InlineData("#128512;", "😀")]
    [InlineData("e#769;", "é")]
    [InlineData("#64258;#176;quot#182;#223;", "ﬂ°quot¶ß")]
    public async Task EntityExpandedLabelUsesTheSameGeometryAsItsUnicodeText(string authoredLabel, string literalText)
    {
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownVectorScene entityScene = await ParseSingleNodeAsync(authoredLabel);
        MarkdownVectorScene literalScene = await ParseSingleNodeAsync(literalText);
        MarkdownVectorSemanticItem entityNode = Assert.Single(entityScene.Semantics,
            static item => item.Role == MarkdownVectorSemanticRole.Node);
        MarkdownVectorSemanticItem literalNode = Assert.Single(literalScene.Semantics,
            static item => item.Role == MarkdownVectorSemanticRole.Node);

        // The independently authored literal is the geometry oracle. Merely
        // decoding the final text after layout would leave an oversized node.
        Assert.Equal(literalText, literalNode.Name);
        Assert.Equal(literalText, entityNode.Name);
        Assert.Equal(literalScene.Viewport, entityScene.Viewport);
        Assert.Equal(literalNode.Bounds, entityNode.Bounds);

        async Task<MarkdownVectorScene> ParseSingleNodeAsync(string label)
        {
            MarkdownDocument document = await engine.ParseAsync($"```mermaid\nflowchart LR\nnode[\"{label}\"]\n```\n");
            Assert.Empty(document.Diagnostics);
            MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
            Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
            return Assert.IsType<MarkdownVectorScene>(Assert.Single(fragment!.Items).VectorScene);
        }
    }

    [Theory]
    [InlineData("A@{ shape: rect, label: \"literal ﬂ°quot¶ß\" }")]
    [InlineData("A@{ shape: rect, label: 'literal ﬂ°quot¶ß' }")]
    [InlineData("A@{ shape: rect, label: literal ﬂ°quot¶ß }")]
    [InlineData("A@{ shape: rect, label: \"literal \\uFB02\\u00B0quot\\u00B6\\u00DF\" }")]
    public async Task AuthoredShapeDataMarkerGlyphsAreNeverPreprocessorMetadata(string nodeSource)
    {
        const string expected = "literal ﬂ°quot¶ß";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();
        MarkdownDocument document = await engine.ParseAsync($"```mermaid\nflowchart LR\n{nodeSource}\n```\n");
        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        Assert.Equal(expected, string.Concat(scene.Commands
            .Where(static item => item.Kind == MarkdownVectorCommandKind.DrawText)
            .Select(static item => item.Text)));
        Assert.Equal(expected, content.SemanticText);
    }

    [Theory]
    [InlineData("A[\"`&nbsp;literal&nbsp;`\"]", "A[\"`#amp;nbsp;literal#amp;nbsp;`\"]")]
    [InlineData("A@{ shape: rect, label: \"&nbsp;literal&nbsp;\", labelType: markdown }",
        "A@{ shape: rect, label: \"#amp;nbsp;literal#amp;nbsp;\", labelType: markdown }")]
    public async Task AuthoredHtmlEntitySpellingStaysLiteralInTypedSvgMarkdownLabels(
        string nodeSource, string independentlyEscapedNodeSource)
    {
        // SVG Markdown text is not HTML. Only provenance-marked Mermaid #...; entities
        // are expanded; the authored HTML spelling must survive painting and semantic copy.
        const string expected = "&nbsp;literal&nbsp;";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();
        MarkdownDocument document = await engine.ParseAsync($"```mermaid\nflowchart LR\n{nodeSource}\n```\n");
        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        Assert.Equal(expected, string.Concat(scene.Commands
            .Where(static item => item.Kind == MarkdownVectorCommandKind.DrawText)
            .Select(static item => item.Text)));
        Assert.Equal(expected, content.SemanticText);

        // The separately authored Mermaid ampersand escape also yields literal "&nbsp;".
        // Comparing final geometry catches a fix that changes painted text only after layout.
        MarkdownDocument escapedDocument = await engine.ParseAsync(
            $"```mermaid\nflowchart LR\n{independentlyEscapedNodeSource}\n```\n");
        Assert.Empty(escapedDocument.Diagnostics);
        MarkdownCodeBlock escapedBlock = Assert.Single(escapedDocument.GetCodeBlocks());
        Assert.True(escapedDocument.TryGetBlockExtensionContent(
            escapedBlock.SourceSpan, out MarkdownContentFragment? escapedFragment));
        MarkdownContent escapedContent = Assert.Single(escapedFragment!.Items);
        MarkdownVectorScene escapedScene = Assert.IsType<MarkdownVectorScene>(escapedContent.VectorScene);
        Assert.Equal(expected, escapedContent.SemanticText);
        Assert.Equal(expected, string.Concat(escapedScene.Commands
            .Where(static item => item.Kind == MarkdownVectorCommandKind.DrawText)
            .Select(static item => item.Text)));
        Assert.Equal(escapedScene.Viewport, scene.Viewport);
        Assert.Equal(
            Assert.Single(escapedScene.Semantics, static item => item.Role == MarkdownVectorSemanticRole.Node).Bounds,
            Assert.Single(scene.Semantics, static item => item.Role == MarkdownVectorSemanticRole.Node).Bounds);
    }

    [Theory]
    [InlineData("traditional", "literal:\U000F0000\uFE03")]
    [InlineData("markdown", "literal:\U000F0000\uFE03")]
    [InlineData("shape-markdown", "literal:\U000F0000\uFE03")]
    [InlineData("traditional", "literal:\U000F0000\uFE04000000000000000158")]
    [InlineData("markdown", "literal:\U000F0000\uFE04000000000000000158")]
    [InlineData("shape-markdown", "literal:\U000F0000\uFE04000000000000000158")]
    public async Task AuthoredProjectionEscapeScalarsRemainLiteral(string nodeKind, string expected)
    {
        string nodeSource = nodeKind switch
        {
            "traditional" => $"A[\"{expected}\"]",
            "markdown" => $"A[\"`{expected}`\"]",
            "shape-markdown" => $"A@{{ shape: rect, label: \"{expected}\", labelType: markdown }}",
            _ => throw new ArgumentOutOfRangeException(nameof(nodeKind)),
        };
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();
        MarkdownDocument document = await engine.ParseAsync($"```mermaid\nflowchart LR\n{nodeSource}\n```\n");
        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        Assert.Equal(expected, string.Concat(scene.Commands
            .Where(static item => item.Kind == MarkdownVectorCommandKind.DrawText)
            .Select(static item => item.Text)));
        Assert.Equal(expected, content.SemanticText);
    }

    [Theory]
    [InlineData("flowchart LR\nA -->|#quot;| B")]
    [InlineData("flowchart LR\nA -->|#quot;| A")]
    [InlineData("flowchart LR\nsubgraph SG[\"#quot;\"]\nA\nend")]
    [InlineData("flowchart LR\nA@{ shape: rect, label: \"#quot;\" }")]
    [InlineData("flowchart LR\nA -->|\"`#quot;`\"| B")]
    [InlineData("flowchart LR\nA -->|\"`#quot;`\"| A")]
    [InlineData("flowchart LR\nsubgraph SG[\"`#quot;`\"]\nA\nend")]
    [InlineData("swimlane-beta LR\nA -->|#quot;| B")]
    [InlineData("swimlane-beta LR\nA -->|\"`#quot;`\"| B")]
    [InlineData("swimlane-beta LR\nA -->|\"`#quot;`\"| A")]
    [InlineData("swimlane-beta LR\nA[\"`#quot;`\"]")]
    [InlineData("swimlane-beta LR\nsubgraph SG[\"`#quot;`\"]\nA\nend")]
    [InlineData("swimlane-beta LR\nA@{ shape: rect, label: \"#quot;\" }")]
    public async Task EntityTextIsFinalAcrossExistingFlowchartLabelRoutes(string source)
    {
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync($"```mermaid\n{source}\n```\n");
        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        Assert.True(scene.Commands.Count(
            static command => command.Kind == MarkdownVectorCommandKind.DrawText && command.Text == "\"") == 1,
            "Expected a final quote label; actual text commands: " + string.Join(" | ", scene.Commands
                .Where(static command => command.Kind == MarkdownVectorCommandKind.DrawText)
                .Select(static command => command.Text)));
        Assert.DoesNotContain(scene.Commands,
            static command => command.Text?.Contains("ﬂ°", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(scene.Commands,
            static command => command.Text?.Contains("\U000F0000", StringComparison.Ordinal) == true);
        string semanticText = Assert.IsType<string>(content.SemanticText);
        Assert.Equal(1, semanticText.Count(static character => character == '"'));
        Assert.DoesNotContain("ﬂ°", semanticText, StringComparison.Ordinal);
        Assert.DoesNotContain("\U000F0000", semanticText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("#35;", "#")]
    [InlineData("#128512;", "😀")]
    [InlineData("e#769;", "é")]
    public async Task DefaultMarkdownShapeDataPreservesEntityTextAndCopy(string authoredLabel, string expectedText)
    {
        await AssertShapeDataLabelAsync(authoredLabel, expectedText);
    }

    [Theory]
    [InlineData("#quot;", "\"")]
    [InlineData("a#lt;br#gt;b", "a<br>b")]
    [InlineData("a#lt;b#gt;text#lt;/b#gt;b", "a<b>text</b>b")]
    [InlineData("#amp;quot;", "&quot;")]
    [InlineData("#doesnotexist;", "&doesnotexist;")]
    [InlineData("#amp;lt;b#amp;gt;", "&lt;b&gt;")]
    public async Task DefaultMarkdownShapeDataDecodesAfterMarkupClassification(string authoredLabel, string expectedText)
    {
        await AssertShapeDataLabelAsync(authoredLabel, expectedText);
    }

    [Fact]
    public async Task DefaultMarkdownShapeDataRetainsBoldWhileEncodedTagsStayLiteral()
    {
        MarkdownVectorScene scene = await AssertShapeDataLabelAsync(
            "**bold**#lt;b#gt;literal#lt;/b#gt;", "bold<b>literal</b>");

        Assert.Contains(scene.Commands, static command =>
            command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Text == "bold" && command.TextStyle!.FontWeight >= 700);
    }

    [Theory]
    [InlineData("**bold**_italic_", "bolditalic")]
    [InlineData("**bold** _italic_", "bold italic")]
    public async Task DefaultMarkdownShapeDataKeepsAuthoredSpacingAcrossMixedStyles(string authoredLabel, string expectedText)
    {
        await AssertShapeDataLabelAsync(authoredLabel, expectedText);
    }

    [Theory]
    [InlineData("#35;", "#")]
    [InlineData("#128512;", "😀")]
    [InlineData("e#769;", "é")]
    public async Task DefaultMarkdownShapeDataUsesLiteralEntityGeometry(string authoredLabel, string literalText)
    {
        MarkdownVectorScene encoded = await AssertShapeDataLabelAsync(authoredLabel, literalText);
        MarkdownVectorScene literal = await AssertShapeDataLabelAsync(literalText, literalText);
        Assert.Equal(literal.Viewport, encoded.Viewport);
        Assert.Equal(
            Assert.Single(literal.Semantics, static item => item.Role == MarkdownVectorSemanticRole.Node).Bounds,
            Assert.Single(encoded.Semantics, static item => item.Role == MarkdownVectorSemanticRole.Node).Bounds);
    }

    private static async Task<MarkdownVectorScene> AssertShapeDataLabelAsync(string authoredLabel, string expectedText)
    {
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();
        string quotedLabel = authoredLabel.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
        MarkdownDocument document = await engine.ParseAsync(
            $"```mermaid\nflowchart LR\nA@{{ shape: rect, label: \"{quotedLabel}\" }}\n```\n");
        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        Assert.Equal(expectedText, string.Concat(scene.Commands
            .Where(static command => command.Kind == MarkdownVectorCommandKind.DrawText)
            .Select(static command => command.Text)));
        Assert.Equal(expectedText, content.SemanticText);
        return scene;
    }

    [Theory]
    [InlineData("foo#32;bar", "foo bar")]
    [InlineData("**foo#32;bar**", "**foo bar**")]
    public async Task DefaultMarkdownShapeDataWrapsEntityWhitespaceAtLiteralWordBoundaries(
        string authoredLabel, string literalLabel)
    {
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownVectorScene encoded = await ParseLabelAsync(authoredLabel);
        MarkdownVectorScene literal = await ParseLabelAsync(literalLabel);
        Assert.Equal(literal.Viewport, encoded.Viewport);
        Assert.Equal(
            Assert.Single(literal.Semantics, static item => item.Role == MarkdownVectorSemanticRole.Node).Bounds,
            Assert.Single(encoded.Semantics, static item => item.Role == MarkdownVectorSemanticRole.Node).Bounds);
        Assert.Equal(
            literal.Commands.Where(static item => item.Kind == MarkdownVectorCommandKind.DrawText)
                .Select(static item => item.Text),
            encoded.Commands.Where(static item => item.Kind == MarkdownVectorCommandKind.DrawText)
                .Select(static item => item.Text));

        async Task<MarkdownVectorScene> ParseLabelAsync(string label)
        {
            string markdown = "```mermaid\n" +
                "%%{init: {\"flowchart\": {\"wrappingWidth\": 45}}}%%\n" +
                $"flowchart LR\nA@{{ shape: rect, label: \"{label}\" }}\n```\n";
            MarkdownDocument document = await engine.ParseAsync(markdown);
            Assert.Empty(document.Diagnostics);
            MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
            Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
            return Assert.IsType<MarkdownVectorScene>(Assert.Single(fragment!.Items).VectorScene);
        }
    }

    [Theory]
    [InlineData("#35;", "#")]
    [InlineData("#128512;", "😀")]
    [InlineData("e#769;", "é")]
    public async Task SelfLoopEntityLabelUsesLiteralTextGeometry(string authoredLabel, string literalText)
    {
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownVectorScene encoded = await ParseSelfLoopAsync(authoredLabel);
        MarkdownVectorScene literal = await ParseSelfLoopAsync(literalText);
        Assert.Contains(encoded.Commands,
            command => command.Kind == MarkdownVectorCommandKind.DrawText && command.Text == literalText);
        Assert.Equal(literal.Viewport, encoded.Viewport);

        async Task<MarkdownVectorScene> ParseSelfLoopAsync(string label)
        {
            MarkdownDocument document = await engine.ParseAsync($"```mermaid\nflowchart LR\nA -->|{label}| A\n```\n");
            Assert.Empty(document.Diagnostics);
            MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
            Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
            return Assert.IsType<MarkdownVectorScene>(Assert.Single(fragment!.Items).VectorScene);
        }
    }

    [Theory]
    [InlineData("\n", "")]
    [InlineData("\r\n", "")]
    [InlineData("\r", "")]
    [InlineData("\n", "---\ntitle: Source provenance\n---\n")]
    [InlineData("\r\n", "---\ntitle: Source provenance\n---\n")]
    [InlineData("\n", "%%{init: {'theme': 'neutral'}}%%\n")]
    public async Task FinalLabelDefinitionOwnsItsSourceAfterPreprocessing(string newline, string prefix)
    {
        const string label = "مرحبا API 👩‍💻";
        string source = (prefix +
            "flowchart LR\n  first[\"مرحبا API 👩‍💻\"] --> second[\"مرحبا API 👩‍💻\"]\n" +
            "  first[\"مرحبا API 👩‍💻\"]").Replace("\n", newline, StringComparison.Ordinal);
        string markdown = "# Provenance 👩‍💻" + newline + newline + "```mermaid" + newline +
            source + newline + "```" + newline;
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync(markdown);
        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        MarkdownVectorSemanticItem[] nodes = scene.Semantics
            .Where(item => item.Role == MarkdownVectorSemanticRole.Node && item.Name == label)
            .ToArray();
        Assert.Equal(2, nodes.Length);

        int superseded = markdown.IndexOf(label, StringComparison.Ordinal);
        int second = markdown.IndexOf(label, superseded + label.Length, StringComparison.Ordinal);
        int final = markdown.IndexOf(label, second + label.Length, StringComparison.Ordinal);
        Assert.True(superseded >= 0 && second > superseded && final > second);
        SourceSpan[] expected = [new(second, label.Length), new(final, label.Length)];
        Assert.Equal(expected, nodes.Select(item => item.SourceSpan).OrderBy(span => span.Start).ToArray());
        Assert.Equal(2, nodes.Select(item => item.SourceId).Distinct().Count());
    }

    [Fact]
    public async Task MixedArabicLatinAndEmojiLabelsKeepExactTextAndTheirOwnSourceOccurrences()
    {
        const string label = "مرحبا API 👩‍💻";
        const string source = "flowchart LR\n  first[\"مرحبا API 👩‍💻\"] --> second[\"مرحبا API 👩‍💻\"]";
        string markdown = $"# Unicode diagram\n\n```mermaid\n{source}\n```\n";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync(markdown);

        Assert.Empty(document.Diagnostics);
        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(block.SourceSpan, out MarkdownContentFragment? fragment));
        MarkdownContent content = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.VectorScene, content.Kind);
        Assert.Equal(source, content.AccessibilityDescription);
        Assert.Contains(label, Assert.IsType<string>(content.SemanticText), StringComparison.Ordinal);

        MarkdownVectorScene scene = Assert.IsType<MarkdownVectorScene>(content.VectorScene);
        (MarkdownVectorSemanticItem Semantic, int Index)[] labelSemantics = scene.Semantics
            .Select(static (semantic, index) => (semantic, index))
            .Where(item => item.semantic.Role == MarkdownVectorSemanticRole.Node &&
                string.Equals(item.semantic.Name, label, StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, labelSemantics.Length);
        Assert.Equal(2, labelSemantics.Select(static item => item.Semantic.SourceId).Distinct().Count());
        foreach ((MarkdownVectorSemanticItem _, int index) in labelSemantics)
        {
            string text = string.Concat(scene.Commands
                .Where(command => command.SemanticIndex == index &&
                    command.Kind == MarkdownVectorCommandKind.DrawText)
                .Select(static command => command.Text));
            Assert.Equal(label, text);
        }

        // The oracle is the authored Markdown string, not the adapter's own
        // source-map output. SourceSpan uses half-open UTF-16 offsets.
        int firstLabelStart = markdown.IndexOf(label, StringComparison.Ordinal);
        Assert.True(firstLabelStart >= 0);
        int secondLabelStart = markdown.IndexOf(
            label,
            firstLabelStart + label.Length,
            StringComparison.Ordinal);
        Assert.True(secondLabelStart > firstLabelStart);

        SourceSpan[] expected =
        [
            new(firstLabelStart, label.Length),
            new(secondLabelStart, label.Length),
        ];
        SourceSpan[] actual = labelSemantics
            .Select(static item => item.Semantic.SourceSpan)
            .OrderBy(static span => span.Start)
            .ToArray();
        Assert.Equal(expected, actual);
    }
}
