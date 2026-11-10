using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Input;

/// <summary>What horizontal overflow scrolling needs from the control.</summary>
internal interface IHorizontalOverflowScrollHost
{
    /// <summary>Moves the selection overlay to the scrolled text.</summary>
    void UpdateSelectionOverlay();

    /// <summary>Moves the focus ring to the focused item.</summary>
    void UpdateFocusRing();

    /// <summary>Starts the recovery clock that retries deferred work.</summary>
    void StartRecoveryClock();

    /// <summary>True while the recovery clock is running.</summary>
    bool HasRecoveryClock { get; }

    /// <summary>Rebuilds the layout from the document.</summary>
    void RequestFullRebuild();

    /// <summary>Starts geometry recovery when <paramref name="snapshot"/> needs it. Returns true if it does.</summary>
    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    /// <summary>The control's automation peer, or null when none was created.</summary>
    MarkdownAutomationPeer? FindAutomationPeer();
}
