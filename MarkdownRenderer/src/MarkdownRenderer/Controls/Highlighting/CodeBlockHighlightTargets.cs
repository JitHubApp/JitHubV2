using System.Collections.Generic;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Highlighting;

/// <summary>
/// Finds the code blocks a highlight pass targets in a band of a snapshot,
/// and the cache key each block's highlight is stored under.
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
}
