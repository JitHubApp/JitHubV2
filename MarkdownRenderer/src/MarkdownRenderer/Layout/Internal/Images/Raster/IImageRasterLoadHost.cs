using MarkdownRenderer.Layout.Internal.Images.BitmapCache;

namespace MarkdownRenderer.Layout.Internal.Images.Raster;

/// <summary>The image box as seen by its <see cref="ImageRasterLoader"/>.</summary>
internal interface IImageRasterLoadHost : IImageLoadHost
{
    /// <summary>The width a display-sized raster decode may fill, in DIPs.</summary>
    double RasterSizingAvailableWidth { get; }

    /// <summary>
    /// On the UI thread, shows a cached lower-resolution preview unless the box
    /// already presents a bitmap.
    /// </summary>
    void PresentRasterPreview(SharedCanvasBitmapCache.Lease cachedPreview);

    /// <summary>On the UI thread, applies a finished raster load.</summary>
    void PresentRasterLoad(in RasterLoadCompletion completion);
}
