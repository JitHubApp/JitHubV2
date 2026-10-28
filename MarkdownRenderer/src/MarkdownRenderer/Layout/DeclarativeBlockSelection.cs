using System;
using System.Collections.Generic;
using Markdig.Syntax;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Selects extension output by exact parsed-node identity and suppresses a
/// complete fragment once any hosted primitive has requested native fallback.
/// </summary>
internal static class DeclarativeBlockSelection
{
    internal static bool TryGetContent(
        MarkdownRenderer.Document.MarkdownDocument document,
        Block block,
        IReadOnlySet<HostedElementFallbackKey> hostedElementFallbacks,
        out MarkdownContentFragment? fragment)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(hostedElementFallbacks);

        if (!document.TryGetBlockExtensionContent(block, out fragment) ||
            fragment is null ||
            hostedElementFallbacks.Contains(new HostedElementFallbackKey(fragment)))
        {
            fragment = null;
            return false;
        }

        return true;
    }
}
