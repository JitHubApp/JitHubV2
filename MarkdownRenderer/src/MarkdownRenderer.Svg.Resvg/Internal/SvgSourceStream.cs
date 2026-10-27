namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Read-only, seekable stream over one scoped source lease. XML readers can inspect
/// encoding/BOMs using normal stream semantics while the underlying native bytes stay
/// alive even if the source owner is retired concurrently.
/// </summary>
internal sealed class SvgSourceStream : Stream
{
    private SvgSourceBuffer.ReadLease? _lease;
    private readonly CancellationToken _cancellationToken;
    private long _position;

    public SvgSourceStream(SvgSourceBuffer.ReadLease lease, CancellationToken cancellationToken = default)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead => Volatile.Read(ref _lease) is not null;

    public override bool CanSeek => Volatile.Read(ref _lease) is not null;

    public override bool CanWrite => false;

    public override long Length => GetLease().Length;

    public override long Position
    {
        get
        {
            _ = GetLease();
            return _position;
        }
        set
        {
            SvgSourceBuffer.ReadLease lease = GetLease();
            if (value < 0 || value > lease.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
        {
            throw new ArgumentException("The requested read range exceeds the destination.");
        }

        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        SvgSourceBuffer.ReadLease lease = GetLease();
        _cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty || _position >= lease.Length)
        {
            return 0;
        }

        int requested = (int)Math.Min(buffer.Length, lease.Length - _position);
        int copied = 0;
        while (copied < requested)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int index = checked((int)_position);
            ReadOnlySpan<byte> source = lease.GetSpanAt(index);
            int count = Math.Min(requested - copied, source.Length);
            if (count == 0)
            {
                throw new InvalidOperationException("The source lease returned an empty span before end of stream.");
            }

            source[..count].CopyTo(buffer[copied..]);
            copied += count;
            _position += count;
        }

        return copied;
    }

    public override int ReadByte()
    {
        SvgSourceBuffer.ReadLease lease = GetLease();
        _cancellationToken.ThrowIfCancellationRequested();
        if (_position >= lease.Length)
        {
            return -1;
        }

        return lease.GetByte(checked((int)_position++));
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
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

    public override long Seek(long offset, SeekOrigin origin)
    {
        SvgSourceBuffer.ReadLease lease = GetLease();
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(lease.Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (target < 0 || target > lease.Length)
        {
            throw new IOException("The requested position is outside the source snapshot.");
        }

        return _position = target;
    }

    public override void Flush() => _ = GetLease();

    public override void SetLength(long value) => throw new NotSupportedException("SVG source streams are read-only.");

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("SVG source streams are read-only.");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
        }

        base.Dispose(disposing);
    }

    private SvgSourceBuffer.ReadLease GetLease() =>
        Volatile.Read(ref _lease) ?? throw new ObjectDisposedException(nameof(SvgSourceStream));
}
