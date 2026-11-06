using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Raw-view, non-interactive task checkbox. Declared <c>partial</c> because
/// CsWinRT generates interop code for types that derive from WinRT classes.
/// </summary>
internal sealed partial class ReadOnlyTaskCheckBox : CheckBox
{
    internal ReadOnlyTaskCheckBox(
        InlineEmbedAutomationMetadata metadata,
        float markerSize,
        Windows.UI.Color accent,
        Windows.UI.Color foreground)
    {
        TaskMarkerControlFactory.Configure(this, isInteractive: false, metadata.IsChecked, markerSize, accent, foreground);
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        AutomationProperties.SetAutomationId(this, metadata.AutomationId);
        AutomationProperties.SetName(this, metadata.CurrentName);
        AutomationProperties.SetHelpText(this, metadata.ReadOnlyHelpText);
    }

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new ReadOnlyTaskCheckBoxAutomationPeer(this);
}
