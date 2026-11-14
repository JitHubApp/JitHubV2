using System;
using System.Numerics;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;

namespace MarkdownRenderer.Layout.Internal.Inlines;

/// <summary>
/// Supplies DirectWrite with the exact advance and line height of an
/// atomic Markdown run. Painting remains owned by the renderer so the
/// callback is intentionally empty. Using a real inline object avoids the
/// font-dependent U+FFFC glyph advance that previously made tall images
/// consume hundreds of extra horizontal DIPs and wrap unnecessarily.
/// </summary>
/// <remarks>
/// Declared <c>partial</c> because CsWinRT generates the AOT interop
/// vtable for classes that implement the WinRT <c>ICanvasTextInlineObject</c>
/// interface.
/// </remarks>
internal sealed partial class InlineLayoutObject : ICanvasTextInlineObject
{
    public InlineLayoutObject(
        float width,
        float height,
        float inverseMetricScale = 1f,
        bool preserveSubUnitDimensions = false,
        bool allowZeroDimensions = false,
        float baselineXHeightDip = -1f)
    {
        float minimumDimension = allowZeroDimensions
            ? 0f
            : preserveSubUnitDimensions ? float.Epsilon : 1f;
        float safeWidth = Math.Max(minimumDimension, width);
        float scale = float.IsFinite(inverseMetricScale) && inverseMetricScale > 0f
            ? inverseMetricScale
            : 1f;
        float safeHeight = Math.Max(minimumDimension, height / scale);
        Size = new Size(safeWidth, safeHeight);
        DrawBounds = new Rect(0, 0, safeWidth, safeHeight);
        Baseline = !allowZeroDimensions &&
            float.IsFinite(baselineXHeightDip) && baselineXHeightDip >= 0f
                ? (safeHeight + baselineXHeightDip / scale) / 2f
                : safeHeight;
    }

    public Size Size { get; }
    public float Baseline { get; }
    public bool SupportsSideways => false;
    public Rect DrawBounds { get; }
    public CanvasLineBreakCondition BreakBefore => CanvasLineBreakCondition.Neutral;
    public CanvasLineBreakCondition BreakAfter => CanvasLineBreakCondition.Neutral;

    public void Draw(
        ICanvasTextRenderer textRenderer,
        Vector2 point,
        bool isSideways,
        bool isRightToLeft,
        object brush)
    {
    }
}
