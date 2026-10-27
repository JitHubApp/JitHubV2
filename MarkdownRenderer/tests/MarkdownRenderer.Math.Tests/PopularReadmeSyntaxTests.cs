using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;
using Xunit;

namespace MarkdownRenderer.Math.Tests;

public sealed class PopularReadmeSyntaxTests
{
    [Theory]
    [InlineData(@"\underbrace{x}_{y}")]
    [InlineData(@"\texttt{rollout batch}")]
    [InlineData(@"\mathcal{J}_{PO} = \mathbb{E}_{q \sim P(Q),\, o \sim \pi_\theta(\cdot \mid q)} \left[ \underbrace{\Phi(r_t, A_t)}_{\text{策略目标}} - \underbrace{h(\text{KL}_t)}_{\text{正则项}} \right]")]
    [InlineData(@"\texttt{rollout batch} \rightarrow \texttt{calculate rewards} \rightarrow \texttt{policy update}")]
    public async Task CommandsAbsentFromTheFrozenMathParserRetainExactAtomicFallback(string tex)
    {
        // The pinned parser has no underbrace or texttt command. These cases
        // classify the corpus diagnostics; they do not claim typesetting support
        // or silently replace the commands with different mathematical content.
        string source = "$$\n" + tex + "\n$$";
        MathFormulaRequest request = Assert.Single(MathDelimiterScanner.Scan(source).Formulas);
        MathFormulaResult result = await new MathFormulaProcessor().ProcessAsync(request);
        Assert.Equal(MathFormulaResultKind.InvalidSource, result.Kind);
        Assert.Equal("MATH100", result.Diagnostic?.Code);
        Assert.Equal(source, result.FallbackSource);
        Assert.Null(result.Scene);

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseMathematics()
            .WithParseCacheBudgetBytes(0)
            .Build();
        Document.MarkdownDocument document = await engine.ParseAsync(source);
        Assert.True(document.TryGetBlockExtensionContent(
            new SourceSpan(0, source.Length), out MarkdownContentFragment? fragment));
        MarkdownContent fallback = Assert.Single(fragment!.Items);
        Assert.Equal(MarkdownContentKind.CodeBlock, fallback.Kind);
        Assert.Equal(MarkdownStyleRole.CodeBlock, fallback.StyleRole);
        Assert.Equal(source, fallback.Text);
        Assert.Equal(new SourceSpan(0, source.Length), fallback.SourceSpan);
        Assert.Equal("MATH100", Assert.Single(document.Diagnostics).Code);
    }

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
