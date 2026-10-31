using System.Reflection;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Svg.Resvg;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Engine;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;

namespace MarkdownRenderer.PublicApi.Tests;

/// <summary>
/// Every assembly the renderer packages ship, each found through one of its
/// public types. The MarkdownRenderer.All meta-package ships no assembly of
/// its own.
/// </summary>
internal static class ShippingAssemblies
{
    public static IReadOnlyList<Assembly> All { get; } =
    [
        typeof(MarkdownEngine).Assembly,
        typeof(MathFeature).Assembly,
        typeof(MermaidRenderer).Assembly,
        typeof(ResvgMarkdownSvgRenderer).Assembly,
        typeof(TextMateSyntaxHighlightingExtensions).Assembly,
        typeof(TextMateSharpGrammarProvider).Assembly,
        typeof(CommonTextMateGrammarProvider).Assembly,
        typeof(AllTextMateGrammarProvider).Assembly,
    ];

    public static IEnumerable<object[]> Names()
        => All.Select(static assembly => new object[] { assembly.GetName().Name! });

    public static Assembly Get(string name)
        => All.Single(assembly => assembly.GetName().Name == name);

    public static IEnumerable<Type> ExportedTypes()
        => All.SelectMany(static assembly => assembly.GetExportedTypes());
}
