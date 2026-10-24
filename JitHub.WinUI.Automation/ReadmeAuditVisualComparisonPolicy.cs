namespace JitHub.WinUI.Automation;

internal readonly record struct ReadmeAuditVisualViewportProfile(
    int WidthDip,
    int HeightDip,
    double DeviceScaleFactor,
    string ColorScheme,
    int DocumentClientWidthDip,
    int BodyClientWidthDip,
    double ArticleWidthDip);

internal readonly record struct ReadmeAuditVisualViewportPosition(
    double ScrollTopViewportUnits,
    double ScrollPercent,
    double VerticalViewSize);

internal readonly record struct ReadmeAuditVisualTile(
    int Index,
    double RelativeY,
    int Width,
    int Height,
    string File);

internal readonly record struct ReadmeAuditVisualTileSegment(
    int TileIndex,
    string File,
    int SourceY,
    int DestinationY,
    int Height);

internal readonly record struct ReadmeAuditVisualCapturedViewport(
    double? DocumentTopDip,
    double ViewportHeightDip);

internal readonly record struct ReadmeAuditVisualCapturedViewportSize(
    double WidthDip,
    double HeightDip);

/// <summary>
/// Pure geometry and identity checks for the README audit's screenshot
/// comparison. The live GitHub view is diagnostic only; source replay is only
/// comparable when its inputs and viewport profile exactly match the native
/// capture.
/// </summary>
internal static class ReadmeAuditVisualComparisonPolicy
{
    public static bool TryResolveCapturedViewportDipSize(
        double measuredHostWidthPixels,
        double measuredHostHeightPixels,
        double pixelsPerDip,
        IReadOnlyList<ReadmeAuditVisualCapturedViewportSize> capturedTiles,
        out int widthDip,
        out int heightDip)
    {
        widthDip = 0;
        heightDip = 0;
        if (!double.IsFinite(measuredHostWidthPixels) || measuredHostWidthPixels <= 0 ||
            !double.IsFinite(measuredHostHeightPixels) || measuredHostHeightPixels <= 0 ||
            !double.IsFinite(pixelsPerDip) || pixelsPerDip <= 0 ||
            capturedTiles.Count == 0)
        {
            return false;
        }

        double capturedWidthDip = capturedTiles[0].WidthDip;
        double capturedHeightDip = capturedTiles[0].HeightDip;
        if (!double.IsFinite(capturedWidthDip) || capturedWidthDip <= 0 ||
            capturedWidthDip > 8192 || capturedWidthDip != Math.Floor(capturedWidthDip) ||
            !double.IsFinite(capturedHeightDip) || capturedHeightDip <= 0 ||
            capturedHeightDip > 8192 || capturedHeightDip != Math.Floor(capturedHeightDip))
        {
            return false;
        }
        widthDip = (int)capturedWidthDip;
        heightDip = (int)capturedHeightDip;
        for (int index = 1; index < capturedTiles.Count; index++)
        {
            if (capturedTiles[index].WidthDip != widthDip ||
                capturedTiles[index].HeightDip != heightDip)
            {
                return false;
            }
        }

        // UIA supplies physical integer bounds, while the renderer reports
        // ceil(measured viewport DIPs). They may differ by less than one DIP
        // at fractional scales. Use the renderer's capture dimensions for
        // the Edge CSS viewport, but reject a genuinely different host area.
        return Math.Abs(widthDip * pixelsPerDip - measuredHostWidthPixels) <= pixelsPerDip &&
               Math.Abs(heightDip * pixelsPerDip - measuredHostHeightPixels) <= pixelsPerDip;
    }

    public static bool IsCaptureWithinMeasuredHostViewport(
        int capturedWidthPixels,
        int capturedHeightPixels,
        int measuredHostWidthPixels,
        int measuredHostHeightPixels)
    {
        if (capturedWidthPixels <= 0 || capturedHeightPixels <= 0 ||
            measuredHostWidthPixels <= 0 || measuredHostHeightPixels <= 0)
        {
            return false;
        }

        return Math.Abs((long)capturedWidthPixels - measuredHostWidthPixels) <= 1 &&
               Math.Abs((long)capturedHeightPixels - measuredHostHeightPixels) <= 1;
    }

    public static bool HasMonotonicNativeViewportCoverage(
        IReadOnlyList<ReadmeAuditVisualCapturedViewport> viewports)
    {
        if (viewports.Count == 0)
            return false;

        double previousTop = 0;
        double previousHeight = 0;
        for (int index = 0; index < viewports.Count; index++)
        {
            ReadmeAuditVisualCapturedViewport viewport = viewports[index];
            if (viewport.DocumentTopDip is not double top ||
                !double.IsFinite(top) || top < 0 ||
                !double.IsFinite(viewport.ViewportHeightDip) || viewport.ViewportHeightDip <= 0)
            {
                return false;
            }

            if (index == 0 ? top > 0.5 :
                top <= previousTop || top - previousTop > previousHeight + 0.000001)
            {
                return false;
            }

            previousTop = top;
            previousHeight = viewport.ViewportHeightDip;
        }

        return true;
    }

    public static bool HasMatchedSourceProfile(
        string? nativeReadmeSha256,
        string? corpusReadmeSha256,
        string? replayReadmeSha256,
        ReadmeAuditVisualViewportProfile native,
        ReadmeAuditVisualViewportProfile replay,
        out string reason)
    {
        if (!IsSha256(nativeReadmeSha256) ||
            !IsSha256(corpusReadmeSha256) ||
            !IsSha256(replayReadmeSha256) ||
            !string.Equals(nativeReadmeSha256, corpusReadmeSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(nativeReadmeSha256, replayReadmeSha256, StringComparison.OrdinalIgnoreCase))
        {
            reason = "source-hash-mismatch";
            return false;
        }

        if (native.WidthDip <= 0 || native.HeightDip <= 0 ||
            native.WidthDip != replay.WidthDip || native.HeightDip != replay.HeightDip)
        {
            reason = "viewport-size-mismatch";
            return false;
        }

        if (native.DocumentClientWidthDip != native.WidthDip ||
            native.BodyClientWidthDip != native.WidthDip ||
            !double.IsFinite(native.ArticleWidthDip) ||
            Math.Abs(native.ArticleWidthDip - native.WidthDip) > 0.5 ||
            replay.DocumentClientWidthDip != replay.WidthDip ||
            replay.BodyClientWidthDip != replay.WidthDip ||
            !double.IsFinite(replay.ArticleWidthDip) ||
            Math.Abs(replay.ArticleWidthDip - native.ArticleWidthDip) > 0.5)
        {
            reason = "content-width-mismatch";
            return false;
        }

        if (!double.IsFinite(native.DeviceScaleFactor) || native.DeviceScaleFactor <= 0 ||
            !double.IsFinite(replay.DeviceScaleFactor) || replay.DeviceScaleFactor <= 0 ||
            Math.Abs(native.DeviceScaleFactor - replay.DeviceScaleFactor) > 0.000001)
        {
            reason = "device-scale-mismatch";
            return false;
        }

        if (!IsSupportedColorScheme(native.ColorScheme) ||
            !string.Equals(native.ColorScheme, replay.ColorScheme, StringComparison.OrdinalIgnoreCase))
        {
            reason = "color-scheme-mismatch";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static bool TryGetViewportPixelBounds(
        ReadmeAuditVisualViewportPosition position,
        int viewportHeightDip,
        double referencePixelsPerDip,
        out int topPixel,
        out int heightPixels)
    {
        topPixel = 0;
        heightPixels = 0;
        if (!double.IsFinite(position.ScrollTopViewportUnits) ||
            position.ScrollTopViewportUnits < 0 ||
            viewportHeightDip <= 0 ||
            !double.IsFinite(referencePixelsPerDip) ||
            referencePixelsPerDip <= 0)
        {
            return false;
        }

        double top = position.ScrollTopViewportUnits * viewportHeightDip * referencePixelsPerDip;
        double height = viewportHeightDip * referencePixelsPerDip;
        if (!double.IsFinite(top) || !double.IsFinite(height) ||
            top > int.MaxValue || height < 1 || height > int.MaxValue)
        {
            return false;
        }

        topPixel = checked((int)Math.Round(top));
        heightPixels = checked((int)Math.Round(height));
        return heightPixels > 0;
    }

    /// <summary>
    /// Uses the renderer's exact captured document top for source-page pixels.
    /// UIA percent/view-size is only an approximation of that top and can
    /// misalign otherwise identical content. When Edge's shorter page clamps
    /// first, compare the two actual terminal viewports only if the source
    /// replay documented that clamp. The caller must first validate the full
    /// replay movement profile and its terminal observations.
    /// </summary>
    public static bool TryGetReplayViewportPixelBounds(
        int nativeViewportIndex,
        int nativeViewportCount,
        IReadOnlyList<double>? replayCaptureOffsets,
        double nativeRendererDocumentTopDip,
        int edgeDocumentHeightDip,
        bool terminalClampObserved,
        int viewportHeightDip,
        int viewportHeightPixels,
        double referencePixelsPerDip,
        out int topPixel,
        out int heightPixels)
    {
        topPixel = 0;
        heightPixels = 0;
        if (replayCaptureOffsets is null || nativeViewportCount <= 0 ||
            replayCaptureOffsets.Count != nativeViewportCount ||
            nativeViewportIndex < 0 || nativeViewportIndex >= nativeViewportCount ||
            !double.IsFinite(nativeRendererDocumentTopDip) || nativeRendererDocumentTopDip < 0 ||
            edgeDocumentHeightDip < viewportHeightDip || viewportHeightDip <= 0 ||
            viewportHeightPixels <= 0)
        {
            return false;
        }

        double edgeCapturedOffset = replayCaptureOffsets[nativeViewportIndex];
        double edgeMaximumTopDip = edgeDocumentHeightDip - viewportHeightDip;
        if (!double.IsFinite(edgeCapturedOffset) || edgeCapturedOffset < 0)
            return false;

        double comparisonTopDip = nativeRendererDocumentTopDip;
        if (comparisonTopDip > edgeMaximumTopDip)
        {
            // One physical pixel of tolerance handles independent DIP-to-pixel
            // rounding. A larger difference requires an independently
            // documented source-replay terminal clamp; never silently crop a
            // missing bottom region as if Edge had rendered it.
            double toleranceDip = 1 / referencePixelsPerDip;
            if (comparisonTopDip - edgeMaximumTopDip > toleranceDip &&
                (!terminalClampObserved ||
                 Math.Abs(edgeCapturedOffset * viewportHeightDip - edgeMaximumTopDip) > toleranceDip))
            {
                return false;
            }

            comparisonTopDip = edgeMaximumTopDip;
        }

        if (!TryGetViewportPixelBounds(
            new ReadmeAuditVisualViewportPosition(
                comparisonTopDip / viewportHeightDip,
                ScrollPercent: 0,
                VerticalViewSize: 0),
            viewportHeightDip,
            referencePixelsPerDip,
            out topPixel,
            out _))
        {
            return false;
        }

        heightPixels = viewportHeightPixels;
        return true;
    }

    public static bool TryCreateTileSegments(
        IReadOnlyList<ReadmeAuditVisualTile> tiles,
        int viewportTopPixel,
        int viewportWidthPixels,
        int viewportHeightPixels,
        double pixelsPerMetadataUnit,
        out IReadOnlyList<ReadmeAuditVisualTileSegment> segments)
    {
        segments = Array.Empty<ReadmeAuditVisualTileSegment>();
        if (tiles.Count == 0 || viewportTopPixel < 0 ||
            viewportWidthPixels <= 0 || viewportHeightPixels <= 0 ||
            !double.IsFinite(pixelsPerMetadataUnit) || pixelsPerMetadataUnit <= 0 ||
            viewportTopPixel > int.MaxValue - viewportHeightPixels)
        {
            return false;
        }

        int viewportEnd = viewportTopPixel + viewportHeightPixels;
        int coveredThrough = viewportTopPixel;
        var result = new List<ReadmeAuditVisualTileSegment>();
        foreach (ReadmeAuditVisualTile tile in tiles.OrderBy(static tile => tile.RelativeY))
        {
            if (!double.IsFinite(tile.RelativeY) || tile.RelativeY < 0 ||
                tile.RelativeY > int.MaxValue || tile.Width <= 0 ||
                tile.Height <= 0 || string.IsNullOrWhiteSpace(tile.File))
            {
                return false;
            }

            double scaledTop = tile.RelativeY * pixelsPerMetadataUnit;
            double scaledWidth = tile.Width * pixelsPerMetadataUnit;
            double scaledHeight = tile.Height * pixelsPerMetadataUnit;
            if (!double.IsFinite(scaledTop) || !double.IsFinite(scaledWidth) ||
                !double.IsFinite(scaledHeight) || scaledTop > int.MaxValue ||
                scaledWidth > int.MaxValue || scaledHeight > int.MaxValue)
            {
                return false;
            }

            int tileTop = checked((int)Math.Round(scaledTop));
            int tileWidth = checked((int)Math.Round(scaledWidth));
            int tileHeight = checked((int)Math.Round(scaledHeight));
            // Edge tiles retain the canonical ceil-DIP viewport width; crop
            // their right edge to the measured physical capture, but never
            // claim coverage when the source is narrower than that capture.
            if (tileWidth < viewportWidthPixels || tileHeight <= 0 || tileTop > int.MaxValue - tileHeight)
                return false;
            int tileEnd = tileTop + tileHeight;
            if (tileEnd <= coveredThrough)
                continue;
            if (tileTop > coveredThrough)
                return false;

            int segmentEnd = Math.Min(tileEnd, viewportEnd);
            int segmentHeight = segmentEnd - coveredThrough;
            if (segmentHeight <= 0)
                continue;

            result.Add(new ReadmeAuditVisualTileSegment(
                tile.Index,
                tile.File,
                coveredThrough - tileTop,
                coveredThrough - viewportTopPixel,
                segmentHeight));
            coveredThrough = segmentEnd;
            if (coveredThrough == viewportEnd)
            {
                segments = result;
                return true;
            }
        }

        return false;
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static bool IsSupportedColorScheme(string? value) =>
        string.Equals(value, "light", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "dark", StringComparison.OrdinalIgnoreCase);
}
