using System.Xml.Linq;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class MarkdownRenderedHtmlCodeCopyTests
{
    [Theory]
    [InlineData("```text\nif ready:\n    run()\n\tfinish()\n```", "if ready:\n    run()\n\tfinish()")]
    [InlineData("    if ready:\n        run()\n    \tfinish()", "if ready:\n    run()\n\tfinish()")]
    [InlineData("<pre><code>if ready:\n    run()\n\tfinish()</code></pre>", "if ready:\n    run()\n\tfinish()")]
    public async Task BlockCodeHtmlCopyKeepsIndentationAndTabs(string source, string expected)
    {
        using LayoutSnapshot original = await Build(source);
        MarkdownSemanticDocument semantic = original.SemanticDocument;
        MarkdownSemanticNode code = Assert.Single(
            MarkdownSemanticDocument.EnumerateDepthFirst(semantic.Root),
            static node => node.Role == MarkdownSemanticRole.CodeBlock);
        Assert.Equal(expected, GetRawText(semantic, code).TrimEnd('\r', '\n'));

        string fragment = MarkdownRenderedHtmlWriter.BuildFragment(semantic, code.TextStart, code.TextEnd);
        XElement copiedCode = Assert.Single(XElement.Parse(fragment, LoadOptions.PreserveWhitespace).Descendants("code"));
        Assert.Equal(GetRawText(semantic, code), copiedCode.Value);
        using LayoutSnapshot pasted = await Build(fragment);

        // This is a content oracle, not a comparison with another browser:
        // the receiver is our existing supported HTML renderer. Code's tabs
        // and indentation must survive the formatted clipboard representation.
        Assert.Equal(expected, pasted.SemanticDocument.Text.TrimEnd('\r', '\n'));
    }

    [Theory]
    [InlineData("`left  right`", "left  right")]
    [InlineData("` <tag> & literal `", "<tag> & literal")]
    public async Task InlineCodeHtmlCopyKeepsItsDisplayedWhitespaceAndEscapedText(
        string source,
        string expected)
    {
        using LayoutSnapshot original = await Build(source);
        MarkdownSemanticDocument semantic = original.SemanticDocument;
        Assert.Equal(expected, semantic.Text.TrimEnd('\r', '\n'));

        string fragment = MarkdownRenderedHtmlWriter.BuildFragment(
            semantic, 0, semantic.Text.TrimEnd('\r', '\n').Length);
        XElement copiedCode = Assert.Single(XElement.Parse(fragment).Descendants("code"));
        Assert.Equal(expected, copiedCode.Value);
        Assert.Equal("white-space: pre-wrap", copiedCode.Attribute("style")?.Value);
        Assert.DoesNotContain("<pre>", fragment, StringComparison.Ordinal);
        // Inline white-space CSS is deliberately outside the native HTML
        // receiver's frozen subset. Verify its standard clipboard declaration
        // and exact characters rather than claiming that subset implements it.
    }

    [Theory]
    [InlineData("  first")]
    [InlineData("  ")]
    [InlineData("\n\tsecond & <script>stay()</script>")]
    [InlineData("\n")]
    [InlineData("\tsecond & <script>stay()</script>\n")]
    public async Task PartialCodeCopyIncludesExactlyTheSelectedCharacters(string selected)
    {
        const string body = "  first\n\tsecond & <script>stay()</script>\n  third\n";
        using LayoutSnapshot original = await Build($"```text\n{body}```");
        MarkdownSemanticDocument semantic = original.SemanticDocument;
        int start = semantic.Text.IndexOf(selected, StringComparison.Ordinal);
        Assert.True(start >= 0);

        string fragment = MarkdownRenderedHtmlWriter.BuildFragment(semantic, start, start + selected.Length);
        XElement root = XElement.Parse(fragment, LoadOptions.PreserveWhitespace);
        XElement pre = Assert.Single(root.Elements("pre"));
        XElement code = Assert.Single(pre.Elements("code"));
        Assert.Equal(selected, code.Value);
        Assert.Equal(selected, root.Value);
        Assert.Empty(root.Descendants("script"));
        Assert.Empty(root.Descendants("br"));
    }

    [Fact]
    public async Task VirtualizedCodeChunksProduceOnePreformattedRegion()
    {
        string body = string.Join("\n", Enumerable.Range(0, 270).Select(index => $"\tline {index}  value"));
        using LayoutSnapshot original = await Build($"```text\n{body}\n```");
        MarkdownSemanticDocument semantic = original.SemanticDocument;
        MarkdownSemanticNode codeNode = Assert.Single(
            MarkdownSemanticDocument.EnumerateDepthFirst(semantic.Root),
            static node => node.Role == MarkdownSemanticRole.CodeBlock);
        Assert.True(Assert.IsType<CodeBlockBox>(codeNode.Box).Chunks.Count > 1);
        Assert.Equal(body, GetRawText(semantic, codeNode));

        XElement root = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(
            semantic, codeNode.TextStart, codeNode.TextEnd), LoadOptions.PreserveWhitespace);
        XElement code = Assert.Single(Assert.Single(root.Elements("pre")).Elements("code"));
        Assert.Equal(body, code.Value);
        Assert.Equal(body, root.Value);
    }

    [Fact]
    public async Task SeparateCodeBlocksDoNotAbsorbNeighboringTextOrLinks()
    {
        const string source = "before **strong**\n\n```text\n  alpha\n```\n\n" +
            "middle [link](https://example.test/)\n\n```text\n\tomega\n```\n\nafter";
        using LayoutSnapshot original = await Build(source);
        MarkdownSemanticDocument semantic = original.SemanticDocument;
        XElement root = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(
            semantic, 0, semantic.Text.Length), LoadOptions.PreserveWhitespace);
        Assert.Equal(new[] { "  alpha", "\tomega" }, root.Elements("pre").Select(element => element.Value));
        Assert.Equal("strong", Assert.Single(root.Elements("strong")).Value);
        XElement link = Assert.Single(root.Elements("a"));
        Assert.Equal("link", link.Value);
        Assert.Equal("https://example.test/", link.Attribute("href")?.Value);
        Assert.StartsWith("before strong", root.Value, StringComparison.Ordinal);
        Assert.EndsWith("after", root.Value.TrimEnd('\n'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinkedInlineCodeKeepsWhitespaceAndItsSafeDestination()
    {
        using LayoutSnapshot original = await Build("[`left  right`](https://example.test/?q=one&v=two)");
        MarkdownSemanticDocument semantic = original.SemanticDocument;
        Assert.Equal("left  right", semantic.Text);
        LinkRun run = Assert.IsType<LinkRun>(Assert.Single(semantic.TextSpans).InlineRun);
        LinkInlineStyleMap styles = Assert.IsType<LinkInlineStyleMap>(run.InlineStyleMap);
        Assert.Equal(1, styles.Count);
        Assert.Equal(MarkdownElementKeys.CodeInline, styles.GetSpan(0).Path.StyleKey);

        XElement root = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(
            semantic, 0, semantic.Text.Length), LoadOptions.PreserveWhitespace);
        XElement link = Assert.Single(root.Elements("a"));
        XElement code = Assert.Single(link.Elements("code"));
        Assert.Equal("https://example.test/?q=one&v=two", link.Attribute("href")?.Value);
        Assert.Equal("left  right", code.Value);
        Assert.Equal("white-space: pre-wrap", code.Attribute("style")?.Value);
    }

    [Theory]
    [InlineData("https://example.test/", true)]
    [InlineData("javascript:alert(1)", false)]
    public async Task MixedLinkCodeStylesAreClippedAndUnsafeDestinationsStayInert(
        string destination,
        bool safeDestination)
    {
        using LayoutSnapshot original = await Build($"[before `left  right` after]({destination})");
        MarkdownSemanticDocument semantic = original.SemanticDocument;
        Assert.Equal("before left  right after", semantic.Text);
        XElement whole = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(
            semantic, 0, semantic.Text.Length), LoadOptions.PreserveWhitespace);
        Assert.Equal(semantic.Text, whole.Value);
        Assert.Equal("left  right", Assert.Single(whole.Descendants("code")).Value);
        Assert.Equal(safeDestination ? 1 : 0, whole.Descendants("a").Count());

        const string selected = "ft  ri";
        int start = semantic.Text.IndexOf(selected, StringComparison.Ordinal);
        Assert.True(start >= 0);
        XElement partial = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(
            semantic, start, start + selected.Length), LoadOptions.PreserveWhitespace);
        Assert.Equal(selected, partial.Value);
        XElement code = Assert.Single(partial.Descendants("code"));
        Assert.Equal(selected, code.Value);
        Assert.Equal("white-space: pre-wrap", code.Attribute("style")?.Value);
        Assert.Equal(safeDestination ? 1 : 0, partial.Descendants("a").Count());
        Assert.Empty(partial.Descendants("script"));
    }

    [Fact]
    public async Task NestedLinkStylePathsRetainAncestryAcrossPartialSelection()
    {
        using LayoutSnapshot original = await Build(
            "[**bold `left  right`** and *tail*](https://example.test/)");
        MarkdownSemanticDocument semantic = original.SemanticDocument;
        const string selected = "old left  right and ta";
        int start = semantic.Text.IndexOf(selected, StringComparison.Ordinal);
        Assert.True(start >= 0);
        XElement root = XElement.Parse(MarkdownRenderedHtmlWriter.BuildFragment(
            semantic, start, start + selected.Length), LoadOptions.PreserveWhitespace);
        Assert.Equal(selected, root.Value);
        XElement link = Assert.Single(root.Elements("a"));
        XElement code = Assert.Single(link.Descendants("code"));
        Assert.Equal("strong", code.Parent?.Name.LocalName);
        Assert.Equal("left  right", code.Value);
        Assert.Equal("white-space: pre-wrap", code.Attribute("style")?.Value);
        Assert.Equal("ta", Assert.Single(link.Descendants("em")).Value);
    }

    private static string GetRawText(MarkdownSemanticDocument document, MarkdownSemanticNode node) =>
        document.Text.Substring(node.TextStart, node.TextEnd - node.TextStart);

    private static async Task<LayoutSnapshot> Build(string source)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme(SafeHtmlOptions.Default)
            .Build();
        MarkdownDocument document = await engine.ParseAsync(source);
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        var style = new ElementStyle();
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = style,
                [MarkdownElementKeys.CodeInline] = style,
                [MarkdownElementKeys.CodeBlock] = style,
            },
            new Dictionary<string, ElementStyleOverride>(),
            default(Color), default, default, default,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);
        return new LayoutBuilder(context, semanticDocument: document).Build(document.ParsedDocument!, 600);
    }
}
