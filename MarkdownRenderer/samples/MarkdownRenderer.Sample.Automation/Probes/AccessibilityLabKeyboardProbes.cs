using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using MarkdownRenderer.Sample.Automation.Uia;
using static MarkdownRenderer.Sample.Automation.Uia.AutomationElementText;
using static MarkdownRenderer.Sample.Automation.Uia.FocusClassification;
using static MarkdownRenderer.Sample.Automation.Uia.ProbeAssert;
using static MarkdownRenderer.Sample.Automation.Uia.SampleWindowQueries;

namespace MarkdownRenderer.Sample.Automation.Probes;

/// <summary>
/// Probes the Accessibility Lab's Tab order across painted links and hosted controls.
/// </summary>
internal static class AccessibilityLabKeyboardProbes
{
    internal static void ProbeAccessibilityLabKeyboardOrder(Window window)
    {
        SelectSample(window, "AccessibilityLab");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);

        var focusedPaintedLink = TabIntoRendererFromSampleList(window, renderer);
        Assert(IsPaintedLinkFocus(focusedPaintedLink),
            $"Tab entry should promote renderer root focus to the first painted hyperlink, focused={DescribeFocus(focusedPaintedLink)}");

        Keyboard.Press(VirtualKeyShort.TAB); // hosted button
        Thread.Sleep(250);
        var focusedButton = renderer.Automation.FocusedElement();
        Assert(IsNativeActionButton(focusedButton),
            $"Second Tab should land on hosted native button, focused={DescribeFocus(focusedButton)}");

        Keyboard.Press(VirtualKeyShort.TAB); // first focusable descendant in composite embed
        Thread.Sleep(250);
        var focusedCompositeTextBox = renderer.Automation.FocusedElement();
        Assert(IsCompositeValueTextBox(focusedCompositeTextBox),
            $"Third Tab should land inside the composite hosted embed text box, focused={DescribeFocus(focusedCompositeTextBox)}");

        Keyboard.Press(VirtualKeyShort.TAB); // second focusable descendant in composite embed
        Thread.Sleep(250);
        var focusedCompositeButton = renderer.Automation.FocusedElement();
        Assert(IsCompositeActionButton(focusedCompositeButton),
            $"Fourth Tab should stay inside the composite hosted embed and land on its button, focused={DescribeFocus(focusedCompositeButton)}");

        Keyboard.Press(VirtualKeyShort.TAB); // copy button for the declined hosted-element fallback
        Thread.Sleep(250);
        var focusedCopyButton = renderer.Automation.FocusedElement();
        Assert(
            string.Equals(NameOrEmpty(focusedCopyButton), "Copy code", StringComparison.Ordinal) &&
            AutomationIdOrEmpty(focusedCopyButton).StartsWith("MarkdownCodeCopy-", StringComparison.Ordinal),
            $"Fifth Tab should land on the fallback code-block copy button, focused={DescribeFocus(focusedCopyButton)}");

        var focusedSecondPaintedLink = PressTabExpectPaintedLink(renderer, "Sixth Tab");
        Assert(IsPaintedLinkFocus(focusedSecondPaintedLink),
            $"Sixth Tab should land on second painted hyperlink, focused={DescribeFocus(focusedSecondPaintedLink)}");

        Keyboard.Press(VirtualKeyShort.TAB); // seventh Tab leaves markdown
        Thread.Sleep(300);
        var focusedAfterExit = renderer.Automation.FocusedElement();
        Assert(!IsAccessibilityLabInternalFocus(focusedAfterExit),
            $"Tab at the last markdown item must leave the renderer instead of looping into embeds, focused={DescribeFocus(focusedAfterExit)}");
        TryFocus(window, "main window after markdown keyboard boundary probe");
        Thread.Sleep(250);
    }

    internal static AutomationElement TabIntoRendererFromSampleList(Window window, AutomationElement renderer)
    {
        AutomationElement? observedPaintedLink = null;
        var observed = new List<string>();
        using var paintedLinkFocused = new ManualResetEventSlim(false);
        var focusHandler = renderer.Automation.RegisterFocusChangedEvent(element =>
        {
            string description;
            try { description = DescribeFocus(element); }
            catch (Exception ex) { description = $"<unreadable focus event: {ex.Message}>"; }

            lock (observed) observed.Add(description);
            if (IsPaintedLinkFocus(element))
            {
                observedPaintedLink = element;
                paintedLinkFocused.Set();
            }
        });

        var source = window.FindFirstDescendant(cf => cf.ByAutomationId("SampleNav_AccessibilityLab"))
                     ?? throw new InvalidOperationException("Accessibility Lab navigation item not found");
        try
        {
            source.Focus();
            Thread.Sleep(250);

            for (int i = 0; i < 80; i++)
            {
                Keyboard.Press(VirtualKeyShort.TAB);
                if (paintedLinkFocused.Wait(TimeSpan.FromMilliseconds(500)) && observedPaintedLink is not null)
                    return observedPaintedLink;

                var focused = renderer.Automation.FocusedElement();
                if (IsAccessibilityLabInternalFocus(focused))
                {
                    if (paintedLinkFocused.Wait(TimeSpan.FromMilliseconds(750)) && observedPaintedLink is not null)
                        return observedPaintedLink;

                    lock (observed)
                    {
                        throw new InvalidOperationException(
                            $"Tab reached markdown without a virtual hyperlink focus event. Current focus={DescribeFocus(focused)}; events={string.Join(" | ", observed)}");
                    }
                }
            }

            lock (observed)
            {
                throw new InvalidOperationException(
                    $"Tab did not reach the Accessibility Lab renderer within 80 stops; events={string.Join(" | ", observed)}");
            }
        }
        finally
        {
            renderer.Automation.UnregisterFocusChangedEvent(focusHandler);
        }
    }

    private static AutomationElement PressTabExpectPaintedLink(AutomationElement renderer, string stepName)
    {
        AutomationElement? observedPaintedLink = null;
        var observed = new List<string>();
        using var paintedLinkFocused = new ManualResetEventSlim(false);
        var focusHandler = renderer.Automation.RegisterFocusChangedEvent(element =>
        {
            string description;
            try { description = DescribeFocus(element); }
            catch (Exception ex) { description = $"<unreadable focus event: {ex.Message}>"; }

            lock (observed) observed.Add(description);
            if (IsPaintedLinkFocus(element))
            {
                observedPaintedLink = element;
                paintedLinkFocused.Set();
            }
        });

        try
        {
            Keyboard.Press(VirtualKeyShort.TAB);
            if (paintedLinkFocused.Wait(TimeSpan.FromMilliseconds(1000)) && observedPaintedLink is not null)
                return observedPaintedLink;

            var focused = renderer.Automation.FocusedElement();
            lock (observed)
            {
                throw new InvalidOperationException(
                    $"{stepName} did not raise virtual hyperlink focus. Current focus={DescribeFocus(focused)}; events={string.Join(" | ", observed)}");
            }
        }
        finally
        {
            renderer.Automation.UnregisterFocusChangedEvent(focusHandler);
        }
    }

    internal static void ProbeAccessibilityLabPointerResume(Window window)
    {
        SelectSample(window, "AccessibilityLab");
        Thread.Sleep(1200);

        var renderer = FindRenderer(window);
        var button = renderer.FindAllDescendants(cf => cf.ByControlType(ControlType.Button))
            .FirstOrDefault(e => (e.Name ?? string.Empty).Contains("Native action", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Hosted Native action button not found");

        var buttonBounds = button.BoundingRectangle;
        var rendererBounds = renderer.BoundingRectangle;
        var nearbyDocumentPoint = new System.Drawing.Point(
            buttonBounds.Left + buttonBounds.Width / 2,
            Math.Max(rendererBounds.Top + 4, buttonBounds.Top - 12));
        Mouse.Click(nearbyDocumentPoint, FlaUI.Core.Input.MouseButton.Left);
        Thread.Sleep(300);

        AutomationElement focused = renderer.Automation.FocusedElement();
        for (int attempt = 0; attempt < 5; attempt++)
        {
            Keyboard.Press(VirtualKeyShort.TAB);
            Thread.Sleep(300);
            focused = renderer.Automation.FocusedElement();
            if (IsCompositeValueTextBox(focused) || IsCompositeActionButton(focused))
                break;
        }

        Assert(IsCompositeValueTextBox(focused) || IsCompositeActionButton(focused),
            $"Tab after pointer dismissal near hosted controls should resume within markdown focus order, focused={DescribeFocus(focused)}");
    }
}
