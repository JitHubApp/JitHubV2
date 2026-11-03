using System.Collections.Generic;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Highlighting;

/// <summary>
/// Finds the code blocks a highlight pass targets in a band of a snapshot,
/// the cache key each block's highlight is stored under, and applies a
/// result too large for the cache to its blocks directly.
/// </summary>
internal static class CodeBlockHighlightTargets
{
    /// <summary>True when <paramref name="block"/> intersects the band from <paramref name="top"/> to <paramref name="bottom"/>.</summary>
    internal static bool IsInBand(
        LayoutSnapshot snapshot,
        CodeBlockBox block,
        double top,
        double bottom)
    {
        Rect bounds = snapshot.ToLogicalRectForBlockIndexUnderPaint(block.BlockIndex, block.Bounds);
        return bounds.Bottom >= top && bounds.Top <= bottom;
    }

    /// <summary>The code blocks in <paramref name="blocks"/>, including those nested in list items and stacks.</summary>
    internal static IEnumerable<CodeBlockBox> EnumerateCodeBlocks(
        IReadOnlyList<BlockBox> blocks)
    {
        foreach (var block in blocks)
        {
            foreach (var codeBlock in EnumerateCodeBlocks(block))
                yield return codeBlock;
        }
    }

    private static IEnumerable<CodeBlockBox> EnumerateCodeBlocks(BlockBox block)
    {
        switch (block)
        {
            case CodeBlockBox codeBlock:
                yield return codeBlock;
                break;
            case ListItemBox listItem:
                foreach (var item in EnumerateCodeBlocks(listItem.Marker))
                    yield return item;
                foreach (var item in EnumerateCodeBlocks(listItem.Content))
                    yield return item;
                break;
            case StackBox stack:
                foreach (var child in stack.Children)
                {
                    foreach (var item in EnumerateCodeBlocks(child))
                        yield return item;
                }
                break;
        }
    }

    /// <summary>The cache key of <paramref name="block"/>'s highlight for one theme variant and provider revision.</summary>
    internal static CodeBlockHighlightCacheKey CreateCacheKey(
        CodeBlockBox block,
        MarkdownThemeVariant variant,
        int providerIdentity,
        int providerRevision)
        => new(block.CodeLanguage, block.Metadata.CodeTextHash, block.CodeText.Length, variant, providerIdentity, providerRevision);

    /// <summary>The highlight theme variant of <paramref name="theme"/>, from its contrast mode and surface luminance.</summary>
    internal static MarkdownThemeVariant ResolveThemeVariant(ThemeSnapshot theme)
    {
        if (theme.IsHighContrast)
            return MarkdownThemeVariant.HighContrast;

        var bg = theme.SurfaceColor;
        double luminance = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
        return luminance < 0.5 ? MarkdownThemeVariant.Dark : MarkdownThemeVariant.Light;
    }

    /// <summary>
    /// Applies a result too large for the cache straight to the code blocks
    /// of <paramref name="snapshot"/> it was requested for: every match when
    /// no performance session is attached, else the matches in the current
    /// directional band. <paramref name="snapshotBusy"/> reports a lease or
    /// band that is not available yet, so the request stays retryable.
    /// </summary>
    internal static bool ApplyUncachedResult(
        ICodeBlockHighlightingHost host,
        MarkdownViewport viewport,
        LayoutSnapshot snapshot,
        CodeBlockHighlightCacheKey key,
        CodeBlockHighlightResult result,
        out bool snapshotBusy)
    {
        snapshotBusy = false;
        if (!snapshot.TryBeginInteraction())
        {
            snapshotBusy = true;
            return false;
        }

        try
        {
            IReadOnlyList<BlockBox> inspectedBlocks;
            IEnumerable<CodeBlockBox> candidates;
            LazyLayoutBand band = default;
            if (host.PerformanceSession is null)
            {
                inspectedBlocks = snapshot.GetMeasuredTopLevelBlocks();
                candidates = EnumerateCodeBlocks(inspectedBlocks);
            }
            else
            {
                viewport.TryGet(out double viewportTop, out double viewportHeight, out _);
                band = LazyLayoutBand.FromDirectionalViewport(
                    viewportTop,
                    viewportHeight,
                    host.PerformanceSession.Options.LookAheadViewports,
                    host.LazyScrollVelocityPixelsPerSecond);
                if (!snapshot.TryGetMeasuredTopLevelBlocksInBandForSceneProbe(
                        band.Top, band.Bottom, out inspectedBlocks))
                {
                    // The snapshot may still be measuring this band or waiting
                    // for deferred native scenes. Keep the oversized result
                    // retryable after the in-flight key is released.
                    snapshotBusy = true;
                    return false;
                }

                candidates = EnumerateCodeBlocks(inspectedBlocks);
            }

            bool changed = false;
            foreach (var candidate in candidates)
            {
                if (host.PerformanceSession is not null &&
                    !IsInBand(snapshot, candidate, band.Top, band.Bottom))
                    continue;
                if (CreateCacheKey(candidate, key.ThemeVariant, key.ProviderIdentity, key.ProviderRevision).Equals(key))
                    changed |= candidate.ApplySyntaxHighlighting(key, result.Spans);
            }

            return changed;
        }
        finally
        {
            snapshot.EndInteraction();
        }
    }
}
