using System;
using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// A bounded premultiplied CPU raster whose process-decode credit remains
/// owned until the caller finishes GPU upload. The backing array is internal
/// and must not be retained after disposal.
/// </summary>
internal sealed class RasterDecodedPixelLease : IDisposable
{
    private byte[]? _pixels;
    private IDisposable? _processDecodeCredit;

    internal RasterDecodedPixelLease(int width, int height, byte[] pixels, IDisposable processDecodeCredit)
    {
        Width = width;
        Height = height;
        _pixels = pixels;
        _processDecodeCredit = processDecodeCredit;
    }

    internal int Width { get; }
    internal int Height { get; }
    internal byte[] Pixels => Volatile.Read(ref _pixels)
        ?? throw new ObjectDisposedException(nameof(RasterDecodedPixelLease));

    public void Dispose()
    {
        byte[]? pixels = Interlocked.Exchange(ref _pixels, null);
        if (pixels is not null)
            Array.Clear(pixels);
        Interlocked.Exchange(ref _processDecodeCredit, null)?.Dispose();
    }
}
