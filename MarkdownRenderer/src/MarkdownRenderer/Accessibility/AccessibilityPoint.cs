using System.Collections.Generic;

namespace MarkdownRenderer.Accessibility;

internal readonly struct AccessibilityPoint
{
    public AccessibilityPoint(double X, double Y)
    {
        this.X = X;
        this.Y = Y;
    }

    public double X { get; init; }
    public double Y { get; init; }
}
