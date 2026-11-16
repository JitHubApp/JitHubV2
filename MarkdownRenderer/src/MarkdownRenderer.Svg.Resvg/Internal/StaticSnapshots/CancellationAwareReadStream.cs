using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;

namespace MarkdownRenderer.Svg.Resvg.Internal.StaticSnapshots;

internal sealed class CancellationAwareReadStream : Stream
{
    private byte[] _source;
    private readonly CancellationToken _cancellationToken;
    private int _position;
    private bool _disposed;

    internal CancellationAwareReadStream(byte[] source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => false;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _source.Length;
        }
    }

    public override long Position
    {
        get
        {
            ThrowIfDisposed();
            return _position;
        }
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("The buffer range is invalid.");
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        ThrowIfAvailable();
        if (_position >= _source.Length || buffer.IsEmpty)
            return 0;

        int count = Math.Min(buffer.Length, _source.Length - _position);
        _source.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override int ReadByte()
    {
        ThrowIfAvailable();
        return _position < _source.Length ? _source[_position++] : -1;
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Read(buffer.Span));
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Read(buffer, offset, count));
    }

    public override void Flush() => ThrowIfAvailable();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            _source = Array.Empty<byte>();
            _position = 0;
        }
        base.Dispose(disposing);
    }

    private void ThrowIfAvailable()
    {
        ThrowIfDisposed();
        _cancellationToken.ThrowIfCancellationRequested();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
