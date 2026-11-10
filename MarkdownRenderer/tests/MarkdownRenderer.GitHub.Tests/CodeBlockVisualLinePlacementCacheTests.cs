using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.CodeBlocks.Internal;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

/// <remarks>
/// This file is longer than 500 lines because every visual-line cache rule (repeated placement,
/// width reflow and highlighting, private chunk adoption, missing and invalid native metrics,
/// cancellation, gutter width and the allocation diagnostics) is checked against the same
/// independent legacy line-placement oracle and native-metric fault helpers kept in this class.
/// </remarks>
[Collection("Shared Canvas text format cache")]
public sealed class CodeBlockVisualLinePlacementCacheTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData(1f, FlowDirection.LeftToRight)]
    [InlineData(1.25f, FlowDirection.LeftToRight)]
    [InlineData(1.5f, FlowDirection.LeftToRight)]
    [InlineData(1.75f, FlowDirection.LeftToRight)]
    [InlineData(2f, FlowDirection.LeftToRight)]
    [InlineData(2.25f, FlowDirection.LeftToRight)]
    [InlineData(3f, FlowDirection.LeftToRight)]
    [InlineData(4f, FlowDirection.LeftToRight)]
    [InlineData(1f, FlowDirection.RightToLeft)]
    [InlineData(1.25f, FlowDirection.RightToLeft)]
    [InlineData(1.5f, FlowDirection.RightToLeft)]
    [InlineData(1.75f, FlowDirection.RightToLeft)]
    [InlineData(2f, FlowDirection.RightToLeft)]
    [InlineData(2.25f, FlowDirection.RightToLeft)]
    [InlineData(3f, FlowDirection.RightToLeft)]
    [InlineData(4f, FlowDirection.RightToLeft)]
    public async Task RepeatedVerticalAndHorizontalPlacementMatchesIndependentNativeLineOracle(
        float rasterizationScale,
        FlowDirection flowDirection)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        string text = CreateLongMultichunkCode(lineCount: 180, longTokenLength: 124);
        await using CodeBlockVisualLineFixture actual = CodeBlockVisualLineFixture.Create(
            device, text, rasterizationScale, flowDirection, CanvasWordWrapping.NoWrap,
            availableWidth: 420, chunkCount: 5, startLine: 17, showLineNumbers: true);
        await using CodeBlockVisualLineFixture oracle = CodeBlockVisualLineFixture.Create(
            device, text, rasterizationScale, flowDirection, CanvasWordWrapping.NoWrap,
            availableWidth: 420, chunkCount: 5, startLine: 17, showLineNumbers: true);

        Assert.True(actual.Box.CanScrollHorizontally);
        Assert.True(actual.Box.IndexedVisualLineCount > 0);
        AssertNativeMetricsAvailable(actual.Box);
        AssertNativeMetricsAvailable(oracle.Box);
        AssertLineBreakChunkSeamsUseEffectiveHeight(actual.Box);
        AssertMatchesIndependentLegacyOracle(actual.Box, oracle.Box, 13.375f, 131_071.375f, 420);

        Array firstVisualLines = actual.Box.VisualLineIndex.Lines;
        Array firstBottomEdges = actual.Box.VisualLineIndex.BottomEdges;
        object?[] firstNativeLayouts = CaptureChunkNativeLayouts(actual.Box);
        long buildCount = actual.Box.VisualLineCacheBuildCount;

        // These coordinates deliberately exceed 2^17 and retain fractional
        // parts. Every expected top is recomputed from fresh chunk origins and
        // the legacy sequential-double height recurrence, not from a cached
        // prior box displacement.
        for (int placement = 1; placement <= 9; placement++)
        {
            float x = 13.375f + placement * 0.25f;
            float y = 131_071.375f + placement * 0.125f;
            AssertMatchesIndependentLegacyOracle(actual.Box, oracle.Box, x, y, 420);
        }

        Array beforeScrollLines = actual.Box.VisualLineIndex.Lines;
        Array beforeScrollBottomEdges = actual.Box.VisualLineIndex.BottomEdges;
        double unchangedOrigin = actual.Box.Chunks[0].GetTextOriginY();
        foreach (double offset in new[] { 24d, 72d, 128d, 48d })
        {
            Assert.True(actual.Box.SetHorizontalOffset(offset));
            Assert.True(oracle.Box.SetHorizontalOffset(offset));
            Assert.Equal(unchangedOrigin, actual.Box.Chunks[0].GetTextOriginY());
            AssertMatchesIndependentLegacyOracle(
                actual.Box, oracle.Box, (float)actual.Box.Bounds.X, (float)actual.Box.Bounds.Y, 420);
        }

        Assert.Equal(buildCount, actual.Box.VisualLineCacheBuildCount);
        Assert.Same(firstVisualLines, actual.Box.VisualLineIndex.Lines);
        Assert.Same(firstBottomEdges, actual.Box.VisualLineIndex.BottomEdges);
        Assert.Same(beforeScrollLines, actual.Box.VisualLineIndex.Lines);
        Assert.Same(beforeScrollBottomEdges, actual.Box.VisualLineIndex.BottomEdges);
        AssertSameChunkNativeLayouts(actual.Box, firstNativeLayouts);

        Assert.Equal(actual.Box.CopyButtonBounds, oracle.Box.CopyButtonBounds);
        Assert.Equal(actual.Box.HorizontalScrollTrackBounds, oracle.Box.HorizontalScrollTrackBounds);
        Assert.Equal(actual.Box.HorizontalScrollThumbBounds, oracle.Box.HorizontalScrollThumbBounds);
    }

    [Fact]
    public async Task WidthReflowHighlightAndPrivateChunkAdoptionRebuildAgainstIndependentMetrics()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        string text = CreateLongMultichunkCode(lineCount: 112, longTokenLength: 156);
        await using CodeBlockVisualLineFixture source = CodeBlockVisualLineFixture.Create(
            device, text, 1.5f, FlowDirection.RightToLeft, CanvasWordWrapping.Wrap,
            availableWidth: 460, chunkCount: 4, startLine: 99, showLineNumbers: true);
        await using CodeBlockVisualLineFixture initialOracle = CodeBlockVisualLineFixture.Create(
            device, text, 1.5f, FlowDirection.RightToLeft, CanvasWordWrapping.Wrap,
            availableWidth: 460, chunkCount: 4, startLine: 99, showLineNumbers: true);

        AssertNativeMetricsAvailable(source.Box);
        AssertNativeMetricsAvailable(initialOracle.Box);
        AssertLineBreakChunkSeamsUseEffectiveHeight(source.Box);
        AssertMatchesIndependentLegacyOracle(source.Box, initialOracle.Box, 5.25f, 91_337.625f, 460);

        CanvasLineMetrics[][] sourceMetricsBeforeWidthChange = GetChunkMetrics(source.Box);
        object?[] sourceLayoutsBeforeWidthChange = CaptureChunkNativeLayouts(source.Box);
        long buildCountBeforeWidthChange = source.Box.VisualLineCacheBuildCount;
        source.Box.Measure(208);
        source.Box.Arrange(5.25f, 91_337.625f, 208);
        initialOracle.Box.Measure(208);
        initialOracle.Box.Arrange(5.25f, 91_337.625f, 208);

        CanvasLineMetrics[][] sourceMetricsAfterWidthChange = GetChunkMetrics(source.Box);
        object?[] sourceLayoutsAfterWidthChange = CaptureChunkNativeLayouts(source.Box);
        Assert.Equal(sourceMetricsBeforeWidthChange.Length, sourceMetricsAfterWidthChange.Length);
        for (int index = 0; index < sourceMetricsBeforeWidthChange.Length; index++)
        {
            Assert.NotSame(sourceMetricsBeforeWidthChange[index], sourceMetricsAfterWidthChange[index]);
            Assert.NotSame(sourceLayoutsBeforeWidthChange[index], sourceLayoutsAfterWidthChange[index]);
        }
        Assert.True(source.Box.IndexedVisualLineCount > initialOracle.Box.LineCount);
        Assert.True(source.Box.VisualLineCacheBuildCount > buildCountBeforeWidthChange);
        AssertMatchesIndependentLegacyOracle(
            source.Box, initialOracle.Box, 5.25f, 91_337.625f, 208);

        CodeBlockHighlightCacheKey highlightKey = ApplyTestHighlighting(source.Box, initialOracle.Box, text);
        source.Box.Arrange(5.25f, 91_337.625f, 208);
        initialOracle.Box.Arrange(5.25f, 91_337.625f, 208);
        AssertMatchesIndependentLegacyOracle(
            source.Box, initialOracle.Box, 5.25f, 91_337.625f, 208);

        MarkdownLayoutContext candidateContext = CodeBlockVisualLineFixture.CreateCompatibleContext(source, device);
        AssertPrivatePlacementFixtureReady(source.Box, candidateContext);
        CodeBlockBox? shell = null;
        CodeBlockVisualLineFixture? adopted = null;
        try
        {
            CodeBlockBox candidateBox = source.Box.CreatePrivatePlacementCloneShell(candidateContext);
            shell = candidateBox;
            AddPrivateChunkClones(source.Box, candidateBox, candidateContext);

            candidateBox.Measure(208);
            candidateBox.Arrange(5.25f, 91_337.625f, 208);
            AssertLineBreakChunkSeamsUseEffectiveHeight(candidateBox);
            adopted = new CodeBlockVisualLineFixture(candidateBox, candidateContext, source.Theme, source.Style, ownsContext: true);

            AssertAdoptedChunksRebuildIndependentMetrics(
                source.Box, adopted.Box, highlightKey, sourceMetricsAfterWidthChange);
        }
        finally
        {
            if (adopted is not null)
                await adopted.DisposeAsync();
            else
            {
                shell?.Dispose();
                await candidateContext.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private static CodeBlockHighlightCacheKey ApplyTestHighlighting(
        CodeBlockBox source,
        CodeBlockBox oracle,
        string text)
    {
        CodeBlockHighlightCacheKey highlightKey = new(
            source.CodeLanguage,
            source.Metadata.CodeTextHash,
            source.CodeText.Length,
            MarkdownThemeVariant.Light,
            ProviderIdentity: 921,
            ProviderRevision: 1);
        CodeBlockHighlightSpan[] spans =
        [
            new(4, "var".Length, Color.FromArgb(255, 181, 74, 208)),
            new(global::System.Math.Max(0, text.IndexOf("value", StringComparison.Ordinal)), "value".Length,
                Color.FromArgb(255, 48, 132, 202)),
        ];
        Assert.True(source.ApplySyntaxHighlighting(highlightKey, spans));
        Assert.True(oracle.ApplySyntaxHighlighting(highlightKey, spans));
        Assert.True(source.PrivatePlacementForegroundSpanCount > 0);
        return highlightKey;
    }

    private static void AddPrivateChunkClones(
        CodeBlockBox source,
        CodeBlockBox candidateBox,
        MarkdownLayoutContext candidateContext)
    {
        foreach (InlineContainerBox sourceChunk in source.Chunks)
        {
            Assert.True(sourceChunk.TryCreatePrivateInlinePlacementClone(
                candidateContext,
                (float)sourceChunk.Bounds.Width,
                out InlineContainerBox? candidateChunk,
                out _,
                allowUnmeasuredTextOnly: false,
                allowIdleImagePlacements: false,
                allowCodeForegroundSpans: true));
            candidateBox.AddPrivatePlacementChunk(
                candidateChunk ?? throw new InvalidOperationException("Private code chunk cloning returned no chunk."));
        }
    }

    private static void AssertAdoptedChunksRebuildIndependentMetrics(
        CodeBlockBox source,
        CodeBlockBox adopted,
        CodeBlockHighlightCacheKey highlightKey,
        CanvasLineMetrics[][] sourceMetricsAfterWidthChange)
    {
        Assert.NotSame(source, adopted);
        Assert.Equal(source.CodeText, adopted.CodeText);
        Assert.Equal(source.Chunks.Count, adopted.Chunks.Count);
        Assert.True(adopted.HasAppliedSyntaxHighlighting(highlightKey));
        Assert.Equal(source.PrivatePlacementForegroundSpanCount,
            adopted.PrivatePlacementForegroundSpanCount);
        AssertMatchesIndependentLegacyOracle(adopted, 5.25f, 91_337.625f, 208);
        for (int index = 0; index < source.Chunks.Count; index++)
        {
            Assert.NotSame(source.Chunks[index], adopted.Chunks[index]);
            Assert.NotSame(
                sourceMetricsAfterWidthChange[index],
                Assert.IsType<CanvasLineMetrics[]>(adopted.Chunks[index].GetLineMetricsSnapshot()));
        }
    }

    [Fact]
    public async Task MissingMetricsClearsIndexAndLaterNativeMetricsRecoverIt()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        string text = CreateLongMultichunkCode(lineCount: 36, longTokenLength: 72);
        await using CodeBlockVisualLineFixture fixture = CodeBlockVisualLineFixture.Create(
            device, text, 2f, FlowDirection.LeftToRight, CanvasWordWrapping.Wrap,
            availableWidth: 400, chunkCount: 3, startLine: 8, showLineNumbers: true);
        await using CodeBlockVisualLineFixture oracle = CodeBlockVisualLineFixture.Create(
            device, text, 2f, FlowDirection.LeftToRight, CanvasWordWrapping.Wrap,
            availableWidth: 400, chunkCount: 3, startLine: 8, showLineNumbers: true);

        AssertNativeMetricsAvailable(fixture.Box);
        AssertMatchesIndependentLegacyOracle(fixture.Box, oracle.Box, 9.125f, 1_024.375f, 400);
        int originalCount = fixture.Box.IndexedVisualLineCount;
        long buildCount = fixture.Box.VisualLineCacheBuildCount;

        InlineContainerBox missingChunk = fixture.Box.Chunks[1];
        CanvasLineMetrics[]? savedMetrics = missingChunk.CachedLineMetrics;
        bool savedReadState = missingChunk.HasReadLineMetrics;
        Assert.IsType<CanvasLineMetrics[]>(savedMetrics);
        try
        {
            SetLineMetricsCache(missingChunk, null, read: true);
            fixture.Box.Arrange(9.125f, 1_024.375f, 400);

            Assert.Equal(0, fixture.Box.IndexedVisualLineCount);
            Assert.Empty(ReadVisualLines(fixture.Box));
            Assert.Empty(fixture.Box.VisualLineIndex.BottomEdges);
            Assert.Equal(buildCount, fixture.Box.VisualLineCacheBuildCount);

            SetLineMetricsCache(missingChunk, savedMetrics, savedReadState);
            fixture.Box.Arrange(9.125f, 1_024.375f, 400);
            oracle.Box.Arrange(9.125f, 1_024.375f, 400);

            Assert.Equal(originalCount, fixture.Box.IndexedVisualLineCount);
            AssertMatchesIndependentLegacyOracle(
                fixture.Box, oracle.Box, 9.125f, 1_024.375f, 400);
            Assert.True(fixture.Box.VisualLineCacheBuildCount > buildCount);
        }
        finally
        {
            SetLineMetricsCache(missingChunk, savedMetrics, savedReadState);
        }
    }

    [Fact]
    public async Task EmptyNonfiniteOverflowAndInvalidOriginInputsFailClosedThenRecover()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        string text = CreateLongMultichunkCode(lineCount: 30, longTokenLength: 72);
        await using CodeBlockVisualLineFixture fixture = CodeBlockVisualLineFixture.Create(
            device, text, 1.5f, FlowDirection.LeftToRight, CanvasWordWrapping.Wrap,
            availableWidth: 400, chunkCount: 3, startLine: 9, showLineNumbers: true);
        await using CodeBlockVisualLineFixture oracle = CodeBlockVisualLineFixture.Create(
            device, text, 1.5f, FlowDirection.LeftToRight, CanvasWordWrapping.Wrap,
            availableWidth: 400, chunkCount: 3, startLine: 9, showLineNumbers: true);

        AssertNativeMetricsAvailable(fixture.Box);
        AssertMatchesIndependentLegacyOracle(fixture.Box, oracle.Box, 7.25f, 8_192.375f, 400);
        InlineContainerBox chunk = fixture.Box.Chunks[0];
        CanvasLineMetrics[]? savedMetrics = chunk.CachedLineMetrics;
        bool savedReadState = chunk.HasReadLineMetrics;
        CanvasLineMetrics[] validMetrics = Assert.IsType<CanvasLineMetrics[]>(savedMetrics);
        long originalBuildCount = fixture.Box.VisualLineCacheBuildCount;

        try
        {
            SetCachedMetrics(chunk, Array.Empty<CanvasLineMetrics>());
            fixture.Box.Arrange(7.25f, 8_192.375f, 400);
            AssertVisualLineFallbackIsComplete(fixture.Box);
            Assert.Equal(originalBuildCount, fixture.Box.VisualLineCacheBuildCount);
            RestoreAndAssertRecovery(fixture.Box, oracle.Box, chunk,
                validMetrics, savedReadState, 7.25f, 8_192.375f, 400);

            CanvasLineMetrics[] nonfiniteMetrics = validMetrics.ToArray();
            nonfiniteMetrics[0].Height = float.NaN;
            SetCachedMetrics(chunk, nonfiniteMetrics);
            fixture.Box.Arrange(7.25f, 8_192.375f, 400);
            AssertVisualLineFallbackIsComplete(fixture.Box);
            RestoreAndAssertRecovery(fixture.Box, oracle.Box, chunk,
                validMetrics, savedReadState, 7.25f, 8_192.375f, 400);

            Assert.True(validMetrics.Length > 1);
            CanvasLineMetrics[] overflowingMetrics = validMetrics.ToArray();
            overflowingMetrics[0].CharacterCount = int.MaxValue;
            SetCachedMetrics(chunk, overflowingMetrics);
            fixture.Box.Arrange(7.25f, 8_192.375f, 400);
            AssertVisualLineFallbackIsComplete(fixture.Box);
            RestoreAndAssertRecovery(fixture.Box, oracle.Box, chunk,
                validMetrics, savedReadState, 7.25f, 8_192.375f, 400);

            fixture.Box.Arrange(7.25f, float.NaN, 400);
            AssertVisualLineFallbackIsComplete(fixture.Box);
            fixture.Box.Arrange(7.25f, 8_192.375f, 400);
            oracle.Box.Arrange(7.25f, 8_192.375f, 400);
            AssertMatchesIndependentLegacyOracle(
                fixture.Box, oracle.Box, 7.25f, 8_192.375f, 400);
        }
        finally
        {
            SetLineMetricsCache(chunk, savedMetrics, savedReadState);
        }

        fixture.Box.Dispose();
        AssertVisualLineFallbackIsComplete(fixture.Box);
        AssertOptionalChunkMetricCacheIsCleared(fixture.Box);
    }

    [Fact]
    public async Task CancellationDuringRemeasurePreservesPreviouslyPublishedLinePlacement()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        using var cancellation = new CancellationTokenSource();
        string text = CreateLongMultichunkCode(lineCount: 48, longTokenLength: 60);
        await using CodeBlockVisualLineFixture fixture = CodeBlockVisualLineFixture.Create(
            device, text, 1f, FlowDirection.RightToLeft, CanvasWordWrapping.Wrap,
            availableWidth: 370, chunkCount: 3, startLine: 1, showLineNumbers: true,
            cancellationToken: cancellation.Token);
        await using CodeBlockVisualLineFixture oracle = CodeBlockVisualLineFixture.Create(
            device, text, 1f, FlowDirection.RightToLeft, CanvasWordWrapping.Wrap,
            availableWidth: 370, chunkCount: 3, startLine: 1, showLineNumbers: true);

        AssertMatchesIndependentLegacyOracle(fixture.Box, oracle.Box, 4.25f, 233.625f, 370);
        LinePlacement[] previous = ReadVisualLines(fixture.Box);
        Array previousArray = fixture.Box.VisualLineIndex.Lines;
        long previousBuildCount = fixture.Box.VisualLineCacheBuildCount;

        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Box.Measure(210));
        Assert.Equal(previous, ReadVisualLines(fixture.Box));
        Assert.Same(previousArray, fixture.Box.VisualLineIndex.Lines);
        Assert.Equal(previousBuildCount, fixture.Box.VisualLineCacheBuildCount);

        fixture.Context.SetCancellationToken(CancellationToken.None);
        fixture.Box.Arrange(4.25f, 233.625f, 370);
        AssertMatchesIndependentLegacyOracle(fixture.Box, oracle.Box, 4.25f, 233.625f, 370);
    }

    [Theory]
    [InlineData(9, 1, 9)]
    [InlineData(10, 1, 10)]
    [InlineData(99, 1, 99)]
    [InlineData(100, 1, 100)]
    [InlineData(999, 1, 999)]
    [InlineData(1000, 1, 1000)]
    [InlineData(1, int.MaxValue, 2)]
    public async Task GutterWidthMatchesLegacyInvariantDecimalDigitOracle(
        int expectedLastLine,
        int startLine,
        int lineCount)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        string text = string.Join("\n", Enumerable.Repeat("x", lineCount));
        await using CodeBlockVisualLineFixture fixture = CodeBlockVisualLineFixture.Create(
            device, text, 1f, FlowDirection.LeftToRight, CanvasWordWrapping.NoWrap,
            availableWidth: 720, chunkCount: global::System.Math.Min(8, lineCount), startLine,
            showLineNumbers: true);

        Assert.Equal(lineCount, fixture.Box.LineCount);
        fixture.Box.Measure(720);
        fixture.Box.Arrange(0.375f, 0.625f, 720);

        int wrappedLegacyLastLine = global::System.Math.Max(
            1, unchecked(startLine + global::System.Math.Max(0, lineCount - 1)));
        Assert.Equal(expectedLastLine, wrappedLegacyLastLine);
        int digits = wrappedLegacyLastLine.ToString(CultureInfo.InvariantCulture).Length;
        double expectedGutterWidth = 0d + 10f + global::System.Math.Max(2, digits) *
            global::System.Math.Max(6, fixture.Style.FontSize * 0.56f) + 10f;
        double innerWidth = global::System.Math.Max(
            1, 720 - fixture.Box.Margin.Left - fixture.Box.Margin.Right);
        double codeTextPaddingLeft = expectedGutterWidth > 0 ? 12 : 0;
        float expectedViewportWidth = (float)global::System.Math.Max(1,
            innerWidth - fixture.Style.Padding.Left - fixture.Style.Padding.Right -
            expectedGutterWidth - codeTextPaddingLeft);
        Assert.Equal(expectedViewportWidth, fixture.Box.HorizontalViewport);
    }

    [Fact]
    public async Task LongMultichunkRepeatedArrangeReportsAllocationAndElapsedDiagnostics()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        string text = CreateLongMultichunkCode(lineCount: 2_048, longTokenLength: 96);
        await using CodeBlockVisualLineFixture actual = CodeBlockVisualLineFixture.Create(
            device, text, 1.5f, FlowDirection.LeftToRight, CanvasWordWrapping.NoWrap,
            availableWidth: 428, chunkCount: 8, startLine: 10_000, showLineNumbers: true);
        await using CodeBlockVisualLineFixture oracle = CodeBlockVisualLineFixture.Create(
            device, text, 1.5f, FlowDirection.LeftToRight, CanvasWordWrapping.NoWrap,
            availableWidth: 428, chunkCount: 8, startLine: 10_000, showLineNumbers: true);

        AssertNativeMetricsAvailable(actual.Box);
        AssertNativeMetricsAvailable(oracle.Box);
        Assert.True(actual.Box.CanScrollHorizontally);
        float x = 11.375f;
        float y = 131_072.375f;
        actual.Box.Arrange(x, y, 428);
        oracle.Box.Arrange(x, y, 428);
        AssertMatchesIndependentLegacyOracle(actual.Box, oracle.Box, x, y, 428);

        // Warm style/context access and the candidate fast path before taking
        // diagnostic counters. The elapsed and allocated figures are evidence
        // only; they are deliberately not pass/fail performance limits.
        for (int index = 0; index < 6; index++)
            actual.Box.Arrange(x, y + index * 0.125f, 428);

        Array warmedVisualLines = actual.Box.VisualLineIndex.Lines;
        Array warmedBottomEdges = actual.Box.VisualLineIndex.BottomEdges;
        object?[] warmedNativeLayouts = CaptureChunkNativeLayouts(actual.Box);
        long warmedBuildCount = actual.Box.VisualLineCacheBuildCount;
        (int scrollChanges, long allocatedBytes, long elapsedMilliseconds) =
            MeasureRepeatedVerticalArrange(actual.Box, x, y);

        float finalY = y + 12 + 119 * 0.125f;
        oracle.Box.Arrange(x + (119 % 3) * 0.25f, finalY, 428);
        Assert.True(oracle.Box.SetHorizontalOffset(actual.Box.HorizontalOffset));
        Assert.Equal(actual.Box.HorizontalOffset, oracle.Box.HorizontalOffset);
        AssertMatchesIndependentLegacyOracle(
            actual.Box, oracle.Box, (float)actual.Box.Bounds.X, (float)actual.Box.Bounds.Y, 428);

        bool reusedVisualLines = ReferenceEquals(warmedVisualLines, actual.Box.VisualLineIndex.Lines);
        bool reusedBottomEdges = ReferenceEquals(warmedBottomEdges, actual.Box.VisualLineIndex.BottomEdges);
        long finalBuildCount = actual.Box.VisualLineCacheBuildCount;
        Assert.Equal(15, scrollChanges);
        Assert.Equal(warmedBuildCount, finalBuildCount);
        Assert.True(reusedVisualLines);
        Assert.True(reusedBottomEdges);
        Assert.Equal(0, allocatedBytes);
        AssertSameChunkNativeLayouts(actual.Box, warmedNativeLayouts);

        _output.WriteLine(
            "visual-line-placement diagnostic: lines={0}, chunks={1}, placements=120, " +
            "allocatedBytes={2}, elapsedMs={3}, cacheBuilds={4}, lineArrayReused={5}, " +
            "bottomArrayReused={6}, scale={7}, y={8:R}. These observations are not a release gate.",
            actual.Box.IndexedVisualLineCount,
            actual.Box.Chunks.Count,
            allocatedBytes,
            elapsedMilliseconds,
            finalBuildCount.ToString(CultureInfo.InvariantCulture),
            reusedVisualLines,
            reusedBottomEdges,
            actual.Context.RasterizationScale,
            actual.Box.Bounds.Y);

        AssertSameOriginHorizontalScrollReusesPlacement(actual, oracle);
    }

    private static (int ScrollChanges, long AllocatedBytes, long ElapsedMilliseconds) MeasureRepeatedVerticalArrange(
        CodeBlockBox box,
        float x,
        float y)
    {
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        int scrollChanges = 0;
        for (int index = 0; index < 120; index++)
        {
            float placementY = y + 12 + index * 0.125f;
            box.Arrange(x + (index % 3) * 0.25f, placementY, 428);
            if (index % 8 == 0)
            {
                double nextOffset = index % 16 == 0 ? 24 : 64;
                if (box.SetHorizontalOffset(nextOffset))
                    scrollChanges++;
            }
        }
        long elapsedTicks = Stopwatch.GetTimestamp() - started;
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long elapsedMilliseconds = (long)(elapsedTicks * 1000d / Stopwatch.Frequency);
        return (scrollChanges, allocatedBytes, elapsedMilliseconds);
    }

    private void AssertSameOriginHorizontalScrollReusesPlacement(
        CodeBlockVisualLineFixture actual,
        CodeBlockVisualLineFixture oracle)
    {
        float horizontalY = (float)actual.Box.Bounds.Y;
        float horizontalX = (float)actual.Box.Bounds.X;
        Array horizontalVisualLines = actual.Box.VisualLineIndex.Lines;
        Array horizontalBottomEdges = actual.Box.VisualLineIndex.BottomEdges;
        object?[] horizontalNativeLayouts = CaptureChunkNativeLayouts(actual.Box);
        long horizontalBuildCount = actual.Box.VisualLineCacheBuildCount;
        int horizontalChanges = 0;
        long horizontalAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long horizontalStarted = Stopwatch.GetTimestamp();
        for (int index = 0; index < 120; index++)
        {
            double nextOffset = index % 2 == 0 ? 64 : 24;
            if (actual.Box.SetHorizontalOffset(nextOffset))
                horizontalChanges++;
        }
        long horizontalElapsedTicks = Stopwatch.GetTimestamp() - horizontalStarted;
        long horizontalAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - horizontalAllocatedBefore;
        long horizontalElapsedMilliseconds =
            (long)(horizontalElapsedTicks * 1000d / Stopwatch.Frequency);

        Assert.Equal(120, horizontalChanges);
        Assert.Equal(horizontalY, actual.Box.Bounds.Y);
        Assert.Equal(horizontalBuildCount, actual.Box.VisualLineCacheBuildCount);
        Assert.Same(horizontalVisualLines, actual.Box.VisualLineIndex.Lines);
        Assert.Same(horizontalBottomEdges, actual.Box.VisualLineIndex.BottomEdges);
        Assert.Equal(0, horizontalAllocatedBytes);
        AssertSameChunkNativeLayouts(actual.Box, horizontalNativeLayouts);

        oracle.Box.Arrange(horizontalX, horizontalY, 428);
        Assert.Equal(actual.Box.HorizontalOffset, oracle.Box.HorizontalOffset);
        AssertMatchesIndependentLegacyOracle(actual.Box, oracle.Box, horizontalX, horizontalY, 428);
        _output.WriteLine(
            "same-origin horizontal-scroll diagnostic: changes={0}, allocatedBytes={1}, " +
            "elapsedMs={2}, cacheBuilds={3}, nativeLayoutsReused={4}. These observations are not a release gate.",
            horizontalChanges,
            horizontalAllocatedBytes,
            horizontalElapsedMilliseconds,
            actual.Box.VisualLineCacheBuildCount.ToString(CultureInfo.InvariantCulture),
            horizontalNativeLayouts.Zip(CaptureChunkNativeLayouts(actual.Box), ReferenceEquals).All(static same => same));
    }

    private static string CreateLongMultichunkCode(int lineCount, int longTokenLength)
    {
        var builder = new StringBuilder(lineCount * (longTokenLength + 48));
        string longToken = string.Join(
            ' ', Enumerable.Repeat(new string('x', 12), global::System.Math.Max(1, longTokenLength / 12)));
        for (int index = 0; index < lineCount; index++)
        {
            builder.Append("    var value_");
            builder.Append(index.ToString("D5", CultureInfo.InvariantCulture));
            builder.Append(" = \"");
            builder.Append(longToken);
            builder.Append("\"; // logical line ");
            builder.Append(index.ToString(CultureInfo.InvariantCulture));
            builder.Append(index % 5 == 0 ? "\r\n" : "\n");
        }
        return builder.ToString();
    }

    private static CanvasLineMetrics[][] GetChunkMetrics(CodeBlockBox box)
    {
        var result = new CanvasLineMetrics[box.Chunks.Count][];
        for (int index = 0; index < result.Length; index++)
            result[index] = Assert.IsType<CanvasLineMetrics[]>(box.Chunks[index].GetLineMetricsSnapshot());
        return result;
    }

    private static void AssertPrivatePlacementFixtureReady(
        CodeBlockBox box,
        MarkdownLayoutContext targetContext)
    {
        int expectedOffset = 0;
        foreach (InlineContainerBox chunk in box.Chunks)
        {
            Assert.Equal(expectedOffset, chunk.CodeBlockTextOffset);
            Assert.InRange(chunk.CodeBlockTextLength, 0, box.CodeText.Length - expectedOffset);
            TextRun run = Assert.IsType<TextRun>(Assert.Single(chunk.Runs));
            Assert.Equal(box.CodeText.Substring(expectedOffset, chunk.CodeBlockTextLength), run.Text);

            Assert.True(
                chunk.TryGetPrivateRasterPlacementCounts(
                    targetContext,
                    global::System.Math.Max(1f, (float)chunk.Bounds.Width),
                    out int runCount,
                    out int imageCount,
                    out long styleKeyCopyEntries,
                    allowUnmeasuredTextOnly: true,
                    allowIdleImagePlacements: false,
                    allowCodeForegroundSpans: true),
                $"Code chunk at offset {expectedOffset}, length {chunk.CodeBlockTextLength}, " +
                $"boundsWidth {chunk.Bounds.Width:R} was not eligible for private placement.");
            Assert.Equal(1, runCount);
            Assert.Equal(0, imageCount);
            Assert.True(styleKeyCopyEntries >= 0);
            expectedOffset += chunk.CodeBlockTextLength;
        }

        Assert.Equal(box.CodeText.Length, expectedOffset);
        Assert.True(
            box.TryGetPrivatePlacementCounts(
                targetContext,
                allowUnmeasured: true,
                out int copiedLineCount,
                out long styleKeyEntries),
            $"Code block failed bounded private placement preflight: codeLength={box.CodeText.Length}, " +
            $"lineCount={box.LineCount}, chunkCount={box.Chunks.Count}, styleKeys={styleKeyEntries}.");
        Assert.Equal(box.LineCount, copiedLineCount);
    }

    private static object?[] CaptureChunkNativeLayouts(CodeBlockBox box)
    {
        var result = new object?[box.Chunks.Count];
        for (int index = 0; index < result.Length; index++)
            result[index] = box.Chunks[index].TextLayout;
        Assert.All(result, item => Assert.NotNull(item));
        return result;
    }

    private static void AssertSameChunkNativeLayouts(CodeBlockBox box, object?[] expected)
    {
        Assert.Equal(box.Chunks.Count, expected.Length);
        for (int index = 0; index < expected.Length; index++)
            Assert.Same(expected[index], box.Chunks[index].TextLayout);
    }

    private static void SetCachedMetrics(InlineContainerBox chunk, CanvasLineMetrics[]? metrics)
        => SetLineMetricsCache(chunk, metrics, read: true);

    // Fault injection: no product path yields malformed DirectWrite line
    // metrics, so crafted ones replace the chunk's cached metrics.
    private static void SetLineMetricsCache(InlineContainerBox chunk, CanvasLineMetrics[]? metrics, bool read)
    {
        chunk.ReplaceCachedLineMetrics(metrics, read);
        Assert.Same(metrics, chunk.CachedLineMetrics);
        Assert.Equal(read, chunk.HasReadLineMetrics);
    }

    private static void RestoreAndAssertRecovery(
        CodeBlockBox subject,
        CodeBlockBox oracle,
        InlineContainerBox mutatedChunk,
        CanvasLineMetrics[] validMetrics,
        bool originalReadState,
        float x,
        float y,
        float width)
    {
        SetLineMetricsCache(mutatedChunk, validMetrics, originalReadState);
        subject.Arrange(x, y, width);
        AssertMatchesIndependentLegacyOracle(subject, oracle, x, y, width);
    }

    private static void AssertVisualLineFallbackIsComplete(CodeBlockBox box)
    {
        Assert.Equal(0, box.IndexedVisualLineCount);
        Assert.Empty(box.VisualLineIndex.Lines);
        Assert.Empty(box.VisualLineIndex.BottomEdges);
        AssertOptionalChunkMetricCacheIsCleared(box);
    }

    private static void AssertOptionalChunkMetricCacheIsCleared(CodeBlockBox box)
    {
        VisualLineChunkCache[]? entries = box.VisualLineIndex.ChunkCache;
        if (entries is null)
            return;

        foreach (VisualLineChunkCache entry in entries)
        {
            Assert.Null(entry.Chunk);
            Assert.Null(entry.Metrics);
        }
    }

    private static void AssertNativeMetricsAvailable(CodeBlockBox box)
    {
        Assert.NotEmpty(box.Chunks);
        foreach (InlineContainerBox chunk in box.Chunks)
        {
            CanvasLineMetrics[] metrics = Assert.IsType<CanvasLineMetrics[]>(chunk.GetLineMetricsSnapshot());
            Assert.NotEmpty(metrics);
            Assert.All(metrics, metric =>
            {
                Assert.True(metric.CharacterCount >= 0);
                Assert.True(float.IsFinite(metric.Height));
            });
        }
    }

    private static void AssertLineBreakChunkSeamsUseEffectiveHeight(CodeBlockBox box)
    {
        for (int chunkIndex = 0; chunkIndex + 1 < box.Chunks.Count; chunkIndex++)
        {
            InlineContainerBox preceding = box.Chunks[chunkIndex];
            InlineContainerBox following = box.Chunks[chunkIndex + 1];
            int precedingEnd = preceding.CodeBlockTextOffset + preceding.CodeBlockTextLength;
            if (preceding.CodeBlockTextLength <= 0 ||
                precedingEnd > box.CodeText.Length ||
                box.CodeText[precedingEnd - 1] != '\n')
            {
                continue;
            }

            CanvasLineMetrics[] metrics = Assert.IsType<CanvasLineMetrics[]>(
                preceding.GetLineMetricsSnapshot());
            if (metrics.Length <= 1 || metrics[^1].CharacterCount != 0)
                continue;

            double expectedAdvance = 0;
            for (int index = 0; index < metrics.Length - 1; index++)
                expectedAdvance += global::System.Math.Max(1, metrics[index].Height);
            double actualAdvance = following.GetTextOriginY() - preceding.GetTextOriginY();
            Assert.True(
                global::System.Math.Abs(expectedAdvance - actualAdvance) <= 1.0,
                $"Chunk {chunkIndex} advanced {actualAdvance:R} DIPs across an LF seam; " +
                $"its nonduplicate native rows occupy {expectedAdvance:R} DIPs.");
            Assert.True(
                global::System.Math.Abs(preceding.Bounds.Bottom - following.Bounds.Top) <= 0.01,
                $"Chunk {chunkIndex} leaves a physical gap or overlap at its LF seam.");
        }
    }

    private static void AssertMatchesIndependentLegacyOracle(
        CodeBlockBox actual,
        CodeBlockBox independentNativeOracle,
        float x,
        float y,
        float width)
    {
        actual.Arrange(x, y, width);
        independentNativeOracle.Arrange(x, y, width);
        LinePlacement[] expected = CaptureLegacyLinePlacement(independentNativeOracle);
        LinePlacement[] actualLines = ReadVisualLines(actual);
        Assert.Equal(expected, actualLines);
        Assert.Equal(expected.Length, actual.IndexedVisualLineCount);

        double[] expectedBottoms = expected.Select(static line => line.Top + line.Height).ToArray();
        double[] actualBottoms = actual.VisualLineIndex.BottomEdges.Cast<double>().ToArray();
        Assert.Equal(expectedBottoms, actualBottoms);
    }

    private static void AssertMatchesIndependentLegacyOracle(
        CodeBlockBox box,
        float x,
        float y,
        float width)
    {
        box.Arrange(x, y, width);
        LinePlacement[] expected = CaptureLegacyLinePlacement(box);
        Assert.Equal(expected, ReadVisualLines(box));
        Assert.Equal(expected.Select(static line => line.Top + line.Height),
            box.VisualLineIndex.BottomEdges.Cast<double>());
    }

    private static LinePlacement[] CaptureLegacyLinePlacement(CodeBlockBox nativeMetricsOwner)
    {
        int[] logicalLineEnds = CreateLogicalLineEnds(nativeMetricsOwner.CodeText);
        var result = new List<LinePlacement>();
        for (int chunkIndex = 0; chunkIndex < nativeMetricsOwner.Chunks.Count; chunkIndex++)
        {
            InlineContainerBox chunk = nativeMetricsOwner.Chunks[chunkIndex];
            CanvasLineMetrics[] metrics = Assert.IsType<CanvasLineMetrics[]>(chunk.GetLineMetricsSnapshot());
            Assert.NotEmpty(metrics);
            int localOffset = 0;
            double top = chunk.GetTextOriginY();
            Assert.True(double.IsFinite(top));

            int visibleMetricCount = metrics.Length;
            if (chunkIndex + 1 < nativeMetricsOwner.Chunks.Count &&
                chunk.CodeBlockTextOffset >= 0 &&
                chunk.CodeBlockTextLength > 0 &&
                chunk.CodeBlockTextLength <= nativeMetricsOwner.CodeText.Length - chunk.CodeBlockTextOffset &&
                chunk.CodeBlockTextOffset + chunk.CodeBlockTextLength ==
                    nativeMetricsOwner.Chunks[chunkIndex + 1].CodeBlockTextOffset &&
                nativeMetricsOwner.Chunks[chunkIndex + 1].CodeBlockTextLength > 0 &&
                nativeMetricsOwner.CodeText[chunk.CodeBlockTextOffset + chunk.CodeBlockTextLength - 1] == '\n' &&
                metrics.Length > 1 &&
                metrics[^1].CharacterCount == 0)
            {
                // A real source LF at an inter-chunk boundary produces a
                // zero-character trailing DirectWrite metric in the previous
                // chunk and the first metric in the next chunk for the same
                // logical row. This independent oracle excludes only that
                // exact duplicate row; all other empty metrics remain visible.
                visibleMetricCount--;
            }

            for (int metricIndex = 0; metricIndex < visibleMetricCount; metricIndex++)
            {
                CanvasLineMetrics metric = metrics[metricIndex];
                int textOffset = chunk.CodeBlockTextOffset + localOffset;
                int logicalLineIndex = FindFirstLogicalLineEndingAfter(logicalLineEnds, textOffset);
                if (logicalLineIndex >= logicalLineEnds.Length)
                    logicalLineIndex = logicalLineEnds.Length - 1;

                double height = global::System.Math.Max(1, metric.Height);
                Assert.True(double.IsFinite(height));
                Assert.True(double.IsFinite(top + height));
                result.Add(new LinePlacement(logicalLineIndex, top, height));

                // Preserve the original addition order; an offset-plus-delta
                // transform does not reproduce its large-coordinate rounding.
                top += height;
                localOffset += global::System.Math.Max(0, metric.CharacterCount);
            }
        }

        return result.ToArray();
    }

    private static int[] CreateLogicalLineEnds(string text)
    {
        var ends = new List<int>();
        if (text.Length == 0)
        {
            ends.Add(1);
            return ends.ToArray();
        }

        int start = 0;
        for (int index = 0; index <= text.Length; index++)
        {
            bool atEnd = index == text.Length;
            if (!atEnd && text[index] != '\n')
                continue;

            int end = atEnd ? index : index + 1;
            ends.Add(start + global::System.Math.Max(1, end - start));
            start = end;
        }
        return ends.ToArray();
    }

    private static int FindFirstLogicalLineEndingAfter(int[] ends, int textOffset)
    {
        int index = 0;
        while (index < ends.Length && ends[index] <= textOffset)
            index++;
        return index;
    }

    private static LinePlacement[] ReadVisualLines(CodeBlockBox box)
    {
        CodeVisualLineInfo[] values = box.VisualLineIndex.Lines;
        var result = new LinePlacement[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            CodeVisualLineInfo value = values[index];
            result[index] = new LinePlacement(value.LogicalLineIndex, value.Top, value.Height);
        }
        return result;
    }


    private readonly record struct LinePlacement(int LogicalLineIndex, double Top, double Height);
}
