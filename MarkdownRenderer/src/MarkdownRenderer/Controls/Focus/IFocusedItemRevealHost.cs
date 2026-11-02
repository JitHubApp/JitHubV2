using Windows.Foundation;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>What revealing the focused item needs from the control.</summary>
internal interface IFocusedItemRevealHost
{
    /// <summary>Asks for focus geometry to be read again later.</summary>
    void RequestFocusGeometryRetry();

    /// <summary>
    /// Asks the ancestor viewport to bring <paramref name="rect"/> into view
    /// when the renderer does not own its scroll viewer. Returns false when
    /// it owns one or the request failed.
    /// </summary>
    bool BringDocumentRectIntoExternalViewport(Rect rect, bool animate, double alignmentRatio);
}
