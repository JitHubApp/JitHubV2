namespace MarkdownRenderer.Images.Internal.Raster;

internal sealed class RasterImageBudgetResult(
    bool Accepted,
    bool CanRenderStaticPreview,
    string? Format,
    int Width,
    int Height,
    int FrameCount,
    long TotalPixels,
    long DecodedBytes,
    string? Reason,
    (int Left, int Top, int Width, int Height)? FirstFrame = null)
{
    public readonly bool Accepted = Accepted;
    public readonly bool CanRenderStaticPreview = CanRenderStaticPreview;
    public readonly string? Format = Format;
    public readonly int Width = Width;
    public readonly int Height = Height;
    public readonly int FrameCount = FrameCount;
    public readonly long TotalPixels = TotalPixels;
    public readonly long DecodedBytes = DecodedBytes;
    public readonly string? Reason = Reason;
    internal readonly (int Left, int Top, int Width, int Height)? FirstFrame = FirstFrame;

    // Orientation changes the planning axes, not the admitted source budget.
    // Keep the validated frame/pixel/byte metadata unchanged.
    internal RasterImageBudgetResult WithDimensions(int width, int height) =>
        new(Accepted, CanRenderStaticPreview, Format, width, height,
            FrameCount, TotalPixels, DecodedBytes, Reason, FirstFrame);

    public static RasterImageBudgetResult Reject(string reason) =>
        new(false, false, null, 0, 0, 0, 0, 0, reason);

    public static RasterImageBudgetResult RejectAnimation(
        string format,
        int width,
        int height,
        int frameCount,
        long totalPixels,
        long decodedBytes,
        string reason,
        (int Left, int Top, int Width, int Height)? firstFrame = null) =>
        new(false, true, format, width, height, frameCount, totalPixels, decodedBytes, reason, firstFrame);

    public static RasterImageBudgetResult RequireStaticPreview(
        string format,
        int width,
        int height,
        int frameCount,
        long totalPixels,
        long decodedBytes,
        string reason,
        (int Left, int Top, int Width, int Height)? firstFrame = null) =>
        new(false, true, format, width, height, frameCount, totalPixels, decodedBytes, reason, firstFrame);
}
