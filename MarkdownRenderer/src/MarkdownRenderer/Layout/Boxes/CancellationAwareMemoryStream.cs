using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// A borrowed byte-array stream that checks cancellation at each read chunk.
/// The stream is not published; disposing it never owns or modifies the source.
/// </summary>
internal sealed class CancellationAwareMemoryStream : MemoryStream
{
    private readonly CancellationToken _cancellationToken;

    public CancellationAwareMemoryStream(byte[] buffer, CancellationToken cancellationToken)
        : base(buffer, writable: false)
    {
        _cancellationToken = cancellationToken;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return base.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return base.Read(buffer);
    }

    public override int ReadByte()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return base.ReadByte();
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cancellationToken.ThrowIfCancellationRequested();
        return base.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cancellationToken.ThrowIfCancellationRequested();
        return base.ReadAsync(buffer, cancellationToken);
    }
}
