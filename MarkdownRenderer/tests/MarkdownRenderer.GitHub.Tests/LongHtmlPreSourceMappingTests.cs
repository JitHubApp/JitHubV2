using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
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
public sealed class LongHtmlPreSourceMappingTests
{
    private const int PayloadLength = 20 * 1024;
    private const string PreCodeSentinel = "HTML_PRE_CODE_SOURCE_SENTINEL";
    private const string TableCellSentinel = "HTML_PRE_TABLE_CELL_SOURCE_SENTINEL";
    private const string EntitySentinel = "HTML_PRE_ENTITY_SOURCE_SENTINEL";

    [Fact]
    public void LongHtmlPreCodeSelectionMapsOneThirdSentinelToExactAuthoredCharacters()
    {
        string body = CreatePayload(PreCodeSentinel, out int sentinelOffset);
        string source = $"<pre><code>{body}</code></pre>";
        int authoredBodyStart = source.IndexOf(body, StringComparison.Ordinal);
        Assert.True(authoredBodyStart >= 0);

        using LayoutSnapshot snapshot = Build(source);
        CodeBlockBox code = Assert.Single(FlattenBoxes(snapshot).OfType<CodeBlockBox>());
        InlineContainerBox chunk = Assert.Single(code.Chunks);
        TextRun run = Assert.IsType<TextRun>(Assert.Single(chunk.Runs));
        Assert.Equal(body, code.CodeText);

        AssertMapsExactText(
            snapshot,
            chunk,
            run,
            sentinelOffset,
            PreCodeSentinel,
            authoredBodyStart + sentinelOffset);
    }

    [Fact]
    public void LongPreformattedHtmlTableCellSelectionMapsOneThirdSentinelExactly()
    {
        string body = CreatePayload(TableCellSentinel, out int sentinelOffset);
        string source = $"<table><tr><td><pre><code>{body}</code></pre></td></tr></table>";
        int authoredBodyStart = source.IndexOf(body, StringComparison.Ordinal);
        Assert.True(authoredBodyStart >= 0);

        using LayoutSnapshot snapshot = Build(source);
        TableBox table = Assert.Single(FlattenBoxes(snapshot).OfType<TableBox>());
        InlineContainerBox cell = Assert.Single(table.GetCellBoxes());
        TextRun run = Assert.IsType<TextRun>(Assert.Single(cell.Runs));
        Assert.Equal(body, run.Text);

        AssertMapsExactText(
            snapshot,
            cell,
            run,
            sentinelOffset,
            TableCellSentinel,
            authoredBodyStart + sentinelOffset);
    }

    [Fact]
    public void EntityHeavyHtmlPreControlStillMapsDecodedEntitiesAndOneThirdSentinelExactly()
    {
        const int fragmentCount = 96;
        const int sentinelAfterFragments = fragmentCount / 3;
        const string fragment = "A&amp;B&#x1F642;C";
        string prefix = string.Concat(Enumerable.Repeat(fragment, sentinelAfterFragments));
        string suffix = string.Concat(Enumerable.Repeat(fragment, fragmentCount - sentinelAfterFragments));
        string sourceBody = prefix + EntitySentinel + suffix;
        string renderedBody = WebUtility.HtmlDecode(sourceBody);
        string source = $"<pre><code>{sourceBody}</code></pre>";
        int authoredBodyStart = source.IndexOf(sourceBody, StringComparison.Ordinal);
        Assert.True(authoredBodyStart >= 0);

        using LayoutSnapshot snapshot = Build(source);
        CodeBlockBox code = Assert.Single(FlattenBoxes(snapshot).OfType<CodeBlockBox>());
        InlineContainerBox chunk = Assert.Single(code.Chunks);
        TextRun run = Assert.IsType<TextRun>(Assert.Single(chunk.Runs));
        Assert.Equal(renderedBody, code.CodeText);

        int sentinelOffset = WebUtility.HtmlDecode(prefix).Length;
        AssertMapsExactText(
            snapshot,
            chunk,
            run,
            sentinelOffset,
            EntitySentinel,
            authoredBodyStart + prefix.Length);

        const string numericEntity = "&#x1F642;";
        int entitySourceOffset = sourceBody.IndexOf(numericEntity, StringComparison.Ordinal);
        Assert.True(entitySourceOffset >= 0);
        int entityRenderedOffset = WebUtility.HtmlDecode(sourceBody[..entitySourceOffset]).Length;
        var entityRange = new DocumentRange(
            new DocumentPosition(chunk.BlockIndex, run.InlineIndex, entityRenderedOffset),
            new DocumentPosition(chunk.BlockIndex, run.InlineIndex, entityRenderedOffset + 2));

        Assert.True(snapshot.SourceMap.TryMapRange(entityRange, out SourceSpan entitySpan));
        Assert.Equal(
            new SourceSpan(authoredBodyStart + entitySourceOffset, numericEntity.Length),
            entitySpan);
        Assert.Equal(numericEntity, snapshot.SourceMap.Slice(entityRange));
    }

    [Fact]
    public void DenseHtmlEntityPreformattedSourceMapsBeyondSixteenThousandExactSegments()
    {
        const int entityCount = 20_000;
        const string entity = "&gt;";
        string sourceBody = string.Concat(Enumerable.Repeat(entity, entityCount));
        string source = $"<pre><code>{sourceBody}</code></pre>";
        int authoredBodyStart = source.IndexOf(sourceBody, StringComparison.Ordinal);
        Assert.True(authoredBodyStart >= 0);

        using LayoutSnapshot snapshot = Build(source);
        CodeBlockBox code = Assert.Single(FlattenBoxes(snapshot).OfType<CodeBlockBox>());
        InlineContainerBox chunk = Assert.Single(code.Chunks);
        TextRun run = Assert.IsType<TextRun>(Assert.Single(chunk.Runs));
        Assert.Equal(new string('>', entityCount), code.CodeText);

        const int renderedOffset = entityCount / 2;
        var range = new DocumentRange(
            new DocumentPosition(chunk.BlockIndex, run.InlineIndex, renderedOffset),
            new DocumentPosition(chunk.BlockIndex, run.InlineIndex, renderedOffset + 1));
        Assert.True(snapshot.SourceMap.TryMapRange(range, out SourceSpan actualSpan));
        Assert.Equal(new SourceSpan(authoredBodyStart + renderedOffset * entity.Length, entity.Length), actualSpan);
        Assert.Equal(entity, snapshot.SourceMap.Slice(range));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void QuotedLongHtmlPreCodeSelectionMapsAcrossRemovedQuotePrefixAndNewlineExactly(string newline)
    {
        const string sentinel = "QUOTED_HTML_PRE_SOURCE_SENTINEL";
        const string trailingBody = "_UNSELECTED_TRAILING_BODY_";
        string firstLineBody = new string('x', PayloadLength / 3);
        string firstSourceLine = $"> <pre><code>{firstLineBody}";
        string secondSourceLine = $"> {sentinel}{trailingBody}</code></pre>";
        string source = firstSourceLine + newline + secondSourceLine;

        using LayoutSnapshot snapshot = Build(source);
        CodeBlockBox code = Assert.Single(FlattenBoxes(snapshot).OfType<CodeBlockBox>());
        InlineContainerBox chunk = Assert.Single(code.Chunks);
        TextRun run = Assert.IsType<TextRun>(Assert.Single(chunk.Runs));
        Assert.Equal(firstLineBody + "\n" + sentinel + trailingBody, code.CodeText);

        int renderedOffset = firstLineBody.Length + 1;
        int sourceOffset = firstSourceLine.Length + newline.Length + 2;
        AssertMapsExactText(snapshot, chunk, run, renderedOffset, sentinel, sourceOffset);

        var fullCodeRange = new DocumentRange(
            new DocumentPosition(chunk.BlockIndex, run.InlineIndex, 0),
            new DocumentPosition(chunk.BlockIndex, run.InlineIndex, code.CodeText.Length));
        Assert.True(snapshot.SourceMap.TryMapRange(fullCodeRange, out _));
        string fullCodeSource = snapshot.SourceMap.Slice(fullCodeRange);
        Assert.Contains("<pre><code>", fullCodeSource, StringComparison.Ordinal);
        Assert.Contains("</code></pre>", fullCodeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PreformattedCrLfAcrossNestedTextNodesMapsTheWholeAuthoredEnvelope()
    {
        const string source = "<pre><code>A&#13;<span>&#10;</span>B</code></pre>";
        const string expectedEnvelope = "&#13;<span>&#10;";

        using LayoutSnapshot snapshot = Build(source);
        CodeBlockBox code = Assert.Single(FlattenBoxes(snapshot).OfType<CodeBlockBox>());
        InlineContainerBox chunk = Assert.Single(code.Chunks);
        TextRun run = Assert.IsType<TextRun>(Assert.Single(chunk.Runs));
        Assert.Equal("A\nB", code.CodeText);

        var newlineRange = new DocumentRange(
            new DocumentPosition(chunk.BlockIndex, run.InlineIndex, 1),
            new DocumentPosition(chunk.BlockIndex, run.InlineIndex, 2));
        Assert.True(snapshot.SourceMap.TryMapRange(newlineRange, out SourceSpan actualSpan));
        Assert.Equal(
            new SourceSpan(source.IndexOf(expectedEnvelope, StringComparison.Ordinal), expectedEnvelope.Length),
            actualSpan);
        Assert.Equal(expectedEnvelope, snapshot.SourceMap.Slice(newlineRange));
    }

    [Fact]
    public void UnterminatedPreformattedElementMapsBodyToItsExactAuthoredRange()
    {
        const string body = "unterminated preformatted body with an interior sentinel";
        const string source = "<pre><code>unterminated preformatted body with an interior sentinel";

        using LayoutSnapshot snapshot = Build(source);
        CodeBlockBox code = Assert.Single(FlattenBoxes(snapshot).OfType<CodeBlockBox>());
        InlineContainerBox chunk = Assert.Single(code.Chunks);
        TextRun run = Assert.IsType<TextRun>(Assert.Single(chunk.Runs));
        Assert.Equal(body, code.CodeText);
        AssertMapsExactText(snapshot, chunk, run, body.IndexOf("interior", StringComparison.Ordinal), "interior",
            source.IndexOf("interior", StringComparison.Ordinal));
    }

    private static string CreatePayload(string sentinel, out int sentinelOffset)
    {
        sentinelOffset = PayloadLength / 3;
        var chars = Enumerable.Repeat('x', PayloadLength).ToArray();
        sentinel.AsSpan().CopyTo(chars.AsSpan(sentinelOffset));
        return new string(chars);
    }

    private static void AssertMapsExactText(
        LayoutSnapshot snapshot,
        InlineContainerBox owner,
        TextRun run,
        int renderedOffset,
        string expectedText,
        int expectedSourceOffset)
    {
        var range = new DocumentRange(
            new DocumentPosition(owner.BlockIndex, run.InlineIndex, renderedOffset),
            new DocumentPosition(owner.BlockIndex, run.InlineIndex, renderedOffset + expectedText.Length));

        Assert.True(snapshot.SourceMap.TryMapRange(range, out SourceSpan actualSpan));
        Assert.Equal(new SourceSpan(expectedSourceOffset, expectedText.Length), actualSpan);
        Assert.Equal(expectedText, snapshot.SourceMap.Slice(range));
    }

    private static IEnumerable<BlockBox> FlattenBoxes(LayoutSnapshot snapshot)
    {
        foreach (BlockBox block in snapshot.Blocks)
        {
            yield return block;
            if (block is StackBox stack)
            {
                foreach (BlockBox child in FlattenBoxes(stack))
                    yield return child;
            }
            else if (block is TableBox table)
            {
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    yield return cell;
            }
        }
    }

    private static IEnumerable<BlockBox> FlattenBoxes(StackBox stack)
    {
        foreach (BlockBox child in stack.Children)
        {
            yield return child;
            if (child is StackBox nested)
            {
                foreach (BlockBox descendant in FlattenBoxes(nested))
                    yield return descendant;
            }
            else if (child is TableBox table)
            {
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                    yield return cell;
            }
        }
    }

    private static LayoutSnapshot Build(string source)
    {
        var registry = new MarkdownExtensionRegistry().ConfigureGitHubReadmeRegistry(SafeHtmlOptions.Default);
        Markdig.Syntax.MarkdownDocument parsed = Markdown.Parse(source, registry.BuildPipeline());
        var style = new ElementStyle();
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>
            {
                [MarkdownElementKeys.Body] = style,
                [MarkdownElementKeys.CodeBlock] = style,
                [MarkdownElementKeys.CodeBlockHeader] = style,
                [MarkdownElementKeys.CodeBlockLanguage] = style,
                [MarkdownElementKeys.CodeBlockGutter] = style,
                [MarkdownElementKeys.CodeBlockLineNumber] = style,
                [MarkdownElementKeys.Table] = style,
                [MarkdownElementKeys.TableCell] = style,
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

        return new LayoutBuilder(context).Build(parsed, availableWidth: 640f);
    }
}
