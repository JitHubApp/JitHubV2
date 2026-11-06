using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class CodeBlockNoWrapChunkGeometryTests
{
    private const int ChunkLimit = 32_768;

    [Fact]
    public async Task NoWrapLongSingleLogicalLineUsesOneVisualRowAndFullHorizontalExtent()
    {
        string body = new string('x', ChunkLimit + 12_000);
        using LayoutSnapshot snapshot = await BuildNoWrap(body);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());

        Assert.Equal(CodeBlockWrappingMode.NoWrap, code.Context.CodeBlockWrappingMode);
        Assert.Equal(body, code.CodeText);
        Assert.Equal(1, code.LineCount);
        Assert.Equal(1, code.IndexedVisualLineCount);
        InlineContainerBox chunk = Assert.Single(code.Chunks);

        double completeLineWidth = chunk.ContentWidth;
        Assert.True(
            code.HorizontalExtent + 0.5 >= completeLineWidth,
            $"The NoWrap extent {code.HorizontalExtent} must contain the complete line width {completeLineWidth}.");
    }

    [Fact]
    public async Task NoWrapLongMultilineCodeKeepsOneVisualRowPerAuthoredLine()
    {
        string firstLine = new string('x', ChunkLimit + 8_000);
        string secondLine = new string('y', ChunkLimit + 4_000);
        string body = firstLine + "\n" + secondLine;
        using LayoutSnapshot snapshot = await BuildNoWrap(body);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());

        Assert.Equal(CodeBlockWrappingMode.NoWrap, code.Context.CodeBlockWrappingMode);
        Assert.Equal(body, code.CodeText);
        Assert.Equal(2, code.LineCount);
        Assert.Equal(2, code.IndexedVisualLineCount);
        Assert.Equal(2, code.Chunks.Count);
        Assert.True(code.HorizontalExtent > code.HorizontalViewport);

        CanvasLineMetrics[] firstMetrics = GetMetrics(code.Chunks[0]);
        Assert.True(firstMetrics.Length > 1);
        Assert.Equal(0, firstMetrics[^1].CharacterCount);
    }

    [Fact]
    public async Task NoWrapLongMultilineChunksHaveContinuousBaselinesAcrossRepeatedPlacement()
    {
        string firstLine = new string('x', ChunkLimit + 8_000);
        string secondLine = new string('y', ChunkLimit + 4_000);
        string body = firstLine + "\n" + secondLine;
        using LayoutSnapshot snapshot = await BuildNoWrap(body);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());
        Assert.Equal(2, code.Chunks.Count);
        CanvasLineMetrics[] metrics = GetMetrics(code.Chunks[0]);
        Assert.True(metrics.Length > 1);
        Assert.Equal(0, metrics[^1].CharacterCount);

        // This geometry-only control intentionally does not assert the visual
        // row count, so a count failure cannot mask a physical gap regression.
        AssertChunkSeamHasNoExtraRow(code, metrics);
        double effectiveFirstChunkHeight = code.Chunks[0].Bounds.Height;
        for (int iteration = 0; iteration < 3; iteration++)
        {
            code.Measure(600);
            code.Arrange(4.25f, 128.5f + iteration * 17f, 600);
            AssertClose(effectiveFirstChunkHeight, code.Chunks[0].Bounds.Height, 0.01);
            AssertChunkSeamHasNoExtraRow(code, GetMetrics(code.Chunks[0]));
        }
    }

    [Fact]
    public async Task NoWrapKeepsOnlyFinalAuthoredLfBlankRowInTheVisualIndex()
    {
        string firstLine = new string('a', ChunkLimit + 8_000);
        string finalLine = new string('b', ChunkLimit + 4_000);
        string body = firstLine + "\n" + finalLine + "\n";
        using LayoutSnapshot snapshot = await BuildCodeBlock(
            body, CodeBlockWrappingMode.NoWrap, availableWidth: 600);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());

        Assert.Equal(CodeBlockWrappingMode.NoWrap, code.Context.CodeBlockWrappingMode);
        Assert.Equal(body, code.CodeText);
        Assert.Equal(3, code.LineCount);
        Assert.Equal(2, code.Chunks.Count);

        CanvasLineMetrics[] firstMetrics = GetMetrics(code.Chunks[0]);
        CanvasLineMetrics[] finalMetrics = GetMetrics(code.Chunks[1]);
        Assert.Equal(0, firstMetrics[^1].CharacterCount);
        Assert.Equal(0, finalMetrics[^1].CharacterCount);

        // The intermediate empty metric is shared with the next chunk and is
        // omitted; the final chunk's empty metric represents the authored
        // terminal LF and remains in both its physical height and line index.
        Assert.Equal(3, code.IndexedVisualLineCount);
    }

    [Fact]
    public async Task NoWrapFinalLfRetainsItsPhysicalHeightWithoutAnIntermediateGap()
    {
        string firstLine = new string('a', ChunkLimit + 8_000);
        string finalLine = new string('b', ChunkLimit + 4_000);
        string body = firstLine + "\n" + finalLine + "\n";
        using LayoutSnapshot snapshot = await BuildNoWrap(body);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());
        Assert.Equal(body, code.CodeText);
        Assert.Equal(2, code.Chunks.Count);

        CanvasLineMetrics[] firstMetrics = GetMetrics(code.Chunks[0]);
        CanvasLineMetrics[] finalMetrics = GetMetrics(code.Chunks[1]);
        Assert.Equal(0, firstMetrics[^1].CharacterCount);
        Assert.Equal(0, finalMetrics[^1].CharacterCount);
        AssertChunkSeamHasNoExtraRow(code, firstMetrics);

        using LayoutSnapshot noFinalLfSnapshot = await BuildCodeBlock(
            finalLine, CodeBlockWrappingMode.NoWrap, availableWidth: 600);
        CodeBlockBox noFinalLfCode = Assert.Single(noFinalLfSnapshot.Blocks.OfType<CodeBlockBox>());
        AssertClose(
            finalMetrics[^1].Height,
            code.Chunks[1].Bounds.Height - noFinalLfCode.Chunks[0].Bounds.Height,
            1.0);
        AssertClose(code.HorizontalViewportBounds.Bottom, code.Chunks[1].Bounds.Bottom, 1.0);
    }

    [Fact]
    public async Task WrappedChunksRemoveOnlyTheDuplicateIntermediateEmptyMetric()
    {
        string word = new string('w', 24);
        string firstLine = string.Join(' ', Enumerable.Repeat(word, 1_500));
        string secondLine = string.Join(' ', Enumerable.Repeat("secondword", 3_800));
        string body = firstLine + "\n" + secondLine;
        using LayoutSnapshot snapshot = await BuildCodeBlock(
            body, CodeBlockWrappingMode.Wrap, availableWidth: 320);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());

        Assert.Equal(CodeBlockWrappingMode.Wrap, code.Context.CodeBlockWrappingMode);
        Assert.Equal(body, code.CodeText);
        Assert.Equal(2, code.LineCount);
        Assert.Equal(2, code.Chunks.Count);

        CanvasLineMetrics[] firstMetrics = GetMetrics(code.Chunks[0]);
        Assert.True(firstMetrics.Length > 1, "The Wrap fixture must produce multiple native rows in its first chunk.");
        Assert.Equal(0, firstMetrics[^1].CharacterCount);
        Assert.Equal(
            GetMetrics(code.Chunks[0]).Length + GetMetrics(code.Chunks[1]).Length - 1,
            code.IndexedVisualLineCount);
    }

    [Fact]
    public async Task WrappedChunksHaveContinuousBaselinesAtTheSameLfBoundary()
    {
        string firstLine = string.Join(' ', Enumerable.Repeat(new string('w', 24), 1_500));
        string secondLine = string.Join(' ', Enumerable.Repeat("secondword", 3_800));
        string body = firstLine + "\n" + secondLine;
        using LayoutSnapshot snapshot = await BuildCodeBlock(
            body, CodeBlockWrappingMode.Wrap, availableWidth: 320);
        CodeBlockBox code = Assert.Single(snapshot.Blocks.OfType<CodeBlockBox>());
        Assert.Equal(2, code.Chunks.Count);
        CanvasLineMetrics[] firstMetrics = GetMetrics(code.Chunks[0]);
        Assert.True(firstMetrics.Length > 1);
        Assert.Equal(0, firstMetrics[^1].CharacterCount);

        // Count is asserted by a separate test; keep this one focused on the
        // actual wrapped-row baseline and chunk-bound placement.
        AssertChunkSeamHasNoExtraRow(code, firstMetrics);
    }

    private static async Task<LayoutSnapshot> BuildNoWrap(string body)
        => await BuildCodeBlock(body, CodeBlockWrappingMode.NoWrap, availableWidth: 600);

    private static async Task<LayoutSnapshot> BuildCodeBlock(
        string body,
        CodeBlockWrappingMode wrappingMode,
        float availableWidth)
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();
        string source = $"```text\n{body}\n```";
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
            CanvasDevice.GetSharedDevice(forceSoftwareRenderer: true),
            theme,
            new MarkdownSourceMap(source),
            registry,
            FlowDirection.LeftToRight)
        {
            CodeBlockWrappingMode = wrappingMode,
        };

        return new LayoutBuilder(context, semanticDocument: document)
            .Build(document.ParsedDocument!, availableWidth);
    }

    private static CanvasLineMetrics[] GetMetrics(InlineContainerBox chunk)
        => Assert.IsType<CanvasLineMetrics[]>(chunk.GetLineMetricsSnapshot());

    private static void AssertChunkSeamHasNoExtraRow(
        CodeBlockBox code,
        CanvasLineMetrics[] precedingMetrics)
    {
        Assert.True(code.Chunks.Count > 1);
        InlineContainerBox preceding = code.Chunks[0];
        InlineContainerBox following = code.Chunks[1];
        double expectedTextOriginAdvance = SumMetricHeights(precedingMetrics[..^1]);
        double actualTextOriginAdvance = following.GetTextOriginY() - preceding.GetTextOriginY();

        AssertClose(expectedTextOriginAdvance, actualTextOriginAdvance, 1.0);
        AssertClose(preceding.Bounds.Bottom, following.Bounds.Top, 0.01);
    }

    private static double SumMetricHeights(IEnumerable<CanvasLineMetrics> metrics)
        => metrics.Sum(static metric => global::System.Math.Max(1f, metric.Height));

    private static void AssertClose(double expected, double actual, double tolerance)
    {
        Assert.True(
            global::System.Math.Abs(expected - actual) <= tolerance,
            $"Expected {expected:R} and {actual:R} to differ by no more than {tolerance:R}.");
    }
}
