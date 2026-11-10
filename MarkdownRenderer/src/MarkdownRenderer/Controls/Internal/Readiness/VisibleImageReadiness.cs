using MarkdownRenderer.Controls.Internal.Images;
using MarkdownRenderer.Controls.Internal.Pipeline;
using MarkdownRenderer.Controls.Internal.View;
using MarkdownRenderer.Layout.Internal.Images;
using MarkdownRenderer.Layout.Internal.Placement;
using MarkdownRenderer.Layout.Internal.Snapshots;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Internal.Readiness;

/// <summary>
/// Answers UI Automation's "are visible images still loading" query from the
/// published snapshot and its image realization plan, conservatively
/// reporting loading whenever either is between publications.
/// </summary>
internal sealed class VisibleImageReadiness
{
    private readonly PublishedLayout _published;
    private readonly ImagePlanSet _imagePlans;
    private readonly MarkdownViewport _viewport;

    internal VisibleImageReadiness(PublishedLayout published, ImagePlanSet imagePlans, MarkdownViewport viewport)
    {
        _published = published;
        _imagePlans = imagePlans;
        _viewport = viewport;
    }

    /// <summary>
    /// Returns true while an image inside the viewport is still waiting for
    /// its pixels, or while the answer cannot be read without waiting.
    /// </summary>
    internal bool HasLoadingImages()
    {
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null)
            return false;

        bool hasViewport = _viewport.TryGet(out double top, out double height, out double width);
        Rect viewport = new(0, top, width, height);
        long snapshotGeneration = _published.Generation;
        ImagePlanPublication? publication = _imagePlans.Publication;
        if (publication is null ||
            !_imagePlans.IsCurrent(snapshot, snapshotGeneration, publication))
        {
            // A snapshot or its realization plan is between publications. Do
            // not let UIA announce readiness from a previous document/plan.
            return true;
        }

        // A readiness query must never wait behind DirectWrite/layout work. A
        // busy snapshot is conservatively reported as still loading and the
        // existing layout/paint status notifications will retry the query.
        if (!snapshot.TryBeginInteraction())
            return true;

        bool hasVisibleLoadingImage = true;
        long layoutRevision = -1;
        try
        {
            if (!_imagePlans.IsCurrent(snapshot, snapshotGeneration, publication))
                return true;

            layoutRevision = snapshot.LayoutRevision;
            if (layoutRevision != publication.LayoutRevision)
            {
                // A lazy-measure or image-size commit invalidated the indexed
                // bounds. The replacement plan is published after the layout
                // lease is released; until then avoid mixing its bounds with
                // the previous index or membership.
                return true;
            }

            hasVisibleLoadingImage = AnyVisibleImageAwaitingPixels(publication, hasViewport, viewport);
        }
        finally
        {
            snapshot.EndInteraction();
        }

        // Snapshot replacement and plan publication are independent of the
        // layout lock. Fence once more after releasing it so a detached result
        // from a superseded page cannot become a false-ready UIA answer.
        return !_imagePlans.IsCurrent(snapshot, snapshotGeneration, publication) ||
            snapshot.LayoutRevision != layoutRevision ||
            hasVisibleLoadingImage;
    }

    private static bool AnyVisibleImageAwaitingPixels(
        ImagePlanPublication publication,
        bool hasViewport,
        Rect viewport)
    {
        ImageBox[] images = publication.Images;
        Rect[] imageBounds = publication.ImageBounds;
        if (imageBounds.Length != images.Length)
            return true;
        if (images.Length == 0)
            return false;

        ViewportBandIndex? imageIndex = hasViewport ? publication.Index : null;
        ViewportRange range = imageIndex is not null
            ? imageIndex.Find(viewport.Top, viewport.Bottom)
            : new ViewportRange(0, images.Length);
        for (int index = range.Start; index < range.End; index++)
        {
            int ordinal = imageIndex is not null
                ? imageIndex.GetBlockOrdinal(index)
                : index;
            if ((uint)ordinal >= (uint)images.Length)
                return true;

            ImageBox image = images[ordinal];
            if (!image.IsAwaitingVisiblePixelsForAutomation)
                continue;
            Rect bounds = imageBounds[ordinal];

            if (!hasViewport ||
                (bounds.Width > 0 &&
                 bounds.Height > 0 &&
                 bounds.Right > viewport.Left &&
                 bounds.Left < viewport.Right &&
                 bounds.Bottom > viewport.Top &&
                 bounds.Top < viewport.Bottom))
            {
                return true;
            }
        }
        return false;
    }
}
