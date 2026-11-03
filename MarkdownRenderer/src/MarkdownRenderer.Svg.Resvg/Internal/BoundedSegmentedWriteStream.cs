using System.Linq;
using System.Text;
using System.Xml;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed class BoundedSegmentedWriteStream : Stream
{
    private const int SegmentSize = 16 * 1024;

    private readonly int _maximumLength;
    private readonly CancellationToken _cancellationToken;
    private readonly Func<int, byte[]> _allocateSegment;
    private readonly List<byte[]> _segments = [];
    private byte[]? _currentSegment;
    private int _currentOffset;
    private int _length;
    private int _allocatedLength;
    private bool _disposed;

    /// <param name="maximumLength">The output ceiling.</param>
    /// <param name="cancellationToken">Cancels writes and finalization.</param>
    /// <param name="allocateSegment">
    /// Allocates one zeroed segment of exactly the requested length; the
    /// default allocates a new array. Every segment is cleared on dispose.
    /// </param>
    internal BoundedSegmentedWriteStream(
        int maximumLength,
        CancellationToken cancellationToken,
        Func<int, byte[]>? allocateSegment = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLength);
        if (maximumLength > SvgResourceBudget.MaxInputBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        _maximumLength = maximumLength;
        _cancellationToken = cancellationToken;
        _allocateSegment = allocateSegment ?? (static size => new byte[size]);
    }

    internal bool LimitExceeded { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;

    public override long Length
    {
        get
        {
            ThrowIfDisposed();
            return _length;
        }
    }

    public override long Position
    {
        get => Length;
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("The buffer range is invalid.");
        Write(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        ThrowIfAvailable();
        if (buffer.Length > _maximumLength - _length)
        {
            LimitExceeded = true;
            throw new SvgStaticSnapshotResourceException();
        }

        while (!buffer.IsEmpty)
        {
            ThrowIfAvailable();
            if (_currentSegment is null || _currentOffset == _currentSegment.Length)
                AllocateNextSegment();

            int count = Math.Min(buffer.Length, _currentSegment!.Length - _currentOffset);
            buffer[..count].CopyTo(_currentSegment.AsSpan(_currentOffset, count));
            buffer = buffer[count..];
            _currentOffset += count;
            _length += count;
        }
    }

    public override void WriteByte(byte value)
    {
        Span<byte> single = stackalloc byte[1];
        single[0] = value;
        Write(single);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override void Flush() => ThrowIfAvailable();

    internal byte[] ToArray()
    {
        ThrowIfAvailable();
        byte[] result = new byte[_length];
        try
        {
            int destinationOffset = 0;
            foreach (byte[] segment in _segments)
            {
                ThrowIfAvailable();
                int count = Math.Min(segment.Length, _length - destinationOffset);
                if (count <= 0)
                    break;
                segment.AsSpan(0, count).CopyTo(result.AsSpan(destinationOffset, count));
                destinationOffset += count;
            }
            ThrowIfAvailable();
            return result;
        }
        catch
        {
            Array.Clear(result, 0, result.Length);
            throw;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            _disposed = true;
            foreach (byte[] segment in _segments)
                Array.Clear(segment);
            _segments.Clear();
            _currentSegment = null;
            _currentOffset = 0;
            _allocatedLength = 0;
            _length = 0;
        }
        base.Dispose(disposing);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private void AllocateNextSegment()
    {
        int remaining = _maximumLength - _allocatedLength;
        if (remaining <= 0)
        {
            LimitExceeded = true;
            throw new SvgStaticSnapshotResourceException();
        }

        int size = Math.Min(SegmentSize, remaining);
        byte[] segment = _allocateSegment(size);
        if (segment.Length != size)
            throw new InvalidOperationException("An SVG output segment has the wrong length.");
        _currentSegment = segment;
        _segments.Add(_currentSegment);
        _allocatedLength += size;
        _currentOffset = 0;
    }

    private void ThrowIfAvailable()
    {
        ThrowIfDisposed();
        _cancellationToken.ThrowIfCancellationRequested();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
