namespace MarkdownRenderer.Svg.Resvg.Internal.Workers;

internal readonly record struct RasterDecodeGeometry(int SourceWidth, int SourceHeight,
    int Orientation, int IntrinsicWidth, int IntrinsicHeight, int OutputLength, long WorkingBytes);
