using FlaUI.Core.AutomationElements;
using static MarkdownRenderer.Sample.Automation.Uia.AutomationElementText;

namespace MarkdownRenderer.Sample.Automation.Uia;

/// <summary>
/// Turns TextPattern ranges into on-screen points and rectangles that pointer probes can target.
/// </summary>
internal static class TextPatternGeometry
{
    internal static System.Drawing.Point FindTextPatternPointAfterScroll(
        AutomationElement renderer,
        string text,
        string description)
    {
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException($"{description}: renderer does not expose TextPattern");
        var range = textPattern.DocumentRange.FindText(text, backward: false, ignoreCase: true)
                    ?? throw new InvalidOperationException($"{description}: TextPattern could not find '{text}'");
        range.ScrollIntoView(alignToTop: false);
        Thread.Sleep(400);
        return FindTextPatternPoint(renderer, text, description);
    }

    internal static System.Drawing.Point FindTextPatternPoint(AutomationElement renderer, string text, string description)
    {
        var rect = FindTextPatternRect(renderer, text, description);
        var center = new System.Drawing.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
        Console.WriteLine($"[automation] {description}: using TextPattern point {center} from '{text}' in rect {FormatRect(rect)}");
        return center;
    }

    internal static System.Drawing.Rectangle FindTextPatternRect(AutomationElement renderer, string text, string description)
    {
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException($"{description}: renderer does not expose TextPattern");
        var range = textPattern.DocumentRange.FindText(text, backward: false, ignoreCase: true)
                    ?? throw new InvalidOperationException($"{description}: TextPattern could not find '{text}'");

        var rendererBounds = renderer.BoundingRectangle;
        foreach (var rect in range.GetBoundingRectangles())
        {
            if (rect.Width <= 1 || rect.Height <= 1) continue;
            var center = new System.Drawing.Point(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);
            if (rendererBounds.Contains(center))
            {
                return rect;
            }
        }

        throw new InvalidOperationException(
            $"{description}: TextPattern found '{text}' but returned no usable bounding rectangle inside renderer bounds {FormatRect(rendererBounds)}");
    }
}
