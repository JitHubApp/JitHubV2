using System;

namespace JitHub.Markdown.Svg;

/// <summary>
/// Bytes reserved in a <see cref="StrictResourceLeaseCache{TKey, TResource}"/> before a
/// resource is created; disposing it returns whatever was not consumed by a store.
/// </summary>
/// <remarks>
/// The class is <c>partial</c> only so CsWinRT can generate its ABI projection of IDisposable.
/// It is a top-level type rather than nested in the cache because the public API analyzer
/// treats references to types nested in a generic type as nullable-oblivious.
/// </remarks>
public sealed partial class StrictResourceReservation<TKey, TResource> : IDisposable
    where TKey : notnull
    where TResource : class, IDisposable
{
    internal StrictResourceReservation(StrictResourceLeaseCache<TKey, TResource> owner, long byteCount)
    {
        Owner = owner;
        RemainingBytes = byteCount;
    }

    internal StrictResourceLeaseCache<TKey, TResource> Owner { get; }

    internal long RemainingBytes { get; set; }

    internal bool IsDisposed { get; set; }

    public void Dispose() => Owner.ReleaseReservation(this);
}
