using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Owns an immutable SVG source snapshot. Larger snapshots use private native memory so
/// repeated preflight does not leave large managed arrays awaiting collection. A read
/// lease keeps that memory alive across owner retirement.
/// </summary>
internal sealed class SvgSourceBuffer : IDisposable
{
    internal const int SegmentSize = 64 * 1024;
    internal const int NativeMemoryThresholdBytes = 16 * 1024;
    private const int NativeCopyChunkBytes = 64 * 1024;

    internal static readonly SvgSourceBuffer Empty = new(
        new Storage(Array.Empty<byte>(), null, null, 0, 0),
        immortal: true);

    private readonly object _gate = new();
    private readonly bool _immortal;
    private Storage? _storage;
    private bool _sealed;

    private SvgSourceBuffer(Storage storage, bool immortal = false)
    {
        _storage = storage;
        _immortal = immortal;
    }

    public int Length => Volatile.Read(ref _storage)?.Length ?? 0;

    public int AllocatedCapacity => Volatile.Read(ref _storage)?.AllocatedCapacity ?? 0;

    internal bool IsNativeBacked => Volatile.Read(ref _storage)?.IsNativeBacked ?? false;

    public bool IsReleased => Volatile.Read(ref _storage) is null && !_immortal;

    public static SvgSourceBuffer CreateEmpty() => Empty;

    public static SvgSourceBuffer CopyFrom(ReadOnlySpan<byte> source, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (source.Length < NativeMemoryThresholdBytes)
        {
            byte[] copy = source.ToArray();
            cancellationToken.ThrowIfCancellationRequested();
            return TakeOwnedArray(copy);
        }

        NativeStorageHandle? native = new(source.Length);
        try
        {
            Span<byte> destination = native.GetSpan();
            for (int offset = 0; offset < source.Length; offset += NativeCopyChunkBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(NativeCopyChunkBytes, source.Length - offset);
                source.Slice(offset, count).CopyTo(destination.Slice(offset, count));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var storage = new Storage(null, null, native, source.Length, source.Length);
            var snapshot = new SvgSourceBuffer(storage);
            native = null;
            return snapshot;
        }
        finally
        {
            native?.Dispose();
        }
    }

    /// <summary>Transfers array ownership without cloning; used by bounded normalization output.</summary>
    public static SvgSourceBuffer TakeOwnedArray(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.Length == 0
            ? Empty
            : new SvgSourceBuffer(new Storage(source, null, null, source.Length, source.Length));
    }

    public static SvgSourceBuffer TakeOwnedSegments(List<byte[]> segments, int length, int allocatedCapacity)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (length < 0 || allocatedCapacity < length)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (length == 0)
        {
            if (segments.Count != 0 || allocatedCapacity != 0)
            {
                throw new ArgumentException("An empty source cannot retain segment capacity.", nameof(segments));
            }

            return Empty;
        }

        if (segments.Count == 0)
        {
            throw new ArgumentException("A non-empty source requires at least one backing segment.", nameof(segments));
        }

        int computedCapacity = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            byte[] segment = segments[i] ?? throw new ArgumentException("A source segment cannot be null.", nameof(segments));
            if (segment.Length == 0 || segment.Length > SegmentSize)
            {
                throw new ArgumentException("A source segment must have a canonical bounded length.", nameof(segments));
            }

            if (i < segments.Count - 1 && segment.Length != SegmentSize)
            {
                throw new ArgumentException("Only the final source segment may be partial.", nameof(segments));
            }

            computedCapacity = checked(computedCapacity + segment.Length);
        }

        int precedingCapacity = segments.Count == 1 ? 0 : computedCapacity - segments[^1].Length;
        if (computedCapacity != allocatedCapacity || length > computedCapacity || length <= precedingCapacity)
        {
            throw new ArgumentException("Source logical length and allocated segment capacity do not match the declared bounds.", nameof(segments));
        }

        return new SvgSourceBuffer(new Storage(null, segments, null, length, allocatedCapacity));
    }

    public ReadLease AcquireReadLease()
    {
        lock (_gate)
        {
            Storage storage = _storage ?? throw new ObjectDisposedException(nameof(SvgSourceBuffer));
            storage.AddReference();
            try
            {
                return new ReadLease(storage);
            }
            catch
            {
                storage.ReleaseReference();
                throw;
            }
        }
    }

    public void CopyTo(Span<byte> destination)
    {
        lock (_gate)
        {
            GetStorageUnderOwnerLock().CopyTo(destination);
        }
    }

    public byte GetByte(int index)
    {
        lock (_gate)
        {
            return GetStorageUnderOwnerLock().GetByte(index);
        }
    }

    public bool StartsWithAt(int index, ReadOnlySpan<byte> value)
    {
        lock (_gate)
        {
            return GetStorageUnderOwnerLock().StartsWithAt(index, value);
        }
    }

    public int IndexOfSequence(int startIndex, ReadOnlySpan<byte> value)
    {
        lock (_gate)
        {
            return GetStorageUnderOwnerLock().IndexOfSequence(startIndex, value);
        }
    }

    public void FillRange(int startIndex, int length, byte value)
    {
        lock (_gate)
        {
            if (_sealed)
            {
                throw new InvalidOperationException("A sealed SVG source snapshot is immutable.");
            }

            Storage storage = _storage ?? throw new ObjectDisposedException(nameof(SvgSourceBuffer));
            storage.FillRange(startIndex, length, value);
        }
    }

    public byte[] ComputeSha256()
    {
        lock (_gate)
        {
            return GetStorageUnderOwnerLock().ComputeSha256();
        }
    }

    public void Seal()
    {
        lock (_gate)
        {
            if (_storage is null)
            {
                throw new ObjectDisposedException(nameof(SvgSourceBuffer));
            }

            _sealed = true;
        }
    }

    internal void AttachRetirementResource(IDisposable resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (_immortal)
        {
            throw new InvalidOperationException("Retirement resources cannot be attached to the immortal empty source.");
        }

        lock (_gate)
        {
            if (!_sealed)
            {
                throw new InvalidOperationException("Retirement credits can only be attached to a sealed SVG snapshot.");
            }

            GetStorageUnderOwnerLock().AttachRetirementResource(resource);
        }
    }

    public void Dispose()
    {
        if (_immortal)
        {
            return;
        }

        Storage? storage;
        lock (_gate)
        {
            storage = _storage;
            Volatile.Write(ref _storage, null);
        }

        storage?.ReleaseReference();
    }

    private Storage GetStorageUnderOwnerLock() =>
        _storage ?? throw new ObjectDisposedException(nameof(SvgSourceBuffer));

    internal sealed class ReadLease : IDisposable
    {
        private Storage? _storage;
        private readonly Storage _releasedState;

        internal ReadLease(Storage storage)
        {
            _storage = storage;
            _releasedState = storage;
        }

        internal int Length => GetStorage().Length;

        internal bool IsNativeBacked => GetStorage().IsNativeBacked;

        // Used by lifetime tests after Dispose; Storage drops its native handle at refcount zero.
        internal bool IsStorageReleased => _releasedState.IsReleased;

        /// <summary>
        /// Returns a contiguous view valid only while this lease remains undisposed.
        /// Consumers must not dispose this same lease concurrently with span use;
        /// owner disposal is safe because the lease independently retains storage.
        /// </summary>
        internal ReadOnlySpan<byte> GetSpanAt(int index) => GetStorage().GetSpanAt(index);

        internal byte GetByte(int index) => GetStorage().GetByte(index);

        internal bool StartsWithAt(int index, ReadOnlySpan<byte> value) => GetStorage().StartsWithAt(index, value);

        internal int IndexOfSequence(int startIndex, ReadOnlySpan<byte> value) =>
            GetStorage().IndexOfSequence(startIndex, value);

        internal void CopyTo(Span<byte> destination) => GetStorage().CopyTo(destination);

        internal byte[] ComputeSha256() => GetStorage().ComputeSha256();

        public void Dispose()
        {
            Interlocked.Exchange(ref _storage, null)?.ReleaseReference();
        }

        private Storage GetStorage() =>
            Volatile.Read(ref _storage) ?? throw new ObjectDisposedException(nameof(ReadLease));
    }

    internal sealed class Storage
    {
        private readonly object _gate = new();
        private byte[]? _array;
        private List<byte[]>? _segments;
        private NativeStorageHandle? _native;
        // The source owns the process snapshot reservation and, in diagnostic
        // builds, one provider-ownership ticket. Keep these inline to avoid a
        // per-snapshot list allocation on the preflight hot path.
        private IDisposable? _retirementResource0;
        private IDisposable? _retirementResource1;
        private int _references = 1; // The SvgSourceBuffer owner.
        private bool _retirementStarted;
        private bool _retirementCompleted;

        public Storage(byte[]? array, List<byte[]>? segments, NativeStorageHandle? native, int length, int allocatedCapacity)
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
            NativeStorageHandle? native = null;
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
            int segmentIndex = index / SegmentSize;
            int segmentOffset = index % SegmentSize;
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
            return segments[index / SegmentSize][index % SegmentSize];
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
                    int segmentIndex = absolute / SegmentSize;
                    int segmentOffset = absolute % SegmentSize;
                    int count = Math.Min(remaining, segments[segmentIndex].Length - segmentOffset);
                    segments[segmentIndex].AsSpan(segmentOffset, count).Fill(value);
                    absolute += count;
                    remaining -= count;
                }
            }
        }
    }

    internal sealed unsafe class NativeStorageHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeStorageHandle(int length)
            : base(ownsHandle: true)
        {
            if (length <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            void* allocation = NativeMemory.Alloc((nuint)length);
            if (allocation is null)
            {
                throw new OutOfMemoryException();
            }

            ByteLength = length;
            SetHandle((nint)allocation);
        }

        public int ByteLength { get; }

        public Span<byte> GetSpan() => new((void*)DangerousGetHandle(), ByteLength);

        public Span<byte> GetSpan(int startIndex, int length)
        {
            ValidateRange(startIndex, length);
            return new Span<byte>((byte*)DangerousGetHandle() + startIndex, length);
        }

        public ReadOnlySpan<byte> GetReadOnlySpan(int startIndex, int length)
        {
            ValidateRange(startIndex, length);
            return new ReadOnlySpan<byte>((byte*)DangerousGetHandle() + startIndex, length);
        }

        protected override bool ReleaseHandle()
        {
            NativeMemory.Free((void*)handle);
            return true;
        }

        private void ValidateRange(int startIndex, int length)
        {
            if (startIndex < 0 || length < 0 || startIndex > ByteLength - length)
            {
                throw new ArgumentOutOfRangeException(nameof(startIndex));
            }
        }
    }
}
