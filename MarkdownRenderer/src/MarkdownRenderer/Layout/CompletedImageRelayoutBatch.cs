using System.Collections.Generic;
using System.Threading;
using Microsoft.Graphics.Canvas.Text;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Layout.Internal.Images.Placement;

namespace MarkdownRenderer.Layout;

/// <summary>
/// State of one completed-image relayout call as it moves from capture under
/// the source gate, through private measurement outside it, to the final
/// commit. It is never shared between calls or threads.
/// </summary>
internal sealed class CompletedImageRelayoutBatch
{
    internal CompletedImageRelayoutBatch(int count, float availableWidth, CancellationToken cancellationToken)
    {
        Count = count;
        Prepared = new BlockBox?[count];
        AvailableWidth = availableWidth;
        CancellationToken = cancellationToken;
    }

    /// <summary>Requested roots, then the number of distinct top-level roots.</summary>
    internal int Count { get; set; }

    /// <summary>Private clones, discarded into <see cref="Context"/> when the call ends.</summary>
    internal BlockBox?[] Prepared { get; }

    internal float AvailableWidth { get; }

    internal CancellationToken CancellationToken { get; }

    internal long PointerEpoch { get; set; }

    internal long Revision { get; set; }

    internal int MeasuredCount { get; set; }

    internal LayoutPlacementGeneration SourcePlacements { get; set; } = null!;

    internal float ContentWidth { get; set; }

    internal float ContentX { get; set; }

    internal MarkdownLayoutContext? SourceContext { get; set; }

    /// <summary>Fresh raster-placement context that owns the private clones.</summary>
    internal MarkdownLayoutContext? Context { get; set; }

    internal BlockBox[] SourceRoots { get; set; } = [];

    internal ChangedBlockMeasure[] Changed { get; set; } = [];

    internal RasterPlacementSourceFence[] Sources { get; set; } = [];

    internal ImageMeasuredPlacementTransfer[] Transfers { get; set; } = [];

    internal InlineMeasuredPlacementTransfer?[] InlineTransfers { get; set; } = [];

    internal TableMeasuredPlacementTransfer?[] TableTransfers { get; set; } = [];

    internal BlockMeasuredPlacementTransfer?[] ContainerTransfers { get; set; } = [];

    internal List<InlineMeasuredPlacementTransfer> AllInlineTransfers { get; } = new();

    internal long NextRevision { get; set; }

    internal LayoutPlacementGeneration CandidatePlacements { get; set; } = null!;

    internal float[]? CandidateContinuations { get; set; }

    internal LayoutSnapshotCommittedMetadata CandidateMetadata { get; set; } = null!;

    internal long CurrentProviderGeneration { get; set; }

    internal HashSet<CanvasTextLayout> NativeLayoutOwners { get; set; } = null!;

    internal HashSet<NativeResourceRetirementRecord> NativeRetirementOwners { get; set; } = null!;
}
