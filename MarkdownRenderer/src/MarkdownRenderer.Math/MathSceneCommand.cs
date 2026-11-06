using System.Collections.ObjectModel;

namespace MarkdownRenderer.Math;

/// <summary>
/// One immutable formula scene command. A null color requests the host's semantic
/// math foreground; explicit colors may still be replaced in High Contrast.
/// </summary>
internal sealed class MathSceneCommand
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
