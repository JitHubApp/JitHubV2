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
    internal const int NativeCopyChunkBytes = 64 * 1024;

    internal static readonly SvgSourceBuffer Empty = new(
        new SvgSourceStorage(Array.Empty<byte>(), null, null, 0, 0),
        immortal: true);

    private readonly object _gate = new();
    private readonly bool _immortal;
    private SvgSourceStorage? _storage;
    private bool _sealed;

    private SvgSourceBuffer(SvgSourceStorage storage, bool immortal = false)
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

        SvgSourceNativeMemory? native = new(source.Length);
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
            var storage = new SvgSourceStorage(null, null, native, source.Length, source.Length);
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
            : new SvgSourceBuffer(new SvgSourceStorage(source, null, null, source.Length, source.Length));
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

        return new SvgSourceBuffer(new SvgSourceStorage(null, segments, null, length, allocatedCapacity));
    }

    public SvgSourceReadLease AcquireReadLease()
    {
        lock (_gate)
        {
            SvgSourceStorage storage = _storage ?? throw new ObjectDisposedException(nameof(SvgSourceBuffer));
            storage.AddReference();
            try
            {
                return new SvgSourceReadLease(storage);
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

            SvgSourceStorage storage = _storage ?? throw new ObjectDisposedException(nameof(SvgSourceBuffer));
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

        SvgSourceStorage? storage;
        lock (_gate)
        {
            storage = _storage;
            Volatile.Write(ref _storage, null);
        }

        storage?.ReleaseReference();
    }

    private SvgSourceStorage GetStorageUnderOwnerLock() =>
        _storage ?? throw new ObjectDisposedException(nameof(SvgSourceBuffer));
}
