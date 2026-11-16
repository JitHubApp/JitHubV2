using System;
using System.Threading;
using MarkdownRenderer.Extensions.Vectors;
using MarkdownRenderer.Layout.Internal.Vectors.Drawing;

namespace MarkdownRenderer.Layout.Internal.Vectors;

/// <summary>
/// Indexes a vector scene's commands by semantic item: the first text command,
/// the single unambiguous opaque surface background, and the reference font size.
/// </summary>
internal static class VectorSceneSemanticCommandIndex
{
    internal const int AmbiguousBackgroundCommandIndex = -2;

    internal static (int[] Text, int[] Background, float ReferenceFontSize) IndexSemanticCommands(
        MarkdownVectorScene scene,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int semanticCount = scene.Semantics.Count;
        if (semanticCount == 0 && scene.ReferenceFontSize > 0)
        {
            return (Array.Empty<int>(), Array.Empty<int>(), scene.ReferenceFontSize);
        }

        var textIndices = semanticCount == 0 ? Array.Empty<int>() : new int[semanticCount];
        var backgroundIndices = semanticCount == 0 ? Array.Empty<int>() : new int[semanticCount];
        var ambiguousBackgrounds = semanticCount == 0 ? Array.Empty<bool>() : new bool[semanticCount];
        Array.Fill(textIndices, -1);
        Array.Fill(backgroundIndices, -1);
        double weightedTextSize = 0;
        long textUnits = 0;
        bool inferReferenceFontSize = scene.ReferenceFontSize <= 0;
        for (int i = 0; i < scene.Commands.Count; i++)
        {
            if ((i & 0x3F) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            MarkdownVectorCommand command = scene.Commands[i];
            if (inferReferenceFontSize &&
                command.Kind == MarkdownVectorCommandKind.DrawText &&
                command.TextStyle is { FontSize: > 0 } textStyle)
            {
                int weight = Math.Max(1, command.Text?.Length ?? 0);
                weightedTextSize += textStyle.FontSize * weight;
                textUnits += weight;
            }

            if ((uint)command.SemanticIndex >= (uint)textIndices.Length)
                continue;

            int semanticIndex = command.SemanticIndex;
            if (command.Kind == MarkdownVectorCommandKind.DrawText &&
                command.Style is not null &&
                command.TextStyle is not null &&
                textIndices[semanticIndex] < 0)
            {
                textIndices[semanticIndex] = i;
            }

            if (!TryGetVisibleBackgroundStyle(
                    command,
                    scene.Semantics[semanticIndex].Role,
                    out MarkdownVectorPaintStyle style))
            {
                continue;
            }

            if (ambiguousBackgrounds[semanticIndex])
                continue;
            if (backgroundIndices[semanticIndex] < 0)
            {
                backgroundIndices[semanticIndex] = i;
                continue;
            }

            MarkdownVectorPaintStyle existing =
                scene.Commands[backgroundIndices[semanticIndex]].Style!;
            if (!HaveEquivalentFill(existing, style))
            {
                ambiguousBackgrounds[semanticIndex] = true;
                backgroundIndices[semanticIndex] = AmbiguousBackgroundCommandIndex;
            }
        }

        float referenceFontSize = scene.ReferenceFontSize > 0
            ? scene.ReferenceFontSize
            : textUnits > 0
                ? (float)(weightedTextSize / textUnits)
                : 0;
        return (textIndices, backgroundIndices, referenceFontSize);
    }

    internal static bool TryGetVisibleBackgroundStyle(
        MarkdownVectorCommand command,
        MarkdownVectorSemanticRole semanticRole,
        out MarkdownVectorPaintStyle style)
    {
        if (command.Kind is not (MarkdownVectorCommandKind.DrawRectangle or
            MarkdownVectorCommandKind.DrawEllipse or
            MarkdownVectorCommandKind.DrawPath) ||
            command.Style is not { Opacity: >= 0.999f } candidate)
        {
            style = null!;
            return false;
        }

        style = candidate;
        bool opaque = style.FillArgb is not { } fill || (fill >> 24) == 0xFF;
        MarkdownVectorPaintRole highContrastRole = style.HighContrastFillRole ??
            (style.FillRole == MarkdownVectorPaintRole.Surface
                ? MarkdownVectorPaintRole.Surface
                : VectorScenePaintResolver.InferFillRole(command, style, semanticRole));
        return opaque && highContrastRole == MarkdownVectorPaintRole.Surface;
    }

    internal static bool HaveEquivalentFill(
        MarkdownVectorPaintStyle left,
        MarkdownVectorPaintStyle right) =>
        left.FillArgb == right.FillArgb &&
        left.FillRole == right.FillRole &&
        left.HighContrastFillRole == right.HighContrastFillRole &&
        Math.Abs(left.Opacity - right.Opacity) < 0.001f;
}
