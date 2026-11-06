using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Creates and releases the Win2D resources a vector scene paints with:
/// stroke styles, path figures, slot arrays and the scene-to-canvas conversions.
/// </summary>
internal static class VectorSceneNativeFactory
{
    internal static void DisposeDeviceResources(
        ref CanvasGeometry?[]? geometries,
        ref CanvasTextLayout?[]? textLayouts,
        ref CanvasStrokeStyle?[]? strokeStyles,
        ref CanvasActiveLayer?[]? clipStack,
        ref CanvasTextFormat? pendingTextFormat,
        ref CanvasPathBuilder? pendingPathBuilder,
        Action<IDisposable> disposer,
        bool preserveGeometryForUncertainClips)
    {
        int count = Math.Max(geometries?.Length ?? 0,
            Math.Max(textLayouts?.Length ?? 0, strokeStyles?.Length ?? 0));
        BlockBoxCleanupScope cleanup = default;
        for (int i = 0; i < count; i++)
        {
            if (!preserveGeometryForUncertainClips && geometries is not null && i < geometries.Length)
                cleanup.TryDisposeAndClear(ref geometries[i], disposer);
            if (textLayouts is not null && i < textLayouts.Length)
                cleanup.TryDisposeAndClear(ref textLayouts[i], disposer);
            if (strokeStyles is not null && i < strokeStyles.Length)
                cleanup.TryDisposeAndClear(ref strokeStyles[i], disposer);
        }

        if (preserveGeometryForUncertainClips || !AreAllResourcesCleared(clipStack))
            cleanup.RecordFailure(new InvalidOperationException(
                "An uncertain vector active layer and its clip geometry require terminal quarantine, not post-session disposal."));
        cleanup.TryDisposeAndClear(ref pendingTextFormat, disposer);
        cleanup.TryDisposeAndClear(ref pendingPathBuilder, disposer);

        if (AreAllResourcesCleared(geometries))
            geometries = null;
        if (AreAllResourcesCleared(textLayouts))
            textLayouts = null;
        if (AreAllResourcesCleared(strokeStyles))
            strokeStyles = null;
        if (AreAllResourcesCleared(clipStack))
            clipStack = null;

        cleanup.ThrowIfAny("disposing vector drawing resources");
    }

    internal static bool AreAllResourcesCleared<T>(T?[]? resources)
        where T : class, IDisposable
    {
        if (resources is null)
            return true;

        for (int i = 0; i < resources.Length; i++)
        {
            if (resources[i] is not null)
                return false;
        }

        return true;
    }

    internal static T?[] CreateSlots<T>(int count) where T : class
        => count == 0 ? Array.Empty<T?>() : new T?[count];

    internal static Vector2 ResolveTextOrigin(
        MarkdownVectorCommand command,
        CanvasTextLayout layout)
    {
        float baseline = layout.LineMetrics.Length > 0
            ? layout.LineMetrics[0].Baseline
            : command.TextStyle!.FontSize * 0.8f;
        float width = (float)Math.Max(0, layout.DrawBounds.Width);
        float x = command.TextStyle!.Alignment switch
        {
            MarkdownVectorTextAlignment.Center => command.Start.X - width / 2f,
            MarkdownVectorTextAlignment.End => command.Start.X - width,
            _ => command.Start.X,
        };
        return new Vector2(x, command.Start.Y - baseline);
    }

    internal static CanvasStrokeStyle CreateStrokeStyle(MarkdownVectorPaintStyle style, NativeResourceRetirement owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        NativeResourceRetirementRecord retirement = owner.Reserve(
            resourceGroupCount: 0, temporarySlotCount: 1);
        CanvasStrokeStyle? result = new CanvasStrokeStyle();
        try
        {
            ConfigureStrokeStyle(result, style);
            return result;
        }
        catch (Exception preparationFailure)
        {
            BlockBoxCleanupScope cleanup = default;
            cleanup.TryDisposeAndClear(ref result, retirement.Disposer);
            retirement.SetTemporary(0, result);
            retirement.Publish();
            try { cleanup.ThrowIfAny("disposing unpublished vector stroke style"); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Vector stroke style preparation and cleanup both failed.",
                    preparationFailure, cleanupFailure);
            }
            throw;
        }
    }

    internal static void ConfigureStrokeStyle(CanvasStrokeStyle result, MarkdownVectorPaintStyle style)
    {
        result.StartCap = ToCap(style.LineCap);
        result.EndCap = ToCap(style.LineCap);
        result.DashCap = ToCap(style.LineCap);
        result.LineJoin = ToJoin(style.LineJoin);
        result.DashOffset = style.DashOffset;
        result.TransformBehavior = style.NonScalingStroke
            ? CanvasStrokeTransformBehavior.Fixed
            : CanvasStrokeTransformBehavior.Normal;
        if (style.DashArray.Count > 0)
        {
            var dashes = new float[style.DashArray.Count];
            for (int i = 0; i < dashes.Length; i++)
                dashes[i] = style.DashArray[i];
            result.CustomDashStyle = dashes;
        }
    }

    internal static CanvasCapStyle ToCap(MarkdownVectorLineCap value) => value switch
    {
        MarkdownVectorLineCap.Round => CanvasCapStyle.Round,
        MarkdownVectorLineCap.Square => CanvasCapStyle.Square,
        _ => CanvasCapStyle.Flat,
    };

    internal static CanvasLineJoin ToJoin(MarkdownVectorLineJoin value) => value switch
    {
        MarkdownVectorLineJoin.Round => CanvasLineJoin.Round,
        MarkdownVectorLineJoin.Bevel => CanvasLineJoin.Bevel,
        _ => CanvasLineJoin.Miter,
    };

    internal static Matrix3x2 ToMatrix(MarkdownVectorTransform value) =>
        new(value.M11, value.M12, value.M21, value.M22, value.M31, value.M32);

    /// <summary>Replays a vector path's operations into a path builder, closing any open figure.</summary>
    internal static void AddPathOperations(
        CanvasPathBuilder builder,
        IReadOnlyList<MarkdownVectorPathOperation> operations,
        CancellationToken cancellationToken)
    {
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
    }

    internal static Vector2 ToVector2(MarkdownVectorPoint point) => new(point.X, point.Y);

    internal static Rect ToRect(MarkdownVectorRectangle rectangle) =>
        new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
}
