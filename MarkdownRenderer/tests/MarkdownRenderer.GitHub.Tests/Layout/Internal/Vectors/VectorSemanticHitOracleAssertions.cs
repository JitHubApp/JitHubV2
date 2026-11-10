using System.Runtime.CompilerServices;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions.Internal;
using MarkdownRenderer.Extensions.Vectors;
using MarkdownRenderer.Layout.Internal.Vectors;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Vectors;

/// <summary>
/// Computes the expected vector semantic hit from the native scene (deepest,
/// then smallest, eligible semantic) and checks that a vector box answers
/// selection, text and link hit tests the same way.
/// </summary>
internal static class VectorSemanticHitOracleAssertions
{
    internal enum SemanticHitKind
    {
        Selectable,
        Linked,
        Text,
    }

    internal readonly record struct SemanticHitOracle(
        int SelectableIndex,
        int TextIndex,
        int LinkedIndex,
        DocumentPosition SelectablePosition,
        DocumentPosition TextPosition,
        MarkdownVectorLinkAction? LinkAction);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static SemanticHitOracle CaptureNativeSemanticHitOracle(
        VectorSceneBox block,
        Point point)
    {
        MarkdownVectorScene scene = block.Scene;
        int selectableIndex = FindNativeSemanticHit(scene, block, point, SemanticHitKind.Selectable);
        int textIndex = FindNativeSemanticHit(scene, block, point, SemanticHitKind.Text);
        int linkedIndex = FindNativeSemanticHit(scene, block, point, SemanticHitKind.Linked);
        return new SemanticHitOracle(
            selectableIndex,
            textIndex,
            linkedIndex,
            NativePositionForHit(block, point, selectableIndex),
            NativePositionForHit(block, point, textIndex),
            linkedIndex >= 0
                ? scene.GetLinkAction(linkedIndex)
                : null);
    }

    internal static int FindNativeSemanticHit(
        MarkdownVectorScene scene,
        VectorSceneBox block,
        Point point,
        SemanticHitKind kind)
    {
        if (!block.ContainsSceneViewport(point))
            return -1;

        int bestIndex = -1;
        int bestDepth = -1;
        double bestArea = double.PositiveInfinity;
        for (int index = 0; index < scene.Semantics.Count; index++)
        {
            MarkdownVectorSemanticItem semantic = scene.Semantics[index];
            bool eligible = kind switch
            {
                SemanticHitKind.Selectable => MarkdownVectorSemanticPolicy.IsSelectable(semantic.Flags),
                SemanticHitKind.Linked => MarkdownVectorSemanticPolicy.IsInvokable(
                    semantic.Flags,
                    scene.GetLinkAction(index) is not null),
                SemanticHitKind.Text => MarkdownVectorSemanticPolicy.IsExposed(semantic.Flags) &&
                                        !string.IsNullOrWhiteSpace(semantic.Name) &&
                                        !(semantic.ParentIndex < 0 &&
                                          semantic.Role == MarkdownVectorSemanticRole.Diagram),
                _ => false,
            };
            if (!eligible)
                continue;

            Rect bounds = block.GetSemanticBounds(index);
            if (!bounds.Contains(point))
                continue;

            int depth = scene.GetSemanticDepth(index);
            double area = bounds.Width * bounds.Height;
            if (depth < bestDepth || (depth == bestDepth && area > bestArea))
                continue;

            bestIndex = index;
            bestDepth = depth;
            bestArea = area;
        }

        return bestIndex;
    }

    internal static DocumentPosition NativePositionForHit(
        VectorSceneBox block,
        Point point,
        int semanticIndex)
    {
        if (semanticIndex < 0)
            return DocumentPosition.Zero;

        Rect bounds = block.GetSemanticBounds(semanticIndex);
        return new DocumentPosition(
            block.BlockIndex,
            semanticIndex,
            point.X <= bounds.Left + bounds.Width / 2 ? 0 : 1);
    }

    internal static void AssertVectorHitParity(
        VectorSceneBox block,
        Point point,
        SemanticHitOracle expected)
    {
        bool selectableHit = block.HitTest(point, out DocumentPosition selectablePosition);
        Assert.Equal(expected.SelectableIndex >= 0, selectableHit);
        Assert.Equal(expected.SelectablePosition, selectablePosition);
        Assert.Equal(expected.SelectableIndex >= 0, block.IsSelectableAt(point));

        bool textHit = block.TryGetTextPositionAt(point, out DocumentPosition textPosition);
        Assert.Equal(expected.TextIndex >= 0, textHit);
        Assert.Equal(expected.TextPosition, textPosition);

        bool linkedHit = block.TryGetLinkAt(point, out MarkdownVectorLinkAction? link);
        Assert.Equal(expected.LinkedIndex >= 0, linkedHit);
        Assert.Same(expected.LinkAction, link);
    }
}
