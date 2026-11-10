using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images.Loading;
using MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;
using MarkdownRenderer.Layout.Internal.Images.Svg.Tiles;

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;

/// <summary>
/// What one SVG publication commit replaced or abandoned under its locks,
/// retired by <see cref="RetireReplacedAndAbandoned"/> once every lock is
/// released.
/// </summary>
internal sealed class SvgPublicationCommitState
{
    /// <summary>True once the accepted metadata, document and plan were applied.</summary>
    internal bool LocalCommitApplied;

    /// <summary>True once the tile cache accepted the matching transition.</summary>
    internal bool TileTransitionApplied;

    /// <summary>The document the commit replaced.</summary>
    internal IMarkdownSvgDocument? Previous;

    /// <summary>The tile plan the commit replaced.</summary>
    internal SvgTileRenderPlan? PreviousPlan;

    /// <summary>The disposal work reserved for <see cref="Previous"/>.</summary>
    internal ImageWorkLease? PreviousCleanup;

    /// <summary>The owner slot of a plan detached again after its transition failed.</summary>
    internal SvgHostOwnerSlot? AbandonedOwnerSlot;

    /// <summary>The document of a plan detached again after its transition failed.</summary>
    internal IMarkdownSvgDocument? AbandonedDocument;

    /// <summary>The disposal work reserved for <see cref="AbandonedDocument"/>.</summary>
    internal ImageWorkLease? AbandonedDocumentCleanup;

    /// <summary>
    /// Releases the replaced plan's owner slot and schedules the replaced
    /// document's disposal, then does the same for an abandoned plan.
    /// </summary>
    internal void RetireReplacedAndAbandoned(SvgTileRenderPlan? acceptedPlan, SvgDocumentCustody documents)
    {
        if (LocalCommitApplied)
        {
            if (PreviousPlan is not null && !ReferenceEquals(PreviousPlan, acceptedPlan))
                PreviousPlan.OwnerSlot?.Dispose();

            if (Previous is not null && PreviousCleanup is not null)
                documents.ScheduleDisposal(Previous, PreviousCleanup);
        }

        AbandonedOwnerSlot?.Dispose();
        if (AbandonedDocument is not null && AbandonedDocumentCleanup is not null)
            documents.ScheduleDisposal(AbandonedDocument, AbandonedDocumentCleanup);
    }
}
