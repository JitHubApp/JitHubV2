using System.Runtime.CompilerServices;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Parsing.Internal;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Windows.UI;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.VectorSceneGraphLiveness;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Contexts, scenes, boxes and snapshots for the vector scene terminal
/// retirement tests. Each builder that creates a scene graph is not inlined,
/// so no caller frame keeps that graph alive after it returns its owners and
/// a weak view of the graph.
/// </summary>
internal static class VectorTerminalRetirementScenes
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static (LayoutSnapshot Snapshot, VectorSceneBox Block, InlineContainerBox Inline,
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
    internal static (VectorSceneBox First, VectorSceneBox Second,
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
    internal static (InlineVectorSceneRun Run, WeakSceneGraph Graph) CreateInlineRunForMetrics(
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static (LayoutSnapshot Snapshot, VectorSceneBox Block, ThemeSnapshot Theme,
        WeakSceneGraph Graph) CreateSemanticSceneSnapshot()
    {
        MarkdownLayoutContext context = CreateContext();
        context.UseProgressiveNativeSceneResidency = true;
        MarkdownVectorScene scene = CreateLinkedLabelScene();
        var projectedLinks = Assert.IsAssignableFrom<IList<MarkdownVectorLinkAction?>>(scene.SemanticLinks);
        Assert.True(projectedLinks.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => projectedLinks[1] = null);
        WeakSceneGraph graph = CreateWeakSceneGraph(scene);
        VectorSceneBox block = CreateDiagramBlock(
            context,
            scene,
            new SourceSpan(4, 24),
            accessibilityName: "Pipeline search index",
            accessibilityDescription: "A pipeline diagram.");
        LayoutSnapshot snapshot = MeasureIntoSnapshot(context, block);
        return (snapshot, block, context.ThemeSnapshot, graph);
    }

    /// <summary>
    /// A 160 by 50 pipeline diagram whose one linked, focusable label is drawn
    /// as authored text with its own face, size and fill.
    /// </summary>
    private static MarkdownVectorScene CreateLinkedLabelScene()
    {
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
        return scene;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static (VectorSceneBox Block, WeakSceneGraph Graph) CreateDeepSemanticProjectionBox()
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
    internal static (LayoutSnapshot Snapshot, VectorSceneBox Block, WeakSceneGraph Graph)
        CreateOverlappingNestedSemanticSnapshot(FlowDirection flowDirection)
    {
        MarkdownLayoutContext context = CreateContext(flowDirection);
        MarkdownVectorScene scene = CreateOverlappingNestedScene();
        WeakSceneGraph graph = CreateWeakSceneGraph(scene);
        VectorSceneBox block = CreateDiagramBlock(
            context,
            scene,
            new SourceSpan(0, 24),
            accessibilityName: "Nested diagram",
            accessibilityDescription: null);
        LayoutSnapshot snapshot = MeasureIntoSnapshot(context, block);
        return (snapshot, block, graph);
    }

    /// <summary>
    /// A 160 by 50 diagram whose linked children overlap: a broad child holding
    /// a deeper child, a narrow sibling over the broad child's corner, and a
    /// decorative overlay that must never win a hit.
    /// </summary>
    private static MarkdownVectorScene CreateOverlappingNestedScene()
    {
        MarkdownVectorSemanticItem[] semantics = CreateOverlappingNestedSemantics();
        MarkdownVectorPathOperation[] path =
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(1, 1)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(4, 1)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(4, 4)),
            MarkdownVectorPathOperation.Close(),
        ];
        return new MarkdownVectorScene(
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
    }

    private static MarkdownVectorSemanticItem[] CreateOverlappingNestedSemantics()
    {
        MarkdownVectorSemanticFlags interactiveFlags =
            MarkdownVectorSemanticFlags.Focusable |
            MarkdownVectorSemanticFlags.Selectable |
            MarkdownVectorSemanticFlags.Linked;
        return
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
    }

    private static VectorSceneBox CreateDiagramBlock(
        MarkdownLayoutContext context,
        MarkdownVectorScene scene,
        SourceSpan sourceSpan,
        string accessibilityName,
        string? accessibilityDescription)
    {
        var contentBuilder = new MarkdownContentBuilder();
        contentBuilder.AddVectorScene(
            scene,
            sourceSpan,
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: accessibilityName,
            accessibilityDescription: accessibilityDescription);
        return new VectorSceneBox(
            context,
            Assert.Single(contentBuilder.Build().Items),
            MarkdownElementKeys.Diagram)
        {
            BlockIndex = 0,
        };
    }

    private static LayoutSnapshot MeasureIntoSnapshot(MarkdownLayoutContext context, VectorSceneBox block)
    {
        block.Measure(160);
        block.Arrange(0, 0, 160);
        return new LayoutSnapshot(
            new BlockBox[] { block },
            context.SourceMap,
            width: 160,
            height: (float)block.Bounds.Height);
    }

    internal static VectorSceneBox CreateVectorBox(
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

    internal static MarkdownLayoutContext CreateContext(
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

    internal static MarkdownVectorScene CreateScene()
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
}
