using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class VectorSceneCompactResourceTests
{
    private readonly MarkdownRenderResources _renderResources = new();

    private const float TargetWidth = 76;
    private const float TargetHeight = 62;
    private const float SceneWidth = 64;
    private const float SceneHeight = 48;

    [Fact]
    public void PreparedScratchArraysMatchActualResourceCountsAndMaximumScopeDepths()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: false);
        MarkdownVectorScene scene = CreateMixedScene(rightToLeft: false);
        using var drawing = new VectorSceneDrawing(device, scene, CancellationToken.None, _renderResources);

        Assert.True(drawing.IsPrepared);
        Assert.Equal(CountGeometryCommands(scene), Slots(drawing.GeometrySlots).Count);
        Assert.Equal(CountTextCommands(scene), Slots(drawing.TextLayoutSlots).Count);
        Assert.Equal(CountStrokeStyles(scene), Slots(drawing.StrokeStyleSlots).Count);
        Assert.Equal(CountMaximumDepth(scene, MarkdownVectorCommandKind.BeginGroup, MarkdownVectorCommandKind.EndGroup),
            Slots(drawing.TransformStackSlots).Count);
        Assert.Equal(CountMaximumDepth(scene, MarkdownVectorCommandKind.BeginClip, MarkdownVectorCommandKind.EndClip),
            Slots(drawing.ClipStackSlots).Count);

        // The first path/text commands are intentionally invisible; each still
        // owns a native slot, so the visible commands after them must use their
        // own geometry, text-layout, and stroke-style ordinal.
        Assert.True(scene.Commands.Count(command => command.Kind == MarkdownVectorCommandKind.DrawPath) >= 2);
        Assert.True(scene.Commands.Count(command => command.Kind == MarkdownVectorCommandKind.DrawText) >= 2);
    }

    [Fact]
    public void PrimitiveOnlyScenesReuseSharedEmptyNativeArrays()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: false);
        var primitive = MarkdownVectorCommand.FillRectangle(
            new MarkdownVectorRectangle(1, 1, 4, 4),
            0xFF336699);
        var commands = new MarkdownVectorCommand[256];
        Array.Fill(commands, primitive);
        var scene = new MarkdownVectorScene(SceneWidth, SceneHeight, 36, commands);

        using var drawing = new VectorSceneDrawing(device, scene, CancellationToken.None, _renderResources);

        Assert.Same(Array.Empty<CanvasGeometry?>(), Slots(drawing.GeometrySlots));
        Assert.Same(Array.Empty<CanvasTextLayout?>(), Slots(drawing.TextLayoutSlots));
        Assert.Same(Array.Empty<CanvasStrokeStyle?>(), Slots(drawing.StrokeStyleSlots));
        Assert.Same(Array.Empty<Matrix3x2>(), Slots(drawing.TransformStackSlots));
        Assert.Same(Array.Empty<CanvasActiveLayer?>(), Slots(drawing.ClipStackSlots));
    }

    [Fact]
    public void LargePrimitiveAndSparsePathStormRetainsOnlyActualNativeSlots()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: false);
        const int primitiveCount = 8_192;
        const int pathCount = 64;
        var commands = new MarkdownVectorCommand[primitiveCount + pathCount];
        var primitive = MarkdownVectorCommand.FillRectangle(
            new MarkdownVectorRectangle(1, 1, 2, 2),
            0xFF224466);
        Array.Fill(commands, primitive, 0, primitiveCount);

        IReadOnlyList<MarkdownVectorPathOperation> path = TrianglePath(8, 8, 6);
        MarkdownVectorCommand fillPath = MarkdownVectorCommand.FillPath(path, 0xFF806040);
        Array.Fill(commands, fillPath, primitiveCount, pathCount);
        var scene = new MarkdownVectorScene(SceneWidth, SceneHeight, 36, commands);
        using var drawing = new VectorSceneDrawing(device, scene, CancellationToken.None, _renderResources);

        IReadOnlyList<CanvasGeometry?> geometries = Slots(drawing.GeometrySlots);
        IReadOnlyList<CanvasTextLayout?> textLayouts = Slots(drawing.TextLayoutSlots);
        IReadOnlyList<CanvasStrokeStyle?> strokeStyles = Slots(drawing.StrokeStyleSlots);
        IReadOnlyList<Matrix3x2> transforms = Slots(drawing.TransformStackSlots);
        IReadOnlyList<CanvasActiveLayer?> clips = Slots(drawing.ClipStackSlots);

        Assert.Equal(pathCount, geometries.Count);
        Assert.Same(Array.Empty<CanvasTextLayout?>(), textLayouts);
        Assert.Same(Array.Empty<CanvasStrokeStyle?>(), strokeStyles);
        Assert.Same(Array.Empty<Matrix3x2>(), transforms);
        Assert.Same(Array.Empty<CanvasActiveLayer?>(), clips);

        // Compare retained managed scratch-array payload only. This does not
        // estimate Win2D/native allocations, driver memory, or process RSS.
        long retainedPayloadBytes =
            (long)geometries.Count * IntPtr.Size +
            (long)textLayouts.Count * IntPtr.Size +
            (long)strokeStyles.Count * IntPtr.Size +
            (long)transforms.Count * Marshal.SizeOf<Matrix3x2>() +
            (long)clips.Count * IntPtr.Size;
        long previousCommandIndexedPayloadBytes = (long)commands.Length *
            (4 * IntPtr.Size + Marshal.SizeOf<Matrix3x2>());

        Assert.True(
            retainedPayloadBytes * 5 <= previousCommandIndexedPayloadBytes,
            $"Compact scratch payload {retainedPayloadBytes} bytes must be at most 20% of the prior command-indexed " +
            $"payload {previousCommandIndexedPayloadBytes} bytes (x64 prior baseline: 56 bytes/command). ");
    }

    [Theory]
    [InlineData(1f, false, false, false)]
    [InlineData(1f, false, false, true)]
    [InlineData(1f, false, true, false)]
    [InlineData(1f, false, true, true)]
    [InlineData(1f, true, false, false)]
    [InlineData(1f, true, false, true)]
    [InlineData(1f, true, true, false)]
    [InlineData(1f, true, true, true)]
    [InlineData(1.5f, false, false, false)]
    [InlineData(1.5f, false, false, true)]
    [InlineData(1.5f, false, true, false)]
    [InlineData(1.5f, false, true, true)]
    [InlineData(1.5f, true, false, false)]
    [InlineData(1.5f, true, false, true)]
    [InlineData(1.5f, true, true, false)]
    [InlineData(1.5f, true, true, true)]
    [InlineData(2f, false, false, false)]
    [InlineData(2f, false, false, true)]
    [InlineData(2f, false, true, false)]
    [InlineData(2f, false, true, true)]
    [InlineData(2f, true, false, false)]
    [InlineData(2f, true, false, true)]
    [InlineData(2f, true, true, false)]
    [InlineData(2f, true, true, true)]
    [InlineData(4f, false, false, false)]
    [InlineData(4f, false, false, true)]
    [InlineData(4f, false, true, false)]
    [InlineData(4f, false, true, true)]
    [InlineData(4f, true, false, false)]
    [InlineData(4f, true, false, true)]
    [InlineData(4f, true, true, false)]
    [InlineData(4f, true, true, true)]
    public void MixedCommandsMatchCommandIndexedWin2DOracleAcrossDpiBidiAndThemesAfterReprepare(
        float rasterizationScale,
        bool rightToLeft,
        bool highContrast,
        bool forceSoftwareRenderer)
    {
        using var device = new CanvasDevice(forceSoftwareRenderer);
        MarkdownVectorScene scene = CreateMixedScene(rightToLeft);
        var palette = highContrast
            ? new VectorScenePaintPalette(
                Color.FromArgb(0xFF, 0xF8, 0xF8, 0xF8),
                Color.FromArgb(0xFF, 0x08, 0x08, 0x08),
                Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00))
            : new VectorScenePaintPalette(
                Color.FromArgb(0xFF, 0x22, 0x2A, 0x34),
                Color.FromArgb(0xFF, 0xE8, 0xEC, 0xF0),
                Color.FromArgb(0xFF, 0x12, 0x67, 0xB1));

        using var drawing = new VectorSceneDrawing(device, scene, CancellationToken.None, _renderResources);
        Color[] expected = CommandIndexedVectorReferenceRenderer.Render(
            device, scene, TargetWidth, TargetHeight, new Rect(4, 4, SceneWidth, SceneHeight),
            rasterizationScale, palette, highContrast);
        Color[] first = RenderCompact(
            drawing, device, scene, rasterizationScale, palette, highContrast);
        Assert.Equal(expected, first);

        drawing.ReleaseNativeResources();
        Assert.False(drawing.IsPrepared);
        drawing.PrepareNativeResources(CancellationToken.None);
        Assert.True(drawing.IsPrepared);
        Assert.Equal(CountGeometryCommands(scene), Slots(drawing.GeometrySlots).Count);
        Assert.Equal(CountTextCommands(scene), Slots(drawing.TextLayoutSlots).Count);
        Assert.Equal(CountStrokeStyles(scene), Slots(drawing.StrokeStyleSlots).Count);
        Assert.Equal(2, Slots(drawing.TransformStackSlots).Count);
        Assert.Equal(2, Slots(drawing.ClipStackSlots).Count);

        Color[] afterReprepare = RenderCompact(
            drawing, device, scene, rasterizationScale, palette, highContrast);
        Assert.Equal(expected, afterReprepare);
    }

    private static MarkdownVectorScene CreateMixedScene(bool rightToLeft)
    {
        MarkdownVectorTextStyle hiddenText = new(
            "Segoe UI", 10, rightToLeft: rightToLeft, alignment: MarkdownVectorTextAlignment.Center);
        MarkdownVectorTextStyle currentColorText = new(
            "Segoe UI", 11, fontWeight: 600, italic: true,
            rightToLeft: rightToLeft,
            alignment: rightToLeft ? MarkdownVectorTextAlignment.End : MarkdownVectorTextAlignment.Start);
        MixedScenePaints paints = CreateMixedScenePaints();
        MarkdownVectorCommand[] commands = CreateMixedCommands(rightToLeft, hiddenText, currentColorText, paints);

        return new MarkdownVectorScene(
            SceneWidth,
            SceneHeight,
            36,
            commands,
            referenceFontSize: 12,
            viewport: new MarkdownVectorRectangle(0, 0, SceneWidth, SceneHeight),
            semantics: Array.Empty<MarkdownVectorSemanticItem>(),
            links: Array.Empty<MarkdownVectorLinkAction>());
    }

    /// <summary>
    /// The mixed scene's paints: a dashed semantic shape, an alpha-zero and
    /// opacity-zero shape and text style that must stay invisible, and a
    /// current-color style that maps to the link color in High Contrast.
    /// </summary>
    private static MixedScenePaints CreateMixedScenePaints()
    {
        MarkdownVectorPaintStyle visibleShape = MarkdownVectorPaintStyle.CreateSemantic(
            MarkdownVectorPaintRole.Surface,
            MarkdownVectorPaintRole.Foreground,
            strokeWidth: 1.5f,
            lineCap: MarkdownVectorLineCap.Round,
            lineJoin: MarkdownVectorLineJoin.Round,
            dashArray: new[] { 3f, 2f },
            dashOffset: 0.5f)
            .WithHighContrastRoles(
                MarkdownVectorPaintRole.Foreground,
                MarkdownVectorPaintRole.Surface);

        MarkdownVectorPaintStyle hiddenShape = new(
            fillArgb: 0x00FF0000,
            strokeArgb: 0x000000FF,
            strokeWidth: 2f,
            opacity: 0f,
            lineCap: MarkdownVectorLineCap.Square,
            lineJoin: MarkdownVectorLineJoin.Bevel,
            dashArray: new[] { 2f, 1f });

        MarkdownVectorPaintStyle currentColorStyle = MarkdownVectorPaintStyle.CreateSemantic(
            MarkdownVectorPaintRole.Foreground,
            MarkdownVectorPaintRole.Foreground,
            strokeWidth: 1.25f,
            lineCap: MarkdownVectorLineCap.Round,
            lineJoin: MarkdownVectorLineJoin.Bevel)
            .WithHighContrastRoles(
                MarkdownVectorPaintRole.Link,
                MarkdownVectorPaintRole.Foreground);

        MarkdownVectorPaintStyle hiddenTextStyle = new(
            fillArgb: 0x00000000,
            opacity: 0f);
        return new MixedScenePaints(visibleShape, hiddenShape, currentColorStyle, hiddenTextStyle);
    }

    private static MarkdownVectorCommand[] CreateMixedCommands(
        bool rightToLeft,
        MarkdownVectorTextStyle hiddenText,
        MarkdownVectorTextStyle currentColorText,
        MixedScenePaints paints)
    {
        (MarkdownVectorPaintStyle visibleShape, MarkdownVectorPaintStyle hiddenShape,
            MarkdownVectorPaintStyle currentColorStyle, MarkdownVectorPaintStyle hiddenTextStyle) = paints;
        MarkdownVectorPathOperation[] firstPath = TrianglePath(13, 9, 14);
        MarkdownVectorPathOperation[] secondPath =
        [
            MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(21, 21)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(43, 22)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(37, 38)),
            MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(25, 35)),
            MarkdownVectorPathOperation.Close(),
        ];

        MarkdownVectorCommand[] commands =
        [
            // Legacy command kinds coexist with the styled commands below.
            MarkdownVectorCommand.FillRectangle(
                new MarkdownVectorRectangle(0, 0, SceneWidth, SceneHeight), 0xFFF8F7F3),
            MarkdownVectorCommand.BeginGroup(new MarkdownVectorTransform(1, 0, 0, 1, 1.25f, 0.75f)),
            MarkdownVectorCommand.BeginClip(RectanglePath(2, 2, 59, 43)),
            MarkdownVectorCommand.FillPath(firstPath, colorArgb: null),
            MarkdownVectorCommand.DrawRectangle(
                new MarkdownVectorRectangle(7, 7, 45, 34), visibleShape),
            MarkdownVectorCommand.BeginGroup(new MarkdownVectorTransform(0.96f, 0.04f, -0.03f, 1.02f, 1.5f, -0.5f)),
            MarkdownVectorCommand.BeginClip(RectanglePath(10, 12, 42, 29)),
            // Both alpha-zero and opacity-zero ensure this stays invisible in
            // normal and High Contrast modes while consuming all its slots.
            MarkdownVectorCommand.DrawPath(secondPath, hiddenShape, isClosed: true),
            MarkdownVectorCommand.DrawPath(secondPath, visibleShape, isClosed: true),
            MarkdownVectorCommand.DrawText(
                "not visible",
                new MarkdownVectorPoint(30, 26),
                hiddenText,
                hiddenTextStyle),
            MarkdownVectorCommand.DrawText(
                rightToLeft ? "مرحبا native" : "CurrentColor native",
                new MarkdownVectorPoint(rightToLeft ? 47 : 15, 31),
                currentColorText,
                currentColorStyle),
            MarkdownVectorCommand.EndClip(),
            MarkdownVectorCommand.DrawEllipse(
                new MarkdownVectorRectangle(36, 20, 10, 9), currentColorStyle),
            MarkdownVectorCommand.EndGroup(),
            MarkdownVectorCommand.EndClip(),
            MarkdownVectorCommand.EndGroup(),
            MarkdownVectorCommand.StrokePath(firstPath, 1.75f, 0xFF2F6A88),
            MarkdownVectorCommand.StrokeRectangle(
                new MarkdownVectorRectangle(4, 4, 54, 39), 0.75f, 0xFF643E78),
            MarkdownVectorCommand.StrokeLine(
                new MarkdownVectorPoint(4, 45), new MarkdownVectorPoint(59, 44), 1.25f, 0xFF43845A),
            MarkdownVectorCommand.DrawLine(
                new MarkdownVectorPoint(5, 42), new MarkdownVectorPoint(56, 41), currentColorStyle),
        ];
        return commands;
    }

    private sealed record MixedScenePaints(
        MarkdownVectorPaintStyle VisibleShape,
        MarkdownVectorPaintStyle HiddenShape,
        MarkdownVectorPaintStyle CurrentColor,
        MarkdownVectorPaintStyle HiddenText);

    private static Color[] RenderCompact(
        VectorSceneDrawing drawing,
        CanvasDevice device,
        MarkdownVectorScene scene,
        float rasterizationScale,
        VectorScenePaintPalette palette,
        bool highContrast)
    {
        using var target = new CanvasRenderTarget(
            device, TargetWidth, TargetHeight, 96 * rasterizationScale);
        using (CanvasDrawingSession session = target.CreateDrawingSession())
        {
            session.Clear(Color.FromArgb(0, 0, 0, 0));
            Matrix3x2 original = Matrix3x2.CreateTranslation(1.25f, 0.75f);
            session.Transform = original;
            drawing.Paint(
                new LayoutPlacementTransformContext(),
                session,
                new Rect(4, 4, SceneWidth, SceneHeight),
                palette,
                highContrast);
            Assert.Equal(original, session.Transform);
        }

        return target.GetPixelColors();
    }

    private static int CountGeometryCommands(MarkdownVectorScene scene)
        => scene.Commands.Count(command =>
            CommandIndexedVectorNativeFactory.RequiresGeometry(command.Kind));

    private static int CountTextCommands(MarkdownVectorScene scene)
        => scene.Commands.Count(CommandIndexedVectorNativeFactory.RequiresTextLayout);

    private static int CountStrokeStyles(MarkdownVectorScene scene)
        => scene.Commands.Count(command =>
            command.Style is { } style && CommandIndexedVectorNativeFactory.RequiresStrokeStyle(style));

    private static int CountMaximumDepth(
        MarkdownVectorScene scene,
        MarkdownVectorCommandKind beginKind,
        MarkdownVectorCommandKind endKind)
    {
        int depth = 0;
        int maximum = 0;
        foreach (MarkdownVectorCommand command in scene.Commands)
        {
            if (command.Kind == beginKind)
                maximum = System.Math.Max(maximum, ++depth);
            else if (command.Kind == endKind)
                depth--;
        }
        Assert.Equal(0, depth);
        return maximum;
    }

    private static MarkdownVectorPathOperation[] TrianglePath(float x, float y, float size) =>
    [
        MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(x, y)),
        MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(x + size, y + size * 0.1f)),
        MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(x + size * 0.55f, y + size)),
        MarkdownVectorPathOperation.Close(),
    ];

    private static MarkdownVectorPathOperation[] RectanglePath(float x, float y, float width, float height) =>
    [
        MarkdownVectorPathOperation.MoveTo(new MarkdownVectorPoint(x, y)),
        MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(x + width, y)),
        MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(x + width, y + height)),
        MarkdownVectorPathOperation.LineTo(new MarkdownVectorPoint(x, y + height)),
        MarkdownVectorPathOperation.Close(),
    ];

    // A prepared drawing owns every slot kind, even when a kind is empty.
    private static IReadOnlyList<T> Slots<T>(IReadOnlyList<T>? slots) =>
        Assert.IsAssignableFrom<IReadOnlyList<T>>(slots);
}
