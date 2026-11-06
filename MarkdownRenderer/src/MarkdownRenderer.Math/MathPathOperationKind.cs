namespace MarkdownRenderer.Math;

/// <summary>Identifies one operation in a vector path.</summary>
internal enum MathPathOperationKind
{
    /// <summary>Starts a new contour.</summary>
    Move,
    /// <summary>Adds a straight segment.</summary>
    Line,
    /// <summary>Adds a quadratic Bezier segment.</summary>
    QuadraticBezier,
    /// <summary>Adds a cubic Bezier segment.</summary>
    CubicBezier,
    /// <summary>Closes the current contour.</summary>
    Close,
}
