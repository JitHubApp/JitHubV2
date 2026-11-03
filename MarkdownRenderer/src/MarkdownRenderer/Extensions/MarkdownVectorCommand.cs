using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace MarkdownRenderer.Extensions;

/// <summary>One immutable painter-neutral scene command.</summary>
public sealed class MarkdownVectorCommand
{
    private static readonly IReadOnlyList<MarkdownVectorPathOperation> EmptyPath =
        Array.Empty<MarkdownVectorPathOperation>();

    private MarkdownVectorCommand(
        MarkdownVectorCommandKind kind,
        IReadOnlyList<MarkdownVectorPathOperation>? path,
        MarkdownVectorRectangle rectangle,
        MarkdownVectorPoint start,
        MarkdownVectorPoint end,
        float strokeWidth,
        uint? colorArgb,
        MarkdownVectorPaintStyle? style = null,
        string? text = null,
        MarkdownVectorTextStyle? textStyle = null,
        MarkdownVectorTransform transform = default,
        int semanticIndex = -1,
        bool isClosed = false)
    {
        if (!float.IsFinite(strokeWidth) || strokeWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(strokeWidth));

        Kind = kind;
        Path = CopyPath(path);
        Rectangle = rectangle;
        Start = start;
        End = end;
        StrokeWidth = strokeWidth;
        ColorArgb = colorArgb;
        Style = style;
        Text = text;
        TextStyle = textStyle;
        Transform = transform == default ? MarkdownVectorTransform.Identity : transform;
        SemanticIndex = semanticIndex;
        IsClosed = isClosed;
    }

    /// <summary>Gets the command kind.</summary>
    public MarkdownVectorCommandKind Kind { get; }
    /// <summary>Gets immutable path operations.</summary>
    public IReadOnlyList<MarkdownVectorPathOperation> Path { get; }
    /// <summary>Gets the command rectangle.</summary>
    public MarkdownVectorRectangle Rectangle { get; }
    /// <summary>Gets the line start.</summary>
    public MarkdownVectorPoint Start { get; }
    /// <summary>Gets the line end.</summary>
    public MarkdownVectorPoint End { get; }
    /// <summary>Gets the stroke width.</summary>
    public float StrokeWidth { get; }
    /// <summary>Gets an explicit ARGB color, or null for the semantic foreground.</summary>
    public uint? ColorArgb { get; }
    /// <summary>Gets a complete fill/stroke style for rich scene commands.</summary>
    public MarkdownVectorPaintStyle? Style { get; }
    /// <summary>Gets text for a resolved text command.</summary>
    public string? Text { get; }
    /// <summary>Gets resolved text attributes.</summary>
    public MarkdownVectorTextStyle? TextStyle { get; }
    /// <summary>Gets the transform carried by a group command.</summary>
    public MarkdownVectorTransform Transform { get; }
    /// <summary>Gets the associated semantic index, or -1.</summary>
    public int SemanticIndex { get; }
    /// <summary>Gets whether a rich path closes its final contour.</summary>
    public bool IsClosed { get; }

    /// <summary>Creates a filled path command.</summary>
    public static MarkdownVectorCommand FillPath(
        IReadOnlyList<MarkdownVectorPathOperation> path,
        uint? colorArgb = null) =>
        new(MarkdownVectorCommandKind.FillPath, RequirePath(path), default, default, default, 0, colorArgb);

    /// <summary>Creates a stroked path command.</summary>
    public static MarkdownVectorCommand StrokePath(
        IReadOnlyList<MarkdownVectorPathOperation> path,
        float strokeWidth,
        uint? colorArgb = null) =>
        new(MarkdownVectorCommandKind.StrokePath, RequirePath(path), default, default, default, strokeWidth, colorArgb);

    /// <summary>Creates a filled rectangle command.</summary>
    public static MarkdownVectorCommand FillRectangle(
        MarkdownVectorRectangle rectangle,
        uint? colorArgb = null) =>
        new(MarkdownVectorCommandKind.FillRectangle, null, rectangle, default, default, 0, colorArgb);

    /// <summary>Creates a stroked rectangle command.</summary>
    public static MarkdownVectorCommand StrokeRectangle(
        MarkdownVectorRectangle rectangle,
        float strokeWidth,
        uint? colorArgb = null) =>
        new(MarkdownVectorCommandKind.StrokeRectangle, null, rectangle, default, default, strokeWidth, colorArgb);

    /// <summary>Creates a stroked line command.</summary>
    public static MarkdownVectorCommand StrokeLine(
        MarkdownVectorPoint start,
        MarkdownVectorPoint end,
        float strokeWidth,
        uint? colorArgb = null) =>
        new(MarkdownVectorCommandKind.StrokeLine, null, default, start, end, strokeWidth, colorArgb);

    /// <summary>Creates a styled rectangle command.</summary>
    public static MarkdownVectorCommand DrawRectangle(
        MarkdownVectorRectangle rectangle,
        MarkdownVectorPaintStyle style,
        int semanticIndex = -1) =>
        new(MarkdownVectorCommandKind.DrawRectangle, null, rectangle, default, default, 0, null,
            style ?? throw new ArgumentNullException(nameof(style)), semanticIndex: semanticIndex);

    /// <summary>Creates a styled path command.</summary>
    public static MarkdownVectorCommand DrawPath(
        IReadOnlyList<MarkdownVectorPathOperation> path,
        MarkdownVectorPaintStyle style,
        bool isClosed = false,
        int semanticIndex = -1) =>
        new(MarkdownVectorCommandKind.DrawPath, RequirePath(path), default, default, default, 0, null,
            style ?? throw new ArgumentNullException(nameof(style)), semanticIndex: semanticIndex, isClosed: isClosed);

    /// <summary>Creates a styled line command.</summary>
    public static MarkdownVectorCommand DrawLine(
        MarkdownVectorPoint start,
        MarkdownVectorPoint end,
        MarkdownVectorPaintStyle style,
        int semanticIndex = -1) =>
        new(MarkdownVectorCommandKind.DrawLine, null, default, start, end, 0, null,
            style ?? throw new ArgumentNullException(nameof(style)), semanticIndex: semanticIndex);

    /// <summary>Creates a styled ellipse command.</summary>
    public static MarkdownVectorCommand DrawEllipse(
        MarkdownVectorRectangle rectangle,
        MarkdownVectorPaintStyle style,
        int semanticIndex = -1) =>
        new(MarkdownVectorCommandKind.DrawEllipse, null, rectangle, default, default, 0, null,
            style ?? throw new ArgumentNullException(nameof(style)), semanticIndex: semanticIndex);

    /// <summary>Creates one positioned text-line command.</summary>
    public static MarkdownVectorCommand DrawText(
        string text,
        MarkdownVectorPoint baselineOrigin,
        MarkdownVectorTextStyle textStyle,
        MarkdownVectorPaintStyle style,
        int semanticIndex = -1) =>
        new(MarkdownVectorCommandKind.DrawText, null, default, baselineOrigin, default, 0, null,
            style ?? throw new ArgumentNullException(nameof(style)),
            text ?? throw new ArgumentNullException(nameof(text)),
            textStyle ?? throw new ArgumentNullException(nameof(textStyle)),
            semanticIndex: semanticIndex);

    /// <summary>Begins a transformed group.</summary>
    public static MarkdownVectorCommand BeginGroup(
        MarkdownVectorTransform transform,
        int semanticIndex = -1) =>
        new(MarkdownVectorCommandKind.BeginGroup, null, default, default, default, 0, null,
            transform: transform, semanticIndex: semanticIndex);

    /// <summary>Ends the current group.</summary>
    public static MarkdownVectorCommand EndGroup() =>
        new(MarkdownVectorCommandKind.EndGroup, null, default, default, default, 0, null);

    /// <summary>Begins a path clip.</summary>
    public static MarkdownVectorCommand BeginClip(
        IReadOnlyList<MarkdownVectorPathOperation> path) =>
        new(MarkdownVectorCommandKind.BeginClip, RequirePath(path), default, default, default, 0, null);

    /// <summary>Ends the current clip.</summary>
    public static MarkdownVectorCommand EndClip() =>
        new(MarkdownVectorCommandKind.EndClip, null, default, default, default, 0, null);

    private static IReadOnlyList<MarkdownVectorPathOperation> RequirePath(
        IReadOnlyList<MarkdownVectorPathOperation> path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Count == 0)
            throw new ArgumentException("A vector path must contain at least one operation.", nameof(path));
        return path;
    }

    private static IReadOnlyList<MarkdownVectorPathOperation> CopyPath(
        IReadOnlyList<MarkdownVectorPathOperation>? path)
    {
        if (path is null || path.Count == 0)
            return EmptyPath;
        var copy = new MarkdownVectorPathOperation[path.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = path[i];
        return new ReadOnlyCollection<MarkdownVectorPathOperation>(copy);
    }
}
