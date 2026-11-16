using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.RelativeScriptFontSizeFixture;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Inlines.Styling;

/// <summary>
/// Checks that every code surface turns off programming ligatures and
/// contextual alternates, so a code font such as JetBrains Mono draws
/// <c>-&gt;</c>, <c>-&gt;&gt;</c>, <c>--&gt;</c> and <c>=&gt;</c> as typed, while
/// prose keeps the font's default typography.
/// </summary>
/// <remarks>
/// The unpackaged test host may not resolve the app's code font, so these
/// tests read the OpenType features the renderer set on the native layout
/// rather than glyph shapes.
/// </remarks>
public sealed class InlineRunStyleApplierCodeTypographyTests
{
    private const float LayoutWidth = 640f;

    private static readonly CanvasTypographyFeatureName[] DisabledFeatures =
    [
        CanvasTypographyFeatureName.StandardLigatures,
        CanvasTypographyFeatureName.ContextualLigatures,
        CanvasTypographyFeatureName.ContextualAlternates,
        CanvasTypographyFeatureName.DiscretionaryLigatures,
    ];

    [Fact]
    public void InlineCodeSpansDrawArrowsAsTypedAndProseKeepsDefaultTypography()
    {
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            "Call `Alice->>John` then `(_req: Request) => {` here.",
            MarkdownStyleSheet.Empty);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        CanvasTextLayout layout = Measured(box);

        AssertLigaturesOff(layout, IndexOf(box, "->>"), "->>".Length);
        AssertLigaturesOff(layout, IndexOf(box, "=> {"), "=> {".Length);
        AssertDefaultTypography(layout, IndexOf(box, "Call"), "Call ".Length);
        AssertDefaultTypography(layout, IndexOf(box, " here."), " here.".Length);
    }

    [Fact]
    public void InlineCodeInsideALinkDrawsArrowsAsTyped()
    {
        using LayoutSnapshot snapshot = BuildParsedSnapshot(
            "See [the `a->b` rule](https://example.test/rule) => done.",
            MarkdownStyleSheet.Empty);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        CanvasTextLayout layout = Measured(box);

        AssertLigaturesOff(layout, IndexOf(box, "a->b"), "a->b".Length);
        AssertDefaultTypography(layout, IndexOf(box, "the "), "the ".Length);
        AssertDefaultTypography(layout, IndexOf(box, " => done."), " => done.".Length);
    }

    [Theory]
    [InlineData("<kbd>-></kbd> and <kbd>=></kbd>", "->")]
    [InlineData("<samp>[*] --> Still</samp>", "-->")]
    [InlineData("<code>Alice->>John</code>", "->>")]
    public void SafeHtmlCodeElementsDrawArrowsAsTyped(string source, string arrow)
    {
        using LayoutSnapshot snapshot = BuildParsedSnapshot(source, MarkdownStyleSheet.Empty);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        CanvasTextLayout layout = Measured(box);

        AssertLigaturesOff(layout, IndexOf(box, arrow), arrow.Length);
    }

    [Theory]
    [InlineData("```\nAlice->>John: Hello\n[*] --> Still\n```")]
    [InlineData("    Alice->>John: Hello\n    [*] --> Still\n")]
    [InlineData("<pre>Alice->>John: Hello\n[*] --> Still</pre>")]
    public void CodeBlocksDrawArrowsAsTyped(string source)
    {
        using LayoutSnapshot snapshot = BuildParsedSnapshot(source, MarkdownStyleSheet.Empty);
        InlineContainerBox chunk = SingleCodeChunk(snapshot);
        CanvasTextLayout layout = Measured(chunk);

        AssertLigaturesOff(layout, 0, chunk.LayoutBuffer.Length);
    }

    [Fact]
    public void SyntaxHighlightedCodeBlockKeepsLigaturesOffUnderTokenColors()
    {
        // Token colours are applied to an already-styled layout by
        // SetForegroundSpans, the same call the highlighter's result feeds.
        // Fixed spans keep this deterministic: a real tokenizer can exceed
        // its per-line time budget under parallel load and return no spans.
        const string code = "Deno.serve((_req: Request) => {\n  return new Response(\"Hello\");\n});";
        using LayoutSnapshot snapshot = BuildParsedSnapshot("```ts\n" + code + "\n```", MarkdownStyleSheet.Empty);
        InlineContainerBox chunk = SingleCodeChunk(snapshot);
        CanvasTextLayout layout = Measured(chunk);
        int arrow = IndexOf(chunk, "=>");
        CodeBlockHighlightSpan[] spans =
        [
            new(0, arrow, Color.FromArgb(255, 121, 192, 255)),
            new(arrow, "=>".Length, Color.FromArgb(255, 255, 123, 114)),
            new(arrow + "=>".Length, chunk.LayoutBuffer.Length - arrow - "=>".Length, Color.FromArgb(255, 165, 214, 255)),
        ];

        chunk.SetForegroundSpans(spans);

        AssertLigaturesOff(layout, 0, chunk.LayoutBuffer.Length);
    }

    [Fact]
    public void SelectionLayoutDrawsInlineCodeArrowsAsTyped()
    {
        using LayoutSnapshot snapshot = BuildParsedSnapshot("Call `Alice->>John` here.", MarkdownStyleSheet.Empty);
        InlineContainerBox box = Assert.Single(snapshot.Blocks.OfType<InlineContainerBox>());
        _ = Measured(box);

        CanvasTextLayout selection = box.EnsureSelectionLayout(
            Color.FromArgb(255, 255, 255, 255),
            box.GetContainerStyle());

        AssertLigaturesOff(selection, IndexOf(box, "->>"), "->>".Length);
        AssertDefaultTypography(selection, IndexOf(box, "Call"), "Call ".Length);
    }

    private static InlineContainerBox SingleCodeChunk(LayoutSnapshot snapshot)
        => Assert.Single(Assert.Single(CodeBlocks(snapshot.Blocks)).Chunks);

    private static IEnumerable<CodeBlockBox> CodeBlocks(IEnumerable<BlockBox> blocks)
    {
        foreach (BlockBox block in blocks)
        {
            if (block is CodeBlockBox codeBlock)
                yield return codeBlock;
            else if (block is StackBox stack)
            {
                foreach (CodeBlockBox nested in CodeBlocks(stack.Children))
                    yield return nested;
            }
        }
    }

    private static CanvasTextLayout Measured(InlineContainerBox box)
    {
        box.Measure(LayoutWidth);
        return Assert.IsType<CanvasTextLayout>(box.TextLayout);
    }

    private static int IndexOf(InlineContainerBox box, string value)
    {
        int index = box.LayoutBuffer.IndexOf(value, StringComparison.Ordinal);
        Assert.True(index >= 0, $"'{value}' is not in \"{box.LayoutBuffer}\".");
        return index;
    }

    private static void AssertLigaturesOff(CanvasTextLayout layout, int start, int length)
    {
        for (int index = start; index < start + length; index++)
        {
            CanvasTypography? typography = layout.GetTypography(index);
            Assert.True(typography is not null, $"Code character {index} has no typography.");
            CanvasTypographyFeature[] features = typography!.GetFeatures();
            foreach (CanvasTypographyFeatureName name in DisabledFeatures)
            {
                Assert.Contains(features, feature => feature.Name == name && feature.Parameter == 0);
            }

            // Any typography turns DirectWrite's default kerning off, so code
            // must re-enable it to keep its advances.
            Assert.Contains(
                features,
                feature => feature.Name == CanvasTypographyFeatureName.Kerning && feature.Parameter == 1);
        }
    }

    private static void AssertDefaultTypography(CanvasTextLayout layout, int start, int length)
    {
        for (int index = start; index < start + length; index++)
        {
            CanvasTypography? typography = layout.GetTypography(index);
            Assert.True(
                typography is null || typography.GetFeatures().Length == 0,
                $"Prose character {index} has typography features.");
        }
    }
}
