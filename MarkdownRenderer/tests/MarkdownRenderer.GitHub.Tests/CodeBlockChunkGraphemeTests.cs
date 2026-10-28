using System.Globalization;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class CodeBlockChunkGraphemeTests
{
    [Theory]
    [InlineData("😀", 1)]
    [InlineData("e\u0301", 1)]
    [InlineData("👩\u200d👩\u200d👧\u200d👦", 3)]
    public async Task NativeCodeChunksNeverSplitAnAuthoredGrapheme(string grapheme, int unitsBeforeBoundary)
    {
        const int currentChunkBudget = 32_768;
        // Keep the original grapheme exactly across the preferred chunk
        // target, then add a separate authored line so the test still
        // exercises multiple chunks after chunks stop splitting logical lines.
        string body = new string('x', currentChunkBudget - unitsBeforeBoundary) +
                      grapheme +
                      new string('y', 80) +
                      "\nnext line";
        string source = $"```text\n{body}\n```";
        using LayoutSnapshot snapshot = await Build(source);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());
        Assert.Equal(body, code.CodeText);
        Assert.True(code.Chunks.Count > 1);

        var authoredBoundaries = new HashSet<int>(StringInfo.ParseCombiningCharacters(body)) { body.Length };
        foreach (InlineContainerBox chunk in code.Chunks)
        {
            TextRun run = Assert.IsType<TextRun>(Assert.Single(chunk.Runs));
            Assert.Equal(body.Substring(chunk.CodeBlockTextOffset, chunk.CodeBlockTextLength), run.Text);
            Assert.Contains(chunk.CodeBlockTextOffset, authoredBoundaries);
            Assert.Contains(chunk.CodeBlockTextOffset + chunk.CodeBlockTextLength, authoredBoundaries);
            Assert.False(char.IsLowSurrogate(run.Text[0]));
            Assert.False(char.IsHighSurrogate(run.Text[^1]));
        }
    }

    private static async Task<LayoutSnapshot> Build(string source)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();
        MarkdownDocument document = await engine.ParseAsync(source);
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        var style = new ElementStyle();
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = style,
                [MarkdownElementKeys.CodeBlock] = style,
            },
            new Dictionary<string, ElementStyleOverride>(),
            default(Color), default, default, default,
            isDark: false, isHighContrast: false, textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true), theme,
            new MarkdownSourceMap(source), registry, FlowDirection.LeftToRight);
        return new LayoutBuilder(context, semanticDocument: document).Build(document.ParsedDocument!, 600);
    }
}
