using System;
using System.Collections.Generic;
using MarkdownRenderer.Accessibility;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Windows.Foundation;

namespace JitHub.Services.Markdown;

internal sealed class FirstViewportImagesReadyRendererProbe
{
    private readonly FirstViewportPaintCoverage _coverage = new();
    private LayoutSnapshot? _coveredSnapshot;
    private long _coveredRevision;
    private double _coveredScale;

#pragma warning disable MR1001 // Audit wiring intentionally targets the legacy control type.
    internal bool TryAcknowledgeAfterPaint(
        MarkdownRendererControl renderer,
        long generation,
        Rect paintedRegion)
#pragma warning restore MR1001
    {
        if (renderer.AutomationPipelineGeneration != generation ||
            renderer.AutomationSnapshotGeneration != generation ||
            renderer.CurrentSnapshot is not { } snapshot ||
            !renderer.TryGetVisibleDocumentRect(out Rect viewport) ||
            !IsFiniteNonEmptyRect(viewport) ||
            !IsFiniteNonEmptyRect(paintedRegion) ||
            !snapshot.IsBandMeasured(LazyLayoutBand.FromViewport(
                viewport.Top,
                viewport.Height,
                overscan: 0)))
        {
            ResetCoverage();
            return false;
        }

        long revision = snapshot.LayoutRevision;
        double scale = renderer.XamlRoot?.RasterizationScale ?? 0;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            ResetCoverage();
            return false;
        }

        if (!ReferenceEquals(_coveredSnapshot, snapshot) ||
            _coveredRevision != revision ||
            _coveredScale != scale)
        {
            ResetCoverage();
            _coveredSnapshot = snapshot;
            _coveredRevision = revision;
            _coveredScale = scale;
        }

        // An earlier region may have painted a placeholder. Begin a new
        // coverage epoch only once all visible rasters and SVG tiles are ready;
        // image completion invalidates the canvas and repaints the viewport.
        if (HasVisibleLoadingImages(
                renderer.AutomationImagePlans,
                renderer.AutomationImagePlanIndex,
                viewport,
                hasViewport: true))
        {
            _coverage.Reset();
            return false;
        }

        return _coverage.AddPaintedRegion(
            viewport.Left,
            viewport.Top,
            viewport.Width,
            viewport.Height,
            paintedRegion.Left,
            paintedRegion.Top,
            paintedRegion.Width,
            paintedRegion.Height);
    }

#pragma warning disable MR1001 // Audit wiring intentionally targets the legacy control type.
    internal bool IsCurrentAcknowledgement(MarkdownRendererControl renderer, long generation, Rect viewport)
#pragma warning restore MR1001
    {
        LayoutSnapshot? snapshot = renderer.CurrentSnapshot;
        return renderer.AutomationPipelineGeneration == generation &&
            renderer.AutomationSnapshotGeneration == generation &&
            snapshot is not null &&
            ReferenceEquals(_coveredSnapshot, snapshot) &&
            _coveredRevision == snapshot.LayoutRevision &&
            _coveredScale == renderer.XamlRoot?.RasterizationScale &&
            _coverage.Covers(viewport.Left, viewport.Top, viewport.Width, viewport.Height);
    }

    private void ResetCoverage()
    {
        _coverage.Reset();
        _coveredSnapshot = null;
        _coveredRevision = 0;
        _coveredScale = 0;
    }

#pragma warning disable MR1001 // Audit wiring intentionally targets the legacy control type.
    internal static bool HasVisibleLoadingImages(MarkdownRendererControl renderer)
#pragma warning restore MR1001
    {
        bool hasViewport = renderer.TryGetVisibleDocumentRect(out Rect viewport);
        return HasVisibleLoadingImages(
            renderer.AutomationImagePlans,
            hasViewport ? renderer.AutomationImagePlanIndex : null,
            viewport,
            hasViewport);
    }

    internal static bool HasVisibleLoadingImages(
        IReadOnlyList<ImageBox> imagePlans,
        ViewportBandIndex? imageIndex,
        Rect viewport,
        bool hasViewport)
    {
        if (imagePlans.Count == 0)
            return false;
        if (hasViewport && !IsFiniteNonEmptyRect(viewport))
            return true;

        ViewportRange range = imageIndex is not null
            ? imageIndex.Find(viewport.Top, viewport.Bottom)
            : new ViewportRange(0, imagePlans.Count);
        for (int index = range.Start; index < range.End; index++)
        {
            ImageBox image = imagePlans[
                imageIndex is not null ? imageIndex.GetBlockOrdinal(index) : index];
            if (!hasViewport)
            {
                if (image.AccessibilityState == MarkdownImageAccessibilityState.Loading)
                    return true;
                continue;
            }

            if (image.AccessibilityState == MarkdownImageAccessibilityState.Error)
            {
                continue;
            }

            if (!IsFiniteRect(image.Bounds))
                return true;
            if (image.Bounds.Width <= 0 || image.Bounds.Height <= 0)
                continue;
            if (!Intersects(image.Bounds, viewport))
                continue;

            if (!image.UsesSvgTilesForAutomation || image.HasBitmapForAutomation)
            {
                if (!image.HasBitmapForAutomation)
                    return true;
                continue;
            }

            if (HasMissingVisibleSvgTile(image, viewport))
                return true;
        }

        return false;
    }

    private static bool HasMissingVisibleSvgTile(ImageBox image, Rect viewport)
    {
        (int width, int height) = image.SvgRasterPixelSize;
        Rect destination = image.AutomationRenderDestination;
        if (width <= 0 || height <= 0 || !IsFiniteRect(destination) ||
            destination.Width <= 0 || destination.Height <= 0)
        {
            return true;
        }

        if (!Intersects(destination, viewport))
            return false;

        double visibleLeftDip = Math.Max(destination.Left, viewport.Left);
        double visibleTopDip = Math.Max(destination.Top, viewport.Top);
        double visibleRightDip = Math.Min(destination.Right, viewport.Right);
        double visibleBottomDip = Math.Min(destination.Bottom, viewport.Bottom);
        double visibleLeftPixels = (visibleLeftDip - destination.Left) * width / destination.Width;
        double visibleTopPixels = (visibleTopDip - destination.Top) * height / destination.Height;
        double visibleRightPixels = (visibleRightDip - destination.Left) * width / destination.Width;
        double visibleBottomPixels = (visibleBottomDip - destination.Top) * height / destination.Height;
        if (!double.IsFinite(visibleLeftPixels) || !double.IsFinite(visibleTopPixels) ||
            !double.IsFinite(visibleRightPixels) || !double.IsFinite(visibleBottomPixels))
        {
            return true;
        }

        int visibleLeft = Math.Clamp((int)Math.Floor(Math.Clamp(visibleLeftPixels, 0, width)), 0, width - 1);
        int visibleTop = Math.Clamp((int)Math.Floor(Math.Clamp(visibleTopPixels, 0, height)), 0, height - 1);
        int visibleRight = Math.Clamp((int)Math.Ceiling(Math.Clamp(visibleRightPixels, 0, width)), visibleLeft + 1, width);
        int visibleBottom = Math.Clamp((int)Math.Ceiling(Math.Clamp(visibleBottomPixels, 0, height)), visibleTop + 1, height);
        int firstTileX = visibleLeft / ImageBox.SvgTileSizePixels;
        int firstTileY = visibleTop / ImageBox.SvgTileSizePixels;
        int lastTileX = (visibleRight - 1) / ImageBox.SvgTileSizePixels;
        int lastTileY = (visibleBottom - 1) / ImageBox.SvgTileSizePixels;
        return !image.HasAllSvgTilesForAutomation(firstTileX, firstTileY, lastTileX, lastTileY);
    }

    private static bool Intersects(Rect left, Rect right) =>
        left.Right > right.Left && left.Left < right.Right &&
        left.Bottom > right.Top && left.Top < right.Bottom;

    internal static bool IsFiniteNonEmptyRect(Rect rect) =>
        IsFiniteRect(rect) && rect.Width > 0 && rect.Height > 0;

    internal static bool IsFiniteRect(Rect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
        double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom);
}
