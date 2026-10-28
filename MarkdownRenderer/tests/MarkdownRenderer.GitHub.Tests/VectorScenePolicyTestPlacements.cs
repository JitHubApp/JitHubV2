using System;
using System.Collections.Generic;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Windows.Foundation;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Builds the same immutable geometry context that LayoutSnapshot supplies to
/// scene residency. Direct policy tests use small, already-arranged roots; this
/// helper rejects layouts that cannot be represented by the production flow
/// recurrence instead of silently inventing a mismatched viewport index.
/// </summary>
internal sealed class VectorScenePolicyTestPlacements
{
    private VectorScenePolicyTestPlacements(
        LayoutPlacementGeneration generation,
        IReadOnlyDictionary<int, int> ordinalByBlockIndex,
        IReadOnlyList<int> ordinals)
    {
        Generation = generation;
        OrdinalByBlockIndex = ordinalByBlockIndex;
        Ordinals = ordinals;
    }

    internal LayoutPlacementGeneration Generation { get; }
    internal IReadOnlyDictionary<int, int> OrdinalByBlockIndex { get; }
    internal IReadOnlyList<int> Ordinals { get; }

    internal static VectorScenePolicyTestPlacements Create(IReadOnlyList<BlockBox> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var inputs = new LayoutPlacementInput[roots.Count];
        var ordinalByBlockIndex = new Dictionary<int, int>(roots.Count);
        var ordinals = new int[roots.Count];
        float initialTop = roots.Count == 0 ? 0 : CheckedFloat(roots[0].Bounds.Top);
        float blockSpacing = 0;
        double documentWidth = 1;
        float previousBottom = initialTop;

        for (int ordinal = 0; ordinal < roots.Count; ordinal++)
        {
            BlockBox root = roots[ordinal] ?? throw new ArgumentException(
                "Placement roots cannot be null.", nameof(roots));
            Rect bounds = root.Bounds;
            float x = CheckedFloat(bounds.X);
            float top = CheckedFloat(bounds.Top);
            float width = CheckedFloat(bounds.Width);
            float height = CheckedFloat(bounds.Height);
            if (width < 0 || height < 0 || !ordinalByBlockIndex.TryAdd(root.BlockIndex, ordinal))
                throw new ArgumentException("Placement roots require finite geometry and unique block indices.", nameof(roots));

            if (ordinal == 1)
                blockSpacing = top - previousBottom;
            else if (ordinal > 1 && top != previousBottom + blockSpacing)
                throw new ArgumentException(
                    "Direct policy fixtures must follow one exact top-level spacing recurrence.", nameof(roots));

            inputs[ordinal] = new LayoutPlacementInput(
                x, top, x, width, height, measured: true, sharesFlowWithNext: false);
            ordinals[ordinal] = ordinal;
            documentWidth = System.Math.Max(documentWidth, bounds.Right);
            previousBottom = top + height;
        }

        LayoutPlacementGeneration generation = LayoutPlacementGeneration.Create(
            inputs,
            initialTop,
            blockSpacing,
            bottomPadding: 0,
            documentWidth: documentWidth,
            revision: 0);
        for (int ordinal = 0; ordinal < roots.Count; ordinal++)
        {
            if (generation.GetPlacement(ordinal).Top != CheckedFloat(roots[ordinal].Bounds.Top))
                throw new ArgumentException(
                    "Generated test placement does not match the arranged root bounds.", nameof(roots));
        }

        return new VectorScenePolicyTestPlacements(
            generation, ordinalByBlockIndex, ordinals);
    }

    private static float CheckedFloat(double value)
    {
        if (!double.IsFinite(value) || value < -float.MaxValue || value > float.MaxValue)
            throw new ArgumentException("Placement geometry must fit finite single-precision values.");
        float result = (float)value;
        if (!float.IsFinite(result))
            throw new ArgumentException("Placement geometry must fit finite single-precision values.");
        return result;
    }
}
