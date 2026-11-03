using System;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Managed-only geometry captured from a text layout. This deliberately keeps
/// rectangles rather than CanvasTextLayoutRegion or CanvasTextLayout objects so
/// optional readiness indexes cannot extend native layout lifetimes.
/// </summary>
internal readonly struct InlineSceneGeometry
{
    private readonly Rect _firstRegion;
    private readonly Rect[]? _additionalRegions;
    private readonly int _additionalRegionOffset;

    internal InlineSceneGeometry(InlineVectorSceneRun run, CanvasTextLayoutRegion[]? regions)
    {
        Run = run;
        if (regions is { Length: > 0 })
        {
            _firstRegion = regions[0].LayoutBounds;
            if (regions.Length > 1)
            {
                _additionalRegions = new Rect[regions.Length - 1];
                for (int index = 1; index < regions.Length; index++)
                    _additionalRegions[index - 1] = regions[index].LayoutBounds;
                _additionalRegionOffset = 1;
            }
            RegionCount = regions.Length;
        }
        else
        {
            _firstRegion = default;
            _additionalRegions = null;
            RegionCount = 0;
        }
    }

    internal InlineSceneGeometry(InlineVectorSceneRun run, Rect[] regions)
    {
        Run = run;
        if (regions.Length > 0)
        {
            _firstRegion = regions[0];
            _additionalRegions = regions.Length > 1 ? regions : null;
            _additionalRegionOffset = 0;
            RegionCount = regions.Length;
        }
        else
        {
            _firstRegion = default;
            _additionalRegions = null;
            RegionCount = 0;
        }
    }

    internal readonly InlineVectorSceneRun Run;

    internal readonly int RegionCount;

    private InlineSceneGeometry(InlineVectorSceneRun run, in InlineSceneGeometry source)
    {
        Run = run;
        _firstRegion = source._firstRegion;
        _additionalRegions = source._additionalRegions;
        _additionalRegionOffset = source._additionalRegionOffset;
        RegionCount = source.RegionCount;
    }

    // Regions are immutable after capture. A root-local placement transfer
    // rebinds only semantic identity, without copying any per-region payload.
    internal InlineSceneGeometry WithRun(InlineVectorSceneRun run) => new(run, this);

    internal bool HasSameRegions(in InlineSceneGeometry other) =>
        _firstRegion.Equals(other._firstRegion) &&
        ReferenceEquals(_additionalRegions, other._additionalRegions) &&
        _additionalRegionOffset == other._additionalRegionOffset &&
        RegionCount == other.RegionCount;

    internal Rect GetRegionBounds(int index)
    {
        if ((uint)index >= (uint)RegionCount)
            throw new ArgumentOutOfRangeException(nameof(index));
        return index == 0 ? _firstRegion : _additionalRegions![index - _additionalRegionOffset];
    }
}
