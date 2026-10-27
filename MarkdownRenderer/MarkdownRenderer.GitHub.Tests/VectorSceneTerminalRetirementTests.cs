using System.Runtime.CompilerServices;
using System.Threading;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Document;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

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
        AssertVectorHitParity(block, hitPoint, hitOracle);
        Assert.Equal(rects, document.GetDocumentRects(linkedLabel.TextStart, linkedLabel.TextEnd).ToArray());
        MarkdownTextFormatCache formatCache = MarkdownTextFormatCache.Create(document, theme);
        MarkdownTextStyleRun styleRun = Assert.Single(
            Enumerable.Range(0, formatCache.RunCount).Select(formatCache.GetRun));
        Assert.Equal(1, formatCache.RunCount);
        Assert.Equal(MarkdownElementKeys.Diagram, styleRun.ElementKey);
        Assert.Equal("Authored Diagram Face", styleRun.Style.FontFamily);
        Assert.Equal(18, styleRun.Style.FontSize, precision: 3);
        Assert.Equal(Color.FromArgb(0xFF, 0x24, 0x68, 0xAC), styleRun.Style.Foreground);

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
        MarkdownTextFormatCache rebuiltFormatCache = MarkdownTextFormatCache.Create(rebuilt, theme);
        MarkdownTextStyleRun rebuiltStyleRun = Assert.Single(
            Enumerable.Range(0, rebuiltFormatCache.RunCount).Select(rebuiltFormatCache.GetRun));
        Assert.Equal(styleRun.ElementKey, rebuiltStyleRun.ElementKey);
        Assert.Equal(styleRun.Style.FontFamily, rebuiltStyleRun.Style.FontFamily);
        Assert.Equal(styleRun.Style.FontSize, rebuiltStyleRun.Style.FontSize);
        Assert.Equal(styleRun.Style.Foreground, rebuiltStyleRun.Style.Foreground);

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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (LayoutSnapshot Snapshot, VectorSceneBox Block, InlineContainerBox Inline,
        WeakSceneGraph Graph) CreateSharedSceneSnapshot()
    {
        MarkdownLayoutContext context = CreateContext();
        MarkdownVectorScene scene = CreateScene();
        WeakSceneGraph graph = CreateWeakSceneGraph(scene);
        var contentBuilder = new MarkdownContentBuilder();
        contentBuilder.AddVectorScene(
            scene,
            new SourceSpan(0, 1),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Retirement test diagram");
        MarkdownContent content = Assert.Single(contentBuilder.Build().Items);
        var block = new VectorSceneBox(context, content, MarkdownElementKeys.Diagram)
        {
            BlockIndex = 0,
        };
        block.PrepareNativeResources(CancellationToken.None);

        var inline = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = 1,
        };
        var run = new InlineVectorSceneRun(
            context,
            scene,
            semanticText: "x",
            accessibilityName: "Inline math x",
            accessibilityDescription: "A shared scene graph.",
            elementKey: MarkdownElementKeys.Math);
        inline.Add(run);
        run.PrepareNativeResources(CancellationToken.None);

        var snapshot = new LayoutSnapshot(
            new BlockBox[] { block, inline },
            context.SourceMap,
            width: 320,
            height: 80);
        return (snapshot, block, inline, graph);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (VectorSceneBox First, VectorSceneBox Second,
        WeakSceneGraph Graph) CreateSharedPreparedBoxes()
    {
        MarkdownLayoutContext context = CreateContext();
        MarkdownVectorScene scene = CreateScene();
        WeakSceneGraph graph = CreateWeakSceneGraph(scene);
        VectorSceneBox first = CreateVectorBox(context, scene, blockIndex: 0);
        VectorSceneBox second = CreateVectorBox(context, scene, blockIndex: 1);
        first.PrepareNativeResources(CancellationToken.None);
        second.PrepareNativeResources(CancellationToken.None);
        return (first, second, graph);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (InlineVectorSceneRun Run, WeakSceneGraph Graph) CreateInlineRunForMetrics(
        float width,
        float height,
        float baseline,
        float referenceFontSize)
    {
        MarkdownLayoutContext context = CreateContext();
        MarkdownVectorPathOperation[] path =
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(1, 1)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(30, 1)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(30, 30)),
            MarkdownVectorPathOperation.Close(),
        ];
        MarkdownVectorScene scene = new(
            width,
            height,
            baseline,
            commands: [MarkdownVectorCommand.FillPath(path)],
            referenceFontSize: referenceFontSize);
        WeakSceneGraph graph = CreateWeakSceneGraph(scene);
        var run = new InlineVectorSceneRun(
            context,
            scene,
            semanticText: "x squared",
            accessibilityName: "Inline math x",
            accessibilityDescription: "A shared scene graph.",
            elementKey: MarkdownElementKeys.Math)
        {
            LinkUrl = "https://example.test/formula",
            LinkTitle = "Formula documentation",
        };
        return (run, graph);
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (LayoutSnapshot Snapshot, VectorSceneBox Block, ThemeSnapshot Theme,
        WeakSceneGraph Graph) CreateSemanticSceneSnapshot()
    {
        MarkdownLayoutContext context = CreateContext();
        context.UseProgressiveNativeSceneResidency = true;
        MarkdownVectorSemanticItem[] semantics =
        [
            new(
                1,
                MarkdownVectorSemanticRole.Diagram,
                "Pipeline search index",
                "A pipeline diagram.",
                -1,
                new SourceSpan(4, 24),
                new MarkdownVectorRectangle(0, 0, 160, 50)),
            new(
                2,
                MarkdownVectorSemanticRole.Node,
                "Search index",
                "Indexed label.",
                0,
                new SourceSpan(8, 12),
                new MarkdownVectorRectangle(10, 8, 90, 24),
                MarkdownVectorSemanticFlags.Focusable |
                MarkdownVectorSemanticFlags.Selectable |
                MarkdownVectorSemanticFlags.Linked),
        ];
        MarkdownVectorTextStyle textStyle = new("Authored Diagram Face", 18);
        MarkdownVectorPaintStyle paintStyle = new(fillArgb: 0xFF2468AC);
        MarkdownVectorPathOperation[] path =
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(1, 1)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(4, 1)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(4, 4)),
            MarkdownVectorPathOperation.Close(),
        ];
        MarkdownVectorCommand[] commands =
        [
            MarkdownVectorCommand.FillPath(path),
            MarkdownVectorCommand.DrawText(
                "Indexed label",
                new MarkdownVectorPoint(12, 25),
                textStyle,
                paintStyle,
                semanticIndex: 1),
        ];
        MarkdownVectorScene scene = new(
            width: 160,
            height: 50,
            baseline: 40,
            commands: commands,
            referenceFontSize: 16,
            viewport: new MarkdownVectorRectangle(0, 0, 160, 50),
            semantics: semantics,
            links: [new MarkdownVectorLinkAction(1, "https://example.test/search", action: null, external: true)]);
        var projectedLinks = Assert.IsAssignableFrom<IList<MarkdownVectorLinkAction?>>(scene.SemanticLinks);
        Assert.True(projectedLinks.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => projectedLinks[1] = null);
        WeakSceneGraph graph = CreateWeakSceneGraph(scene);
        var contentBuilder = new MarkdownContentBuilder();
        contentBuilder.AddVectorScene(
            scene,
            new SourceSpan(4, 24),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Pipeline search index",
            accessibilityDescription: "A pipeline diagram.");
        var block = new VectorSceneBox(
            context,
            Assert.Single(contentBuilder.Build().Items),
            MarkdownElementKeys.Diagram)
        {
            BlockIndex = 0,
        };
        block.Measure(160);
        block.Arrange(0, 0, 160);
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { block },
            context.SourceMap,
            width: 160,
            height: (float)block.Bounds.Height);
        return (snapshot, block, context.ThemeSnapshot, graph);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (VectorSceneBox Block, WeakSceneGraph Graph) CreateDeepSemanticProjectionBox()
    {
        const int semanticCount = 4096;
        MarkdownLayoutContext context = CreateContext();
        var semantics = new MarkdownVectorSemanticItem[semanticCount];
        for (int index = 0; index < semanticCount - 1; index++)
        {
            semantics[index] = new MarkdownVectorSemanticItem(
                (uint)(index + 1),
                index == 0 ? MarkdownVectorSemanticRole.Label : MarkdownVectorSemanticRole.Group,
                index == 0 ? "Deepest text" : null,
                null,
                index + 1,
                new SourceSpan(0, 1),
                new MarkdownVectorRectangle(0, 0, 64, 64));
        }
        semantics[^1] = new MarkdownVectorSemanticItem(
            (uint)semanticCount,
            MarkdownVectorSemanticRole.Diagram,
            "Reverse ordered root",
            "Root surface is inherited by the deepest text.",
            -1,
            new SourceSpan(0, 1),
            new MarkdownVectorRectangle(0, 0, 64, 64));

        MarkdownVectorPathOperation[] rootPath =
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(0, 0)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(64, 0)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(64, 64)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(0, 64)),
            MarkdownVectorPathOperation.Close(),
        ];
        MarkdownVectorPaintStyle rootSurface = MarkdownVectorPaintStyle.CreateSemantic(
                MarkdownVectorPaintRole.Authored,
                MarkdownVectorPaintRole.Authored,
                fillArgb: 0xFF123456)
            .WithHighContrastRoles(MarkdownVectorPaintRole.Surface, MarkdownVectorPaintRole.Foreground);
        MarkdownVectorScene scene = new(
            width: 64,
            height: 64,
            baseline: 48,
            commands:
            [
                MarkdownVectorCommand.DrawPath(
                    rootPath,
                    rootSurface,
                    isClosed: true,
                    semanticIndex: semanticCount - 1),
                MarkdownVectorCommand.DrawText(
                    "Deepest text",
                    new MarkdownVectorPoint(4, 32),
                    new MarkdownVectorTextStyle("Authored Deep Face", 12),
                    MarkdownVectorPaintStyle.CreateSemantic(
                        MarkdownVectorPaintRole.Foreground,
                        MarkdownVectorPaintRole.Authored),
                    semanticIndex: 0),
            ],
            referenceFontSize: 12,
            viewport: new MarkdownVectorRectangle(0, 0, 64, 64),
            semantics,
            links: Array.Empty<MarkdownVectorLinkAction>());
        WeakSceneGraph graph = CreateWeakSceneGraph(scene);
        var contentBuilder = new MarkdownContentBuilder();
        contentBuilder.AddVectorScene(
            scene,
            new SourceSpan(0, 1),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Reverse ordered root",
            accessibilityDescription: "Root surface is inherited by the deepest text.");
        var block = new VectorSceneBox(
            context,
            Assert.Single(contentBuilder.Build().Items),
            MarkdownElementKeys.Diagram)
        {
            BlockIndex = 0,
        };
        return (block, graph);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (LayoutSnapshot Snapshot, VectorSceneBox Block, WeakSceneGraph Graph)
        CreateOverlappingNestedSemanticSnapshot(FlowDirection flowDirection)
    {
        MarkdownLayoutContext context = CreateContext(flowDirection);
        MarkdownVectorSemanticFlags interactiveFlags =
            MarkdownVectorSemanticFlags.Focusable |
            MarkdownVectorSemanticFlags.Selectable |
            MarkdownVectorSemanticFlags.Linked;
        MarkdownVectorSemanticItem[] semantics =
        [
            new(
                1,
                MarkdownVectorSemanticRole.Diagram,
                "Root diagram",
                null,
                -1,
                new SourceSpan(0, 24),
                new MarkdownVectorRectangle(0, 0, 160, 50),
                MarkdownVectorSemanticFlags.Selectable),
            new(
                2,
                MarkdownVectorSemanticRole.Node,
                "Broad child",
                null,
                0,
                new SourceSpan(0, 16),
                new MarkdownVectorRectangle(10, 5, 130, 35),
                interactiveFlags),
            new(
                3,
                MarkdownVectorSemanticRole.Label,
                "Deep child",
                null,
                1,
                new SourceSpan(4, 8),
                new MarkdownVectorRectangle(60, 10, 50, 20),
                interactiveFlags),
            new(
                4,
                MarkdownVectorSemanticRole.Node,
                "Narrow sibling",
                null,
                0,
                new SourceSpan(16, 8),
                new MarkdownVectorRectangle(10, 5, 20, 10),
                interactiveFlags),
            new(
                5,
                MarkdownVectorSemanticRole.Node,
                "Decorative overlay",
                null,
                0,
                new SourceSpan(20, 4),
                new MarkdownVectorRectangle(144, 42, 14, 7),
                interactiveFlags | MarkdownVectorSemanticFlags.Decorative),
        ];
        MarkdownVectorPathOperation[] path =
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(1, 1)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(4, 1)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(4, 4)),
            MarkdownVectorPathOperation.Close(),
        ];
        MarkdownVectorScene scene = new(
            width: 160,
            height: 50,
            baseline: 40,
            commands: [MarkdownVectorCommand.FillPath(path)],
            referenceFontSize: 16,
            viewport: new MarkdownVectorRectangle(0, 0, 160, 50),
            semantics: semantics,
            links:
            [
                new MarkdownVectorLinkAction(1, "https://example.test/broad", action: null, external: true),
                new MarkdownVectorLinkAction(2, "https://example.test/deep", action: null, external: true),
                new MarkdownVectorLinkAction(3, "https://example.test/narrow", action: null, external: true),
                new MarkdownVectorLinkAction(4, "https://example.test/decorative", action: null, external: true),
            ]);
        WeakSceneGraph graph = CreateWeakSceneGraph(scene);
        var contentBuilder = new MarkdownContentBuilder();
        contentBuilder.AddVectorScene(
            scene,
            new SourceSpan(0, 24),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Nested diagram");
        var block = new VectorSceneBox(
            context,
            Assert.Single(contentBuilder.Build().Items),
            MarkdownElementKeys.Diagram)
        {
            BlockIndex = 0,
        };
        block.Measure(160);
        block.Arrange(0, 0, 160);
        var snapshot = new LayoutSnapshot(
            new BlockBox[] { block },
            context.SourceMap,
            width: 160,
            height: (float)block.Bounds.Height);
        return (snapshot, block, graph);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static SemanticHitOracle CaptureNativeSemanticHitOracle(
        VectorSceneBox block,
        Point point)
    {
        MarkdownVectorScene scene = block.Scene;
        int selectableIndex = FindNativeSemanticHit(scene, block, point, SemanticHitKind.Selectable);
        int textIndex = FindNativeSemanticHit(scene, block, point, SemanticHitKind.Text);
        int linkedIndex = FindNativeSemanticHit(scene, block, point, SemanticHitKind.Linked);
        return new SemanticHitOracle(
            selectableIndex,
            textIndex,
            linkedIndex,
            NativePositionForHit(block, point, selectableIndex),
            NativePositionForHit(block, point, textIndex),
            linkedIndex >= 0
                ? scene.GetLinkAction(linkedIndex)
                : null);
    }

    private static int FindNativeSemanticHit(
        MarkdownVectorScene scene,
        VectorSceneBox block,
        Point point,
        SemanticHitKind kind)
    {
        if (!block.ContainsSceneViewport(point))
            return -1;

        int bestIndex = -1;
        int bestDepth = -1;
        double bestArea = double.PositiveInfinity;
        for (int index = 0; index < scene.Semantics.Count; index++)
        {
            MarkdownVectorSemanticItem semantic = scene.Semantics[index];
            bool eligible = kind switch
            {
                SemanticHitKind.Selectable => MarkdownVectorSemanticPolicy.IsSelectable(semantic.Flags),
                SemanticHitKind.Linked => MarkdownVectorSemanticPolicy.IsInvokable(
                    semantic.Flags,
                    scene.GetLinkAction(index) is not null),
                SemanticHitKind.Text => MarkdownVectorSemanticPolicy.IsExposed(semantic.Flags) &&
                                        !string.IsNullOrWhiteSpace(semantic.Name) &&
                                        !(semantic.ParentIndex < 0 &&
                                          semantic.Role == MarkdownVectorSemanticRole.Diagram),
                _ => false,
            };
            if (!eligible)
                continue;

            Rect bounds = block.GetSemanticBounds(index);
            if (!bounds.Contains(point))
                continue;

            int depth = scene.GetSemanticDepth(index);
            double area = bounds.Width * bounds.Height;
            if (depth < bestDepth || (depth == bestDepth && area > bestArea))
                continue;

            bestIndex = index;
            bestDepth = depth;
            bestArea = area;
        }

        return bestIndex;
    }

    private static DocumentPosition NativePositionForHit(
        VectorSceneBox block,
        Point point,
        int semanticIndex)
    {
        if (semanticIndex < 0)
            return DocumentPosition.Zero;

        Rect bounds = block.GetSemanticBounds(semanticIndex);
        return new DocumentPosition(
            block.BlockIndex,
            semanticIndex,
            point.X <= bounds.Left + bounds.Width / 2 ? 0 : 1);
    }

    private static void AssertVectorHitParity(
        VectorSceneBox block,
        Point point,
        SemanticHitOracle expected)
    {
        bool selectableHit = block.HitTest(point, out DocumentPosition selectablePosition);
        Assert.Equal(expected.SelectableIndex >= 0, selectableHit);
        Assert.Equal(expected.SelectablePosition, selectablePosition);
        Assert.Equal(expected.SelectableIndex >= 0, block.IsSelectableAt(point));

        bool textHit = block.TryGetTextPositionAt(point, out DocumentPosition textPosition);
        Assert.Equal(expected.TextIndex >= 0, textHit);
        Assert.Equal(expected.TextPosition, textPosition);

        bool linkedHit = block.TryGetLinkAt(point, out MarkdownVectorLinkAction? link);
        Assert.Equal(expected.LinkedIndex >= 0, linkedHit);
        Assert.Same(expected.LinkAction, link);
    }

    private static VectorSceneBox CreateVectorBox(
        MarkdownLayoutContext context,
        MarkdownVectorScene scene,
        int blockIndex)
    {
        var contentBuilder = new MarkdownContentBuilder();
        contentBuilder.AddVectorScene(
            scene,
            new SourceSpan(blockIndex, 1),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Shared owner test");
        return new VectorSceneBox(
            context,
            Assert.Single(contentBuilder.Build().Items),
            MarkdownElementKeys.Diagram)
        {
            BlockIndex = blockIndex,
        };
    }

    private static MarkdownLayoutContext CreateContext(
        FlowDirection flowDirection = FlowDirection.LeftToRight)
    {
        var style = new ElementStyle
        {
            FontSize = 16,
            Foreground = Color.FromArgb(0xFF, 0x20, 0x20, 0x20),
            Background = Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA),
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.Math] = style,
            [MarkdownElementKeys.Diagram] = new ElementStyle
            {
                FontSize = 16,
                FontFamily = "Host Diagram Face",
                Foreground = Color.FromArgb(0xFF, 0x20, 0x20, 0x20),
                Background = Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA),
            },
            [MarkdownElementKeys.Link] = style,
        };
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            surfaceColor: style.Background!.Value,
            selectionHighlightColor: Color.FromArgb(0xFF, 0x00, 0x66, 0xCC),
            selectionForegroundColor: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            focusVisualColor: style.Foreground,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(new string(' ', 100)),
            new MarkdownExtensionRegistry(),
            flowDirection);
    }

    private static MarkdownVectorScene CreateScene()
    {
        MarkdownVectorPathOperation[] path =
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(1, 1)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(30, 1)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(30, 30)),
            MarkdownVectorPathOperation.Close(),
        ];
        return new MarkdownVectorScene(
            width: 40,
            height: 40,
            baseline: 40,
            commands: [MarkdownVectorCommand.FillPath(path)]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakSceneGraph CreateWeakSceneGraph(MarkdownVectorScene scene)
    {
        MarkdownVectorCommand command = scene.Commands[0];
        return new WeakSceneGraph(
            new WeakReference<MarkdownVectorScene>(scene),
            new WeakReference<IReadOnlyList<MarkdownVectorCommand>>(scene.Commands),
            new WeakReference<MarkdownVectorCommand>(command),
            new WeakReference<IReadOnlyList<MarkdownVectorPathOperation>>(command.Path));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsSceneGraphAlive(WeakSceneGraph graph) =>
        graph.Scene.TryGetTarget(out _) ||
        graph.Commands.TryGetTarget(out _) ||
        graph.FirstCommand.TryGetTarget(out _) ||
        graph.FirstPath.TryGetTarget(out _);

    private enum SemanticHitKind
    {
        Selectable,
        Linked,
        Text,
    }

    private readonly record struct SemanticHitOracle(
        int SelectableIndex,
        int TextIndex,
        int LinkedIndex,
        DocumentPosition SelectablePosition,
        DocumentPosition TextPosition,
        MarkdownVectorLinkAction? LinkAction);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AssertSceneGraphCollected(
        WeakSceneGraph graph,
        params object[] owners)
    {
        for (int attempt = 0; attempt < 8; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!IsSceneGraphAlive(graph))
            {
                KeepOwnersAlive(owners);
                return;
            }
            Thread.Sleep(10);
        }

        KeepOwnersAlive(owners);
        Assert.False(
            IsSceneGraphAlive(graph),
            "A terminally retired owner retained the scene, command collection, command, or path graph.");
    }

    private sealed class WeakSceneGraph(
        WeakReference<MarkdownVectorScene> scene,
        WeakReference<IReadOnlyList<MarkdownVectorCommand>> commands,
        WeakReference<MarkdownVectorCommand> firstCommand,
        WeakReference<IReadOnlyList<MarkdownVectorPathOperation>> firstPath)
    {
        internal WeakReference<MarkdownVectorScene> Scene { get; } = scene;
        internal WeakReference<IReadOnlyList<MarkdownVectorCommand>> Commands { get; } = commands;
        internal WeakReference<MarkdownVectorCommand> FirstCommand { get; } = firstCommand;
        internal WeakReference<IReadOnlyList<MarkdownVectorPathOperation>> FirstPath { get; } = firstPath;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void KeepOwnersAlive(object[] owners)
    {
        foreach (object owner in owners)
            GC.KeepAlive(owner);
    }
}
