using System.Globalization;
using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.CodeBlocks.Internal;
using MarkdownRenderer.Controls.Internal;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Theming;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Accessibility.Internal.Peers;

/// <summary>
/// The UIA contract of a code block peer: a short name that says it is a code
/// block and its language, a TextPattern over exactly the code text, and a
/// document TextPattern that reads each block's code once in reading order.
/// These drive the WinUI-free name and pattern policies the peers delegate to.
/// </summary>
public sealed class MarkdownCodeBlockAutomationTests
{
    private const string TypeScriptSource =
        "```typescript\nconst answer: number = 42;\n\nexport { answer };\n```";

    private const string IndentedSource =
        "Intro paragraph.\n\n    plain()\n      nested()\n    done()\n";

    [Fact]
    public async Task FencedBlockWithLanguageIsNamedForItsLanguage()
    {
        using LayoutSnapshot snapshot = await GitHubReadmeEngineLayoutFixture.BuildAsync(TypeScriptSource);
        MarkdownSemanticNode node = SingleCodeNode(snapshot.SemanticDocument);
        CodeBlockBox code = Assert.IsType<CodeBlockBox>(node.Box);

        string name = GetName(snapshot.SemanticDocument, node);

        Assert.Equal($"{code.LanguageDisplay} code block", name);
        Assert.Equal("TypeScript code block", name);
    }

    [Fact]
    public async Task IndentedBlockWithoutLanguageKeepsTheGenericName()
    {
        using LayoutSnapshot snapshot = await GitHubReadmeEngineLayoutFixture.BuildAsync(IndentedSource);
        MarkdownSemanticNode node = SingleCodeNode(snapshot.SemanticDocument);

        Assert.IsType<CodeBlockBox>(node.Box);
        Assert.Null(node.CodeLanguage);
        Assert.Equal("Code block", GetName(snapshot.SemanticDocument, node));
    }

    [Fact]
    public async Task LanguageNameIsLocalizedThroughTheHostStringProvider()
    {
        using LayoutSnapshot snapshot = await GitHubReadmeEngineLayoutFixture.BuildAsync(TypeScriptSource);
        MarkdownSemanticNode node = SingleCodeNode(snapshot.SemanticDocument);
        var provider = new KeyedStringProvider(MarkdownStringKeys.CodeBlockLanguageName, "Codeblock ({0})");

        string name = MarkdownNodeAutomationText.GetName(
            CreateLocalization(provider),
            snapshot.SemanticDocument,
            node,
            task: null);

        Assert.Equal("Codeblock (TypeScript)", name);
    }

    [Fact]
    public async Task HugeBlockKeepsAShortNameAndExposesItsCodeThroughTheTextPattern()
    {
        string body = string.Join("\n", Enumerable.Range(0, 4_000).Select(static line => $"line {line} = value;"));
        using LayoutSnapshot snapshot = await GitHubReadmeEngineLayoutFixture.BuildAsync($"```text\n{body}\n```");
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode node = SingleCodeNode(document);

        string name = GetName(document, node);

        Assert.True(name.Length < 64, $"The code block name holds {name.Length} characters.");
        Assert.DoesNotContain("line 0", name, StringComparison.Ordinal);
        Assert.Equal(body, GetPatternText(document, node));
    }

    [Theory]
    [InlineData(TypeScriptSource)]
    [InlineData(IndentedSource)]
    public async Task TextPatternCoversExactlyTheCodeWithItsLineBreaks(string source)
    {
        using LayoutSnapshot snapshot = await GitHubReadmeEngineLayoutFixture.BuildAsync(source);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode node = SingleCodeNode(document);
        CodeBlockBox code = Assert.IsType<CodeBlockBox>(node.Box);

        string patternText = GetPatternText(document, node);

        Assert.Equal(code.CodeText, patternText);
        Assert.Contains('\n', patternText);
    }

    [Fact]
    public async Task HighlightedBlockKeepsItsNameAndExactCodeText()
    {
        using LayoutSnapshot snapshot = await GitHubReadmeEngineLayoutFixture.BuildAsync(TypeScriptSource);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());
        var key = new CodeBlockHighlightCacheKey(
            code.CodeLanguage!, CodeHash: 1, code.CodeText.Length,
            MarkdownThemeVariant.Light, ProviderIdentity: 1, ProviderRevision: 1);
        CodeBlockHighlightSpan[] tokens =
        [
            new(0, 5, Color.FromArgb(255, 0, 0, 200)),
            new(6, 6, Color.FromArgb(255, 0, 120, 0)),
            new(14, 6, Color.FromArgb(255, 160, 0, 0)),
        ];
        Assert.True(code.ApplySyntaxHighlighting(key, tokens));

        MarkdownSemanticDocument highlighted = MarkdownSemanticDocument.Build(snapshot);
        MarkdownSemanticNode node = SingleCodeNode(highlighted);

        Assert.Equal("TypeScript code block", GetName(highlighted, node));
        Assert.Equal(code.CodeText, GetPatternText(highlighted, node));
        Assert.Equal(1, CountOccurrences(highlighted.Text, code.CodeText));
    }

    [Fact]
    public async Task DocumentTextReadsEachCodeBlockOnceInReadingOrder()
    {
        const string source =
            "Before the code.\n\n" +
            "```typescript\nlet first = 1;\nlet second = 2;\n```\n\n" +
            "Between the blocks.\n\n" +
            "    indented()\n    again()\n\n" +
            "After the code.";
        using LayoutSnapshot snapshot = await GitHubReadmeEngineLayoutFixture.BuildAsync(source);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        CodeBlockBox[] blocks = snapshot.Blocks.OfType<CodeBlockBox>().ToArray();
        Assert.Equal(2, blocks.Length);

        string text = document.Text;
        int before = text.IndexOf("Before the code.", StringComparison.Ordinal);
        int fenced = text.IndexOf(blocks[0].CodeText, StringComparison.Ordinal);
        int between = text.IndexOf("Between the blocks.", StringComparison.Ordinal);
        int indented = text.IndexOf(blocks[1].CodeText, StringComparison.Ordinal);
        int after = text.IndexOf("After the code.", StringComparison.Ordinal);

        Assert.True(before >= 0 && before < fenced && fenced < between && between < indented && indented < after,
            $"Reading order is broken in: {text}");
        Assert.All(blocks, block => Assert.Equal(1, CountOccurrences(text, block.CodeText)));
        // The header label and the Copy button are chrome, not document text.
        Assert.DoesNotContain("TypeScript", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Copy", text, StringComparison.Ordinal);

        MarkdownSemanticNode[] nodes = CodeNodes(document).ToArray();
        Assert.Equal(2, nodes.Length);
        Assert.Equal(blocks[0].CodeText, GetPatternText(document, nodes[0]));
        Assert.Equal(blocks[1].CodeText, GetPatternText(document, nodes[1]));
    }

    [Fact]
    public async Task PreformattedTableCellIsNamedAsACodeBlockRatherThanByItsText()
    {
        const string code = "first line\nsecond line";
        using LayoutSnapshot snapshot = await GitHubReadmeEngineLayoutFixture.BuildAsync(
            $"<table><tr><td><pre>{code}</pre></td></tr></table>");
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode node = SingleCodeNode(document);

        Assert.NotNull(node.InlineBox);
        Assert.Equal("Code block", GetName(document, node));
        Assert.Equal(code, GetPatternText(document, node).TrimEnd('\n'));
    }

    [Fact]
    public async Task NonCodeNodesDoNotGainACodeTextPattern()
    {
        using LayoutSnapshot snapshot = await GitHubReadmeEngineLayoutFixture.BuildAsync(
            "- first item\n- second item");
        MarkdownSemanticNode list = Assert.Single(
            MarkdownSemanticDocument.EnumerateDepthFirst(snapshot.SemanticDocument.Root),
            static node => node.Role == MarkdownSemanticRole.List);

        Assert.False(MarkdownNodeAutomationDescriptor.TryGetTextPatternRange(list, out _, out _));
    }

    private static string GetName(MarkdownSemanticDocument document, MarkdownSemanticNode node) =>
        MarkdownNodeAutomationText.GetName(CreateLocalization(provider: null), document, node, task: null);

    private static string GetPatternText(MarkdownSemanticDocument document, MarkdownSemanticNode node)
    {
        Assert.True(
            MarkdownNodeAutomationDescriptor.TryGetTextPatternRange(node, out int start, out int end),
            "The code block peer does not expose a TextPattern.");
        Assert.InRange(start, 0, document.Text.Length);
        Assert.InRange(end, start, document.Text.Length);
        return document.Text.Substring(start, end - start);
    }

    private static MarkdownSemanticNode SingleCodeNode(MarkdownSemanticDocument document) =>
        Assert.Single(CodeNodes(document));

    private static IEnumerable<MarkdownSemanticNode> CodeNodes(MarkdownSemanticDocument document) =>
        MarkdownSemanticDocument.EnumerateDepthFirst(document.Root)
            .Where(static node => node.Role == MarkdownSemanticRole.CodeBlock);

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static RendererLocalization CreateLocalization(IMarkdownStringProvider? provider) =>
        new(new LocalizationHost(provider), new PublishedLayout());

    private sealed class LocalizationHost(IMarkdownStringProvider? provider) : IRendererLocalizationHost
    {
        public string Language => "en-US";

        public string? EnvironmentLanguage => null;

        public IMarkdownStringProvider? StringProvider => provider;
    }

    private sealed class KeyedStringProvider(string key, string value) : IMarkdownStringProvider
    {
        public string? GetString(string requestedKey, CultureInfo culture) =>
            string.Equals(requestedKey, key, StringComparison.Ordinal) ? value : null;
    }
}
