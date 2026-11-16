namespace MarkdownRenderer.Images.Internal.Raster;

/// <summary>The format, dimensions and frame facts read from a raster header.</summary>
internal sealed class RasterImageHeader(
    string Format,
    int Width,
    int Height,
    int FrameCount,
    long? TotalPixels = null,
    (int Left, int Top, int Width, int Height)? FirstFrame = null)
{
    public readonly string Format = Format;
    public readonly int Width = Width;
    public readonly int Height = Height;
    public readonly int FrameCount = FrameCount;
    public readonly long? TotalPixels = TotalPixels;
    public readonly (int Left, int Top, int Width, int Height)? FirstFrame = FirstFrame;
}
