using System.Numerics;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout.Internal.Vectors.Drawing;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using static MarkdownRenderer.GitHub.Tests.CommandIndexedVectorNativeFactory;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// A frozen Win2D renderer for vector scenes that keeps one native slot per
/// command, the storage shape before compaction. The compact-resource tests
/// compare <see cref="VectorSceneDrawing"/> pixels against it.
/// </summary>
internal static class CommandIndexedVectorReferenceRenderer
{
    /// <summary>
    /// Frozen command-indexed Win2D oracle for the pre-compaction storage shape.
    /// Native objects live at their command ordinal; painting never uses the
    /// compact sequential type cursors under test.
    /// </summary>
    internal static Color[] Render(
        CanvasDevice device,
        MarkdownVectorScene scene,
        float targetWidth,
        float targetHeight,
        Rect destination,
        float rasterizationScale,
        VectorScenePaintPalette palette,
        bool highContrast)
    {
        int commandCount = scene.Commands.Count;
        var resources = new CommandIndexedResources(commandCount);

        try
        {
            resources.Create(device, scene);

            using var target = new CanvasRenderTarget(
                device, targetWidth, targetHeight, 96 * rasterizationScale);
            using (CanvasDrawingSession session = target.CreateDrawingSession())
            {
                session.Clear(Color.FromArgb(0, 0, 0, 0));
                Matrix3x2 original = Matrix3x2.CreateTranslation(1.25f, 0.75f);
                session.Transform = VectorViewportTransform.Create(
                    scene.Viewport,
                    destination).Matrix * original;
                PaintCommands(session, scene, resources, original, palette, highContrast);
            }

            return target.GetPixelColors();
        }
        finally
        {
            resources.Dispose();
        }
    }

    private static void PaintCommands(
        CanvasDrawingSession session,
        MarkdownVectorScene scene,
        CommandIndexedResources resources,
        Matrix3x2 original,
        VectorScenePaintPalette palette,
        bool highContrast)
    {
        int transformDepth = 0;
        int clipDepth = 0;
        try
        {
            for (int index = 0; index < scene.Commands.Count; index++)
            {
                MarkdownVectorCommand command = scene.Commands[index];
                CanvasGeometry? geometry = resources.Geometries[index];
                if (TryPaintScopeCommand(session, command, geometry, resources, ref transformDepth, ref clipDepth))
                    continue;

                PaintDrawCommand(
                    session,
                    command,
                    geometry,
                    resources.TextLayouts[index],
                    resources.StrokeStyles[index],
                    palette,
                    highContrast);
            }
        }
        finally
        {
            while (clipDepth > 0)
            {
                int activeIndex = --clipDepth;
                resources.ClipStack[activeIndex]?.Dispose();
                resources.ClipStack[activeIndex] = null;
            }
            session.Transform = original;
        }
    }

    /// <summary>
    /// Applies a group or clip scope command. Returns false for drawing commands.
    /// </summary>
    private static bool TryPaintScopeCommand(
        CanvasDrawingSession session,
        MarkdownVectorCommand command,
        CanvasGeometry? geometry,
        CommandIndexedResources resources,
        ref int transformDepth,
        ref int clipDepth)
    {
        switch (command.Kind)
        {
            case MarkdownVectorCommandKind.BeginGroup:
                resources.TransformStack[transformDepth++] = session.Transform;
                session.Transform = ToMatrix(command.Transform) * session.Transform;
                return true;
            case MarkdownVectorCommandKind.EndGroup:
                if (transformDepth > 0)
                    session.Transform = resources.TransformStack[--transformDepth];
                return true;
            case MarkdownVectorCommandKind.BeginClip:
                if (geometry is not null)
                    resources.ClipStack[clipDepth++] = session.CreateLayer(1f, geometry);
                return true;
            case MarkdownVectorCommandKind.EndClip:
                if (clipDepth > 0)
                {
                    int activeIndex = --clipDepth;
                    resources.ClipStack[activeIndex]?.Dispose();
                    resources.ClipStack[activeIndex] = null;
                }
                return true;
            default:
                return false;
        }
    }

    private static void PaintDrawCommand(
        CanvasDrawingSession session,
        MarkdownVectorCommand command,
        CanvasGeometry? geometry,
        CanvasTextLayout? textLayout,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast)
    {
        MarkdownVectorPaintStyle? style = command.Style;
        switch (command.Kind)
        {
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
                DrawStyledLine(session, command, style, strokeStyle, palette, highContrast);
                break;
            case MarkdownVectorCommandKind.DrawEllipse when style is not null:
                DrawStyledEllipse(session, command, style, strokeStyle, palette, highContrast);
                break;
            case MarkdownVectorCommandKind.DrawText when style is not null && textLayout is not null:
                DrawStyledText(session, command, style, textLayout, palette, highContrast);
                break;
        }
    }

    private static void DrawStyledLine(
        CanvasDrawingSession session,
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast)
    {
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
    }

    private static void DrawStyledText(
        CanvasDrawingSession session,
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        CanvasTextLayout textLayout,
        VectorScenePaintPalette palette,
        bool highContrast)
    {
        Color textColor = ResolveStyleColor(
            command, style, fill: true, text: true, palette, highContrast);
        if (textColor.A > 0)
        {
            Vector2 origin = ResolveTextOrigin(command, textLayout);
            session.DrawTextLayout(textLayout, origin.X, origin.Y, textColor);
        }
    }

    /// <summary>
    /// The command-indexed native objects and scope stacks of one reference
    /// render. Every array has one slot per scene command.
    /// </summary>
    private sealed class CommandIndexedResources(int commandCount) : IDisposable
    {
        internal CanvasGeometry?[] Geometries { get; } = new CanvasGeometry?[commandCount];

        internal CanvasTextLayout?[] TextLayouts { get; } = new CanvasTextLayout?[commandCount];

        internal CanvasStrokeStyle?[] StrokeStyles { get; } = new CanvasStrokeStyle?[commandCount];

        internal Matrix3x2[] TransformStack { get; } = new Matrix3x2[commandCount];

        internal CanvasActiveLayer?[] ClipStack { get; } = new CanvasActiveLayer?[commandCount];

        internal void Create(CanvasDevice device, MarkdownVectorScene scene)
        {
            for (int index = 0; index < scene.Commands.Count; index++)
            {
                MarkdownVectorCommand command = scene.Commands[index];
                if (RequiresGeometry(command.Kind))
                {
                    Geometries[index] = CreateGeometry(
                        device,
                        command.Path,
                        command.Style?.FillRule ?? MarkdownVectorFillRule.Nonzero);
                }

                if (RequiresTextLayout(command))
                    TextLayouts[index] = CreateTextLayout(device, scene, command.Text!, command.TextStyle!);

                if (command.Style is { } style && RequiresStrokeStyle(style))
                    StrokeStyles[index] = CreateStrokeStyle(style);
            }
        }

        public void Dispose()
        {
            DisposeAll(Geometries);
            DisposeAll(TextLayouts);
            DisposeAll(StrokeStyles);
            DisposeAll(ClipStack);
        }
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
                VectorScenePaintResolver.InferFillRole(command, style, semanticRole: null);
        }
        else
        {
            argb = style.StrokeArgb;
            declaredRole = style.StrokeRole;
            fallbackRole = style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground;
        }

        MarkdownVectorPaintRole role = VectorScenePaintResolver.ResolvePaintRole(
            command, declaredRole, argb, fallbackRole, highContrast, linkedSemantic: false);
        return VectorScenePaintResolver.ResolveColor(argb, style.Opacity, palette, role, highContrast);
    }

    private static Color ResolveLegacyColor(
        uint? argb,
        VectorScenePaintPalette palette,
        bool highContrast) =>
        VectorScenePaintResolver.ResolveColor(
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

    private static Matrix3x2 ToMatrix(MarkdownVectorTransform value) =>
        new(value.M11, value.M12, value.M21, value.M22, value.M31, value.M32);

    private static Rect ToRect(MarkdownVectorRectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    private static void DisposeAll<T>(T?[] resources) where T : class, IDisposable
    {
        for (int index = 0; index < resources.Length; index++)
        {
            resources[index]?.Dispose();
            resources[index] = null;
        }
    }
}
