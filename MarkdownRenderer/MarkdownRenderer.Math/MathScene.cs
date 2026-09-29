using System.Collections.ObjectModel;

namespace MarkdownRenderer.Math;

/// <summary>Identifies one operation in a vector path.</summary>
public enum MathPathOperationKind
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

/// <summary>A finite point in device-independent scene coordinates.</summary>
public readonly record struct MathPoint
{
    /// <summary>Creates a point.</summary>
    public MathPoint(float x, float y)
    {
        MathSceneValidation.ThrowIfNotFinite(x, nameof(x));
        MathSceneValidation.ThrowIfNotFinite(y, nameof(y));
        X = x;
        Y = y;
    }

    /// <summary>Gets the horizontal coordinate.</summary>
    public float X { get; }

    /// <summary>Gets the vertical coordinate.</summary>
    public float Y { get; }
}

/// <summary>An immutable path operation.</summary>
public readonly record struct MathPathOperation
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

/// <summary>Identifies a native-painter-neutral drawing operation.</summary>
public enum MathSceneCommandKind
{
    /// <summary>Fills a vector path.</summary>
    FillPath,
    /// <summary>Strokes a vector path.</summary>
    StrokePath,
    /// <summary>Fills an axis-aligned rectangle.</summary>
    FillRectangle,
    /// <summary>Strokes an axis-aligned rectangle.</summary>
    StrokeRectangle,
    /// <summary>Strokes a line, normally a fraction or radical rule.</summary>
    StrokeLine,
}

/// <summary>An immutable rectangle in device-independent scene coordinates.</summary>
public readonly record struct MathRectangle
{
    /// <summary>Creates a rectangle.</summary>
    public MathRectangle(float x, float y, float width, float height)
    {
        MathSceneValidation.ThrowIfNotFinite(x, nameof(x));
        MathSceneValidation.ThrowIfNotFinite(y, nameof(y));
        MathSceneValidation.ThrowIfNotFiniteOrNegative(width, nameof(width));
        MathSceneValidation.ThrowIfNotFiniteOrNegative(height, nameof(height));
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    /// <summary>Gets the left coordinate.</summary>
    public float X { get; }

    /// <summary>Gets the top coordinate.</summary>
    public float Y { get; }

    /// <summary>Gets the width.</summary>
    public float Width { get; }

    /// <summary>Gets the height.</summary>
    public float Height { get; }
}

/// <summary>
/// One immutable formula scene command. A null color requests the host's semantic
/// math foreground; explicit colors may still be replaced in High Contrast.
/// </summary>
public sealed class MathSceneCommand
{
    private static readonly IReadOnlyList<MathPathOperation> EmptyPath = Array.Empty<MathPathOperation>();

    internal MathSceneCommand(
        MathSceneCommandKind kind,
        IReadOnlyList<MathPathOperation>? path,
        MathRectangle rectangle,
        MathPoint start,
        MathPoint end,
        float strokeWidth,
        uint? colorArgb)
    {
        MathSceneValidation.ThrowIfNotFiniteOrNegative(strokeWidth, nameof(strokeWidth));
        Kind = kind;
        Path = CopyPath(path);
        Rectangle = rectangle;
        Start = start;
        End = end;
        StrokeWidth = strokeWidth;
        ColorArgb = colorArgb;
    }

    /// <summary>Gets the operation kind.</summary>
    public MathSceneCommandKind Kind { get; }

    /// <summary>Gets path operations for a path command.</summary>
    public IReadOnlyList<MathPathOperation> Path { get; }

    /// <summary>Gets the rectangle for a rectangle command.</summary>
    public MathRectangle Rectangle { get; }

    /// <summary>Gets the start point for a line command.</summary>
    public MathPoint Start { get; }

    /// <summary>Gets the end point for a line command.</summary>
    public MathPoint End { get; }

    /// <summary>Gets the stroke width. It is zero for fill commands.</summary>
    public float StrokeWidth { get; }

    /// <summary>Gets an explicit ARGB color, or null for the semantic formula foreground.</summary>
    public uint? ColorArgb { get; }

    private static IReadOnlyList<MathPathOperation> CopyPath(IReadOnlyList<MathPathOperation>? path)
    {
        if (path is null || path.Count == 0)
            return EmptyPath;

        var copy = new MathPathOperation[path.Count];
        for (int i = 0; i < path.Count; i++)
            copy[i] = path[i];
        return new ReadOnlyCollection<MathPathOperation>(copy);
    }
}

/// <summary>
/// An immutable, thread-safe vector scene for one formula. The scene contains
/// outlines and rules only; it owns no Win2D, device, bitmap, or font-lifetime object.
/// </summary>
public sealed class MathScene
{
    internal MathScene(float width, float height, float baseline, IReadOnlyList<MathSceneCommand> commands)
    {
        MathSceneValidation.ThrowIfNotFiniteOrNegative(width, nameof(width));
        MathSceneValidation.ThrowIfNotFiniteOrNegative(height, nameof(height));
        MathSceneValidation.ThrowIfNotFiniteOrNegative(baseline, nameof(baseline));
        if (baseline > height)
            throw new ArgumentOutOfRangeException(nameof(baseline), "The baseline must lie inside the scene.");

        ArgumentNullException.ThrowIfNull(commands);
        var copy = new MathSceneCommand[commands.Count];
        for (int i = 0; i < commands.Count; i++)
            copy[i] = commands[i] ?? throw new ArgumentException("Scene commands cannot contain null.", nameof(commands));

        Width = width;
        Height = height;
        Baseline = baseline;
        Commands = new ReadOnlyCollection<MathSceneCommand>(copy);
    }

    /// <summary>Gets the scene width in device-independent units.</summary>
    public float Width { get; }

    /// <summary>Gets the scene height in device-independent units.</summary>
    public float Height { get; }

    /// <summary>Gets the baseline measured from the scene's top edge.</summary>
    public float Baseline { get; }

    /// <summary>Gets drawing operations in painter order.</summary>
    public IReadOnlyList<MathSceneCommand> Commands { get; }
}

internal static class MathSceneValidation
{
    internal static void ThrowIfNotFinite(float value, string parameterName)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(parameterName, "Scene coordinates must be finite.");
    }

    internal static void ThrowIfNotFiniteOrNegative(float value, string parameterName)
    {
        ThrowIfNotFinite(value, parameterName);
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
    }
}
