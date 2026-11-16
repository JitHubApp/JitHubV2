namespace MarkdownRenderer.Svg.Resvg.TestSupport;

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
