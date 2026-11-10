using System.Runtime.CompilerServices;
using System.Threading;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.VectorSceneGraphLiveness;
using static MarkdownRenderer.GitHub.Tests.VectorSemanticHitOracleAssertions;
using static MarkdownRenderer.GitHub.Tests.VectorTerminalRetirementScenes;

namespace MarkdownRenderer.GitHub.Tests;

[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class VectorSceneTerminalRetirementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SnapshotRetirementWaitsForReaderLeaseThenReleasesEverySceneAlias(bool interactionLease)
    {
        (LayoutSnapshot snapshot, VectorSceneBox block, InlineContainerBox inline,
            WeakSceneGraph graph) = CreateSharedSceneSnapshot();
        Assert.True(block.IsNativeResourcesPrepared);
        Assert.True(Assert.IsType<InlineVectorSceneRun>(Assert.Single(inline.Runs)).IsNativeResourcesPrepared);

        var paintEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePaint = new ManualResetEventSlim();
        Task paintLease = Task.Run(() =>
        {
            bool acquired = interactionLease
                ? snapshot.TryBeginInteraction()
                : snapshot.TryBeginPaint();
            Assert.True(acquired);
            paintEntered.TrySetResult();
            try
            {
                if (!releasePaint.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("The snapshot paint lease was not released.");
            }
            finally
            {
                if (interactionLease)
                    snapshot.EndInteraction();
                else
                    snapshot.EndPaint();
            }
        });

        Task? retirement = null;
        bool remainedPending = false;
        bool blockStayedPrepared = false;
        bool inlineStayedPrepared = false;
        try
        {
            await paintEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            retirement = snapshot.Retire();
            Task repeatedRetirement = snapshot.Retire();
            Assert.Same(retirement, repeatedRetirement);

            await Task.Delay(75);
            remainedPending = !retirement.IsCompleted;
            blockStayedPrepared = block.IsNativeResourcesPrepared;
            inlineStayedPrepared = Assert.IsType<InlineVectorSceneRun>(Assert.Single(inline.Runs))
                .IsNativeResourcesPrepared;
        }
        finally
        {
            releasePaint.Set();
            try
            {
                await paintLease.WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                retirement ??= snapshot.Retire();
                await retirement.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        Assert.True(remainedPending, "Snapshot retirement bypassed a held paint/read lease.");
        Assert.True(blockStayedPrepared, "The block scene was terminally disposed while paint held the snapshot lease.");
        Assert.True(inlineStayedPrepared, "The inline scene was terminally disposed while paint held the snapshot lease.");
        Assert.False(block.IsNativeResourcesPrepared);
        Assert.False(Assert.IsType<InlineVectorSceneRun>(Assert.Single(inline.Runs)).IsNativeResourcesPrepared);
        Assert.False(snapshot.TryBeginPaint());
        Assert.False(snapshot.TryBeginInteraction());
        snapshot.Dispose();

        Assert.Equal(MarkdownAccessibilityRole.Diagram, block.AccessibilityRole);
        Assert.Equal("Retirement test diagram", block.AccessibilityName);
        Assert.Equal(new SourceSpan(0, 1), block.SourceRange);
        Assert.Throws<ObjectDisposedException>(() => { _ = block.Scene; });
        Assert.Throws<ObjectDisposedException>(() => { _ = block.Measure(160); });
        Assert.Throws<ObjectDisposedException>(
            () => { _ = Assert.IsType<InlineVectorSceneRun>(Assert.Single(inline.Runs)).Scene; });
        AssertSceneGraphCollected(graph, snapshot, block, inline);
    }

    [Fact]
    public void NativeResourceReleaseIsReversibleAndSharedOwnersRetireIndependently()
    {
        (VectorSceneBox first, VectorSceneBox second,
            WeakSceneGraph graph) = CreateSharedPreparedBoxes();
        try
        {
            VerifyIndependentOwnerTransitions(first, second);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }

        AssertSceneGraphCollected(graph, first, second);
    }

    [Theory]
    [InlineData(10f, 20f, 40f, 18f, 13f, 80f, 36f, 26f)]
    [InlineData(0f, 99f, 0.5f, 0.25f, 0.125f, 1f, 1f, 0.125f)]
    [InlineData(20f, 5f, 100f, 40f, 30f, 25f, 10f, 7.5f)]
    [InlineData(16f, 0f, 20f, 10f, 0f, 1f, 1f, 0f)]
    public void InlineMetricsRemainExactAfterNativeReleaseAndGraphRetirement(
        float referenceFontSize,
        float fontSize,
        float sceneWidth,
        float sceneHeight,
        float sceneBaseline,
        float expectedWidth,
        float expectedHeight,
        float expectedBaseline)
    {
        (InlineVectorSceneRun run, WeakSceneGraph graph) = CreateInlineRunForMetrics(
            sceneWidth,
            sceneHeight,
            sceneBaseline,
            referenceFontSize);

        Assert.True(run.IsNativeResourcesPrepared);
        run.Measure(fontSize);
        AssertInlineMetrics(run, expectedWidth, expectedHeight, expectedBaseline);

        run.ReleaseNativeResources();
        Assert.False(run.IsNativeResourcesPrepared);
        run.Measure(fontSize);
        AssertInlineMetrics(run, expectedWidth, expectedHeight, expectedBaseline);
        Assert.False(run.IsNativeResourcesPrepared);

        run.Dispose();
        Assert.Throws<ObjectDisposedException>(() => run.Measure(fontSize));
        Assert.Throws<ObjectDisposedException>(() => { _ = run.Scene; });
        AssertInlineMetrics(run, expectedWidth, expectedHeight, expectedBaseline);
        Assert.Equal("x squared", run.SemanticText);
        Assert.Equal("x squared", run.AccessibleText);
        Assert.Equal("Inline math x", run.AccessibilityName);
        Assert.Equal("A shared scene graph.", run.AccessibilityDescription);
        Assert.Equal("https://example.test/formula", run.LinkUrl);
        Assert.Equal("Formula documentation", run.LinkTitle);

        AssertSceneGraphCollected(graph, run);
    }

    [Fact]
    public async Task RetiredSnapshotKeepsSemanticMetadataAndTextFormattingIndependentOfSceneGraph()
    {
        (LayoutSnapshot snapshot, VectorSceneBox block, ThemeSnapshot theme,
            WeakSceneGraph graph) = CreateSemanticSceneSnapshot();
        Assert.False(block.IsNativeResourcesPrepared);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode diagram = Assert.Single(document.Root.Children);
        MarkdownSemanticNode linkedLabel = Assert.Single(diagram.Children);
        MarkdownVectorSemanticItem semanticItem = Assert.IsType<MarkdownVectorSemanticItem>(
            block.GetSemanticItem(linkedLabel.VectorSemanticIndex));
        Assert.True(block.TryGetLink(linkedLabel.VectorSemanticIndex, out MarkdownVectorLinkAction? link));
        Assert.NotNull(link);
        Rect bounds = block.GetSemanticBounds(linkedLabel.VectorSemanticIndex);
        var hitPoint = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        SemanticHitOracle hitOracle = CaptureNativeSemanticHitOracle(block, hitPoint);
        Assert.Equal(linkedLabel.VectorSemanticIndex, hitOracle.SelectableIndex);
        Assert.Equal(linkedLabel.VectorSemanticIndex, hitOracle.TextIndex);
        Assert.Equal(linkedLabel.VectorSemanticIndex, hitOracle.LinkedIndex);
        AssertVectorHitParity(block, hitPoint, hitOracle);
        Rect[] rects = document.GetDocumentRects(linkedLabel.TextStart, linkedLabel.TextEnd).ToArray();

        Task retirement = snapshot.Retire();
        await retirement.WaitAsync(TimeSpan.FromSeconds(10));

        AssertRetiredLinkedLabelMetadata(document, diagram, linkedLabel, block, semanticItem, bounds);
        AssertVectorHitParity(block, hitPoint, hitOracle);
        Assert.Equal(rects, document.GetDocumentRects(linkedLabel.TextStart, linkedLabel.TextEnd).ToArray());
        MarkdownTextStyleRun styleRun = AssertSingleAuthoredDiagramStyleRun(document, theme);

        // Rebuild both projections only after terminal retirement. Neither the
        // semantic builder nor focus-plan construction may need the Scene root.
        using var rebuiltSnapshot = new LayoutSnapshot(
            new BlockBox[] { block },
            snapshot.SourceMap,
            width: 160,
            height: (float)block.Bounds.Height);
        MarkdownSemanticDocument rebuilt = rebuiltSnapshot.SemanticDocument;
        MarkdownSemanticNode rebuiltDiagram = Assert.Single(rebuilt.Root.Children);
        MarkdownSemanticNode rebuiltLinkedLabel = Assert.Single(rebuiltDiagram.Children);
        Assert.Equal(document.Text, rebuilt.Text);
        Assert.Equal(document.GetText(linkedLabel), rebuilt.GetText(rebuiltLinkedLabel));
        Assert.Equal(diagram.AccessibilityName, rebuiltDiagram.AccessibilityName);
        Assert.Equal(diagram.AccessibilityDescription, rebuiltDiagram.AccessibilityDescription);
        Assert.Equal(linkedLabel.HelpText, rebuiltLinkedLabel.HelpText);
        Assert.Equal(snapshot.CollectFocusableItems(), rebuiltSnapshot.CollectFocusableItems());
        AssertRebuiltStyleRunMatches(rebuilt, theme, styleRun);

        AssertSceneGraphCollected(
            graph,
            snapshot,
            rebuiltSnapshot,
            block,
            document,
            rebuilt,
            diagram,
            linkedLabel,
            rebuiltDiagram,
            rebuiltLinkedLabel,
            semanticItem,
            styleRun);
    }

    [Fact]
    public void ReverseOrderedDeepSemanticProjectionIsLinearCachedAndSurvivesDrawingRetirement()
    {
        (VectorSceneBox block, WeakSceneGraph graph) = CreateDeepSemanticProjectionBox();
        Assert.False(block.IsNativeResourcesPrepared);

        ElementStyle frozen = block.ResolveSemanticTextStyle(0);
        Assert.Equal("Authored Deep Face", frozen.FontFamily);
        Assert.Equal(Color.FromArgb(0xFF, 0x12, 0x34, 0x56), frozen.Background);
        Assert.Equal("Deepest text", block.GetSemanticItem(0)?.Name);
        Assert.Equal("Reverse ordered root", block.AccessibilityName);

        // Warm the call site before measuring the cache-hit contract.
        for (int i = 0; i < 8; i++)
            _ = block.ResolveSemanticTextStyle(0);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        bool sameProjection = true;
        for (int i = 0; i < 256; i++)
            sameProjection &= ReferenceEquals(frozen, block.ResolveSemanticTextStyle(0));
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.True(sameProjection);
        Assert.Equal(0, allocatedBytes);

        block.Dispose();
        Assert.False(block.IsNativeResourcesPrepared);
        Assert.Same(frozen, block.ResolveSemanticTextStyle(0));
        Assert.Equal(Color.FromArgb(0xFF, 0x12, 0x34, 0x56), block.ResolveSemanticTextStyle(0).Background);
        AssertSceneGraphCollected(graph, block, frozen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CanceledLayoutContextStopsVectorProjectionDuringConstruction(bool emptyScene)
    {
        MarkdownLayoutContext context = CreateContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        context.SetCancellationToken(cancellation.Token);
        MarkdownVectorScene scene = emptyScene
            ? new MarkdownVectorScene(40, 40, 40, Array.Empty<MarkdownVectorCommand>())
            : CreateScene();
        var builder = new MarkdownContentBuilder();
        builder.AddVectorScene(
            scene,
            new SourceSpan(0, 1),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram);
        MarkdownContent content = Assert.Single(builder.Build().Items);

        Assert.ThrowsAny<OperationCanceledException>(
            () => new VectorSceneBox(context, content, MarkdownElementKeys.Diagram));
    }

    [Theory]
    [InlineData(FlowDirection.LeftToRight)]
    [InlineData(FlowDirection.RightToLeft)]
    public async Task OverlappingNestedSemanticHitsKeepDepthAndAreaOrderAfterSceneGraphRetirement(
        FlowDirection flowDirection)
    {
        (LayoutSnapshot snapshot, VectorSceneBox block, WeakSceneGraph graph) =
            CreateOverlappingNestedSemanticSnapshot(flowDirection);

        Rect narrowSiblingBounds = block.GetSemanticBounds(3);
        var narrowSiblingPoint = new Point(
            narrowSiblingBounds.Left + narrowSiblingBounds.Width / 2,
            narrowSiblingBounds.Top + narrowSiblingBounds.Height / 2);
        SemanticHitOracle narrowSiblingOracle = CaptureNativeSemanticHitOracle(block, narrowSiblingPoint);
        Assert.Equal(3, narrowSiblingOracle.SelectableIndex);
        Assert.Equal(3, narrowSiblingOracle.TextIndex);
        Assert.Equal(3, narrowSiblingOracle.LinkedIndex);
        AssertVectorHitParity(block, narrowSiblingPoint, narrowSiblingOracle);

        Rect deepChildBounds = block.GetSemanticBounds(2);
        var deepChildPoint = new Point(
            deepChildBounds.Left + deepChildBounds.Width / 2,
            deepChildBounds.Top + deepChildBounds.Height / 2);
        SemanticHitOracle deepChildOracle = CaptureNativeSemanticHitOracle(block, deepChildPoint);
        Assert.Equal(2, deepChildOracle.SelectableIndex);
        Assert.Equal(2, deepChildOracle.TextIndex);
        Assert.Equal(2, deepChildOracle.LinkedIndex);
        AssertVectorHitParity(block, deepChildPoint, deepChildOracle);

        Rect hiddenBounds = block.GetSemanticBounds(4);
        var hiddenChildPoint = new Point(
            hiddenBounds.Left + hiddenBounds.Width / 2,
            hiddenBounds.Top + hiddenBounds.Height / 2);
        SemanticHitOracle parentOnlyOracle = CaptureNativeSemanticHitOracle(block, hiddenChildPoint);
        Assert.Equal(0, parentOnlyOracle.SelectableIndex);
        Assert.Equal(-1, parentOnlyOracle.TextIndex);
        Assert.Equal(-1, parentOnlyOracle.LinkedIndex);
        AssertVectorHitParity(block, hiddenChildPoint, parentOnlyOracle);

        Rect viewport = block.HorizontalViewportBounds;
        var outsideViewportPoint = new Point(viewport.Left - 1, viewport.Top + viewport.Height / 2);
        Assert.False(block.ContainsSceneViewport(outsideViewportPoint));
        SemanticHitOracle outsideViewportOracle =
            CaptureNativeSemanticHitOracle(block, outsideViewportPoint);
        Assert.Equal(-1, outsideViewportOracle.SelectableIndex);
        Assert.Equal(-1, outsideViewportOracle.TextIndex);
        Assert.Equal(-1, outsideViewportOracle.LinkedIndex);
        Assert.Null(outsideViewportOracle.LinkAction);
        AssertVectorHitParity(block, outsideViewportPoint, outsideViewportOracle);

        await snapshot.Retire().WaitAsync(TimeSpan.FromSeconds(10));

        AssertVectorHitParity(block, narrowSiblingPoint, narrowSiblingOracle);
        AssertVectorHitParity(block, deepChildPoint, deepChildOracle);
        AssertVectorHitParity(block, hiddenChildPoint, parentOnlyOracle);
        AssertVectorHitParity(block, outsideViewportPoint, outsideViewportOracle);
        AssertSceneGraphCollected(graph, snapshot, block);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void VerifyIndependentOwnerTransitions(VectorSceneBox first, VectorSceneBox second)
    {
        MarkdownVectorScene graph = first.Scene;
        Assert.Same(graph, second.Scene);
        Assert.True(first.IsNativeResourcesPrepared);
        Assert.True(second.IsNativeResourcesPrepared);

        first.ReleaseNativeResources();
        Assert.False(first.IsNativeResourcesPrepared);
        Assert.True(second.IsNativeResourcesPrepared);
        Assert.Same(graph, first.Scene);
        Assert.Same(graph, second.Scene);

        first.PrepareNativeResources(CancellationToken.None);
        Assert.True(first.IsNativeResourcesPrepared);
        Assert.Same(graph, first.Scene);

        first.Dispose();
        first.Dispose();
        first.ReleaseNativeResources();
        Assert.False(first.IsNativeResourcesPrepared);
        Assert.Throws<ObjectDisposedException>(() => { _ = first.Scene; });
        Assert.Throws<ObjectDisposedException>(() => first.PrepareNativeResources(CancellationToken.None));

        Assert.True(second.IsNativeResourcesPrepared);
        Assert.Same(graph, second.Scene);
        second.ReleaseNativeResources();
        Assert.False(second.IsNativeResourcesPrepared);
        Assert.Same(graph, second.Scene);
        second.PrepareNativeResources(CancellationToken.None);
        Assert.True(second.IsNativeResourcesPrepared);
        Assert.Same(graph, second.Scene);

        second.Dispose();
        second.Dispose();
        second.ReleaseNativeResources();
        Assert.False(second.IsNativeResourcesPrepared);
        Assert.Throws<ObjectDisposedException>(() => { _ = second.Scene; });
        Assert.Throws<ObjectDisposedException>(() => second.PrepareNativeResources(CancellationToken.None));
    }

    private static void AssertRetiredLinkedLabelMetadata(
        MarkdownSemanticDocument document,
        MarkdownSemanticNode diagram,
        MarkdownSemanticNode linkedLabel,
        VectorSceneBox block,
        MarkdownVectorSemanticItem semanticItem,
        Rect bounds)
    {
        Assert.Equal("Search index", document.GetText(linkedLabel));
        Assert.Equal("Search index", document.Text);
        Assert.Equal(bounds, linkedLabel.Bounds);
        Assert.Equal("Pipeline search index", diagram.AccessibilityName);
        Assert.Equal("A pipeline diagram.", diagram.AccessibilityDescription);
        Assert.Equal("Search index", linkedLabel.AccessibilityName);
        Assert.Equal("Indexed label.", linkedLabel.AccessibilityDescription);
        Assert.Equal("https://example.test/search", linkedLabel.HelpText);
        Assert.Equal(new SourceSpan(4, 24), block.SourceRange);
        Assert.Equal(new SourceSpan(8, 12), semanticItem.SourceSpan);
        Assert.Equal("Search index", semanticItem.Name);
        Assert.Equal(bounds, block.GetSemanticBounds(linkedLabel.VectorSemanticIndex));
        Assert.Equal(semanticItem, block.GetSemanticItem(linkedLabel.VectorSemanticIndex));
        Assert.True(block.TryGetLink(linkedLabel.VectorSemanticIndex, out MarkdownVectorLinkAction? retiredLink));
        Assert.NotNull(retiredLink);
        Assert.Equal("https://example.test/search", retiredLink.Target);
        Assert.True(retiredLink.External);
    }

    private static MarkdownTextStyleRun AssertSingleAuthoredDiagramStyleRun(
        MarkdownSemanticDocument document,
        ThemeSnapshot theme)
    {
        MarkdownTextFormatCache formatCache = MarkdownTextFormatCache.Create(document, theme);
        MarkdownTextStyleRun styleRun = Assert.Single(
            Enumerable.Range(0, formatCache.RunCount).Select(formatCache.GetRun));
        Assert.Equal(1, formatCache.RunCount);
        Assert.Equal(MarkdownElementKeys.Diagram, styleRun.ElementKey);
        Assert.Equal("Authored Diagram Face", styleRun.Style.FontFamily);
        Assert.Equal(18, styleRun.Style.FontSize, precision: 3);
        Assert.Equal(Color.FromArgb(0xFF, 0x24, 0x68, 0xAC), styleRun.Style.Foreground);
        return styleRun;
    }

    private static void AssertRebuiltStyleRunMatches(
        MarkdownSemanticDocument rebuilt,
        ThemeSnapshot theme,
        MarkdownTextStyleRun styleRun)
    {
        MarkdownTextFormatCache rebuiltFormatCache = MarkdownTextFormatCache.Create(rebuilt, theme);
        MarkdownTextStyleRun rebuiltStyleRun = Assert.Single(
            Enumerable.Range(0, rebuiltFormatCache.RunCount).Select(rebuiltFormatCache.GetRun));
        Assert.Equal(styleRun.ElementKey, rebuiltStyleRun.ElementKey);
        Assert.Equal(styleRun.Style.FontFamily, rebuiltStyleRun.Style.FontFamily);
        Assert.Equal(styleRun.Style.FontSize, rebuiltStyleRun.Style.FontSize);
        Assert.Equal(styleRun.Style.Foreground, rebuiltStyleRun.Style.Foreground);
    }

    private static void AssertInlineMetrics(
        InlineVectorSceneRun run,
        float expectedWidth,
        float expectedHeight,
        float expectedBaseline)
    {
        Assert.Equal(expectedWidth, run.DesiredWidth, precision: 4);
        Assert.Equal(expectedHeight, run.DesiredHeight, precision: 4);
        Assert.Equal(expectedBaseline, run.DesiredBaseline, precision: 4);
    }
}
