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
    private readonly List<Rect> _pendingImageRegions = new();

    internal long PaintCallbackCount { get; private set; }
    internal long PaintedRegionCount { get; private set; }
    internal Rect LastPaintedRegion { get; private set; }
    internal int VisibleLoadingImageCount { get; private set; }
    internal bool VisibleImagesLoading { get; private set; } = true;
    internal int PendingImageRegionCount => _pendingImageRegions.Count;
    internal string LastReason { get; private set; } = "not-started";
    internal double ViewportLeft { get; private set; }
    internal double ViewportTop { get; private set; }
    internal double ViewportWidth { get; private set; }
    internal double ViewportHeight { get; private set; }
    internal double RasterizationScale { get; private set; }
    internal long LayoutRevision { get; private set; }
    internal double CoveredArea => _coverage.CoveredArea;
    internal double ViewportArea => _coverage.ViewportArea;
    internal int UncoveredRegionCount => _coverage.UncoveredRegionCount;
    internal double LastPaintedIntersectionArea => _coverage.LastPaintedIntersectionArea;
    internal bool HasPostArmPaintedRegion => PaintedRegionCount > 0;

#pragma warning disable MR1001 // Audit wiring intentionally targets the legacy control type.
    internal bool TryAcknowledgeAfterPaint(
        MarkdownRendererControl renderer,
        long generation,
        Rect paintedRegion)
#pragma warning restore MR1001
    {
        PaintCallbackCount++;
        PaintedRegionCount++;
        LastPaintedRegion = paintedRegion;
        LastReason = "validating-paint";

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
            LastReason = "generation-or-measured-viewport-mismatch";
            return false;
        }

        long revision = snapshot.LayoutRevision;
        double scale = renderer.XamlRoot?.RasterizationScale ?? 0;
        if (!double.IsFinite(scale) || scale <= 0)
        {
            ResetCoverage();
            LastReason = "invalid-rasterization-scale";
            return false;
        }

        ViewportLeft = viewport.Left;
        ViewportTop = viewport.Top;
        ViewportWidth = viewport.Width;
        ViewportHeight = viewport.Height;
        LayoutRevision = revision;
        RasterizationScale = scale;
        if (!_coverage.EnsureIdentity(snapshot, generation, revision, scale, viewport))
        {
            LastReason = "invalid-coverage-identity";
            return false;
        }

        _pendingImageRegions.Clear();
        bool hasVisibleLoadingImages = TryCollectVisibleLoadingImages(
                renderer.AutomationImagePlans,
                renderer.AutomationImagePlanIndex,
                viewport,
                hasViewport: true,
                _pendingImageRegions,
                out int visibleLoadingImageCount,
                out _,
                out bool masksValid,
                out bool pendingMaskBudgetExceeded);
        VisibleLoadingImageCount = visibleLoadingImageCount;
        VisibleImagesLoading = hasVisibleLoadingImages || !masksValid;
        if (!masksValid)
        {
            // Do not let coverage accumulated before an invalid/over-budget
            // mask snapshot be reused by a later, smaller ready snapshot.
            _coverage.Reset();
            LastReason = pendingMaskBudgetExceeded
                ? "pending-mask-count-budget-exceeded"
                : "invalid-pending-image-mask";
            return false;
        }

        // A callback while images are loading still proves the non-image part
        // of this region was painted. Keep the pending bitmap/tile pixels
        // uncovered so a later successful image paint can complete exact
        // viewport coverage without requiring an unrelated full repaint.
        bool covered = _coverage.AddPaintedRegionExcluding(
            viewport.Left,
            viewport.Top,
            viewport.Width,
            viewport.Height,
            paintedRegion.Left,
            paintedRegion.Top,
            paintedRegion.Width,
            paintedRegion.Height,
            hasVisibleLoadingImages ? _pendingImageRegions : Array.Empty<Rect>());
        if (_coverage.LastFailureReason is { } failureReason)
        {
            LastReason = failureReason;
            return false;
        }
        if (covered && !hasVisibleLoadingImages)
        {
            LastReason = "viewport-covered-after-ready-paint";
            return true;
        }

        LastReason = hasVisibleLoadingImages
            ? "waiting-for-visible-image-paint"
            : "waiting-for-viewport-coverage";
        return false;
    }

#pragma warning disable MR1001 // Audit wiring intentionally targets the legacy control type.
    internal bool IsCurrentAcknowledgement(MarkdownRendererControl renderer, long generation, Rect viewport)
#pragma warning restore MR1001
    {
        LayoutSnapshot? snapshot = renderer.CurrentSnapshot;
        return renderer.AutomationPipelineGeneration == generation &&
            renderer.AutomationSnapshotGeneration == generation &&
            snapshot is not null &&
            _coverage.IsIdentityCurrent(
                snapshot,
                generation,
                snapshot.LayoutRevision,
                renderer.XamlRoot?.RasterizationScale ?? 0,
                viewport) &&
            _coverage.Covers(viewport.Left, viewport.Top, viewport.Width, viewport.Height);
    }

    private void ResetCoverage()
    {
        _coverage.Reset();
        _pendingImageRegions.Clear();
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
        return TryCollectVisibleLoadingImages(
            imagePlans,
            imageIndex,
            viewport,
            hasViewport,
            pendingRegions: null,
            out _,
            out _,
            out _,
            out _);
    }

    private static bool TryCollectVisibleLoadingImages(
        IReadOnlyList<ImageBox> imagePlans,
        ViewportBandIndex? imageIndex,
        Rect viewport,
        bool hasViewport,
        List<Rect>? pendingRegions,
        out int visibleLoadingImageCount,
        out int missingVisibleSvgTileCount,
        out bool masksValid,
        out bool pendingMaskBudgetExceeded)
    {
        visibleLoadingImageCount = 0;
        missingVisibleSvgTileCount = 0;
        masksValid = true;
        pendingMaskBudgetExceeded = false;
        if (imagePlans.Count == 0)
            return false;
        if (hasViewport && !IsFiniteNonEmptyRect(viewport))
        {
            masksValid = false;
            return true;
        }

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
                {
                    visibleLoadingImageCount++;
                    return true;
                }
                continue;
            }

            if (image.AccessibilityState == MarkdownImageAccessibilityState.Error)
                continue;

            Rect destination = image.AutomationRenderDestination;
            if (!IsFiniteRect(image.Bounds) || !IsFiniteNonEmptyRect(destination))
            {
                masksValid = false;
                return true;
            }
            if (!Intersects(destination, viewport))
                continue;

            if (image.UsesSvgTilesForAutomation && !image.HasBitmapForAutomation)
            {
                if (!AppendMissingVisibleSvgTileRegions(
                        image,
                        viewport,
                        pendingRegions,
                        out int missingTileCount,
                        out bool tileMaskValid,
                        out bool tileMaskBudgetExceeded))
                {
                    if (!tileMaskValid)
                    {
                        masksValid = false;
                        pendingMaskBudgetExceeded = tileMaskBudgetExceeded;
                        if (tileMaskBudgetExceeded)
                            visibleLoadingImageCount++;
                        return true;
                    }

                    continue;
                }

                visibleLoadingImageCount++;
                missingVisibleSvgTileCount += missingTileCount;
                if (pendingRegions is null)
                    return true;
                continue;
            }

            if (!image.HasBitmapForAutomation)
            {
                visibleLoadingImageCount++;
                if (pendingRegions is not null)
                {
                    if (!TryAppendPendingRegion(pendingRegions, destination))
                    {
                        masksValid = false;
                        pendingMaskBudgetExceeded = true;
                        return true;
                    }
                }
                else
                {
                    return true;
                }
            }
        }

        return visibleLoadingImageCount > 0;
    }

    private static bool AppendMissingVisibleSvgTileRegions(
        ImageBox image,
        Rect viewport,
        List<Rect>? pendingRegions,
        out int missingTileCount,
        out bool masksValid,
        out bool pendingMaskBudgetExceeded)
    {
        missingTileCount = 0;
        masksValid = true;
        pendingMaskBudgetExceeded = false;
        (int width, int height) = image.SvgRasterPixelSize;
        Rect destination = image.AutomationRenderDestination;
        if (width <= 0 || height <= 0 || !IsFiniteNonEmptyRect(destination))
        {
            masksValid = false;
            return false;
        }

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
            masksValid = false;
            return false;
        }

        int visibleLeft = Math.Clamp((int)Math.Floor(Math.Clamp(visibleLeftPixels, 0, width)), 0, width - 1);
        int visibleTop = Math.Clamp((int)Math.Floor(Math.Clamp(visibleTopPixels, 0, height)), 0, height - 1);
        int visibleRight = Math.Clamp((int)Math.Ceiling(Math.Clamp(visibleRightPixels, 0, width)), visibleLeft + 1, width);
        int visibleBottom = Math.Clamp((int)Math.Ceiling(Math.Clamp(visibleBottomPixels, 0, height)), visibleTop + 1, height);
        int firstTileX = visibleLeft / ImageBox.SvgTileSizePixels;
        int firstTileY = visibleTop / ImageBox.SvgTileSizePixels;
        int lastTileX = (visibleRight - 1) / ImageBox.SvgTileSizePixels;
        int lastTileY = (visibleBottom - 1) / ImageBox.SvgTileSizePixels;

        for (int tileY = firstTileY; tileY <= lastTileY; tileY++)
        {
            for (int tileX = firstTileX; tileX <= lastTileX; tileX++)
            {
                if (image.HasAllSvgTilesForAutomation(tileX, tileY, tileX, tileY))
                    continue;

                missingTileCount++;
                if (pendingRegions is not null)
                {
                    double tileLeft = destination.Left + tileX * ImageBox.SvgTileSizePixels * destination.Width / width;
                    double tileTop = destination.Top + tileY * ImageBox.SvgTileSizePixels * destination.Height / height;
                    double tileRight = destination.Left + Math.Min(width, (tileX + 1) * ImageBox.SvgTileSizePixels) * destination.Width / width;
                    double tileBottom = destination.Top + Math.Min(height, (tileY + 1) * ImageBox.SvgTileSizePixels) * destination.Height / height;
                    Rect tileDestination = new(tileLeft, tileTop, tileRight - tileLeft, tileBottom - tileTop);
                    if (!IsFiniteNonEmptyRect(tileDestination))
                    {
                        masksValid = false;
                        return false;
                    }
                    if (!TryAppendPendingRegion(pendingRegions, tileDestination))
                    {
                        masksValid = false;
                        pendingMaskBudgetExceeded = true;
                        return false;
                    }
                }
            }
        }

        return missingTileCount > 0;
    }

    private static bool TryAppendPendingRegion(List<Rect> pendingRegions, Rect region)
    {
        if (pendingRegions.Count >= FirstViewportPaintCoverage.MaximumPendingMasks)
            return false;

        pendingRegions.Add(region);
        return true;
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
