using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class SummaryCodeSourceProjectionTests
{
    [Theory]
    [InlineData(false, "\n")]
    [InlineData(false, "\r\n")]
    [InlineData(true, "\n")]
    [InlineData(true, "\r\n")]
    public async Task DetailsSummaryCodeMapsNormalizedNewlinesToExactOriginalTerminators(
        bool preparedTablePrefix,
        string newline)
    {
        const string firstLine = "SUMMARY-CODE-FIRST-LINE";
        const string secondLine = "SUMMARY-CODE-SECOND-LINE";
        string[] lines = preparedTablePrefix
            ?
            [
                "| Name | Value |",
                "| --- | --- |",
                "| alpha | beta |",
                "row-without-a-pipe",
                string.Empty,
                "<details open><summary>Summary before a discarded gap",
                string.Empty,
                "  ```text",
                "  " + firstLine,
                "  " + secondLine,
                "  ```",
                "  </summary>",
                "<p>Disclosure body</p>",
                "</details>",
            ]
            :
            [
                "<details open><summary>Summary before a discarded gap",
                string.Empty,
                "  ```text",
                "  " + firstLine,
                "  " + secondLine,
                "  ```",
                "  </summary>",
                "<p>Disclosure body</p>",
                "</details>",
            ];
        string source = string.Join(newline, lines);

        Gfm029Compatibility.PreparedSource prepared = Gfm029Compatibility.PrepareSource(
            source,
            enableTables: true);
        Assert.Equal(preparedTablePrefix, prepared.HasChanges);

        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();
        MarkdownDocument document = await engine.ParseAsync(source);
        GitHubDetailsBlock details = Assert.Single(Enumerate(document.ParsedDocument!).OfType<GitHubDetailsBlock>());

        int summaryFirstLine = details.SummaryMarkup.IndexOf(firstLine, System.StringComparison.Ordinal);
        Assert.True(summaryFirstLine >= 0);
        int summaryNewline = summaryFirstLine + firstLine.Length;
        int expectedSourceNewline = source.IndexOf(firstLine, System.StringComparison.Ordinal) + firstLine.Length;
        var expectedTerminator = new SourceSpan(expectedSourceNewline, newline.Length);

        MarkdownSourceProjection summaryProjection = details.CreateSummarySourceProjection();
        Assert.True(summaryProjection.TryMapRange(summaryNewline, 1, out SourceSpan summaryMapped));
        Assert.Equal(expectedTerminator, summaryMapped);
        Assert.Equal(newline, source.Substring(summaryMapped.Start, summaryMapped.Length));

        ThemeSnapshot theme = CreateTheme();
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        var layoutContext = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);
        using LayoutSnapshot snapshot = new LayoutBuilder(layoutContext, semanticDocument: document)
            .Build(document.ParsedDocument!, availableWidth: 640f);

        CodeBlockBox code = Assert.Single(snapshot.Blocks.SelectMany(EnumerateCodeBlocks));
        Assert.Equal(firstLine + "\n" + secondLine, code.CodeText);
        InlineContainerBox chunk = Assert.Single(code.Chunks);
        TextRun run = Assert.IsType<TextRun>(Assert.Single(chunk.Runs));
        int codeNewline = firstLine.Length;
        var range = new DocumentRange(
            new DocumentPosition(chunk.BlockIndex, run.InlineIndex, codeNewline),
            new DocumentPosition(chunk.BlockIndex, run.InlineIndex, codeNewline + 1));

        Assert.True(snapshot.SourceMap.TryMapRange(range, out SourceSpan codeMapped));
        Assert.Equal(expectedTerminator, codeMapped);
        Assert.Equal(newline, snapshot.SourceMap.Slice(range));
    }

    private static ThemeSnapshot CreateTheme()
    {
        var codeStyle = new ElementStyle();
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = codeStyle,
                [MarkdownElementKeys.CodeBlock] = codeStyle,
                [MarkdownElementKeys.CodeBlockHeader] = codeStyle,
                [MarkdownElementKeys.CodeBlockLanguage] = codeStyle,
                [MarkdownElementKeys.CodeBlockGutter] = codeStyle,
                [MarkdownElementKeys.CodeBlockLineNumber] = codeStyle,
                [MarkdownElementKeys.Table] = codeStyle,
                [MarkdownElementKeys.TableCell] = codeStyle,
            },
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1f);
    }

    private static IEnumerable<Markdig.Syntax.Block> Enumerate(Markdig.Syntax.ContainerBlock root)
    {
        foreach (Markdig.Syntax.Block block in root)
        {
            yield return block;
            if (block is Markdig.Syntax.ContainerBlock container)
            {
                foreach (Markdig.Syntax.Block child in Enumerate(container))
                    yield return child;
            }
        }
    }

    private static IEnumerable<CodeBlockBox> EnumerateCodeBlocks(BlockBox block)
    {
        if (block is CodeBlockBox code)
            yield return code;
        if (block is StackBox stack)
        {
            foreach (BlockBox child in stack.Children)
            {
                foreach (CodeBlockBox nested in EnumerateCodeBlocks(child))
                    yield return nested;
            }
        }
        else if (block is ListItemBox listItem)
        {
            foreach (CodeBlockBox nested in EnumerateCodeBlocks(listItem.Content))
                yield return nested;
        }
    }
}
