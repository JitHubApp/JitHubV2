using System.Numerics;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Layout;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI.Text;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Builds the Win2D geometry, text layout and stroke style objects of the
/// command-indexed reference renderer, and says which commands need each.
/// </summary>
internal static class CommandIndexedVectorNativeFactory
{
    internal static CanvasGeometry CreateGeometry(
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

    internal static CanvasTextLayout CreateTextLayout(
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

    internal static CanvasStrokeStyle CreateStrokeStyle(MarkdownVectorPaintStyle style)
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

    internal static bool RequiresGeometry(MarkdownVectorCommandKind kind)
        => kind is MarkdownVectorCommandKind.FillPath or
            MarkdownVectorCommandKind.StrokePath or
            MarkdownVectorCommandKind.DrawPath or
            MarkdownVectorCommandKind.BeginClip;

    internal static bool RequiresTextLayout(MarkdownVectorCommand command)
        => command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Text is not null && command.TextStyle is not null;

    internal static bool RequiresStrokeStyle(MarkdownVectorPaintStyle style)
        => style.StrokeWidth > 0 &&
            (style.NonScalingStroke ||
             style.DashArray.Count > 0 ||
             style.LineCap != MarkdownVectorLineCap.Flat ||
             style.LineJoin != MarkdownVectorLineJoin.Miter);

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

    private static Vector2 ToVector2(MarkdownVectorPoint point) => new(point.X, point.Y);
}
