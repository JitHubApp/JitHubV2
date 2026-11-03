namespace MarkdownRenderer.Extensions;

/// <summary>Identifies one operation in a device-independent vector path.</summary>
public enum MarkdownVectorPathOperationKind
{
    /// <summary>Starts a contour.</summary>
    Move,
    /// <summary>Adds a line segment.</summary>
    Line,
    /// <summary>Adds a quadratic Bezier segment.</summary>
    QuadraticBezier,
    /// <summary>Adds a cubic Bezier segment.</summary>
    CubicBezier,
    /// <summary>Closes the current contour.</summary>
    Close,
}
