using MarkdownRenderer.Extensions;
using Xunit;

namespace MarkdownRenderer.Math.Tests;

public sealed class PopularReadmeSyntaxTests
{
    [Fact]
    public async Task AlignEnvironmentAndEquationTagsRenderAsNativeMath()
    {
        const string tex = """
            \begin{align}
            x &= \frac{a}{b} \tag1 \\
            y &= x^2 \tag{2}
            \end{align}
            """;
        string markdown = $"$$\n{tex}\n$$";
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .WithParseCacheBudgetBytes(0)
            .Build();

        Document.MarkdownDocument document = await engine.ParseAsync(markdown);

        Assert.True(document.TryGetBlockExtensionContent(
            new SourceSpan(0, markdown.Length),
            out MarkdownContentFragment? fragment));
        MarkdownContent formula = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.VectorScene, formula.Kind);
        Assert.Equal(tex, formula.SemanticText);
        Assert.Contains("equation 1", formula.AccessibilityName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("equation 2", formula.AccessibilityName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("begin", formula.AccessibilityName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("a l i g n", formula.AccessibilityName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tag", formula.AccessibilityName, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(document.Diagnostics);
    }
}
