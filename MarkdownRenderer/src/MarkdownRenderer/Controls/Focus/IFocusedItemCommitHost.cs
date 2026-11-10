using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.LazyLayout;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>What committing and activating the focused item needs from the control.</summary>
internal interface IFocusedItemCommitHost
{
    /// <summary>Gives the control itself keyboard focus.</summary>
    bool FocusRenderer();

    /// <summary>Gives the control itself programmatic focus.</summary>
    bool FocusRendererProgrammatically();

    /// <summary>
    /// Queues lazy layout for the estimated band of a focus target whose
    /// top-level block has not been measured yet.
    /// </summary>
    void RealizeLazyFocusTarget(LayoutSnapshot snapshot, LazyLayoutBand estimatedBand);

    /// <summary>The link at <paramref name="position"/> in the published layout, if any.</summary>
    LinkTarget? FindLinkTargetAt(DocumentPosition position);

    /// <summary>Activates <paramref name="link"/> as pointer input would.</summary>
    bool ActivateLink(LinkTarget link, MarkdownLinkInputKind inputKind, MarkdownInputModifiers modifiers);

    /// <summary>The control's automation peer, created if needed.</summary>
    MarkdownAutomationPeer? GetOrCreateAutomationPeer();
}
