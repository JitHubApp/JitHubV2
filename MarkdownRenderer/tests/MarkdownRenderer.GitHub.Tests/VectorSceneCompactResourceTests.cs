using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class VectorSceneCompactResourceTests
{
    private const float TargetWidth = 76;
    private const float TargetHeight = 62;
    private const float SceneWidth = 64;
    private const float SceneHeight = 48;
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void PreparedScratchArraysMatchActualResourceCountsAndMaximumScopeDepths()
    {
        using var device = new CanvasDevice(forceSoftwareRenderer: false);
        MarkdownVectorScene scene = CreateMixedScene(rightToLeft: false);
        using var drawing = new VectorSceneDrawing(device, scene, CancellationToken.None);

        Assert.True(drawing.IsPrepared);
        Assert.Equal(CountGeometryCommands(scene), GetArray<CanvasGeometry?>(drawing, "_geometries").Length);
        Assert.Equal(CountTextCommands(scene), GetArray<CanvasTextLayout?>(drawing, "_textLayouts").Length);
        Assert.Equal(CountStrokeStyles(scene), GetArray<CanvasStrokeStyle?>(drawing, "_strokeStyles").Length);
        Assert.Equal(CountMaximumDepth(scene, MarkdownVectorCommandKind.BeginGroup, MarkdownVectorCommandKind.EndGroup),
            GetArray<Matrix3x2>(drawing, "_transformStack").Length);
        Assert.Equal(CountMaximumDepth(scene, MarkdownVectorCommandKind.BeginClip, MarkdownVectorCommandKind.EndClip),
            GetArray<CanvasActiveLayer?>(drawing, "_clipStack").Length);

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

        using var drawing = new VectorSceneDrawing(device, scene, CancellationToken.None);

        Assert.Same(Array.Empty<CanvasGeometry?>(), GetArray<CanvasGeometry?>(drawing, "_geometries"));
        Assert.Same(Array.Empty<CanvasTextLayout?>(), GetArray<CanvasTextLayout?>(drawing, "_textLayouts"));
        Assert.Same(Array.Empty<CanvasStrokeStyle?>(), GetArray<CanvasStrokeStyle?>(drawing, "_strokeStyles"));
        Assert.Same(Array.Empty<Matrix3x2>(), GetArray<Matrix3x2>(drawing, "_transformStack"));
        Assert.Same(Array.Empty<CanvasActiveLayer?>(), GetArray<CanvasActiveLayer?>(drawing, "_clipStack"));
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
        using var drawing = new VectorSceneDrawing(device, scene, CancellationToken.None);

        CanvasGeometry?[] geometries = GetArray<CanvasGeometry?>(drawing, "_geometries");
        CanvasTextLayout?[] textLayouts = GetArray<CanvasTextLayout?>(drawing, "_textLayouts");
        CanvasStrokeStyle?[] strokeStyles = GetArray<CanvasStrokeStyle?>(drawing, "_strokeStyles");
        Matrix3x2[] transforms = GetArray<Matrix3x2>(drawing, "_transformStack");
        CanvasActiveLayer?[] clips = GetArray<CanvasActiveLayer?>(drawing, "_clipStack");

        Assert.Equal(pathCount, geometries.Length);
        Assert.Same(Array.Empty<CanvasTextLayout?>(), textLayouts);
        Assert.Same(Array.Empty<CanvasStrokeStyle?>(), strokeStyles);
        Assert.Same(Array.Empty<Matrix3x2>(), transforms);
        Assert.Same(Array.Empty<CanvasActiveLayer?>(), clips);

        // Compare retained managed scratch-array payload only. This does not
        // estimate Win2D/native allocations, driver memory, or process RSS.
        long retainedPayloadBytes =
            (long)geometries.Length * IntPtr.Size +
            (long)textLayouts.Length * IntPtr.Size +
            (long)strokeStyles.Length * IntPtr.Size +
            (long)transforms.Length * Marshal.SizeOf<Matrix3x2>() +
            (long)clips.Length * IntPtr.Size;
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

        using var drawing = new VectorSceneDrawing(device, scene, CancellationToken.None);
        Color[] expected = RenderCommandIndexedReference(
            device, scene, rasterizationScale, palette, highContrast);
        Color[] first = RenderCompact(
            drawing, device, scene, rasterizationScale, palette, highContrast);
        Assert.Equal(expected, first);

        drawing.ReleaseNativeResources();
        Assert.False(drawing.IsPrepared);
        drawing.PrepareNativeResources(CancellationToken.None);
        Assert.True(drawing.IsPrepared);
        Assert.Equal(CountGeometryCommands(scene), GetArray<CanvasGeometry?>(drawing, "_geometries").Length);
        Assert.Equal(CountTextCommands(scene), GetArray<CanvasTextLayout?>(drawing, "_textLayouts").Length);
        Assert.Equal(CountStrokeStyles(scene), GetArray<CanvasStrokeStyle?>(drawing, "_strokeStyles").Length);
        Assert.Equal(2, GetArray<Matrix3x2>(drawing, "_transformStack").Length);
        Assert.Equal(2, GetArray<CanvasActiveLayer?>(drawing, "_clipStack").Length);

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
                session,
                new Rect(4, 4, SceneWidth, SceneHeight),
                palette,
                highContrast);
            Assert.Equal(original, session.Transform);
        }

        return target.GetPixelColors();
    }

    /// <summary>
    /// Frozen command-indexed Win2D oracle for the pre-compaction storage shape.
    /// Native objects live at their command ordinal; painting never uses the
    /// compact sequential type cursors under test.
    /// </summary>
    private static Color[] RenderCommandIndexedReference(
        CanvasDevice device,
        MarkdownVectorScene scene,
        float rasterizationScale,
        VectorScenePaintPalette palette,
        bool highContrast)
    {
        int commandCount = scene.Commands.Count;
        var geometries = new CanvasGeometry?[commandCount];
        var textLayouts = new CanvasTextLayout?[commandCount];
        var strokeStyles = new CanvasStrokeStyle?[commandCount];
        var transformStack = new Matrix3x2[commandCount];
        var clipStack = new CanvasActiveLayer?[commandCount];

        try
        {
            for (int index = 0; index < commandCount; index++)
            {
                MarkdownVectorCommand command = scene.Commands[index];
                if (RequiresGeometry(command.Kind))
                {
                    geometries[index] = CreateGeometry(
                        device,
                        command.Path,
                        command.Style?.FillRule ?? MarkdownVectorFillRule.Nonzero);
                }

                if (RequiresTextLayout(command))
                    textLayouts[index] = CreateTextLayout(device, scene, command.Text!, command.TextStyle!);

                if (command.Style is { } style && RequiresStrokeStyle(style))
                    strokeStyles[index] = CreateStrokeStyle(style);
            }

            using var target = new CanvasRenderTarget(
                device, TargetWidth, TargetHeight, 96 * rasterizationScale);
            using (CanvasDrawingSession session = target.CreateDrawingSession())
            {
                session.Clear(Color.FromArgb(0, 0, 0, 0));
                Matrix3x2 original = Matrix3x2.CreateTranslation(1.25f, 0.75f);
                session.Transform = VectorViewportTransform.Create(
                    scene.Viewport,
                    new Rect(4, 4, SceneWidth, SceneHeight)).Matrix * original;
                int transformDepth = 0;
                int clipDepth = 0;
                try
                {
                    for (int index = 0; index < commandCount; index++)
                    {
                        MarkdownVectorCommand command = scene.Commands[index];
                        MarkdownVectorPaintStyle? style = command.Style;
                        CanvasGeometry? geometry = geometries[index];
                        CanvasTextLayout? textLayout = textLayouts[index];
                        CanvasStrokeStyle? strokeStyle = strokeStyles[index];

                        switch (command.Kind)
                        {
                            case MarkdownVectorCommandKind.BeginGroup:
                                transformStack[transformDepth++] = session.Transform;
                                session.Transform = ToMatrix(command.Transform) * session.Transform;
                                break;
                            case MarkdownVectorCommandKind.EndGroup:
                                if (transformDepth > 0)
                                    session.Transform = transformStack[--transformDepth];
                                break;
                            case MarkdownVectorCommandKind.BeginClip:
                                if (geometry is not null)
                                    clipStack[clipDepth++] = session.CreateLayer(1f, geometry);
                                break;
                            case MarkdownVectorCommandKind.EndClip:
                                if (clipDepth > 0)
                                {
                                    int activeIndex = --clipDepth;
                                    clipStack[activeIndex]?.Dispose();
                                    clipStack[activeIndex] = null;
                                }
                                break;
                            case MarkdownVectorCommandKind.FillPath when geometry is not null:
                                session.FillGeometry(
                                    geometry,
                                    ResolveLegacyColor(command.ColorArgb, palette, highContrast));
                                break;
                            case MarkdownVectorCommandKind.StrokePath when geometry is not null:
                                session.DrawGeometry(
                                    geometry,
                                    ResolveLegacyColor(command.ColorArgb, palette, highContrast),
                                    command.StrokeWidth);
                                break;
                            case MarkdownVectorCommandKind.FillRectangle:
                                session.FillRectangle(
                                    ToRect(command.Rectangle),
                                    ResolveLegacyColor(command.ColorArgb, palette, highContrast));
                                break;
                            case MarkdownVectorCommandKind.StrokeRectangle:
                                session.DrawRectangle(
                                    ToRect(command.Rectangle),
                                    ResolveLegacyColor(command.ColorArgb, palette, highContrast),
                                    command.StrokeWidth);
                                break;
                            case MarkdownVectorCommandKind.StrokeLine:
                                session.DrawLine(
                                    command.Start.X, command.Start.Y,
                                    command.End.X, command.End.Y,
                                    ResolveLegacyColor(command.ColorArgb, palette, highContrast),
                                    command.StrokeWidth);
                                break;
                            case MarkdownVectorCommandKind.DrawRectangle when style is not null:
                                DrawStyledRectangle(session, command, style, strokeStyle, palette, highContrast);
                                break;
                            case MarkdownVectorCommandKind.DrawPath when style is not null:
                                DrawStyledPath(session, command, geometry, style, strokeStyle, palette, highContrast);
                                break;
                            case MarkdownVectorCommandKind.DrawLine when style is not null:
                                if (style.StrokeWidth > 0)
                                {
                                    Color lineColor = ResolveStyleColor(
                                        command, style, fill: false, text: false, palette, highContrast);
                                    if (lineColor.A > 0)
                                    {
                                        session.DrawLine(
                                            command.Start.X, command.Start.Y,
                                            command.End.X, command.End.Y,
                                            lineColor, style.StrokeWidth, strokeStyle);
                                    }
                                }
                                break;
                            case MarkdownVectorCommandKind.DrawEllipse when style is not null:
                                DrawStyledEllipse(session, command, style, strokeStyle, palette, highContrast);
                                break;
                            case MarkdownVectorCommandKind.DrawText when style is not null && textLayout is not null:
                                Color textColor = ResolveStyleColor(
                                    command, style, fill: true, text: true, palette, highContrast);
                                if (textColor.A > 0)
                                {
                                    Vector2 origin = ResolveTextOrigin(command, textLayout);
                                    session.DrawTextLayout(textLayout, origin.X, origin.Y, textColor);
                                }
                                break;
                        }
                    }
                }
                finally
                {
                    while (clipDepth > 0)
                    {
                        int activeIndex = --clipDepth;
                        clipStack[activeIndex]?.Dispose();
                        clipStack[activeIndex] = null;
                    }
                    session.Transform = original;
                }
            }

            return target.GetPixelColors();
        }
        finally
        {
            DisposeAll(geometries);
            DisposeAll(textLayouts);
            DisposeAll(strokeStyles);
            DisposeAll(clipStack);
        }
    }

    private static CanvasGeometry CreateGeometry(
        ICanvasResourceCreator resourceCreator,
        IReadOnlyList<MarkdownVectorPathOperation> operations,
        MarkdownVectorFillRule fillRule)
    {
        using var builder = new CanvasPathBuilder(resourceCreator);
        builder.SetFilledRegionDetermination(fillRule == MarkdownVectorFillRule.EvenOdd
            ? CanvasFilledRegionDetermination.Alternate
            : CanvasFilledRegionDetermination.Winding);

        bool figureOpen = false;
        foreach (MarkdownVectorPathOperation operation in operations)
        {
            switch (operation.Kind)
            {
                case MarkdownVectorPathOperationKind.Move:
                    if (figureOpen)
                        builder.EndFigure(CanvasFigureLoop.Open);
                    builder.BeginFigure(ToVector2(operation.Point1));
                    figureOpen = true;
                    break;
                case MarkdownVectorPathOperationKind.Line when figureOpen:
                    builder.AddLine(ToVector2(operation.Point1));
                    break;
                case MarkdownVectorPathOperationKind.QuadraticBezier when figureOpen:
                    builder.AddQuadraticBezier(ToVector2(operation.Point1), ToVector2(operation.Point2));
                    break;
                case MarkdownVectorPathOperationKind.CubicBezier when figureOpen:
                    builder.AddCubicBezier(
                        ToVector2(operation.Point1),
                        ToVector2(operation.Point2),
                        ToVector2(operation.Point3));
                    break;
                case MarkdownVectorPathOperationKind.Close when figureOpen:
                    builder.EndFigure(CanvasFigureLoop.Closed);
                    figureOpen = false;
                    break;
            }
        }
        if (figureOpen)
            builder.EndFigure(CanvasFigureLoop.Open);
        return CanvasGeometry.CreatePath(builder);
    }

    private static CanvasTextLayout CreateTextLayout(
        ICanvasResourceCreator resourceCreator,
        MarkdownVectorScene scene,
        string text,
        MarkdownVectorTextStyle style)
    {
        (float width, float height) = VectorSceneDrawing.ResolveTextLayoutSize(scene, text, style);
        using var format = new CanvasTextFormat
        {
            FontFamily = VectorSceneDrawing.ResolveFontFamily(style, hostFontFamily: null),
            FontSize = style.FontSize,
            FontWeight = new FontWeight { Weight = style.FontWeight },
            FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal,
            Direction = style.RightToLeft
                ? CanvasTextDirection.RightToLeftThenTopToBottom
                : CanvasTextDirection.LeftToRightThenTopToBottom,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        return new CanvasTextLayout(resourceCreator, text, format, width, height);
    }

    private static CanvasStrokeStyle CreateStrokeStyle(MarkdownVectorPaintStyle style)
    {
        var result = new CanvasStrokeStyle
        {
            StartCap = ToCanvasCap(style.LineCap),
            EndCap = ToCanvasCap(style.LineCap),
            DashCap = ToCanvasCap(style.LineCap),
            LineJoin = ToCanvasJoin(style.LineJoin),
            DashOffset = style.DashOffset,
            TransformBehavior = style.NonScalingStroke
                ? CanvasStrokeTransformBehavior.Fixed
                : CanvasStrokeTransformBehavior.Normal,
        };
        if (style.DashArray.Count > 0)
        {
            var dashes = new float[style.DashArray.Count];
            for (int index = 0; index < dashes.Length; index++)
                dashes[index] = style.DashArray[index];
            result.CustomDashStyle = dashes;
        }
        return result;
    }

    private static void DrawStyledRectangle(
        CanvasDrawingSession session,
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast)
    {
        Rect rectangle = ToRect(command.Rectangle);
        Color fill = ResolveStyleColor(command, style, fill: true, text: false, palette, highContrast);
        if (fill.A > 0)
            session.FillRectangle(rectangle, fill);
        Color stroke = ResolveStyleColor(command, style, fill: false, text: false, palette, highContrast);
        if (style.StrokeWidth > 0 && stroke.A > 0)
            session.DrawRectangle(rectangle, stroke, style.StrokeWidth, strokeStyle);
    }

    private static void DrawStyledPath(
        CanvasDrawingSession session,
        MarkdownVectorCommand command,
        CanvasGeometry? geometry,
        MarkdownVectorPaintStyle style,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast)
    {
        if (geometry is null)
            return;
        Color fill = ResolveStyleColor(command, style, fill: true, text: false, palette, highContrast);
        if (fill.A > 0)
            session.FillGeometry(geometry, fill);
        Color stroke = ResolveStyleColor(command, style, fill: false, text: false, palette, highContrast);
        if (style.StrokeWidth > 0 && stroke.A > 0)
            session.DrawGeometry(geometry, stroke, style.StrokeWidth, strokeStyle);
    }

    private static void DrawStyledEllipse(
        CanvasDrawingSession session,
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast)
    {
        MarkdownVectorRectangle rectangle = command.Rectangle;
        float centerX = rectangle.X + rectangle.Width / 2f;
        float centerY = rectangle.Y + rectangle.Height / 2f;
        float radiusX = rectangle.Width / 2f;
        float radiusY = rectangle.Height / 2f;
        Color fill = ResolveStyleColor(command, style, fill: true, text: false, palette, highContrast);
        if (fill.A > 0)
            session.FillEllipse(centerX, centerY, radiusX, radiusY, fill);
        Color stroke = ResolveStyleColor(command, style, fill: false, text: false, palette, highContrast);
        if (style.StrokeWidth > 0 && stroke.A > 0)
            session.DrawEllipse(centerX, centerY, radiusX, radiusY, stroke, style.StrokeWidth, strokeStyle);
    }

    private static Color ResolveStyleColor(
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        bool fill,
        bool text,
        VectorScenePaintPalette palette,
        bool highContrast)
    {
        uint? argb;
        MarkdownVectorPaintRole declaredRole;
        MarkdownVectorPaintRole fallbackRole;
        if (text)
        {
            argb = style.FillArgb ?? style.StrokeArgb;
            bool useFill = style.FillArgb is not null || style.StrokeArgb is null;
            declaredRole = useFill ? style.FillRole : style.StrokeRole;
            fallbackRole = useFill
                ? style.HighContrastFillRole ?? MarkdownVectorPaintRole.Foreground
                : style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground;
        }
        else if (fill)
        {
            argb = style.FillArgb;
            declaredRole = style.FillRole;
            fallbackRole = style.HighContrastFillRole ??
                VectorSceneDrawing.InferFillRole(command, style, semanticRole: null);
        }
        else
        {
            argb = style.StrokeArgb;
            declaredRole = style.StrokeRole;
            fallbackRole = style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground;
        }

        MarkdownVectorPaintRole role = VectorSceneDrawing.ResolvePaintRole(
            command, declaredRole, argb, fallbackRole, highContrast, linkedSemantic: false);
        return VectorSceneDrawing.ResolveColor(argb, style.Opacity, palette, role, highContrast);
    }

    private static Color ResolveLegacyColor(
        uint? argb,
        VectorScenePaintPalette palette,
        bool highContrast) =>
        VectorSceneDrawing.ResolveColor(
            argb,
            opacity: 1f,
            palette,
            highContrast ? MarkdownVectorPaintRole.Foreground : MarkdownVectorPaintRole.Authored,
            highContrast);

    private static Vector2 ResolveTextOrigin(MarkdownVectorCommand command, CanvasTextLayout layout)
    {
        float baseline = layout.LineMetrics.Length > 0
            ? layout.LineMetrics[0].Baseline
            : command.TextStyle!.FontSize * 0.8f;
        float width = (float)System.Math.Max(0, layout.DrawBounds.Width);
        float x = command.TextStyle!.Alignment switch
        {
            MarkdownVectorTextAlignment.Center => command.Start.X - width / 2f,
            MarkdownVectorTextAlignment.End => command.Start.X - width,
            _ => command.Start.X,
        };
        return new Vector2(x, command.Start.Y - baseline);
    }

    private static bool RequiresGeometry(MarkdownVectorCommandKind kind)
        => kind is MarkdownVectorCommandKind.FillPath or
            MarkdownVectorCommandKind.StrokePath or
            MarkdownVectorCommandKind.DrawPath or
            MarkdownVectorCommandKind.BeginClip;

    private static bool RequiresTextLayout(MarkdownVectorCommand command)
        => command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Text is not null && command.TextStyle is not null;

    private static bool RequiresStrokeStyle(MarkdownVectorPaintStyle style)
        => style.StrokeWidth > 0 &&
            (style.NonScalingStroke ||
             style.DashArray.Count > 0 ||
             style.LineCap != MarkdownVectorLineCap.Flat ||
             style.LineJoin != MarkdownVectorLineJoin.Miter);

    private static int CountGeometryCommands(MarkdownVectorScene scene)
        => scene.Commands.Count(command => RequiresGeometry(command.Kind));

    private static int CountTextCommands(MarkdownVectorScene scene)
        => scene.Commands.Count(RequiresTextLayout);

    private static int CountStrokeStyles(MarkdownVectorScene scene)
        => scene.Commands.Count(command => command.Style is { } style && RequiresStrokeStyle(style));

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

    private static CanvasCapStyle ToCanvasCap(MarkdownVectorLineCap value) => value switch
    {
        MarkdownVectorLineCap.Round => CanvasCapStyle.Round,
        MarkdownVectorLineCap.Square => CanvasCapStyle.Square,
        _ => CanvasCapStyle.Flat,
    };

    private static CanvasLineJoin ToCanvasJoin(MarkdownVectorLineJoin value) => value switch
    {
        MarkdownVectorLineJoin.Round => CanvasLineJoin.Round,
        MarkdownVectorLineJoin.Bevel => CanvasLineJoin.Bevel,
        _ => CanvasLineJoin.Miter,
    };

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

    private static Matrix3x2 ToMatrix(MarkdownVectorTransform value) =>
        new(value.M11, value.M12, value.M21, value.M22, value.M31, value.M32);

    private static Vector2 ToVector2(MarkdownVectorPoint point) => new(point.X, point.Y);

    private static Rect ToRect(MarkdownVectorRectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    private static T[] GetArray<T>(VectorSceneDrawing drawing, string fieldName)
    {
        FieldInfo field = Assert.IsAssignableFrom<FieldInfo>(
            typeof(VectorSceneDrawing).GetField(fieldName, PrivateInstance));
        return Assert.IsAssignableFrom<T[]>(field.GetValue(drawing));
    }

    private static void DisposeAll<T>(T?[] resources) where T : class, IDisposable
    {
        for (int index = 0; index < resources.Length; index++)
        {
            resources[index]?.Dispose();
            resources[index] = null;
        }
    }
}
