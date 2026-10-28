using System;
using System.Threading;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Images;

/// <summary>
/// The result of checking an encoded raster image's headers against the
/// renderer's decode budget. Inspection reads only container headers and
/// orientation metadata; it never decodes pixels.
/// </summary>
/// <remarks>
/// Raster decoders and SVG renderers use it to apply the same limits as the
/// view before they hand untrusted bytes to a decoder.
/// </remarks>
public sealed class MarkdownRasterImageInspection
{
    private readonly RasterImageBudgetResult _budget;

    private MarkdownRasterImageInspection(
        RasterImageBudgetResult budget,
        RasterIntrinsicMetadata? displaySize,
        int orientation)
    {
        _budget = budget;
        DisplayWidth = displaySize?.Width ?? 0;
        DisplayHeight = displaySize?.Height ?? 0;
        Orientation = orientation;
    }

    /// <summary>Checks <paramref name="encodedBytes"/> against the decode budget.</summary>
    /// <param name="encodedBytes">The encoded image.</param>
    /// <param name="cancellationToken">Cancels the inspection.</param>
    public static MarkdownRasterImageInspection Inspect(
        ReadOnlySpan<byte> encodedBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RasterImageBudgetResult budget = RasterImageResourceBudget.Validate(encodedBytes);
        bool hasDisplaySize = RasterIntrinsicMetadata.TryReadValidated(
            encodedBytes,
            budget,
            out RasterIntrinsicMetadata displaySize,
            out int orientation,
            cancellationToken);
        return hasDisplaySize
            ? new MarkdownRasterImageInspection(budget, displaySize, orientation)
            : new MarkdownRasterImageInspection(budget, null, 1);
    }

    /// <summary>Gets whether every frame of the image fits the budget.</summary>
    public bool IsAccepted => _budget.Accepted;

    /// <summary>
    /// Gets whether the whole image is over budget but a static preview of its
    /// first frame fits, as for a long animation.
    /// </summary>
    public bool CanRenderStaticPreview => _budget.CanRenderStaticPreview;

    /// <summary>Gets a stable reason code when the image is not accepted.</summary>
    public string? RejectionReason => _budget.Accepted ? null : _budget.Reason;

    /// <summary>
    /// Gets the container format, or null when it is not recognized or the
    /// image is rejected before its format is established.
    /// </summary>
    public MarkdownRasterFormat? Format => _budget.Format switch
    {
        "PNG" => MarkdownRasterFormat.Png,
        "JPEG" => MarkdownRasterFormat.Jpeg,
        "GIF" => MarkdownRasterFormat.Gif,
        "WEBP" => MarkdownRasterFormat.WebP,
        "BMP" => MarkdownRasterFormat.Bmp,
        "ICO" => MarkdownRasterFormat.Ico,
        "AVIF" => MarkdownRasterFormat.Avif,
        _ => null,
    };

    /// <summary>Gets the stored width in pixels, before orientation.</summary>
    public int PixelWidth => _budget.Width;

    /// <summary>Gets the stored height in pixels, before orientation.</summary>
    public int PixelHeight => _budget.Height;

    /// <summary>Gets the number of frames.</summary>
    public int FrameCount => _budget.FrameCount;

    /// <summary>
    /// Gets whether the first frame covers only part of the image canvas, as in
    /// some animated GIF and WebP files.
    /// </summary>
    public bool HasFirstFrameRegion => _budget.FirstFrame is not null;

    /// <summary>Gets the bytes that decoding every admitted frame needs.</summary>
    public long DecodedBytes => _budget.DecodedBytes;

    /// <summary>Gets the EXIF orientation, 1 to 8. It is 1 when the image has none.</summary>
    public int Orientation { get; }

    /// <summary>
    /// Gets the width after orientation, or 0 when the image is rejected or
    /// its container is too incomplete to read orientation from.
    /// </summary>
    public int DisplayWidth { get; }

    /// <summary>
    /// Gets the height after orientation, or 0 when the image is rejected or
    /// its container is too incomplete to read orientation from.
    /// </summary>
    public int DisplayHeight { get; }
}
