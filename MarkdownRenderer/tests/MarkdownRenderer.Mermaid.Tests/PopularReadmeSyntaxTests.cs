using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Mermaid.Tests;

public sealed class PopularReadmeSyntaxTests
{
    [Fact]
    public async Task CommonFlowchartShapesLabelsAndStylesRenderAsNativeDiagram()
    {
        const string source = """
            flowchart TD
              start([Start]) --> decision{Ready?}
              decision -->|yes| work[[Process]]
              decision -.->|no| stop["Wait<br/>and retry →"]
              work --> done([Done])
              style decision fill:#f4b400,color:#202124
            """;
        string markdown = $"```mermaid\n{source}\n```";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync(markdown);

        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(
            block.SourceSpan,
            out MarkdownContentFragment? fragment));
        MarkdownContent diagram = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.VectorScene, diagram.Kind);
        Assert.NotNull(diagram.VectorScene);
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public async Task HtmlFormattedFlowchartLabelsRenderWithoutLiteralMarkupAndRetainSource()
    {
        const string source = """
            flowchart LR
              first["<b>1. Let the Agent act</b><br/><strong>s01 Agent Loop</strong>"] --> second[Done]
            """;
        string markdown = $"```mermaid\n{source}\n```";
        using var renderer = new MermaidRenderer();
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMermaid(renderer)
            .WithParseCacheBudgetBytes(0)
            .Build();

        MarkdownDocument document = await engine.ParseAsync(markdown);

        MarkdownCodeBlock block = Assert.Single(document.GetCodeBlocks());
        Assert.True(document.TryGetBlockExtensionContent(
            block.SourceSpan,
            out MarkdownContentFragment? fragment));
        MarkdownContent diagram = Assert.Single(fragment!.Items);
        Assert.Equal(source, diagram.AccessibilityDescription);
        Assert.DoesNotContain("<b>", diagram.SemanticText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<strong>", diagram.SemanticText, StringComparison.OrdinalIgnoreCase);
        Assert.All(
            diagram.VectorScene!.Commands.Where(command => command.Text is not null),
            command =>
            {
                Assert.DoesNotContain("<b>", command.Text!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("<strong>", command.Text!, StringComparison.OrdinalIgnoreCase);
            });
        Assert.Empty(document.Diagnostics);
    }
}
