using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace MarkdownRenderer.Svg.Resvg.Internal.Sources;

/// <summary>A counted read borrow of one <see cref="SvgSourceBuffer"/> storage that outlives owner retirement.</summary>
internal sealed class SvgSourceReadLease : IDisposable
{
    private SvgSourceStorage? _storage;
    private readonly SvgSourceStorage _releasedState;

    internal SvgSourceReadLease(SvgSourceStorage storage)
    {
        _storage = storage;
        _releasedState = storage;
    }

    internal int Length => GetStorage().Length;

    internal bool IsNativeBacked => GetStorage().IsNativeBacked;

    // Used by lifetime tests after Dispose; SvgSourceStorage drops its native handle at refcount zero.
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

    private SvgSourceStorage GetStorage() =>
        Volatile.Read(ref _storage) ?? throw new ObjectDisposedException("ReadLease");
}
