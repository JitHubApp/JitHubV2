using FlaUI.Core.AutomationElements;

namespace MarkdownRenderer.Sample.Automation.Uia;

/// <summary>
/// Reads names, identifiers and TextPattern text from UIA elements without letting a stale element abort a probe.
/// </summary>
internal static class AutomationElementText
{
    internal static string NameOrEmpty(AutomationElement element)
    {
        try { return element.Name ?? string.Empty; }
        catch
        {
            try { return element.Properties.Name.ValueOrDefault ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    internal static string AutomationIdOrEmpty(AutomationElement element)
    {
        try { return element.AutomationId ?? string.Empty; }
        catch
        {
            try { return element.Properties.AutomationId.ValueOrDefault ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    internal static string ClassNameOrEmpty(AutomationElement element)
    {
        try { return element.ClassName ?? string.Empty; }
        catch
        {
            try { return element.Properties.ClassName.ValueOrDefault ?? string.Empty; }
            catch { return string.Empty; }
        }
    }

    internal static string DescribeFocus(AutomationElement focused)
        => $"{focused.ControlType}/{NameOrEmpty(focused)}/AutomationId={AutomationIdOrEmpty(focused)}";

    internal static string GetAutomationElementText(AutomationElement element)
    {
        var textPattern = element.Patterns.Text.PatternOrDefault;
        return textPattern is null
            ? NameOrEmpty(element)
            : textPattern.DocumentRange.GetText(-1);
    }

    internal static string GetRendererDocumentText(AutomationElement renderer)
    {
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        return textPattern.DocumentRange.GetText(-1);
    }

    internal static string GetRendererSelectionText(AutomationElement renderer)
    {
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        return string.Concat(textPattern.GetSelection().Select(range => range.GetText(-1)));
    }

    internal static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "…";

    internal static string FormatRect(System.Drawing.Rectangle rect)
        => $"x={rect.X},y={rect.Y},w={rect.Width},h={rect.Height}";
}
