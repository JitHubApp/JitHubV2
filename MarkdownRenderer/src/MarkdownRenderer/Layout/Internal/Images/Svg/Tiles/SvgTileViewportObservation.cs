using System;
using System.Threading;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;

/// <summary>
/// What an image's SVG tile cache last learned about the viewport: whether
/// the image is visible, the visible tile range when Paint computed one, the
/// observation version that pressure parking fences on, and the visible
/// demand signature of a pressure-parked image. Not thread-safe:
/// <see cref="SvgTileCache"/> mutates it under its tile lock. Only
/// <see cref="Visibility"/> and <see cref="Version"/> are read without it.
/// </summary>
internal sealed class SvgTileViewportObservation
{
    private int _svgVisibleFirstTileX;
    private int _svgVisibleFirstTileY;
    private int _svgVisibleLastTileX;
    private int _svgVisibleLastTileY;
    private bool _svgVisibleTileRangeKnown;
    // -1 means the accepted plan has not received a viewport pass yet, 0 is
    // known offscreen, and 1 is in the current viewport.
    private int _svgCurrentViewportVisible = -1;
    private long _svgViewportObservationVersion;
    private bool _svgPressureDemandSignatureKnown;
    private int _svgPressureDemandFirstX;
    private int _svgPressureDemandFirstY;
    private int _svgPressureDemandLastX;
    private int _svgPressureDemandLastY;
    private int _svgPressureDemandWidth;
    private int _svgPressureDemandHeight;
    private MarkdownSvgPhysicalViewport? _svgPressureDemandPhysicalViewport;

    /// <summary>-1 before the first viewport pass, 0 offscreen, 1 in the viewport.</summary>
    internal int Visibility => Volatile.Read(ref _svgCurrentViewportVisible);

    /// <summary>Increments whenever the visibility or the visible tile range changes.</summary>
    internal long Version => Volatile.Read(ref _svgViewportObservationVersion);

    /// <summary>Whether Paint has recorded the exact visible tile range.</summary>
    internal bool RangeKnown => _svgVisibleTileRangeKnown;

    internal int FirstTileX => _svgVisibleFirstTileX;

    internal int FirstTileY => _svgVisibleFirstTileY;

    internal int LastTileX => _svgVisibleLastTileX;

    internal int LastTileY => _svgVisibleLastTileY;

    /// <summary>Whether a pressure-parked image has recorded a visible-demand signature.</summary>
    internal bool PressureSignatureKnown => _svgPressureDemandSignatureKnown;

    /// <summary>Paint found the accepted plan's destination outside the viewport.</summary>
    internal void ObserveOffscreen()
    {
        bool observationChanged =
            Volatile.Read(ref _svgCurrentViewportVisible) != 0 ||
            _svgVisibleTileRangeKnown ||
            _svgVisibleFirstTileX != 0 || _svgVisibleFirstTileY != 0 ||
            _svgVisibleLastTileX != 0 || _svgVisibleLastTileY != 0;
        Volatile.Write(ref _svgCurrentViewportVisible, 0);
        _svgVisibleTileRangeKnown = false;
        _svgVisibleFirstTileX = 0;
        _svgVisibleFirstTileY = 0;
        _svgVisibleLastTileX = 0;
        _svgVisibleLastTileY = 0;
        if (observationChanged)
            Interlocked.Increment(ref _svgViewportObservationVersion);
    }

    /// <summary>Paint computed the accepted plan's exact visible tile range.</summary>
    internal void ObserveVisibleRange(
        int firstVisibleTileX,
        int firstVisibleTileY,
        int lastVisibleTileX,
        int lastVisibleTileY)
    {
        bool observationChanged =
            Volatile.Read(ref _svgCurrentViewportVisible) != 1 ||
            !_svgVisibleTileRangeKnown ||
            _svgVisibleFirstTileX != firstVisibleTileX ||
            _svgVisibleFirstTileY != firstVisibleTileY ||
            _svgVisibleLastTileX != lastVisibleTileX ||
            _svgVisibleLastTileY != lastVisibleTileY;
        _svgVisibleFirstTileX = firstVisibleTileX;
        _svgVisibleFirstTileY = firstVisibleTileY;
        _svgVisibleLastTileX = lastVisibleTileX;
        _svgVisibleLastTileY = lastVisibleTileY;
        _svgVisibleTileRangeKnown = true;
        Volatile.Write(ref _svgCurrentViewportVisible, 1);
        if (observationChanged)
            Interlocked.Increment(ref _svgViewportObservationVersion);
    }

    /// <summary>
    /// Scroll realization knows visibility, not the exact tile range.
    /// Re-entering the viewport therefore stays pinned until Paint computes
    /// a fresh range for the current viewport.
    /// </summary>
    internal void ObserveVisibility(bool visible)
    {
        Volatile.Write(ref _svgCurrentViewportVisible, visible ? 1 : 0);
        _svgVisibleTileRangeKnown = false;
        _svgVisibleFirstTileX = 0;
        _svgVisibleFirstTileY = 0;
        _svgVisibleLastTileX = 0;
        _svgVisibleLastTileY = 0;
        Interlocked.Increment(ref _svgViewportObservationVersion);
    }

    /// <summary>A pressure-parked image is offscreen; its range is forgotten without a version change.</summary>
    internal void MarkParkedOffscreen()
    {
        Volatile.Write(ref _svgCurrentViewportVisible, 0);
        _svgVisibleTileRangeKnown = false;
    }

    /// <summary>Records a pressure-parked image's visible range without a version change.</summary>
    internal void MarkParkedVisibleRange(int firstX, int firstY, int lastX, int lastY)
    {
        Volatile.Write(ref _svgCurrentViewportVisible, 1);
        _svgVisibleFirstTileX = firstX;
        _svgVisibleFirstTileY = firstY;
        _svgVisibleLastTileX = lastX;
        _svgVisibleLastTileY = lastY;
        _svgVisibleTileRangeKnown = true;
    }

    /// <summary>Records a parked image's visible demand; returns whether it differs from the last one.</summary>
    internal bool TryRecordPressureSignature(
        int firstX,
        int firstY,
        int lastX,
        int lastY,
        int width,
        int height,
        MarkdownSvgPhysicalViewport physicalViewport)
    {
        bool changed = !_svgPressureDemandSignatureKnown ||
            _svgPressureDemandFirstX != firstX || _svgPressureDemandFirstY != firstY ||
            _svgPressureDemandLastX != lastX || _svgPressureDemandLastY != lastY ||
            _svgPressureDemandWidth != width || _svgPressureDemandHeight != height ||
            !Nullable.Equals(_svgPressureDemandPhysicalViewport, physicalViewport);
        if (changed)
        {
            _svgPressureDemandSignatureKnown = true;
            _svgPressureDemandFirstX = firstX;
            _svgPressureDemandFirstY = firstY;
            _svgPressureDemandLastX = lastX;
            _svgPressureDemandLastY = lastY;
            _svgPressureDemandWidth = width;
            _svgPressureDemandHeight = height;
            _svgPressureDemandPhysicalViewport = physicalViewport;
        }
        return changed;
    }

    /// <summary>Sets the visibility alone, as a tile-grid swap does before its reset.</summary>
    internal void SetVisibility(int visibility) => Volatile.Write(ref _svgCurrentViewportVisible, visibility);

    /// <summary>Forgets the visible range and the pressure-demand signature.</summary>
    internal void ForgetRangeAndSignature()
    {
        _svgVisibleTileRangeKnown = false;
        _svgPressureDemandSignatureKnown = false;
    }

    /// <summary>Forgets the pressure-demand signature.</summary>
    internal void ForgetPressureSignature() => _svgPressureDemandSignatureKnown = false;
}
