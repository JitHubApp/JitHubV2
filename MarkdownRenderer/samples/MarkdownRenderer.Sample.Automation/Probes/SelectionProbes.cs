using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.Tools;
using FlaUI.Core.WindowsAPI;
using MarkdownRenderer.Sample.Automation.Native;
using MarkdownRenderer.Sample.Automation.Uia;
using static MarkdownRenderer.Sample.Automation.Native.NativeClipboard;
using static MarkdownRenderer.Sample.Automation.Native.NativeMouse;
using static MarkdownRenderer.Sample.Automation.Uia.AutomationElementText;
using static MarkdownRenderer.Sample.Automation.Uia.ProbeAssert;
using static MarkdownRenderer.Sample.Automation.Uia.SampleWindowQueries;
using static MarkdownRenderer.Sample.Automation.Uia.TextPatternGeometry;

namespace MarkdownRenderer.Sample.Automation.Probes;

/// <summary>
/// Probes keyboard traversal, pointer selection, dismissal and Ctrl+C copy.
/// </summary>
internal static class SelectionProbes
{
    internal static void ProbeKeyboardNav(Window window)
    {
        SelectSample(window, "KeyboardNav");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        renderer.Focus();
        Thread.Sleep(200);

        // Tab through links repeatedly; the renderer must survive all presses without
        // throwing or becoming unresponsive. With the boundary-exit fix, Tab at the
        // last link exits the control; further presses may or may not re-enter it.
        for (int i = 0; i < 15; i++)
        {
            Keyboard.Press(VirtualKeyShort.TAB);
            Thread.Sleep(150);
        }

        // Re-focus the renderer and verify Shift+Tab and Escape both work.
        renderer.Focus();
        Thread.Sleep(200);
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        Thread.Sleep(200);
        Keyboard.TypeSimultaneously(VirtualKeyShort.SHIFT, VirtualKeyShort.TAB);
        Thread.Sleep(200);
        Keyboard.Press(VirtualKeyShort.ESCAPE);
        Thread.Sleep(200);

        // Verify renderer is still responsive
        var textAfter = GetRendererDocumentText(renderer);
        Assert(textAfter.Length > 0, "Keyboard Nav renderer must remain responsive after Tab/Escape traversal");
    }

    internal static void ProbeClickDismissesFocus(Window window)
    {
        // Navigate to the Keyboard Nav page, which has keyboard-focusable links.
        SelectSample(window, "KeyboardNav");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        renderer.Focus();
        Thread.Sleep(200);

        // Tab once to show focus ring.
        Keyboard.Press(VirtualKeyShort.TAB);
        Thread.Sleep(200);

        // Click somewhere inside the renderer to dismiss focus.
        var bounds = renderer.BoundingRectangle;
        Mouse.MoveTo((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2));
        Thread.Sleep(100);
        Mouse.LeftClick();
        Thread.Sleep(300);

        // The renderer must still be responsive after the click.
        var textAfter = GetRendererDocumentText(renderer);
        Assert(textAfter.Length > 0, "Renderer must remain responsive after click-dismisses-focus");
    }

    internal static void ProbeSelectionDismissesOnExternalPointer(Window window)
    {
        SelectSample(window, "Selection");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var textPattern = renderer.Patterns.Text.PatternOrDefault
                          ?? throw new InvalidOperationException("MarkdownRenderer must expose UIA TextPattern");
        var selectionRange = textPattern.DocumentRange.FindText("Click", backward: false, ignoreCase: true)
                             ?? throw new InvalidOperationException("Selection external-dismiss probe could not find text");
        selectionRange.ExpandToEnclosingUnit(TextUnit.Word);
        selectionRange.Select();
        Thread.Sleep(500);
        Assert(!string.IsNullOrWhiteSpace(GetRendererSelectionText(renderer)),
            "TextPattern.Select must create a UIA-visible selection before dismissal");

        var editor = window.FindFirstDescendant(cf => cf.ByAutomationId("MarkdownEditor"))
                     ?? throw new InvalidOperationException("Markdown source editor not found");
        var bounds = editor.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(bounds.Left + 20, bounds.Top + 20), FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(500);

        Assert(string.IsNullOrEmpty(GetRendererSelectionText(renderer)),
            "clicking another app control must dismiss the markdown selection");
    }

    internal static void ProbeSelectionDismissesOnHostedControlPointer(Window window)
    {
        SelectSample(window, "AccessibilityLab");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var point = FindTextPatternPoint(renderer, "quick", "selection hosted-control-dismiss probe");
        Mouse.DoubleClick(point, FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(500);
        Assert(!string.IsNullOrWhiteSpace(GetRendererSelectionText(renderer)),
            "double-clicking renderer text must create a UIA-visible selection before hosted-control dismissal");

        var hostedTextBox = window.FindFirstDescendant(cf => cf.ByAutomationId("CompositeValueTextBox"))
                            ?? throw new InvalidOperationException("Composite hosted TextBox not found");
        var bounds = hostedTextBox.BoundingRectangle;
        Mouse.Click(new System.Drawing.Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(500);

        Assert(string.IsNullOrEmpty(GetRendererSelectionText(renderer)),
            "clicking a hosted WinUI control inside the markdown renderer must dismiss the markdown selection");
    }

    internal static void ProbeSelectionPersistsAfterPointerRelease(Window window)
    {
        var cases = new[]
        {
            (Sample: "Typography", Start: "Heading 6", End: "Regular paragraph text", Expected: "quick brown fox"),
            (Sample: "Selection", Start: "Click and drag", End: "The selection spans", Expected: "select"),
            (Sample: "Code", Start: "C# with a filename", End: "public sealed class", Expected: "public"),
        };

        foreach (var c in cases)
        {
            SelectSample(window, c.Sample);
            Thread.Sleep(1200);

            var renderer = FindRenderer(window);
            var start = FindTextPatternPoint(renderer, c.Start, $"{c.Sample} selection persist start");
            var end = FindTextPatternPoint(renderer, c.End, $"{c.Sample} selection persist end");
            Assert(Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) >= 24,
                $"{c.Sample} selection persist probe did not find a meaningful drag range: start={start}, end={end}");

            DragMouseThrough(start, end);
            Thread.Sleep(700);

            string selected = GetRendererSelectionText(renderer);
            Assert(!string.IsNullOrWhiteSpace(selected),
                $"{c.Sample} selection must remain active after mouse release");
            Assert(selected.Contains(c.Expected, StringComparison.OrdinalIgnoreCase),
                $"{c.Sample} selection after release should include dragged text, got: {Truncate(selected, 160)}");
        }
    }

    internal static void ProbeCtrlCCopiesPointerSelection(Window window)
    {
        SelectSample(window, "Selection");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var start = FindTextPatternPoint(renderer, "Click and drag", "ctrl-c copy drag start");
        var end = FindTextPatternPoint(renderer, "The selection spans", "ctrl-c copy drag end");
        DragMouseThrough(start, end);
        Thread.Sleep(500);

        string selected = GetRendererSelectionText(renderer);
        Assert(!string.IsNullOrWhiteSpace(selected),
            "ctrl-c copy probe must have an active pointer selection before copying");

        bool rendererFocused = Retry.WhileFalse(
            () =>
            {
                TryFocus(renderer, "renderer before Ctrl+C");
                return renderer.Properties.HasKeyboardFocus.ValueOrDefault;
            },
            timeout: TimeSpan.FromSeconds(3),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(rendererFocused, "renderer did not acquire keyboard focus before Ctrl+C");
        Assert(!string.IsNullOrWhiteSpace(GetRendererSelectionText(renderer)),
            "focusing the renderer before Ctrl+C must preserve the pointer selection");

        const string sentinel = "markdown-renderer-clipboard-sentinel";
        SetClipboardText(sentinel);
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
        bool clipboardChanged = Retry.WhileFalse(
            () => !string.Equals(GetClipboardText(), sentinel, StringComparison.Ordinal),
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(100)).Result;
        Assert(clipboardChanged,
            "Ctrl+C left the clipboard unchanged; renderer likely did not handle the key event");
        string copied = GetClipboardText();
        Assert(copied.Contains("select any text", StringComparison.OrdinalIgnoreCase) ||
               copied.Contains("selection spans", StringComparison.OrdinalIgnoreCase),
            $"Ctrl+C should copy selected rendered text, got: {Truncate(copied, 240)}");
    }

    internal static void ProbeTableSelectionRowBorderIsStable(Window window)
    {
        SelectSample(window, "Tables");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var anchorRect = FindTextPatternRect(renderer, "Selection + Copy", "table row-border drag anchor");
        var previousRowRect = FindTextPatternRect(renderer, "Links", "table row-border drag previous row");
        var start = new System.Drawing.Point(anchorRect.Left + anchorRect.Width / 2, anchorRect.Top + anchorRect.Height / 2);
        var border = new System.Drawing.Point(
            anchorRect.Left + anchorRect.Width / 2,
            previousRowRect.Bottom + Math.Max(1, (anchorRect.Top - previousRowRect.Bottom) / 2));

        SendMouseMove(start);
        Thread.Sleep(100);
        SendLeftButtonDown();
        try
        {
            Thread.Sleep(120);
            SendMouseMove(border);
            Thread.Sleep(500);

            string selected = GetRendererSelectionText(renderer);
            Assert(!string.IsNullOrWhiteSpace(selected),
                $"table row-border drag should keep a non-empty selection at border point {border}");
            Assert(!selected.Contains("AOT compatibility", StringComparison.OrdinalIgnoreCase) &&
                   !selected.Contains("Live theme switch", StringComparison.OrdinalIgnoreCase),
                $"table row-border drag must not jump to rows after the anchor, got: {Truncate(selected, 240)}");
        }
        finally
        {
            SendLeftButtonUp();
        }
    }

    internal static void ProbeDoubleClickSelectsWord(Window window)
    {
        // Navigate to Typography sample which has plain paragraphs.
        SelectSample(window, "Typography");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var point = FindTextPatternPoint(renderer, "Lorem", "double-click word probe");
        Mouse.MoveTo(point);
        Thread.Sleep(900);

        Mouse.DoubleClick(point, FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(700);

        string selected = GetRendererSelectionText(renderer);
        Assert(selected.Trim().Equals("Lorem", StringComparison.OrdinalIgnoreCase),
            $"double-click probe did not select the word under the pointer, got: {Truncate(selected, 160)}");
    }

    internal static void ProbeTripleClickSelectsLine(Window window)
    {
        // Navigate to Selection sample which has plain paragraphs.
        SelectSample(window, "Selection");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var point = FindTextPatternPoint(renderer, "Click and drag", "triple-click line probe");
        Mouse.MoveTo(point);
        Thread.Sleep(900);

        // Triple-click = double-click plus one more click in rapid succession on
        // the same real text point. Use point-targeted helpers so the click is not
        // dependent on whatever position the previous probe left the cursor at.
        Mouse.DoubleClick(point, FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(60);
        Mouse.Click(point, FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(700);

        string selected = GetRendererSelectionText(renderer);
        Assert(selected.Contains("Click and drag", StringComparison.OrdinalIgnoreCase) &&
                selected.Trim().Length > "Click and drag".Length,
            $"triple-click probe did not expand to the containing line, got: {Truncate(selected, 240)}");
    }
}
