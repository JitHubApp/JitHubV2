using System.Buffers;

namespace MarkdownRenderer.GitHub.Tests.Layout.Internal.Images.Raster;

/// <summary>Pixel buffer owner that records when <see cref="FakeRasterDecoder"/> output is released.</summary>
internal sealed class TrackingMemoryOwner(int length) : IMemoryOwner<byte>
{
    private byte[]? _buffer = new byte[length];
    private int _disposeCount;

    internal bool IsDisposed => Volatile.Read(ref _buffer) is null;
    internal int DisposeCount => Volatile.Read(ref _disposeCount);
    public Memory<byte> Memory => Volatile.Read(ref _buffer)
        ?? throw new ObjectDisposedException(nameof(TrackingMemoryOwner));

    public void Dispose()
    {
        Interlocked.Increment(ref _disposeCount);
        Interlocked.Exchange(ref _buffer, null);
    }
}
