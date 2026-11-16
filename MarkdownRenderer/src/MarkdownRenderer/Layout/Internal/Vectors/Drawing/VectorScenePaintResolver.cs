using System;
using Windows.UI;
using MarkdownRenderer.Extensions.Vectors;
using MarkdownRenderer.Layout.Internal.Text;

namespace MarkdownRenderer.Layout.Internal.Vectors.Drawing;

/// <summary>
/// Resolves vector-scene paint: theme-palette and high-contrast colors,
/// semantic fill roles, font families and bounded text-layout sizes.
/// </summary>
internal static class VectorScenePaintResolver
{
    // DirectWrite rejects extremely large requested layout dimensions even
    // when they are finite.  float.MaxValue also makes its internal glyph
    // arithmetic overflow for sufficiently long labels.  Vector scenes are
    // already bounded, so use a generous finite canvas instead of pretending
    // that the text has an infinite layout surface.
    internal const float MaximumTextLayoutDimension = 1_000_000f;

    internal static Color ResolveLegacyColor(
        uint? argb,
        VectorScenePaintPalette palette,
        bool highContrast,
        Color? foregroundOverride)
    {
        TryResolveColor(
            argb,
            1f,
            palette,
            highContrast ? MarkdownVectorPaintRole.Foreground : MarkdownVectorPaintRole.Authored,
            highContrast,
            foregroundOverride,
            out Color color);
        return color;
    }

    internal static Color ResolveColor(
        uint? argb,
        float opacity,
        VectorScenePaintPalette palette,
        MarkdownVectorPaintRole role,
        bool highContrast)
    {
        TryResolveColor(
            argb,
            opacity,
            palette,
            role,
            highContrast,
            foregroundOverride: null,
            out Color color);
        return color;
    }

    internal static string ResolveFontFamily(
        MarkdownVectorTextStyle style,
        string? hostFontFamily)
    {
        ArgumentNullException.ThrowIfNull(style);
        string selected = style.FontRole == MarkdownVectorFontRole.Host &&
               !string.IsNullOrWhiteSpace(hostFontFamily)
            ? hostFontFamily.Trim()
            : style.FontFamily;
        return CanvasFontFamilyReference.ForCanvas(selected);
    }

    internal static (float Width, float Height) ResolveTextLayoutSize(
        MarkdownVectorScene scene,
        string text,
        MarkdownVectorTextStyle style)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(style);

        // NoWrap keeps the authored line intact.  The estimate merely gives
        // DirectWrite enough finite measuring space; it does not scale or
        // otherwise alter the scene.  Use double arithmetic so adversarially
        // long labels cannot overflow before the clamp is applied.
        double estimatedTextWidth = checked((double)text.Length) * style.FontSize * 2d + 64d;
        double width = Math.Max(
            Math.Max(scene.Width, scene.Viewport.Width),
            estimatedTextWidth);
        double height = Math.Max(
            Math.Max(scene.Height, scene.Viewport.Height),
            style.FontSize * 4d + 64d);
        return (
            (float)Math.Clamp(width, 1d, MaximumTextLayoutDimension),
            (float)Math.Clamp(height, 1d, MaximumTextLayoutDimension));
    }

    internal static bool TryResolveColor(
        uint? argb,
        float opacity,
        VectorScenePaintPalette palette,
        MarkdownVectorPaintRole role,
        bool highContrast,
        Color? foregroundOverride,
        out Color color)
    {
        byte authoredAlpha = argb is { } authored ? (byte)(authored >> 24) : (byte)0xFF;
        if (argb is not null && authoredAlpha == 0)
        {
            color = default;
            return false;
        }

        bool semanticMapping = argb is null || role != MarkdownVectorPaintRole.Authored;
        if (foregroundOverride is { } overrideColor)
        {
            color = Color.FromArgb(
                semanticMapping || argb is null ? overrideColor.A : authoredAlpha,
                overrideColor.R,
                overrideColor.G,
                overrideColor.B);
        }
        else if (semanticMapping)
        {
            color = palette.Resolve(role);
        }
        else
        {
            uint value = argb.GetValueOrDefault();
            byte alpha = (byte)(value >> 24);

            if (highContrast)
            {
                Color mapped = palette.Resolve(MarkdownVectorPaintRole.Foreground);
                // System High Contrast colors are semantic, opaque pairs. Retaining
                // authored alpha here can collapse thin strokes/text back into the
                // surface and violates the platform HC brush contract.
                color = Color.FromArgb(highContrast ? (byte)0xFF : alpha, mapped.R, mapped.G, mapped.B);
            }
            else
            {
                color = Color.FromArgb(alpha, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
            }
        }

        byte resolvedAlpha = highContrast && color.A > 0
            ? (byte)0xFF
            : (byte)Math.Clamp(
                (int)Math.Round(color.A * opacity * (semanticMapping ? authoredAlpha / 255f : 1f)),
                0,
                255);
        color = Color.FromArgb(resolvedAlpha, color.R, color.G, color.B);
        return color.A > 0;
    }

    internal static MarkdownVectorPaintRole InferFillRole(
        MarkdownVectorCommand command,
        MarkdownVectorPaintStyle style,
        MarkdownVectorSemanticRole? semanticRole = null)
    {
        // Rectangle/ellipse fills are Mermaid surfaces even when they do not
        // carry a border (the full-scene background is a common example). A
        // filled path is treated as a surface only when its stroke identifies
        // it as a closed node; fill-only paths are commonly arrowheads or other
        // authored accents and should retain their light/dark palette.
        return ((command.Kind is MarkdownVectorCommandKind.DrawRectangle or
                    MarkdownVectorCommandKind.DrawEllipse) ||
                (command.Kind == MarkdownVectorCommandKind.DrawPath &&
                 (style.StrokeWidth > 0 ||
                  semanticRole is MarkdownVectorSemanticRole.Diagram or
                      MarkdownVectorSemanticRole.Group or
                      MarkdownVectorSemanticRole.Node or
                      MarkdownVectorSemanticRole.Legend)))
            ? MarkdownVectorPaintRole.Surface
            : MarkdownVectorPaintRole.Foreground;
    }

    internal static MarkdownVectorSemanticRole? GetSemanticRole(
        MarkdownVectorScene scene,
        MarkdownVectorCommand command) =>
        command.SemanticIndex >= 0 && command.SemanticIndex < scene.Semantics.Count
            ? scene.Semantics[command.SemanticIndex].Role
            : null;

    internal static MarkdownVectorPaintRole ResolvePaintRole(
        MarkdownVectorCommand command,
        MarkdownVectorPaintRole declaredRole,
        uint? argb,
        MarkdownVectorPaintRole highContrastFallback,
        bool highContrast,
        bool linkedSemantic)
    {
        ArgumentNullException.ThrowIfNull(command);
        MarkdownVectorPaintRole role = declaredRole;
        if (role == MarkdownVectorPaintRole.Authored)
        {
            if (highContrast)
                role = highContrastFallback;
            else if (argb is null)
                role = MarkdownVectorPaintRole.Foreground;
            else
                return MarkdownVectorPaintRole.Authored;
        }

        return role == MarkdownVectorPaintRole.Foreground && linkedSemantic
            ? MarkdownVectorPaintRole.Link
            : role;
    }
}
