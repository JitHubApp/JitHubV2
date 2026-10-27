using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using Xunit.Abstractions;

namespace MarkdownRenderer.GitHub.Tests;

[Collection("Shared Canvas text format cache")]
public sealed class CodeBlockVisualLinePlacementCacheTests(ITestOutputHelper output)
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

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
        await using Fixture actual = CreateFixture(
            device, text, rasterizationScale, flowDirection, CanvasWordWrapping.NoWrap,
            availableWidth: 420, chunkCount: 5, startLine: 17, showLineNumbers: true);
        await using Fixture oracle = CreateFixture(
            device, text, rasterizationScale, flowDirection, CanvasWordWrapping.NoWrap,
            availableWidth: 420, chunkCount: 5, startLine: 17, showLineNumbers: true);

        Assert.True(actual.Box.CanScrollHorizontally);
        Assert.True(actual.Box.IndexedVisualLineCount > 0);
        AssertNativeMetricsAvailable(actual.Box);
        AssertNativeMetricsAvailable(oracle.Box);
        AssertLineBreakChunkSeamsUseEffectiveHeight(actual.Box);
        AssertMatchesIndependentLegacyOracle(actual.Box, oracle.Box, 13.375f, 131_071.375f, 420);

        Array firstVisualLines = GetArray(actual.Box, "_visualLines");
        Array firstBottomEdges = GetArray(actual.Box, "_visualLineBottomEdges");
        object?[] firstNativeLayouts = CaptureChunkNativeLayouts(actual.Box);
        long? buildCount = TryGetCacheBuildCount(actual.Box);

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

        Array beforeScrollLines = GetArray(actual.Box, "_visualLines");
        Array beforeScrollBottomEdges = GetArray(actual.Box, "_visualLineBottomEdges");
        double unchangedOrigin = actual.Box.Chunks[0].GetTextOriginY();
        foreach (double offset in new[] { 24d, 72d, 128d, 48d })
        {
            Assert.True(actual.Box.SetHorizontalOffset(offset));
            Assert.True(oracle.Box.SetHorizontalOffset(offset));
            Assert.Equal(unchangedOrigin, actual.Box.Chunks[0].GetTextOriginY());
            AssertMatchesIndependentLegacyOracle(
                actual.Box, oracle.Box, (float)actual.Box.Bounds.X, (float)actual.Box.Bounds.Y, 420);
        }

        if (buildCount is not null)
        {
            Assert.Equal(buildCount.Value, TryGetCacheBuildCount(actual.Box));
            Assert.Same(firstVisualLines, GetArray(actual.Box, "_visualLines"));
            Assert.Same(firstBottomEdges, GetArray(actual.Box, "_visualLineBottomEdges"));
            Assert.Same(beforeScrollLines, GetArray(actual.Box, "_visualLines"));
            Assert.Same(beforeScrollBottomEdges, GetArray(actual.Box, "_visualLineBottomEdges"));
        }
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
        await using Fixture source = CreateFixture(
            device, text, 1.5f, FlowDirection.RightToLeft, CanvasWordWrapping.Wrap,
            availableWidth: 460, chunkCount: 4, startLine: 99, showLineNumbers: true);
        await using Fixture initialOracle = CreateFixture(
            device, text, 1.5f, FlowDirection.RightToLeft, CanvasWordWrapping.Wrap,
            availableWidth: 460, chunkCount: 4, startLine: 99, showLineNumbers: true);

        AssertNativeMetricsAvailable(source.Box);
        AssertNativeMetricsAvailable(initialOracle.Box);
        AssertLineBreakChunkSeamsUseEffectiveHeight(source.Box);
        AssertMatchesIndependentLegacyOracle(source.Box, initialOracle.Box, 5.25f, 91_337.625f, 460);

        CanvasLineMetrics[][] sourceMetricsBeforeWidthChange = GetChunkMetrics(source.Box);
        object?[] sourceLayoutsBeforeWidthChange = CaptureChunkNativeLayouts(source.Box);
        long? buildCountBeforeWidthChange = TryGetCacheBuildCount(source.Box);
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
        if (buildCountBeforeWidthChange is not null)
            Assert.True(TryGetCacheBuildCount(source.Box)!.Value > buildCountBeforeWidthChange.Value);
        AssertMatchesIndependentLegacyOracle(
            source.Box, initialOracle.Box, 5.25f, 91_337.625f, 208);

        CodeBlockHighlightCacheKey highlightKey = new(
            source.Box.CodeLanguage,
            source.Box.Metadata.CodeTextHash,
            source.Box.CodeText.Length,
            CodeBlockThemeVariant.Light,
            ProviderIdentity: 921,
            ProviderRevision: 1);
        CodeBlockHighlightSpan[] spans =
        [
            new(4, "var".Length, Color.FromArgb(255, 181, 74, 208)),
            new(global::System.Math.Max(0, text.IndexOf("value", StringComparison.Ordinal)), "value".Length,
                Color.FromArgb(255, 48, 132, 202)),
        ];
        Assert.True(source.Box.ApplySyntaxHighlighting(highlightKey, spans));
        Assert.True(initialOracle.Box.ApplySyntaxHighlighting(highlightKey, spans));
        Assert.True(source.Box.PrivatePlacementForegroundSpanCount > 0);
        source.Box.Arrange(5.25f, 91_337.625f, 208);
        initialOracle.Box.Arrange(5.25f, 91_337.625f, 208);
        AssertMatchesIndependentLegacyOracle(
            source.Box, initialOracle.Box, 5.25f, 91_337.625f, 208);

        MarkdownLayoutContext candidateContext = CreateCompatibleContext(source, device);
        AssertPrivatePlacementFixtureReady(source.Box, candidateContext);
        CodeBlockBox? shell = null;
        Fixture? adopted = null;
        try
        {
            CodeBlockBox candidateBox = source.Box.CreatePrivatePlacementCloneShell(candidateContext);
            shell = candidateBox;
            foreach (InlineContainerBox sourceChunk in source.Box.Chunks)
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

            candidateBox.Measure(208);
            candidateBox.Arrange(5.25f, 91_337.625f, 208);
            AssertLineBreakChunkSeamsUseEffectiveHeight(candidateBox);
            adopted = new Fixture(candidateBox, candidateContext, source.Theme, source.Style, ownsContext: true);

            Assert.NotSame(source.Box, adopted.Box);
            Assert.Equal(source.Box.CodeText, adopted.Box.CodeText);
            Assert.Equal(source.Box.Chunks.Count, adopted.Box.Chunks.Count);
            Assert.True(adopted.Box.HasAppliedSyntaxHighlighting(highlightKey));
            Assert.Equal(source.Box.PrivatePlacementForegroundSpanCount,
                adopted.Box.PrivatePlacementForegroundSpanCount);
            AssertMatchesIndependentLegacyOracle(adopted.Box, 5.25f, 91_337.625f, 208);
            for (int index = 0; index < source.Box.Chunks.Count; index++)
            {
                Assert.NotSame(source.Box.Chunks[index], adopted.Box.Chunks[index]);
                Assert.NotSame(
                    sourceMetricsAfterWidthChange[index],
                    Assert.IsType<CanvasLineMetrics[]>(adopted.Box.Chunks[index].GetLineMetricsSnapshot()));
            }
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

    [Fact]
    public async Task MissingMetricsClearsIndexAndLaterNativeMetricsRecoverIt()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        string text = CreateLongMultichunkCode(lineCount: 36, longTokenLength: 72);
        await using Fixture fixture = CreateFixture(
            device, text, 2f, FlowDirection.LeftToRight, CanvasWordWrapping.Wrap,
            availableWidth: 400, chunkCount: 3, startLine: 8, showLineNumbers: true);
        await using Fixture oracle = CreateFixture(
            device, text, 2f, FlowDirection.LeftToRight, CanvasWordWrapping.Wrap,
            availableWidth: 400, chunkCount: 3, startLine: 8, showLineNumbers: true);

        AssertNativeMetricsAvailable(fixture.Box);
        AssertMatchesIndependentLegacyOracle(fixture.Box, oracle.Box, 9.125f, 1_024.375f, 400);
        int originalCount = fixture.Box.IndexedVisualLineCount;
        long? buildCount = TryGetCacheBuildCount(fixture.Box);

        InlineContainerBox missingChunk = fixture.Box.Chunks[1];
        FieldInfo metricsField = GetRequiredPrivateField(typeof(InlineContainerBox), "_cachedLineMetrics");
        FieldInfo metricsReadField = GetRequiredPrivateField(typeof(InlineContainerBox), "_lineMetricsRead");
        object? savedMetrics = metricsField.GetValue(missingChunk);
        object? savedReadState = metricsReadField.GetValue(missingChunk);
        Assert.IsType<CanvasLineMetrics[]>(savedMetrics);
        try
        {
            metricsField.SetValue(missingChunk, null);
            metricsReadField.SetValue(missingChunk, true);
            fixture.Box.Arrange(9.125f, 1_024.375f, 400);

            Assert.Equal(0, fixture.Box.IndexedVisualLineCount);
            Assert.Empty(ReadVisualLines(fixture.Box));
            Assert.Empty(GetArray(fixture.Box, "_visualLineBottomEdges"));
            Assert.Equal(buildCount, TryGetCacheBuildCount(fixture.Box));

            metricsField.SetValue(missingChunk, savedMetrics);
            metricsReadField.SetValue(missingChunk, savedReadState);
            fixture.Box.Arrange(9.125f, 1_024.375f, 400);
            oracle.Box.Arrange(9.125f, 1_024.375f, 400);

            Assert.Equal(originalCount, fixture.Box.IndexedVisualLineCount);
            AssertMatchesIndependentLegacyOracle(
                fixture.Box, oracle.Box, 9.125f, 1_024.375f, 400);
            if (buildCount is not null)
                Assert.True(TryGetCacheBuildCount(fixture.Box)!.Value > buildCount.Value);
        }
        finally
        {
            metricsField.SetValue(missingChunk, savedMetrics);
            metricsReadField.SetValue(missingChunk, savedReadState);
        }
    }

    [Fact]
    public async Task EmptyNonfiniteOverflowAndInvalidOriginInputsFailClosedThenRecover()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: true);
        string text = CreateLongMultichunkCode(lineCount: 30, longTokenLength: 72);
        await using Fixture fixture = CreateFixture(
            device, text, 1.5f, FlowDirection.LeftToRight, CanvasWordWrapping.Wrap,
            availableWidth: 400, chunkCount: 3, startLine: 9, showLineNumbers: true);
        await using Fixture oracle = CreateFixture(
            device, text, 1.5f, FlowDirection.LeftToRight, CanvasWordWrapping.Wrap,
            availableWidth: 400, chunkCount: 3, startLine: 9, showLineNumbers: true);

        AssertNativeMetricsAvailable(fixture.Box);
        AssertMatchesIndependentLegacyOracle(fixture.Box, oracle.Box, 7.25f, 8_192.375f, 400);
        InlineContainerBox chunk = fixture.Box.Chunks[0];
        FieldInfo metricsField = GetRequiredPrivateField(typeof(InlineContainerBox), "_cachedLineMetrics");
        FieldInfo readField = GetRequiredPrivateField(typeof(InlineContainerBox), "_lineMetricsRead");
        object? savedMetrics = metricsField.GetValue(chunk);
        object? savedReadState = readField.GetValue(chunk);
        CanvasLineMetrics[] validMetrics = Assert.IsType<CanvasLineMetrics[]>(savedMetrics);
        long? originalBuildCount = TryGetCacheBuildCount(fixture.Box);

        try
        {
            SetCachedMetrics(chunk, metricsField, readField, Array.Empty<CanvasLineMetrics>());
            fixture.Box.Arrange(7.25f, 8_192.375f, 400);
            AssertVisualLineFallbackIsComplete(fixture.Box);
            Assert.Equal(originalBuildCount, TryGetCacheBuildCount(fixture.Box));
            RestoreAndAssertRecovery(fixture.Box, oracle.Box, chunk, metricsField, readField,
                validMetrics, savedReadState, 7.25f, 8_192.375f, 400);

            CanvasLineMetrics[] nonfiniteMetrics = validMetrics.ToArray();
            nonfiniteMetrics[0] = WithMetricValue(nonfiniteMetrics[0], "Height", float.NaN);
            SetCachedMetrics(chunk, metricsField, readField, nonfiniteMetrics);
            fixture.Box.Arrange(7.25f, 8_192.375f, 400);
            AssertVisualLineFallbackIsComplete(fixture.Box);
            RestoreAndAssertRecovery(fixture.Box, oracle.Box, chunk, metricsField, readField,
                validMetrics, savedReadState, 7.25f, 8_192.375f, 400);

            Assert.True(validMetrics.Length > 1);
            CanvasLineMetrics[] overflowingMetrics = validMetrics.ToArray();
            overflowingMetrics[0] = WithMetricValue(
                overflowingMetrics[0], "CharacterCount", int.MaxValue);
            SetCachedMetrics(chunk, metricsField, readField, overflowingMetrics);
            fixture.Box.Arrange(7.25f, 8_192.375f, 400);
            AssertVisualLineFallbackIsComplete(fixture.Box);
            RestoreAndAssertRecovery(fixture.Box, oracle.Box, chunk, metricsField, readField,
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
            metricsField.SetValue(chunk, savedMetrics);
            readField.SetValue(chunk, savedReadState);
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
        await using Fixture fixture = CreateFixture(
            device, text, 1f, FlowDirection.RightToLeft, CanvasWordWrapping.Wrap,
            availableWidth: 370, chunkCount: 3, startLine: 1, showLineNumbers: true,
            cancellationToken: cancellation.Token);
        await using Fixture oracle = CreateFixture(
            device, text, 1f, FlowDirection.RightToLeft, CanvasWordWrapping.Wrap,
            availableWidth: 370, chunkCount: 3, startLine: 1, showLineNumbers: true);

        AssertMatchesIndependentLegacyOracle(fixture.Box, oracle.Box, 4.25f, 233.625f, 370);
        LinePlacement[] previous = ReadVisualLines(fixture.Box);
        Array previousArray = GetArray(fixture.Box, "_visualLines");
        long? previousBuildCount = TryGetCacheBuildCount(fixture.Box);

        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Box.Measure(210));
        Assert.Equal(previous, ReadVisualLines(fixture.Box));
        Assert.Same(previousArray, GetArray(fixture.Box, "_visualLines"));
        Assert.Equal(previousBuildCount, TryGetCacheBuildCount(fixture.Box));

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
        await using Fixture fixture = CreateFixture(
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
        await using Fixture actual = CreateFixture(
            device, text, 1.5f, FlowDirection.LeftToRight, CanvasWordWrapping.NoWrap,
            availableWidth: 428, chunkCount: 8, startLine: 10_000, showLineNumbers: true);
        await using Fixture oracle = CreateFixture(
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

        Array warmedVisualLines = GetArray(actual.Box, "_visualLines");
        Array warmedBottomEdges = GetArray(actual.Box, "_visualLineBottomEdges");
        object?[] warmedNativeLayouts = CaptureChunkNativeLayouts(actual.Box);
        long? warmedBuildCount = TryGetCacheBuildCount(actual.Box);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        int scrollChanges = 0;
        for (int index = 0; index < 120; index++)
        {
            float placementY = y + 12 + index * 0.125f;
            actual.Box.Arrange(x + (index % 3) * 0.25f, placementY, 428);
            if (index % 8 == 0)
            {
                double nextOffset = index % 16 == 0 ? 24 : 64;
                if (actual.Box.SetHorizontalOffset(nextOffset))
                    scrollChanges++;
            }
        }
        long elapsedTicks = Stopwatch.GetTimestamp() - started;
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        long elapsedMilliseconds = (long)(elapsedTicks * 1000d / Stopwatch.Frequency);

        float finalY = y + 12 + 119 * 0.125f;
        oracle.Box.Arrange(x + (119 % 3) * 0.25f, finalY, 428);
        Assert.True(oracle.Box.SetHorizontalOffset(actual.Box.HorizontalOffset));
        Assert.Equal(actual.Box.HorizontalOffset, oracle.Box.HorizontalOffset);
        AssertMatchesIndependentLegacyOracle(
            actual.Box, oracle.Box, (float)actual.Box.Bounds.X, (float)actual.Box.Bounds.Y, 428);

        bool reusedVisualLines = ReferenceEquals(warmedVisualLines, GetArray(actual.Box, "_visualLines"));
        bool reusedBottomEdges = ReferenceEquals(warmedBottomEdges, GetArray(actual.Box, "_visualLineBottomEdges"));
        long? finalBuildCount = TryGetCacheBuildCount(actual.Box);
        Assert.Equal(15, scrollChanges);
        if (warmedBuildCount is not null)
        {
            Assert.Equal(warmedBuildCount.Value, finalBuildCount);
            Assert.True(reusedVisualLines);
            Assert.True(reusedBottomEdges);
            Assert.Equal(0, allocatedBytes);
        }
        AssertSameChunkNativeLayouts(actual.Box, warmedNativeLayouts);

        _output.WriteLine(
            "visual-line-placement diagnostic: lines={0}, chunks={1}, placements=120, " +
            "allocatedBytes={2}, elapsedMs={3}, cacheBuilds={4}, lineArrayReused={5}, " +
            "bottomArrayReused={6}, scale={7}, y={8:R}. These observations are not a release gate.",
            actual.Box.IndexedVisualLineCount,
            actual.Box.Chunks.Count,
            allocatedBytes,
            elapsedMilliseconds,
            finalBuildCount?.ToString(CultureInfo.InvariantCulture) ?? "unavailable-on-baseline-runtime",
            reusedVisualLines,
            reusedBottomEdges,
            actual.Context.RasterizationScale,
            actual.Box.Bounds.Y);

        float horizontalY = (float)actual.Box.Bounds.Y;
        float horizontalX = (float)actual.Box.Bounds.X;
        Array horizontalVisualLines = GetArray(actual.Box, "_visualLines");
        Array horizontalBottomEdges = GetArray(actual.Box, "_visualLineBottomEdges");
        object?[] horizontalNativeLayouts = CaptureChunkNativeLayouts(actual.Box);
        long? horizontalBuildCount = TryGetCacheBuildCount(actual.Box);
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
        if (horizontalBuildCount is not null)
        {
            Assert.Equal(horizontalBuildCount.Value, TryGetCacheBuildCount(actual.Box));
            Assert.Same(horizontalVisualLines, GetArray(actual.Box, "_visualLines"));
            Assert.Same(horizontalBottomEdges, GetArray(actual.Box, "_visualLineBottomEdges"));
            Assert.Equal(0, horizontalAllocatedBytes);
        }
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
            TryGetCacheBuildCount(actual.Box)?.ToString(CultureInfo.InvariantCulture) ??
                "unavailable-on-baseline-runtime",
            horizontalNativeLayouts.Zip(CaptureChunkNativeLayouts(actual.Box), ReferenceEquals).All(static same => same));
    }

    private static Fixture CreateFixture(
        CanvasDevice device,
        string codeText,
        float rasterizationScale,
        FlowDirection flowDirection,
        CanvasWordWrapping wrapping,
        float availableWidth,
        int chunkCount,
        int startLine,
        bool showLineNumbers,
        CancellationToken cancellationToken = default)
    {
        ElementStyle codeStyle = new()
        {
            FontFamily = "Consolas",
            FontSize = 13,
            Foreground = Color.FromArgb(255, 28, 30, 33),
        };
        ElementStyle lineNumberStyle = new()
        {
            FontFamily = "Consolas",
            FontSize = 11,
            Foreground = Color.FromArgb(255, 86, 93, 101),
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        var styles = new Dictionary<string, ElementStyle>
        {
            [MarkdownElementKeys.Body] = codeStyle,
            [MarkdownElementKeys.CodeBlock] = codeStyle,
            [MarkdownElementKeys.CodeBlockLineNumber] = lineNumberStyle,
            [MarkdownElementKeys.CodeBlockGutter] = new ElementStyle(),
            [MarkdownElementKeys.CodeBlockHeader] = new ElementStyle(),
            [MarkdownElementKeys.CodeBlockLanguage] = new ElementStyle(),
        };
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            transparent, transparent, transparent, transparent,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var registry = new MarkdownExtensionRegistry();
        var sourceMap = new MarkdownSourceMap(codeText);
        MarkdownLayoutContext context = CreateContext(
            device, theme, sourceMap, registry, flowDirection, rasterizationScale, cancellationToken);
        var attributes = new Dictionary<string, string>
        {
            ["startline"] = startLine.ToString(CultureInfo.InvariantCulture),
        };
        CodeBlockMetadata metadata = CodeBlockMetadata.FromDeclarative(
            new SourceSpan(0, codeText.Length), codeText, "csharp", attributes);
        var box = new CodeBlockBox(context, metadata, codeText, isCopyButtonEnabled: true, showLineNumbers)
        {
            BlockIndex = context.NextBlockIndex(),
        };

        // CodeBlockBox canonicalizes CRLF/CR to LF in CopyPayload. Chunk
        // descriptors must partition the exact displayed payload so private
        // placement preflight can prove contiguous source offsets and text.
        string displayedCodeText = box.CodeText;
        foreach ((int offset, int length) in SplitAtLineBoundaries(displayedCodeText, chunkCount))
        {
            var chunk = new InlineContainerBox(context, MarkdownElementKeys.CodeBlock)
            {
                BlockIndex = context.NextBlockIndex(),
                CodeLanguage = metadata.Language,
                CodeBlockTextOffset = offset,
                CodeBlockTextLength = length,
                WordWrapping = wrapping,
            };
            chunk.Add(new TextRun(displayedCodeText.Substring(offset, length)));
            box.AddChunk(chunk);
        }

        box.Measure(availableWidth);
        box.Arrange(0, 0, availableWidth);
        return new Fixture(box, context, theme, codeStyle, ownsContext: true);
    }

    private static MarkdownLayoutContext CreateContext(
        CanvasDevice device,
        ThemeSnapshot theme,
        MarkdownSourceMap sourceMap,
        MarkdownExtensionRegistry registry,
        FlowDirection flowDirection,
        float rasterizationScale,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, bool>? disclosureStates = null)
        => new(device, theme, sourceMap, registry, flowDirection, language: "en-US")
        {
            CancellationToken = cancellationToken,
            RasterizationScale = rasterizationScale,
            IsCodeBlockCopyEnabled = true,
            // Private-placement preflight requires the exact immutable state
            // snapshots to be shared. A fresh context otherwise creates a new
            // empty DisclosureStates dictionary and is correctly rejected.
            DisclosureStates = disclosureStates ?? new Dictionary<string, bool>(StringComparer.Ordinal),
        };

    private static MarkdownLayoutContext CreateCompatibleContext(Fixture source, CanvasDevice device)
        => CreateContext(
            device,
            source.Theme,
            source.Context.SourceMap,
            source.Context.Registry,
            source.Context.FlowDirection,
            (float)source.Context.RasterizationScale,
            disclosureStates: source.Context.DisclosureStates);

    private static IEnumerable<(int Offset, int Length)> SplitAtLineBoundaries(string text, int chunkCount)
    {
        int start = 0;
        for (int part = 1; part < chunkCount; part++)
        {
            int target = (int)((long)text.Length * part / chunkCount);
            int split = text.IndexOf('\n', target);
            if (split < 0 || split + 1 >= text.Length)
                break;
            int end = split + 1;
            if (end <= start)
                continue;
            yield return (start, end - start);
            start = end;
        }

        if (start < text.Length)
            yield return (start, text.Length - start);
        else if (text.Length == 0)
            yield return (0, 0);
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
        FieldInfo layoutField = GetRequiredPrivateField(typeof(InlineContainerBox), "_layout");
        var result = new object?[box.Chunks.Count];
        for (int index = 0; index < result.Length; index++)
            result[index] = layoutField.GetValue(box.Chunks[index]);
        Assert.All(result, item => Assert.NotNull(item));
        return result;
    }

    private static void AssertSameChunkNativeLayouts(CodeBlockBox box, object?[] expected)
    {
        Assert.Equal(box.Chunks.Count, expected.Length);
        FieldInfo layoutField = GetRequiredPrivateField(typeof(InlineContainerBox), "_layout");
        for (int index = 0; index < expected.Length; index++)
            Assert.Same(expected[index], layoutField.GetValue(box.Chunks[index]));
    }

    private static void SetCachedMetrics(
        InlineContainerBox chunk,
        FieldInfo metricsField,
        FieldInfo readField,
        CanvasLineMetrics[]? metrics)
    {
        metricsField.SetValue(chunk, metrics);
        readField.SetValue(chunk, true);
    }

    private static CanvasLineMetrics WithMetricValue<T>(CanvasLineMetrics metric, string propertyName, T value)
    {
        object boxedMetric = metric;
        FieldInfo field = typeof(CanvasLineMetrics)
            .GetFields(InstanceFields)
            .SingleOrDefault(candidate =>
                candidate.FieldType == typeof(T) &&
                candidate.Name.Contains(propertyName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Win2D CanvasLineMetrics no longer exposes a test-writable backing field for {propertyName}.");
        field.SetValue(boxedMetric, value);
        return Assert.IsType<CanvasLineMetrics>(boxedMetric);
    }

    private static void RestoreAndAssertRecovery(
        CodeBlockBox subject,
        CodeBlockBox oracle,
        InlineContainerBox mutatedChunk,
        FieldInfo metricsField,
        FieldInfo readField,
        CanvasLineMetrics[] validMetrics,
        object? originalReadState,
        float x,
        float y,
        float width)
    {
        metricsField.SetValue(mutatedChunk, validMetrics);
        readField.SetValue(mutatedChunk, originalReadState);
        subject.Arrange(x, y, width);
        AssertMatchesIndependentLegacyOracle(subject, oracle, x, y, width);
    }

    private static void AssertVisualLineFallbackIsComplete(CodeBlockBox box)
    {
        Assert.Equal(0, box.IndexedVisualLineCount);
        Assert.Empty(GetArray(box, "_visualLines"));
        Assert.Empty(GetArray(box, "_visualLineBottomEdges"));
        AssertOptionalChunkMetricCacheIsCleared(box);
    }

    private static void AssertOptionalChunkMetricCacheIsCleared(CodeBlockBox box)
    {
        FieldInfo? field = typeof(CodeBlockBox).GetField("_visualLineChunkCache", PrivateInstance);
        if (field is null)
            return; // The frozen pre-cache assembly has no descriptor array.

        object? cache = field.GetValue(box);
        if (cache is null)
            return;

        Array entries = Assert.IsAssignableFrom<Array>(cache);
        Type entryType = entries.GetType().GetElementType() ??
            throw new InvalidOperationException("The visual-line chunk cache has no entry type.");
        FieldInfo[] referenceFields = entryType.GetFields(InstanceFields)
            .Where(static member => !member.FieldType.IsValueType)
            .ToArray();
        for (int index = 0; index < entries.Length; index++)
        {
            object entry = entries.GetValue(index) ??
                throw new InvalidOperationException("The visual-line chunk cache entry was null.");
            foreach (FieldInfo referenceField in referenceFields)
                Assert.Null(referenceField.GetValue(entry));
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
        double[] actualBottoms = GetArray(actual, "_visualLineBottomEdges").Cast<double>().ToArray();
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
            GetArray(box, "_visualLineBottomEdges").Cast<double>());
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
        Array values = GetArray(box, "_visualLines");
        Type valueType = values.GetType().GetElementType() ??
            throw new InvalidOperationException("The visual-line array has no element type.");
        FieldInfo logicalLineIndex = GetRequiredPrivateField(valueType, "LogicalLineIndex");
        FieldInfo top = GetRequiredPrivateField(valueType, "Top");
        FieldInfo height = GetRequiredPrivateField(valueType, "Height");
        var result = new LinePlacement[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            object value = values.GetValue(index) ?? throw new InvalidOperationException("A visual line was null.");
            result[index] = new LinePlacement(
                Assert.IsType<int>(logicalLineIndex.GetValue(value)),
                Assert.IsType<double>(top.GetValue(value)),
                Assert.IsType<double>(height.GetValue(value)));
        }
        return result;
    }

    private static Array GetArray(CodeBlockBox box, string fieldName)
        => Assert.IsAssignableFrom<Array>(GetRequiredPrivateField(typeof(CodeBlockBox), fieldName).GetValue(box));

    private static long? TryGetCacheBuildCount(CodeBlockBox box)
    {
        PropertyInfo? property = typeof(CodeBlockBox).GetProperty(
            "VisualLineCacheBuildCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property is null)
            return null; // Frozen pre-optimization assemblies remain test-compatible.
        return Assert.IsType<int>(property.GetValue(box));
    }

    private static FieldInfo GetRequiredPrivateField(Type type, string name)
        => Assert.IsAssignableFrom<FieldInfo>(type.GetField(name, InstanceFields));

    private readonly record struct LinePlacement(int LogicalLineIndex, double Top, double Height);

    private sealed class Fixture(
        CodeBlockBox box,
        MarkdownLayoutContext context,
        ThemeSnapshot theme,
        ElementStyle style,
        bool ownsContext) : IAsyncDisposable
    {
        internal CodeBlockBox Box { get; } = box;
        internal MarkdownLayoutContext Context { get; } = context;
        internal ThemeSnapshot Theme { get; } = theme;
        internal ElementStyle Style { get; } = style;

        public async ValueTask DisposeAsync()
        {
            Box.Dispose();
            if (ownsContext)
                await Context.DrainOwnedRetirementsAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
