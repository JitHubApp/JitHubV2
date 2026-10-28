using System;
using Windows.Foundation;

namespace MarkdownRenderer.Layout;

internal readonly struct LayoutPlacementInput(
    float baseX,
    float baseTop,
    float logicalX,
    float width,
    float height,
    bool measured,
    bool sharesFlowWithNext)
{
    internal readonly float BaseX = baseX;
    internal readonly float BaseTop = baseTop;
    internal readonly float LogicalX = logicalX;
    internal readonly float Width = width;
    internal readonly float Height = height;
    internal readonly bool Measured = measured;
    internal readonly bool SharesFlowWithNext = sharesFlowWithNext;
}

internal readonly struct LayoutRootPlacement(
    float baseX,
    float baseTop,
    float x,
    float top,
    float width,
    float height,
    bool measured)
{
    internal readonly float BaseX = baseX;
    internal readonly float BaseTop = baseTop;
    internal readonly float X = x;
    internal readonly float Top = top;
    internal readonly float Width = width;
    internal readonly float Height = height;
    internal readonly bool Measured = measured;
    internal float TranslationX => X - BaseX;
    internal float TranslationY => Top - BaseTop;
    // LayoutSnapshot advances the document flow in single precision. Keep the
    // index edge identical to a fresh Arrange instead of adding the two float
    // operands after widening them to double.
    internal double Bottom => (double)(Top + Height);
    internal Rect LogicalBounds => new(X, Top, Width, Height);
}

/// <summary>
/// Immutable top-level geometry/index for one LayoutSnapshot revision. The
/// viewport index is private and is fully built before this generation escapes.
/// </summary>
internal sealed class LayoutPlacementGeneration
{
    private readonly LayoutRootPlacement[] _roots;
    private readonly float[] _continuations;
    private readonly bool[] _sharesFlowWithNext;
    private readonly ViewportBandIndex _viewportIndex;

    private LayoutPlacementGeneration(
        LayoutRootPlacement[] roots,
        float[] continuations,
        bool[] sharesFlowWithNext,
        ViewportBandIndex viewportIndex,
        Size size,
        long revision)
    {
        _roots = roots;
        _continuations = continuations;
        _sharesFlowWithNext = sharesFlowWithNext;
        _viewportIndex = viewportIndex;
        Size = size;
        Revision = revision;
    }

    internal int Count => _roots.Length;
    internal Size Size { get; }
    internal long Revision { get; }
    internal bool IsDocumentOrder => _viewportIndex.IsDocumentOrder;

    internal LayoutRootPlacement GetPlacement(int ordinal)
    {
        if ((uint)ordinal >= (uint)_roots.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        return _roots[ordinal];
    }

    internal float GetContinuation(int ordinal)
    {
        if ((uint)ordinal >= (uint)_continuations.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        return _continuations[ordinal];
    }

    internal bool SharesFlowWithNext(int ordinal)
    {
        if ((uint)ordinal >= (uint)_sharesFlowWithNext.Length)
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        return _sharesFlowWithNext[ordinal];
    }

    internal ViewportRange Find(double top, double bottom)
        => _viewportIndex.Find(top, bottom);

    internal bool TryFindFirstBottomAtOrAfter(double offset, out int ordinal)
        => _viewportIndex.TryFindFirstBottomAtOrAfter(offset, out ordinal);

    internal (double Top, double Bottom) GetIndexedBounds(int slot)
        => _viewportIndex.GetBounds(slot);

    internal int GetBlockOrdinal(int slot)
        => _viewportIndex.GetBlockOrdinal(slot);

    internal Rect ToLogicalRect(int ordinal, Rect physicalRect)
    {
        LayoutRootPlacement placement = GetPlacement(ordinal);
        // Clipped vector semantics use Rect.Empty (infinite origin and negative
        // infinite dimensions). It has no position to translate, and rebuilding
        // it through Rect's constructor throws before consumers can skip it.
        if (physicalRect.IsEmpty)
            return Rect.Empty;
        // Map through the authoritative source-owner origin. Adding a rounded
        // `Top - BaseTop` translation back to a large root-local coordinate can
        // introduce a one-ULP drift even when the rectangle is exactly at the
        // root origin; subtract locally first, then add the published origin.
        return new Rect(
            (double)(placement.X + ((float)physicalRect.X - placement.BaseX)),
            (double)(placement.Top + ((float)physicalRect.Y - placement.BaseTop)),
            physicalRect.Width,
            physicalRect.Height);
    }

    internal Point ToLogicalPoint(int ordinal, Point physicalPoint)
    {
        LayoutRootPlacement placement = GetPlacement(ordinal);
        return new Point(
            (double)placement.X + (physicalPoint.X - placement.BaseX),
            (double)placement.Top + (physicalPoint.Y - placement.BaseTop));
    }

    internal Point ToPhysicalPoint(int ordinal, Point logicalPoint)
    {
        LayoutRootPlacement placement = GetPlacement(ordinal);
        return new Point(
            (double)placement.BaseX + (logicalPoint.X - placement.X),
            (double)placement.BaseTop + (logicalPoint.Y - placement.Top));
    }

    internal Rect ToPhysicalViewport(int ordinal, Rect logicalViewport)
    {
        LayoutRootPlacement placement = GetPlacement(ordinal);
        if (logicalViewport.IsEmpty)
            return Rect.Empty;
        return new Rect(
            (double)placement.BaseX + (logicalViewport.X - placement.X),
            (double)placement.BaseTop + (logicalViewport.Y - placement.Top),
            logicalViewport.Width,
            logicalViewport.Height);
    }

    internal LazyLayoutBand ToPhysicalBand(int ordinal, LazyLayoutBand logicalBand)
    {
        LayoutRootPlacement placement = GetPlacement(ordinal);
        return new LazyLayoutBand(
            (double)placement.BaseTop + (logicalBand.Top - placement.Top),
            (double)placement.BaseTop + (logicalBand.Bottom - placement.Top));
    }

    internal float[] CopyContinuations() => (float[])_continuations.Clone();

    internal LayoutPlacementGeneration WithRevision(long revision)
        => revision == Revision
            ? this
            : new LayoutPlacementGeneration(_roots, _continuations, _sharesFlowWithNext,
                _viewportIndex, Size, revision);

    internal LayoutPlacementGeneration WithMeasuredRoots(ReadOnlySpan<bool> measuredRoots, long revision)
    {
        if (measuredRoots.Length != _roots.Length)
            throw new ArgumentException("Measured-root count must match the generation.", nameof(measuredRoots));

        var roots = (LayoutRootPlacement[])_roots.Clone();
        for (int ordinal = 0; ordinal < roots.Length; ordinal++)
        {
            LayoutRootPlacement placement = roots[ordinal];
            roots[ordinal] = new LayoutRootPlacement(
                placement.BaseX,
                placement.BaseTop,
                placement.X,
                placement.Top,
                placement.Width,
                placement.Height,
                measuredRoots[ordinal]);
        }

        // Geometry and the private index are immutable and may be shared.
        return new LayoutPlacementGeneration(
            roots, _continuations, _sharesFlowWithNext, _viewportIndex, Size, revision);
    }

    internal static LayoutPlacementGeneration Create(
        ReadOnlySpan<LayoutPlacementInput> inputs,
        float initialTop,
        float blockSpacing,
        float bottomPadding,
        double documentWidth,
        long revision,
        LayoutPlacementGeneration? source = null,
        int firstRecomputedOrdinal = 0)
    {
        if ((uint)firstRecomputedOrdinal > (uint)inputs.Length)
            throw new ArgumentOutOfRangeException(nameof(firstRecomputedOrdinal));
        if (source is null && firstRecomputedOrdinal != 0)
            throw new ArgumentException("A suffix requires a source generation.", nameof(source));
        if (source is not null && source.Count != inputs.Length)
            throw new ArgumentException("Source and input root counts must match.", nameof(source));

        var roots = new LayoutRootPlacement[inputs.Length];
        var continuations = new float[inputs.Length];
        var flowWithNext = new bool[inputs.Length];
        int first = firstRecomputedOrdinal;
        float y = initialTop;
        if (source is not null)
        {
            for (int ordinal = 0; ordinal < first; ordinal++)
            {
                roots[ordinal] = source._roots[ordinal];
                continuations[ordinal] = source._continuations[ordinal];
                flowWithNext[ordinal] = source._sharesFlowWithNext[ordinal];
            }
            if (first > 0)
                y = source._continuations[first - 1];
        }

        for (int ordinal = first; ordinal < inputs.Length; ordinal++)
        {
            LayoutPlacementInput input = inputs[ordinal];
            if (!float.IsFinite(input.BaseX) || !float.IsFinite(input.BaseTop) ||
                !float.IsFinite(input.LogicalX) || !float.IsFinite(input.Width) || input.Width < 0 ||
                !float.IsFinite(input.Height) || input.Height < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(inputs), "Placement inputs must be finite and nonnegative.");
            }

            roots[ordinal] = new LayoutRootPlacement(
                input.BaseX,
                input.BaseTop,
                input.LogicalX,
                y,
                input.Width,
                input.Height,
                input.Measured);
            flowWithNext[ordinal] = input.SharesFlowWithNext;

            // Keep the exact operation order of LayoutSnapshot.AdvanceBlock.
            y += input.Height;
            if (ordinal < inputs.Length - 1 && !input.SharesFlowWithNext)
                y += blockSpacing;
            continuations[ordinal] = y;
        }

        y += bottomPadding;
        var viewportIndex = new ViewportBandIndex(roots.Length);
        for (int ordinal = 0; ordinal < roots.Length; ordinal++)
        {
            LayoutRootPlacement placement = roots[ordinal];
            viewportIndex.SetEntry(ordinal, ordinal, placement.Top, placement.Bottom);
        }
        viewportIndex.Commit();

        return new LayoutPlacementGeneration(
            roots,
            continuations,
            flowWithNext,
            viewportIndex,
            new Size(documentWidth, y),
            revision);
    }

    /// <summary>
    /// Captures geometry already arranged by an eager/custom caller. Snapshot
    /// construction must not reinterpret the first physical root as if it had
    /// been arranged at document origin; these coordinates are the initial
    /// logical coordinates until a later layout pass establishes a new flow.
    /// </summary>
    internal static LayoutPlacementGeneration CaptureExisting(
        ReadOnlySpan<LayoutPlacementInput> inputs,
        Size size,
        long revision,
        float blockSpacing)
    {
        if (!float.IsFinite(blockSpacing) || blockSpacing < 0)
            throw new ArgumentOutOfRangeException(nameof(blockSpacing));

        var roots = new LayoutRootPlacement[inputs.Length];
        var continuations = new float[inputs.Length];
        var flowWithNext = new bool[inputs.Length];
        var viewportIndex = new ViewportBandIndex(inputs.Length);

        for (int ordinal = 0; ordinal < inputs.Length; ordinal++)
        {
            LayoutPlacementInput input = inputs[ordinal];
            if (!float.IsFinite(input.BaseX) || !float.IsFinite(input.BaseTop) ||
                !float.IsFinite(input.LogicalX) || !float.IsFinite(input.Width) || input.Width < 0 ||
                !float.IsFinite(input.Height) || input.Height < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(inputs),
                    "Placement inputs must be finite and nonnegative.");
            }

            roots[ordinal] = new LayoutRootPlacement(
                input.BaseX,
                input.BaseTop,
                input.LogicalX,
                input.BaseTop,
                input.Width,
                input.Height,
                input.Measured);
            flowWithNext[ordinal] = input.SharesFlowWithNext;
            float continuation = input.BaseTop + input.Height;
            if (ordinal < inputs.Length - 1 && !input.SharesFlowWithNext)
                continuation += blockSpacing;
            continuations[ordinal] = continuation;
            viewportIndex.SetEntry(ordinal, ordinal, input.BaseTop,
                (double)(input.BaseTop + input.Height));
        }

        viewportIndex.Commit();
        return new LayoutPlacementGeneration(
            roots, continuations, flowWithNext, viewportIndex, size, revision);
    }
}
