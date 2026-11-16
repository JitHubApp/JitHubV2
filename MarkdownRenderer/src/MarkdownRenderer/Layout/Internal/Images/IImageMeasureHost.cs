using Windows.Foundation;

namespace MarkdownRenderer.Layout.Internal.Images;

/// <summary>The image box as seen by its <see cref="ImageMeasureCustody"/>.</summary>
internal interface IImageMeasureHost
{
    /// <summary>Whether the box has been disposed.</summary>
    bool IsDisposed { get; }

    /// <summary>Restores the box's bounds when a speculative measure rolls back.</summary>
    void RestoreBounds(Rect bounds);
}
