namespace MarkdownRenderer.Math.Internal.Scenes;

/// <summary>An immutable path operation.</summary>
internal readonly record struct MathPathOperation
{
    private MathPathOperation(
        MathPathOperationKind kind,
        MathPoint point1,
        MathPoint point2,
        MathPoint point3)
    {
        Kind = kind;
        Point1 = point1;
        Point2 = point2;
        Point3 = point3;
    }

    /// <summary>Gets the operation kind.</summary>
    public MathPathOperationKind Kind { get; }

    /// <summary>Gets the end point, or the first control point for a cubic curve.</summary>
    public MathPoint Point1 { get; }

    /// <summary>Gets the end point for a quadratic curve, or second control point for a cubic curve.</summary>
    public MathPoint Point2 { get; }

    /// <summary>Gets the end point for a cubic curve.</summary>
    public MathPoint Point3 { get; }

    /// <summary>Creates a move operation.</summary>
    public static MathPathOperation MoveTo(MathPoint point) =>
        new(MathPathOperationKind.Move, point, default, default);

    /// <summary>Creates a line operation.</summary>
    public static MathPathOperation LineTo(MathPoint point) =>
        new(MathPathOperationKind.Line, point, default, default);

    /// <summary>Creates a quadratic Bezier operation.</summary>
    public static MathPathOperation QuadraticBezierTo(MathPoint control, MathPoint end) =>
        new(MathPathOperationKind.QuadraticBezier, control, end, default);

    /// <summary>Creates a cubic Bezier operation.</summary>
    public static MathPathOperation CubicBezierTo(MathPoint control1, MathPoint control2, MathPoint end) =>
        new(MathPathOperationKind.CubicBezier, control1, control2, end);

    /// <summary>Creates a contour-closing operation.</summary>
    public static MathPathOperation Close() =>
        new(MathPathOperationKind.Close, default, default, default);
}
