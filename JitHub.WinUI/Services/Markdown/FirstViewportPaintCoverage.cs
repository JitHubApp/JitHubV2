using System;
using System.Collections.Generic;

namespace JitHub.Services.Markdown;

/// <summary>
/// Tracks exact coverage of an audit viewport by successfully painted Win2D
/// invalidation regions. A bounding-box union is not sufficient: disjoint
/// regions can leave an unpainted hole in the middle of the viewport.
/// </summary>
internal sealed class FirstViewportPaintCoverage
{
    private const int MaximumUncoveredRegions = 4096;
    private readonly List<Region> _uncovered = new();
    private Region _viewport;
    private bool _initialized;

    internal void Reset()
    {
        _uncovered.Clear();
        _initialized = false;
    }

    internal bool Covers(
        double viewportLeft,
        double viewportTop,
        double viewportWidth,
        double viewportHeight)
    {
        return TryCreateRegion(viewportLeft, viewportTop, viewportWidth, viewportHeight, out Region viewport) &&
            _initialized &&
            _viewport.Equals(viewport) &&
            _uncovered.Count == 0;
    }

    internal bool AddPaintedRegion(
        double viewportLeft,
        double viewportTop,
        double viewportWidth,
        double viewportHeight,
        double paintedLeft,
        double paintedTop,
        double paintedWidth,
        double paintedHeight)
    {
        if (!TryCreateRegion(viewportLeft, viewportTop, viewportWidth, viewportHeight, out Region viewport) ||
            !TryCreateRegion(paintedLeft, paintedTop, paintedWidth, paintedHeight, out Region painted))
        {
            Reset();
            return false;
        }

        if (!_initialized || !_viewport.Equals(viewport))
        {
            Reset();
            _viewport = viewport;
            _uncovered.Add(viewport);
            _initialized = true;
        }

        for (int index = _uncovered.Count - 1; index >= 0; index--)
        {
            Region pending = _uncovered[index];
            double left = Math.Max(pending.Left, painted.Left);
            double top = Math.Max(pending.Top, painted.Top);
            double right = Math.Min(pending.Right, painted.Right);
            double bottom = Math.Min(pending.Bottom, painted.Bottom);
            if (left >= right || top >= bottom)
                continue;

            _uncovered.RemoveAt(index);
            AddIfNonEmpty(pending.Left, pending.Top, pending.Right, top);
            AddIfNonEmpty(pending.Left, bottom, pending.Right, pending.Bottom);
            AddIfNonEmpty(pending.Left, top, left, bottom);
            AddIfNonEmpty(right, top, pending.Right, bottom);
            if (_uncovered.Count > MaximumUncoveredRegions)
            {
                // Pathological invalidations fail closed rather than let the
                // audit itself grow without bound on a busy UI thread.
                Reset();
                return false;
            }
        }

        return _uncovered.Count == 0;
    }

    private void AddIfNonEmpty(double left, double top, double right, double bottom)
    {
        if (left < right && top < bottom)
            _uncovered.Add(new Region(left, top, right, bottom));
    }

    private static bool TryCreateRegion(
        double left,
        double top,
        double width,
        double height,
        out Region region)
    {
        double right = left + width;
        double bottom = top + height;
        if (!double.IsFinite(left) || !double.IsFinite(top) ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            !double.IsFinite(right) || !double.IsFinite(bottom) ||
            width <= 0 || height <= 0)
        {
            region = default;
            return false;
        }

        region = new Region(left, top, right, bottom);
        return true;
    }

    private readonly record struct Region(double Left, double Top, double Right, double Bottom);
}
