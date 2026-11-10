using System.Threading;
using System.Threading.Tasks;
using MarkdownRenderer.Internal.Concurrency;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Controls.Images;

/// <summary>What image relayout needs from the control and the owners it publishes through.</summary>
internal interface IImageRelayoutHost
{
    IMarkdownPerformanceSession? PerformanceSession { get; }

    /// <summary>The width layout runs at, in DIPs.</summary>
    float LayoutWidth { get; }

    /// <summary>The scroll viewport's vertical offset, or null without one.</summary>
    double? VerticalScrollOffset { get; }

    /// <summary>
    /// True while a pointer owner holds the published snapshot's geometry, so
    /// relayout must neither reflow it nor replace it.
    /// </summary>
    bool HasSnapshotInteraction { get; }

    /// <summary>
    /// Settles a finished rebuild pipeline whose UI completion was withheld,
    /// then returns true while a rebuild is pending or building.
    /// </summary>
    bool DefersToRebuild();

    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    void RequestFullRebuild();

    /// <summary>
    /// Stops the recovery clock. Unless <paramref name="force"/> is set, it
    /// keeps running while other deferred work is pending.
    /// </summary>
    void StopRecoveryClock(bool force);

    Task RetireCancellationTokenSource(RetirableCancellationTokenSource? cts, Task? lifetime);

    /// <summary>
    /// The inputs of a private raster placement for <paramref name="snapshot"/>,
    /// or null when the snapshot cannot use one.
    /// </summary>
    RasterPlacementInputs? TryCreateRasterPlacementInputs(LayoutSnapshot snapshot, bool optedIn);

    /// <summary>
    /// Publishes a private raster placement candidate. Returns false when its
    /// proof no longer holds and the old tree must be rebuilt instead.
    /// </summary>
    bool TryPublishRasterPlacement(LayoutSnapshot snapshot, RasterPlacementPlan candidate, long generation);

    /// <summary>
    /// Publishes the in-place relayout of the published snapshot, restoring
    /// <paramref name="anchor"/> when given.
    /// </summary>
    void PublishRelayout(LayoutSnapshot snapshot, (int BlockIndex, double OffsetFromTop)? anchor);
}
