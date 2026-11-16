using System;
using System.Buffers;
using System.Threading;

namespace MarkdownRenderer.Images.Raster;

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
