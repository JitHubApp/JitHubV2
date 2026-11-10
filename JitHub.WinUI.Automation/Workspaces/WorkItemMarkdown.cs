using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using JitHub.WinUI.Automation.Native;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementGeometry;
using static JitHub.WinUI.Automation.Elements.ElementQueries;
using static JitHub.WinUI.Automation.Interaction.ElementActions;
using static JitHub.WinUI.Automation.Interaction.MouseInput;
using static JitHub.WinUI.Automation.Interaction.ScrollActions;
using static JitHub.WinUI.Automation.Windows.WindowActivation;

namespace JitHub.WinUI.Automation.Workspaces;

/// <summary>
/// Checks rendered work item Markdown and copies a selected range of it.
/// </summary>
internal static class WorkItemMarkdown
{
    // This method stays whole because it is a single retry loop that scrolls the nearest vertical ancestor and re-finds
    // the text range, repeating until a glyph rectangle is visible, while keeping the last viewport and range bounds
    // for the failure message.
    private static Rectangle FindVisibleMarkdownRangeRect(
        AutomationElement host,
        ITextRange range,
        string description,
        bool preferLastRectangle = false,
        bool searchFromBottom = false,
        int minimumVisibleHeight = 8)
    {
        string rangeText = range.GetText(-1);
        Rectangle lastViewportBounds = Rectangle.Empty;
        Rectangle lastRangeBounds = Rectangle.Empty;
        string lastRangeRectangles = string.Empty;
        const int fineSearchAttempts = 24;
        const int totalSearchAttempts = 64;
        for (int attempt = 0; attempt < totalSearchAttempts; attempt++)
        {
            Rectangle viewportBounds = GetClippedAutomationBounds(host);
            Rectangle[] rangeBounds = range.GetBoundingRectangles();
            IEnumerable<Rectangle> visibleRangeBounds = rangeBounds
                .Where(rect => rect.Width > 1 && rect.Height > 1 && viewportBounds.IntersectsWith(rect));
            Rectangle rectangle = preferLastRectangle
                ? visibleRangeBounds.LastOrDefault()
                : visibleRangeBounds.FirstOrDefault();
            Rectangle visibleRectangle = Rectangle.Intersect(rectangle, viewportBounds);
            if (visibleRectangle.Width > 8 && visibleRectangle.Height >= minimumVisibleHeight)
            {
                return visibleRectangle;
            }

            Rectangle[] measurableRangeBounds = rangeBounds
                .Where(rect => rect.Width > 1 && rect.Height > 1)
                .ToArray();
            Rectangle rangeBoundsUnion = measurableRangeBounds.Length == 0
                ? Rectangle.Empty
                : measurableRangeBounds.Aggregate(Rectangle.Union);
            lastViewportBounds = viewportBounds;
            lastRangeBounds = rangeBoundsUnion;
            lastRangeRectangles = string.Join(", ", rangeBounds.Select(rect => rect.ToString()));
            if (!rangeBoundsUnion.IsEmpty)
            {
                bool targetBelow = rangeBoundsUnion.Bottom >=
                    viewportBounds.Bottom - Math.Max(2, minimumVisibleHeight / 2);
                double verticalGap = targetBelow
                    ? rangeBoundsUnion.Top - viewportBounds.Bottom
                    : viewportBounds.Top - rangeBoundsUnion.Bottom;
                ScrollAmount amount = verticalGap > Math.Max(24, viewportBounds.Height / 2d)
                    ? targetBelow ? ScrollAmount.LargeIncrement : ScrollAmount.LargeDecrement
                    : targetBelow ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement;
                ScrollNearestVerticalAncestor(
                    host,
                    amount);
            }
            else
            {
                double searchPercent = attempt < fineSearchAttempts
                    ? attempt
                    : fineSearchAttempts +
                        ((attempt - fineSearchAttempts) * (100d - fineSearchAttempts) /
                         (totalSearchAttempts - fineSearchAttempts - 1));
                SetNearestVerticalAncestorScrollPercent(
                    host,
                    searchFromBottom ? 100 - searchPercent : searchPercent);
            }

            range.ScrollIntoView(alignToTop: true);
            Thread.Sleep(200);
            try
            {
                var currentTextPattern = host.Patterns.Text.PatternOrDefault;
                range = currentTextPattern?.DocumentRange.FindText(
                        rangeText,
                        backward: false,
                        ignoreCase: false)
                    ?? range;
            }
            catch (COMException)
            {
            }
        }

        throw new InvalidOperationException(
            $"{description}: TextPattern range had no visible glyph bounds after realization retries. " +
            $"Viewport={lastViewportBounds}; Range={lastRangeBounds}; Rectangles=[{lastRangeRectangles}]. " +
            $"Ancestors=[{DescribeScrollAncestors(host)}].");
    }

    // This method stays whole because it selects one resolved phrase in the Markdown host with a stepped pointer drag,
    // then proves that the same selection copies through Ctrl+C and through the context menu Copy, using clipboard
    // sentinels set between the steps.
    internal static void ExerciseMyWorkItemMarkdownCopy(
        Window window,
        UIA3Automation automation,
        string hostAutomationId,
        string selectionPhrase,
        string context)
    {
        AutomationElement host = WaitForElement(
            $"{context} Markdown host",
            () => FindCurrentVisibleByAutomationId(window, hostAutomationId),
            TimeSpan.FromSeconds(10));
        var textPattern = host.Patterns.Text.PatternOrDefault
            ?? throw new InvalidOperationException($"{context}: Markdown host did not expose TextPattern.");
        string documentText = textPattern.DocumentRange.GetText(-1);
        string[] preferredWords = selectionPhrase
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.Trim('.', ',', ':', ';', '!', '?', '`', '\'', '"'))
            .Where(word => word.Length >= 5)
            .ToArray();
        string[] documentWords = documentText
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.Trim('.', ',', ':', ';', '!', '?', '`', '\'', '"', '-', '*', '#'))
            .Where(word => word.Length >= 5)
            .Take(40)
            .ToArray();
        string effectivePhrase = preferredWords
            .Concat(documentWords)
            .FirstOrDefault(candidate =>
                textPattern.DocumentRange.FindText(candidate, backward: false, ignoreCase: false) is not null)
            ?? throw new InvalidOperationException($"{context}: Markdown host exposed no selectable text range.");
        var range = textPattern.DocumentRange.FindText(
            effectivePhrase,
            backward: false,
            ignoreCase: false)
            ?? throw new InvalidOperationException($"{context}: selectable text range disappeared before interaction.");
        Rectangle selectionRect = FindVisibleMarkdownRangeRect(host, range, $"{context} selection phrase");
        Point start = new(selectionRect.Left + 2, selectionRect.Top + selectionRect.Height / 2);
        Point end = new(selectionRect.Right - 2, selectionRect.Top + selectionRect.Height / 2);
        Point selectionPoint = new((start.X + end.X) / 2, start.Y);

        TryActivateWindow(window);
        host.Focus();
        Thread.Sleep(100);
        SendMouseInput(start, MouseEventFlags.MOUSEEVENTF_MOVE);
        SendMouseInput(start, MouseEventFlags.MOUSEEVENTF_LEFTDOWN);
        for (int step = 1; step <= 12; step++)
        {
            double ratio = step / 12d;
            SendMouseInput(new Point(
                (int)Math.Round(start.X + ((end.X - start.X) * ratio)),
                start.Y), MouseEventFlags.MOUSEEVENTF_MOVE | MouseEventFlags.MOUSEEVENTF_MOVE_NOCOALESCE);
            Thread.Sleep(25);
        }
        SendMouseInput(end, MouseEventFlags.MOUSEEVENTF_LEFTUP);
        Thread.Sleep(200);
        string selectedText = string.Concat(textPattern.GetSelection().Select(item => item.GetText(-1)));
        AssertProbe(selectedText.Contains(effectivePhrase, StringComparison.Ordinal),
            $"{context}: pointer selection was not preserved by the Markdown host.");

        NativeMethods.SetClipboardText("__jithub_my_work_item_ctrl_c_pending__");
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_C);
        WaitUntil(
            $"{context} Ctrl+C",
            () => NativeMethods.GetClipboardText().Contains(effectivePhrase, StringComparison.Ordinal),
            TimeSpan.FromSeconds(4));

        NativeMethods.SetClipboardText("__jithub_my_work_item_context_copy_pending__");
        Mouse.MoveTo(selectionPoint);
        Thread.Sleep(100);
        Mouse.RightClick();
        Thread.Sleep(400);
        AutomationElement copy = WaitForElement(
            $"{context} context Copy",
            () => window.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .FirstOrDefault(item =>
                        string.Equals(GetElementName(item), "Copy", StringComparison.OrdinalIgnoreCase) &&
                        !item.Properties.IsOffscreen.ValueOrDefault)
                ?? automation.GetDesktop()
                    .FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem))
                    .FirstOrDefault(item =>
                        string.Equals(GetElementName(item), "Copy", StringComparison.OrdinalIgnoreCase) &&
                        !item.Properties.IsOffscreen.ValueOrDefault),
            TimeSpan.FromSeconds(5));
        InvokeOrClick(copy);
        WaitUntil(
            $"{context} context Copy clipboard",
            () => NativeMethods.GetClipboardText().Contains(effectivePhrase, StringComparison.Ordinal),
            TimeSpan.FromSeconds(4));
    }

    internal static void AssertMarkdownTextVisible(Window window, params string[] expectedFragments)
    {
        string[] visibleTexts = window
            .FindAllDescendants(cf => cf.ByControlType(ControlType.Text))
            .Where(IsVisible)
            .Select(GetElementName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToArray();

        foreach (string expectedFragment in expectedFragments)
        {
            AssertProbe(
                visibleTexts.Any(text => text.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase)),
                $"Expected markdown/comment text fragment '{expectedFragment}' was not visible.");
        }
    }
}
