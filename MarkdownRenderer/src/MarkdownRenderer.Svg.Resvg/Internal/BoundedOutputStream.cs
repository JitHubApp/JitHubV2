using System.Security.Cryptography;
using System.Text;
using System.Xml;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

internal sealed class BoundedOutputStream(
    int maximumBytes,
    CancellationToken cancellationToken,
    Action? beforeFirstSegmentAllocation = null) : Stream
{
    private List<byte[]>? _segments = [];
    private long _capacity;
    private long _length;
    private int _lastSegmentLength;
    private Action? _beforeFirstSegmentAllocation = beforeFirstSegmentAllocation;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _length;

    public override long Position
    {
        get => _length;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush() => cancellationToken.ThrowIfCancellationRequested();

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush();
        return Task.CompletedTask;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count)
            throw new ArgumentException("The write range is outside the buffer.");
        WriteCore(buffer.AsSpan(offset, count));
    }

    public override void Write(ReadOnlySpan<byte> buffer) => WriteCore(buffer);

    public override void WriteByte(byte value)
    {
        Span<byte> singleByte = stackalloc byte[1];
        singleByte[0] = value;
        WriteCore(singleByte);
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

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteCore(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    internal int AllocatedCapacity => checked((int)_capacity);

    /// <summary>Transfers the writer's segment arrays without flattening or copying source bytes.</summary>
    internal SvgSourceBuffer DetachSourceBuffer()
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<byte[]> segments = _segments ?? throw new ObjectDisposedException(nameof(BoundedOutputStream));

        // Keep tiny normalized snapshots from retaining nearly one chunk
        // of unused capacity apiece. Allocate and validate the compacted
        // tail before replacing the writer's owner entry so cancellation
        // or allocation failure leaves ordinary Dispose cleanup intact.
        if (_lastSegmentLength > 0 && _lastSegmentLength < segments[^1].Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] compactTail = GC.AllocateUninitializedArray<byte>(_lastSegmentLength);
            segments[^1].AsSpan(0, _lastSegmentLength).CopyTo(compactTail);
            cancellationToken.ThrowIfCancellationRequested();
            _capacity -= segments[^1].Length - compactTail.Length;
            segments[^1] = compactTail;
        }

        SvgSourceBuffer source = SvgSourceBuffer.TakeOwnedSegments(
            segments,
            checked((int)_length),
            checked((int)_capacity));
        _segments = null;
        _capacity = 0;
        _length = 0;
        _lastSegmentLength = 0;
        return source;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _segments?.Clear();
            _segments = null;
        }
        _capacity = 0;
        _length = 0;
        _lastSegmentLength = 0;
        _beforeFirstSegmentAllocation = null;
        base.Dispose(disposing);
    }

    private void WriteCore(ReadOnlySpan<byte> value)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value.Length > maximumBytes - _length)
            throw SvgPreflightFailures.Resource("The normalized SVG exceeds the configured source-byte ceiling.");

        while (!value.IsEmpty)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<byte[]> segments = _segments ?? throw new ObjectDisposedException(nameof(BoundedOutputStream));
            if (segments.Count == 0 || _lastSegmentLength == segments[^1].Length)
            {
                int capacity = checked((int)Math.Min(SvgSourceBuffer.SegmentSize, maximumBytes - _capacity));
                if (capacity <= 0)
                    throw SvgPreflightFailures.Resource("The normalized SVG exceeds the configured source-byte ceiling.");
                Action? allocationObserver = Interlocked.Exchange(
                    ref _beforeFirstSegmentAllocation,
                    null);
                allocationObserver?.Invoke();
                // Tests can deterministically cancel at the growth/allocation
                // boundary; cancellation still wins before any segment exists.
                cancellationToken.ThrowIfCancellationRequested();
                segments.Add(GC.AllocateUninitializedArray<byte>(capacity));
                _capacity += capacity;
                _lastSegmentLength = 0;
            }

            byte[] segment = segments[^1];
            int copyLength = Math.Min(value.Length, segment.Length - _lastSegmentLength);
            value[..copyLength].CopyTo(segment.AsSpan(_lastSegmentLength));
            _lastSegmentLength += copyLength;
            _length += copyLength;
            value = value[copyLength..];
        }
    }
}
