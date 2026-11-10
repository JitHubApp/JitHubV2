using System;
using System.Globalization;
using System.Text;
using Windows.Foundation;
using Windows.UI;
using MarkdownRenderer.Images.Svg;
using MarkdownRenderer.Layout.Internal.Context;
using MarkdownRenderer.Theming;
using SvgRasterPlan = (int Width, int Height, bool UsesTiles, MarkdownRenderer.Images.Svg.MarkdownSvgPhysicalViewport PhysicalViewport);

namespace MarkdownRenderer.Layout.Internal.Images.Svg.Rendering;

/// <summary>
/// Chooses the physical raster an image box renders its SVG at, whether that
/// raster must be tiled, and the device-bitmap identity of a rendered raster
/// under the box's theme, language and provider generation.
/// </summary>
internal sealed class SvgRasterPlanner
{
    private const long MaxSvgOutputRasterBytes = 64L * 1024 * 1024;
    // Direct2D bitmap limits vary by feature level and a recovering/WARP
    // device can transiently report a larger capability than it can allocate.
    // Keep the untiled path within the Windows-wide guaranteed ceiling so the
    // decision is deterministic across hardware, WARP, and CI machines.
    private const int MaxUntiledSvgDimensionPixels = 16_384;

    private readonly ImageBox _box;

    internal SvgRasterPlanner(ImageBox box) => _box = box;

    /// <summary>
    /// The raster for <paramref name="intrinsic"/> at the measured display
    /// size and rasterization scale. Throws a resource-limit failure when the
    /// dimensions overflow.
    /// </summary>
    internal SvgRasterPlan PickRasterDimensions(Size intrinsic)
    {
        ImageMeasureCustody measure = _box.MeasureCustody;
        MarkdownLayoutContext context = _box.Context;
        float naturalWidth = (float)Math.Max(1d, intrinsic.Width);
        float naturalHeight = (float)Math.Max(1d, intrinsic.Height);
        float maxWidth = measure.IsInlineLayout
            ? Math.Max(1f, measure.AvailableWidth)
            : Math.Max(1f, measure.AvailableWidth - (float)(_box.Margin.Left + _box.Margin.Right));
        if (!float.IsFinite(maxWidth) || measure.AvailableWidth <= 0)
            maxWidth = naturalWidth;

        float fit = naturalWidth > maxWidth ? maxWidth / naturalWidth : 1f;
        float displayWidth = Math.Max(1f, naturalWidth * fit);
        float displayHeight = Math.Max(1f, naturalHeight * fit);
        ImageRequestedSize.Apply(
            _box.RequestedWidth, _box.RequestedHeight, ref displayWidth, ref displayHeight, maxWidth);

        double rasterScale = context.RasterizationScale;
        if (!double.IsFinite(rasterScale) || rasterScale <= 0)
            rasterScale = 1d;

        (int width, int height, long outputBytes) = GetRasterSize(displayWidth, displayHeight, rasterScale);
        return (
            width,
            height,
            outputBytes > (context.PerformanceSession?.Options.ReleaseOffscreenImageResources != true ? MaxSvgOutputRasterBytes :
                Math.Min(MaxSvgOutputRasterBytes, _box.Resources.BitmapOutput.MaximumSingleOutputBytes)) ||
                width > MaxUntiledSvgDimensionPixels ||
                height > MaxUntiledSvgDimensionPixels ||
                width > context.ResourceCreator.Device.MaximumBitmapSizeInPixels ||
                height > context.ResourceCreator.Device.MaximumBitmapSizeInPixels,
            new MarkdownSvgPhysicalViewport(displayWidth * rasterScale, displayHeight * rasterScale));
    }

    private static (int Width, int Height, long OutputBytes) GetRasterSize(
        float displayWidth, float displayHeight, double rasterScale)
    {
        int width;
        int height;
        try
        {
            width = checked((int)Math.Ceiling(displayWidth * rasterScale));
            height = checked((int)Math.Ceiling(displayHeight * rasterScale));
        }
        catch (OverflowException ex)
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.ResourceLimitExceeded,
                "The requested SVG raster dimensions exceed the supported range.",
                ex);
        }

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        try
        {
            return (width, height, checked((long)width * height * 4L));
        }
        catch (OverflowException ex)
        {
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.ResourceLimitExceeded,
                "The requested SVG raster exceeds the output budget.",
                ex);
        }
    }

    /// <summary>
    /// The device-bitmap identity of one rendered raster. Text-bearing SVGs
    /// add the provider generation and language; scheme- and color-dependent
    /// SVGs add the theme inputs they use.
    /// </summary>
    internal string BuildBitmapIdentity(
        string contentHash,
        string securityPartitionHash,
        MarkdownSvgDocumentInfo info,
        int width,
        int height,
        MarkdownSvgColorScheme colorScheme,
        MarkdownSvgColor semanticColor,
        long rendererIdentity,
        long providerCacheGeneration,
        MarkdownSvgPhysicalViewport physicalViewport)
    {
        var identity = new StringBuilder(192);
        identity.Append("svg:v3:renderer=")
            .Append(rendererIdentity)
            .Append(':')
            .Append(securityPartitionHash)
            .Append(':')
            .Append(contentHash)
            .Append(':')
            .Append(width)
            .Append('x')
            .Append(height)
            .Append(":viewport=")
            .Append(BitConverter.DoubleToInt64Bits(physicalViewport.Width).ToString("X16", CultureInfo.InvariantCulture))
            .Append('x')
            .Append(BitConverter.DoubleToInt64Bits(physicalViewport.Height).ToString("X16", CultureInfo.InvariantCulture));

        if (info.HasText)
        {
            identity.Append(":generation=")
                .Append(providerCacheGeneration)
                .Append(":locale=")
                .Append(_box.Context.Language);
        }
        if (info.UsesColorScheme)
            identity.Append(":scheme=").Append((int)colorScheme);
        if (info.UsesCurrentColor)
        {
            identity.Append(":color=")
                .Append(semanticColor.Alpha.ToString("X2", CultureInfo.InvariantCulture))
                .Append(semanticColor.Red.ToString("X2", CultureInfo.InvariantCulture))
                .Append(semanticColor.Green.ToString("X2", CultureInfo.InvariantCulture))
                .Append(semanticColor.Blue.ToString("X2", CultureInfo.InvariantCulture));
        }

        return identity.ToString();
    }

    /// <summary>The body foreground, which an SVG's <c>currentColor</c> resolves to.</summary>
    internal MarkdownSvgColor GetSemanticColor()
    {
        Color foreground = _box.Context.ThemeSnapshot
            .GetStyle(MarkdownElementKeys.Body)
            .Foreground;
        return new MarkdownSvgColor(
            foreground.R,
            foreground.G,
            foreground.B,
            foreground.A);
    }

    /// <summary>The color scheme an SVG's <c>prefers-color-scheme</c> rules see.</summary>
    internal MarkdownSvgColorScheme GetColorScheme() =>
        _box.Context.ThemeSnapshot.IsDark
            ? MarkdownSvgColorScheme.Dark
            : MarkdownSvgColorScheme.Light;
}
