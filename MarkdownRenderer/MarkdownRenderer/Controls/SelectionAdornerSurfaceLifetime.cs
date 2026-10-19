using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Controls;

/// <summary>
/// The DIP size and DPI of one selection-adornment surface allocation.
/// </summary>
internal readonly record struct SelectionAdornerSurfaceAllocation(int Width, int Height, float Dpi);

/// <summary>
/// Chooses when the selection-adornment surface may be reused. The surface is
/// allocated grow-only in coarse buckets, so ordinary relayout, source
/// replacement and narrow/wide resizes reuse one XAML-attached surface instead
/// of leaving a stream of replaced surfaces for XAML image-peer cleanup.
/// </summary>
internal static class SelectionAdornerSurfaceAllocationPolicy
{
    internal const int BucketDips = 256;
    internal const int MaximumAreaOversize = 4;
    internal const int MaximumPixelDimension = 16_384;
    private const float DpiTolerance = 0.1f;

    internal static bool TryGetRequired(
        double viewportWidth,
        double viewportHeight,
        float dpi,
        out SelectionAdornerSurfaceAllocation required)
    {
        required = default;
        if (!double.IsFinite(viewportWidth) || !double.IsFinite(viewportHeight) ||
            viewportWidth <= 0 || viewportHeight <= 0 ||
            !float.IsFinite(dpi) || dpi <= 0)
        {
            return false;
        }

        double width = Math.Ceiling(viewportWidth);
        double height = Math.Ceiling(viewportHeight);
        if (width > int.MaxValue || height > int.MaxValue)
            return false;

        required = new SelectionAdornerSurfaceAllocation((int)width, (int)height, dpi);
        return true;
    }

    internal static bool CanReuse(
        SelectionAdornerSurfaceAllocation current,
        SelectionAdornerSurfaceAllocation required)
        => SameDpi(current, required) &&
           current.Width >= required.Width &&
           current.Height >= required.Height &&
           !IsOversized(current, required);

    /// <summary>
    /// Returns the allocation for a required size. A same-DPI current
    /// allocation keeps its larger dimensions, so alternating narrow and wide
    /// viewports converge on one surface, unless the merged surface would be
    /// oversized for the request.
    /// </summary>
    internal static SelectionAdornerSurfaceAllocation Allocate(
        SelectionAdornerSurfaceAllocation required,
        SelectionAdornerSurfaceAllocation? current)
    {
        SelectionAdornerSurfaceAllocation exact = new(
            RoundUp(required.Width, required.Dpi),
            RoundUp(required.Height, required.Dpi),
            required.Dpi);
        if (current is not { } previous || !SameDpi(previous, required))
            return exact;

        SelectionAdornerSurfaceAllocation merged = new(
            Math.Max(exact.Width, previous.Width),
            Math.Max(exact.Height, previous.Height),
            required.Dpi);
        return IsOversized(merged, required) ? exact : merged;
    }

    private static bool SameDpi(
        SelectionAdornerSurfaceAllocation left,
        SelectionAdornerSurfaceAllocation right)
        => Math.Abs(left.Dpi - right.Dpi) <= DpiTolerance;

    private static bool IsOversized(
        SelectionAdornerSurfaceAllocation current,
        SelectionAdornerSurfaceAllocation required)
    {
        long currentArea = (long)current.Width * current.Height;
        long bucketedArea = (long)RoundUp(required.Width, required.Dpi) * RoundUp(required.Height, required.Dpi);
        return currentArea > MaximumAreaOversize * Math.Max(bucketedArea, (long)BucketDips * BucketDips);
    }

    private static int RoundUp(int dips, float dpi)
    {
        long bucketed = ((long)dips + BucketDips - 1) / BucketDips * BucketDips;
        // Never exceed the largest Win2D surface unless the viewport itself
        // already requires it; that case keeps its exact, previous size.
        long maximum = (long)Math.Floor(MaximumPixelDimension * 96.0 / dpi);
        return (int)Math.Max(Math.Min(bucketed, maximum), dips);
    }
}

/// <summary>
/// The XAML property that displays a selection-adornment surface.
/// </summary>
internal interface ISelectionAdornerSurfaceHost<TSurface>
    where TSurface : class
{
    TSurface? Source { get; set; }
}

/// <summary>
/// Owns the selection-adornment surface and its single XAML attachment.
/// Win2D's CanvasImageSource is not disposable, so its final native release
/// belongs to XAML or the projection. This owner therefore keeps one surface
/// for the adorner's lifetime, never nests a Source mutation inside another,
/// and roots every replaced surface for a bounded number of frames after XAML
/// has detached it.
/// </summary>
internal sealed class SelectionAdornerSurfaceLifetime<TSurface, TDevice>
    where TSurface : class
    where TDevice : class
{
    internal const int RetirementFrames = 2;

    private readonly Func<TDevice, SelectionAdornerSurfaceAllocation, TSurface> _create;
    private readonly Action<Exception>? _reportReleaseFailure;
    private readonly List<(TSurface Surface, long RetiredFrame)> _retired = new();
    private TSurface? _surface;
    private TDevice? _device;
    private SelectionAdornerSurfaceAllocation _allocation;
    private ISelectionAdornerSurfaceHost<TSurface>? _boundHost;
    private bool _mutating;
    private bool _releaseRequested;
    private long _frame;
    private long _generation;

    internal SelectionAdornerSurfaceLifetime(
        Func<TDevice, SelectionAdornerSurfaceAllocation, TSurface> create,
        Action<Exception>? reportReleaseFailure = null)
    {
        ArgumentNullException.ThrowIfNull(create);
        _create = create;
        _reportReleaseFailure = reportReleaseFailure;
    }

    internal TSurface? Surface => _surface;
    internal TDevice? Device => _device;
    internal SelectionAdornerSurfaceAllocation Allocation => _allocation;
    internal long Generation => _generation;
    internal int RetiredCount => _retired.Count;
    internal bool IsMutating => _mutating;

    /// <summary>
    /// Returns the current surface after it is attached to <paramref name="host"/>,
    /// creating or growing it only when the current allocation cannot be
    /// reused. Returns null when the request is deferred, invalid, or released
    /// by a reentrant lifecycle callback. Creation and attachment failures
    /// propagate after the owner has restored a consistent state.
    /// </summary>
    internal TSurface? Ensure(
        ISelectionAdornerSurfaceHost<TSurface> host,
        double viewportWidth,
        double viewportHeight,
        float dpi,
        TDevice device)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(device);
        if (_mutating)
            return null;
        if (!SelectionAdornerSurfaceAllocationPolicy.TryGetRequired(viewportWidth, viewportHeight, dpi, out var required))
            return null;

        bool sameDevice = ReferenceEquals(_device, device);
        if (_surface is { } current && sameDevice &&
            SelectionAdornerSurfaceAllocationPolicy.CanReuse(_allocation, required))
        {
            if (ReferenceEquals(_boundHost, host) && ReferenceEquals(host.Source, current))
                return current;

            // A newly created adorner image, or a host whose attachment state
            // became unknown, always receives the live surface explicitly.
            _boundHost = null;
            SetSource(host, current);
            _boundHost = host;
            unchecked { _generation++; }
            return CompleteDeferredRelease(host) ? null : current;
        }

        SelectionAdornerSurfaceAllocation allocation = SelectionAdornerSurfaceAllocationPolicy.Allocate(
            required,
            _surface is not null && sameDevice ? _allocation : null);
        TSurface replacement = _create(device, allocation);
        TSurface? previous = _surface;
        try
        {
            SetSource(host, replacement);
        }
        catch
        {
            // XAML may hold either surface. Root both and rebind next time.
            _boundHost = null;
            Retire(replacement);
            throw;
        }

        _surface = replacement;
        _device = device;
        _allocation = allocation;
        _boundHost = host;
        unchecked { _generation++; }
        if (previous is not null)
            Retire(previous);
        return CompleteDeferredRelease(host) ? null : replacement;
    }

    /// <summary>
    /// Detaches and retires the current surface. Never throws: a failed XAML
    /// detach is reported, the surface stays rooted, and the next Ensure call
    /// rebinds explicitly. A release requested while a Source mutation is in
    /// progress is deferred until that mutation returns.
    /// </summary>
    internal bool Release(ISelectionAdornerSurfaceHost<TSurface>? host)
    {
        if (_mutating)
        {
            _releaseRequested = true;
            return false;
        }

        _releaseRequested = false;
        TSurface? previous = _surface;
        ISelectionAdornerSurfaceHost<TSurface>? target = host ?? _boundHost;
        _surface = null;
        _device = null;
        _allocation = default;
        _boundHost = null;
        unchecked { _generation++; }
        if (previous is not null)
            Retire(previous);
        if (target is null)
            return true;

        try
        {
            SetSource(target, null);
            return true;
        }
        catch (Exception failure)
        {
            _reportReleaseFailure?.Invoke(failure);
            return false;
        }
        finally
        {
            // A release requested by this detach's own callback is satisfied.
            _releaseRequested = false;
        }
    }

    internal bool IsRetired(TSurface surface)
    {
        foreach ((TSurface retired, _) in _retired)
        {
            if (ReferenceEquals(retired, surface))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Advances the retirement clock and drops surfaces that XAML detached at
    /// least <see cref="RetirementFrames"/> frames ago.
    /// </summary>
    internal void AdvanceFrame()
    {
        unchecked { _frame++; }
        _retired.RemoveAll(entry => unchecked(_frame - entry.RetiredFrame) >= RetirementFrames);
    }

    /// <summary>Drops every retired surface, for final control disposal.</summary>
    internal void DropRetired() => _retired.Clear();

    private bool CompleteDeferredRelease(ISelectionAdornerSurfaceHost<TSurface> host)
    {
        if (!_releaseRequested)
            return false;

        Release(host);
        return true;
    }

    private void SetSource(ISelectionAdornerSurfaceHost<TSurface> host, TSurface? value)
    {
        _mutating = true;
        try
        {
            host.Source = value;
        }
        finally
        {
            _mutating = false;
        }
    }

    private void Retire(TSurface surface)
    {
        if (!IsRetired(surface))
            _retired.Add((surface, _frame));
    }
}
