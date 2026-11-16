namespace MarkdownRenderer.Extensions.Vectors;

/// <summary>An immutable vector-path operation.</summary>
public readonly record struct MarkdownVectorPathOperation
{
    private MarkdownVectorPathOperation(
        MarkdownVectorPathOperationKind kind,
        MarkdownVectorPoint point1,
        MarkdownVectorPoint point2,
        MarkdownVectorPoint point3)
    {
        Kind = kind;
        Point1 = point1;
        Point2 = point2;
        Point3 = point3;
    }

    /// <summary>Gets the operation kind.</summary>
    public MarkdownVectorPathOperationKind Kind { get; }

    /// <summary>Gets the first point or control point.</summary>
    public MarkdownVectorPoint Point1 { get; }

    /// <summary>Gets the second point or control point.</summary>
    public MarkdownVectorPoint Point2 { get; }

    /// <summary>Gets the third point.</summary>
    public MarkdownVectorPoint Point3 { get; }

    /// <summary>Creates a move operation.</summary>
    public static MarkdownVectorPathOperation MoveTo(MarkdownVectorPoint point) =>
        new(MarkdownVectorPathOperationKind.Move, point, default, default);

    /// <summary>Creates a line operation.</summary>
    public static MarkdownVectorPathOperation LineTo(MarkdownVectorPoint point) =>
        new(MarkdownVectorPathOperationKind.Line, point, default, default);

    /// <summary>Creates a quadratic Bezier operation.</summary>
    public static MarkdownVectorPathOperation QuadraticBezierTo(
        MarkdownVectorPoint control,
        MarkdownVectorPoint end) =>
        new(MarkdownVectorPathOperationKind.QuadraticBezier, control, end, default);

    /// <summary>Creates a cubic Bezier operation.</summary>
    public static MarkdownVectorPathOperation CubicBezierTo(
        MarkdownVectorPoint control1,
        MarkdownVectorPoint control2,
        MarkdownVectorPoint end) =>
        new(MarkdownVectorPathOperationKind.CubicBezier, control1, control2, end);

    /// <summary>Creates a contour-closing operation.</summary>
    public static MarkdownVectorPathOperation Close() =>
        new(MarkdownVectorPathOperationKind.Close, default, default, default);
}
