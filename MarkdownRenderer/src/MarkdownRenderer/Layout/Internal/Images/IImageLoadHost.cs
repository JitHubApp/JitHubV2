using System;
using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images.Loading;

namespace MarkdownRenderer.Layout.Internal.Images;

/// <summary>
/// What every stage of an image box's load pipeline asks of the box: its
/// counted image work, cleanup and retirement-failure recording, UI-thread
/// publication, and the box's unavailable and failure reports.
/// </summary>
internal interface IImageLoadHost
{
    /// <summary>True once the box is disposed.</summary>
    bool IsDisposed { get; }

    /// <summary>
    /// Takes the image-work lock: admits one unit of image work unless the box
    /// is disposed or its resources are suspended.
    /// </summary>
    bool TryBeginImageWork(out ImageWorkLease? work);

    /// <summary>Runs one cleanup action, recording rather than throwing its failure.</summary>
    void TryImageCleanup(Action cleanup);

    /// <summary>Records a cleanup or callback failure with the box's retirement.</summary>
    void RecordImageRetirementFailure(Exception exception);

    /// <summary>
    /// Runs <paramref name="publish"/> on the box's dispatcher, or
    /// <paramref name="onDropped"/> when the publication cannot run.
    /// </summary>
    void PublishOnUiThread(Action publish, Action? onDropped = null);

    /// <summary>Reports an unavailable image to the host once per load.</summary>
    void ReportUnavailable(
        MarkdownImageUnavailableReason reason, MarkdownSvgFailureReason? svgFailureReason = null);

    /// <summary>Publishes the box's failure placeholder on the UI thread.</summary>
    void PublishFailure(string cacheKey, bool cachePermanently = true);

    /// <summary>Lets a cancelled load start again, unless the box is disposed.</summary>
    void ResetLoadAfterCancellation();
}
