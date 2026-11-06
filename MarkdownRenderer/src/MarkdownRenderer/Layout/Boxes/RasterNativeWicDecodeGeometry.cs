namespace MarkdownRenderer.Layout.Boxes;

// Share native ownership, color management and retirement, not the public
// planning contracts. GIF's returned buffer is only its descriptor frame.
/// <summary>
/// The source and target dimensions and format of a native WIC decode,
/// taken from a static or GIF first-frame decode plan.
/// </summary>
internal readonly struct RasterNativeWicDecodeGeometry
{
    internal RasterNativeWicDecodeGeometry(RasterImageDecodePlan plan)
    {
        SourceWidth = plan.SourceWidth;
        SourceHeight = plan.SourceHeight;
        Format = plan.Format;
        FrameCount = 1;
        Orientation = plan.Orientation;
        OutputWidth = plan.OutputWidth;
        OutputHeight = plan.OutputHeight;
        DecodeWidth = plan.DecodeWidth;
        DecodeHeight = plan.DecodeHeight;
    }

    internal RasterNativeWicDecodeGeometry(RasterGifFirstFrameDecodePlan plan)
    {
        SourceWidth = plan.FrameWidth;
        SourceHeight = plan.FrameHeight;
        Format = "GIF";
        FrameCount = plan.FrameCount;
        Orientation = 1;
        OutputWidth = DecodeWidth = plan.DecodeWidth;
        OutputHeight = DecodeHeight = plan.DecodeHeight;
    }

    internal readonly int SourceWidth, SourceHeight, FrameCount, Orientation;
    internal readonly int OutputWidth, OutputHeight, DecodeWidth, DecodeHeight;
    internal readonly string Format;
}
