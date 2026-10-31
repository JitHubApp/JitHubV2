namespace MarkdownRenderer.Svg.Resvg.TestSupport;

// Allocation-free checks keep failed fidelity observations from perturbing the
// independently measured render/evict memory gate. All failures remain fatal.
public static class SmokeRasterValidation
{
    public const int ManyImagesWidth = 890;
    public const int ManyImagesHeight = 158;
    public const int ManyImagesCount = 182;

    public static SmokeRasterFailure CheckManyImages(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width != ManyImagesWidth || height != ManyImagesHeight ||
            pixels.Length != ManyImagesWidth * ManyImagesHeight * 4)
            return SmokeRasterFailure.Dimensions;

        for (int image = 0; image < ManyImagesCount; image++)
        {
            int x = (image % 26) * 34 + 8;
            int y = (image / 26) * 22 + 8;
            int index = ((y * width) + x) * 4;
            if (pixels[index] != image + 1 || pixels[index + 1] != 255 - image ||
                pixels[index + 2] != (image * 37) % 256 || pixels[index + 3] != 255)
                return SmokeRasterFailure.UniqueEmbeddedImages;
        }

        return SmokeRasterFailure.None;
    }

    public static SmokeRasterResult CheckRich(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width != 128 || height != 64 || pixels.Length != 128 * 64 * 4)
            return new(SmokeRasterFailure.Dimensions, 128, -1);

        SmokeRasterFailure failures = SmokeRasterFailure.None;
        if (!IsRedPixel(pixels, 4, 4, width))
            failures |= SmokeRasterFailure.FirstEmbeddedImage;
        if (!IsRedPixel(pixels, 20, 4, width))
            failures |= SmokeRasterFailure.RepeatedEmbeddedImage;

        int textLeft = width;
        int textRight = -1;
        for (int y = 20; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (pixels[((y * width) + x) * 4 + 3] <= 16)
                    continue;
                textLeft = Math.Min(textLeft, x);
                textRight = Math.Max(textRight, x);
            }
        }

        if (textLeft < 32 || textRight > 122 || textRight - textLeft < 24)
            failures |= SmokeRasterFailure.RtlBounds;
        return new(failures, textLeft, textRight);
    }

    private static bool IsRedPixel(ReadOnlySpan<byte> pixels, int x, int y, int width)
    {
        int index = ((y * width) + x) * 4;
        return pixels[index] >= 200 && pixels[index + 1] <= 60 &&
            pixels[index + 2] <= 80 && pixels[index + 3] >= 240;
    }
}

[Flags]
public enum SmokeRasterFailure
{
    None = 0,
    Dimensions = 1,
    FirstEmbeddedImage = 2,
    RepeatedEmbeddedImage = 4,
    RtlBounds = 8,
    UniqueEmbeddedImages = 16,
}

public readonly record struct SmokeRasterResult(SmokeRasterFailure Failures, int TextLeft, int TextRight);
