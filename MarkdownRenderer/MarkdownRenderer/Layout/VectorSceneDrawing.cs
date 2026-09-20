using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Owns device-dependent resources for one immutable vector scene. Both inline
/// formulas and block diagrams use this implementation so their paint behavior
/// cannot drift.
/// </summary>
internal sealed class VectorSceneDrawing : IDisposable
{
    // DirectWrite rejects extremely large requested layout dimensions even
    // when they are finite.  float.MaxValue also makes its internal glyph
    // arithmetic overflow for sufficiently long labels.  Vector scenes are
    // already bounded, so use a generous finite canvas instead of pretending
    // that the text has an infinite layout surface.
    private const float MaximumTextLayoutDimension = 1_000_000f;

    private readonly MarkdownVectorScene _scene;
    private readonly CanvasGeometry?[] _geometries;
    private readonly CanvasTextLayout?[] _textLayouts;
    private readonly CanvasStrokeStyle?[] _strokeStyles;
    private readonly bool[] _linkedSemantics;
    private readonly Matrix3x2[] _transformStack;
    private readonly CanvasActiveLayer?[] _clipStack;
    private bool _disposed;

    internal VectorSceneDrawing(
        ICanvasResourceCreator resourceCreator,
        MarkdownVectorScene scene,
        CancellationToken cancellationToken,
        string? hostFontFamily = null)
    {
        ArgumentNullException.ThrowIfNull(resourceCreator);
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _geometries = new CanvasGeometry?[scene.Commands.Count];
        _textLayouts = new CanvasTextLayout?[scene.Commands.Count];
        _strokeStyles = new CanvasStrokeStyle?[scene.Commands.Count];
        _linkedSemantics = new bool[scene.Semantics.Count];
        _transformStack = new Matrix3x2[scene.Commands.Count];
        _clipStack = new CanvasActiveLayer?[scene.Commands.Count];

        for (int i = 0; i < scene.Links.Count; i++)
            _linkedSemantics[scene.Links[i].SemanticIndex] = true;

        try
        {
            for (int i = 0; i < scene.Commands.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                MarkdownVectorCommand command = scene.Commands[i];
                if (command.Kind is MarkdownVectorCommandKind.FillPath or
                    MarkdownVectorCommandKind.StrokePath or
                    MarkdownVectorCommandKind.DrawPath or
                    MarkdownVectorCommandKind.BeginClip)
                {
                    _geometries[i] = CreateGeometry(
                        resourceCreator,
                        command.Path,
                        command.Style?.FillRule ?? MarkdownVectorFillRule.Nonzero,
                        cancellationToken);
                }

                if (command.Kind == MarkdownVectorCommandKind.DrawText &&
                    command.Text is { } text &&
                    command.TextStyle is { } textStyle)
                {
                    var format = new CanvasTextFormat
                    {
                        FontFamily = ResolveFontFamily(textStyle, hostFontFamily),
                        FontSize = textStyle.FontSize,
                        FontWeight = new FontWeight { Weight = textStyle.FontWeight },
                        FontStyle = textStyle.Italic ? FontStyle.Italic : FontStyle.Normal,
                        Direction = textStyle.RightToLeft
                            ? CanvasTextDirection.RightToLeftThenTopToBottom
                            : CanvasTextDirection.LeftToRightThenTopToBottom,
                        WordWrapping = CanvasWordWrapping.NoWrap,
                    };
                    try
                    {
                        (float layoutWidth, float layoutHeight) = ResolveTextLayoutSize(
                            scene,
                            text,
                            textStyle);
                        try
                        {
                            _textLayouts[i] = new CanvasTextLayout(
                                resourceCreator,
                                text,
                                format,
                                layoutWidth,
                                layoutHeight);
                        }
                        catch (Exception exception) when (
                            exception is ArgumentException or COMException)
                        {
                            throw new ArgumentException(
                                $"Win2D rejected vector text command {i} " +
                                $"(length={text.Length}, family='{format.FontFamily}', " +
                                $"size={format.FontSize}, weight={format.FontWeight.Weight}, " +
                                $"layout={layoutWidth}x{layoutHeight}).",
                                exception);
                        }
                    }
                    finally
                    {
                        format.Dispose();
                    }
                }

                if (command.Style is { } style && RequiresStrokeStyle(style))
                {
                    _strokeStyles[i] = CreateStrokeStyle(style);
                }
            }
        }
        catch
        {
            DisposeDeviceResources();
            throw;
        }
    }

    internal MarkdownVectorScene Scene => _scene;

    internal void Paint(
        CanvasDrawingSession drawingSession,
        Rect destination,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Width <= 0 || destination.Height <= 0 ||
            _scene.Viewport.Width <= 0 || _scene.Viewport.Height <= 0)
        {
            return;
        }

        VectorViewportTransform viewportTransform = VectorViewportTransform.Create(
            _scene.Viewport,
            destination);
        if (!float.IsFinite(viewportTransform.Scale) || viewportTransform.Scale <= 0)
            return;
        Matrix3x2 originalTransform = drawingSession.Transform;
        drawingSession.Transform = viewportTransform.Matrix * originalTransform;

        int transformDepth = 0;
        int clipDepth = 0;
        try
        {
            for (int i = 0; i < _scene.Commands.Count; i++)
            {
                MarkdownVectorCommand command = _scene.Commands[i];
                MarkdownVectorPaintStyle? style = command.Style;
                switch (command.Kind)
                {
                    case MarkdownVectorCommandKind.BeginGroup:
                        _transformStack[transformDepth++] = drawingSession.Transform;
                        drawingSession.Transform = ToMatrix(command.Transform) * drawingSession.Transform;
                        break;
                    case MarkdownVectorCommandKind.EndGroup:
                        if (transformDepth > 0)
                            drawingSession.Transform = _transformStack[--transformDepth];
                        break;
                    case MarkdownVectorCommandKind.BeginClip:
                        if (_geometries[i] is { } clip)
                            _clipStack[clipDepth++] = drawingSession.CreateLayer(1f, clip);
                        break;
                    case MarkdownVectorCommandKind.EndClip:
                        if (clipDepth > 0)
                        {
                            clipDepth--;
                            _clipStack[clipDepth]?.Dispose();
                            _clipStack[clipDepth] = null;
                        }
                        break;
                    case MarkdownVectorCommandKind.FillPath:
                        if (_geometries[i] is { } fillGeometry)
                            drawingSession.FillGeometry(fillGeometry, ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride));
                        break;
                    case MarkdownVectorCommandKind.StrokePath:
                        if (_geometries[i] is { } strokeGeometry)
                            drawingSession.DrawGeometry(strokeGeometry, ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride), command.StrokeWidth);
                        break;
                    case MarkdownVectorCommandKind.FillRectangle:
                        drawingSession.FillRectangle(ToRect(command.Rectangle), ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride));
                        break;
                    case MarkdownVectorCommandKind.StrokeRectangle:
                        drawingSession.DrawRectangle(ToRect(command.Rectangle), ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride), command.StrokeWidth);
                        break;
                    case MarkdownVectorCommandKind.StrokeLine:
                        drawingSession.DrawLine(command.Start.X, command.Start.Y, command.End.X, command.End.Y,
                            ResolveLegacyColor(command.ColorArgb, palette, highContrast, foregroundOverride), command.StrokeWidth);
                        break;
                    case MarkdownVectorCommandKind.DrawRectangle when style is not null:
                        DrawRectangle(drawingSession, command, style, _strokeStyles[i], palette, highContrast, foregroundOverride);
                        break;
                    case MarkdownVectorCommandKind.DrawPath when style is not null:
                        DrawPath(drawingSession, command, _geometries[i], style, _strokeStyles[i], palette, highContrast, foregroundOverride);
                        break;
                    case MarkdownVectorCommandKind.DrawLine when style is not null:
                        if (style.StrokeWidth > 0 && TryResolveColor(style.StrokeArgb, style.Opacity,
                                palette, ResolvePaintRole(
                                    command,
                                    style.StrokeRole,
                                    style.StrokeArgb,
                                    style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground,
                                    highContrast),
                                highContrast, foregroundOverride, out Color lineColor))
                        {
                            drawingSession.DrawLine(command.Start.X, command.Start.Y, command.End.X, command.End.Y,
                                lineColor, style.StrokeWidth, _strokeStyles[i]);
                        }
                        break;
                    case MarkdownVectorCommandKind.DrawEllipse when style is not null:
                        DrawEllipse(drawingSession, command, style, _strokeStyles[i], palette, highContrast, foregroundOverride);
                        break;
                    case MarkdownVectorCommandKind.DrawText when style is not null:
                        DrawText(drawingSession, command, style, _textLayouts[i], palette, highContrast, foregroundOverride);
                        break;
                }
            }
        }
        finally
        {
            while (clipDepth > 0)
            {
                clipDepth--;
                _clipStack[clipDepth]?.Dispose();
                _clipStack[clipDepth] = null;
            }
            drawingSession.Transform = originalTransform;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        DisposeDeviceResources();
    }

    private void DisposeDeviceResources()
    {
        for (int i = 0; i < _geometries.Length; i++)
        {
            _geometries[i]?.Dispose();
            _textLayouts[i]?.Dispose();
            _strokeStyles[i]?.Dispose();
            _geometries[i] = null;
            _textLayouts[i] = null;
            _strokeStyles[i] = null;
        }
    }

    private void DrawRectangle(
        CanvasDrawingSession ds,
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        Rect rect = ToRect(command.Rectangle);
        MarkdownVectorPaintRole fillRole = ResolvePaintRole(
            command,
            style.FillRole,
            style.FillArgb,
            style.HighContrastFillRole ?? InferFillRole(command, style, GetSemanticRole(command)),
            highContrast);
        if (TryResolveColor(style.FillArgb, style.Opacity, palette, fillRole,
                highContrast, foregroundOverride, out Color fill))
            ds.FillRectangle(rect, fill);
        if (style.StrokeWidth > 0 && TryResolveColor(style.StrokeArgb, style.Opacity, palette,
                ResolvePaintRole(
                    command,
                    style.StrokeRole,
                    style.StrokeArgb,
                    style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground,
                    highContrast),
                highContrast, foregroundOverride, out Color stroke))
            ds.DrawRectangle(rect, stroke, style.StrokeWidth, strokeStyle);
    }

    private void DrawPath(
        CanvasDrawingSession ds,
        MarkdownVectorCommand command,
        CanvasGeometry? geometry,
        MarkdownVectorPaintStyle style,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        if (geometry is null)
            return;
        MarkdownVectorPaintRole fillRole = ResolvePaintRole(
            command,
            style.FillRole,
            style.FillArgb,
            style.HighContrastFillRole ?? InferFillRole(command, style, GetSemanticRole(command)),
            highContrast);
        if (TryResolveColor(style.FillArgb, style.Opacity, palette, fillRole,
                highContrast, foregroundOverride, out Color fill))
            ds.FillGeometry(geometry, fill);
        if (style.StrokeWidth > 0 && TryResolveColor(style.StrokeArgb, style.Opacity, palette,
                ResolvePaintRole(
                    command,
                    style.StrokeRole,
                    style.StrokeArgb,
                    style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground,
                    highContrast),
                highContrast, foregroundOverride, out Color stroke))
            ds.DrawGeometry(geometry, stroke, style.StrokeWidth, strokeStyle);
    }

    private void DrawEllipse(
        CanvasDrawingSession ds,
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        CanvasStrokeStyle? strokeStyle,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        MarkdownVectorRectangle rectangle = command.Rectangle;
        float centerX = rectangle.X + rectangle.Width / 2f;
        float centerY = rectangle.Y + rectangle.Height / 2f;
        float radiusX = rectangle.Width / 2f;
        float radiusY = rectangle.Height / 2f;
        MarkdownVectorPaintRole fillRole = ResolvePaintRole(
            command,
            style.FillRole,
            style.FillArgb,
            style.HighContrastFillRole ?? InferFillRole(command, style, GetSemanticRole(command)),
            highContrast);
        if (TryResolveColor(style.FillArgb, style.Opacity, palette, fillRole,
                highContrast, foregroundOverride, out Color fill))
            ds.FillEllipse(centerX, centerY, radiusX, radiusY, fill);
        if (style.StrokeWidth > 0 && TryResolveColor(style.StrokeArgb, style.Opacity, palette,
                ResolvePaintRole(
                    command,
                    style.StrokeRole,
                    style.StrokeArgb,
                    style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground,
                    highContrast),
                highContrast, foregroundOverride, out Color stroke))
            ds.DrawEllipse(centerX, centerY, radiusX, radiusY, stroke, style.StrokeWidth, strokeStyle);
    }

    private void DrawText(
        CanvasDrawingSession ds,
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        CanvasTextLayout? layout,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        uint? textArgb = style.FillArgb ?? style.StrokeArgb;
        MarkdownVectorPaintRole declaredRole = style.FillArgb is not null || style.StrokeArgb is null
            ? style.FillRole
            : style.StrokeRole;
        MarkdownVectorPaintRole highContrastRole = style.FillArgb is not null || style.StrokeArgb is null
            ? style.HighContrastFillRole ?? MarkdownVectorPaintRole.Foreground
            : style.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground;
        if (layout is null || !TryResolveColor(textArgb, style.Opacity,
                palette, ResolvePaintRole(
                    command,
                    declaredRole,
                    textArgb,
                    highContrastRole,
                    highContrast),
                highContrast, foregroundOverride, out Color color))
        {
            return;
        }

        float baseline = layout.LineMetrics.Length > 0 ? layout.LineMetrics[0].Baseline : command.TextStyle!.FontSize * 0.8f;
        float width = (float)Math.Max(0, layout.DrawBounds.Width);
        float x = command.TextStyle!.Alignment switch
        {
            MarkdownVectorTextAlignment.Center => command.Start.X - width / 2f,
            MarkdownVectorTextAlignment.End => command.Start.X - width,
            _ => command.Start.X,
        };
        ds.DrawTextLayout(layout, x, command.Start.Y - baseline, color);
    }

    internal static CanvasStrokeStyle CreateStrokeStyle(MarkdownVectorPaintStyle style)
    {
        var result = new CanvasStrokeStyle
        {
            StartCap = ToCap(style.LineCap),
            EndCap = ToCap(style.LineCap),
            DashCap = ToCap(style.LineCap),
            LineJoin = ToJoin(style.LineJoin),
            DashOffset = style.DashOffset,
            TransformBehavior = style.NonScalingStroke
                ? CanvasStrokeTransformBehavior.Fixed
                : CanvasStrokeTransformBehavior.Normal,
        };
        try
        {
            if (style.DashArray.Count > 0)
            {
                var dashes = new float[style.DashArray.Count];
                for (int i = 0; i < dashes.Length; i++)
                    dashes[i] = style.DashArray[i];
                result.CustomDashStyle = dashes;
            }
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    internal static bool RequiresStrokeStyle(MarkdownVectorPaintStyle style)
        => style.StrokeWidth > 0 &&
           (style.NonScalingStroke ||
            style.DashArray.Count > 0 ||
            style.LineCap != MarkdownVectorLineCap.Flat ||
            style.LineJoin != MarkdownVectorLineJoin.Miter);

    private static CanvasCapStyle ToCap(MarkdownVectorLineCap value) => value switch
    {
        MarkdownVectorLineCap.Round => CanvasCapStyle.Round,
        MarkdownVectorLineCap.Square => CanvasCapStyle.Square,
        _ => CanvasCapStyle.Flat,
    };

    private static CanvasLineJoin ToJoin(MarkdownVectorLineJoin value) => value switch
    {
        MarkdownVectorLineJoin.Round => CanvasLineJoin.Round,
        MarkdownVectorLineJoin.Bevel => CanvasLineJoin.Bevel,
        _ => CanvasLineJoin.Miter,
    };

    private static CanvasGeometry CreateGeometry(
        ICanvasResourceCreator resourceCreator,
        IReadOnlyList<MarkdownVectorPathOperation> operations,
        MarkdownVectorFillRule fillRule,
        CancellationToken cancellationToken)
    {
        using var builder = new CanvasPathBuilder(resourceCreator);
        builder.SetFilledRegionDetermination(fillRule == MarkdownVectorFillRule.EvenOdd
            ? CanvasFilledRegionDetermination.Alternate
            : CanvasFilledRegionDetermination.Winding);
        bool figureOpen = false;
        for (int i = 0; i < operations.Count; i++)
        {
            if ((i & 0xff) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            MarkdownVectorPathOperation operation = operations[i];
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
                    builder.AddCubicBezier(ToVector2(operation.Point1), ToVector2(operation.Point2), ToVector2(operation.Point3));
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

    private static Matrix3x2 ToMatrix(MarkdownVectorTransform value) =>
        new(value.M11, value.M12, value.M21, value.M22, value.M31, value.M32);

    private static Vector2 ToVector2(MarkdownVectorPoint point) => new(point.X, point.Y);

    private static Rect ToRect(MarkdownVectorRectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);

    private static Color ResolveLegacyColor(
        uint? argb,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        TryResolveColor(
            argb,
            1f,
            palette,
            highContrast ? MarkdownVectorPaintRole.Foreground : MarkdownVectorPaintRole.Authored,
            highContrast,
            foregroundOverride,
            out Color color);
        return color;
    }

    internal static Color ResolveColorForTesting(
        uint? argb,
        float opacity,
        VectorScenePaintPalette palette,
        MarkdownVectorPaintRole role,
        bool highContrast)
        => ResolveColor(argb, opacity, palette, role, highContrast);

    internal static Color ResolveColor(
        uint? argb,
        float opacity,
        VectorScenePaintPalette palette,
        MarkdownVectorPaintRole role,
        bool highContrast)
    {
        TryResolveColor(
            argb,
            opacity,
            palette,
            role,
            highContrast,
            foregroundOverride: null,
            out Color color);
        return color;
    }

    internal static string ResolveFontFamily(
        MarkdownVectorTextStyle style,
        string? hostFontFamily)
    {
        ArgumentNullException.ThrowIfNull(style);
        string selected = style.FontRole == MarkdownVectorFontRole.Host &&
               !string.IsNullOrWhiteSpace(hostFontFamily)
            ? hostFontFamily.Trim()
            : style.FontFamily;
        return SharedCanvasTextFormatCache.NormalizeFontFamilyForCanvas(selected);
    }

    internal static (float Width, float Height) ResolveTextLayoutSize(
        MarkdownVectorScene scene,
        string text,
        MarkdownVectorTextStyle style)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);

        // NoWrap keeps the authored line intact.  The estimate merely gives
        // DirectWrite enough finite measuring space; it does not scale or
        // otherwise alter the scene.  Use double arithmetic so adversarially
        // long labels cannot overflow before the clamp is applied.
        double estimatedTextWidth = checked((double)text.Length) * style.FontSize * 2d + 64d;
        double width = Math.Max(
            Math.Max(scene.Width, scene.Viewport.Width),
            estimatedTextWidth);
        double height = Math.Max(
            Math.Max(scene.Height, scene.Viewport.Height),
            style.FontSize * 4d + 64d);
        return (
            (float)Math.Clamp(width, 1d, MaximumTextLayoutDimension),
            (float)Math.Clamp(height, 1d, MaximumTextLayoutDimension));
    }

    private static bool TryResolveColor(
        uint? argb,
        float opacity,
        VectorScenePaintPalette palette,
        MarkdownVectorPaintRole role,
        bool highContrast,
        Color? foregroundOverride,
        out Color color)
    {
        byte authoredAlpha = argb is { } authored ? (byte)(authored >> 24) : (byte)0xFF;
        if (argb is not null && authoredAlpha == 0)
        {
            color = default;
            return false;
        }

        bool semanticMapping = argb is null || role != MarkdownVectorPaintRole.Authored;
        if (foregroundOverride is { } overrideColor)
        {
            color = Color.FromArgb(
                semanticMapping || argb is null ? overrideColor.A : authoredAlpha,
                overrideColor.R,
                overrideColor.G,
                overrideColor.B);
        }
        else if (semanticMapping)
        {
            color = palette.Resolve(role);
        }
        else
        {
            uint value = argb.GetValueOrDefault();
            byte alpha = (byte)(value >> 24);

            if (highContrast)
            {
                Color mapped = palette.Resolve(MarkdownVectorPaintRole.Foreground);
                // System High Contrast colors are semantic, opaque pairs. Retaining
                // authored alpha here can collapse thin strokes/text back into the
                // surface and violates the platform HC brush contract.
                color = Color.FromArgb(highContrast ? (byte)0xFF : alpha, mapped.R, mapped.G, mapped.B);
            }
            else
            {
                color = Color.FromArgb(alpha, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
            }
        }

        byte resolvedAlpha = highContrast && color.A > 0
            ? (byte)0xFF
            : (byte)Math.Clamp(
                (int)Math.Round(color.A * opacity * (semanticMapping ? authoredAlpha / 255f : 1f)),
                0,
                255);
        color = Color.FromArgb(resolvedAlpha, color.R, color.G, color.B);
        return color.A > 0;
    }

    private MarkdownVectorPaintRole ResolvePaintRole(
        MarkdownVectorCommand command,
        MarkdownVectorPaintRole declaredRole,
        uint? argb,
        MarkdownVectorPaintRole highContrastFallback,
        bool highContrast) =>
        ResolvePaintRole(
            command,
            declaredRole,
            argb,
            highContrastFallback,
            highContrast,
            command.SemanticIndex >= 0 &&
            command.SemanticIndex < _linkedSemantics.Length &&
            _linkedSemantics[command.SemanticIndex]);

    internal static MarkdownVectorPaintRole ResolvePaintRole(
        MarkdownVectorCommand command,
        MarkdownVectorPaintRole declaredRole,
        uint? argb,
        MarkdownVectorPaintRole highContrastFallback,
        bool highContrast,
        bool linkedSemantic)
    {
        ArgumentNullException.ThrowIfNull(command);
        MarkdownVectorPaintRole role = declaredRole;
        if (role == MarkdownVectorPaintRole.Authored)
        {
            if (highContrast)
                role = highContrastFallback;
            else if (argb is null)
                role = MarkdownVectorPaintRole.Foreground;
            else
                return MarkdownVectorPaintRole.Authored;
        }

        return role == MarkdownVectorPaintRole.Foreground && linkedSemantic
            ? MarkdownVectorPaintRole.Link
            : role;
    }

    internal static MarkdownVectorPaintRole InferFillRole(
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        MarkdownVectorSemanticRole? semanticRole = null)
    {
        // Rectangle/ellipse fills are Mermaid surfaces even when they do not
        // carry a border (the full-scene background is a common example). A
        // filled path is treated as a surface only when its stroke identifies
        // it as a closed node; fill-only paths are commonly arrowheads or other
        // authored accents and should retain their light/dark palette.
        return ((command.Kind is MarkdownVectorCommandKind.DrawRectangle or
                    MarkdownVectorCommandKind.DrawEllipse) ||
                (command.Kind == MarkdownVectorCommandKind.DrawPath &&
                 (style.StrokeWidth > 0 ||
                  semanticRole is MarkdownVectorSemanticRole.Diagram or
                      MarkdownVectorSemanticRole.Group or
                      MarkdownVectorSemanticRole.Node or
                      MarkdownVectorSemanticRole.Legend)))
            ? MarkdownVectorPaintRole.Surface
            : MarkdownVectorPaintRole.Foreground;
    }

    private MarkdownVectorSemanticRole? GetSemanticRole(MarkdownVectorCommand command) =>
        command.SemanticIndex >= 0 && command.SemanticIndex < _scene.Semantics.Count
            ? _scene.Semantics[command.SemanticIndex].Role
            : null;
}

internal readonly record struct VectorScenePaintPalette(Color Foreground, Color Surface, Color Link)
{
    public Color Resolve(MarkdownVectorPaintRole role) => role switch
    {
        MarkdownVectorPaintRole.Surface => Surface,
        MarkdownVectorPaintRole.Link => Link,
        _ => Foreground,
    };
}
