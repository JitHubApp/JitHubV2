using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI.Xaml;
using System.Globalization;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class VectorAccessibilityContractTests
{
    [Theory]
    [InlineData(16f, 1f)]
    [InlineData(24f, 1.5f)]
    [InlineData(32f, 2f)]
    public void BlockVectorScaleTracksResolvedFontAtCommonTextScales(
        float resolvedFontSize,
        float expectedScale)
    {
        var scene = new MarkdownVectorScene(
            width: 200,
            height: 60,
            baseline: 48,
            commands: Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 16);

        Assert.Equal(
            expectedScale,
            VectorSceneBox.ResolveContentScale(scene, resolvedFontSize, resolvedFontSize / 16f),
            precision: 4);
    }

    [Fact]
    public void AtomicVectorSelectionEndpointUsesFullBoundsButOrdinaryHitTestStaysSemantic()
    {
        MarkdownVectorScene scene = CreateSemanticScene(includeLink: false);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = 2,
        };
        vector.Measure(240);
        vector.Arrange(0, 40, 240);
        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 240,
            height: (float)vector.Bounds.Bottom);
        var upperBackground = new Point(vector.Bounds.Left + 2, vector.Bounds.Top + 2);
        var lowerBackground = new Point(vector.Bounds.Left + 2, vector.Bounds.Bottom - 2);

        Assert.False(vector.HitTest(upperBackground, out _));
        Assert.True(snapshot.HitTestSelectionEndpoint(upperBackground, out DocumentPosition before));
        Assert.Equal(new DocumentPosition(2, 0, 0), before);

        Assert.True(snapshot.HitTestSelectionEndpoint(lowerBackground, out DocumentPosition after));
        Assert.Equal(new DocumentPosition(2, 0, 1), after);
        Assert.False(snapshot.HitTestSelectionEndpoint(
            new Point(vector.Bounds.Right + 1, vector.Bounds.Bottom + 1),
            out _));

        var selectionFromPreviousBlock = new DocumentPosition(1, 0, 0);
        Assert.Empty(vector.GetSelectionRects(new DocumentRange(selectionFromPreviousBlock, before)));
        Assert.Single(vector.GetSelectionRects(new DocumentRange(selectionFromPreviousBlock, after)));
        Assert.Empty(vector.GetSelectionRects(new DocumentRange(
            after,
            new DocumentPosition(3, 0, 0))));
        Assert.Single(vector.GetSelectionRects(new DocumentRange(
            new DocumentPosition(2, 1, 0),
            new DocumentPosition(2, 1, 1))));
        Assert.Single(vector.GetSelectionRects(new DocumentRange(
            new DocumentPosition(2, 1, 0),
            new DocumentPosition(3, 0, 0))));

        const string source = "before\n```mermaid\ngraph TD\n```";
        var sourceMap = new MarkdownSourceMap(source);
        sourceMap.Add(1, 0, 6, new SourceSpan(0, 6));
        sourceMap.Add(2, 0, 1, new SourceSpan(7, source.Length - 7));
        Assert.Equal(
            source,
            sourceMap.Slice(new DocumentRange(selectionFromPreviousBlock, after)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedVectorSceneRepaintsAboveOpaqueSelectionFill(bool highContrast)
    {
        var scene = new MarkdownVectorScene(
            width: 20,
            height: 20,
            baseline: 16,
            commands:
            [
                MarkdownVectorCommand.FillRectangle(
                    new MarkdownVectorRectangle(0, 0, 20, 20),
                    0xFFFF0000),
            ],
            referenceFontSize: 16);
        MarkdownLayoutContext context = CreateContext(isHighContrast: highContrast);
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = 1,
        };
        vector.Measure(100);
        vector.Arrange(0, 0, 100);

        try
        {
            int targetHeight = System.Math.Max(1, (int)System.Math.Ceiling(vector.Bounds.Height));
            using var target = new CanvasRenderTarget(
                context.ResourceCreator,
                100,
                targetHeight,
                96);
            Color selectionBackground = Color.FromArgb(0xFF, 0x00, 0x66, 0xCC);
            using (CanvasDrawingSession drawingSession = target.CreateDrawingSession())
            {
                drawingSession.Clear(Color.FromArgb(0, 0, 0, 0));
                drawingSession.FillRectangle(vector.Bounds, selectionBackground);
                vector.PaintSelectionForeground(
                    drawingSession,
                    new DocumentRange(
                        new DocumentPosition(1, 0, 0),
                        new DocumentPosition(1, 0, 1)),
                    context.ThemeSnapshot.SelectionForegroundColor,
                    new Rect(0, 0, 100, targetHeight));
            }

            int sampleX = System.Math.Clamp((int)vector.ContentBounds.Left + 10, 0, 99);
            int sampleY = System.Math.Clamp((int)vector.ContentBounds.Top + 10, 0, targetHeight - 1);
            Color pixel = target.GetPixelColors()[sampleY * 100 + sampleX];
            if (highContrast)
                Assert.Equal(context.ThemeSnapshot.SelectionForegroundColor, pixel);
            else
                Assert.NotEqual(selectionBackground, pixel);
        }
        finally
        {
            vector.Dispose();
        }
    }

    [Fact]
    public void BlockVectorScaleInfersRichTextReferenceAndFallsBackToTextScale()
    {
        var textScene = new MarkdownVectorScene(
            width: 200,
            height: 60,
            baseline: 48,
            commands:
            [
                MarkdownVectorCommand.DrawText(
                    "Diagram label",
                    new MarkdownVectorPoint(10, 32),
                    new MarkdownVectorTextStyle("Segoe UI", 20),
                    new MarkdownVectorPaintStyle(fillArgb: 0xFF111111)),
            ]);
        var fixedScene = new MarkdownVectorScene(
            width: 200,
            height: 60,
            baseline: 48,
            commands: Array.Empty<MarkdownVectorCommand>());

        Assert.Equal(1.5f, VectorSceneBox.ResolveContentScale(textScene, 30, 2), precision: 4);
        Assert.Equal(2f, VectorSceneBox.ResolveContentScale(fixedScene, 30, 2), precision: 4);
    }

    [Fact]
    public void HostFontRoleAppliesDiagramFontFamilyWithoutChangingAuthoredText()
    {
        var hostText = new MarkdownVectorTextStyle(
            MarkdownVectorFontRole.Host,
            "Scene Fallback",
            16);
        var authoredText = new MarkdownVectorTextStyle("Authored Face", 16);

        Assert.Equal(
            "Host Diagram Face",
            VectorSceneDrawing.ResolveFontFamily(hostText, " Host Diagram Face "));
        Assert.Equal(
            "Scene Fallback",
            VectorSceneDrawing.ResolveFontFamily(hostText, null));
        Assert.Equal(
            "Authored Face",
            VectorSceneDrawing.ResolveFontFamily(authoredText, "Host Diagram Face"));
    }

    [Fact]
    public void SemanticPaletteKeepsSurfaceForegroundAndLinkDistinctInHighContrast()
    {
        var palette = new VectorScenePaintPalette(
            Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            Color.FromArgb(0xFF, 0x00, 0x00, 0x00),
            Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF));

        Color foreground = VectorSceneDrawing.ResolveColorForTesting(
            0x66333333,
            opacity: 0.25f,
            palette,
            MarkdownVectorPaintRole.Foreground,
            highContrast: true);
        Color surface = VectorSceneDrawing.ResolveColorForTesting(
            0x66EEEEEE,
            opacity: 0.25f,
            palette,
            MarkdownVectorPaintRole.Surface,
            highContrast: true);
        Color link = VectorSceneDrawing.ResolveColorForTesting(
            0x660000FF,
            opacity: 0.25f,
            palette,
            MarkdownVectorPaintRole.Link,
            highContrast: true);

        Assert.Equal(palette.Foreground, foreground);
        Assert.Equal(palette.Surface, surface);
        Assert.Equal(palette.Link, link);
        Assert.Equal(0xFF, foreground.A);
        Assert.Equal(0xFF, surface.A);
        Assert.Equal(0xFF, link.A);
    }

    [Theory]
    [InlineData(false, 0xFF0055CCu, 0xFF202020u)]
    [InlineData(true, 0xFF00FFFFu, 0xFFFFFFFFu)]
    public void VectorSemanticTextStyleUsesTheSameLinkedPaintRoleAsDrawing(
        bool highContrast,
        uint expectedLinkedArgb,
        uint expectedPlainArgb)
    {
        MarkdownVectorPaintStyle sharedPaint = MarkdownVectorPaintStyle.CreateSemantic(
            MarkdownVectorPaintRole.Foreground,
            MarkdownVectorPaintRole.Authored);
        var hostText = new MarkdownVectorTextStyle(
            MarkdownVectorFontRole.Host,
            "Scene fallback",
            18,
            fontWeight: 650,
            italic: true);
        MarkdownVectorSemanticItem[] semantics =
        [
            new(
                1,
                MarkdownVectorSemanticRole.Diagram,
                "Text style diagram",
                null,
                -1,
                new SourceSpan(0, 10),
                new MarkdownVectorRectangle(0, 0, 160, 50)),
            new(
                2,
                MarkdownVectorSemanticRole.Node,
                "Linked label",
                null,
                0,
                new SourceSpan(0, 5),
                new MarkdownVectorRectangle(5, 5, 70, 30),
                MarkdownVectorSemanticFlags.Linked),
            new(
                3,
                MarkdownVectorSemanticRole.Node,
                "Plain label",
                null,
                0,
                new SourceSpan(6, 4),
                new MarkdownVectorRectangle(85, 5, 70, 30)),
        ];
        MarkdownVectorCommand[] commands =
        [
            MarkdownVectorCommand.DrawText(
                "Linked label",
                new MarkdownVectorPoint(10, 25),
                hostText,
                sharedPaint,
                semanticIndex: 1),
            MarkdownVectorCommand.DrawText(
                "Plain label",
                new MarkdownVectorPoint(90, 25),
                new MarkdownVectorTextStyle("Authored face", 14, fontWeight: 350),
                sharedPaint,
                semanticIndex: 2),
        ];
        var scene = new MarkdownVectorScene(
            160,
            50,
            40,
            commands,
            referenceFontSize: 16,
            new MarkdownVectorRectangle(0, 0, 160, 50),
            semantics,
            [new MarkdownVectorLinkAction(1, "https://example.test/linked", action: null)]);
        MarkdownLayoutContext context = CreateContext(isHighContrast: highContrast);
        var vector = new VectorSceneBox(
            context,
            CreateContent(scene),
            MarkdownElementKeys.Diagram);
        try
        {
            ElementStyle linked = vector.ResolveSemanticTextStyle(1);
            ElementStyle plain = vector.ResolveSemanticTextStyle(2);

            Assert.Equal(ToColor(expectedLinkedArgb), linked.Foreground);
            Assert.Equal(ToColor(expectedPlainArgb), plain.Foreground);
            Assert.Equal("Host Diagram Face", linked.FontFamily);
            Assert.Equal(18, linked.FontSize, precision: 3);
            Assert.Equal((ushort)650, linked.FontWeight.Weight);
            Assert.Equal(Windows.UI.Text.FontStyle.Italic, linked.FontStyle);
            Assert.Equal("Authored face", plain.FontFamily);
            Assert.Equal(14, plain.FontSize, precision: 3);
            Assert.Equal((ushort)350, plain.FontWeight.Weight);

            // Projection is view-time only: the shared immutable scene command and
            // its declared role remain unchanged after both semantic lookups.
            Assert.Same(sharedPaint, commands[0].Style);
            Assert.Same(sharedPaint, commands[1].Style);
            Assert.Equal(MarkdownVectorPaintRole.Foreground, sharedPaint.FillRole);
        }
        finally
        {
            vector.Dispose();
        }
    }

    [Theory]
    [InlineData(false, CanvasStrokeTransformBehavior.Normal)]
    [InlineData(true, CanvasStrokeTransformBehavior.Fixed)]
    public void NonScalingStrokeMapsToWin2DFixedTransformBehavior(
        bool nonScalingStroke,
        CanvasStrokeTransformBehavior expected)
    {
        var style = new MarkdownVectorPaintStyle(
            strokeArgb: 0xFF202020,
            strokeWidth: 2,
            nonScalingStroke: nonScalingStroke);

        using CanvasStrokeStyle strokeStyle = VectorSceneDrawing.CreateStrokeStyle(style);

        Assert.Equal(nonScalingStroke, VectorSceneDrawing.RequiresStrokeStyle(style));
        Assert.Equal(expected, strokeStyle.TransformBehavior);
    }

    [Fact]
    public void SemanticPaletteAdaptsContainersAtViewTimeButPreservesFillOnlyAccents()
    {
        var light = new VectorScenePaintPalette(
            Color.FromArgb(0xFF, 0x20, 0x20, 0x20),
            Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA),
            Color.FromArgb(0xFF, 0x00, 0x55, 0xCC));
        var dark = new VectorScenePaintPalette(
            Color.FromArgb(0xFF, 0xEE, 0xEE, 0xEE),
            Color.FromArgb(0xFF, 0x18, 0x18, 0x18),
            Color.FromArgb(0xFF, 0x66, 0xAA, 0xFF));
        var authoredRectangle = MarkdownVectorCommand.DrawRectangle(
            new MarkdownVectorRectangle(0, 0, 20, 20),
            new MarkdownVectorPaintStyle(fillArgb: 0xFFFFFFFF));
        var semanticRectangle = MarkdownVectorCommand.DrawRectangle(
            new MarkdownVectorRectangle(0, 0, 20, 20),
            MarkdownVectorPaintStyle.CreateSemantic(
                MarkdownVectorPaintRole.Surface,
                MarkdownVectorPaintRole.Authored,
                fillArgb: 0xFFFFFFFF));
        var accentPath = MarkdownVectorCommand.DrawPath(
            [
                MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(0, 0)),
                MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(10, 5)),
                MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(0, 10)),
                MarkdownVectorPathOperation.Close(),
            ],
            new MarkdownVectorPaintStyle(fillArgb: 0xFFFF5500),
            isClosed: true);

        Assert.Equal(
            MarkdownVectorPaintRole.Surface,
            VectorSceneDrawing.InferFillRole(authoredRectangle, authoredRectangle.Style!));
        Assert.Equal(
            MarkdownVectorPaintRole.Foreground,
            VectorSceneDrawing.InferFillRole(accentPath, accentPath.Style!));
        Assert.Equal(
            MarkdownVectorPaintRole.Surface,
            VectorSceneDrawing.InferFillRole(
                accentPath,
                accentPath.Style!,
                MarkdownVectorSemanticRole.Node));

        Color lightSurface = VectorSceneDrawing.ResolveColorForTesting(
            semanticRectangle.Style!.FillArgb,
            semanticRectangle.Style.Opacity,
            light,
            semanticRectangle.Style.FillRole,
            highContrast: false);
        Color darkSurface = VectorSceneDrawing.ResolveColorForTesting(
            semanticRectangle.Style.FillArgb,
            semanticRectangle.Style.Opacity,
            dark,
            semanticRectangle.Style.FillRole,
            highContrast: false);
        Color authoredLight = VectorSceneDrawing.ResolveColorForTesting(
            authoredRectangle.Style!.FillArgb,
            authoredRectangle.Style.Opacity,
            light,
            authoredRectangle.Style.FillRole,
            highContrast: false);
        Color authoredDark = VectorSceneDrawing.ResolveColorForTesting(
            authoredRectangle.Style.FillArgb,
            authoredRectangle.Style.Opacity,
            dark,
            authoredRectangle.Style.FillRole,
            highContrast: false);
        Color lightAccent = VectorSceneDrawing.ResolveColorForTesting(
            accentPath.Style!.FillArgb,
            accentPath.Style.Opacity,
            light,
            accentPath.Style.FillRole,
            highContrast: false);
        Color darkAccent = VectorSceneDrawing.ResolveColorForTesting(
            accentPath.Style.FillArgb,
            accentPath.Style.Opacity,
            dark,
            accentPath.Style.FillRole,
            highContrast: false);

        Assert.Equal(light.Surface, lightSurface);
        Assert.Equal(dark.Surface, darkSurface);
        Assert.NotEqual(lightSurface, darkSurface);
        Assert.Equal(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), authoredLight);
        Assert.Equal(authoredLight, authoredDark);
        Assert.Equal(lightAccent, darkAccent);
        Assert.Equal(Color.FromArgb(0xFF, 0xFF, 0x55, 0x00), lightAccent);
    }

    [Fact]
    public void VectorChildrenOwnDistinctTextRangesBoundsAndKeyboardLinkEntries()
    {
        MarkdownVectorScene scene = CreateSemanticScene(includeLink: true);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        vector.Measure(100);
        vector.Arrange(10, 20, 100);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 100,
            height: (float)vector.Bounds.Height);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode diagram = Assert.Single(document.Root.Children);
        MarkdownSemanticNode alpha = Assert.Single(
            diagram.Children,
            static child => child.AccessibilityName == "Alpha");
        MarkdownSemanticNode beta = Assert.Single(
            diagram.Children,
            static child => child.AccessibilityName == "Beta");

        Assert.Equal("Alpha", document.GetText(alpha));
        Assert.Equal("Beta", document.GetText(beta));
        Assert.NotEqual((alpha.TextStart, alpha.TextEnd), (beta.TextStart, beta.TextEnd));
        Assert.Equal(alpha.TextStart, document.TextOffsetFromDocumentPosition(
            new DocumentPosition(vector.BlockIndex, 1, 0)));
        Assert.Equal(alpha.TextEnd, document.TextOffsetFromDocumentPosition(
            new DocumentPosition(vector.BlockIndex, 1, 1)));

        MarkdownTextSpan alphaSpan = Assert.Single(
            document.TextSpans,
            static span => span.VectorSemanticIndex == 1);
        MarkdownTextSpan betaSpan = Assert.Single(
            document.TextSpans,
            static span => span.VectorSemanticIndex == 2);
        Assert.Equal((alpha.TextStart, alpha.TextEnd), (alphaSpan.TextStart, alphaSpan.TextEnd));
        Assert.Equal((beta.TextStart, beta.TextEnd), (betaSpan.TextStart, betaSpan.TextEnd));

        Rect alphaBounds = Assert.Single(document.GetDocumentRects(alpha.TextStart, alpha.TextEnd));
        Assert.Equal(new Rect(20, 25, 60, 20), alphaBounds);
        Assert.Equal(alphaBounds, alpha.Bounds);

        FocusableItem focusable = Assert.Single(snapshot.CollectFocusableItems());
        Assert.True(focusable.IsVectorSemantic);
        Assert.Equal(vector.BlockIndex, focusable.BlockIndex);
        Assert.Equal(1, focusable.InlineIndex);
    }

    [Fact]
    public void VectorKeyboardFocusFollowsSemanticOrderIndependentlyOfLinkActions()
    {
        MarkdownVectorSemanticItem[] semantics =
        [
            new(
                1,
                MarkdownVectorSemanticRole.Diagram,
                "Flow diagram",
                null,
                -1,
                new SourceSpan(0, 10),
                new MarkdownVectorRectangle(0, 0, 200, 60)),
            new(
                2,
                MarkdownVectorSemanticRole.Node,
                "Alpha",
                null,
                0,
                new SourceSpan(0, 5),
                new MarkdownVectorRectangle(10, 5, 60, 20),
                MarkdownVectorSemanticFlags.Focusable),
            new(
                3,
                MarkdownVectorSemanticRole.Node,
                "Beta",
                null,
                0,
                new SourceSpan(6, 4),
                new MarkdownVectorRectangle(120, 35, 40, 15),
                MarkdownVectorSemanticFlags.Focusable | MarkdownVectorSemanticFlags.Linked),
        ];
        var scene = new MarkdownVectorScene(
            200,
            60,
            48,
            Array.Empty<MarkdownVectorCommand>(),
            16,
            new MarkdownVectorRectangle(0, 0, 200, 60),
            semantics,
            [
                new MarkdownVectorLinkAction(2, "https://example.test/beta", action: null),
            ]);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        vector.Measure(100);
        vector.Arrange(0, 0, 100);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 100,
            height: (float)vector.Bounds.Height);
        IReadOnlyList<FocusableItem> focusable = snapshot.CollectFocusableItems();

        Assert.Equal(2, focusable.Count);
        Assert.Equal(1, focusable[0].InlineIndex);
        Assert.Equal(2, focusable[1].InlineIndex);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public void VectorSemanticFlagsDriveFocusSelectionPointerAndUiaProjections(int rawFlags)
    {
        var flags = (MarkdownVectorSemanticFlags)rawFlags;
        bool decorative = (flags & MarkdownVectorSemanticFlags.Decorative) != 0;
        bool expectedExposed = !decorative;
        bool expectedFocusable = expectedExposed && (flags & MarkdownVectorSemanticFlags.Focusable) != 0;
        bool expectedSelectable = expectedExposed && (flags & MarkdownVectorSemanticFlags.Selectable) != 0;
        bool expectedLinked = expectedExposed && (flags & MarkdownVectorSemanticFlags.Linked) != 0;
        MarkdownVectorScene scene = CreateFlagScene(flags);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        vector.Measure(200);
        vector.Arrange(0, 0, 200);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 200,
            height: (float)vector.Bounds.Height);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode diagram = Assert.Single(document.Root.Children);
        MarkdownSemanticNode? itemNode = diagram.Children.SingleOrDefault(
            static child => child.AccessibilityName == "Flag target");
        Rect itemBounds = vector.GetSemanticBounds(1);
        var point = new Point(
            itemBounds.Left + itemBounds.Width / 2,
            itemBounds.Top + itemBounds.Height / 2);

        Assert.Equal(expectedExposed, MarkdownVectorSemanticPolicy.IsExposed(flags));
        Assert.Equal(expectedFocusable, MarkdownVectorSemanticPolicy.IsKeyboardFocusable(flags));
        Assert.Equal(expectedSelectable, MarkdownVectorSemanticPolicy.IsSelectable(flags));
        Assert.Equal(expectedLinked, MarkdownVectorSemanticPolicy.IsLinked(flags));
        Assert.Equal(expectedLinked, MarkdownVectorSemanticPolicy.IsInvokable(flags, hasAction: true));

        Assert.Equal(expectedExposed, itemNode is not null);
        if (itemNode is not null)
        {
            Assert.Equal(flags, itemNode.VectorSemanticFlags);
            Assert.Equal(
                expectedLinked ? MarkdownSemanticRole.Link : MarkdownSemanticRole.Group,
                itemNode.Role);
            Assert.Equal(
                expectedLinked ? MarkdownAccessibilityRole.Link : MarkdownAccessibilityRole.Group,
                itemNode.AccessibilityRole);
        }

        Assert.Equal(
            expectedFocusable,
            snapshot.CollectFocusableItems().Any(static item => item.IsVectorSemantic));
        Assert.Equal(
            expectedExposed,
            document.TextSpans.Any(static span => span.VectorSemanticIndex == 1));
        Assert.Equal(expectedSelectable, vector.HitTest(point, out DocumentPosition position));
        if (expectedSelectable)
            Assert.Equal(1, position.InlineIndex);
        Assert.Equal(expectedLinked, vector.TryGetLinkAt(point, out MarkdownVectorLinkAction? link));
        Assert.Equal(expectedLinked, link is not null);

#pragma warning disable MR1001 // Exercise the shared pointer activation implementation.
        var target = MarkdownRendererControl.FindLinkTargetInBlock(
            vector,
            new DocumentPosition(vector.BlockIndex, 1, 0),
            point);
#pragma warning restore MR1001
        Assert.Equal(expectedLinked, target.HasValue);
    }

    [Fact]
    public void LinkedOnlyVectorChildOwnsTextRangeWithoutBecomingSelectable()
    {
        MarkdownVectorScene scene = CreateFlagScene(MarkdownVectorSemanticFlags.Linked);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        vector.Measure(200);
        vector.Arrange(0, 0, 200);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 200,
            height: (float)vector.Bounds.Height);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode diagram = Assert.Single(document.Root.Children);
        MarkdownSemanticNode item = Assert.Single(
            diagram.Children,
            static child => child.AccessibilityName == "Flag target");
        MarkdownTextSpan span = Assert.Single(
            document.TextSpans,
            static candidate => candidate.VectorSemanticIndex == 1);

        Assert.Equal(MarkdownSemanticRole.Link, item.Role);
        Assert.Equal(MarkdownAccessibilityRole.Link, item.AccessibilityRole);
        Assert.Equal("Flag target", document.GetText(item));
        Assert.Equal((item.TextStart, item.TextEnd), (span.TextStart, span.TextEnd));
        Assert.True(item.TextEnd > item.TextStart);

        Rect bounds = vector.GetSemanticBounds(1);
        var point = new Point(
            bounds.Left + bounds.Width / 2,
            bounds.Top + bounds.Height / 2);
        Assert.False(vector.HitTest(point, out _));
        Assert.True(vector.TryGetLinkAt(point, out MarkdownVectorLinkAction? link));
        Assert.NotNull(link);
    }

    [Fact]
    public void PointerHitTargetPreservesExternalVectorLinkDispositionMetadata()
    {
        MarkdownVectorScene scene = CreateSemanticScene(includeLink: true);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        vector.Measure(100);
        vector.Arrange(10, 20, 100);

#pragma warning disable MR1001 // Exercise the shared implementation used by MarkdownDocumentView.
        var target = MarkdownRendererControl.FindLinkTargetInBlock(
            vector,
            new DocumentPosition(vector.BlockIndex, 0, 0),
            new Point(30, 30));
#pragma warning restore MR1001

        Assert.NotNull(target);
        Assert.Equal("https://example.test/alpha", target.Value.Url);
        Assert.True(target.Value.External);
    }

    [Fact]
    public void UnstructuredVectorSemanticTextRemainsOneExactRange()
    {
        const string semanticText = "  x + y\r\n= z  ";
        var scene = new MarkdownVectorScene(
            width: 80,
            height: 30,
            baseline: 24,
            commands: Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 16);
        var builder = new MarkdownContentBuilder();
        builder.AddVectorScene(
            scene,
            new SourceSpan(0, semanticText.Length),
            MarkdownStyleRole.Math,
            MarkdownAccessibilityRole.Math,
            accessibilityName: "Formula",
            semanticText: semanticText);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(
            context,
            Assert.Single(builder.Build().Items),
            MarkdownElementKeys.Math)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        vector.Measure(100);
        vector.Arrange(0, 0, 100);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 100,
            height: (float)vector.Bounds.Height);
        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownTextSpan span = Assert.Single(document.TextSpans);

        Assert.Equal(semanticText, document.Text);
        Assert.Equal((0, semanticText.Length), (span.TextStart, span.TextEnd));
        Assert.Equal(-1, span.VectorSemanticIndex);
    }

    [Fact]
    public void PlainWideVectorSceneGetsAKeyboardOperableOverflowEntry()
    {
        MarkdownVectorScene scene = CreateSemanticScene(includeLink: false);
        MarkdownLayoutContext context = CreateContext();
        var vector = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        vector.Measure(100);
        vector.Arrange(0, 0, 100);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { vector },
            context.SourceMap,
            width: 100,
            height: (float)vector.Bounds.Height);
        FocusableItem focusable = Assert.Single(snapshot.CollectFocusableItems());

        Assert.True(vector.CanScrollHorizontally);
        Assert.True(focusable.IsHorizontalOverflow);
        Assert.Equal(vector.BlockIndex, focusable.BlockIndex);
    }

    [Fact]
    public void OverflowVirtualFocusMapsToItsExactBlockPeerAndFocusableEntry()
    {
        MarkdownVectorScene scene = CreateSemanticScene(includeLink: false);
        MarkdownLayoutContext context = CreateContext();
        var first = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        var second = new VectorSceneBox(context, CreateContent(scene), MarkdownElementKeys.Diagram)
        {
            BlockIndex = context.NextBlockIndex(),
        };
        first.Measure(100);
        first.Arrange(0, 0, 100);
        second.Measure(100);
        second.Arrange(0, (float)first.Bounds.Bottom, 100);

        using var snapshot = new LayoutSnapshot(
            new BlockBox[] { first, second },
            context.SourceMap,
            width: 100,
            height: (float)second.Bounds.Bottom);
        IReadOnlyList<FocusableItem> focusable = snapshot.CollectFocusableItems();

        Assert.Equal(2, focusable.Count);
        Assert.All(focusable, static item => Assert.True(item.IsHorizontalOverflow));
#pragma warning disable MR1001 // Exercise the shared implementation used by MarkdownDocumentView.
        Assert.True(MarkdownRendererControl.TryFindHorizontalOverflowFocusableIndex(
            snapshot,
            focusable,
            first,
            out int firstIndex));
        Assert.True(MarkdownRendererControl.TryFindHorizontalOverflowFocusableIndex(
            snapshot,
            focusable,
            second,
            out int secondIndex));
#pragma warning restore MR1001
        Assert.Equal(0, firstIndex);
        Assert.Equal(1, secondIndex);

        MarkdownSemanticDocument document = snapshot.SemanticDocument;
        MarkdownSemanticNode firstPeerNode = Assert.IsType<MarkdownSemanticNode>(
            MarkdownAutomationPeer.FindHorizontalOverflowFocusNode(document, first));
        MarkdownSemanticNode secondPeerNode = Assert.IsType<MarkdownSemanticNode>(
            MarkdownAutomationPeer.FindHorizontalOverflowFocusNode(document, second));
        Assert.Same(first, firstPeerNode.Box);
        Assert.Same(second, secondPeerNode.Box);
        Assert.NotSame(firstPeerNode, secondPeerNode);
        Assert.Equal(-1, firstPeerNode.VectorSemanticIndex);
        Assert.Equal(-1, secondPeerNode.VectorSemanticIndex);
    }

    [Fact]
    public void ReadOnlyTaskEmbedIsExcludedFromKeyboardOrder()
    {
        var readOnlyTask = new InlineEmbedRun(
            1,
            1,
            () => throw new InvalidOperationException())
        {
            AutomationMetadata = new InlineEmbedAutomationMetadata(
                "Checked",
                "Unchecked",
                "Read only",
                "Toggle",
                "task-1",
                isChecked: false),
        };

        Assert.False(LayoutSnapshot.IsInlineEmbedKeyboardFocusable(readOnlyTask));
    }

    [Fact]
    public void LayoutLocalizationUsesTheCapturedRendererLanguage()
    {
        var provider = new RecordingStringProvider("{0:N1}");
        MarkdownLayoutContext context = CreateContext("tr-TR", provider);

        string value = context.ResolveFormattedString("Number", "{0:N1}", 1234.5);

        Assert.Equal("tr-TR", context.Culture.Name);
        Assert.Equal("tr-TR", provider.LastCulture?.Name);
        Assert.Equal(1234.5.ToString("N1", context.Culture), value);
    }

    private static MarkdownVectorScene CreateSemanticScene(bool includeLink)
    {
        MarkdownVectorSemanticFlags alphaFlags = includeLink
            ? MarkdownVectorSemanticFlags.Focusable |
              MarkdownVectorSemanticFlags.Selectable |
              MarkdownVectorSemanticFlags.Linked
            : MarkdownVectorSemanticFlags.None;
        MarkdownVectorSemanticItem[] semantics =
        [
            new(
                1,
                MarkdownVectorSemanticRole.Diagram,
                "Flow diagram",
                null,
                -1,
                new SourceSpan(0, 10),
                new MarkdownVectorRectangle(0, 0, 200, 60)),
            new(
                2,
                MarkdownVectorSemanticRole.Node,
                "Alpha",
                "First node",
                0,
                new SourceSpan(0, 5),
                new MarkdownVectorRectangle(10, 5, 60, 20),
                alphaFlags),
            new(
                3,
                MarkdownVectorSemanticRole.Label,
                "Beta",
                null,
                0,
                new SourceSpan(6, 4),
                new MarkdownVectorRectangle(120, 35, 40, 15),
                includeLink ? MarkdownVectorSemanticFlags.Selectable : MarkdownVectorSemanticFlags.None),
        ];
        IReadOnlyList<MarkdownVectorLinkAction> links = includeLink
            ? [new MarkdownVectorLinkAction(1, "https://example.test/alpha", action: null, external: true)]
            : Array.Empty<MarkdownVectorLinkAction>();
        return new MarkdownVectorScene(
            width: 200,
            height: 60,
            baseline: 48,
            commands: Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 16,
            viewport: new MarkdownVectorRectangle(0, 0, 200, 60),
            semantics,
            links);
    }

    private static MarkdownVectorScene CreateFlagScene(MarkdownVectorSemanticFlags flags)
    {
        IReadOnlyList<MarkdownVectorLinkAction> links =
            (flags & MarkdownVectorSemanticFlags.Linked) != 0
                ? [new MarkdownVectorLinkAction(1, "https://example.test/flag", action: null)]
                : Array.Empty<MarkdownVectorLinkAction>();
        return new MarkdownVectorScene(
            width: 80,
            height: 40,
            baseline: 32,
            commands: Array.Empty<MarkdownVectorCommand>(),
            referenceFontSize: 16,
            viewport: new MarkdownVectorRectangle(0, 0, 80, 40),
            semantics:
            [
                new(
                    1,
                    MarkdownVectorSemanticRole.Diagram,
                    "Flag diagram",
                    null,
                    -1,
                    new SourceSpan(0, 10),
                    new MarkdownVectorRectangle(0, 0, 80, 40)),
                new(
                    2,
                    MarkdownVectorSemanticRole.Node,
                    "Flag target",
                    null,
                    0,
                    new SourceSpan(0, 10),
                    new MarkdownVectorRectangle(10, 10, 40, 20),
                    flags),
            ],
            links);
    }

    private static MarkdownContent CreateContent(MarkdownVectorScene scene)
    {
        var builder = new MarkdownContentBuilder();
        builder.AddVectorScene(
            scene,
            new SourceSpan(0, 10),
            MarkdownStyleRole.Diagram,
            MarkdownAccessibilityRole.Diagram,
            accessibilityName: "Flow diagram",
            semanticText: "Alpha\nBeta");
        return Assert.Single(builder.Build().Items);
    }

    private static MarkdownLayoutContext CreateContext(
        string language = "en-US",
        IMarkdownStringProvider? stringProvider = null,
        bool isHighContrast = false)
    {
        var diagram = new ElementStyle
        {
            FontFamily = "Host Diagram Face",
            FontSize = 16,
            Foreground = isHighContrast
                ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
                : Color.FromArgb(0xFF, 0x20, 0x20, 0x20),
            Background = isHighContrast
                ? Color.FromArgb(0xFF, 0x00, 0x00, 0x00)
                : Color.FromArgb(0xFF, 0xFA, 0xFA, 0xFA),
        };
        var link = new ElementStyle
        {
            FontSize = 16,
            Foreground = isHighContrast
                ? Color.FromArgb(0xFF, 0x00, 0xFF, 0xFF)
                : Color.FromArgb(0xFF, 0x00, 0x55, 0xCC),
        };
        var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
        {
            [MarkdownElementKeys.Body] = diagram,
            [MarkdownElementKeys.Diagram] = diagram,
            [MarkdownElementKeys.Link] = link,
        };
        Color selectionHighlight = isHighContrast
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00)
            : Color.FromArgb(0xFF, 0x00, 0x66, 0xCC);
        Color selectionForeground = isHighContrast
            ? Color.FromArgb(0xFF, 0x00, 0x00, 0x00)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        var theme = new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            surfaceColor: diagram.Background!.Value,
            selectionHighlightColor: selectionHighlight,
            selectionForegroundColor: selectionForeground,
            focusVisualColor: diagram.Foreground,
            isDark: false,
            isHighContrast: isHighContrast,
            textScaleFactor: 1);
        return new MarkdownLayoutContext(
            CanvasDevice.GetSharedDevice(),
            theme,
            new MarkdownSourceMap(new string(' ', 100)),
            new MarkdownExtensionRegistry(),
            FlowDirection.LeftToRight,
            language: language)
        {
            StringProvider = stringProvider,
        };
    }

    private static Color ToColor(uint argb) => Color.FromArgb(
        (byte)(argb >> 24),
        (byte)(argb >> 16),
        (byte)(argb >> 8),
        (byte)argb);

    private sealed class RecordingStringProvider(string value) : IMarkdownStringProvider
    {
        internal CultureInfo? LastCulture { get; private set; }

        public string? GetString(string key, CultureInfo culture)
        {
            LastCulture = culture;
            return value;
        }
    }
}
