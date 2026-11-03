using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Document;

/// <summary>
/// Entry storage for one <see cref="MarkdownSourceMap"/>: the insertion-ordered
/// entries, whether they arrived in document order, and the block bounds used
/// to attach source-only affixes. Not thread-safe; the owning map serializes
/// every call under its own gate.
/// </summary>
internal sealed class SourceMapEntryStore
{
    // Layout entries are normally appended in document order. Avoid building
    // this all-block index for that common case: source affixes can be located
    // directly in the ordered entry list. Preserve a dictionary fallback for
    // callers that append out-of-order keys.
    private Dictionary<int, BlockEntryBounds>? _blockEntryBounds;
    private readonly PagedEntryBuffer<MarkdownSourceMap.Entry> _entries = new();
    private bool _entriesWereAddedInOrder = true;

    /// <summary>Gets whether every entry so far was added in document order.</summary>
    internal bool EntriesWereAddedInOrder => _entriesWereAddedInOrder;

    /// <summary>
    /// Gets whether the all-block fallback index exists. Ordered additions
    /// never build it; the first affix request after an out-of-order
    /// addition does.
    /// </summary>
    internal bool HasBlockEntryIndex => _blockEntryBounds is not null;

    internal MarkdownSourceMap.Entry[] ToArray() => _entries.ToArray();

    internal void Add(
        int blockIndex,
        int inlineIndex,
        int renderedLength,
        SourceSpan span,
        MarkdownSourceMap.SourceProjection? projection)
    {
        int insertionOrder = _entries.Count;
        var entry = new MarkdownSourceMap.Entry(
            blockIndex,
            inlineIndex,
            renderedLength,
            span,
            SourcePrefixLength: 0,
            SourceSuffixLength: 0,
            Projection: projection,
            InsertionOrder: insertionOrder);

        if (_entriesWereAddedInOrder && insertionOrder > 0 &&
            SourceMapEntryOrderComparer.CompareDocumentOrder(_entries[insertionOrder - 1], entry) > 0)
        {
            _entriesWereAddedInOrder = false;
        }

        _entries.Add(entry);
        if (_blockEntryBounds is { } blockEntryBounds)
            TrackBlockEntry(blockEntryBounds, entry, insertionOrder);
    }

    /// <summary>
    /// Expands the first/last source-map entries for a block to include source-only
    /// prefix/suffix characters such as heading markers. Returns whether an
    /// entry span changed.
    /// </summary>
    internal bool AddSourceAffixesToBlock(int blockIndex, int sourceStart, int sourceEnd, int sourceTextLength)
    {
        BlockEntryBounds bounds;
        if (_blockEntryBounds is { } blockEntryBounds)
        {
            if (!blockEntryBounds.TryGetValue(blockIndex, out bounds))
                return false;
        }
        else if (_entriesWereAddedInOrder)
        {
            if (!TryFindOrderedBlockEntryBounds(blockIndex, out bounds))
                return false;
        }
        else
        {
            // Out-of-order public additions retain the legacy semantics.
            // Materialize once on the first affix request, then track all
            // later additions incrementally.
            blockEntryBounds = BuildBlockEntryBounds();
            _blockEntryBounds = blockEntryBounds;
            if (!blockEntryBounds.TryGetValue(blockIndex, out bounds))
                return false;
        }

        sourceStart = Math.Clamp(sourceStart, 0, sourceTextLength);
        sourceEnd = Math.Clamp(sourceEnd, sourceStart, sourceTextLength);

        var firstEntry = _entries[bounds.FirstEntryIndex];
        int prefixLength = Math.Max(0, firstEntry.Span.Start - sourceStart);
        if (prefixLength > 0)
        {
            firstEntry = firstEntry with
            {
                Span = new SourceSpan(sourceStart, firstEntry.Span.Length + prefixLength),
                SourcePrefixLength = firstEntry.SourcePrefixLength + prefixLength,
            };
            _entries[bounds.FirstEntryIndex] = firstEntry;
        }

        // Read the entry again because the first and last entry can be the same.
        var lastEntry = _entries[bounds.LastEntryIndex];
        int suffixLength = Math.Max(0, sourceEnd - lastEntry.Span.End);
        if (suffixLength > 0)
        {
            lastEntry = lastEntry with
            {
                Span = new SourceSpan(lastEntry.Span.Start, lastEntry.Span.Length + suffixLength),
                SourceSuffixLength = lastEntry.SourceSuffixLength + suffixLength,
            };
            _entries[bounds.LastEntryIndex] = lastEntry;
        }

        return prefixLength > 0 || suffixLength > 0;
    }

    private bool TryFindOrderedBlockEntryBounds(int blockIndex, out BlockEntryBounds bounds)
    {
        // _entriesWereAddedInOrder means the complete (block, inline,
        // insertion-order) key is monotone, so a block occupies one contiguous
        // range. Use allocation-free lower/upper bounds rather than retaining a
        // dictionary entry for every ordinary rendered run.
        int low = 0;
        int high = _entries.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (_entries[middle].BlockIndex < blockIndex)
                low = middle + 1;
            else
                high = middle;
        }

        int first = low;
        low = first;
        high = _entries.Count;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (_entries[middle].BlockIndex <= blockIndex)
                low = middle + 1;
            else
                high = middle;
        }

        int end = low;
        while (first < end && _entries[first].Span.IsEmpty)
            first++;
        while (end > first && _entries[end - 1].Span.IsEmpty)
            end--;

        if (first >= end)
        {
            bounds = default;
            return false;
        }

        bounds = new BlockEntryBounds(first, end - 1);
        return true;
    }

    private Dictionary<int, BlockEntryBounds> BuildBlockEntryBounds()
    {
        var blockEntryBounds = new Dictionary<int, BlockEntryBounds>();
        for (int index = 0; index < _entries.Count; index++)
            TrackBlockEntry(blockEntryBounds, _entries[index], index);
        return blockEntryBounds;
    }

    private void TrackBlockEntry(
        Dictionary<int, BlockEntryBounds> blockEntryBounds,
        MarkdownSourceMap.Entry entry,
        int entryIndex)
    {
        if (entry.Span.IsEmpty)
            return;

        if (!blockEntryBounds.TryGetValue(entry.BlockIndex, out var bounds))
        {
            blockEntryBounds.Add(entry.BlockIndex, new BlockEntryBounds(entryIndex, entryIndex));
            return;
        }

        int firstEntryIndex = bounds.FirstEntryIndex;
        if (SourceMapEntryOrderComparer.CompareDocumentOrder(entry, _entries[firstEntryIndex]) < 0)
            firstEntryIndex = entryIndex;

        int lastEntryIndex = bounds.LastEntryIndex;
        if (SourceMapEntryOrderComparer.CompareDocumentOrder(entry, _entries[lastEntryIndex]) > 0)
            lastEntryIndex = entryIndex;

        blockEntryBounds[entry.BlockIndex] = new BlockEntryBounds(firstEntryIndex, lastEntryIndex);
    }

    private readonly struct BlockEntryBounds(int firstEntryIndex, int lastEntryIndex)
    {
        internal readonly int FirstEntryIndex = firstEntryIndex;

        internal readonly int LastEntryIndex = lastEntryIndex;
    }
}
