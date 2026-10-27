using System.Reflection;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Math;
using MarkdownRenderer.Mermaid;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Svg.Resvg;
using MarkdownRenderer.SyntaxHighlighting.TextMate;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.All;
using MarkdownRenderer.SyntaxHighlighting.TextMate.Grammars.Common;

namespace MarkdownRenderer.PublicApi.Tests;

/// <summary>
/// Every assembly the renderer packages ship, each found through one of its
/// public types. The MarkdownRenderer and MarkdownRenderer.All meta-packages
/// ship no assembly of their own.
/// </summary>
internal static class ShippingAssemblies
{
    public static IReadOnlyList<Assembly> All { get; } =
    [
        typeof(MarkdownEngine).Assembly,
        typeof(MarkdownScrollView).Assembly,
        typeof(GfmExtensions).Assembly,
        typeof(GitHubReadmeExtensions).Assembly,
        typeof(SafeHtmlFeature).Assembly,
        typeof(MathFeature).Assembly,
        typeof(MermaidRenderer).Assembly,
        typeof(MarkdownPerformanceSession).Assembly,
        typeof(ResvgMarkdownSvgRenderer).Assembly,
        typeof(TextMateSyntaxHighlightingExtensions).Assembly,
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
