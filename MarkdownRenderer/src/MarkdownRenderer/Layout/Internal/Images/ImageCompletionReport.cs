using System;
using Windows.Foundation;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Layout.Internal.Images.Loading;
using MarkdownRenderer.Layout.Internal.Images.Placement;
using MarkdownRenderer.Layout.Internal.Images.Svg;

namespace MarkdownRenderer.Layout.Internal.Images;

/// <summary>
/// Reports what an image box has completed: its accessibility state, its SVG
/// title and description, whether a visible image still awaits its pixels,
/// and the completed result a rebuild of the same document carries forward.
/// </summary>
/// <remarks>
/// A carried completion lets a rebuilt box report Loaded and measure at its
/// earlier size while it reacquires pixels. It is immutable and never owns
/// pixels or native resources.
/// </remarks>
internal sealed class ImageCompletionReport
{
    private readonly ImageBox _box;
    private readonly ImageLoadState _load;
    private readonly ImagePresentation _presentation;
    private readonly SvgImageState _svg;

    internal ImageCompletionReport(
        ImageBox box,
        ImageLoadState load,
        ImagePresentation presentation,
        SvgImageState svg)
    {
        _box = box;
        _load = load;
        _presentation = presentation;
        _svg = svg;
    }

    /// <summary>Current state projected into the image's UIA accessible name.</summary>
    internal MarkdownImageAccessibilityState AccessibilityState => _load.IsFailed
        ? MarkdownImageAccessibilityState.Error
        : _presentation.HasPresentedImage || _presentation.Bitmap is not null ||
          _box.SvgTileCache.HasRenderedTile || _load.CarriedCompletion is not null
            ? MarkdownImageAccessibilityState.Loaded
            : MarkdownImageAccessibilityState.Loading;

    /// <summary>
    /// True while a visible image cannot show its pixels yet: it is still
    /// loading, or it carries an earlier layout's completed result and has
    /// not presented in this layout.
    /// </summary>
    internal bool IsAwaitingVisiblePixels =>
        AccessibilityState == MarkdownImageAccessibilityState.Loading ||
        (_load.CarriedCompletion is not null && !_load.IsFailed && !_presentation.HasPresentedImage &&
         _presentation.Bitmap is null && !_box.SvgTileCache.HasRenderedTile);

    /// <summary>The SVG &lt;title&gt; value, or the carried one.</summary>
    internal string? SvgTitle => _svg.Title ?? _load.CarriedCompletion?.SvgTitle;

    /// <summary>The SVG &lt;desc&gt; value; without pixels, the failure description first.</summary>
    internal string? SvgDescription => _presentation.Bitmap is null
        ? _svg.FailureDescription ?? _svg.Description ?? _load.CarriedCompletion?.SvgDescription
        : _svg.Description;

    /// <summary>
    /// Captures this image's completed result for the next rebuild of the same
    /// document. An image that has not presented in this layout forwards the
    /// result it carried in, so consecutive rebuilds keep it. Call on the UI
    /// thread, which owns image publication.
    /// </summary>
    internal bool TryCaptureCompletedCarry(out CompletedImageCarry? carry)
    {
        carry = null;
        if (_box.Lifetime.IsDisposed || string.IsNullOrEmpty(_box.Url))
            return false;
        if (_load.IsFailed)
        {
            if (!_load.IsFailureCarryable)
                return false;
            carry = CompletedImageCarry.ForFailure(
                _box.Url,
                _load.IsSvg,
                _load.IsSvg ? _svg.IntrinsicSize : _presentation.RasterIntrinsicSize,
                _svg.FailureReason,
                _svg.FailureDescription);
            return true;
        }

        bool presented = _presentation.HasPresentedImage || _presentation.Bitmap is not null ||
            _box.SvgTileCache.HasRenderedTile;
        Size intrinsic = presented ? GetPresentedIntrinsicSize() : default;
        var candidate = new CompletedImageCarry(
            _box.Url,
            _load.IsSvg,
            intrinsic,
            _svg.Title ?? _load.CarriedCompletion?.SvgTitle,
            _svg.Description ?? _load.CarriedCompletion?.SvgDescription);
        if (presented && candidate.HasUsableIntrinsicSize)
        {
            carry = candidate;
            return true;
        }

        carry = _load.CarriedCompletion;
        return carry is not null;
    }

    private Size GetPresentedIntrinsicSize()
    {
        if (_load.IsSvg)
            return _svg.IntrinsicSize;

        Size rasterIntrinsic = _presentation.RasterIntrinsicSize;
        if (rasterIntrinsic.Width > 0 && rasterIntrinsic.Height > 0)
            return rasterIntrinsic;

        if (_presentation.Bitmap is { } bitmap)
        {
            try
            {
                return bitmap.Size;
            }
            catch (ObjectDisposedException)
            {
                return default;
            }
        }

        return default;
    }
}
