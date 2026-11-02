using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Images;

/// <summary>
/// The images of the published layout, in plan order, with the viewport
/// band index over their bounds and the publication automation reads.
/// Membership and indexed bounds stay fixed until the next plan
/// publication; the ImageBox objects themselves are borrowed from the
/// snapshot.
/// </summary>
internal sealed class ImagePlanSet
{
    private readonly MarkdownViewLifetime _lifetime;
    private readonly PublishedLayout _published;

    // Cleared in place, never replaced, except when a raster placement
    // adopts its prepared list; scans compare the instance to detect that.
    private List<ImageBox> _items = new();
    private ViewportBandIndex? _index;
    private ImagePlanPublication? _publication;

    internal ImagePlanSet(MarkdownViewLifetime lifetime, PublishedLayout published)
    {
        _lifetime = lifetime;
        _published = published;
    }

    internal int Count => _items.Count;

    internal ImageBox this[int ordinal] => _items[ordinal];

    /// <summary>The plan list itself; a different instance means the plans were replaced.</summary>
    internal List<ImageBox> Items => _items;

    /// <summary>The band index over the plans' document bounds, or null when there are none.</summary>
    internal ViewportBandIndex? Index => _index;

    /// <summary>The last published plan membership; readable from any thread.</summary>
    internal ImagePlanPublication? Publication
    {
        get => Volatile.Read(ref _publication);
        set => Volatile.Write(ref _publication, value);
    }

    internal void Add(ImageBox image) => _items.Add(image);

    internal void Clear()
    {
        _items.Clear();
        _index = null;
    }

    /// <summary>Adopts the plans a raster placement prepared off the UI thread.</summary>
    internal void Adopt(List<ImageBox> items, ViewportBandIndex? index)
    {
        _items = items;
        _index = index;
    }

    /// <summary>
    /// Indexes the collected plans by their bounds in
    /// <paramref name="snapshot"/>, which must be under a paint lease.
    /// Returns the publication for them, or null when there are no plans.
    /// </summary>
    internal ImagePlanPublication? IndexUnderPaint(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        long layoutRevision)
    {
        if (_items.Count == 0)
            return null;

        _index = new ViewportBandIndex(_items.Count);
        var imageBounds = new Rect[_items.Count];
        for (int index = 0; index < _items.Count; index++)
        {
            Rect bounds = snapshot.ToLogicalRectForBlockIndexUnderPaint(
                _items[index].BlockIndex, _items[index].Bounds);
            imageBounds[index] = bounds;
            _index.SetEntry(index, index, bounds.Top, bounds.Bottom);
        }
        _index.Commit();
        return new ImagePlanPublication(
            snapshot,
            snapshotGeneration,
            layoutRevision,
            _items.ToArray(),
            imageBounds,
            _index);
    }

    /// <summary>
    /// True while <paramref name="publication"/> is the current one and
    /// describes the published snapshot at its current layout revision.
    /// </summary>
    internal bool IsCurrent(
        LayoutSnapshot snapshot,
        long snapshotGeneration,
        ImagePlanPublication publication) =>
        !_lifetime.IsDisposed && !_lifetime.IsUnloaded &&
        ReferenceEquals(snapshot, _published.Snapshot) &&
        snapshotGeneration == _published.Generation &&
        ReferenceEquals(publication, Publication) &&
        ReferenceEquals(snapshot, publication.Snapshot) &&
        snapshotGeneration == publication.SnapshotGeneration &&
        snapshot.LayoutRevision == publication.LayoutRevision;

    /// <summary>An empty publication for a plan pass that found no images.</summary>
    internal static ImagePlanPublication Empty(LayoutSnapshot snapshot, long snapshotGeneration, long layoutRevision) =>
        new(snapshot, snapshotGeneration, layoutRevision, Array.Empty<ImageBox>(), Array.Empty<Rect>(), null);
}
