using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Input.Deferred;

/// <summary>
/// The lazy-layout state <see cref="DeferredGestureAdmission"/> reads to
/// decide whether a gesture's geometry is measured and published.
/// </summary>
internal interface IDeferredGestureLayoutHost
{
    /// <summary>The renderer's current rasterization scale, or 1 before it has a XAML root.</summary>
    double RasterizationScale { get; }

    /// <summary>True when <paramref name="point"/> is over a hosted embed.</summary>
    bool IsPointOverEmbed(Point point);

    /// <summary>True when <paramref name="point"/> is over a code block's action button.</summary>
    bool IsPointOverCodeBlockAction(Point point);

    /// <summary>
    /// True while a lazy layout commit for <paramref name="snapshot"/> is
    /// publishing, pending, or not yet applied on the UI thread.
    /// </summary>
    bool IsLayoutPublicationPending(LayoutSnapshot snapshot);

    /// <summary>
    /// True when the active or pending lazy layout request for
    /// <paramref name="snapshot"/> covers <paramref name="band"/>.
    /// </summary>
    bool IsLazyLayoutBandCovered(LayoutSnapshot snapshot, LazyLayoutBand band);

    /// <summary>
    /// True when another lazy layout request for <paramref name="snapshot"/>
    /// is active, pending or queued, so it owns the next retry.
    /// </summary>
    bool HasSupersedingLazyLayoutRequest(LayoutSnapshot snapshot);

    /// <summary>Queues lazy layout for <paramref name="band"/>.</summary>
    void QueueLazyLayoutForBand(
        LayoutSnapshot snapshot,
        LazyLayoutBand band,
        LazyLayoutBand visibleVectorBand,
        bool preserveScrollAnchor);
}
