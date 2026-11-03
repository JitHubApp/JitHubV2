using MarkdownRenderer.Layout;
using Windows.Foundation;

namespace MarkdownRenderer.Performance;

/// <summary>Finite-geometry checks shared by vector-scene residency band walks.</summary>
internal static class VectorSceneBandGeometry
{
    internal static bool Intersects(Rect left, Rect right) =>
        left.Right > right.Left && left.Left < right.Right &&
        left.Bottom > right.Top && left.Top < right.Bottom;

    internal static bool IsFiniteNonNegativeRect(Rect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
        double.IsFinite(rect.Right) && double.IsFinite(rect.Bottom) &&
        rect.Width >= 0 && rect.Height >= 0;

    internal static bool IsValidBand(LazyLayoutBand band) =>
        double.IsFinite(band.Top) && double.IsFinite(band.Bottom) && band.Bottom >= band.Top;
}
