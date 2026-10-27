using System;
using System.Collections.Generic;
using System.Linq;
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

[Collection(SharedCanvasBitmapCacheTestCollection.Name)]
public sealed class LargeCodeBlockSourceMappingTests
{
    private const int BodyLength = 20 * 1024;

    [Theory]
    [InlineData("\n", "quote")]
    [InlineData("\r\n", "quote")]
    [InlineData("\r", "quote")]
    [InlineData("\n", "list")]
    [InlineData("\r\n", "quote-list")]
    [InlineData("\n", "indented-tab")]
    [InlineData("\n", "indented-tab-extra")]
    [InlineData("\r\n", "indented-tab-extra")]
    public void LargeMultilineCodeMapsExactBodyAndLineEndingWithoutContainerPrefixes(string newline, string container)
    {
        string line1 = new string('x', 18 * 1024);
        char[] secondLine = Enumerable.Repeat('y', 18 * 1024).ToArray();
        secondLine[9 * 1024] = 'M';
        string line2 = new string(secondLine);
        string source = BuildWrappedSource(newline, container, line1, line2);
        int authoredLine1Start = source.IndexOf(line1, StringComparison.Ordinal);
        Assert.True(authoredLine1Start >= 0);
        int authoredNewlineStart = authoredLine1Start + line1.Length;
        int authoredMarker = source.IndexOf('M');
        Assert.True(authoredMarker > authoredNewlineStart);

        using LayoutSnapshot snapshot = Build(source);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.SelectMany(EnumerateCodeBlocks));
        string renderedIndent = container == "indented-tab-extra" ? "  " : string.Empty;
        Assert.StartsWith(renderedIndent + line1 + "\n" + renderedIndent + line2, code.CodeText, StringComparison.Ordinal);

        int codeOffset = container == "indented-tab-extra"
            ? code.CodeText.IndexOf('M')
            : line1.Length + 1 + (9 * 1024);
        Assert.Equal("M", code.CodeText.Substring(codeOffset, 1));
        AssertMappedCharacter(snapshot, code, codeOffset, authoredMarker, "M");
        AssertMappedCharacter(snapshot, code, code.CodeText.IndexOf('\n'), authoredNewlineStart, newline);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void CodeInTrimmedDisclosureSummaryMapsThroughItsSourceProjection(string newline)
    {
        const string firstLine = "SUMMARY-CODE-BEGIN-MIDDLE-MARKER-END";
        const string secondLine = "SUMMARY-CODE-SECOND-LINE";
        string source = string.Join(newline,
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
        ]);

        using LayoutSnapshot snapshot = Build(source);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.SelectMany(EnumerateCodeBlocks));
        Assert.Equal(firstLine + "\n" + secondLine, code.CodeText);

        int authoredFirstLineStart = source.IndexOf(firstLine, StringComparison.Ordinal);
        Assert.True(authoredFirstLineStart >= 0);
        int markerOffset = firstLine.IndexOf("MIDDLE-MARKER", StringComparison.Ordinal);
        int authoredMarker = authoredFirstLineStart + markerOffset;
        AssertMappedCharacter(snapshot, code, markerOffset, authoredMarker, "M");

        int authoredLineBreak = authoredFirstLineStart + firstLine.Length;
        AssertMappedCharacter(snapshot, code, firstLine.Length, authoredLineBreak, newline);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async System.Threading.Tasks.Task CodeInDisclosureSummaryMapsThroughPreparedTablePrefix(string newline)
    {
        const string firstLine = "PREPARED-SUMMARY-CODE-MIDDLE-MARKER-END";
        const string secondLine = "PREPARED-SUMMARY-CODE-SECOND-LINE";
        string source = string.Join(newline,
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
        ]);

        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source);
        Markdig.Syntax.MarkdownDocument parsed =
            Assert.IsType<Markdig.Syntax.MarkdownDocument>(document.ParsedDocument);
        GitHubDetailsBlock details = Assert.Single(parsed.OfType<GitHubDetailsBlock>());
        int summaryFirstLine = details.SummaryMarkup.IndexOf(firstLine, StringComparison.Ordinal);
        Assert.True(summaryFirstLine >= 0);
        int summaryLineBreak = summaryFirstLine + firstLine.Length;
        int authoredFirstLineStart = source.IndexOf(firstLine, StringComparison.Ordinal);
        Assert.True(authoredFirstLineStart >= 0);
        int authoredLineBreak = authoredFirstLineStart + firstLine.Length;
        Assert.True(details.CreateSummarySourceProjection().TryMapRange(
            summaryLineBreak,
            1,
            out SourceSpan summaryMappedLineBreak));
        Assert.Equal(new SourceSpan(authoredLineBreak, newline.Length), summaryMappedLineBreak);

        using LayoutSnapshot snapshot = BuildGitHubReadmeLayout(engine, source, document);
        Assert.Contains(snapshot.Blocks, static block => block is MarkdownRenderer.Layout.Boxes.TableBox);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.SelectMany(EnumerateCodeBlocks));
        Assert.Equal(firstLine + "\n" + secondLine, code.CodeText);

        int markerOffset = firstLine.IndexOf("MIDDLE-MARKER", StringComparison.Ordinal);
        int authoredMarker = authoredFirstLineStart + markerOffset;
        AssertMappedCharacter(snapshot, code, markerOffset, authoredMarker, "M");

        AssertMappedCharacter(snapshot, code, firstLine.Length, authoredLineBreak, newline);
    }

    [Fact]
    public async System.Threading.Tasks.Task GitHubTableSourcePreparationPreservesFollowingCodeBlockSourceMap()
    {
        const string codeLine = "GFM-INSERTED-TABLE-CODE-MIDDLE-MARKER-END";
        const string source = "| Name | Value |\n" +
            "| --- | --- |\n" +
            "| alpha | beta |\n" +
            "row-without-a-pipe\n\n" +
            "```text\n" +
            codeLine + "\n" +
            "```\n";
        Assert.Equal(1, source.Split(codeLine, StringSplitOptions.None).Length - 1);

        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source);
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        var codeStyle = new ElementStyle();
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = codeStyle,
                [MarkdownElementKeys.CodeBlock] = codeStyle,
                [MarkdownElementKeys.CodeBlockHeader] = codeStyle,
                [MarkdownElementKeys.CodeBlockLanguage] = codeStyle,
                [MarkdownElementKeys.CodeBlockGutter] = codeStyle,
                [MarkdownElementKeys.CodeBlockLineNumber] = codeStyle,
            },
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1f);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);

        using LayoutSnapshot snapshot = new LayoutBuilder(context, semanticDocument: document)
            .Build(document.ParsedDocument!, availableWidth: 640f);
        Assert.Contains(snapshot.Blocks, static block => block is MarkdownRenderer.Layout.Boxes.TableBox);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());
        Assert.Equal(codeLine, code.CodeText);

        int codeOffset = code.CodeText.IndexOf("MIDDLE-MARKER", StringComparison.Ordinal);
        int sourceOffset = source.IndexOf("MIDDLE-MARKER", StringComparison.Ordinal);
        Assert.True(codeOffset >= 0);
        Assert.True(sourceOffset >= 0);
        AssertMappedCharacter(snapshot, code, codeOffset, sourceOffset, "M");
    }

    [Fact]
    public async System.Threading.Tasks.Task GitHubTablePreparationMapsAnEmptyLineInsideFollowingCodeBlock()
    {
        const string firstLine = "GFM-CODE-FIRST-LINE";
        const string secondLine = "GFM-CODE-SECOND-MIDDLE-MARKER-END";
        string source = "| Name | Value |\n" +
            "| --- | --- |\n" +
            "| alpha | beta |\n" +
            "row-without-a-pipe\n\n" +
            "```text\n" +
            firstLine + "\n\n" +
            secondLine + "\n" +
            "```\n";

        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();
        using LayoutSnapshot snapshot = await BuildGitHubReadmeLayoutAsync(engine, source);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.SelectMany(EnumerateCodeBlocks));
        Assert.Equal(firstLine + "\n\n" + secondLine, code.CodeText);

        int firstLineSource = source.IndexOf(firstLine, StringComparison.Ordinal);
        int firstNewlineSource = firstLineSource + firstLine.Length;
        int secondNewlineSource = firstNewlineSource + 1;
        AssertMappedCharacter(snapshot, code, firstLine.Length, firstNewlineSource, "\n");
        AssertMappedCharacter(snapshot, code, firstLine.Length + 1, secondNewlineSource, "\n");

        int markerOffset = code.CodeText.IndexOf("MIDDLE-MARKER", StringComparison.Ordinal);
        int markerSource = source.IndexOf("MIDDLE-MARKER", StringComparison.Ordinal);
        Assert.True(markerOffset >= 0);
        Assert.True(markerSource >= 0);
        AssertMappedCharacter(snapshot, code, markerOffset, markerSource, "M");
    }

    [Fact]
    public async System.Threading.Tasks.Task GitHubTablePreparationPreservesFollowingHtmlPreSourceMap()
    {
        const string preformattedBody = "HTML-PRE-AFTER-TABLE-MIDDLE-MARKER-END";
        const string source = "| Name | Value |\n" +
            "| --- | --- |\n" +
            "| alpha | beta |\n" +
            "row-without-a-pipe\n\n" +
            "<pre><code>" + preformattedBody + "</code></pre>\n";

        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();
        using LayoutSnapshot snapshot = await BuildGitHubReadmeLayoutAsync(engine, source);
        Assert.Contains(snapshot.Blocks, static block => block is MarkdownRenderer.Layout.Boxes.TableBox);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.SelectMany(EnumerateCodeBlocks));
        Assert.Equal(preformattedBody, code.CodeText);

        int codeOffset = code.CodeText.IndexOf("MIDDLE-MARKER", StringComparison.Ordinal);
        int sourceOffset = source.IndexOf("MIDDLE-MARKER", StringComparison.Ordinal);
        Assert.True(codeOffset >= 0);
        Assert.True(sourceOffset >= 0);
        AssertMappedCharacter(snapshot, code, codeOffset, sourceOffset, "M");
    }

    [Fact]
    public async System.Threading.Tasks.Task GitHubTablePreparationDoesNotChangeTableLikeLinesInsideFencedCode()
    {
        const string fencedHeader = "| Name | Value |";
        const string fencedSeparator = "| --- | --- |";
        const string fencedBody = "fenced-row-without-a-pipe-MIDDLE-MARKER-END";
        const string mismatchedHeader = "| Other | Value |";
        const string mismatchedSeparator = "| --- |";
        const string mismatchedBody = "fenced-masked-row-without-a-pipe-SECOND-MARKER-END";
        const string source = "| Name | Value |\n" +
            "| --- | --- |\n" +
            "| alpha | beta |\n" +
            "row-without-a-pipe\n\n" +
            "```text\n" +
            fencedHeader + "\n" +
            fencedSeparator + "\n" +
            fencedBody + "\n" +
            "```\n\n" +
            "```text\n" +
            mismatchedHeader + "\n" +
            mismatchedSeparator + "\n" +
            mismatchedBody + "\n" +
            "```\n";
        const string expectedInsertedCode = fencedHeader + "\n" + fencedSeparator + "\n" + fencedBody;
        const string expectedMaskedCode = mismatchedHeader + "\n" + mismatchedSeparator + "\n" + mismatchedBody;

        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();
        using LayoutSnapshot snapshot = await BuildGitHubReadmeLayoutAsync(engine, source);
        Assert.Single(snapshot.Blocks.OfType<MarkdownRenderer.Layout.Boxes.TableBox>());
        CodeBlockBox[] codeBlocks = snapshot.Blocks.SelectMany(EnumerateCodeBlocks).ToArray();
        Assert.Equal(2, codeBlocks.Length);
        Assert.Equal(expectedInsertedCode, codeBlocks[0].CodeText);
        Assert.Equal(expectedMaskedCode, codeBlocks[1].CodeText);

        int codeOffset = codeBlocks[0].CodeText.IndexOf("MIDDLE-MARKER", StringComparison.Ordinal);
        int sourceOffset = source.IndexOf("MIDDLE-MARKER", StringComparison.Ordinal);
        Assert.True(codeOffset >= 0);
        Assert.True(sourceOffset >= 0);
        AssertMappedCharacter(snapshot, codeBlocks[0], codeOffset, sourceOffset, "M");

        int secondCodeOffset = codeBlocks[1].CodeText.IndexOf("SECOND-MARKER", StringComparison.Ordinal);
        int secondSourceOffset = source.IndexOf("SECOND-MARKER", StringComparison.Ordinal);
        Assert.True(secondCodeOffset >= 0);
        Assert.True(secondSourceOffset >= 0);
        AssertMappedCharacter(snapshot, codeBlocks[1], secondCodeOffset, secondSourceOffset, "S");
    }

    [Theory]
    [InlineData("\n", false, 20 * 1024)]
    [InlineData("\r\n", false, 20 * 1024)]
    [InlineData("\r", false, 20 * 1024)]
    [InlineData("\n", true, 20 * 1024)]
    [InlineData("\r", true, 20 * 1024)]
    [InlineData("\n", false, 80 * 1024)]
    public void LongCodeBlockSelectionsMapToExactAuthoredBodyCharacters(
        string newline,
        bool indented,
        int bodyLength)
    {
        string body = CreateBody(bodyLength);
        string codeBody = bodyLength > 32 * 1024
            ? body + newline + "CHUNK-TAIL"
            : body;
        string source = indented
            ? $"    {codeBody}{newline}{newline}OUTSIDE-CODE-SENTINEL"
            : $"```text{newline}{codeBody}{newline}```{newline}{newline}OUTSIDE-CODE-SENTINEL";
        int authoredBodyStart = source.IndexOf(body, StringComparison.Ordinal);
        Assert.True(authoredBodyStart >= 0);

        using LayoutSnapshot snapshot = Build(source);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());
        if (bodyLength > 32 * 1024)
            Assert.True(code.Chunks.Count > 1);

        Assert.StartsWith(body.AsSpan(0, System.Math.Min(8, body.Length)).ToString(), code.CodeText, StringComparison.Ordinal);
        Assert.True(code.CodeText.Length > 16 * 1024);
        Assert.Contains("OUTSIDE-CODE-SENTINEL", source, StringComparison.Ordinal);

        foreach (int bodyOffset in new[] { bodyLength / 2, 0, bodyLength - 1 })
        {
            InlineContainerBox chunk = Assert.Single(code.Chunks, candidate =>
                bodyOffset >= candidate.CodeBlockTextOffset &&
                bodyOffset < candidate.CodeBlockTextOffset + candidate.CodeBlockTextLength);
            TextRun run = Assert.IsType<TextRun>(Assert.Single(chunk.Runs));
            int localOffset = bodyOffset - chunk.CodeBlockTextOffset;
            var range = new DocumentRange(
                new DocumentPosition(chunk.BlockIndex, run.InlineIndex, localOffset),
                new DocumentPosition(chunk.BlockIndex, run.InlineIndex, localOffset + 1));

            Assert.True(snapshot.SourceMap.TryMapRange(range, out SourceSpan actualSpan));
            if (bodyOffset == bodyLength / 2)
            {
                // The middle marker is not at a block/chunk boundary, so the
                // exact one-unit source span is independent of delimiter policy.
                Assert.Equal(new SourceSpan(authoredBodyStart + bodyOffset, 1), actualSpan);
                Assert.Equal("M", snapshot.SourceMap.Slice(range));
            }
            else
            {
                // Selecting the first or last rendered code unit may include
                // source-only fence/indent syntax by contract. It must still
                // include the actual selected authored body character.
                Assert.InRange(actualSpan.Start, 0, authoredBodyStart + bodyOffset);
                Assert.True(actualSpan.End >= authoredBodyStart + bodyOffset + 1);
                Assert.Contains(body[bodyOffset].ToString(), snapshot.SourceMap.Slice(range), StringComparison.Ordinal);
            }
        }
    }

    private static string CreateBody(int bodyLength)
    {
        var chars = Enumerable.Repeat('x', bodyLength).ToArray();
        chars[0] = 'F';
        chars[bodyLength / 2] = 'M';
        chars[^1] = 'L';
        return new string(chars);
    }

    private static string BuildWrappedSource(string newline, string container, string line1, string line2)
    {
        string[] lines = container switch
        {
            "quote" =>
            [
                "> ```text",
                "> " + line1,
                "> " + line2,
                "> ```",
                string.Empty,
                "AFTER-WRAPPER-SENTINEL",
            ],
            "list" =>
            [
                "- ```text",
                "  " + line1,
                "  " + line2,
                "  ```",
                string.Empty,
                "AFTER-WRAPPER-SENTINEL",
            ],
            "quote-list" =>
            [
                "> - ```text",
                ">   " + line1,
                ">   " + line2,
                ">   ```",
                ">",
                "AFTER-WRAPPER-SENTINEL",
            ],
            "indented-tab" =>
            [
                "\t" + line1,
                "\t" + line2,
                string.Empty,
                "AFTER-WRAPPER-SENTINEL",
            ],
            "indented-tab-extra" =>
            [
                "  \t  " + line1,
                "  \t  " + line2,
                string.Empty,
                "AFTER-WRAPPER-SENTINEL",
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(container)),
        };
        return string.Join(newline, lines);
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

    private static void AssertMappedCharacter(
        LayoutSnapshot snapshot,
        CodeBlockBox code,
        int codeOffset,
        int expectedSourceStart,
        string expectedSourceText)
    {
        InlineContainerBox chunk = Assert.Single(code.Chunks, candidate =>
            codeOffset >= candidate.CodeBlockTextOffset &&
            codeOffset < candidate.CodeBlockTextOffset + candidate.CodeBlockTextLength);
        TextRun run = Assert.IsType<TextRun>(Assert.Single(chunk.Runs));
        int localOffset = codeOffset - chunk.CodeBlockTextOffset;
        var range = new DocumentRange(
            new DocumentPosition(chunk.BlockIndex, run.InlineIndex, localOffset),
            new DocumentPosition(chunk.BlockIndex, run.InlineIndex, localOffset + 1));

        Assert.True(snapshot.SourceMap.TryMapRange(range, out SourceSpan actualSpan));
        Assert.Equal(new SourceSpan(expectedSourceStart, expectedSourceText.Length), actualSpan);
        Assert.Equal(expectedSourceText, snapshot.SourceMap.Slice(range));
    }

    private static LayoutSnapshot Build(string source)
    {
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry();
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(source, registry.BuildPipeline());
        var codeStyle = new ElementStyle();
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = codeStyle,
                [MarkdownElementKeys.CodeBlock] = codeStyle,
                [MarkdownElementKeys.CodeBlockHeader] = codeStyle,
                [MarkdownElementKeys.CodeBlockLanguage] = codeStyle,
                [MarkdownElementKeys.CodeBlockGutter] = codeStyle,
                [MarkdownElementKeys.CodeBlockLineNumber] = codeStyle,
            },
            new Dictionary<string, ElementStyleOverride>(),
            transparent,
            transparent,
            transparent,
            transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1f);
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);

        return new LayoutBuilder(context).Build(parsed, availableWidth: 640f);
    }

    private static async System.Threading.Tasks.Task<LayoutSnapshot> BuildGitHubReadmeLayoutAsync(
        MarkdownEngine engine,
        string source)
    {
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(source);
        return BuildGitHubReadmeLayout(engine, source, document);
    }

    private static LayoutSnapshot BuildGitHubReadmeLayout(
        MarkdownEngine engine,
        string source,
        MarkdownRenderer.Document.MarkdownDocument document)
    {
        MarkdownExtensionRegistry registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        var codeStyle = new ElementStyle();
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
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
        var context = new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight);

        return new LayoutBuilder(context, semanticDocument: document)
            .Build(document.ParsedDocument!, availableWidth: 640f);
    }
}
