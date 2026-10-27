using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.Layout.Boxes;

internal sealed class BoundedAsyncKeyedGate
{
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Entry[] _overflowEntries;
    private readonly int _maximumTrackedKeys;

    internal BoundedAsyncKeyedGate(
        int maximumTrackedKeys,
        int overflowStripeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTrackedKeys);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(overflowStripeCount);
        _maximumTrackedKeys = maximumTrackedKeys;
        _overflowEntries = new Entry[overflowStripeCount];
        for (int i = 0; i < overflowStripeCount; i++)
            _overflowEntries[i] = new Entry(null);
    }

    internal async ValueTask<Entry> AcquireAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                Entry overflow = _overflowEntries[GetOverflowStripe(key)];
                if (_entries.Count < _maximumTrackedKeys && overflow.ReferenceCount == 0)
                {
                    entry = new Entry(key);
                    _entries.Add(key, entry);
                }
                else
                    entry = overflow;
            }
            checked { entry.ReferenceCount++; }
        }

        bool acquired = false;
        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            cancellationToken.ThrowIfCancellationRequested();
            return entry;
        }
        catch
        {
            ReleaseReference(entry, acquired);
            throw;
        }
    }

    private int GetOverflowStripe(string key) =>
        (StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % _overflowEntries.Length;

    internal void Release(ref Entry? entry)
    {
        Entry? acquiredEntry = Interlocked.Exchange(ref entry, null);
        if (acquiredEntry is not null)
            ReleaseReference(acquiredEntry, releasePermit: true);
    }

    private void ReleaseReference(Entry entry, bool releasePermit)
    {
        if (releasePermit)
            entry.Gate.Release();
        bool dispose = false;
        lock (_sync)
        {
            if (entry.Key is null)
            {
                entry.ReferenceCount--;
                return;
            }
            if (--entry.ReferenceCount == 0)
            {
                _entries.Remove(entry.Key);
                dispose = true;
            }
        }
        if (dispose)
            entry.Gate.Dispose();
    }

    internal sealed class Entry(string? key)
    {
        internal readonly string? Key = key;
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int ReferenceCount;
    }
}
