using System.Runtime.InteropServices;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Svg.ThorVG;

/// <summary>
/// Identifies the optional ThorVG feature pack. Referencing this assembly places
/// the native rasterizer for the selected Windows architecture beside the app.
/// </summary>
public static class ThorVgFeature
{
    /// <summary>Gets the native ThorVG version used to build the packaged assets.</summary>
    public const string NativeVersion = "1.1.1";

    /// <summary>Gets whether the current process architecture has a packaged ThorVG asset.</summary>
    public static bool IsSupportedArchitecture =>
        RuntimeInformation.ProcessArchitecture is Architecture.X86 or Architecture.X64 or Architecture.Arm64;

    /// <summary>
    /// Probes the selected-RID native library and returns its semantic version,
    /// or <see langword="null"/> when the optional asset is absent, incompatible,
    /// or missing the required ABI.
    /// </summary>
    public static Version? TryGetLoadedNativeVersion() => ThorVgRasterizer.TryGetNativeVersion();

    /// <summary>
    /// Rasterizes a self-contained SVG to premultiplied BGRA pixels. A null
    /// result is a safe feature-unavailable or invalid-input fallback.
    /// </summary>
    public static ThorVgRaster? Rasterize(
        ReadOnlySpan<byte> svg,
        int targetWidthPixels,
        int targetHeightPixels,
        CancellationToken cancellationToken = default)
    {
        if (svg.IsEmpty)
            return null;

        cancellationToken.ThrowIfCancellationRequested();
        if (svg.Length > SvgResourceBudget.MaxInputBytes)
            return null;

        byte[] source = svg.ToArray();
        SvgResourceBudgetResult budget = SvgResourceBudget.Validate(source, cancellationToken);
        if (!budget.Accepted)
            return null;

        ThorVgRasterizer.Raster? raster = ThorVgRasterizer.Rasterize(
            source,
            targetWidthPixels,
            targetHeightPixels,
            cancellationToken);
        return raster is { } value
            ? new ThorVgRaster(value.Bgra, value.WidthPx, value.HeightPx)
            : null;
    }
}

/// <summary>Immutable metadata and pixels produced by ThorVG's software canvas.</summary>
public sealed class ThorVgRaster
{
    internal ThorVgRaster(byte[] bgraPremultipliedPixels, int widthPixels, int heightPixels)
    {
        BgraPremultipliedPixels = bgraPremultipliedPixels;
        WidthPixels = widthPixels;
        HeightPixels = heightPixels;
    }

    /// <summary>Gets premultiplied BGRA bytes in row-major order.</summary>
    public ReadOnlyMemory<byte> BgraPremultipliedPixels { get; }

    /// <summary>Gets the raster width in physical pixels.</summary>
    public int WidthPixels { get; }

    /// <summary>Gets the raster height in physical pixels.</summary>
    public int HeightPixels { get; }
}
