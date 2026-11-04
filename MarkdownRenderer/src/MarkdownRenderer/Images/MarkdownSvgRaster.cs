using System;
using System.Buffers;
using System.Threading;

namespace MarkdownRenderer.Images;

/// <summary>
/// Owns a premultiplied raster buffer until a host completes GPU upload.
/// </summary>
public sealed class MarkdownSvgRaster : IDisposable
{
    private IMemoryOwner<byte>? _owner;
    private readonly int _lengthBytes;

    /// <summary>Initializes an owned raster over provider-supplied memory.</summary>
    /// <param name="owner">Owner released by <see cref="Dispose"/>.</param>
    /// <param name="lengthBytes">Number of meaningful bytes at the start of the owner's memory.</param>
    /// <param name="widthPixels">Raster width in physical pixels.</param>
    /// <param name="heightPixels">Raster height in physical pixels.</param>
    /// <param name="strideBytes">Byte distance between adjacent raster rows.</param>
    /// <param name="pixelFormat">Premultiplied byte order.</param>
    public MarkdownSvgRaster(
        IMemoryOwner<byte> owner,
        int lengthBytes,
        int widthPixels,
        int heightPixels,
        int strideBytes,
        MarkdownSvgPixelFormat pixelFormat)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightPixels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(strideBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lengthBytes);

        int minimumStride = checked(widthPixels * 4);
        if (strideBytes < minimumStride)
            throw new ArgumentOutOfRangeException(nameof(strideBytes));

        int minimumLength = checked(strideBytes * heightPixels);
        if (lengthBytes < minimumLength || lengthBytes > owner.Memory.Length)
            throw new ArgumentOutOfRangeException(nameof(lengthBytes));

        if (!Enum.IsDefined(pixelFormat))
            throw new ArgumentOutOfRangeException(nameof(pixelFormat));

        _owner = owner;
        _lengthBytes = lengthBytes;
        WidthPixels = widthPixels;
        HeightPixels = heightPixels;
        StrideBytes = strideBytes;
        PixelFormat = pixelFormat;
    }

    /// <summary>Gets the meaningful raster bytes.</summary>
    /// <exception cref="ObjectDisposedException">The raster has been disposed.</exception>
    public ReadOnlyMemory<byte> Pixels
    {
        get
        {
            IMemoryOwner<byte> owner = _owner ??
                throw new ObjectDisposedException(nameof(MarkdownSvgRaster));
            return owner.Memory[.._lengthBytes];
        }
    }

    /// <summary>Gets the number of meaningful raster bytes.</summary>
    public int LengthBytes => _lengthBytes;

    /// <summary>Gets the raster width in physical pixels.</summary>
    public int WidthPixels { get; }

    /// <summary>Gets the raster height in physical pixels.</summary>
    public int HeightPixels { get; }

    /// <summary>Gets the byte distance between adjacent raster rows.</summary>
    public int StrideBytes { get; }

    /// <summary>Gets the premultiplied raster byte order.</summary>
    public MarkdownSvgPixelFormat PixelFormat { get; }

    /// <summary>Releases the provider-owned raster memory.</summary>
    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
}
