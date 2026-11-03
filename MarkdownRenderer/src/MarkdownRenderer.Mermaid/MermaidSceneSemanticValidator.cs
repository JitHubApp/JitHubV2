using static MarkdownRenderer.Mermaid.MmirPrimitives;

namespace MarkdownRenderer.Mermaid;

/// <summary>
/// Validates decoded semantic references: source mappings must name a real
/// semantic item, and the parent graph must be acyclic and within MaxDepth.
/// </summary>
internal static class MermaidSceneSemanticValidator
{
    internal static bool ValidateMappingSemanticReferences(
        MermaidSceneSourceMapping[] mappings,
        int semanticCount,
        CancellationToken cancellationToken,
        out MermaidSceneDecodeResult? failure)
    {
        for (int i = 0; i < mappings.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            if (mappings[i].SemanticIndex < -1 || mappings[i].SemanticIndex >= semanticCount)
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidReference, i, "A source mapping references an invalid semantic item.");
                return false;
            }
        }

        failure = null;
        return true;
    }

    internal static bool ValidateSemanticTree(
        MermaidSemanticItem[] semantics,
        int maxDepth,
        CancellationToken cancellationToken,
        out MermaidSceneDecodeResult? failure,
        out int parentTraversalCount)
    {
        // Zero means unresolved, -1 means active in the current traversal, and a
        // positive value is the memoized one-based depth. The traversal buffer is
        // bounded by MaxDepth; a longer unresolved path is already over budget.
        var resolvedDepths = new int[semantics.Length];
        var traversal = new int[Math.Min(semantics.Length, maxDepth)];
        parentTraversalCount = 0;

        for (int i = 0; i < semantics.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            if (resolvedDepths[i] > 0)
            {
                continue;
            }

            int current = i;
            int traversalLength = 0;
            while (current >= 0 && resolvedDepths[current] == 0)
            {
                ThrowIfCancellationRequestedPeriodically(cancellationToken, parentTraversalCount);
                if (traversalLength == traversal.Length)
                {
                    failure = Failure(MermaidSceneDecodeStatus.BudgetExceeded, i, "The semantic tree exceeds MaxDepth.");
                    return false;
                }

                resolvedDepths[current] = -1;
                traversal[traversalLength++] = current;
                current = semantics[current].ParentIndex;
                parentTraversalCount++;
            }

            if (current >= 0 && resolvedDepths[current] < 0)
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidReference, i, "The semantic parent graph contains a cycle.");
                return false;
            }

            int depth = current >= 0 ? resolvedDepths[current] : 0;
            while (traversalLength > 0)
            {
                ThrowIfCancellationRequestedPeriodically(cancellationToken, traversalLength);
                if (depth >= maxDepth)
                {
                    failure = Failure(MermaidSceneDecodeStatus.BudgetExceeded, i, "The semantic tree exceeds MaxDepth.");
                    return false;
                }

                int semanticIndex = traversal[--traversalLength];
                resolvedDepths[semanticIndex] = ++depth;
            }
        }

        failure = null;
        return true;
    }
}
