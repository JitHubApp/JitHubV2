using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

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

/// <summary>A finite point in device-independent scene coordinates.</summary>
public readonly record struct MarkdownVectorPoint
{
    /// <summary>Creates a point.</summary>
    public MarkdownVectorPoint(float x, float y)
    {
        ThrowIfNotFinite(x, nameof(x));
        ThrowIfNotFinite(y, nameof(y));
        X = x;
        Y = y;
    }

    /// <summary>Gets the horizontal coordinate.</summary>
    public float X { get; }

    /// <summary>Gets the vertical coordinate.</summary>
    public float Y { get; }

    private static void ThrowIfNotFinite(float value, string name)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(name, "Vector coordinates must be finite.");
    }
}

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

/// <summary>Identifies one painter-neutral scene command.</summary>
public enum MarkdownVectorCommandKind
{
    /// <summary>Fills a vector path.</summary>
    FillPath,
    /// <summary>Strokes a vector path.</summary>
    StrokePath,
    /// <summary>Fills an axis-aligned rectangle.</summary>
    FillRectangle,
    /// <summary>Strokes an axis-aligned rectangle.</summary>
    StrokeRectangle,
    /// <summary>Strokes a line.</summary>
    StrokeLine,
    /// <summary>Draws a rectangle using a complete paint style.</summary>
    DrawRectangle,
    /// <summary>Draws a path using a complete paint style.</summary>
    DrawPath,
    /// <summary>Draws a line using a complete paint style.</summary>
    DrawLine,
    /// <summary>Draws an ellipse using a complete paint style.</summary>
    DrawEllipse,
    /// <summary>Draws one already-positioned line of text at its baseline.</summary>
    DrawText,
    /// <summary>Begins a transformed/opacity group.</summary>
    BeginGroup,
    /// <summary>Ends the current group.</summary>
    EndGroup,
    /// <summary>Begins a path clip.</summary>
    BeginClip,
    /// <summary>Ends the current path clip.</summary>
    EndClip,
}

/// <summary>A finite affine transform in painter-neutral scene coordinates.</summary>
public readonly record struct MarkdownVectorTransform
{
    /// <summary>Creates an affine transform.</summary>
    public MarkdownVectorTransform(float m11, float m12, float m21, float m22, float m31, float m32)
    {
        if (!float.IsFinite(m11) || !float.IsFinite(m12) || !float.IsFinite(m21) ||
            !float.IsFinite(m22) || !float.IsFinite(m31) || !float.IsFinite(m32))
        {
            throw new ArgumentOutOfRangeException(nameof(m11), "Vector transforms must be finite.");
        }

        M11 = m11;
        M12 = m12;
        M21 = m21;
        M22 = m22;
        M31 = m31;
        M32 = m32;
    }

    /// <summary>Gets the identity transform.</summary>
    public static MarkdownVectorTransform Identity { get; } = new(1, 0, 0, 1, 0, 0);
    /// <summary>Gets the horizontal scale/rotation coefficient.</summary>
    public float M11 { get; }
    /// <summary>Gets the vertical shear/rotation coefficient.</summary>
    public float M12 { get; }
    /// <summary>Gets the horizontal shear/rotation coefficient.</summary>
    public float M21 { get; }
    /// <summary>Gets the vertical scale/rotation coefficient.</summary>
    public float M22 { get; }
    /// <summary>Gets the horizontal translation.</summary>
    public float M31 { get; }
    /// <summary>Gets the vertical translation.</summary>
    public float M32 { get; }
}

/// <summary>Controls how a vector contour is filled.</summary>
public enum MarkdownVectorFillRule
{
    /// <summary>Uses nonzero winding.</summary>
    Nonzero,
    /// <summary>Alternates filled regions at each contour crossing.</summary>
    EvenOdd,
}

/// <summary>Controls the shape at the ends of stroked segments.</summary>
public enum MarkdownVectorLineCap
{
    /// <summary>Ends at the segment endpoint.</summary>
    Flat,
    /// <summary>Uses a round cap.</summary>
    Round,
    /// <summary>Uses a square cap extending past the endpoint.</summary>
    Square,
}

/// <summary>Controls joins between stroked segments.</summary>
public enum MarkdownVectorLineJoin
{
    /// <summary>Uses a mitered join.</summary>
    Miter,
    /// <summary>Uses a round join.</summary>
    Round,
    /// <summary>Uses a beveled join.</summary>
    Bevel,
}

/// <summary>
/// Controls whether a vector paint keeps its authored color or resolves from
/// the host's semantic palette at draw time.
/// </summary>
public enum MarkdownVectorPaintRole
{
    /// <summary>
    /// Keeps a non-null authored color outside Windows High Contrast. A null
    /// color retains the legacy semantic-foreground behavior.
    /// </summary>
    Authored,
    /// <summary>Uses the host's primary text/stroke color.</summary>
    Foreground,
    /// <summary>Uses the host's contrasting diagram or control surface color.</summary>
    Surface,
    /// <summary>Uses the host's hyperlink color.</summary>
    Link,
}

/// <summary>Immutable fill and stroke values for one vector command.</summary>
public sealed class MarkdownVectorPaintStyle
{
    /// <summary>Creates a painter-neutral vector style.</summary>
    public MarkdownVectorPaintStyle(
        uint? fillArgb = null,
        uint? strokeArgb = null,
        float strokeWidth = 0,
        float opacity = 1,
        MarkdownVectorFillRule fillRule = MarkdownVectorFillRule.Nonzero,
        MarkdownVectorLineCap lineCap = MarkdownVectorLineCap.Flat,
        MarkdownVectorLineJoin lineJoin = MarkdownVectorLineJoin.Miter,
        bool nonScalingStroke = false,
        IReadOnlyList<float>? dashArray = null,
        float dashOffset = 0)
    {
        if (!float.IsFinite(strokeWidth) || strokeWidth < 0 ||
            !float.IsFinite(opacity) || opacity is < 0 or > 1 ||
            !float.IsFinite(dashOffset))
        {
            throw new ArgumentOutOfRangeException(nameof(strokeWidth), "Paint dimensions and opacity must be finite and valid.");
        }

        if (!Enum.IsDefined(fillRule) || !Enum.IsDefined(lineCap) || !Enum.IsDefined(lineJoin))
            throw new ArgumentOutOfRangeException(nameof(fillRule));

        var dashes = dashArray is null || dashArray.Count == 0 ? Array.Empty<float>() : new float[dashArray.Count];
        for (int i = 0; i < dashes.Length; i++)
        {
            float dash = dashArray![i];
            if (!float.IsFinite(dash) || dash < 0)
                throw new ArgumentOutOfRangeException(nameof(dashArray), "Dash values must be finite and non-negative.");
            dashes[i] = dash;
        }

        FillArgb = fillArgb;
        StrokeArgb = strokeArgb;
        StrokeWidth = strokeWidth;
        Opacity = opacity;
        FillRule = fillRule;
        LineCap = lineCap;
        LineJoin = lineJoin;
        NonScalingStroke = nonScalingStroke;
        DashArray = Array.AsReadOnly(dashes);
        DashOffset = dashOffset;
        FillRole = MarkdownVectorPaintRole.Authored;
        StrokeRole = MarkdownVectorPaintRole.Authored;
    }

    /// <summary>Creates a painter-neutral vector style with explicit semantic paint roles.</summary>
    public static MarkdownVectorPaintStyle CreateSemantic(
        MarkdownVectorPaintRole fillRole,
        MarkdownVectorPaintRole strokeRole,
        uint? fillArgb = null,
        uint? strokeArgb = null,
        float strokeWidth = 0,
        float opacity = 1,
        MarkdownVectorFillRule fillRule = MarkdownVectorFillRule.Nonzero,
        MarkdownVectorLineCap lineCap = MarkdownVectorLineCap.Flat,
        MarkdownVectorLineJoin lineJoin = MarkdownVectorLineJoin.Miter,
        bool nonScalingStroke = false,
        IReadOnlyList<float>? dashArray = null,
        float dashOffset = 0)
    {
        if (!Enum.IsDefined(fillRole))
            throw new ArgumentOutOfRangeException(nameof(fillRole));
        if (!Enum.IsDefined(strokeRole))
            throw new ArgumentOutOfRangeException(nameof(strokeRole));

        return new MarkdownVectorPaintStyle(
            fillArgb,
            strokeArgb,
            strokeWidth,
            opacity,
            fillRule,
            lineCap,
            lineJoin,
            nonScalingStroke,
            dashArray,
            dashOffset,
            fillRole,
            strokeRole,
            highContrastFillRole: null,
            highContrastStrokeRole: null);
    }

    /// <summary>
    /// Returns an immutable copy that keeps its authored or semantic colors in
    /// normal themes and uses the supplied semantic roles in Windows High Contrast.
    /// </summary>
    public MarkdownVectorPaintStyle WithHighContrastRoles(
        MarkdownVectorPaintRole fillRole,
        MarkdownVectorPaintRole strokeRole)
    {
        ValidateHighContrastRole(fillRole, nameof(fillRole));
        ValidateHighContrastRole(strokeRole, nameof(strokeRole));
        return new MarkdownVectorPaintStyle(
            FillArgb,
            StrokeArgb,
            StrokeWidth,
            Opacity,
            FillRule,
            LineCap,
            LineJoin,
            NonScalingStroke,
            DashArray,
            DashOffset,
            FillRole,
            StrokeRole,
            fillRole,
            strokeRole);
    }

    private MarkdownVectorPaintStyle(
        uint? fillArgb,
        uint? strokeArgb,
        float strokeWidth,
        float opacity,
        MarkdownVectorFillRule fillRule,
        MarkdownVectorLineCap lineCap,
        MarkdownVectorLineJoin lineJoin,
        bool nonScalingStroke,
        IReadOnlyList<float>? dashArray,
        float dashOffset,
        MarkdownVectorPaintRole fillRole,
        MarkdownVectorPaintRole strokeRole,
        MarkdownVectorPaintRole? highContrastFillRole,
        MarkdownVectorPaintRole? highContrastStrokeRole)
        : this(
            fillArgb,
            strokeArgb,
            strokeWidth,
            opacity,
            fillRule,
            lineCap,
            lineJoin,
            nonScalingStroke,
            dashArray,
            dashOffset)
    {
        FillRole = fillRole;
        StrokeRole = strokeRole;
        HighContrastFillRole = highContrastFillRole;
        HighContrastStrokeRole = highContrastStrokeRole;
    }

    private static void ValidateHighContrastRole(MarkdownVectorPaintRole role, string parameterName)
    {
        if (!Enum.IsDefined(role) || role == MarkdownVectorPaintRole.Authored)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "High Contrast paint must resolve through Foreground, Surface, or Link.");
        }
    }

    /// <summary>Gets the fill ARGB value, or null for semantic foreground.</summary>
    public uint? FillArgb { get; }
    /// <summary>Gets the stroke ARGB value, or null for semantic foreground.</summary>
    public uint? StrokeArgb { get; }
    /// <summary>Gets how the fill color is resolved at draw time.</summary>
    public MarkdownVectorPaintRole FillRole { get; }
    /// <summary>Gets how the stroke color is resolved at draw time.</summary>
    public MarkdownVectorPaintRole StrokeRole { get; }
    /// <summary>Gets the optional semantic fill role used only in Windows High Contrast.</summary>
    public MarkdownVectorPaintRole? HighContrastFillRole { get; }
    /// <summary>Gets the optional semantic stroke role used only in Windows High Contrast.</summary>
    public MarkdownVectorPaintRole? HighContrastStrokeRole { get; }
    /// <summary>Gets the stroke width in scene units.</summary>
    public float StrokeWidth { get; }
    /// <summary>Gets the command opacity from zero through one.</summary>
    public float Opacity { get; }
    /// <summary>Gets the contour fill rule.</summary>
    public MarkdownVectorFillRule FillRule { get; }
    /// <summary>Gets the stroke cap.</summary>
    public MarkdownVectorLineCap LineCap { get; }
    /// <summary>Gets the stroke join.</summary>
    public MarkdownVectorLineJoin LineJoin { get; }
    /// <summary>Gets whether the stroke width stays fixed under transforms.</summary>
    public bool NonScalingStroke { get; }
    /// <summary>Gets the immutable dash lengths.</summary>
    public IReadOnlyList<float> DashArray { get; }
    /// <summary>Gets the offset into the dash pattern.</summary>
    public float DashOffset { get; }
}

/// <summary>
/// Controls whether vector text keeps its authored font family or resolves
/// the family from the host's semantic style at layout time.
/// </summary>
public enum MarkdownVectorFontRole
{
    /// <summary>Uses the font family carried by the vector scene.</summary>
    Authored,
    /// <summary>Uses the font family resolved for the containing markdown style role.</summary>
    Host,
}

/// <summary>Font and positioning attributes for an already-resolved text line.</summary>
public enum MarkdownVectorTextAlignment
{
    /// <summary>The baseline origin is the leading edge.</summary>
    Start,
    /// <summary>The baseline origin is the visual center.</summary>
    Center,
    /// <summary>The baseline origin is the trailing edge.</summary>
    End,
}

/// <summary>Font and positioning attributes for an already-resolved text line.</summary>
public sealed class MarkdownVectorTextStyle
{
    /// <summary>Creates a resolved text style.</summary>
    public MarkdownVectorTextStyle(
        string fontFamily,
        float fontSize,
        ushort fontWeight = 400,
        bool italic = false,
        bool rightToLeft = false,
        MarkdownVectorTextAlignment alignment = MarkdownVectorTextAlignment.Start)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontFamily);
        if (!float.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (fontWeight is < 1 or > 999)
            throw new ArgumentOutOfRangeException(nameof(fontWeight));
        if (!Enum.IsDefined(alignment))
            throw new ArgumentOutOfRangeException(nameof(alignment));

        FontFamily = fontFamily.Trim();
        FontSize = fontSize;
        FontWeight = fontWeight;
        Italic = italic;
        RightToLeft = rightToLeft;
        Alignment = alignment;
        FontRole = MarkdownVectorFontRole.Authored;
    }

    /// <summary>Creates a text style with an explicit font-family role.</summary>
    public MarkdownVectorTextStyle(
        MarkdownVectorFontRole fontRole,
        string fontFamily,
        float fontSize,
        ushort fontWeight = 400,
        bool italic = false,
        bool rightToLeft = false,
        MarkdownVectorTextAlignment alignment = MarkdownVectorTextAlignment.Start)
        : this(fontFamily, fontSize, fontWeight, italic, rightToLeft, alignment)
    {
        if (!Enum.IsDefined(fontRole))
            throw new ArgumentOutOfRangeException(nameof(fontRole));
        FontRole = fontRole;
    }

    /// <summary>Gets the resolved font-family name.</summary>
    public string FontFamily { get; }
    /// <summary>Gets how the font family is resolved by the host.</summary>
    public MarkdownVectorFontRole FontRole { get; }
    /// <summary>Gets the resolved font size in scene units.</summary>
    public float FontSize { get; }
    /// <summary>Gets the OpenType weight from 1 through 999.</summary>
    public ushort FontWeight { get; }
    /// <summary>Gets whether the line uses italic styling.</summary>
    public bool Italic { get; }
    /// <summary>Gets whether the resolved line direction is right-to-left.</summary>
    public bool RightToLeft { get; }
    /// <summary>Gets how the line is positioned relative to its baseline origin.</summary>
    public MarkdownVectorTextAlignment Alignment { get; }
}

/// <summary>Semantic role for a child within an atomic vector scene.</summary>
public enum MarkdownVectorSemanticRole
{
    /// <summary>The root diagram.</summary>
    Diagram,
    /// <summary>A semantic grouping.</summary>
    Group,
    /// <summary>A diagram node.</summary>
    Node,
    /// <summary>A relationship or edge.</summary>
    Edge,
    /// <summary>A standalone label.</summary>
    Label,
    /// <summary>A diagram legend.</summary>
    Legend,
}

/// <summary>Behavioral flags carried by a vector semantic item.</summary>
[Flags]
public enum MarkdownVectorSemanticFlags
{
    /// <summary>No behavioral flags.</summary>
    None = 0,
    /// <summary>The item can receive logical focus.</summary>
    Focusable = 1 << 0,
    /// <summary>The item can participate in diagram selection.</summary>
    Selectable = 1 << 1,
    /// <summary>The item has a declarative link or action.</summary>
    Linked = 1 << 2,
    /// <summary>The item is excluded from accessible content.</summary>
    Decorative = 1 << 3,
}

/// <summary>
/// Defines the effective behavior of vector semantic flags. Decorative content
/// stays in the drawing scene but is excluded from every interactive and
/// accessible projection; the remaining flags are deliberately independent.
/// </summary>
internal static class MarkdownVectorSemanticPolicy
{
    internal static bool IsExposed(MarkdownVectorSemanticFlags flags) =>
        (flags & MarkdownVectorSemanticFlags.Decorative) == 0;

    internal static bool IsKeyboardFocusable(MarkdownVectorSemanticFlags flags) =>
        IsExposed(flags) && (flags & MarkdownVectorSemanticFlags.Focusable) != 0;

    internal static bool IsSelectable(MarkdownVectorSemanticFlags flags) =>
        IsExposed(flags) && (flags & MarkdownVectorSemanticFlags.Selectable) != 0;

    internal static bool IsLinked(MarkdownVectorSemanticFlags flags) =>
        IsExposed(flags) && (flags & MarkdownVectorSemanticFlags.Linked) != 0;

    internal static bool IsInvokable(
        MarkdownVectorSemanticFlags flags,
        bool hasAction) => IsLinked(flags) && hasAction;
}

/// <summary>Immutable, stable semantic identity and bounds within a vector scene.</summary>
public sealed class MarkdownVectorSemanticItem
{
    /// <summary>Creates an immutable vector semantic item.</summary>
    public MarkdownVectorSemanticItem(
        uint sourceId,
        MarkdownVectorSemanticRole role,
        string? name,
        string? description,
        int parentIndex,
        SourceSpan sourceSpan,
        MarkdownVectorRectangle bounds,
        MarkdownVectorSemanticFlags flags = MarkdownVectorSemanticFlags.None)
    {
        if (sourceId == 0)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        if (parentIndex < -1)
            throw new ArgumentOutOfRangeException(nameof(parentIndex));
        if ((flags & ~(MarkdownVectorSemanticFlags.Focusable |
                       MarkdownVectorSemanticFlags.Selectable |
                       MarkdownVectorSemanticFlags.Linked |
                       MarkdownVectorSemanticFlags.Decorative)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }
        MarkdownSyntaxNode.ValidateSourceSpan(sourceSpan);
        SourceId = sourceId;
        Role = role;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        ParentIndex = parentIndex;
        SourceSpan = sourceSpan;
        Bounds = bounds;
        Flags = flags;
    }

    /// <summary>Gets the stable source-derived identity.</summary>
    public uint SourceId { get; }
    /// <summary>Gets the semantic role.</summary>
    public MarkdownVectorSemanticRole Role { get; }
    /// <summary>Gets the accessible name.</summary>
    public string? Name { get; }
    /// <summary>Gets the accessible description.</summary>
    public string? Description { get; }
    /// <summary>Gets the parent semantic index, or -1 for a root.</summary>
    public int ParentIndex { get; }
    /// <summary>Gets the half-open UTF-16 markdown source range.</summary>
    public SourceSpan SourceSpan { get; }
    /// <summary>Gets bounds in scene coordinates.</summary>
    public MarkdownVectorRectangle Bounds { get; }
    /// <summary>Gets behavioral flags.</summary>
    public MarkdownVectorSemanticFlags Flags { get; }
}

/// <summary>A declarative host-approved link or action associated with scene semantics.</summary>
public sealed class MarkdownVectorLinkAction
{
    /// <summary>Creates a declarative vector link/action.</summary>
    public MarkdownVectorLinkAction(int semanticIndex, string? target, string? action, bool external = false)
    {
        if (semanticIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(semanticIndex));
        if (string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(action))
            throw new ArgumentException("A vector link requires a target or action.");
        SemanticIndex = semanticIndex;
        Target = string.IsNullOrWhiteSpace(target) ? null : target.Trim();
        Action = string.IsNullOrWhiteSpace(action) ? null : action.Trim();
        External = external;
    }

    /// <summary>Gets the associated semantic index.</summary>
    public int SemanticIndex { get; }
    /// <summary>Gets the host-approved URI target.</summary>
    public string? Target { get; }
    /// <summary>Gets the host-defined declarative action name.</summary>
    public string? Action { get; }
    /// <summary>Gets whether the native syntax requested external disposition.</summary>
    public bool External { get; }
}

/// <summary>An immutable rectangle in device-independent scene coordinates.</summary>
public readonly record struct MarkdownVectorRectangle
{
    /// <summary>Creates a rectangle.</summary>
    public MarkdownVectorRectangle(float x, float y, float width, float height)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) ||
            !float.IsFinite(width) || !float.IsFinite(height) ||
            width < 0 || height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Vector rectangles must be finite and non-negative.");
        }

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

/// <summary>
/// Immutable, thread-safe vector output emitted by a feature pack. It contains
/// no UI, graphics-device, or native-lifetime object.
/// </summary>
public sealed class MarkdownVectorScene
{
    private readonly MarkdownVectorLinkAction?[] _linkBySemanticIndex;
    private readonly int[] _semanticDepths;

    /// <summary>Creates an immutable vector scene.</summary>
    public MarkdownVectorScene(
        float width,
        float height,
        float baseline,
        IReadOnlyList<MarkdownVectorCommand> commands,
        float referenceFontSize = 0)
        : this(
            width,
            height,
            baseline,
            commands,
            referenceFontSize,
            new MarkdownVectorRectangle(0, 0, width, height),
            Array.Empty<MarkdownVectorSemanticItem>(),
            Array.Empty<MarkdownVectorLinkAction>())
    {
    }

    /// <summary>Creates a rich immutable vector scene with semantics and link actions.</summary>
    public MarkdownVectorScene(
        float width,
        float height,
        float baseline,
        IReadOnlyList<MarkdownVectorCommand> commands,
        float referenceFontSize,
        MarkdownVectorRectangle viewport,
        IReadOnlyList<MarkdownVectorSemanticItem> semantics,
        IReadOnlyList<MarkdownVectorLinkAction> links)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) ||
            !float.IsFinite(baseline) || !float.IsFinite(referenceFontSize) ||
            width < 0 || height < 0 || baseline < 0 || baseline > height || referenceFontSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Scene dimensions must be finite and non-negative.");
        }

        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(semantics);
        ArgumentNullException.ThrowIfNull(links);
        var copy = new MarkdownVectorCommand[commands.Count];
        var scopeStack = new MarkdownVectorCommandKind[commands.Count];
        int scopeDepth = 0;
        for (int i = 0; i < copy.Length; i++)
        {
            MarkdownVectorCommand command = commands[i] ??
                throw new ArgumentException("Scene commands cannot contain null.", nameof(commands));
            switch (command.Kind)
            {
                case MarkdownVectorCommandKind.BeginGroup:
                case MarkdownVectorCommandKind.BeginClip:
                    scopeStack[scopeDepth++] = command.Kind;
                    break;
                case MarkdownVectorCommandKind.EndGroup:
                    if (scopeDepth == 0 || scopeStack[--scopeDepth] != MarkdownVectorCommandKind.BeginGroup)
                        throw new ArgumentException("Vector groups must be properly nested.", nameof(commands));
                    break;
                case MarkdownVectorCommandKind.EndClip:
                    if (scopeDepth == 0 || scopeStack[--scopeDepth] != MarkdownVectorCommandKind.BeginClip)
                        throw new ArgumentException("Vector clips must be properly nested.", nameof(commands));
                    break;
            }
            copy[i] = command;
        }
        if (scopeDepth != 0)
            throw new ArgumentException("Vector groups and clips must be closed.", nameof(commands));

        var semanticCopy = new MarkdownVectorSemanticItem[semantics.Count];
        var sourceIds = new HashSet<uint>();
        for (int i = 0; i < semanticCopy.Length; i++)
        {
            MarkdownVectorSemanticItem item = semantics[i] ?? throw new ArgumentException("Scene semantics cannot contain null.", nameof(semantics));
            if (!sourceIds.Add(item.SourceId) || item.ParentIndex >= semanticCopy.Length || item.ParentIndex == i)
                throw new ArgumentException("Scene semantic identities and parent references must be valid.", nameof(semantics));
            semanticCopy[i] = item;
        }
        int[] semanticDepths = ValidateSemanticHierarchy(semanticCopy);

        for (int i = 0; i < copy.Length; i++)
        {
            if (copy[i].SemanticIndex < -1 || copy[i].SemanticIndex >= semanticCopy.Length)
                throw new ArgumentException("Scene commands must reference scene semantics.", nameof(commands));
        }

        var linkCopy = new MarkdownVectorLinkAction[links.Count];
        var linkBySemanticIndex = new MarkdownVectorLinkAction?[semanticCopy.Length];
        for (int i = 0; i < linkCopy.Length; i++)
        {
            MarkdownVectorLinkAction link = links[i] ?? throw new ArgumentException("Scene links cannot contain null.", nameof(links));
            if (link.SemanticIndex >= semanticCopy.Length)
                throw new ArgumentException("Scene links must reference scene semantics.", nameof(links));
            if ((semanticCopy[link.SemanticIndex].Flags & MarkdownVectorSemanticFlags.Linked) == 0)
                throw new ArgumentException("Scene link actions require the referenced semantic to carry the Linked flag.", nameof(links));
            if (linkBySemanticIndex[link.SemanticIndex] is not null)
                throw new ArgumentException("A scene semantic can declare at most one link action.", nameof(links));
            linkCopy[i] = link;
            linkBySemanticIndex[link.SemanticIndex] = link;
        }

        for (int i = 0; i < semanticCopy.Length; i++)
        {
            if ((semanticCopy[i].Flags & MarkdownVectorSemanticFlags.Linked) != 0 &&
                linkBySemanticIndex[i] is null)
            {
                throw new ArgumentException("Every semantic carrying the Linked flag requires one link action.", nameof(links));
            }
        }

        Width = width;
        Height = height;
        Baseline = baseline;
        ReferenceFontSize = referenceFontSize;
        Commands = new ReadOnlyCollection<MarkdownVectorCommand>(copy);
        Viewport = viewport;
        Semantics = new ReadOnlyCollection<MarkdownVectorSemanticItem>(semanticCopy);
        Links = new ReadOnlyCollection<MarkdownVectorLinkAction>(linkCopy);
        _linkBySemanticIndex = linkBySemanticIndex;
        _semanticDepths = semanticDepths;
    }

    /// <summary>Gets the scene width in device-independent units.</summary>
    public float Width { get; }
    /// <summary>Gets the scene height in device-independent units.</summary>
    public float Height { get; }
    /// <summary>Gets the baseline measured from the top.</summary>
    public float Baseline { get; }
    /// <summary>
    /// Gets the scene font size that maps to the containing markdown style, or
    /// zero to let the block/inline renderer apply its documented fallback.
    /// </summary>
    public float ReferenceFontSize { get; }
    /// <summary>Gets immutable commands in painter order.</summary>
    public IReadOnlyList<MarkdownVectorCommand> Commands { get; }
    /// <summary>Gets the source-space viewport mapped into the scene bounds.</summary>
    public MarkdownVectorRectangle Viewport { get; }
    /// <summary>Gets stable semantic children used for hit testing and UI Automation.</summary>
    public IReadOnlyList<MarkdownVectorSemanticItem> Semantics { get; }
    /// <summary>Gets declarative link/action records associated with semantic children.</summary>
    public IReadOnlyList<MarkdownVectorLinkAction> Links { get; }

    internal MarkdownVectorLinkAction? GetLinkAction(int semanticIndex) =>
        (uint)semanticIndex < (uint)_linkBySemanticIndex.Length
            ? _linkBySemanticIndex[semanticIndex]
            : null;

    internal int GetSemanticDepth(int semanticIndex) =>
        (uint)semanticIndex < (uint)_semanticDepths.Length
            ? _semanticDepths[semanticIndex]
            : -1;

    private static int[] ValidateSemanticHierarchy(
        IReadOnlyList<MarkdownVectorSemanticItem> semantics)
    {
        // 0 = unseen, 1 = in the current parent chain, 2 = completely checked.
        var states = new byte[semantics.Count];
        var path = new int[semantics.Count];
        var semanticDepths = new int[semantics.Count];
        for (int start = 0; start < semantics.Count; start++)
        {
            if (states[start] == 2)
                continue;

            int depth = 0;
            int current = start;
            while (current >= 0 && states[current] == 0)
            {
                states[current] = 1;
                path[depth++] = current;
                current = semantics[current].ParentIndex;
            }
            if (current >= 0 && states[current] == 1)
                throw new ArgumentException("Scene semantic parents cannot contain cycles.", nameof(semantics));
            int resolvedDepth = current >= 0 ? semanticDepths[current] + 1 : 0;
            while (depth > 0)
            {
                int semanticIndex = path[--depth];
                states[semanticIndex] = 2;
                semanticDepths[semanticIndex] = resolvedDepth++;
            }
        }

        return semanticDepths;
    }
}
