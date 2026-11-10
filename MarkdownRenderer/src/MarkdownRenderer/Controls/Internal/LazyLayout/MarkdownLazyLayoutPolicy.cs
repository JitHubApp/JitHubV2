using System;

namespace MarkdownRenderer.Controls.Internal.LazyLayout;

/// <summary>
/// Selects viewport-relative layout before native text measurement becomes
/// disproportionate to the first visible band.
/// </summary>
internal static class MarkdownLazyLayoutPolicy
{
    internal const int TopLevelBlockThreshold = 128;
    internal const int SourceLengthThreshold = 32 * 1024;

    /// <summary>
    /// Extra document pixels measured around the viewport in lazy layout mode.
    /// Wider than embed realization so text, inline image geometry, and
    /// accessibility ranges are ready before the user reaches them.
    /// </summary>
    internal const double OverscanPx = 2400;

    internal static bool ShouldUse(
        int topLevelBlockCount,
        int sourceUtf16Length,
        bool hasCustomBlockEmbedMeasurement)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(topLevelBlockCount);
        ArgumentOutOfRangeException.ThrowIfNegative(sourceUtf16Length);

        return !hasCustomBlockEmbedMeasurement &&
               (topLevelBlockCount >= TopLevelBlockThreshold ||
                sourceUtf16Length >= SourceLengthThreshold);
    }
}
