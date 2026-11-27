using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Controls.Internal.Rendering.Tiles;

/// <summary>
/// The painted document tiles, by index, and whether each still shows the
/// current document. A stale tile keeps its pixels on screen until it is
/// repainted, so invalidation never exposes blank area. Evicted tiles are
/// kept as spares for reuse up to <see cref="MaximumSpares"/>.
/// </summary>
/// <typeparam name="TTile">The presentation object holding a tile's pixels.</typeparam>
internal sealed class DocumentTileCache<TTile>
    where TTile : class
{
    /// <summary>Evicted tiles kept for reuse instead of being released.</summary>
    internal const int MaximumSpares = 4;

    private readonly Dictionary<int, Entry> _entries = [];
    private readonly Stack<TTile> _spares = new();
    private readonly List<int> _scratch = [];

    /// <summary>Tiles currently assigned to an index.</summary>
    internal int Count => _entries.Count;

    /// <summary>Evicted tiles waiting for reuse.</summary>
    internal int SpareCount => _spares.Count;

    /// <summary>True when tile <paramref name="index"/> is missing or stale.</summary>
    internal bool NeedsPaint(int index) => !_entries.TryGetValue(index, out Entry? entry) || !entry.Current;

    /// <summary>The tile assigned to <paramref name="index"/>, current or stale.</summary>
    internal TTile? Find(int index) => _entries.TryGetValue(index, out Entry? entry) ? entry.Tile : null;

    /// <summary>Assigns <paramref name="tile"/> to <paramref name="index"/> without marking it current.</summary>
    internal void Assign(int index, TTile tile)
    {
        if (_entries.TryGetValue(index, out Entry? entry))
        {
            if (!ReferenceEquals(entry.Tile, tile))
                throw new InvalidOperationException($"Tile {index} is already assigned.");
            return;
        }

        _entries.Add(index, new Entry(tile));
    }

    /// <summary>Records that tile <paramref name="index"/> shows the current document.</summary>
    internal void MarkCurrent(int index)
    {
        if (_entries.TryGetValue(index, out Entry? entry))
            entry.Current = true;
    }

    /// <summary>Marks every tile stale, keeping its pixels until repainted.</summary>
    internal void MarkAllStale()
    {
        foreach (Entry entry in _entries.Values)
            entry.Current = false;
    }

    /// <summary>Marks stale every tile from <paramref name="firstIndex"/> through <paramref name="lastIndex"/>.</summary>
    internal void MarkStale(int firstIndex, int lastIndex = int.MaxValue)
    {
        foreach (KeyValuePair<int, Entry> pair in _entries)
        {
            if (pair.Key >= firstIndex && pair.Key <= lastIndex)
                pair.Value.Current = false;
        }
    }

    /// <summary>A recycled tile, or null when none is spare.</summary>
    internal TTile? TakeSpare() => _spares.TryPop(out TTile? tile) ? tile : null;

    /// <summary>
    /// Unassigns every tile <paramref name="plan"/> no longer covers, keeping
    /// up to <see cref="MaximumSpares"/> for reuse and handing the rest to
    /// <paramref name="release"/>. Each evicted tile is first passed to
    /// <paramref name="hide"/>.
    /// </summary>
    internal void Evict(DocumentTilePlan plan, Action<TTile> hide, Action<TTile> release)
    {
        // A document with nothing planned is far from the viewport; keeping
        // spares for it would hold memory for a scroll that may never come.
        int spareLimit = plan.Order.Count == 0 ? 0 : MaximumSpares;
        _scratch.Clear();
        foreach (int index in _entries.Keys)
        {
            if (!plan.Contains(index))
                _scratch.Add(index);
        }

        foreach (int index in _scratch)
        {
            TTile tile = _entries[index].Tile;
            _entries.Remove(index);
            hide(tile);
            if (_spares.Count < spareLimit)
                _spares.Push(tile);
            else
                release(tile);
        }

        if (spareLimit == 0)
            ReleaseSpares(release);
    }

    /// <summary>Hands every spare tile to <paramref name="release"/>.</summary>
    internal void ReleaseSpares(Action<TTile> release)
    {
        while (_spares.TryPop(out TTile? tile))
            release(tile);
    }

    /// <summary>Unassigns every tile and spare, handing each to <paramref name="release"/>.</summary>
    internal void Clear(Action<TTile> release)
    {
        foreach (Entry entry in _entries.Values)
            release(entry.Tile);
        _entries.Clear();
        ReleaseSpares(release);
    }

    private sealed class Entry(TTile tile)
    {
        internal TTile Tile { get; } = tile;

        internal bool Current { get; set; }
    }
}
