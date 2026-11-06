using System;
using Windows.Foundation;

namespace MarkdownRenderer.Layout;

/// <summary>
/// The horizontal scrollbar geometry shared by every
/// <see cref="IHorizontalOverflowBox"/>: its track height, the track drawn
/// directly below a block's horizontal viewport, and the thumb sized to the
/// visible fraction and placed by the scroll offset in the reading direction.
/// </summary>
internal static class HorizontalOverflowScrollbar
{
    internal const float Height = 12f;
    private const float MinimumThumbWidth = 24f;

    /// <summary>The track below the box's horizontal viewport, or empty when it cannot scroll.</summary>
    internal static Rect GetTrackBelowViewport(IHorizontalOverflowBox box)
    {
        if (!box.CanScrollHorizontally)
            return Rect.Empty;

        Rect viewport = box.HorizontalViewportBounds;
        return new Rect(viewport.X, viewport.Bottom, viewport.Width, Height);
    }

    /// <summary>The thumb within the box's current track, or empty when it has none.</summary>
    internal static Rect GetThumbBounds(IHorizontalOverflowBox box)
    {
        Rect track = box.HorizontalScrollTrackBounds;
        if (track.IsEmpty || box.HorizontalExtent <= 0)
            return Rect.Empty;

        double thumbWidth = Math.Clamp(
            track.Width * box.HorizontalViewport / box.HorizontalExtent,
            Math.Min(MinimumThumbWidth, track.Width),
            track.Width);
        double travel = Math.Max(0, track.Width - thumbWidth);
        double maximum = Math.Max(0, box.HorizontalExtent - box.HorizontalViewport);
        double fraction = maximum <= 0 ? 0 : box.HorizontalOffset / maximum;
        double x = box.IsRightToLeft
            ? track.Right - thumbWidth - travel * fraction
            : track.Left + travel * fraction;
        return new Rect(x, track.Y, thumbWidth, track.Height);
    }
}
