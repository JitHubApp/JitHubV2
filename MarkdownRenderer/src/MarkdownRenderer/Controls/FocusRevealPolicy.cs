using System;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The viewport state a focus reveal started from, and the top it scrolls
/// toward once it has scrolled. A reveal may need several geometry retries
/// while lazy layout settles; it stays valid only while the viewport is where
/// focus left it or on the reveal's own (animated, possibly clamped) way.
/// </summary>
internal readonly record struct FocusRevealOrigin(long ViewportRequestSerial, double ViewportTop, double? RevealTop);

internal static class FocusRevealPolicy
{
    /// <summary>One DIP, for layout rounding and clamping noise.</summary>
    internal const double ViewportTolerance = 1.0;

    /// <summary>
    /// A newer scroll request (counted by <paramref name="currentRequestSerial"/>)
    /// ends the reveal even before its view change has applied, and so does a
    /// viewport outside the reveal's own path, such as a wheel or scrollbar move.
    /// </summary>
    internal static bool IsCurrent(FocusRevealOrigin? origin, long currentRequestSerial, double currentTop)
    {
        if (origin is not { } value || value.ViewportRequestSerial != currentRequestSerial || !double.IsFinite(currentTop))
            return false;

        double low = value.ViewportTop;
        double high = value.ViewportTop;
        if (value.RevealTop is { } revealTop)
        {
            low = Math.Min(low, revealTop);
            high = Math.Max(high, revealTop);
        }
        return currentTop >= low - ViewportTolerance && currentTop <= high + ViewportTolerance;
    }
}
