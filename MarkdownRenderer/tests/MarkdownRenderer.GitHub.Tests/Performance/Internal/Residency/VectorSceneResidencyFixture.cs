using System.Threading;
using System.Linq;
using MarkdownRenderer.Math;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming.Internal;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Performance.Internal;
using MarkdownRenderer.Performance.Internal.ProgressiveScenes;
using MarkdownRenderer.Performance.Internal.Residency;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Performance.Internal.Residency;

/// <summary>
/// Contexts, vector scenes, inline and block owners and lazy snapshots for
/// the vector scene residency tests, plus the paint path's deferred scene
/// probe they drive.
/// </summary>
internal static class VectorSceneResidencyFixture
{
    internal static LayoutSnapshot CreateLazySnapshot(
        MarkdownPerformanceSession performanceSession,
        out VectorSceneBox first,
        out VectorSceneBox second)
    {
        MarkdownLayoutContext context = CreateContext(performanceSession);
        first = CreateVectorBox(context, blockIndex: 0);
        var gap = new StackBox { BlockIndex = 1 };
        for (int index = 0; index < 30; index++)
            gap.Add(new FixedHeightBox { BlockIndex = index + 2 });
        second = CreateVectorBox(context, blockIndex: 32);

        BlockBox[] blocks = [first, gap, second];
        IMarkdownVectorSceneResidencyPolicy policy =
            ((IMarkdownPerformanceSessionInternal)performanceSession)
                .CreateVectorSceneResidencyPolicy(blocks)!;
        var snapshot = new LayoutSnapshot(
            blocks,
            context.SourceMap,
            width: 320,
            height: 0,
            blockSpacing: 12,
            vectorSceneResidencyPolicy: policy);
        snapshot.EnableLazyLayout(
            availableWidth: 320,
            viewportTop: 0,
            viewportHeight: 40,
            overscan: 0,
            CancellationToken.None);
        return snapshot;
    }

    internal static VectorSceneBox CreateVectorBox(
        MarkdownLayoutContext context,
        int blockIndex,
        int? pathOperationCount = null)
    {
        var contentBuilder = new MarkdownContentBuilder();
        contentBuilder.AddVectorScene(
            CreateScene(pathOperationCount ?? (blockIndex == 32 ? 40 : 4)),
            new SourceSpan(0, 1),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Scene residency test");
        return new VectorSceneBox(
            context,
            Assert.Single(contentBuilder.Build().Items),
            MarkdownElementKeys.Diagram)
        {
            BlockIndex = blockIndex,
        };
    }

    internal static LayoutSnapshot CreateLazyInlineSnapshot(
        MarkdownPerformanceSession performanceSession,
        out InlineContainerBox first,
        out InlineContainerBox second)
    {
        MarkdownLayoutContext context = CreateContext(performanceSession);
        first = CreateInlineBox(context, blockIndex: 1, pathOperationCount: 4);
        var gap = new StackBox { BlockIndex = 1 };
        for (int index = 0; index < 30; index++)
            gap.Add(new FixedHeightBox { BlockIndex = index + 2 });
        second = CreateInlineBox(context, blockIndex: 32, pathOperationCount: 8);

        BlockBox[] blocks = [first, gap, second];
        return CreateLazySnapshot(context, performanceSession, blocks);
    }

    internal static LayoutSnapshot CreateNestedInlineSnapshot(
        MarkdownPerformanceSession performanceSession,
        out InlineContainerBox first,
        out InlineVectorSceneRun firstRun,
        out ListItemBox list,
        out InlineVectorSceneRun markerRun,
        out InlineVectorSceneRun listContentRun,
        out TableBox table,
        out InlineVectorSceneRun tableRun)
    {
        MarkdownLayoutContext context = CreateContext(performanceSession);
        first = CreateInlineBox(context, blockIndex: 1, pathOperationCount: 4);
        firstRun = Assert.IsType<InlineVectorSceneRun>(Assert.Single(first.Runs));

        var gap = new StackBox { BlockIndex = 1 };
        for (int index = 0; index < 30; index++)
            gap.Add(new FixedHeightBox { BlockIndex = index + 2 });

        InlineContainerBox marker = CreateInlineBox(context, blockIndex: 32, pathOperationCount: 5);
        markerRun = Assert.IsType<InlineVectorSceneRun>(Assert.Single(marker.Runs));
        InlineContainerBox listParagraph = CreateInlineBox(context, blockIndex: 33, pathOperationCount: 6);
        listContentRun = Assert.IsType<InlineVectorSceneRun>(Assert.Single(listParagraph.Runs));
        var listContent = new StackBox { BlockIndex = 33 };
        listContent.Add(listParagraph);
        list = new ListItemBox(marker, listContent, markerWidth: 28)
        {
            BlockIndex = 32,
        };

        InlineContainerBox tableCell = CreateInlineBox(context, blockIndex: 34, pathOperationCount: 7);
        tableRun = Assert.IsType<InlineVectorSceneRun>(Assert.Single(tableCell.Runs));
        table = new TableBox(
            context,
            headerCells: new[] { new[] { tableCell } },
            bodyCells: Array.Empty<InlineContainerBox[]>())
        {
            BlockIndex = 34,
        };

        BlockBox[] blocks = [first, gap, list, table];
        return CreateLazySnapshot(context, performanceSession, blocks);
    }

    internal static LayoutSnapshot CreateLazySnapshot(
        MarkdownLayoutContext context,
        MarkdownPerformanceSession performanceSession,
        IReadOnlyList<BlockBox> blocks,
        double initialViewportHeight = 40)
    {
        IMarkdownVectorSceneResidencyPolicy policy =
            ((IMarkdownPerformanceSessionInternal)performanceSession)
                .CreateVectorSceneResidencyPolicy(blocks)!;
        var snapshot = new LayoutSnapshot(
            blocks,
            context.SourceMap,
            width: 320,
            height: 0,
            blockSpacing: 12,
            vectorSceneResidencyPolicy: policy);
        snapshot.EnableLazyLayout(
            availableWidth: 320,
            viewportTop: 0,
            viewportHeight: initialViewportHeight,
            overscan: 0,
            CancellationToken.None);
        return snapshot;
    }

    internal static InlineContainerBox CreateInlineBox(
        MarkdownLayoutContext context,
        int blockIndex,
        int pathOperationCount)
    {
        var box = new InlineContainerBox(context, MarkdownElementKeys.Body)
        {
            BlockIndex = blockIndex,
        };
        box.Add(CreateInlineRun(context, pathOperationCount, deferredSceneMarker: $"inline-scene:{blockIndex}"));
        return box;
    }

    internal static InlineVectorSceneRun CreateInlineRun(
        MarkdownLayoutContext context,
        int pathOperationCount,
        string? deferredSceneMarker = null)
        => new(
            context,
            CreateScene(pathOperationCount),
            semanticText: "x",
            accessibilityName: "Inline math x",
            accessibilityDescription: "An inline mathematical expression.",
            elementKey: MarkdownElementKeys.Math)
        {
            DeferredSceneMarker = deferredSceneMarker,
        };

    internal static MarkdownVectorScene CreateScene(int pathOperationCount)
    {
        var path = new List<MarkdownVectorPathOperation>(pathOperationCount + 2)
        {
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(0, 0)),
        };
        for (int index = 0; index < pathOperationCount; index++)
            path.Add(MarkdownVectorPathOperation.LineTo(
                new MarkdownVectorPoint(index % 40, index % 40)));
        path.Add(MarkdownVectorPathOperation.Close());
        return new MarkdownVectorScene(
            width: 40,
            height: 40,
            baseline: 40,
            commands: new[] { MarkdownVectorCommand.FillPath(path) });
    }

    internal static MarkdownLayoutContext CreateContext(
        MarkdownPerformanceSession? performanceSession,
        FlowDirection flowDirection = FlowDirection.LeftToRight,
        double rasterizationScale = 1,
        ICanvasResourceCreator? resourceCreator = null)
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
            [MarkdownElementKeys.Diagram] = style,
            [MarkdownElementKeys.Link] = style,
        };
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            surfaceColor: style.Background.Value,
            selectionHighlightColor: Color.FromArgb(0xFF, 0x00, 0x66, 0xCC),
            selectionForegroundColor: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            focusVisualColor: style.Foreground,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        var context = new MarkdownLayoutContext(
            resourceCreator ?? CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(new string(' ', 100)),
            new MarkdownExtensionRegistry(),
            flowDirection)
        {
            PerformanceSession = performanceSession is null
                ? null
                : (IMarkdownPerformanceSessionInternal)performanceSession,
            RasterizationScale = rasterizationScale,
        };
        if (performanceSession is not null)
        {
            context.UseProgressiveNativeSceneResidency =
                !performanceSession.IsDisposed && performanceSession.Options.DeferOffscreenScenes;
        }
        return context;
    }

    internal static MarkdownLayoutContext CreateBuildContext(
        MarkdownRenderer.Document.MarkdownDocument document,
        MarkdownPerformanceSession performanceSession)
    {
        var style = new ElementStyle
        {
            FontSize = 16,
            Foreground = Color.FromArgb(0xFF, 0x20, 0x20, 0x20),
            Background = Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA),
        };
        var theme = new ThemeSnapshot(
            new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = style,
                [MarkdownElementKeys.Math] = style,
                [MarkdownElementKeys.Link] = style,
            },
            new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal),
            surfaceColor: style.Background.Value,
            selectionHighlightColor: Color.FromArgb(0xFF, 0x00, 0x66, 0xCC),
            selectionForegroundColor: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            focusVisualColor: style.Foreground,
            isDark: false,
            isHighContrast: false,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(document.Source),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight)
        {
            PerformanceSession = (IMarkdownPerformanceSessionInternal)performanceSession,
        };
    }

    internal static InlineVectorSceneRun[] GetTopLevelInlineSceneRuns(LayoutSnapshot snapshot)
        => snapshot.Blocks
            .OfType<InlineContainerBox>()
            .SelectMany(static block => block.Runs.OfType<InlineVectorSceneRun>())
            .ToArray();

    /// <summary>
    /// Runs the paint path's deferred scene probe for one vertical band and
    /// optional viewport, and returns whether the visible scene resources are ready.
    /// </summary>
    internal static bool CollectDeferredSceneMarkers(
        LayoutSnapshot snapshot,
        double top,
        double bottom,
        Rect? viewport,
        out IReadOnlyList<string> markerKeys)
    {
        Assert.True(snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
            top,
            bottom,
            out IReadOnlyList<BlockBox> blocks,
            out IReadOnlyList<int> ordinals,
            out LayoutPlacementGeneration placements,
            out IReadOnlyDictionary<int, int> ordinalByBlockIndex));
        Assert.True(MarkdownProgressiveScenePlan.TryCollectVisibleDeferredSceneMarkers(
            blocks,
            ordinals,
            placements,
            snapshot.PlacementTransform,
            ordinalByBlockIndex,
            top,
            bottom,
            viewport,
            snapshot.VectorSceneResidencyPolicy,
            out markerKeys,
            out bool resourcesReady));
        return resourcesReady;
    }

    internal sealed class FixedHeightBox : BlockBox
    {
        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(Bounds.X, Bounds.Y, availableWidth, 50);
            return 50;
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }

    internal sealed class FixedThirtyTwoHeightBox : BlockBox
    {
        public override float Measure(float availableWidth)
        {
            Bounds = new Rect(Bounds.X, Bounds.Y, availableWidth, 32);
            return 32;
        }

        public override void Paint(CanvasDrawingSession ds, Rect viewport) { }
    }
}
