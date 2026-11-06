using System;
using System.Collections.Generic;

namespace JitHub.Markdown.Svg;

/// <summary>One cached resource and its accounting in a <see cref="StrictResourceLeaseCache{TKey, TResource}"/>.</summary>
internal sealed class StrictResourceLeaseEntry<TKey, TResource>(TKey key, TResource resource, long byteCount)
    where TKey : notnull
    where TResource : class, IDisposable
{
    internal TKey Key { get; } = key;

    internal TResource Resource { get; } = resource;

    internal long ByteCount { get; } = byteCount;

    internal LinkedListNode<StrictResourceLeaseEntry<TKey, TResource>>? Node { get; set; }

    internal int LeaseCount { get; set; }

    internal bool IsCached { get; set; } = true;

    internal bool IsDisposed { get; set; }
}
