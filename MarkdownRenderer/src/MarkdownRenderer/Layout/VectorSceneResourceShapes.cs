using System;
using System.Threading;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Owns device-dependent resources for one immutable vector scene. Both inline
/// formulas and block diagrams use this implementation so their paint behavior
/// cannot drift.
/// </summary>

/// <summary>
/// Counts the native geometry, text-layout and stroke-style slots a vector
/// scene needs, and the saturating resource weight used for admission.
/// </summary>
internal static class VectorSceneResourceShapes
{
    internal static bool RequiresGeometry(MarkdownVectorCommandKind kind)
        => kind is MarkdownVectorCommandKind.FillPath or
            MarkdownVectorCommandKind.StrokePath or
            MarkdownVectorCommandKind.DrawPath or
            MarkdownVectorCommandKind.BeginClip;

    internal static bool RequiresTextLayout(MarkdownVectorCommand command)
        => command.Kind == MarkdownVectorCommandKind.DrawText &&
            command.Text is not null && command.TextStyle is not null;

    internal static long EstimateNativeResourceWeightUnits(
        MarkdownVectorScene scene,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);
        return CountNativeResourceShape(scene, cancellationToken, calculateWeight: true).WeightUnits;
    }

    internal static VectorSceneNativeResourceShape CountNativeResourceShape(
        MarkdownVectorScene scene,
        CancellationToken cancellationToken,
        bool calculateWeight)
    {
        int geometries = 0;
        int textLayouts = 0;
        int strokeStyles = 0;
        int transformDepth = 0;
        int clipDepth = 0;
        int maximumTransformDepth = 0;
        int maximumClipDepth = 0;
        long weightUnits = calculateWeight ? 1024 : 0; // Per-scene fixed custody and paint overhead.
        if (calculateWeight)
        {
            weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(scene.Commands.Count, 128));
            weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(scene.Semantics.Count, 256));
            weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(scene.Links.Count, 256));
        }

        if (calculateWeight)
        {
            foreach (MarkdownVectorSemanticItem semantic in scene.Semantics)
            {
                cancellationToken.ThrowIfCancellationRequested();
                weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(semantic.Name?.Length ?? 0, 16));
                weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(semantic.Description?.Length ?? 0, 16));
            }
            foreach (MarkdownVectorLinkAction link in scene.Links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(link.Target?.Length ?? 0, 16));
                weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(link.Action?.Length ?? 0, 16));
            }
        }

        foreach (MarkdownVectorCommand command in scene.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RequiresGeometry(command.Kind))
            {
                geometries++;
                if (calculateWeight)
                {
                    weightUnits = SaturatingAdd(weightUnits, 2048);
                    weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(command.Path.Count, 192));
                    // Cubics and quadratics carry more control-point work than a
                    // straight segment; weight them without inspecting coordinates.
                    for (int operationIndex = 0; operationIndex < command.Path.Count; operationIndex++)
                    {
                        if ((operationIndex & 0xFF) == 0)
                            cancellationToken.ThrowIfCancellationRequested();
                        MarkdownVectorPathOperationKind kind = command.Path[operationIndex].Kind;
                        if (kind is MarkdownVectorPathOperationKind.QuadraticBezier)
                            weightUnits = SaturatingAdd(weightUnits, 128);
                        else if (kind is MarkdownVectorPathOperationKind.CubicBezier)
                            weightUnits = SaturatingAdd(weightUnits, 256);
                    }
                }
            }
            if (RequiresTextLayout(command))
            {
                textLayouts++;
                if (calculateWeight)
                {
                    weightUnits = SaturatingAdd(weightUnits, 4096);
                    weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(command.Text!.Length, 64));
                    weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(command.TextStyle!.FontFamily.Length, 16));
                }
            }
            if (command.Style is { } style && RequiresStrokeStyle(style))
            {
                strokeStyles++;
                if (calculateWeight)
                {
                    weightUnits = SaturatingAdd(weightUnits, 2048);
                    weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(style.DashArray.Count, 128));
                }
            }
            // MarkdownVectorScene validates complete, properly nested groups
            // and clips before publication. Group and clip depths are separate
            // because each uses a different paint stack, even when interleaved.
            switch (command.Kind)
            {
                case MarkdownVectorCommandKind.BeginGroup:
                    maximumTransformDepth = Math.Max(maximumTransformDepth, ++transformDepth);
                    break;
                case MarkdownVectorCommandKind.EndGroup:
                    transformDepth--;
                    break;
                case MarkdownVectorCommandKind.BeginClip:
                    maximumClipDepth = Math.Max(maximumClipDepth, ++clipDepth);
                    break;
                case MarkdownVectorCommandKind.EndClip:
                    clipDepth--;
                    break;
            }
        }
        if (calculateWeight)
        {
            weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(maximumTransformDepth, 512));
            if (textLayouts > 0)
                weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(maximumTransformDepth, 24));
            weightUnits = SaturatingAdd(weightUnits, SaturatingMultiply(maximumClipDepth, 4096));
        }
        return new VectorSceneNativeResourceShape(geometries, textLayouts, strokeStyles,
            maximumTransformDepth, maximumClipDepth, weightUnits);
    }

    internal static long SaturatingMultiply(int count, int weight)
        => count <= 0 ? 0 : (long)count * weight;

    internal static long SaturatingAdd(long left, long right)
        => right > long.MaxValue - left ? long.MaxValue : left + right;

    internal static bool RequiresStrokeStyle(MarkdownVectorPaintStyle style)
        => style.StrokeWidth > 0 &&
           (style.NonScalingStroke ||
            style.DashArray.Count > 0 ||
            style.LineCap != MarkdownVectorLineCap.Flat ||
            style.LineJoin != MarkdownVectorLineJoin.Miter);
}
