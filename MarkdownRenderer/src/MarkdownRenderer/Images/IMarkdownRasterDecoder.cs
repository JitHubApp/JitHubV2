using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Images;

/// <summary>Decodes bounded static raster images independently of installed image codecs.</summary>
/// <remarks>
/// A control borrows this service and never disposes it. Implementations must support concurrent
/// calls, enforce resource limits and active-operation deadlines, and perform expensive work off
/// the UI thread. A failed decode must not return partially initialized pixels. The output is sRGB,
/// with embedded color profiles and EXIF orientation applied before publication.
/// Controls route only containers for which the host can construct a validated static decode
/// plan. Animated images and GIF frame/canvas composition retain their existing host paths;
/// advertising a format does not bypass those admission and composition rules.
/// </remarks>
public interface IMarkdownRasterDecoder
{
    /// <summary>Reports whether this decoder implements a static image format.</summary>
    /// <remarks>This must be a cheap, thread-safe capability check, with no I/O or decoding.</remarks>
    bool SupportsFormat(MarkdownRasterFormat format);

    /// <summary>Decodes an admitted image to exact, oriented physical-pixel dimensions.</summary>
    /// <returns>Owned premultiplied sRGB pixels, kept alive by the caller until GPU upload completes.</returns>
    /// <exception cref="MarkdownRasterException">The decode failed for a typed provider reason.</exception>
    ValueTask<MarkdownRaster> DecodeAsync(
        MarkdownRasterDecodeRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>A static image container. The provider must validate the actual encoded bytes.</summary>
public enum MarkdownRasterFormat
{
    /// <summary>Portable Network Graphics.</summary>
    Png,
    /// <summary>JPEG.</summary>
    Jpeg,
    /// <summary>Windows bitmap.</summary>
    Bmp,
    /// <summary>WebP.</summary>
    WebP,
    /// <summary>Graphics Interchange Format.</summary>
    Gif,
    /// <summary>AV1 Image File Format.</summary>
    Avif,
    /// <summary>Windows icon.</summary>
    Ico,
}

/// <summary>Immutable decode settings for one borrowed encoded source.</summary>
/// <param name="EncodedBytes">
/// Source bytes admitted by the host's image policy. Keep the backing memory unchanged and alive
/// until DecodeAsync completes, including failure or cancellation. Providers must independently
/// validate the container and may not retain this memory after the operation completes.
/// </param>
/// <param name="Format">Expected container, independently checked against the bytes.</param>
/// <param name="TargetWidthPixels">Exact output width after EXIF orientation, without upscaling.</param>
/// <param name="TargetHeightPixels">Exact output height after EXIF orientation, without upscaling.</param>
/// <param name="MaximumWorkingBytes">
/// Host-lowered bound for source mapping, output and decoder-owned working allocations. This is
/// not a process RSS guarantee; providers must also enforce their own immutable process limits.
/// </param>
/// <param name="PixelFormat">Premultiplied output byte order.</param>
/// <param name="Priority">Scheduling class before work enters the decoder.</param>
public sealed record MarkdownRasterDecodeRequest(
    ReadOnlyMemory<byte> EncodedBytes,
    MarkdownRasterFormat Format,
    int TargetWidthPixels,
    int TargetHeightPixels,
    long MaximumWorkingBytes,
    MarkdownRasterPixelFormat PixelFormat = MarkdownRasterPixelFormat.Bgra8Premultiplied,
    MarkdownRasterDecodePriority Priority = MarkdownRasterDecodePriority.Visible);

/// <summary>Supported premultiplied sRGB byte orders.</summary>
public enum MarkdownRasterPixelFormat
{
    /// <summary>Red, green, blue, alpha.</summary>
    Rgba8Premultiplied,
    /// <summary>Blue, green, red, alpha.</summary>
    Bgra8Premultiplied,
}

/// <summary>Scheduling priority for static raster preparation.</summary>
public enum MarkdownRasterDecodePriority
{
    /// <summary>Speculative preparation, behind visible requests.</summary>
    Overscan,
    /// <summary>Content needed in the current viewport.</summary>
    Visible,
}

/// <summary>Owns a decoded premultiplied sRGB raster until GPU upload completes.</summary>
public sealed class MarkdownRaster : IDisposable
{
    private IMemoryOwner<byte>? _owner;

    /// <summary>Transfers an owner to a validated raster; ownership remains with the caller if construction fails.</summary>
    public MarkdownRaster(IMemoryOwner<byte> owner, int widthPixels, int heightPixels,
        int strideBytes, MarkdownRasterPixelFormat pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPixels);
        if (strideBytes < checked(widthPixels * 4))
            throw new ArgumentOutOfRangeException(nameof(strideBytes));
        if (!Enum.IsDefined(pixelFormat))
            throw new ArgumentOutOfRangeException(nameof(pixelFormat));
        LengthBytes = checked(strideBytes * heightPixels);
        if (owner.Memory.Length < LengthBytes)
            throw new ArgumentException("The memory owner is smaller than the declared raster.", nameof(owner));
        WidthPixels = widthPixels;
        HeightPixels = heightPixels;
        StrideBytes = strideBytes;
        PixelFormat = pixelFormat;
        _owner = owner;
    }

    /// <summary>Gets the pixels. Do not access this memory concurrently with, or after, disposal.</summary>
    public ReadOnlyMemory<byte> Pixels => (Volatile.Read(ref _owner)
        ?? throw new ObjectDisposedException(nameof(MarkdownRaster))).Memory[..LengthBytes];
    /// <summary>Gets the meaningful pixel byte length.</summary>
    public int LengthBytes { get; }
    /// <summary>Gets the oriented physical-pixel width.</summary>
    public int WidthPixels { get; }
    /// <summary>Gets the oriented physical-pixel height.</summary>
    public int HeightPixels { get; }
    /// <summary>Gets the byte distance between rows.</summary>
    public int StrideBytes { get; }
    /// <summary>Gets the premultiplied sRGB byte order.</summary>
    public MarkdownRasterPixelFormat PixelFormat { get; }
    /// <summary>Releases the pixel owner exactly once.</summary>
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
}

/// <summary>Stable categories for static raster decoder failures.</summary>
public enum MarkdownRasterFailureReason
{
    /// <summary>Malformed input or a container feature outside the supported static subset.</summary>
    UnsupportedContent,
    /// <summary>The source, decoded image, output or working memory exceeds a limit.</summary>
    ResourceLimitExceeded,
    /// <summary>The active-operation deadline elapsed.</summary>
    Timeout,
    /// <summary>The isolated decoder failed or returned an invalid response.</summary>
    WorkerFailure,
    /// <summary>No decoder worker is available for this architecture.</summary>
    ArchitectureMismatch,
    /// <summary>The operation was canceled or the provider was disposed.</summary>
    Canceled,
}

/// <summary>A typed static raster decoding failure.</summary>
public sealed class MarkdownRasterException : Exception
{
    /// <summary>Initializes a typed decoder failure without embedding image data.</summary>
    public MarkdownRasterException(MarkdownRasterFailureReason reason, string? message = null,
        Exception? innerException = null)
        : base(message ?? "The raster image could not be decoded.", innerException)
    {
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));
        Reason = reason;
    }

    /// <summary>Gets the stable reason for the failure.</summary>
    public MarkdownRasterFailureReason Reason { get; }
}
