using Microsoft.UI.Dispatching;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Focus;

/// <summary>What focusable item publication needs from the control.</summary>
internal interface IFocusableItemsPublicationHost
{
    DispatcherQueue DispatcherQueue { get; }

    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    void RequestFullRebuild();

    void InvalidateAutomationLayout();
}
