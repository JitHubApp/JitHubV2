using System;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Atomic inline vector content. Device-independent commands come from Core;
/// this run alone owns the disposable Win2D geometry built for one layout.
/// </summary>
internal sealed class InlineVectorSceneRun : InlineRun, IDisposable
{
    private readonly MarkdownVectorScene _scene;
    private readonly VectorSceneDrawing _drawing;
    private readonly VectorScenePaintPalette _paintPalette;
    private Rect _localBounds;

    internal InlineVectorSceneRun(
        MarkdownLayoutContext context,
        MarkdownVectorScene scene,
        string semanticText,
        string accessibilityName,
        string? accessibilityDescription,
        string elementKey)
    {
        ArgumentNullException.ThrowIfNull(context);
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        SemanticText = semanticText ?? string.Empty;
        AccessibilityName = accessibilityName ?? string.Empty;
        AccessibilityDescription = accessibilityDescription;
        RenderedLength = 1;
        DesiredWidth = Math.Max(1f, scene.Width);
        DesiredHeight = Math.Max(1f, scene.Height);
        DesiredBaseline = Math.Clamp(scene.Baseline, 0f, DesiredHeight);
        string resolvedElementKey = string.IsNullOrWhiteSpace(elementKey)
            ? MarkdownElementKeys.Math
            : elementKey;
        var styleContextKeys = context.CreateStyleContextSnapshot();
        var styleAliasKeys = context.CreateStyleAliasSnapshot();
        ElementStyle vectorStyle = context.ThemeSnapshot.GetStyle(
            resolvedElementKey,
            styleContextKeys,
            styleAliasKeys);
        _paintPalette = new VectorScenePaintPalette(
            vectorStyle.Foreground,
            vectorStyle.Background ?? context.ThemeSnapshot.SurfaceColor,
            context.ThemeSnapshot.GetStyle(
                MarkdownElementKeys.Link,
                styleContextKeys,
                styleAliasKeys).Foreground);
        _drawing = new VectorSceneDrawing(
            context.ResourceCreator,
            scene,
            context.CancellationToken,
            vectorStyle.FontFamily);
    }

    public override string Text => InlineEmbedRun.PlaceholderChar;

    public override string AccessibleText => SemanticText;

    internal string SemanticText { get; }

    internal string AccessibilityName { get; }

    internal string? AccessibilityDescription { get; }

    internal float DesiredWidth { get; private set; }

    internal float DesiredHeight { get; private set; }

    internal float DesiredBaseline { get; private set; }

    internal Rect LocalBounds => _localBounds;

    internal void Measure(float fontSize)
    {
        float scale = _scene.ReferenceFontSize > 0
            ? Math.Max(0.01f, fontSize / _scene.ReferenceFontSize)
            : 1f;
        DesiredWidth = Math.Max(1f, _scene.Width * scale);
        DesiredHeight = Math.Max(1f, _scene.Height * scale);
        DesiredBaseline = Math.Clamp(_scene.Baseline * scale, 0f, DesiredHeight);
    }

    internal void SetLocalBounds(Rect bounds) => _localBounds = bounds;

    internal void Paint(
        CanvasDrawingSession drawingSession,
        float baseX,
        float baseY,
        Color semanticForeground,
        bool highContrast,
        Color? foregroundOverride = null)
    {
        _drawing.Paint(
            drawingSession,
            new Rect(
                baseX + _localBounds.X,
                baseY + _localBounds.Y,
                _localBounds.Width,
                _localBounds.Height),
            _paintPalette with { Foreground = semanticForeground },
            highContrast,
            foregroundOverride);
    }

    public void Dispose()
    {
        _drawing.Dispose();
    }
}
