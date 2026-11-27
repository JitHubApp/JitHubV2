using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Layout.Internal.LazyLayout;

/// <summary>
/// Splits a realization band into the order its parts should be measured:
/// the visible viewport first, then the wider (leading) side and then the
/// narrower (trailing) side, each in chunks that start nearest the viewport.
/// </summary>
/// <remarks>
/// Each part is measured under its own hold of the layout gate, so painting
/// can read the geometry between parts and a superseding request can stop
/// look-ahead work after any part without losing what was measured.
/// </remarks>
internal static class LazyBandRealizationOrder
{
    /// <summary>The smallest chunk worth a separate gate hold, in DIPs.</summary>
    internal const double MinimumChunkHeight = 256;

    /// <summary>
    /// Returns the parts of <paramref name="band"/> in measuring order. The
    /// first part is always the visible band clamped into
    /// <paramref name="band"/>; look-ahead chunks are at most
    /// <paramref name="chunkHeight"/> tall.
    /// </summary>
    internal static IReadOnlyList<LazyLayoutBand> Plan(
        LazyLayoutBand band,
        LazyLayoutBand visible,
        double chunkHeight)
    {
        if (!double.IsFinite(chunkHeight) || chunkHeight < MinimumChunkHeight)
            chunkHeight = MinimumChunkHeight;

        double visibleTop = Math.Clamp(visible.Top, band.Top, band.Bottom);
        double visibleBottom = Math.Clamp(visible.Bottom, visibleTop, band.Bottom);
        var parts = new List<LazyLayoutBand> { new(visibleTop, visibleBottom) };

        double above = visibleTop - band.Top;
        double below = band.Bottom - visibleBottom;
        if (below >= above)
        {
            AddDownward(parts, visibleBottom, band.Bottom, chunkHeight);
            AddUpward(parts, visibleTop, band.Top, chunkHeight);
        }
        else
        {
            AddUpward(parts, visibleTop, band.Top, chunkHeight);
            AddDownward(parts, visibleBottom, band.Bottom, chunkHeight);
        }

        return parts;
    }

    private static void AddDownward(List<LazyLayoutBand> parts, double from, double to, double chunkHeight)
    {
        for (double top = from; top < to; top += chunkHeight)
            parts.Add(new LazyLayoutBand(top, Math.Min(to, top + chunkHeight)));
    }

    private static void AddUpward(List<LazyLayoutBand> parts, double from, double to, double chunkHeight)
    {
        for (double bottom = from; bottom > to; bottom -= chunkHeight)
            parts.Add(new LazyLayoutBand(Math.Max(to, bottom - chunkHeight), bottom));
    }
}
