using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Extensions.Vectors;

namespace MarkdownRenderer.Mermaid.Tests;

/// <summary>Default markdown shape-data labels keep literal entity text, styles, spacing and geometry.</summary>
public sealed class MermaidMarkdownShapeDataTests
{
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
}
