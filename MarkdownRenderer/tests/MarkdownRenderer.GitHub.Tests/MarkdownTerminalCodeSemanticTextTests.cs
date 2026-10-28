using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

public sealed class MarkdownTerminalCodeSemanticTextTests
{
    [Fact]
    public async Task FinalAuthoredBlankCodeLineRemainsInSemanticSelectionAndHtmlCopy()
    {
        // Markdig's code-line projection drops its physical closing separator,
        // so this authored empty line is represented by the remaining LF.
        const string source = "```text\nalpha\n\n```";
        const string expectedCode = "alpha\n";

        using LayoutSnapshot snapshot = await Build(source);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());
        Assert.Equal(expectedCode, code.CodeText);
        Assert.Equal(2, code.LineCount);

        MarkdownSemanticNode semanticCode = Assert.Single(
            MarkdownSemanticDocument.EnumerateDepthFirst(semantic.Root),
            static node => node.Role == MarkdownSemanticRole.CodeBlock);
        Assert.Same(code, semanticCode.Box);
        Assert.Equal(expectedCode, semantic.Text);
        Assert.InRange(semanticCode.TextEnd, semanticCode.TextStart, semantic.Text.Length);

        string selectedCode = semantic.Text.Substring(
            semanticCode.TextStart,
            semanticCode.TextEnd - semanticCode.TextStart);
        Assert.Equal(expectedCode, selectedCode);

        string fragment = MarkdownRenderedHtmlWriter.BuildFragment(
            semantic, semanticCode.TextStart, semanticCode.TextEnd);
        string copiedCode = Assert.Single(XElement.Parse(
            fragment, LoadOptions.PreserveWhitespace).Descendants("code")).Value;
        Assert.Equal(expectedCode, copiedCode);
    }

    [Fact]
    public async Task MultipleAuthoredBlankCodeLinesRemainInSemanticText()
    {
        const string expectedCode = "alpha\n\n";
        using LayoutSnapshot snapshot = await Build("```text\nalpha\n\n\n```");

        CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());
        Assert.Equal(expectedCode, code.CodeText);
        Assert.Equal(expectedCode, snapshot.SemanticDocument.Text);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task SourceAndRenderedRangesPreserveTheirOwnLineEndingContracts(string lineEnding)
    {
        string source = $"```text{lineEnding}alpha{lineEnding}{lineEnding}```";
        using LayoutSnapshot snapshot = await Build(source);
        MarkdownSemanticDocument semantic = snapshot.SemanticDocument;
        Assert.Equal("alpha\n", semantic.Text);
        Assert.True(semantic.TryGetDocumentRange(0, semantic.Text.Length, out DocumentRange range));
        Assert.False(range.IsEmpty);
        Assert.Equal(source, snapshot.SourceMap.Slice(range));
    }

    [Fact]
    public async Task AuthoredCodeLineFeedRemainsWhenCodeBlockIsNotTerminal()
    {
        const string expectedCode = "alpha\n";
        using LayoutSnapshot snapshot = await Build("```text\nalpha\n\n```\n\nfollowing");

        CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());
        Assert.Equal(expectedCode, code.CodeText);
        Assert.Equal("alpha\nfollowing", snapshot.SemanticDocument.Text);
    }

    [Fact]
    public async Task SyntheticFinalBlockSeparatorIsStillExcluded()
    {
        using LayoutSnapshot snapshot = await Build("ordinary paragraph");

        Assert.Equal("ordinary paragraph", snapshot.SemanticDocument.Text);
        Assert.False(snapshot.SemanticDocument.Text.EndsWith('\n'));
    }

    [Fact]
    public async Task EmptyCodeAndFinalDecorativeImageDoNotErasePriorAuthoredCodeLineFeed()
    {
        using LayoutSnapshot emptyCode = await Build("```text\n```");
        Assert.Empty(Assert.Single(emptyCode.Blocks.OfType<CodeBlockBox>()).CodeText);
        Assert.Empty(emptyCode.SemanticDocument.Text);

        const string source = "```text\nalpha\n\n```\n\n![](https://example.invalid/decorative.png)";
        using LayoutSnapshot withDecorativeImage = await Build(source);
        CodeBlockBox code = Assert.Single(withDecorativeImage.Blocks.OfType<CodeBlockBox>());

        Assert.Equal("alpha\n", code.CodeText);
        Assert.Equal("alpha\n", withDecorativeImage.SemanticDocument.Text);
    }

    [Fact]
    public async Task ExplicitTerminalHtmlHardBreakRemainsAuthoredSemanticText()
    {
        using LayoutSnapshot snapshot = await Build("<div>Tail<br><hr></div>");

        Assert.Equal("Tail\n", snapshot.SemanticDocument.Text);
        Assert.Contains(
            snapshot.SemanticDocument.TextSpans,
            static span => span.InlineRun is LineBreakRun && span.TextEnd - span.TextStart == 1);
    }

    private static async Task<LayoutSnapshot> Build(string source)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme(SafeHtmlOptions.Default)
            .Build();
        MarkdownDocument document = await engine.ParseAsync(source);
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(
            engine.PresentationConfiguration);
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
        return new LayoutBuilder(context, semanticDocument: document)
            .Build(document.ParsedDocument!, 600);
    }

}
