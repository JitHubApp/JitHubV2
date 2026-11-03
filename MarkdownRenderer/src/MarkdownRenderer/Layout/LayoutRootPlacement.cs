using Windows.Foundation;

namespace MarkdownRenderer.Layout;

internal readonly struct LayoutRootPlacement(
    float baseX,
    float baseTop,
    float x,
    float top,
    float width,
    float height,
    bool measured)
{
    internal readonly float BaseX = baseX;
    internal readonly float BaseTop = baseTop;
    internal readonly float X = x;
    internal readonly float Top = top;
    internal readonly float Width = width;
    internal readonly float Height = height;
    internal readonly bool Measured = measured;
    internal float TranslationX => X - BaseX;
    internal float TranslationY => Top - BaseTop;
    // LayoutSnapshot advances the document flow in single precision. Keep the
    // index edge identical to a fresh Arrange instead of adding the two float
    // operands after widening them to double.
    internal double Bottom => (double)(Top + Height);
    internal Rect LogicalBounds => new(X, Top, Width, Height);
}
