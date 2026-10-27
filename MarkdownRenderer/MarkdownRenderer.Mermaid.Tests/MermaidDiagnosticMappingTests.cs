using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Mermaid.Tests;

public sealed class MermaidDiagnosticMappingTests
{
    [Fact]
    public void EveryDiagnosticPreservesSeverityAndMapsFromFenceContent()
    {
        var node = new MarkdownSyntaxNode(
            MarkdownSyntaxKinds.Block.FencedCode,
            new SourceSpan(100, 50),
            literal: "0123456789",
            attributes: new Dictionary<string, string>
            {
                ["contentOffset"] = "10",
            });
        MermaidDiagnostic[] diagnostics =
        [
            new(
                "MMR-WARN",
                MermaidDiagnosticSeverity.Warning,
                "warning",
                new MermaidSourceRange(2, 3)),
            new(
                "MMR-INFO",
                MermaidDiagnosticSeverity.Information,
                "information"),
            new(
                "MMR-ERROR",
                MermaidDiagnosticSeverity.Error,
                "error",
                new MermaidSourceRange(9, 50)),
        ];

        IReadOnlyList<MarkdownDiagnostic> converted =
            MermaidMarkdownExtensions.ConvertDiagnostics(node, node.Literal!, diagnostics);

        Assert.Equal(3, converted.Count);
        Assert.Equal(MarkdownDiagnosticSeverity.Warning, converted[0].Severity);
        Assert.Equal(new SourceSpan(112, 3), converted[0].SourceSpan);
        Assert.Equal(MarkdownDiagnosticSeverity.Information, converted[1].Severity);
        Assert.Equal(node.SourceSpan, converted[1].SourceSpan);
        Assert.Equal(MarkdownDiagnosticSeverity.Error, converted[2].Severity);
        Assert.Equal(new SourceSpan(119, 1), converted[2].SourceSpan);
    }

    [Fact]
    public void InvalidFenceOffsetFallsBackToWholeFence()
    {
        var node = new MarkdownSyntaxNode(
            MarkdownSyntaxKinds.Block.FencedCode,
            new SourceSpan(10, 20),
            literal: "diagram",
            attributes: new Dictionary<string, string>
            {
                ["contentOffset"] = "invalid",
            });

        MarkdownDiagnostic converted = Assert.Single(
            MermaidMarkdownExtensions.ConvertDiagnostics(
                node,
                node.Literal!,
                [new MermaidDiagnostic(
                    "MMR",
                    MermaidDiagnosticSeverity.Error,
                    "message",
                    new MermaidSourceRange(1, 2))]));

        Assert.Equal(node.SourceSpan, converted.SourceSpan);
    }
}
