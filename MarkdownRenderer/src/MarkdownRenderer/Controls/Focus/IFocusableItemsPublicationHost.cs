using MarkdownRenderer.Layout;
using Microsoft.UI.Dispatching;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>What focusable item publication needs from the control.</summary>
internal interface IFocusableItemsPublicationHost
{
    DispatcherQueue DispatcherQueue { get; }

    bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot);

    void RequestFullRebuild();

    void InvalidateAutomationLayout();
}
