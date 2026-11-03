using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Extensions;

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
