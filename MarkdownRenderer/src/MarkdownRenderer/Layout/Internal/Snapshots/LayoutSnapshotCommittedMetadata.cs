using System;
using Windows.Foundation;
using MarkdownRenderer.Layout.Internal.Placement;

namespace MarkdownRenderer.Layout.Internal.Snapshots;

/// <summary>
/// Immutable-once-published snapshot metadata: size, revision, measured root
/// count and the placement generation. Unpublished candidates may complete
/// their size and placements exactly once.
/// </summary>
internal sealed class LayoutSnapshotCommittedMetadata(Size size, long revision, int measuredCount)
{
    private Size _size = size;
    private LayoutPlacementGeneration? _placements;
    private bool _published;
    private bool _sizeCompleted;
    private bool _placementsCompleted;

    internal Size Size => _size;
    internal readonly long Revision = revision;
    internal readonly int MeasuredCount = measuredCount;
    internal LayoutPlacementGeneration Placements => _placements ??
        throw new InvalidOperationException("Layout metadata has no placement generation.");
    internal bool HasPlacements => _placements is not null;

    internal LayoutSnapshotCommittedMetadata(
        Size size,
        long revision,
        int measuredCount,
        LayoutPlacementGeneration? placements)
        : this(size, revision, measuredCount)
    {
        _placements = placements;
        _placementsCompleted = placements is not null;
    }

    internal void CompleteSizeBeforePublish(Size size)
    {
        if (_published || _sizeCompleted)
            throw new InvalidOperationException("Layout metadata size may be completed once before publication.");
        _size = size;
        _sizeCompleted = true;
    }

    internal void CompletePlacementsBeforePublish(LayoutPlacementGeneration placements)
    {
        ArgumentNullException.ThrowIfNull(placements);
        if (_published || _placementsCompleted)
            throw new InvalidOperationException("Layout metadata placements may be completed once before publication.");
        _placements = placements;
        _placementsCompleted = true;
    }

    internal void CompletePlacementRevisionBeforePublish(long revision)
    {
        if (_published || !_placementsCompleted)
            throw new InvalidOperationException("Layout metadata placement revision may be completed only before publication.");
        LayoutPlacementGeneration current = Placements;
        _placements = current.WithRevision(revision);
    }

    internal void MarkPublished()
    {
        if (_published)
            throw new InvalidOperationException("Layout metadata may be published only once.");
        _published = true;
    }
}
