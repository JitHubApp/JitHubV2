using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Markdig.Extensions.Footnotes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Navigation targets recorded while one layout pass is built: footnote
/// definition and reference blocks, stable footnote orders and fragment ids.
/// </summary>
internal sealed class LayoutNavigationTargets
{
    // Records the block indices of each footnote's definition and inline
    // reference so the control can scroll to either end of a back/forward link.
    // Key = footnote order (1-based); value = (defBlockIndex, refBlockIndex).
    // Written by FootnoteRenderer (def) and LayoutBuilder (ref) during build;
    // read by MarkdownRendererControl via LayoutSnapshot after build completes.
    // Concurrent because LayoutBuilder runs on a background thread.
    private readonly ConcurrentDictionary<int, int> _footnoteDefBlocks = new();
    private readonly ConcurrentDictionary<int, int> _footnoteRefBlocks = new();
    private readonly ConcurrentDictionary<string, int> _fragmentTargetBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<Footnote, int> _footnoteOrders = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<int> _reservedFootnoteOrders = new();
    private readonly object _footnoteOrderGate = new();
    private int _nextGeneratedFootnoteOrder;

    /// <summary>Records the block index of the definition for footnote <paramref name="order"/>.</summary>
    internal void RegisterFootnoteDef(int order, int blockIndex) => _footnoteDefBlocks[order] = blockIndex;

    /// <summary>Records the block index of the paragraph containing the inline citation for footnote <paramref name="order"/>.</summary>
    internal void RegisterFootnoteRef(int order, int blockIndex) => _footnoteRefBlocks[order] = blockIndex;

    /// <summary>Returns block index of the footnote definition, or null.</summary>
    internal int? GetFootnoteDefBlock(int order) => _footnoteDefBlocks.TryGetValue(order, out var v) ? v : null;

    /// <summary>Returns block index of the inline citation paragraph, or null.</summary>
    internal int? GetFootnoteRefBlock(int order) => _footnoteRefBlocks.TryGetValue(order, out var v) ? v : null;

    /// <summary>Snapshots the registry into plain dictionaries (safe to read off the build thread).</summary>
    internal (IReadOnlyDictionary<int,int> Defs, IReadOnlyDictionary<int,int> Refs) SnapshotFootnoteRegistry()
        => (new Dictionary<int, int>(_footnoteDefBlocks), new Dictionary<int, int>(_footnoteRefBlocks));

    /// <summary>
    /// Returns one stable display/navigation order for a Markdig footnote during
    /// the current layout pass. Markdig normally assigns <see cref="Footnote.Order"/>,
    /// but malformed or synthetic footnotes can arrive without one; generated
    /// fallback values are shared by references and definitions.
    /// </summary>
    internal int GetOrCreateFootnoteOrder(Footnote footnote, int fallbackHint = 0)
    {
        if (footnote is null)
            return Math.Max(1, fallbackHint);

        lock (_footnoteOrderGate)
        {
            if (_footnoteOrders.TryGetValue(footnote, out var existing))
                return existing;

            int order = footnote.Order > 0 ? footnote.Order : fallbackHint;
            if (order <= 0 || _reservedFootnoteOrders.Contains(order))
            {
                do { _nextGeneratedFootnoteOrder++; }
                while (_reservedFootnoteOrders.Contains(_nextGeneratedFootnoteOrder));
                order = _nextGeneratedFootnoteOrder;
            }

            _reservedFootnoteOrders.Add(order);
            _footnoteOrders[footnote] = order;
            return order;
        }
    }

    internal void RegisterFragmentTarget(string id, int blockIndex)
    {
        id = NormalizeFragmentId(id);
        if (id.Length > 0)
            _fragmentTargetBlocks[id] = blockIndex;
    }

    internal IReadOnlyDictionary<string, int> SnapshotFragmentTargets()
        => new Dictionary<string, int>(_fragmentTargetBlocks, StringComparer.Ordinal);

    private static string NormalizeFragmentId(string id)
    {
        id = id.Trim();
        if (id.StartsWith("#", StringComparison.Ordinal))
            id = id.Substring(1);
        return id;
    }
}
