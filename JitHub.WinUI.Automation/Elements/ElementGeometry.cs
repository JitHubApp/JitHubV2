using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using static JitHub.WinUI.Automation.Conditions.ProbeConditions;
using static JitHub.WinUI.Automation.Elements.ElementQueries;

namespace JitHub.WinUI.Automation.Elements;

/// <summary>
/// Compares element bounds with the window and with other elements.
/// </summary>
internal static class ElementGeometry
{
    internal static Rectangle GetClippedAutomationBounds(AutomationElement element)
    {
        Rectangle clippedBounds = element.BoundingRectangle;
        ITreeWalker walker = element.Automation.TreeWalkerFactory.GetControlViewWalker();
        AutomationElement? candidate = TryGetAutomationParent(walker, element);
        for (int depth = 0; candidate is not null && depth < 16 && !clippedBounds.IsEmpty; depth++)
        {
            Rectangle parentBounds;
            try
            {
                parentBounds = candidate.BoundingRectangle;
            }
            catch (COMException)
            {
                break;
            }

            if (parentBounds.Width > 1 && parentBounds.Height > 1)
            {
                clippedBounds = Rectangle.Intersect(clippedBounds, parentBounds);
            }

            candidate = TryGetAutomationParent(walker, candidate);
        }

        return clippedBounds;
    }

    internal static void AssertElementBoundsAligned(System.Drawing.Rectangle expectedBounds, AutomationElement actualElement, string context)
    {
        var actualBounds = actualElement.BoundingRectangle;
        const double tolerance = 1.5;
        double leftDelta = Math.Abs(actualBounds.X - expectedBounds.X);
        double topDelta = Math.Abs(actualBounds.Y - expectedBounds.Y);
        double widthDelta = Math.Abs(actualBounds.Width - expectedBounds.Width);
        double heightDelta = Math.Abs(actualBounds.Height - expectedBounds.Height);
        AssertProbe(
            leftDelta <= tolerance &&
            topDelta <= tolerance &&
            widthDelta <= tolerance &&
            heightDelta <= tolerance,
            $"{context} bounds were not aligned. expected=({expectedBounds.X:0.0},{expectedBounds.Y:0.0},{expectedBounds.Width:0.0},{expectedBounds.Height:0.0}) actual=({actualBounds.X:0.0},{actualBounds.Y:0.0},{actualBounds.Width:0.0},{actualBounds.Height:0.0}) Δleft={leftDelta:0.0}, Δtop={topDelta:0.0}, Δwidth={widthDelta:0.0}, Δheight={heightDelta:0.0}.");
    }

    internal static System.Drawing.Point CenterPoint(AutomationElement element, Window window)
    {
        _ = window;
        var bounds = element.BoundingRectangle;
        return new System.Drawing.Point(
            (int)Math.Round(bounds.X + bounds.Width / 2d),
            (int)Math.Round(bounds.Y + bounds.Height / 2d));
    }

    internal static bool IsInsideWindowBounds(AutomationElement element, Window window)
    {
        try
        {
            var elementBounds = element.BoundingRectangle;
            var windowBounds = window.BoundingRectangle;
            if (elementBounds.Width <= 0 || elementBounds.Height <= 0)
            {
                return false;
            }

            return elementBounds.X >= windowBounds.X
                && elementBounds.Y >= windowBounds.Y
                && elementBounds.X + elementBounds.Width <= windowBounds.X + windowBounds.Width
                && elementBounds.Y + elementBounds.Height <= windowBounds.Y + windowBounds.Height;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsInsideElementBounds(AutomationElement element, AutomationElement container, double tolerance = 0)
    {
        try
        {
            var elementBounds = element.BoundingRectangle;
            var containerBounds = container.BoundingRectangle;
            if (elementBounds.Width <= 0 || elementBounds.Height <= 0 || containerBounds.Width <= 0 || containerBounds.Height <= 0)
            {
                return false;
            }

            return elementBounds.X >= containerBounds.X - tolerance
                && elementBounds.Y >= containerBounds.Y - tolerance
                && elementBounds.X + elementBounds.Width <= containerBounds.X + containerBounds.Width + tolerance
                && elementBounds.Y + elementBounds.Height <= containerBounds.Y + containerBounds.Height + tolerance;
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsHorizontallyCentered(AutomationElement element, Window window, double tolerance)
    {
        try
        {
            var elementBounds = element.BoundingRectangle;
            var windowBounds = window.BoundingRectangle;
            double elementCenter = elementBounds.X + elementBounds.Width / 2d;
            double windowCenter = windowBounds.X + windowBounds.Width / 2d;
            return Math.Abs(elementCenter - windowCenter) <= tolerance;
        }
        catch
        {
            return false;
        }
    }
}
