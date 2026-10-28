using System;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Selects viewport-relative layout before native text measurement becomes
/// disproportionate to the first visible band.
/// </summary>
internal static class MarkdownLazyLayoutPolicy
{
    internal const int TopLevelBlockThreshold = 128;
    internal const int SourceLengthThreshold = 32 * 1024;

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
