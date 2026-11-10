using System;
using System.Collections.Generic;
using System.Threading;
using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.LazyLayout;

namespace MarkdownRenderer.Performance;

/// <summary>
/// Scalar band index of one inline container's vector-scene line fragments,
/// retaining only the owners and regions that intersect its coverage band.
/// </summary>
internal sealed class InlineSceneGeometryIndex
{
    private readonly double[] _prefixMaximumBottoms;

    private InlineSceneGeometryIndex(
        long generation,
        LazyLayoutBand coverage,
        InlineSceneIndexEntry[] entries)
    {
        Generation = generation;
        Coverage = coverage;
        Entries = entries;
        _prefixMaximumBottoms = new double[entries.Length];
        double maximum = double.NegativeInfinity;
        for (int index = 0; index < entries.Length; index++)
        {
            maximum = Math.Max(maximum, entries[index].Bottom);
            _prefixMaximumBottoms[index] = maximum;
        }
    }

    internal long Generation { get; }
    private LazyLayoutBand Coverage { get; }
    internal InlineSceneIndexEntry[] Entries { get; }

    internal bool Covers(LazyLayoutBand band) =>
        band.Top >= Coverage.Top && band.Bottom <= Coverage.Bottom;

    internal int FindLastStartingAtOrBefore(double bottom)
    {
        int low = 0;
        int high = Entries.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (Entries[middle].Top <= bottom)
                low = middle + 1;
            else
                high = middle;
        }
        return low - 1;
    }

    internal int FindFirstWithBottomAtOrAfter(double top)
    {
        int low = 0;
        int high = _prefixMaximumBottoms.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (_prefixMaximumBottoms[middle] < top)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    internal static bool TryCreate(
        long generation,
        InlineSceneGeometry[] geometry,
        LazyLayoutBand localBand,
        CancellationToken cancellationToken,
        out InlineSceneGeometryIndex? index)
    {
        index = null;
        if (generation == 0 || !VectorSceneBandGeometry.IsValidBand(localBand))
            return false;

        List<InlineSceneIndexEntry> entries = [];
        foreach (InlineSceneGeometry item in geometry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.RegionCount <= 0)
                return false;

            double left = double.PositiveInfinity;
            double right = double.NegativeInfinity;
            double top = double.PositiveInfinity;
            double bottom = double.NegativeInfinity;
            List<Rect>? retainedRegions = null;
            for (int regionIndex = 0; regionIndex < item.RegionCount; regionIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Rect region = item.GetRegionBounds(regionIndex);
                if (!VectorSceneBandGeometry.IsFiniteNonNegativeRect(region))
                    return false;

                top = Math.Min(top, region.Top);
                bottom = Math.Max(bottom, region.Bottom);
                if (!localBand.Intersects(region.Top, region.Bottom))
                    continue;

                left = Math.Min(left, region.Left);
                right = Math.Max(right, region.Right);
                (retainedRegions ??= []).Add(region);
            }

            // Only retain scene owners and line fragments intersecting the
            // candidate band. A long paragraph's off-band Math graph stays
            // owned by its snapshot boxes, not duplicated by this index.
            if (retainedRegions is null)
                continue;

            Rect[] retained = retainedRegions.ToArray();
            var filteredGeometry = new InlineSceneGeometry(item.Run, retained);
            double retainedTop = double.PositiveInfinity;
            double retainedBottom = double.NegativeInfinity;
            foreach (Rect region in retained)
            {
                retainedTop = Math.Min(retainedTop, region.Top);
                retainedBottom = Math.Max(retainedBottom, region.Bottom);
            }
            entries.Add(new InlineSceneIndexEntry(
                filteredGeometry,
                retainedTop,
                retainedBottom,
                left,
                right));
        }

        entries.Sort(static (left, right) =>
        {
            int comparison = left.Top.CompareTo(right.Top);
            return comparison != 0 ? comparison : left.Bottom.CompareTo(right.Bottom);
        });
        index = new InlineSceneGeometryIndex(generation, localBand, entries.ToArray());
        return true;
    }
}
