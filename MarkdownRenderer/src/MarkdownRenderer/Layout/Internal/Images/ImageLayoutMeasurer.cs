using System;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using MarkdownRenderer.Images.Internal.Raster;
using MarkdownRenderer.Layout.Internal.Images.Loading;
using MarkdownRenderer.Layout.Internal.Images.Svg;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Internal.Images;

/// <summary>
/// Measures an image box as a block with its alt caption or as an inline
/// atomic cell, from the presented bitmap, a known or prefetched intrinsic
/// size, a carried completed result, or a placeholder reservation, and
/// proves when a completed load changes only paint.
/// </summary>
/// <remarks>
/// Sizing is intrinsic-first: the image renders at its natural size and only
/// downscales, preserving aspect, when it exceeds the available width. A
/// known SVG intrinsic size wins over the rasterized pixel dimensions, which
/// include the DPI multiplier. Inline images share the block loading and
/// cache pipeline but render no caption or margins and reserve a compact
/// placeholder before loading.
/// </remarks>
internal sealed class ImageLayoutMeasurer
{
    private readonly ImageBox _box;
    private readonly ImageMeasureCustody _measure;
    private readonly ImageLoadState _load;
    private readonly ImagePresentation _presentation;
    private readonly SvgImageState _svg;
    private readonly ImagePlaceholderLayout _placeholder;

    internal ImageLayoutMeasurer(
        ImageBox box,
        ImageMeasureCustody measure,
        ImageLoadState load,
        ImagePresentation presentation,
        SvgImageState svg,
        ImagePlaceholderLayout placeholder)
    {
        _box = box;
        _measure = measure;
        _load = load;
        _presentation = presentation;
        _svg = svg;
        _placeholder = placeholder;
    }

    private float MarginWidth => (float)(_box.Margin.Left + _box.Margin.Right);

    private float MarginHeight => (float)(_box.Margin.Top + _box.Margin.Bottom);

    /// <summary>Measures the block image and its caption; returns the block height.</summary>
    internal float Measure(float availableWidth)
    {
        _measure.AvailableWidth = availableWidth;
        if (_placeholder.ShouldCollapseFailedDecorativeImage)
        {
            _placeholder.CollapseFailedDecorativeImage(availableWidth);
            return 0;
        }

        float maxW = Math.Max(1f, availableWidth - MarginWidth);
        (float w, float h) = MeasureBlockImage(maxW);
        ApplyRequestedSize(ref w, ref h, maxW);
        _measure.ImageWidth = w;
        _measure.ImageHeight = h;
        if (_presentation.Bitmap is null)
        {
            _placeholder.UpdatePlaceholder(w, h);
        }

        float replacementCaptionHeight = ReplaceCaption(maxW);
        float contentHeight = Math.Max(h, _box.ParagraphLineHeight);
        float total = contentHeight + replacementCaptionHeight + MarginHeight;
        _box.SetMeasuredBounds(new Rect(0, 0, availableWidth, total));
        return total;
    }

    private (float Width, float Height) MeasureBlockImage(float maxW)
    {
        Size rasterIntrinsicSize = GetRasterMeasurementIntrinsicSize();
        if (_presentation.Bitmap is { } bmp)
        {
            (float bw, float bh) = GetPresentedIntrinsicSize(bmp);
            float scale = bw > 0 ? Math.Min(1f, maxW / bw) : 1f;
            return (bw * scale, bh * scale);
        }

        if (!_load.IsSvg && rasterIntrinsicSize.Width > 0 && rasterIntrinsicSize.Height > 0)
        {
            float scale = Math.Min(1f, maxW / (float)rasterIntrinsicSize.Width);
            return ((float)rasterIntrinsicSize.Width * scale, (float)rasterIntrinsicSize.Height * scale);
        }

        if (TryGetCarriedMeasurementIntrinsicSize(out Size carriedSize))
        {
            float scale = Math.Min(1f, maxW / (float)carriedSize.Width);
            return ((float)carriedSize.Width * scale, (float)carriedSize.Height * scale);
        }

        Size svgIntrinsic = _svg.IntrinsicSize;
        if (_load.IsSvg && (svgIntrinsic.Width > 0 || _load.IsStarted))
        {
            // SVG load is in flight. Reserve space using the intrinsic size
            // we recovered from the cache; fall back to a 16:9-ish band
            // when we have no intrinsic at all yet.
            float bw = svgIntrinsic.Width > 0 ? (float)svgIntrinsic.Width : maxW;
            float bh = svgIntrinsic.Height > 0 ? (float)svgIntrinsic.Height : 200f;
            float scale = bw > 0 ? Math.Min(1f, maxW / bw) : 1f;
            return (bw * scale, bh * scale);
        }

        // Placeholder height = 32px alt-text band, stretched to column.
        return (maxW, 32f);
    }

    // Builds a replacement before detaching the current caption. A factory
    // or Options failure leaves the source placement intact, and the
    // candidate's pre-reserved record retains any native layout.
    private float ReplaceCaption(float maxW)
    {
        CanvasTextLayout? replacementCaption = null;
        NativeResourceRetirementRecord? replacementCaptionRecord = null;
        float replacementCaptionHeight = 0f;
        if (!string.IsNullOrEmpty(_box.Alt))
        {
            MarkdownLayoutContext context = _box.Context;
            var cs = context.ThemeSnapshot.GetStyle(MarkdownElementKeys.ImageCaption);
            using SharedCanvasTextFormatCache.Lease cfmtLease = context.RenderResources.TextFormats.Acquire(
                cs,
                CanvasWordWrapping.Wrap,
                CanvasHorizontalAlignment.Center,
                context.FlowDirection,
                context.Language,
                true,
                CanvasVerticalAlignment.Top);
            replacementCaption = _measure.CreateOwnedTextLayout(
                _box.Alt,
                cfmtLease.Format,
                maxW,
                float.MaxValue,
                out replacementCaptionRecord);
            try
            {
                replacementCaptionHeight = (float)replacementCaption.LayoutBounds.Height
                                 + (float)(cs.Margin.Top + cs.Margin.Bottom);
            }
            catch
            {
                replacementCaptionRecord.Publish();
                throw;
            }
        }

        _measure.ReplaceCaption(replacementCaption, replacementCaptionRecord, replacementCaptionHeight);
        return replacementCaptionHeight;
    }

    /// <summary>
    /// Measures the image as an inline atomic cell. The caller holds the
    /// measure lock and has checked the box and its checkpoint.
    /// </summary>
    internal Size MeasureInlineNoLock(float availableWidth, float lineHeight)
    {
        _measure.IsInlineLayout = true;
        _measure.AvailableWidth = availableWidth;
        if (_placeholder.ShouldCollapseFailedDecorativeImage)
        {
            _placeholder.CollapseFailedDecorativeImage(availableWidth);
            return new Size(0, 0);
        }

        float maxW = Math.Max(1f, availableWidth);
        (float w, float h) = MeasureInlineImage(maxW, lineHeight);
        ApplyRequestedSize(ref w, ref h, maxW);
        _measure.ImageWidth = w;
        _measure.ImageHeight = h;
        if (_presentation.Bitmap is null)
        {
            _placeholder.UpdatePlaceholder(w, h);
        }
        _measure.CaptionHeight = 0f;
        _measure.ReplaceCaption(null, null, 0f);
        _box.SetMeasuredBounds(new Rect(0, 0, w, h));
        return new Size(w, h);
    }

    private (float Width, float Height) MeasureInlineImage(float maxW, float lineHeight)
    {
        Size rasterIntrinsicSize = GetRasterMeasurementIntrinsicSize();
        if (_placeholder.ShouldExpandInlineFailure)
        {
            float failureHeight = Math.Clamp(lineHeight, 20f, 28f);
            return (_placeholder.MeasureInlineFailureWidth(maxW, failureHeight), failureHeight);
        }

        if (_presentation.Bitmap is { } bmp)
        {
            (float bw, float bh) = GetPresentedIntrinsicSize(bmp);
            float scale = bw > 0 ? Math.Min(1f, maxW / bw) : 1f;
            return (Math.Max(1f, bw * scale), Math.Max(1f, bh * scale));
        }

        if (!_load.IsSvg && rasterIntrinsicSize.Width > 0 && rasterIntrinsicSize.Height > 0)
        {
            float scale = Math.Min(1f, maxW / (float)rasterIntrinsicSize.Width);
            return (Math.Max(1f, (float)rasterIntrinsicSize.Width * scale),
                Math.Max(1f, (float)rasterIntrinsicSize.Height * scale));
        }

        if (TryGetCarriedMeasurementIntrinsicSize(out Size carriedSize))
        {
            float scale = Math.Min(1f, maxW / (float)carriedSize.Width);
            return (Math.Max(1f, (float)carriedSize.Width * scale), Math.Max(1f, (float)carriedSize.Height * scale));
        }

        Size svgIntrinsic = _svg.IntrinsicSize;
        if (_load.IsSvg && (svgIntrinsic.Width > 0 || _load.IsStarted))
        {
            float bw = svgIntrinsic.Width > 0 ? (float)svgIntrinsic.Width : Math.Max(16f, lineHeight);
            float bh = svgIntrinsic.Height > 0 ? (float)svgIntrinsic.Height : Math.Max(16f, lineHeight);
            float scale = bw > 0 ? Math.Min(1f, maxW / bw) : 1f;
            return (Math.Max(1f, bw * scale), Math.Max(1f, bh * scale));
        }

        float h = Math.Clamp(lineHeight, 16f, 32f);
        return (h, h);
    }

    // The presented bitmap's size in DIPs: the accepted SVG or raster
    // intrinsic size when known, else the bitmap's own size.
    private (float Width, float Height) GetPresentedIntrinsicSize(CanvasBitmap bmp)
    {
        Size svgIntrinsic = _svg.IntrinsicSize;
        Size rasterIntrinsic = _presentation.RasterIntrinsicSize;
        if (_load.IsSvg && svgIntrinsic.Width > 0 && svgIntrinsic.Height > 0)
            return ((float)svgIntrinsic.Width, (float)svgIntrinsic.Height);
        if (!_load.IsSvg && rasterIntrinsic.Width > 0 && rasterIntrinsic.Height > 0)
            return ((float)rasterIntrinsic.Width, (float)rasterIntrinsic.Height);
        return ((float)bmp.Size.Width, (float)bmp.Size.Height);
    }

    /// <summary>Sets the document-coordinate rectangle used by an inline image.</summary>
    internal void SetInlineBounds(Rect rect)
    {
        _box.SetMeasuredBounds(rect);
        if (_placeholder.ShouldCollapseFailedDecorativeImage)
        {
            _measure.ImageWidth = 0;
            _measure.ImageHeight = 0;
            return;
        }

        _measure.ImageWidth = (float)Math.Max(1, rect.Width);
        _measure.ImageHeight = (float)Math.Max(1, rect.Height);
    }

    /// <summary>Applies the authored width and height to a natural size.</summary>
    internal void ApplyRequestedSize(ref float width, ref float height, float availableWidth) =>
        ImageRequestedSize.Apply(_box.RequestedWidth, _box.RequestedHeight, ref width, ref height, availableWidth);

    // A scalar hint only: do not claim an authoritative source, retain a
    // buffer/lease, start a load or mutate private-placement descriptor
    // fences. Cache misses never parse or wait during measurement.
    private Size GetRasterMeasurementIntrinsicSize()
    {
        Size known = _presentation.RasterIntrinsicSize;
        if (_load.IsSvg || _presentation.Bitmap is not null || _load.IsFailed ||
            known.Width > 0 || known.Height > 0)
        {
            return known;
        }

        MarkdownLayoutContext context = _box.Context;
        if (context.PerformanceSession is not { IsDisposed: false } session ||
            !session.Options.PrefetchDocumentImages ||
            !session.Options.UseDisplaySizedRasterDecode ||
            context.ImageResolver is not IMarkdownPerformanceDocumentScope scope ||
            !scope.TryGetPrefetchedRasterIntrinsicMetadata(_box.Url, out RasterIntrinsicMetadata metadata))
        {
            return default;
        }

        return new Size(metadata.Width, metadata.Height);
    }

    /// <summary>
    /// The intrinsic size this box measures with while it reacquires a
    /// carried completed image, so the rebuilt layout matches the layout the
    /// image already had. Its own known size always wins.
    /// </summary>
    private bool TryGetCarriedMeasurementIntrinsicSize(out Size size)
    {
        size = default;
        if (_load.CarriedCompletion is not { } carried || _presentation.Bitmap is not null || _load.IsFailed)
            return false;
        Size svgIntrinsic = _svg.IntrinsicSize;
        Size rasterIntrinsic = _presentation.RasterIntrinsicSize;
        if (_load.IsSvg && svgIntrinsic.Width > 0 && svgIntrinsic.Height > 0)
            return false;
        if (!_load.IsSvg && rasterIntrinsic.Width > 0 && rasterIntrinsic.Height > 0)
            return false;
        size = carried.IntrinsicSize;
        return carried.HasUsableIntrinsicSize;
    }

    /// <summary>
    /// True when the committed geometry is exactly what
    /// <paramref name="intrinsicSize"/> measures to, so publishing the decoded
    /// raster needs no relayout. Proves actual committed geometry, not just
    /// matching advisory source dimensions: inline checkpoint rollback and
    /// clipped inline placement can restore a different rectangle.
    /// </summary>
    internal bool HasExactReservedRasterGeometry(Size intrinsicSize)
    {
        if (_box.Lifetime.IsRetired || _load.IsSvg || _load.IsFailed ||
            _measure.CheckpointActive || !float.IsFinite(_measure.AvailableWidth) ||
            _measure.AvailableWidth <= 0 || !double.IsFinite(intrinsicSize.Width) ||
            !double.IsFinite(intrinsicSize.Height) || intrinsicSize.Width <= 0 ||
            intrinsicSize.Height <= 0 || intrinsicSize.Width > int.MaxValue ||
            intrinsicSize.Height > int.MaxValue)
        {
            return false;
        }

        float maxWidth = Math.Max(1f, _measure.IsInlineLayout ? _measure.AvailableWidth :
            _measure.AvailableWidth - MarginWidth);
        float scale = Math.Min(1f, maxWidth / (float)intrinsicSize.Width);
        float width = (float)intrinsicSize.Width * scale;
        float height = (float)intrinsicSize.Height * scale;
        if (_measure.IsInlineLayout)
        {
            width = Math.Max(1f, width);
            height = Math.Max(1f, height);
        }
        ApplyRequestedSize(ref width, ref height, maxWidth);
        if (_measure.ImageWidth != width || _measure.ImageHeight != height)
            return false;

        Rect bounds = _box.Bounds;
        return _measure.IsInlineLayout
            ? bounds.Width == width && bounds.Height == height && _measure.CaptionHeight == 0
            : bounds.Width == _measure.AvailableWidth && bounds.Height ==
                Math.Max(height, _box.ParagraphLineHeight) + _measure.CaptionHeight +
                MarginHeight;
    }

    /// <summary>
    /// Returns whether publishing the completed raster can affect only paint.
    /// The authored width/height pair makes its measured rectangle independent
    /// of decoded intrinsic dimensions; the snapshot separately proves that
    /// the owning layout still has its last successful placement.
    /// </summary>
    internal bool TryCanUsePaintOnlyCompletion()
    {
        IMarkdownPerformanceSessionInternal? session = _box.Context.PerformanceSession;
        if (session is null || session.IsDisposed || !session.Options.ReleaseOffscreenImageResources)
        {
            return false;
        }

        if (!Monitor.TryEnter(_measure.Sync))
            return false;

        try
        {
            if (_box.Lifetime.IsRetired || _load.IsFailed || !_load.IsStarted ||
                _box.Lifetime.LifetimeCancellation.IsCancellationRequested || _measure.CheckpointActive ||
                _load.IsSvg || _presentation.Bitmap is null ||
                !TryGetAuthoredSize(out float expectedWidth, out float expectedHeight))
            {
                return false;
            }

            return _measure.ImageWidth == expectedWidth && _measure.ImageHeight == expectedHeight &&
                   (!_measure.IsInlineLayout ||
                       (_box.Bounds.Width == expectedWidth && _box.Bounds.Height == expectedHeight));
        }
        finally
        {
            Monitor.Exit(_measure.Sync);
        }
    }

    // The measured size of a finite, positive authored width/height pair at
    // the current available width.
    private bool TryGetAuthoredSize(out float expectedWidth, out float expectedHeight)
    {
        expectedWidth = 0f;
        expectedHeight = 0f;
        if (_box.RequestedWidth is not { IsPercent: false } width ||
            !float.IsFinite(width.Value) || width.Value <= 0 ||
            _box.RequestedHeight is not { IsPercent: false } height ||
            !float.IsFinite(height.Value) || height.Value <= 0 ||
            !float.IsFinite(_measure.AvailableWidth) || _measure.AvailableWidth <= 0)
        {
            return false;
        }

        float available = Math.Max(
            1f,
            _measure.IsInlineLayout
                ? _measure.AvailableWidth
                : _measure.AvailableWidth - MarginWidth);
        if (!float.IsFinite(available))
            return false;

        float requestedWidth = width.Resolve(available);
        float requestedHeight = height.Resolve(available);
        if (!float.IsFinite(requestedWidth) || requestedWidth <= 0 ||
            !float.IsFinite(requestedHeight) || requestedHeight <= 0)
        {
            return false;
        }

        float scale = Math.Min(1f, available / requestedWidth);
        expectedWidth = Math.Max(1f, requestedWidth * scale);
        expectedHeight = Math.Max(1f, requestedHeight * scale);
        return float.IsFinite(expectedWidth) && float.IsFinite(expectedHeight);
    }
}
