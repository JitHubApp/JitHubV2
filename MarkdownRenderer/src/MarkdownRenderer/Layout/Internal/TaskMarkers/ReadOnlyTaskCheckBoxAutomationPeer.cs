using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace MarkdownRenderer.Layout.Internal.TaskMarkers;

/// <summary>
/// Automation peer that keeps a read-only task marker out of the control
/// and content views. Declared <c>partial</c> for CsWinRT interop generation.
/// </summary>
internal sealed partial class ReadOnlyTaskCheckBoxAutomationPeer(ReadOnlyTaskCheckBox owner)
    : FrameworkElementAutomationPeer(owner)
{
    protected override string GetClassNameCore() => nameof(CheckBox);

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.CheckBox;

    protected override bool IsControlElementCore() => false;

    protected override bool IsContentElementCore() => false;

    protected override bool IsKeyboardFocusableCore() => false;

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Toggle
            ? null!
            : base.GetPatternCore(patternInterface);
}
