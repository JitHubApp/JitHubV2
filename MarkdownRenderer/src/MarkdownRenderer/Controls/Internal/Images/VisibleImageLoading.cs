using MarkdownRenderer.Controls.Internal.Embeds;
using MarkdownRenderer.Controls.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Placement;

namespace MarkdownRenderer.Controls.Internal.Images;

/// <summary>
/// Starts loading the images within the look-ahead band of a visible
/// realization pass and reconciles which images stay resident.
/// </summary>
internal sealed class VisibleImageLoading
{
    private readonly MarkdownRendererControl _control;
    private readonly LazyLayoutRealization _lazyLayout;
    private readonly ImageResidency _imageResidency;
    private readonly ImagePlanSet _imagePlans;
    private readonly EmbedRealization _embeds;

    internal VisibleImageLoading(
        MarkdownRendererControl control,
        LazyLayoutRealization lazyLayout,
        ImageResidency imageResidency,
        ImagePlanSet imagePlans,
        EmbedRealization embeds)
    {
        _control = control;
        _lazyLayout = lazyLayout;
        _imageResidency = imageResidency;
        _imagePlans = imagePlans;
        _embeds = embeds;
    }

    /// <summary>Returns false when <paramref name="pass"/> was superseded.</summary>
    internal bool Realize(
        in VisibleRealizationPass pass,
        double top,
        double viewportHeight,
        double bottom)
    {
        // Lazy image loading: trigger EnsureLoading for images within the
        // viewport + LazyImageOverscanPx band.  Images already started (cached
        // or previously triggered) are silently skipped by EnsureLoading().
        LazyLayoutBand imageBand = _control.PerformanceSession is { } performanceSession
            ? LazyLayoutBand.FromDirectionalViewport(
                top,
                viewportHeight,
                performanceSession.Options.LookAheadViewports,
                _lazyLayout.ScrollVelocity)
            : LazyLayoutBand.FromViewport(top, viewportHeight, MarkdownRendererControl.LazyImageOverscanPx);
        double imgLoadTop = imageBand.Top;
        double imgLoadBottom = imageBand.Bottom;
        ViewportBandIndex? imageIndex = pass.ImageIndex;
        ViewportRange imageRange = imageIndex?.Find(imgLoadTop, imgLoadBottom) ?? default;
        _imageResidency.Reconcile(imageBand, imageRange);
        if (!_embeds.IsCurrent(pass))
            return false;

        for (int index = imageRange.Start; index < imageRange.End; index++)
        {
            int imageOrdinal = imageIndex!.GetBlockOrdinal(index);
            (double imageTop, double imageBottom) = imageIndex.GetBounds(index);
            var img = _imagePlans[imageOrdinal];
            if (imageBottom >= imgLoadTop && imageTop <= imgLoadBottom)
            {
                bool intersectsViewport = imageBottom >= top && imageTop <= bottom;
                if (!_embeds.IsCurrent(pass))
                    return false;

                // Paint culls offscreen images before ImageBox can observe the
                // viewport. Keep current residency visibility separate from the
                // monotonic load-demand ticket, including images in overscan.
                object? svgObservation = img.CaptureSvgViewportObservationToken();
                img.ObserveSvgViewportVisibility(svgObservation, intersectsViewport);
                img.EnsureLoading(isVisible: intersectsViewport);
                if (!_embeds.IsCurrent(pass))
                    return false;
            }
        }

        return true;
    }
}
