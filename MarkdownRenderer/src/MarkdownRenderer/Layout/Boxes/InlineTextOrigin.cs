using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// The integer-pixel-snapped origin of an inline container's text layout in
/// document coordinates.  Snapping eliminates fractional sub-pixel
/// positioning that otherwise causes DirectWrite glyphs straddling
/// CanvasVirtualControl tile / dirty-rect boundaries to be re-rasterised
/// at slightly different sub-pixel offsets when the dirty rect changes
/// shape (e.g. as a selection-drag extends a highlight rect across the
/// text), which reads as visible "shake" of the rendered glyphs.
/// All paint / hit-test / selection-rect / embed-rect computations use
/// this single snapped origin so they stay in agreement.
/// </summary>
internal static class InlineTextOrigin
{
    /// <summary>
    /// Returns the snapped paint origin of <paramref name="box"/>'s text and,
    /// separately, the snapped final origin used for native glyph placement.
    /// </summary>
    /// <param name="box">The measured and arranged inline container.</param>
    /// <param name="style">The container style whose padding applies.</param>
    /// <param name="finalOriginX">The snapped final X origin.</param>
    /// <param name="finalOriginY">The snapped final Y origin.</param>
    internal static (float X, float Y) GetSnappedOrigin(
        InlineContainerBox box,
        ElementStyle style,
        out float finalOriginX,
        out float finalOriginY)
    {
        // Snap to *device pixels*, not DIPs. At a non-1x rasterization scale
        // (e.g., 1.25x / 1.5x / 2x), snapping only to integer DIPs still leaves
        // glyph origins at fractional device-pixel positions. When the canvas
        // dirty-rect shape changes between frames (as it does whenever the
        // selection drag grows / shrinks adjacent tiles), DirectWrite's pixel
        // snapping can resolve to a slightly different device-pixel column,
        // which manifests as the "selection shake" the user reports —
        // visually amplified on large heading glyphs.
        float scale = (float)box.Context.RasterizationScale;
        if (scale <= 0f) scale = 1f;
        var margin = box.Margin;
        var padding = box.GetEffectivePadding(style);
        float xDip = (float)(box.Bounds.X + margin.Left + padding.Left);
        float yDip = (float)(box.Bounds.Y + margin.Top + padding.Top + box.ScriptBaselineGeometry.TopInset);
        LayoutPlacementTransformContext.SnapOriginToDevicePixels(
            xDip,
            yDip,
            scale,
            out float x,
            out float y);
        LayoutPlacementTransformContext.SnapFinalOriginToDevicePixels(
            xDip,
            yDip,
            scale,
            out finalOriginX,
            out finalOriginY);
        return (x, y);
    }
}
