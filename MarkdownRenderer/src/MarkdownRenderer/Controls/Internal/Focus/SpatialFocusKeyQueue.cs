using System.Collections.Generic;
using Windows.System;
using MarkdownRenderer.Layout.Internal.Focus;

namespace MarkdownRenderer.Controls.Internal.Focus;

/// <summary>
/// Arrow, Home and End keys that arrived while focus geometry was not
/// readable. The queue remembers the item focus started from; once focus
/// moves elsewhere or the snapshot changes, the queued keys no longer apply.
/// Each clear starts a new epoch, so a key processed across a clear is not
/// dequeued twice.
/// </summary>
internal sealed class SpatialFocusKeyQueue
{
    private const int MaxKeys = 64;

    private readonly Queue<VirtualKey> _keys = new();
    private long _epoch;
    private bool _hasOrigin;
    private FocusableItem _origin;
    private long _generation;

    /// <summary>True while at least one key waits for geometry.</summary>
    internal bool HasPending => _keys.Count > 0;

    /// <summary>True when no more keys fit; further repeats stay unhandled.</summary>
    internal bool IsFull => _keys.Count >= MaxKeys;

    /// <summary>True once the item the queued keys started from is recorded.</summary>
    internal bool HasOrigin => _hasOrigin;

    /// <summary>The epoch the current keys were queued in.</summary>
    internal long Epoch => _epoch;

    /// <summary>The oldest queued key.</summary>
    internal VirtualKey Head => _keys.Peek();

    /// <summary>
    /// True when <paramref name="current"/> is still the item the keys
    /// started from, in snapshot <paramref name="generation"/>.
    /// </summary>
    internal bool IsOriginCurrent(FocusableItem? current, long generation) =>
        _generation == generation &&
        _hasOrigin &&
        current is { } item &&
        FocusableItemQueries.SameFocusableIdentity(item, _origin);

    /// <summary>Records the item the next queued keys start from.</summary>
    internal void SetOrigin(FocusableItem origin, long generation)
    {
        _origin = origin;
        _generation = generation;
        _hasOrigin = true;
    }

    internal void Enqueue(VirtualKey key) => _keys.Enqueue(key);

    /// <summary>
    /// Dequeues <paramref name="key"/> after it was processed, unless the
    /// queue was cleared or changed meanwhile. The next key starts from
    /// <paramref name="updatedOrigin"/>; without one the rest are dropped.
    /// </summary>
    internal void CompleteHead(VirtualKey key, long epoch, FocusableItem? updatedOrigin)
    {
        if (epoch != _epoch || _keys.Count == 0 || _keys.Peek() != key)
            return;

        _keys.Dequeue();
        if (_keys.Count == 0)
            _hasOrigin = false;
        else if (updatedOrigin is { } origin)
            _origin = origin;
        else
            Clear();
    }

    internal void Clear()
    {
        ++_epoch;
        _keys.Clear();
        _hasOrigin = false;
        _generation = 0;
    }
}
