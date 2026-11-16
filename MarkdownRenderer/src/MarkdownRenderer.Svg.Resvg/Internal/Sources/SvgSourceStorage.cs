using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace MarkdownRenderer.Svg.Resvg.Internal.Sources;

/// <summary>
/// The reference-counted backing of one <see cref="SvgSourceBuffer"/>: a managed array,
/// bounded managed segments or private native memory, plus the resources retired with it.
/// </summary>
internal sealed class SvgSourceStorage
{
    private readonly object _gate = new();
    private byte[]? _array;
    private List<byte[]>? _segments;
    private SvgSourceNativeMemory? _native;
    // The source owns the process snapshot reservation and, in diagnostic
    // builds, one provider-ownership ticket. Keep these inline to avoid a
    // per-snapshot list allocation on the preflight hot path.
    private IDisposable? _retirementResource0;
    private IDisposable? _retirementResource1;
    private int _references = 1; // The SvgSourceBuffer owner.
    private bool _retirementStarted;
    private bool _retirementCompleted;

    public SvgSourceStorage(byte[]? array, List<byte[]>? segments, SvgSourceNativeMemory? native, int length, int allocatedCapacity)
    {
        _array = array;
        _segments = segments;
        _native = native;
        Length = length;
        AllocatedCapacity = allocatedCapacity;
    }

    public int Length { get; }

    public int AllocatedCapacity { get; }

    public bool IsNativeBacked => _native is not null;

    public bool IsReleased
    {
        get
        {
            lock (_gate)
            {
                return _retirementCompleted;
            }
        }
    }

    public void AddReference()
    {
        lock (_gate)
        {
            if (_retirementStarted)
            {
                throw new ObjectDisposedException(nameof(SvgSourceBuffer));
            }

            checked { _references++; }
        }
    }

    public void ReleaseReference()
    {
        SvgSourceNativeMemory? native = null;
        IDisposable? retirementResource0 = null;
        IDisposable? retirementResource1 = null;
        bool shouldRetire = false;
        lock (_gate)
        {
            if (_references <= 0)
            {
                throw new InvalidOperationException("SVG source storage reference count underflow.");
            }

            _references--;
            if (_references == 0)
            {
                shouldRetire = true;
                _retirementStarted = true;
                native = _native;
                _native = null;
                _array = null;
                _segments = null;
                retirementResource0 = _retirementResource0;
                retirementResource1 = _retirementResource1;
                _retirementResource0 = null;
                _retirementResource1 = null;
            }
        }

        if (!shouldRetire)
            return;

        Exception? retirementFailure = null;
        try
        {
            // Snapshot admission is returned only after private native bytes
            // have been freed. NativeMemory.Free itself is non-throwing.
            native?.Dispose();
        }
        catch (Exception exception)
        {
            retirementFailure = exception;
        }

        DisposeRetirementResource(retirementResource0, ref retirementFailure);
        DisposeRetirementResource(retirementResource1, ref retirementFailure);

        lock (_gate)
        {
            _retirementCompleted = true;
        }

        if (retirementFailure is not null)
        {
            ExceptionDispatchInfo.Capture(retirementFailure).Throw();
        }
    }

    private static void DisposeRetirementResource(IDisposable? resource, ref Exception? firstFailure)
    {
        if (resource is null)
            return;

        try
        {
            resource.Dispose();
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }
    }

    public void AttachRetirementResource(IDisposable resource)
    {
        lock (_gate)
        {
            if (_retirementStarted)
            {
                throw new ObjectDisposedException(nameof(SvgSourceBuffer));
            }

            if (ReferenceEquals(_retirementResource0, resource) || ReferenceEquals(_retirementResource1, resource))
            {
                throw new InvalidOperationException("A source retirement resource cannot be attached twice.");
            }

            if (_retirementResource0 is null)
            {
                _retirementResource0 = resource;
            }
            else if (_retirementResource1 is null)
            {
                _retirementResource1 = resource;
            }
            else
            {
                throw new InvalidOperationException("An SVG source cannot retain more than two retirement credits.");
            }
        }
    }

    public ReadOnlySpan<byte> GetSpanAt(int index)
    {
        if ((uint)index > (uint)Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (_array is { } array)
        {
            return array.AsSpan(index);
        }

        if (_native is { } native)
        {
            return native.GetReadOnlySpan(index, Length - index);
        }

        if (index == Length)
        {
            return ReadOnlySpan<byte>.Empty;
        }

        List<byte[]> segments = _segments ?? throw new ObjectDisposedException(nameof(SvgSourceBuffer));
        int segmentIndex = index / SvgSourceBuffer.SegmentSize;
        int segmentOffset = index % SvgSourceBuffer.SegmentSize;
        int count = Math.Min(segments[segmentIndex].Length - segmentOffset, Length - index);
        return segments[segmentIndex].AsSpan(segmentOffset, count);
    }

    public byte GetByte(int index)
    {
        if ((uint)index >= (uint)Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        if (_array is { } array)
        {
            return array[index];
        }

        if (_native is { } native)
        {
            return native.GetReadOnlySpan(index, 1)[0];
        }

        List<byte[]> segments = _segments ?? throw new ObjectDisposedException(nameof(SvgSourceBuffer));
        return segments[index / SvgSourceBuffer.SegmentSize][index % SvgSourceBuffer.SegmentSize];
    }

    public bool StartsWithAt(int index, ReadOnlySpan<byte> value)
    {
        if ((uint)index > (uint)Length || value.Length > Length - index)
        {
            return false;
        }

        if (_array is { } array)
        {
            return array.AsSpan(index, value.Length).SequenceEqual(value);
        }

        if (_native is { } native)
        {
            return native.GetReadOnlySpan(index, value.Length).SequenceEqual(value);
        }

        List<byte[]> segments = _segments ?? throw new ObjectDisposedException(nameof(SvgSourceBuffer));
        for (int i = 0; i < value.Length; i++)
        {
            if (GetByte(i + index) != value[i])
            {
                return false;
            }
        }

        return true;
    }

    public int IndexOfSequence(int startIndex, ReadOnlySpan<byte> value)
    {
        if (startIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }

        if (startIndex > Length)
        {
            return -1;
        }

        if (value.IsEmpty)
        {
            return startIndex;
        }

        if (value.Length > Length - startIndex)
        {
            return -1;
        }

        if (_array is { } array)
        {
            int relative = array.AsSpan(startIndex).IndexOf(value);
            return relative < 0 ? -1 : startIndex + relative;
        }

        if (_native is { } native)
        {
            int relative = native.GetReadOnlySpan(startIndex, Length - startIndex).IndexOf(value);
            return relative < 0 ? -1 : startIndex + relative;
        }

        int lastStart = Length - value.Length;
        for (int index = startIndex; index <= lastStart; index++)
        {
            if (StartsWithAt(index, value))
            {
                return index;
            }
        }

        return -1;
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException("Destination is shorter than the SVG source.", nameof(destination));
        }

        if (_array is { } array)
        {
            array.AsSpan().CopyTo(destination);
            return;
        }

        if (_native is { } native)
        {
            native.GetReadOnlySpan(0, Length).CopyTo(destination);
            return;
        }

        List<byte[]> segments = _segments ?? throw new ObjectDisposedException(nameof(SvgSourceBuffer));
        int copied = 0;
        foreach (byte[] segment in segments)
        {
            int count = Math.Min(segment.Length, Length - copied);
            if (count <= 0)
                break;
            segment.AsSpan(0, count).CopyTo(destination[copied..]);
            copied += count;
        }
    }

    public byte[] ComputeSha256()
    {
        if (_array is { } array)
        {
            return SHA256.HashData(array);
        }

        if (_native is { } native)
        {
            return SHA256.HashData(native.GetReadOnlySpan(0, Length));
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        List<byte[]> segments = _segments ?? throw new ObjectDisposedException(nameof(SvgSourceBuffer));
        int remaining = Length;
        foreach (byte[] segment in segments)
        {
            int count = Math.Min(segment.Length, remaining);
            if (count <= 0)
                break;
            hash.AppendData(segment.AsSpan(0, count));
            remaining -= count;
        }

        return hash.GetHashAndReset();
    }

    public void FillRange(int startIndex, int length, byte value)
    {
        if (startIndex < 0 || length < 0 || startIndex > Length - length)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex));
        }

        lock (_gate)
        {
            if (_retirementStarted)
            {
                throw new ObjectDisposedException(nameof(SvgSourceBuffer));
            }

            if (_references != 1)
            {
                throw new InvalidOperationException("SVG source bytes cannot be mutated while read leases are active.");
            }

            if (_array is { } array)
            {
                array.AsSpan(startIndex, length).Fill(value);
                return;
            }

            if (_native is { } native)
            {
                native.GetSpan(startIndex, length).Fill(value);
                return;
            }

            List<byte[]> segments = _segments ?? throw new ObjectDisposedException(nameof(SvgSourceBuffer));
            int remaining = length;
            int absolute = startIndex;
            while (remaining > 0)
            {
                int segmentIndex = absolute / SvgSourceBuffer.SegmentSize;
                int segmentOffset = absolute % SvgSourceBuffer.SegmentSize;
                int count = Math.Min(remaining, segments[segmentIndex].Length - segmentOffset);
                segments[segmentIndex].AsSpan(segmentOffset, count).Fill(value);
                absolute += count;
                remaining -= count;
            }
        }
    }
}
