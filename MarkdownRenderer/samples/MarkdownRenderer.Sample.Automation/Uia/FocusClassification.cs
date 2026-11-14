using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using static MarkdownRenderer.Sample.Automation.Uia.AutomationElementText;

namespace MarkdownRenderer.Sample.Automation.Uia;

/// <summary>
/// Classifies the focused element during Accessibility Lab keyboard probes.
/// </summary>
internal static class FocusClassification
{
    internal static bool IsPaintedLinkFocus(AutomationElement focused)
    {
        return focused.ControlType == ControlType.Hyperlink &&
               !string.IsNullOrWhiteSpace(NameOrEmpty(focused));
    }

    private static bool IsRendererOrDocumentFocus(AutomationElement focused)
    {
        return AutomationIdOrEmpty(focused) == "MarkdownRenderer" ||
               focused.ControlType == ControlType.Document;
    }

    internal static bool IsNativeActionButton(AutomationElement focused)
    {
        return focused.ControlType == ControlType.Button &&
               NameOrEmpty(focused).Contains("Native action", StringComparison.Ordinal);
    }

    internal static bool IsCompositeValueTextBox(AutomationElement focused)
    {
        return focused.ControlType == ControlType.Edit &&
               (AutomationIdOrEmpty(focused) == "CompositeValueTextBox" ||
                NameOrEmpty(focused).Contains("Composite value", StringComparison.Ordinal));
    }

    internal static bool IsCompositeActionButton(AutomationElement focused)
    {
        return focused.ControlType == ControlType.Button &&
               (AutomationIdOrEmpty(focused) == "CompositeActionButton" ||
                NameOrEmpty(focused).Contains("Composite action", StringComparison.Ordinal));
    }

    internal static bool IsAccessibilityLabInternalFocus(AutomationElement focused)
    {
        return IsPaintedLinkFocus(focused) ||
               IsRendererOrDocumentFocus(focused) ||
               IsNativeActionButton(focused) ||
               IsCompositeValueTextBox(focused) ||
               IsCompositeActionButton(focused);
    }
}
