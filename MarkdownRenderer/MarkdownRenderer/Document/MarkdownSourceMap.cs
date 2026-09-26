using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;

namespace MarkdownRenderer.Document;

/// <summary>
/// Maps rendered document positions back to exact markdown source spans.
/// </summary>
public sealed class MarkdownSourceMap
{
    private readonly Dictionary<int, BlockEntryBounds> _blockEntryBounds = new();
    private readonly List<Entry> _entries = new();
    private readonly object _gate = new();
    private readonly string _sourceText;
    private EntryIndex? _entryIndex;
    private bool _entriesWereAddedInOrder = true;

    /// <summary>Initializes a source map for the supplied markdown source.</summary>
    public MarkdownSourceMap(string sourceText)
    {
        _sourceText = sourceText ?? string.Empty;
    }

    /// <summary>Gets the markdown source text associated with this map.</summary>
    public string SourceText => _sourceText;

    /// <summary>Adds a mapping from a rendered inline run to a source span.</summary>
    public void Add(int blockIndex, int inlineIndex, int renderedLength, SourceSpan span)
        => AddCore(blockIndex, inlineIndex, renderedLength, span, projection: null);

    /// <summary>
    /// Adds a mapping whose rendered text can be aligned with escaped characters,
    /// entities, emoji shortcodes, and source-only markdown delimiters.
    /// </summary>
    internal void Add(int blockIndex, int inlineIndex, int renderedLength, SourceSpan span, string renderedText)
    {
        SourceProjection? projection = SourceProjection.TryCreate(_sourceText, span, renderedText);
        AddCore(blockIndex, inlineIndex, renderedLength, span, projection);
    }

    private void AddCore(
        int blockIndex,
        int inlineIndex,
        int renderedLength,
        SourceSpan span,
        SourceProjection? projection)
    {
        lock (_gate)
        {
            int insertionOrder = _entries.Count;
            var entry = new Entry(
                blockIndex,
                inlineIndex,
                renderedLength,
                span,
                SourcePrefixLength: 0,
                SourceSuffixLength: 0,
                Projection: projection,
                InsertionOrder: insertionOrder);

            if (_entriesWereAddedInOrder && insertionOrder > 0 &&
                CompareDocumentOrder(_entries[insertionOrder - 1], entry) > 0)
            {
                _entriesWereAddedInOrder = false;
            }

            _entries.Add(entry);
            TrackBlockEntry(entry, insertionOrder);
            _entryIndex = null;
        }
    }

    /// <summary>
    /// Expands the first/last source-map entries for a block to include source-only
    /// prefix/suffix characters such as heading markers.
    /// </summary>
    internal void AddSourceAffixesToBlock(int blockIndex, int sourceStart, int sourceEnd)
    {
        lock (_gate)
        {
            if (!_blockEntryBounds.TryGetValue(blockIndex, out var bounds))
                return;

            sourceStart = Math.Clamp(sourceStart, 0, _sourceText.Length);
            sourceEnd = Math.Clamp(sourceEnd, sourceStart, _sourceText.Length);

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

            if (prefixLength > 0 || suffixLength > 0)
                _entryIndex = null;
        }
    }

    /// <summary>Returns the exact markdown source slice covered by a rendered document range.</summary>
    public string Slice(DocumentRange range)
        => TryMapRange(range, out var sourceSpan)
            ? _sourceText.Substring(sourceSpan.Start, sourceSpan.Length)
            : string.Empty;

    /// <summary>
    /// Maps a rendered document range to one exact half-open UTF-16 source span.
    /// </summary>
    public bool TryMapRange(DocumentRange range, out SourceSpan sourceSpan)
    {
        range = range.Normalized();
        if (range.IsEmpty)
        {
            sourceSpan = SourceSpan.Empty;
            return false;
        }

        // Entries are indexed once in document order. Locate the two selection
        // boundaries independently: the source between them is copied verbatim,
        // so visiting every intervening entry would only add linear work.
        Entry[] entries = GetEntryIndex().Entries;
        int searchStart = LowerBound(entries, range.Start.BlockIndex, range.Start.InlineIndex);
        int searchEnd = range.End.CharacterOffset <= 0
            ? LowerBound(entries, range.End.BlockIndex, range.End.InlineIndex)
            : UpperBound(entries, range.End.BlockIndex, range.End.InlineIndex);

        if (searchStart >= searchEnd ||
            !TryFindFirstHit(entries, searchStart, searchEnd, range, out int firstIndex, out int firstFromOffset) ||
            !TryFindLastHit(entries, firstIndex, searchEnd, range, out int lastIndex, out int lastToOffset))
        {
            sourceSpan = SourceSpan.Empty;
            return false;
        }

        Entry firstHit = entries[firstIndex];
        Entry lastHit = entries[lastIndex];
        int sourceStart = ClampSourceOffset(firstHit.Span.Start, firstFromOffset, 0);
        int sourceEnd = ClampSourceOffset(lastHit.Span.Start, lastToOffset, sourceStart);
        sourceSpan = new SourceSpan(sourceStart, sourceEnd - sourceStart);
        return true;
    }

    /// <summary>
    /// Maps a rendered-text offset within an entry to a source offset within the
    /// entry's source span. Exact when render-length matches span-length;
    /// otherwise proportional.
    /// </summary>
    private static int ProjectStartOffset(Entry entry, int renderedOffset)
    {
        if (entry.SourcePrefixLength == 0 && entry.SourceSuffixLength == 0)
            return entry.Projection?.ProjectStart(renderedOffset)
                ?? ProjectOffset(entry, renderedOffset);

        if (renderedOffset <= 0)
            return 0;

        return entry.SourcePrefixLength + ProjectContentStartOffset(entry, renderedOffset);
    }

    private static int ProjectEndOffset(Entry entry, int renderedOffset)
    {
        if (entry.SourcePrefixLength == 0 && entry.SourceSuffixLength == 0)
            return entry.Projection?.ProjectEnd(renderedOffset)
                ?? ProjectOffset(entry, renderedOffset);

        if (renderedOffset >= entry.RenderedLength)
            return entry.Span.Length;

        return entry.SourcePrefixLength + ProjectContentEndOffset(entry, renderedOffset);
    }

    private static int ProjectContentStartOffset(Entry entry, int renderedOffset)
        => entry.Projection?.ProjectStart(renderedOffset)
            ?? ProjectAffixedContentOffset(entry, renderedOffset);

    private static int ProjectContentEndOffset(Entry entry, int renderedOffset)
        => entry.Projection?.ProjectEnd(renderedOffset)
            ?? ProjectAffixedContentOffset(entry, renderedOffset);

    private static int ProjectAffixedContentOffset(Entry entry, int renderedOffset)
    {
        int contentLength = GetContentSourceLength(entry);
        if (entry.RenderedLength <= 0)
            return 0;

        int contentOffset = entry.RenderedLength == contentLength
            ? renderedOffset
            : (int)Math.Round(renderedOffset * (double)contentLength / Math.Max(1, entry.RenderedLength));
        return Math.Clamp(contentOffset, 0, contentLength);
    }

    private static int GetContentSourceLength(Entry entry)
    {
        int prefixLength = Math.Clamp(entry.SourcePrefixLength, 0, entry.Span.Length);
        int suffixLength = Math.Clamp(entry.SourceSuffixLength, 0, entry.Span.Length - prefixLength);
        return Math.Max(0, entry.Span.Length - prefixLength - suffixLength);
    }

    private static int ProjectOffset(Entry entry, int renderedOffset)
    {
        if (entry.RenderedLength <= 0)
            return 0;
        if (entry.RenderedLength == entry.Span.Length)
            return renderedOffset;

        double scale = (double)entry.Span.Length / Math.Max(1, entry.RenderedLength);
        return (int)Math.Round(renderedOffset * scale);
    }

    private int ClampSourceOffset(int spanStart, int relativeOffset, int minimum)
    {
        long absoluteOffset = (long)spanStart + relativeOffset;
        return (int)Math.Clamp(absoluteOffset, minimum, (long)_sourceText.Length);
    }

    private void TrackBlockEntry(Entry entry, int entryIndex)
    {
        if (entry.Span.IsEmpty)
            return;

        if (!_blockEntryBounds.TryGetValue(entry.BlockIndex, out var bounds))
        {
            _blockEntryBounds.Add(entry.BlockIndex, new BlockEntryBounds(entryIndex, entryIndex));
            return;
        }

        int firstEntryIndex = bounds.FirstEntryIndex;
        if (CompareDocumentOrder(entry, _entries[firstEntryIndex]) < 0)
            firstEntryIndex = entryIndex;

        int lastEntryIndex = bounds.LastEntryIndex;
        if (CompareDocumentOrder(entry, _entries[lastEntryIndex]) > 0)
            lastEntryIndex = entryIndex;

        _blockEntryBounds[entry.BlockIndex] = new BlockEntryBounds(firstEntryIndex, lastEntryIndex);
    }

    private EntryIndex GetEntryIndex()
    {
        lock (_gate)
        {
            if (_entryIndex is not null)
                return _entryIndex;

            Entry[] sortedEntries = _entries.ToArray();
            if (!_entriesWereAddedInOrder)
                Array.Sort(sortedEntries, EntryDocumentOrderComparer.Instance);

            _entryIndex = new EntryIndex(sortedEntries);
            return _entryIndex;
        }
    }

    private static bool TryFindFirstHit(
        Entry[] entries,
        int searchStart,
        int searchEnd,
        DocumentRange range,
        out int entryIndex,
        out int projectedOffset)
    {
        for (int i = searchStart; i < searchEnd; i++)
        {
            if (!TryGetSelectedOffsets(entries[i], range, out int from, out _))
                continue;

            entryIndex = i;
            projectedOffset = ProjectStartOffset(entries[i], from);
            return true;
        }

        entryIndex = -1;
        projectedOffset = 0;
        return false;
    }

    private static bool TryFindLastHit(
        Entry[] entries,
        int searchStart,
        int searchEnd,
        DocumentRange range,
        out int entryIndex,
        out int projectedOffset)
    {
        for (int i = searchEnd - 1; i >= searchStart; i--)
        {
            if (!TryGetSelectedOffsets(entries[i], range, out _, out int to))
                continue;

            entryIndex = i;
            projectedOffset = ProjectEndOffset(entries[i], to);
            return true;
        }

        entryIndex = -1;
        projectedOffset = 0;
        return false;
    }

    private static bool TryGetSelectedOffsets(Entry entry, DocumentRange range, out int from, out int to)
    {
        var startPosition = new DocumentPosition(entry.BlockIndex, entry.InlineIndex, 0);
        var endPosition = new DocumentPosition(entry.BlockIndex, entry.InlineIndex, entry.RenderedLength);
        if (endPosition <= range.Start || startPosition >= range.End)
        {
            from = 0;
            to = 0;
            return false;
        }

        from = 0;
        to = entry.RenderedLength;
        if (range.Start.BlockIndex == entry.BlockIndex && range.Start.InlineIndex == entry.InlineIndex)
            from = Math.Max(0, range.Start.CharacterOffset);
        if (range.End.BlockIndex == entry.BlockIndex && range.End.InlineIndex == entry.InlineIndex)
            to = Math.Min(entry.RenderedLength, range.End.CharacterOffset);

        return to > from;
    }

    private static int LowerBound(Entry[] entries, int blockIndex, int inlineIndex)
    {
        int low = 0;
        int high = entries.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (CompareDocumentKey(entries[middle], blockIndex, inlineIndex) < 0)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static int UpperBound(Entry[] entries, int blockIndex, int inlineIndex)
    {
        int low = 0;
        int high = entries.Length;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (CompareDocumentKey(entries[middle], blockIndex, inlineIndex) <= 0)
                low = middle + 1;
            else
                high = middle;
        }

        return low;
    }

    private static int CompareDocumentKey(Entry entry, int blockIndex, int inlineIndex)
    {
        int comparison = entry.BlockIndex.CompareTo(blockIndex);
        return comparison != 0 ? comparison : entry.InlineIndex.CompareTo(inlineIndex);
    }

    private static int CompareDocumentOrder(Entry left, Entry right)
    {
        int comparison = left.BlockIndex.CompareTo(right.BlockIndex);
        if (comparison != 0)
            return comparison;

        comparison = left.InlineIndex.CompareTo(right.InlineIndex);
        return comparison != 0 ? comparison : left.InsertionOrder.CompareTo(right.InsertionOrder);
    }

    internal IReadOnlyList<Entry> Entries => GetEntryIndex().ReadOnlyEntries;

    internal readonly record struct Entry(
        int BlockIndex,
        int InlineIndex,
        int RenderedLength,
        SourceSpan Span,
        int SourcePrefixLength,
        int SourceSuffixLength,
        SourceProjection? Projection,
        int InsertionOrder);

    /// <summary>
    /// Piecewise UTF-16 projection for a transformed inline run. Each rendered
    /// code unit owns an exact half-open source interval. Atomic transformations
    /// (for example <c>&amp;amp;</c> or an emoji shortcode) deliberately assign the
    /// same source interval to every UTF-16 unit they produce.
    /// </summary>
    internal sealed class SourceProjection
    {
        private const int MaxRenderedLength = 16 * 1024;
        private const int MaxSourceLength = 32 * 1024;
        private const int MaxEntityLength = 64;
        private const int MaxEmojiShortcodeLength = 64;

        private readonly int[] _unitSourceStarts;
        private readonly int[] _unitSourceEnds;

        private SourceProjection(int[] unitSourceStarts, int[] unitSourceEnds)
        {
            _unitSourceStarts = unitSourceStarts;
            _unitSourceEnds = unitSourceEnds;
        }

        internal int ProjectStart(int renderedOffset)
        {
            if (_unitSourceStarts.Length == 0 || renderedOffset <= 0)
                return 0;
            if (renderedOffset >= _unitSourceStarts.Length)
                return _unitSourceEnds[^1];

            return _unitSourceStarts[renderedOffset];
        }

        internal int ProjectEnd(int renderedOffset)
        {
            if (_unitSourceEnds.Length == 0 || renderedOffset <= 0)
                return 0;
            if (renderedOffset >= _unitSourceEnds.Length)
                return _unitSourceEnds[^1];

            return _unitSourceEnds[renderedOffset - 1];
        }

        internal static SourceProjection? TryCreate(string sourceText, SourceSpan span, string renderedText)
        {
            if (string.IsNullOrEmpty(renderedText) ||
                span.Start < 0 ||
                span.Length <= 0 ||
                span.End > sourceText.Length ||
                renderedText.Length > MaxRenderedLength ||
                span.Length > MaxSourceLength)
            {
                return null;
            }

            ReadOnlySpan<char> source = sourceText.AsSpan(span.Start, span.Length);
            if (source.SequenceEqual(renderedText.AsSpan()))
                return null;

            var starts = new int[renderedText.Length];
            var ends = new int[renderedText.Length];
            int sourceCursor = 0;
            int renderedCursor = 0;

            while (renderedCursor < renderedText.Length)
            {
                if (!TryFindNextEmission(source, sourceCursor, renderedText, renderedCursor, out Emission emission))
                {
                    // A single visual scalar is intrinsically atomic even when its
                    // producer is host-defined (for example a custom emoji map).
                    if (renderedCursor == 0 && GetScalarLength(renderedText, 0) == renderedText.Length)
                    {
                        Array.Fill(starts, 0);
                        Array.Fill(ends, source.Length);
                        return new SourceProjection(starts, ends);
                    }

                    return null;
                }

                for (int index = 0; index < emission.RenderedLength; index++)
                {
                    starts[renderedCursor + index] = emission.SourceStart;
                    ends[renderedCursor + index] = emission.SourceEnd;
                }

                sourceCursor = emission.SourceEnd;
                renderedCursor += emission.RenderedLength;
            }

            // Source-only opening and closing syntax belongs to the corresponding
            // edge selection, while interior selections remain delimiter-free.
            starts[0] = 0;
            ends[^1] = source.Length;

            if (!IsValidProjection(starts, ends, source.Length))
                return null;

            return new SourceProjection(starts, ends);
        }

        private static bool TryFindNextEmission(
            ReadOnlySpan<char> source,
            int sourceCursor,
            string rendered,
            int renderedCursor,
            out Emission emission)
        {
            for (int sourceIndex = sourceCursor; sourceIndex < source.Length; sourceIndex++)
            {
                if (TryMatchEntity(source, sourceIndex, rendered, renderedCursor, out emission) ||
                    TryMatchEscape(source, sourceIndex, rendered, renderedCursor, out emission) ||
                    TryMatchEmojiShortcode(source, sourceIndex, rendered, renderedCursor, out emission))
                {
                    return true;
                }

                if (source[sourceIndex] == rendered[renderedCursor])
                {
                    emission = new Emission(sourceIndex, sourceIndex + 1, 1);
                    return true;
                }
            }

            emission = default;
            return false;
        }

        private static bool TryMatchEscape(
            ReadOnlySpan<char> source,
            int sourceIndex,
            string rendered,
            int renderedCursor,
            out Emission emission)
        {
            if (source[sourceIndex] == '\\' &&
                sourceIndex + 1 < source.Length &&
                source[sourceIndex + 1] == rendered[renderedCursor])
            {
                emission = new Emission(sourceIndex, sourceIndex + 2, 1);
                return true;
            }

            emission = default;
            return false;
        }

        private static bool TryMatchEntity(
            ReadOnlySpan<char> source,
            int sourceIndex,
            string rendered,
            int renderedCursor,
            out Emission emission)
        {
            if (source[sourceIndex] != '&')
            {
                emission = default;
                return false;
            }

            int searchLength = Math.Min(MaxEntityLength, source.Length - sourceIndex);
            int relativeEnd = source.Slice(sourceIndex, searchLength).IndexOf(';');
            if (relativeEnd <= 1)
            {
                emission = default;
                return false;
            }

            int sourceLength = relativeEnd + 1;
            string encoded = source.Slice(sourceIndex, sourceLength).ToString();
            string decoded = WebUtility.HtmlDecode(encoded);
            if (decoded.Length == 0 ||
                string.Equals(decoded, encoded, StringComparison.Ordinal) ||
                renderedCursor + decoded.Length > rendered.Length ||
                !rendered.AsSpan(renderedCursor, decoded.Length).SequenceEqual(decoded.AsSpan()))
            {
                emission = default;
                return false;
            }

            emission = new Emission(sourceIndex, sourceIndex + sourceLength, decoded.Length);
            return true;
        }

        private static bool TryMatchEmojiShortcode(
            ReadOnlySpan<char> source,
            int sourceIndex,
            string rendered,
            int renderedCursor,
            out Emission emission)
        {
            if (source[sourceIndex] != ':')
            {
                emission = default;
                return false;
            }

            int scalarLength = GetScalarLength(rendered, renderedCursor);
            int scalarValue = scalarLength == 2
                ? char.ConvertToUtf32(rendered[renderedCursor], rendered[renderedCursor + 1])
                : rendered[renderedCursor];
            if (scalarValue <= 0x7F)
            {
                emission = default;
                return false;
            }

            int maximumEnd = Math.Min(source.Length, sourceIndex + MaxEmojiShortcodeLength);
            int end = sourceIndex + 1;
            while (end < maximumEnd && source[end] != ':')
            {
                char value = source[end];
                if (!(char.IsAsciiLetterOrDigit(value) || value is '_' or '+' or '-'))
                {
                    emission = default;
                    return false;
                }

                end++;
            }

            if (end <= sourceIndex + 1 || end >= maximumEnd || source[end] != ':')
            {
                emission = default;
                return false;
            }

            emission = new Emission(sourceIndex, end + 1, scalarLength);
            return true;
        }

        private static int GetScalarLength(string value, int index)
            => index + 1 < value.Length &&
               char.IsHighSurrogate(value[index]) &&
               char.IsLowSurrogate(value[index + 1])
                ? 2
                : 1;

        private static bool IsValidProjection(int[] starts, int[] ends, int sourceLength)
        {
            int previousStart = -1;
            int previousEnd = -1;
            for (int index = 0; index < starts.Length; index++)
            {
                if (starts[index] < 0 ||
                    ends[index] < starts[index] ||
                    ends[index] > sourceLength ||
                    starts[index] < previousStart ||
                    ends[index] < previousEnd)
                {
                    return false;
                }

                previousStart = starts[index];
                previousEnd = ends[index];
            }

            return true;
        }

        private readonly record struct Emission(int SourceStart, int SourceEnd, int RenderedLength);
    }

    private readonly record struct BlockEntryBounds(int FirstEntryIndex, int LastEntryIndex);

    private sealed class EntryDocumentOrderComparer : IComparer<Entry>
    {
        internal static EntryDocumentOrderComparer Instance { get; } = new();

        public int Compare(Entry x, Entry y) => CompareDocumentOrder(x, y);
    }

    private sealed class EntryIndex
    {
        internal EntryIndex(Entry[] entries)
        {
            Entries = entries;
            ReadOnlyEntries = Array.AsReadOnly(entries);
        }

        internal Entry[] Entries { get; }

        internal ReadOnlyCollection<Entry> ReadOnlyEntries { get; }
    }
}
