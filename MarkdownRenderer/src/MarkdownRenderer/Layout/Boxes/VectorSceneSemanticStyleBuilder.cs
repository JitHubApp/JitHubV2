using System;
using Windows.UI;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Builds the frozen per-semantic text styles of a vector scene box: the
/// draw-time typography and the resolved foreground and inherited surface
/// background of each text semantic.
/// </summary>
internal sealed class VectorSceneSemanticStyleBuilder
{
    private readonly VectorSceneBox _host;

    internal VectorSceneSemanticStyleBuilder(VectorSceneBox host) => _host = host;

    internal ElementStyle[] BuildSemanticTextStyles(
        MarkdownVectorScene scene,
        int[] textCommandIndices,
        int[] backgroundCommandIndices)
    {
        int count = _host.SemanticItems.Count;
        if (count == 0)
            return Array.Empty<ElementStyle>();

        ElementStyle containerStyle = _host.Style;
        VectorScenePaintPalette palette = _host.CreatePaintPalette();
        var styles = new ElementStyle[count];
        Color?[]? backgrounds = null;
        byte[]? backgroundStates = null;
        int[]? path = null;
        float sceneUnitScale = _host.ResolveSceneUnitScale(containerStyle);
        for (int semanticIndex = 0; semanticIndex < count; semanticIndex++)
        {
            _host.ThrowIfCancellationRequested();
            int textCommandIndex = textCommandIndices[semanticIndex];
            if (textCommandIndex < 0)
            {
                styles[semanticIndex] = containerStyle;
                continue;
            }

            backgrounds ??= new Color?[count];
            backgroundStates ??= new byte[count];
            path ??= new int[count];
            Color? background = ResolveSemanticBackgroundColor(
                scene,
                semanticIndex,
                backgroundCommandIndices,
                backgrounds,
                backgroundStates,
                path,
                containerStyle.Background,
                palette);
            styles[semanticIndex] = BuildSemanticTextStyle(
                scene,
                semanticIndex,
                textCommandIndex,
                containerStyle,
                background,
                sceneUnitScale,
                palette);
        }

        return styles;
    }

    private ElementStyle BuildSemanticTextStyle(
        MarkdownVectorScene scene,
        int semanticIndex,
        int commandIndex,
        ElementStyle containerStyle,
        Color? effectiveBackground,
        float sceneUnitScale,
        VectorScenePaintPalette palette)
    {
        MarkdownVectorCommand command = scene.Commands[commandIndex];
        MarkdownVectorPaintStyle paintStyle = command.Style!;
        MarkdownVectorTextStyle textStyle = command.TextStyle!;
        uint? textArgb = paintStyle.FillArgb ?? paintStyle.StrokeArgb;
        MarkdownVectorPaintRole declaredRole =
            paintStyle.FillArgb is not null || paintStyle.StrokeArgb is null
                ? paintStyle.FillRole
                : paintStyle.StrokeRole;
        MarkdownVectorPaintRole highContrastRole =
            paintStyle.FillArgb is not null || paintStyle.StrokeArgb is null
                ? paintStyle.HighContrastFillRole ?? MarkdownVectorPaintRole.Foreground
                : paintStyle.HighContrastStrokeRole ?? MarkdownVectorPaintRole.Foreground;
        bool linkedSemantic = _host.SemanticLinks[semanticIndex] is not null;
        MarkdownVectorPaintRole effectiveRole = VectorSceneDrawing.ResolvePaintRole(
            command,
            declaredRole,
            textArgb,
            highContrastRole,
            _host.LayoutContext.ThemeSnapshot.IsHighContrast,
            linkedSemantic);
        Color effectiveForeground = VectorSceneDrawing.ResolveColor(
            textArgb,
            paintStyle.Opacity,
            palette,
            effectiveRole,
            _host.LayoutContext.ThemeSnapshot.IsHighContrast);

        return new ElementStyle
        {
            FontFamily = VectorSceneDrawing.ResolveFontFamily(textStyle, containerStyle.FontFamily),
            FontSize = Math.Max(1f, textStyle.FontSize * sceneUnitScale),
            FontWeight = new Windows.UI.Text.FontWeight { Weight = textStyle.FontWeight },
            FontStyle = textStyle.Italic
                ? Windows.UI.Text.FontStyle.Italic
                : Windows.UI.Text.FontStyle.Normal,
            Foreground = effectiveForeground,
            HoverForeground = containerStyle.HoverForeground,
            FocusForeground = containerStyle.FocusForeground,
            Background = effectiveBackground,
            AccentBar = containerStyle.AccentBar,
            BorderBrush = containerStyle.BorderBrush,
            BorderThickness = containerStyle.BorderThickness,
            CornerRadius = containerStyle.CornerRadius,
            ListIndent = containerStyle.ListIndent,
            NestedListIndent = containerStyle.NestedListIndent,
            Underline = containerStyle.Underline,
            Strikethrough = containerStyle.Strikethrough,
            Margin = containerStyle.Margin,
            Padding = containerStyle.Padding,
            LineHeightMultiplier = containerStyle.LineHeightMultiplier,
        };
    }

    private Color? ResolveSemanticBackgroundColor(
        MarkdownVectorScene scene,
        int semanticIndex,
        int[] backgroundCommandIndices,
        Color?[] resolvedBackgrounds,
        byte[] resolvedStates,
        int[] path,
        Color? fallback,
        VectorScenePaintPalette palette)
    {
        int current = semanticIndex;
        int pathCount = 0;
        Color? background = fallback;
        while ((uint)current < (uint)_host.SemanticItems.Count)
        {
            if (resolvedStates[current] != 0)
            {
                background = resolvedBackgrounds[current];
                break;
            }

            int commandIndex = backgroundCommandIndices[current];
            if (commandIndex == VectorSceneSemanticCommandIndex.AmbiguousBackgroundCommandIndex)
            {
                resolvedBackgrounds[current] = fallback;
                resolvedStates[current] = 1;
                background = fallback;
                break;
            }
            if (commandIndex >= 0)
            {
                MarkdownVectorCommand command = scene.Commands[commandIndex];
                MarkdownVectorPaintStyle style = command.Style!;
                MarkdownVectorPaintRole role = VectorSceneDrawing.ResolvePaintRole(
                    command,
                    style.FillRole,
                    style.FillArgb,
                    style.HighContrastFillRole ?? VectorSceneDrawing.InferFillRole(
                        command,
                        style,
                        _host.SemanticItems[current].Role),
                    _host.LayoutContext.ThemeSnapshot.IsHighContrast,
                    _host.SemanticLinks[current] is not null);
                background = VectorSceneDrawing.ResolveColor(
                    style.FillArgb,
                    style.Opacity,
                    palette,
                    role,
                    _host.LayoutContext.ThemeSnapshot.IsHighContrast);
                resolvedBackgrounds[current] = background;
                resolvedStates[current] = 1;
                break;
            }

            path[pathCount++] = current;
            if ((pathCount & 0x3F) == 0)
                _host.ThrowIfCancellationRequested();
            current = _host.SemanticItems[current].ParentIndex;
        }

        while (pathCount > 0)
        {
            int pathIndex = path[--pathCount];
            resolvedBackgrounds[pathIndex] = background;
            resolvedStates[pathIndex] = 1;
        }

        return background;
    }
}
