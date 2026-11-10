using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;

namespace JitHub.WinUI.Automation.Interaction;

/// <summary>
/// Scrolls elements and their nearest vertical scroll ancestors.
/// </summary>
internal static class ScrollActions
{
    internal static string DescribeScrollAncestors(AutomationElement element)
    {
        var descriptions = new List<string>();
        ITreeWalker walker = element.Automation.TreeWalkerFactory.GetControlViewWalker();
        AutomationElement? candidate = TryGetAutomationParent(walker, element);
        for (int depth = 0; candidate is not null && depth < 16; depth++)
        {
            string scroll = "scroll=none";
            try
            {
                if (candidate.Patterns.Scroll.IsSupported)
                {
                    var pattern = candidate.Patterns.Scroll.Pattern;
                    scroll = $"scroll=v:{pattern.VerticallyScrollable.ValueOrDefault}," +
                        $"p:{pattern.VerticalScrollPercent.ValueOrDefault:0.#}," +
                        $"view:{pattern.VerticalViewSize.ValueOrDefault:0.#}";
                }
            }
            catch (COMException)
            {
                scroll = "scroll=stale";
            }

            try
            {
                descriptions.Add(
                    $"{depth}:{candidate.ControlType}/{GetAutomationId(candidate)}/{candidate.BoundingRectangle}/{scroll}");
            }
            catch (COMException)
            {
                descriptions.Add($"{depth}:stale/{scroll}");
                break;
            }

            candidate = TryGetAutomationParent(walker, candidate);
        }

        return string.Join(" | ", descriptions);
    }

    internal static void ScrollNearestVerticalAncestor(AutomationElement element, ScrollAmount amount)
    {
        ITreeWalker walker = element.Automation.TreeWalkerFactory.GetControlViewWalker();
        AutomationElement? candidate = TryGetAutomationParent(walker, element);
        for (int depth = 0; candidate is not null && depth < 16; depth++)
        {
            try
            {
                if (candidate.Patterns.Scroll.IsSupported)
                {
                    var scroll = candidate.Patterns.Scroll.Pattern;
                    if (scroll.VerticallyScrollable.ValueOrDefault)
                    {
                        scroll.Scroll(ScrollAmount.NoAmount, amount);
                        return;
                    }
                }
            }
            catch (COMException)
            {
            }

            candidate = TryGetAutomationParent(walker, candidate);
        }
    }

    internal static void SetNearestVerticalAncestorScrollPercent(AutomationElement element, double verticalPercent)
    {
        ITreeWalker walker = element.Automation.TreeWalkerFactory.GetControlViewWalker();
        AutomationElement? candidate = TryGetAutomationParent(walker, element);
        for (int depth = 0; candidate is not null && depth < 16; depth++)
        {
            try
            {
                if (candidate.Patterns.Scroll.IsSupported)
                {
                    var scroll = candidate.Patterns.Scroll.Pattern;
                    if (scroll.VerticallyScrollable.ValueOrDefault)
                    {
                        scroll.SetScrollPercent(
                            FlaUI.Core.Patterns.ScrollPatternConstants.NoScroll,
                            Math.Clamp(verticalPercent, 0, 100));
                        return;
                    }
                }
            }
            catch (COMException)
            {
            }

            candidate = TryGetAutomationParent(walker, candidate);
        }
    }

    internal static void ScrollElementToBottom(AutomationElement element)
    {
        try
        {
            if (element.Patterns.Scroll.IsSupported)
            {
                var scroll = element.Patterns.Scroll.Pattern;
                for (int index = 0; index < 8; index++)
                {
                    scroll.Scroll(ScrollAmount.NoAmount, ScrollAmount.LargeIncrement);
                    Thread.Sleep(60);
                }

                return;
            }
        }
        catch
        {
        }

        try
        {
            element.Focus();
        }
        catch
        {
        }

        for (int index = 0; index < 8; index++)
        {
            Keyboard.Press((VirtualKeyShort)0x22);
            Thread.Sleep(60);
        }
    }

    internal static double GetVerticalScrollPercent(AutomationElement element)
    {
        AssertProbe(element.Patterns.Scroll.IsSupported, $"{GetAutomationId(element)} does not expose the Scroll pattern.");
        var scroll = element.Patterns.Scroll.Pattern;
        AssertProbe(scroll.VerticalScrollPercent.IsSupported, $"{GetAutomationId(element)} does not expose vertical scroll state.");
        return scroll.VerticalScrollPercent.ValueOrDefault;
    }
}
