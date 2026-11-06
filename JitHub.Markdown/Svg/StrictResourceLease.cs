using System;

namespace JitHub.Markdown.Svg;

/// <summary>
/// A lease on one resource in a <see cref="StrictResourceLeaseCache{TKey, TResource}"/>;
/// the resource stays charged to the cache budget until every lease is disposed.
/// </summary>
/// <remarks>
/// The class is <c>partial</c> only so CsWinRT can generate its ABI projection of IDisposable.
/// It is a top-level type rather than nested in the cache because the public API analyzer
/// treats references to types nested in a generic type as nullable-oblivious.
/// </remarks>
public sealed partial class StrictResourceLease<TKey, TResource> : IDisposable
    where TKey : notnull
    where TResource : class, IDisposable
{
    private StrictResourceLeaseCache<TKey, TResource>? _owner;
    private StrictResourceLeaseEntry<TKey, TResource>? _entry;

    internal StrictResourceLease(
        StrictResourceLeaseCache<TKey, TResource> owner,
        StrictResourceLeaseEntry<TKey, TResource> entry)
    {
        _owner = owner;
        _entry = entry;
    }

    /// <summary>Gets the leased resource.</summary>
    /// <exception cref="ObjectDisposedException">This lease has been disposed.</exception>
    public TResource Resource =>
        _entry?.Resource ?? throw new ObjectDisposedException(nameof(StrictResourceLease<TKey, TResource>));

    public void Dispose()
    {
        StrictResourceLeaseCache<TKey, TResource>? owner = _owner;
        StrictResourceLeaseEntry<TKey, TResource>? entry = _entry;
        _owner = null;
        _entry = null;
        if (owner is not null && entry is not null)
        {
            owner.Release(entry);
        }
    }
}
