using System;
using System.Collections.Generic;
using Windows.Foundation;

namespace JitHub.Services.Markdown;

/// <summary>
/// Tracks exact coverage of an audit viewport by successfully painted Win2D
/// invalidation regions. A bounding-box union is not sufficient: disjoint
/// regions can leave an unpainted hole in the middle of the viewport.
/// </summary>
internal sealed class FirstViewportPaintCoverage
{
    private const int MaximumUncoveredRegions = 4096;
    internal const int MaximumPendingMasks = 512;
    private const int MaximumRegionOperationsPerPaint = 65536;
    private readonly List<Region> _uncovered = new();
    private readonly List<Region> _maskScratch = new();
    private Region _viewport;
    private bool _initialized;
    private object? _identitySnapshot;
    private long _identityGeneration;
    private long _identityRevision;
    private double _identityScale;
    private bool _hasIdentity;
    private double _lastPaintedIntersectionArea;

    internal double ViewportArea => _initialized
        ? Math.Max(0, (_viewport.Right - _viewport.Left) * (_viewport.Bottom - _viewport.Top))
        : 0;

    internal double CoveredArea
    {
        get
        {
            double viewportArea = ViewportArea;
            if (!_initialized || viewportArea <= 0)
                return 0;

            double uncoveredArea = 0;
            foreach (Region region in _uncovered)
                uncoveredArea += Math.Max(0, (region.Right - region.Left) * (region.Bottom - region.Top));
            return Math.Clamp(viewportArea - uncoveredArea, 0, viewportArea);
        }
    }

    internal int UncoveredRegionCount => _uncovered.Count;
    internal string? LastFailureReason { get; private set; }

    internal double LastPaintedIntersectionArea => _lastPaintedIntersectionArea;

    internal bool HasIdentity => _hasIdentity;

    internal bool EnsureIdentity(
        object snapshot,
        long generation,
        long revision,
        double rasterizationScale,
        Rect viewport)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (generation <= 0 || !double.IsFinite(rasterizationScale) || rasterizationScale <= 0 ||
            !TryCreateRegion(viewport.Left, viewport.Top, viewport.Width, viewport.Height, out Region identityViewport))
        {
            Reset();
            return false;
        }

        if (!_hasIdentity ||
            !ReferenceEquals(_identitySnapshot, snapshot) ||
            _identityGeneration != generation ||
            _identityRevision != revision ||
            _identityScale != rasterizationScale ||
            !_viewport.Equals(identityViewport))
        {
            Reset();
            _identitySnapshot = snapshot;
            _identityGeneration = generation;
            _identityRevision = revision;
            _identityScale = rasterizationScale;
            _viewport = identityViewport;
            _initialized = true;
            _hasIdentity = true;
            _uncovered.Add(identityViewport);
        }

        return true;
    }

    internal bool IsIdentityCurrent(
        object snapshot,
        long generation,
        long revision,
        double rasterizationScale,
        Rect viewport) =>
        _hasIdentity &&
        ReferenceEquals(_identitySnapshot, snapshot) &&
        _identityGeneration == generation &&
        _identityRevision == revision &&
        _identityScale == rasterizationScale &&
        TryCreateRegion(viewport.Left, viewport.Top, viewport.Width, viewport.Height, out Region currentViewport) &&
        _viewport.Equals(currentViewport);

    internal void Reset()
    {
        _uncovered.Clear();
        _maskScratch.Clear();
        _initialized = false;
        _identitySnapshot = null;
        _identityGeneration = 0;
        _identityRevision = 0;
        _identityScale = 0;
        _hasIdentity = false;
        _lastPaintedIntersectionArea = 0;
        LastFailureReason = null;
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
            !TryCreateRegion(paintedLeft, paintedTop, paintedWidth, paintedHeight, out Region painted) ||
            !EnsureViewport(viewport))
        {
            return Fail("invalid-coverage-region");
        }

        _lastPaintedIntersectionArea = IntersectionArea(_viewport, painted);
        int workRemaining = MaximumRegionOperationsPerPaint;
        return SubtractPaintFromUncovered(painted, ref workRemaining) && _uncovered.Count == 0;
    }

    /// <summary>
    /// Adds the successfully painted part of a region while keeping visible
    /// pending-image rectangles uncovered. Masks are also re-opened in prior
    /// coverage so a stale placeholder/old tile can never satisfy readiness.
    /// </summary>
    internal bool AddPaintedRegionExcluding(
        double viewportLeft,
        double viewportTop,
        double viewportWidth,
        double viewportHeight,
        double paintedLeft,
        double paintedTop,
        double paintedWidth,
        double paintedHeight,
        IReadOnlyList<Rect> excludedRegions)
    {
        ArgumentNullException.ThrowIfNull(excludedRegions);
        if (!TryCreateRegion(viewportLeft, viewportTop, viewportWidth, viewportHeight, out Region viewport) ||
            !TryCreateRegion(paintedLeft, paintedTop, paintedWidth, paintedHeight, out Region painted) ||
            !EnsureViewport(viewport) ||
            excludedRegions.Count > MaximumPendingMasks)
        {
            return Fail(excludedRegions.Count > MaximumPendingMasks
                ? "pending-mask-count-budget-exceeded"
                : "invalid-coverage-region");
        }

        _lastPaintedIntersectionArea = IntersectionArea(_viewport, painted);
        // (Uncovered union Pending) minus (Paint minus Pending) is exactly
        // (Uncovered minus Paint) union Pending. Subtracting once before
        // reopening masks avoids fragmenting the paint region for every image.
        int workRemaining = MaximumRegionOperationsPerPaint;
        if (!SubtractPaintFromUncovered(painted, ref workRemaining))
            return false;

        _maskScratch.Clear();
        foreach (Rect exclusion in excludedRegions)
        {
            if (!TryCreateRegion(exclusion.Left, exclusion.Top, exclusion.Width, exclusion.Height, out Region mask))
                return Fail("invalid-pending-image-mask");

            mask = Intersect(_viewport, mask);
            if (!IsNonEmpty(mask))
                continue;

            // Union each pending image/tile mask into uncovered space without
            // introducing overlapping rectangles or unbounded growth.
            _maskScratch.Clear();
            _maskScratch.Add(mask);
            foreach (Region alreadyUncovered in _uncovered)
            {
                if (!SubtractFromScratch(_maskScratch, alreadyUncovered, ref workRemaining))
                    return false;
                if (_maskScratch.Count == 0)
                    break;
            }

            if (_uncovered.Count + _maskScratch.Count > MaximumUncoveredRegions)
                return Fail("coverage-region-budget-exceeded");
            _uncovered.AddRange(_maskScratch);
        }

        return _uncovered.Count == 0;
    }

    private bool EnsureViewport(Region viewport)
    {
        if (_initialized && _viewport.Equals(viewport))
            return true;

        Reset();
        _viewport = viewport;
        _initialized = true;
        _uncovered.Add(viewport);
        return true;
    }

    private bool SubtractPaintFromUncovered(Region painted, ref int workRemaining)
    {
        for (int index = _uncovered.Count - 1; index >= 0; index--)
        {
            if (--workRemaining < 0)
                return Fail("coverage-work-budget-exceeded");
            Region pending = _uncovered[index];
            Region overlap = Intersect(pending, painted);
            if (!IsNonEmpty(overlap))
                continue;

            _uncovered.RemoveAt(index);
            AddIfNonEmpty(pending.Left, pending.Top, pending.Right, overlap.Top);
            AddIfNonEmpty(pending.Left, overlap.Bottom, pending.Right, pending.Bottom);
            AddIfNonEmpty(pending.Left, overlap.Top, overlap.Left, overlap.Bottom);
            AddIfNonEmpty(overlap.Right, overlap.Top, pending.Right, overlap.Bottom);
            if (_uncovered.Count > MaximumUncoveredRegions)
            {
                // Pathological invalidations fail closed rather than let the
                // audit itself grow without bound on a busy UI thread.
                return Fail("coverage-region-budget-exceeded");
            }
        }

        return true;
    }

    private bool SubtractFromScratch(List<Region> regions, Region subtractor, ref int workRemaining)
    {
        for (int index = regions.Count - 1; index >= 0; index--)
        {
            if (--workRemaining < 0)
                return Fail("coverage-work-budget-exceeded");
            Region pending = regions[index];
            Region overlap = Intersect(pending, subtractor);
            if (!IsNonEmpty(overlap))
                continue;

            regions.RemoveAt(index);
            AddToScratchIfNonEmpty(regions, pending.Left, pending.Top, pending.Right, overlap.Top);
            AddToScratchIfNonEmpty(regions, pending.Left, overlap.Bottom, pending.Right, pending.Bottom);
            AddToScratchIfNonEmpty(regions, pending.Left, overlap.Top, overlap.Left, overlap.Bottom);
            AddToScratchIfNonEmpty(regions, overlap.Right, overlap.Top, pending.Right, overlap.Bottom);
            if (regions.Count > MaximumUncoveredRegions)
                return Fail("coverage-region-budget-exceeded");
        }

        return true;
    }

    private bool Fail(string reason)
    {
        Reset();
        LastFailureReason = reason;
        return false;
    }

    private static void AddToScratchIfNonEmpty(List<Region> regions, double left, double top, double right, double bottom)
    {
        if (left < right && top < bottom)
            regions.Add(new Region(left, top, right, bottom));
    }

    private static Region Intersect(Region left, Region right) => new(
        Math.Max(left.Left, right.Left),
        Math.Max(left.Top, right.Top),
        Math.Min(left.Right, right.Right),
        Math.Min(left.Bottom, right.Bottom));

    private static bool IsNonEmpty(Region region) => region.Left < region.Right && region.Top < region.Bottom;

    private static double IntersectionArea(Region left, Region right)
    {
        Region intersection = Intersect(left, right);
        return IsNonEmpty(intersection)
            ? (intersection.Right - intersection.Left) * (intersection.Bottom - intersection.Top)
            : 0;
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
