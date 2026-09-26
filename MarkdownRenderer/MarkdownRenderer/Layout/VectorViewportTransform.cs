using System;
using System.Numerics;
using MarkdownRenderer.Extensions;
using Windows.Foundation;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Maps a vector scene's source viewport into its destination using the same
/// centered uniform "meet" transform for painting, hit testing, and UIA.
/// </summary>
internal readonly record struct VectorViewportTransform(
    float Scale,
    float TranslationX,
    float TranslationY)
{
    internal Matrix3x2 Matrix => new(
        Scale,
        0,
        0,
        Scale,
        TranslationX,
        TranslationY);

    internal static VectorViewportTransform Create(
        MarkdownVectorRectangle viewport,
        Rect destination)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 ||
            destination.Width <= 0 || destination.Height <= 0)
        {
            return new VectorViewportTransform(
                1,
                (float)destination.X - viewport.X,
                (float)destination.Y - viewport.Y);
        }

        float scaleX = (float)(destination.Width / viewport.Width);
        float scaleY = (float)(destination.Height / viewport.Height);
        float scale = Math.Min(scaleX, scaleY);
        float insetX = Math.Max(0, ((float)destination.Width - viewport.Width * scale) / 2f);
        float insetY = Math.Max(0, ((float)destination.Height - viewport.Height * scale) / 2f);
        return new VectorViewportTransform(
            scale,
            (float)destination.X + insetX - viewport.X * scale,
            (float)destination.Y + insetY - viewport.Y * scale);
    }

    internal Rect Transform(MarkdownVectorRectangle rectangle) => new(
        rectangle.X * Scale + TranslationX,
        rectangle.Y * Scale + TranslationY,
        rectangle.Width * Scale,
        rectangle.Height * Scale);

    internal static Rect Intersect(Rect rectangle, Rect clip)
    {
        double left = Math.Max(rectangle.Left, clip.Left);
        double top = Math.Max(rectangle.Top, clip.Top);
        double right = Math.Min(rectangle.Right, clip.Right);
        double bottom = Math.Min(rectangle.Bottom, clip.Bottom);
        return right > left && bottom > top
            ? new Rect(left, top, right - left, bottom - top)
            : Rect.Empty;
    }
}
