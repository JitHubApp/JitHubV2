using System;
using Microsoft.UI.Xaml.Automation;
using MarkdownRenderer.Controls;

namespace MarkdownRenderer.Accessibility.Internal.Peers;

/// <summary>
/// The UIA ScrollPattern of <see cref="MarkdownAutomationPeer"/> when the
/// control owns its scroll viewport. The document scrolls only vertically.
/// </summary>
internal sealed class MarkdownAutomationScrollPattern
{
    private readonly MarkdownRendererControl _owner;

    internal MarkdownAutomationScrollPattern(MarkdownRendererControl owner)
    {
        _owner = owner;
    }

    internal const double NoScroll = -1;

    internal bool HorizontallyScrollable => false;

    internal double HorizontalScrollPercent => NoScroll;

    internal double HorizontalViewSize => 100;

    internal bool VerticallyScrollable => TryGetVerticalScrollMetrics(out _, out _, out _);

    internal double VerticalScrollPercent
    {
        get
        {
            if (!TryGetVerticalScrollMetrics(out double offset, out _, out double maximum))
                return NoScroll;

            return maximum <= 0 ? NoScroll : (offset / maximum) * 100;
        }
    }

    internal double VerticalViewSize
    {
        get
        {
            if (!_owner.AutomationScrolling.TryGetMetrics(out _, out double viewport, out double extent) ||
                extent <= 0)
            {
                return 100;
            }

            return System.Math.Clamp((viewport / extent) * 100, 0, 100);
        }
    }

    internal void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount)
    {
        if (horizontalAmount != ScrollAmount.NoAmount)
            throw new InvalidOperationException("The markdown document does not scroll horizontally.");
        if (verticalAmount == ScrollAmount.NoAmount)
            return;
        if (!VerticallyScrollable)
            throw new InvalidOperationException("The markdown document has no vertical overflow.");

        _owner.AutomationScrolling.Scroll(verticalAmount);
    }

    internal void SetScrollPercent(double horizontalPercent, double verticalPercent)
    {
        ValidateScrollPercent(horizontalPercent, nameof(horizontalPercent));
        ValidateScrollPercent(verticalPercent, nameof(verticalPercent));
        if (horizontalPercent != NoScroll)
            throw new InvalidOperationException("The markdown document does not scroll horizontally.");
        if (verticalPercent == NoScroll)
            return;
        if (!VerticallyScrollable)
            throw new InvalidOperationException("The markdown document has no vertical overflow.");

        _owner.AutomationScrolling.SetVerticalPercent(verticalPercent);
    }

    private bool TryGetVerticalScrollMetrics(
        out double offset,
        out double viewport,
        out double maximum)
    {
        if (!_owner.AutomationScrolling.TryGetMetrics(out offset, out viewport, out double extent))
        {
            maximum = 0;
            return false;
        }

        maximum = System.Math.Max(0, extent - viewport);
        return maximum > 0;
    }

    private static void ValidateScrollPercent(double value, string parameterName)
    {
        if (value != NoScroll &&
            (!double.IsFinite(value) || value < 0 || value > 100))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
