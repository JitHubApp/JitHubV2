using System;

namespace MarkdownRenderer.Layout.Internal.Images.Loading;

/// <summary>
/// The image box as seen by its work lifetime: what happens when admitted
/// work drains, and the box-owned resources released by terminal retirement.
/// </summary>
internal interface IImageWorkLifetimeHost
{
    /// <summary>
    /// Runs after the last unit of admitted work completed and the drain
    /// signal was set, outside the image-work lock.
    /// </summary>
    void OnImageWorkDrained();

    /// <summary>
    /// Releases the box's documents, tiles, bitmap and native layouts once
    /// every admitted operation has drained, outside the image-work lock.
    /// Each failure goes to <paramref name="addFailure"/>; none is thrown.
    /// </summary>
    void ReleaseRetiredResources(Action<Exception> addFailure);

    /// <summary>
    /// Clears the box's source and cache identity references. The caller
    /// holds the image-work lock and every late operation has drained.
    /// </summary>
    void ClearRetiredStateNoLock();
}
