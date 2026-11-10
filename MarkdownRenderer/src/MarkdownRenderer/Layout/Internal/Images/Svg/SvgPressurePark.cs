using MarkdownRenderer.Images;
using MarkdownRenderer.Layout.Internal.Images.Loading;
using MarkdownRenderer.Layout.Internal.Images.Svg.HostAdmission;

namespace MarkdownRenderer.Layout.Internal.Images.Svg;

/// <summary>
/// The outcome of one pressure park: whether the plan was detached, and the
/// document, owner slot and disposal work it left to retire outside the
/// image-work lock, with the epoch of its visual update.
/// </summary>
internal struct SvgPressurePark
{
    /// <summary>True once the plan was detached.</summary>
    internal bool Parked;

    /// <summary>The detached document.</summary>
    internal IMarkdownSvgDocument? Document;

    /// <summary>The detached plan's host owner slot.</summary>
    internal SvgHostOwnerSlot? OwnerSlot;

    /// <summary>The document's disposal work, when no use remained.</summary>
    internal ImageWorkLease? Cleanup;

    /// <summary>The pressure-park publication epoch of the visual update.</summary>
    internal long PublicationEpoch;
}
