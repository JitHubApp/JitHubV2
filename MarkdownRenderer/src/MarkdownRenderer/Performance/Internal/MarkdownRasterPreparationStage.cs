namespace MarkdownRenderer.Performance.Internal;

internal enum MarkdownRasterPreparationStage
{
    SourceStreamOpen = 1,
    DecoderCreation = 2,
    WicPixelDecode = 3,
    Win2DBitmapUpload = 4,
    Win2DDirectLoad = 5,
    CachePublication = 6,
    PreparationAdmission = 7,
    RasterProviderDecode = 8,
}
