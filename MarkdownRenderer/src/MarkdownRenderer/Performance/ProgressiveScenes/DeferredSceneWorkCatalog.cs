using System;
using System.Collections.Generic;
using MarkdownRenderer.Extensions;
using RendererDocument = MarkdownRenderer.Document.MarkdownDocument;

namespace MarkdownRenderer.Performance.ProgressiveScenes;

/// <summary>
/// Reads the private deferred-scene markers that built-in scene feature packs
/// attach to their readable fallback content.
/// </summary>
internal static class DeferredSceneWorkCatalog
{
    // Built-in scene feature packs use this private, serializable content
    // attribute to mark a readable fallback. The marker value is
    // "<exact syntax kind>|<document-unique token>". It is metadata only; the
    // source text and source span remain the fallback and diagnostic authority.

    private const char MarkerSeparator = '|';

    internal static bool ContainsDeferredSceneFallback(RendererDocument document)
    {
        foreach (MarkdownContentFragment fragment in document.ExtensionBlockNodeContent.Values)
        {
            bool found = false;
            Collect(fragment.Items, content => found = true);
            if (found)
                return true;
        }

        foreach (MarkdownContentFragment fragment in document.ExtensionInlineNodeContent.Values)
        {
            bool found = false;
            Collect(fragment.Items, content => found = true);
            if (found)
                return true;
        }

        return false;
    }

    internal static void Collect(
        IReadOnlyList<MarkdownContent> content,
        Action<MarkdownContent> onCandidate)
    {
        foreach (MarkdownContent item in content)
        {
            if (item.Attributes.ContainsKey(MarkdownContentAttributes.DeferredScene))
            {
                onCandidate(item);
            }

            if (item.Children.Count > 0)
                Collect(item.Children, onCandidate);
        }
    }

    internal static bool TryReadMarker(string marker, string syntaxKind, out string token)
    {
        token = string.Empty;
        int separator = marker.IndexOf(MarkerSeparator);
        if (separator <= 0 || separator == marker.Length - 1 ||
            !string.Equals(marker[..separator], syntaxKind, StringComparison.Ordinal))
        {
            return false;
        }

        token = marker[(separator + 1)..];
        return true;
    }
}
