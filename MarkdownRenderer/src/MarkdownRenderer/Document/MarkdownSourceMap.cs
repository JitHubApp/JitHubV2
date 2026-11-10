using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using MarkdownRenderer.Document.Internal;

namespace MarkdownRenderer.Document;

/// <summary>
/// Maps rendered document positions back to exact markdown source spans.
/// </summary>
public sealed class MarkdownSourceMap
{
    private readonly SourceMapEntryStore _entries = new();
    private readonly object _gate = new();
    private readonly string _sourceText;
    private EntryIndex? _entryIndex;

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

    /// <summary>
    /// Adds a mapping with an exact compact projection supplied by a parser-aware
    /// producer. Unlike the general text aligner, this path never guesses how
    /// rendered characters correspond to source characters.
    /// </summary>
    internal void Add(
        int blockIndex,
        int inlineIndex,
        int renderedLength,
        SourceSpan span,
        SourceProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        if (projection.RenderedLength != renderedLength || projection.SourceLength != span.Length)
            throw new ArgumentException("The exact source projection does not match its rendered or source span.", nameof(projection));

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
            _entries.Add(blockIndex, inlineIndex, renderedLength, span, projection);
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
            if (_entries.AddSourceAffixesToBlock(blockIndex, sourceStart, sourceEnd, _sourceText.Length))
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
        int searchStart = SourceMapRangeSearch.LowerBound(entries, range.Start.BlockIndex, range.Start.InlineIndex);
        int searchEnd = range.End.CharacterOffset <= 0
            ? SourceMapRangeSearch.LowerBound(entries, range.End.BlockIndex, range.End.InlineIndex)
            : SourceMapRangeSearch.UpperBound(entries, range.End.BlockIndex, range.End.InlineIndex);

        if (searchStart >= searchEnd ||
            !SourceMapRangeSearch.TryFindFirstHit(entries, searchStart, searchEnd, range, out int firstIndex, out int firstFromOffset) ||
            !SourceMapRangeSearch.TryFindLastHit(entries, firstIndex, searchEnd, range, out int lastIndex, out int lastToOffset))
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

    private int ClampSourceOffset(int spanStart, int relativeOffset, int minimum)
    {
        long absoluteOffset = (long)spanStart + relativeOffset;
        return (int)Math.Clamp(absoluteOffset, minimum, (long)_sourceText.Length);
    }

    private EntryIndex GetEntryIndex()
    {
        lock (_gate)
        {
            if (_entryIndex is not null)
                return _entryIndex;

            Entry[] sortedEntries = _entries.ToArray();
            if (!_entries.EntriesWereAddedInOrder)
                Array.Sort(sortedEntries, SourceMapEntryOrderComparer.Instance);

            _entryIndex = new EntryIndex(sortedEntries);
            return _entryIndex;
        }
    }

    internal IReadOnlyList<Entry> Entries => GetEntryIndex().ReadOnlyEntries;

    internal readonly struct Entry(
        int BlockIndex,
        int InlineIndex,
        int RenderedLength,
        SourceSpan Span,
        int SourcePrefixLength,
        int SourceSuffixLength,
        SourceProjection? Projection,
        int InsertionOrder)
    {
        public int BlockIndex { get; } = BlockIndex;
        public int InlineIndex { get; } = InlineIndex;
        public int RenderedLength { get; } = RenderedLength;
        public SourceSpan Span { get; init; } = Span;
        public int SourcePrefixLength { get; init; } = SourcePrefixLength;
        public int SourceSuffixLength { get; init; } = SourceSuffixLength;
        public SourceProjection? Projection { get; } = Projection;
        public int InsertionOrder { get; } = InsertionOrder;
    }

    /// <summary>
    /// Piecewise UTF-16 projection for a transformed inline run. The legacy
    /// unit map assigns each rendered code unit an exact half-open source
    /// interval; the compact segment map can also represent parser-owned linear
    /// ranges and explicit atomic transformations without interpolation.
    /// </summary>
    internal readonly record struct ProjectionSegment(
        int RenderedStart,
        int RenderedLength,
        int SourceStart,
        int SourceLength,
        bool IsAtomic = false);

    internal sealed class SourceProjection
    {
        // Each range stores two span-relative offsets in one 32-bit word.
        // SourceProjectionUnitRanges keeps its source-length ceiling <=
        // UInt16.MaxValue so this is exact, and retains an explicit guard so a
        // future ceiling increase cannot silently truncate source offsets.
        private readonly uint[]? _unitSourceRanges;
        private readonly ProjectionSegment[]? _segments;
        private readonly int _renderedLength;
        private readonly int _sourceLength;
        private readonly bool _includeSourcePrefix;
        private readonly bool _includeSourceSuffix;

        private SourceProjection(uint[] unitSourceRanges)
        {
            _unitSourceRanges = unitSourceRanges;
            _renderedLength = unitSourceRanges.Length;
            _sourceLength = unitSourceRanges.Length == 0
                ? 0
                : SourceProjectionUnitRanges.GetRangeEnd(unitSourceRanges[^1]);
            _includeSourcePrefix = true;
            _includeSourceSuffix = true;
        }

        private SourceProjection(
            ProjectionSegment[] segments,
            int renderedLength,
            int sourceLength,
            bool includeSourcePrefix,
            bool includeSourceSuffix)
        {
            _segments = segments;
            _renderedLength = renderedLength;
            _sourceLength = sourceLength;
            _includeSourcePrefix = includeSourcePrefix;
            _includeSourceSuffix = includeSourceSuffix;
        }

        internal static SourceProjection? TryCreate(string sourceText, SourceSpan span, string renderedText)
            => SourceProjectionUnitRanges.TryBuild(sourceText, span, renderedText) is { } unitSourceRanges
                ? new SourceProjection(unitSourceRanges)
                : null;

        internal int RenderedLength => _renderedLength;

        internal int SourceLength => _sourceLength;

        /// <summary>
        /// Creates a bounded exact mapping from contiguous rendered segments to
        /// parser-provided source ranges. Source gaps are allowed between
        /// segments (for example, omitted quote prefixes), but source and
        /// rendered ranges must each remain ordered and fully covered.
        /// </summary>
        internal static SourceProjection? TryCreateExactSegments(
            int renderedLength,
            int sourceLength,
            IReadOnlyList<ProjectionSegment> segments,
            bool includeSourcePrefix,
            bool includeSourceSuffix)
        {
            ArgumentNullException.ThrowIfNull(segments);
            if (renderedLength <= 0 || sourceLength <= 0 || segments.Count == 0 ||
                segments.Count > Math.Min(renderedLength, sourceLength))
                return null;

            var copy = new ProjectionSegment[segments.Count];
            int renderedEnd = 0;
            int sourceEnd = 0;
            for (int index = 0; index < copy.Length; index++)
            {
                ProjectionSegment segment = segments[index];
                long nextRenderedEnd = (long)segment.RenderedStart + segment.RenderedLength;
                long nextSourceEnd = (long)segment.SourceStart + segment.SourceLength;
                if (segment.RenderedStart != renderedEnd ||
                    segment.RenderedLength <= 0 ||
                    segment.SourceStart < sourceEnd ||
                    segment.SourceStart < 0 ||
                    segment.SourceLength <= 0 ||
                    nextRenderedEnd > renderedLength ||
                    nextSourceEnd > sourceLength ||
                    (segment.SourceLength != segment.RenderedLength && !segment.IsAtomic))
                {
                    return null;
                }

                copy[index] = segment;
                renderedEnd = (int)nextRenderedEnd;
                sourceEnd = (int)nextSourceEnd;
            }

            if (renderedEnd != renderedLength)
                return null;

            return new SourceProjection(
                copy,
                renderedLength,
                sourceLength,
                includeSourcePrefix,
                includeSourceSuffix);
        }

        internal int ProjectStart(int renderedOffset)
        {
            if (_unitSourceRanges is { } unitSourceRanges)
            {
                if (unitSourceRanges.Length == 0 || renderedOffset <= 0)
                    return 0;
                if (renderedOffset >= unitSourceRanges.Length)
                    return SourceProjectionUnitRanges.GetRangeEnd(unitSourceRanges[^1]);

                return SourceProjectionUnitRanges.GetRangeStart(unitSourceRanges[renderedOffset]);
            }

            ProjectionSegment[] segments = _segments!;
            if (segments.Length == 0)
                return 0;
            if (renderedOffset <= 0)
                return _includeSourcePrefix ? 0 : segments[0].SourceStart;
            if (renderedOffset >= _renderedLength)
                return _includeSourceSuffix ? _sourceLength : ProjectionSegmentSearch.GetSegmentSourceEnd(segments[^1]);

            int index = ProjectionSegmentSearch.FindSegmentForStart(segments, renderedOffset);
            ProjectionSegment segment = segments[index];
            return ProjectionSegmentSearch.MapSegmentStartBoundary(segment, renderedOffset - segment.RenderedStart);
        }

        internal int ProjectEnd(int renderedOffset)
        {
            if (_unitSourceRanges is { } unitSourceRanges)
            {
                if (unitSourceRanges.Length == 0 || renderedOffset <= 0)
                    return 0;
                if (renderedOffset >= unitSourceRanges.Length)
                    return SourceProjectionUnitRanges.GetRangeEnd(unitSourceRanges[^1]);

                return SourceProjectionUnitRanges.GetRangeEnd(unitSourceRanges[renderedOffset - 1]);
            }

            ProjectionSegment[] segments = _segments!;
            if (segments.Length == 0)
                return 0;
            if (renderedOffset <= 0)
                return _includeSourcePrefix ? 0 : segments[0].SourceStart;
            if (renderedOffset >= _renderedLength)
                return _includeSourceSuffix ? _sourceLength : ProjectionSegmentSearch.GetSegmentSourceEnd(segments[^1]);

            int index = ProjectionSegmentSearch.FindSegmentForEnd(segments, renderedOffset);
            ProjectionSegment segment = segments[index];
            return ProjectionSegmentSearch.MapSegmentEndBoundary(segment, renderedOffset - segment.RenderedStart);
        }

    }

    private sealed class EntryIndex
    {
        internal EntryIndex(Entry[] entries)
        {
            Entries = entries;
            ReadOnlyEntries = Array.AsReadOnly(entries);
        }

        internal readonly Entry[] Entries;

        internal readonly ReadOnlyCollection<Entry> ReadOnlyEntries;
    }
}
