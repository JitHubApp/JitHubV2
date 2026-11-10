using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer.Accessibility.Internal.Semantics;

/// <summary>Attaches a vector scene's exposed semantic nodes under their nearest exposed ancestors.</summary>
internal static class MarkdownVectorSemanticTree
{
    internal static void AttachVectorSemanticNodes(
        IReadOnlyList<MarkdownVectorSemanticItem> semantics,
        IReadOnlyList<MarkdownSemanticNode?> semanticNodes,
        MarkdownSemanticNode sceneNode,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int semanticCount = semantics.Count;
        if (semanticCount == 0)
            return;

        // Parent indices may point forward. Cache each item's nearest exposed
        // ancestor (including itself) while resolving parents iteratively, so
        // deep runs of omitted/decorative items are walked only once. The final
        // loop still attaches in semantic-index order, preserving sibling order.
        // MarkdownVectorScene has already validated parent indices and rejected cycles.
        const int UnresolvedAncestor = int.MinValue;
        var nearestExposedAncestors = new int[semanticCount];
        Array.Fill(nearestExposedAncestors, UnresolvedAncestor);
        var path = new int[semanticCount];

        for (int i = 0; i < semanticCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MarkdownSemanticNode? child = semanticNodes[i];
            if (child is null)
                continue;

            int current = semantics[i].ParentIndex;
            int pathCount = 0;
            int nearestExposedAncestor = -1;
            while ((uint)current < (uint)semanticCount)
            {
                if ((pathCount & 0x3F) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                int cachedAncestor = nearestExposedAncestors[current];
                if (cachedAncestor != UnresolvedAncestor)
                {
                    nearestExposedAncestor = cachedAncestor;
                    break;
                }

                if (semanticNodes[current] is not null)
                {
                    nearestExposedAncestor = current;
                    nearestExposedAncestors[current] = current;
                    break;
                }

                path[pathCount++] = current;
                current = semantics[current].ParentIndex;
            }

            int unwindCount = 0;
            while (pathCount > 0)
            {
                if ((unwindCount++ & 0x3F) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                int ancestorIndex = path[--pathCount];
                if (semanticNodes[ancestorIndex] is not null)
                    nearestExposedAncestor = ancestorIndex;
                nearestExposedAncestors[ancestorIndex] = nearestExposedAncestor;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)nearestExposedAncestor < (uint)semanticCount &&
                semanticNodes[nearestExposedAncestor] is { } parentNode)
            {
                parentNode.Add(child);
            }
            else
            {
                sceneNode.Add(child);
            }
        }
    }
}
