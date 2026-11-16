using System.Drawing;

namespace MarkdownRenderer.Sample.Automation.Uia;

/// <summary>
/// Classifies the interior pixels of a High Contrast vector-text capture into
/// black-like surface pixels and semantic glyph pixels (Hotlight cyan or
/// WindowText white), so a probe can prove that glyph paint, not only a focus
/// border or a solid rectangle, reached the screen.
/// </summary>
internal sealed class HighContrastVectorPixels
{
    private long _surfaceRed;
    private long _surfaceGreen;
    private long _surfaceBlue;
    private long _semanticRed;
    private long _semanticGreen;
    private long _semanticBlue;

    private HighContrastVectorPixels(int inset, int width, int height)
    {
        Inset = inset;
        Width = width;
        Height = height;
    }

    /// <summary>The band skipped at each edge to ignore a focus border.</summary>
    internal int Inset { get; }

    internal int Width { get; }

    internal int Height { get; }

    internal int PixelCount => Width * Height;

    internal int SurfacePixels { get; private set; }

    internal int SemanticPixels { get; private set; }

    /// <summary>The mean surface color. Read it only when <see cref="SurfacePixels"/> is positive.</summary>
    internal Color AverageSurface => Color.FromArgb(
        (int)(_surfaceRed / SurfacePixels),
        (int)(_surfaceGreen / SurfacePixels),
        (int)(_surfaceBlue / SurfacePixels));

    /// <summary>The mean glyph color. Read it only when <see cref="SemanticPixels"/> is positive.</summary>
    internal Color AverageSemantic => Color.FromArgb(
        (int)(_semanticRed / SemanticPixels),
        (int)(_semanticGreen / SemanticPixels),
        (int)(_semanticBlue / SemanticPixels));

    /// <summary>
    /// Measures <paramref name="bitmap"/> inside an inset of up to four pixels.
    /// Glyph pixels are cyan-like when <paramref name="expectHotlight"/> is
    /// set, and white-like otherwise.
    /// </summary>
    internal static HighContrastVectorPixels Measure(Bitmap bitmap, bool expectHotlight)
    {
        // Ignore the outer focus-border band even after focus has been cleared.
        // Requiring Hotlight/WindowText pixels in this interior makes a cyan
        // perimeter alone insufficient to pass the rendered-paint gate.
        int interiorInset = Math.Min(
            4,
            Math.Max(1, (Math.Min(bitmap.Width, bitmap.Height) - 1) / 4));
        var result = new HighContrastVectorPixels(
            interiorInset,
            bitmap.Width - (interiorInset * 2),
            bitmap.Height - (interiorInset * 2));
        for (int y = interiorInset; y < bitmap.Height - interiorInset; y++)
        {
            for (int x = interiorInset; x < bitmap.Width - interiorInset; x++)
                result.Classify(bitmap.GetPixel(x, y), expectHotlight);
        }

        return result;
    }

    private void Classify(Color pixel, bool expectHotlight)
    {
        if (pixel.R <= 48 && pixel.G <= 48 && pixel.B <= 48)
        {
            SurfacePixels++;
            _surfaceRed += pixel.R;
            _surfaceGreen += pixel.G;
            _surfaceBlue += pixel.B;
            return;
        }

        bool isSemantic = expectHotlight
            ? pixel.R <= 80 && pixel.G >= 160 && pixel.B >= 160
            : pixel.R >= 160 && pixel.G >= 160 && pixel.B >= 160;
        if (!isSemantic)
            return;
        SemanticPixels++;
        _semanticRed += pixel.R;
        _semanticGreen += pixel.G;
        _semanticBlue += pixel.B;
    }
}
