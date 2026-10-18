using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// A source-backed semantic-text fragment. A null source represents a repeated
/// structural character (for example a paragraph newline) without allocating a
/// one-character string for every separator.
/// </summary>
internal readonly struct SemanticTextSegment
{
    internal SemanticTextSegment(string source, int sourceStart, int length, int projectionStart)
    {
        Source = source;
        RepeatedCharacter = default;
        SourceStart = sourceStart;
        Length = length;
        ProjectionStart = projectionStart;
    }

    private SemanticTextSegment(char repeatedCharacter, int length, int projectionStart)
    {
        Source = null;
        RepeatedCharacter = repeatedCharacter;
        SourceStart = 0;
        Length = length;
        ProjectionStart = projectionStart;
    }

    internal string? Source { get; }
    internal char RepeatedCharacter { get; }
    internal int SourceStart { get; }
    internal int Length { get; }
    internal int ProjectionStart { get; }
    internal int ProjectionEnd => checked(ProjectionStart + Length);

    internal static SemanticTextSegment Repeated(char value, int length, int projectionStart)
        => new(value, length, projectionStart);
}

/// <summary>
/// Stores the common first fragment inline and allocates a growable index only
/// when a projection actually has multiple noncontiguous fragments.
/// </summary>
internal sealed class SemanticTextSegmentCollection
{
    private SemanticTextSegment _first;
    private List<SemanticTextSegment>? _additional;

    internal int Count { get; private set; }
    internal int Length { get; private set; }
    internal bool BuilderFallbackLocked { get; set; }
    internal StringBuilder? BuilderFallback { get; private set; }
    internal SemanticTextSegment First => _first;
    internal List<SemanticTextSegment>? Additional => _additional;
    internal int EstimatedDescriptorCapacity => 1 + (_additional?.Capacity ?? 0);

    internal void AddSource(string source, int sourceStart, int length)
        => Add(new SemanticTextSegment(source, sourceStart, length, Length));

    internal void AddRepeated(char value, int length)
        => Add(SemanticTextSegment.Repeated(value, length, Length));

    internal SemanticTextSegment Get(int index)
    {
        if (index < _first.ProjectionEnd)
            return _first;

        List<SemanticTextSegment> additional = _additional!;
        int low = 0;
        int high = additional.Count;
        while (low < high)
        {
            int middle = low + ((high - low) >> 1);
            if (additional[middle].ProjectionEnd <= index)
                low = middle + 1;
            else
                high = middle;
        }

        if ((uint)low >= (uint)additional.Count)
            throw new InvalidOperationException("The semantic-text fragments do not cover the requested offset.");
        return additional[low];
    }

    internal void Trim(int length)
    {
        if (length == 0)
        {
            _first = default;
            _additional = null;
            Count = 0;
            Length = 0;
            return;
        }

        if (Count == 0)
        {
            Length = 0;
            return;
        }

        if (_additional is { } additional)
        {
            while (additional.Count > 0 && additional[^1].ProjectionStart >= length)
            {
                additional.RemoveAt(additional.Count - 1);
                Count--;
            }
        }

        if (Count > 0)
        {
            SemanticTextSegment last = Last;
            if (last.ProjectionEnd > length)
            {
                SemanticTextSegment trimmed = last.Source is { } source
                    ? new SemanticTextSegment(
                        source,
                        last.SourceStart,
                        length - last.ProjectionStart,
                        last.ProjectionStart)
                    : SemanticTextSegment.Repeated(
                        last.RepeatedCharacter,
                        length - last.ProjectionStart,
                        last.ProjectionStart);
                SetLast(trimmed);
            }
        }
        else
        {
            _first = default;
        }

        Length = length;
    }

    internal void ClearForBuilderFallback(StringBuilder builder)
    {
        _first = default;
        _additional = null;
        Count = 0;
        Length = 0;
        BuilderFallbackLocked = true;
        BuilderFallback = builder;
    }

    private SemanticTextSegment Last
        => Count == 1 ? _first : _additional![^1];

    private void Add(SemanticTextSegment segment)
    {
        if (Count > 0)
        {
            SemanticTextSegment last = Last;
            bool canMerge = last.Source is { } previousSource &&
                segment.Source is { } source &&
                ReferenceEquals(previousSource, source) &&
                last.SourceStart + last.Length == segment.SourceStart;
            canMerge |= last.Source is null && segment.Source is null &&
                last.RepeatedCharacter == segment.RepeatedCharacter;
            if (canMerge)
            {
                SemanticTextSegment merged = last.Source is { } mergeSource
                    ? new SemanticTextSegment(
                        mergeSource,
                        last.SourceStart,
                        checked(last.Length + segment.Length),
                        last.ProjectionStart)
                    : SemanticTextSegment.Repeated(
                        last.RepeatedCharacter,
                        checked(last.Length + segment.Length),
                        last.ProjectionStart);
                SetLast(merged);
                Length = checked(Length + segment.Length);
                return;
            }
        }

        if (Count == 0)
            _first = segment;
        else
            (_additional ??= new List<SemanticTextSegment>(2)).Add(segment);
        Count++;
        Length = checked(Length + segment.Length);
    }

    private void SetLast(SemanticTextSegment segment)
    {
        if (Count == 1)
            _first = segment;
        else
            _additional![^1] = segment;
    }
}

/// <summary>
/// Reconstructs semantic text while opportunistically reusing an exact prior
/// text projection. It retains immutable source slices during traversal and
/// creates the final contiguous string once, after the projection is complete.
/// Matching is always ordinal UTF-16; a mismatch retains the already-verified
/// prefix as a source slice rather than copying it through a growing builder.
/// </summary>
internal sealed class SemanticTextProjectionAccumulator
{
    private const int ComparisonChunkLength = 4096;
    // Segment descriptors are cheaper than a second full text buffer for normal
    // projections, but adversarially fragmented text must not make metadata
    // dominate the character storage it replaces.
    private const int MinimumSegmentCountBeforeCompaction = 32;
    private const int EstimatedSegmentBytes = 32;
    private const int MaterializedPrefixLimit = 4096;

    private readonly string? _expected;
    private readonly CancellationToken _cancellationToken;
    private object? _state;
    private int _matchedLength;
    private int _pendingTrailingNewlines;

    internal SemanticTextProjectionAccumulator(string? expected = null)
    {
        _expected = expected;
        _cancellationToken = LayoutPassCancellation.GetEffectiveToken(CancellationToken.None);
        if (expected is null)
            _state = new StringBuilder();
    }

    private bool IsMatchingExpected => _expected is not null && _matchedLength >= 0;
    private SemanticTextSegmentCollection? Segments => _state as SemanticTextSegmentCollection;
    private StringBuilder? Materialized => _state switch
    {
        StringBuilder builder => builder,
        SemanticTextSegmentCollection { BuilderFallback: { } builder } => builder,
        _ => null
    };

    internal int Length
    {
        get => checked(CommittedLength + _pendingTrailingNewlines);
        set
        {
            int currentLength = Length;
            if ((uint)value > (uint)currentLength)
                throw new ArgumentOutOfRangeException(nameof(value), "The semantic text accumulator can only shrink.");
            if (value == currentLength)
                return;

            int committedLength = CommittedLength;
            if (value >= committedLength)
            {
                _pendingTrailingNewlines = value - committedLength;
                return;
            }

            _pendingTrailingNewlines = 0;
            if (IsMatchingExpected)
            {
                _matchedLength = value;
                return;
            }

            if (Materialized is { } materialized)
            {
                materialized.Length = value;
                return;
            }

            Segments!.Trim(value);
        }
    }

    internal char this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Length)
                throw new IndexOutOfRangeException();

            int committedLength = CommittedLength;
            if (index >= committedLength)
                return '\n';

            if (IsMatchingExpected)
                return _expected![index];

            if (Materialized is { } materialized)
                return materialized[index];

            SemanticTextSegment segment = Segments!.Get(index);
            return segment.Source is { } source
                ? source[segment.SourceStart + index - segment.ProjectionStart]
                : segment.RepeatedCharacter;
        }
    }

    internal void Append(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        AppendSource(value, 0, value.Length);
    }

    internal void Append(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return;

        if (!IsMatchingExpected)
        {
            FlushPendingTrailingNewlines();
            EnsureCanAppend(value.Length);
            if (Materialized is { } materialized)
            {
                if (_expected is null && Segments?.BuilderFallbackLocked != true)
                    AppendColdBuilderSpan(materialized, value);
                else
                    AppendBuilder(materialized, value);
                return;
            }
            _cancellationToken.ThrowIfCancellationRequested();
            string source = value.ToString();
            AddSourceSegment(source, 0, source.Length);
            return;
        }

        int trailingNewlines = CountTrailingNewlines(value);
        int contentLength = value.Length - trailingNewlines;
        if (contentLength > 0)
        {
            FlushPendingTrailingNewlines();
            if (IsMatchingExpected)
                AppendCompared(value, 0, contentLength);
            else
            {
                _cancellationToken.ThrowIfCancellationRequested();
                AddSourceSegment(value[..contentLength].ToString(), 0, contentLength);
            }
        }

        AddPendingTrailingNewlines(trailingNewlines);
    }

    // Consume an immutable source slice without constructing a substring.
    internal void AppendSlice(string source, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(source);
        _ = source.AsSpan(start, count); // Preserve the span API's argument validation.
        if (count == 0)
            return;

        AppendSource(source, start, count);
    }

    internal void Append(char value)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (!IsMatchingExpected)
        {
            FlushPendingTrailingNewlines();
            EnsureCanAppend(1);
            AddRepeatedCharacter(value, 1);
            return;
        }

        if (value == '\n')
        {
            AddPendingTrailingNewlines(1);
            return;
        }

        FlushPendingTrailingNewlines();
        if (IsMatchingExpected)
            AppendCompared(value);
        else
            AddRepeatedCharacter(value, 1);
    }

    internal string GetText()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        // Normally the semantic builder trims these before completion. Treat an
        // untrimmed terminal separator as actual content for defensive callers.
        FlushPendingTrailingNewlines();
        if (IsMatchingExpected && _matchedLength == _expected!.Length)
        {
            return _expected;
        }

        if (IsMatchingExpected)
            MaterializeMatchedPrefix();

        string result;
        if (Materialized is { } materialized)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            result = materialized.ToString();
        }
        else if (Segments is null || Segments.Length == 0)
        {
            result = string.Empty;
        }
        else if (Segments is { Count: 1 } segments &&
                 segments.First.ProjectionStart == 0 &&
                 segments.First.SourceStart == 0 &&
                 segments.First.Source is { } onlySource &&
                 segments.First.Length == onlySource.Length)
        {
            result = onlySource;
        }
        else
        {
            result = string.Create(Segments!.Length, this, static (destination, accumulator) =>
                accumulator.CopyTo(destination));
        }

        return result;
    }

    private int CommittedLength => Materialized?.Length ?? (IsMatchingExpected ? _matchedLength : (Segments?.Length ?? 0));

    private void AppendSource(string source, int start, int count)
    {
        if (count == 0)
            return;
        if (!IsMatchingExpected)
        {
            FlushPendingTrailingNewlines();
            EnsureCanAppend(count);
            AddSourceSegment(source, start, count);
            return;
        }

        int trailingNewlines = CountTrailingNewlines(source.AsSpan(start, count));
        int contentLength = count - trailingNewlines;
        if (contentLength > 0)
        {
            FlushPendingTrailingNewlines();
            if (IsMatchingExpected)
                AppendCompared(source, start, contentLength);
            else
                AddSourceSegment(source, start, contentLength);
        }

        AddPendingTrailingNewlines(trailingNewlines);
    }

    private void AppendCompared(string source, int start, int count)
    {
        EnsureCanAppend(count);
        int sourceOffset = start;
        int remaining = count;
        while (remaining > 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            string expected = _expected!;
            if (_matchedLength == expected.Length)
            {
                MaterializeMatchedPrefix();
                AddSourceSegment(source, sourceOffset, remaining);
                return;
            }

            int compareLength = Math.Min(
                Math.Min(remaining, ComparisonChunkLength),
                expected.Length - _matchedLength);
            ReadOnlySpan<char> actualChunk = source.AsSpan(sourceOffset, compareLength);
            ReadOnlySpan<char> expectedChunk = expected.AsSpan(_matchedLength, compareLength);
            if (actualChunk.SequenceEqual(expectedChunk))
            {
                _matchedLength += compareLength;
                sourceOffset += compareLength;
                remaining -= compareLength;
                continue;
            }

            int matchingPrefixLength = 0;
            while (matchingPrefixLength < compareLength &&
                   actualChunk[matchingPrefixLength] == expectedChunk[matchingPrefixLength])
            {
                matchingPrefixLength++;
            }

            _matchedLength += matchingPrefixLength;
            sourceOffset += matchingPrefixLength;
            remaining -= matchingPrefixLength;
            MaterializeMatchedPrefix();
            AddSourceSegment(source, sourceOffset, remaining);
            return;
        }
    }

    private void AppendCompared(ReadOnlySpan<char> value, int start, int count)
    {
        EnsureCanAppend(count);
        int sourceOffset = start;
        int remaining = count;
        while (remaining > 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            string expected = _expected!;
            if (_matchedLength == expected.Length)
            {
                MaterializeMatchedPrefix();
                _cancellationToken.ThrowIfCancellationRequested();
                string suffixString = value.Slice(sourceOffset, remaining).ToString();
                AddSourceSegment(suffixString, 0, suffixString.Length);
                return;
            }

            int compareLength = Math.Min(
                Math.Min(remaining, ComparisonChunkLength),
                expected.Length - _matchedLength);
            ReadOnlySpan<char> actualChunk = value.Slice(sourceOffset, compareLength);
            ReadOnlySpan<char> expectedChunk = expected.AsSpan(_matchedLength, compareLength);
            if (actualChunk.SequenceEqual(expectedChunk))
            {
                _matchedLength += compareLength;
                sourceOffset += compareLength;
                remaining -= compareLength;
                continue;
            }

            int matchingPrefixLength = 0;
            while (matchingPrefixLength < compareLength &&
                   actualChunk[matchingPrefixLength] == expectedChunk[matchingPrefixLength])
            {
                matchingPrefixLength++;
            }

            _matchedLength += matchingPrefixLength;
            sourceOffset += matchingPrefixLength;
            remaining -= matchingPrefixLength;
            MaterializeMatchedPrefix();
            _cancellationToken.ThrowIfCancellationRequested();
            string suffix = value.Slice(sourceOffset, remaining).ToString();
            AddSourceSegment(suffix, 0, suffix.Length);
            return;
        }
    }

    private void AppendCompared(char value)
    {
        EnsureCanAppend(1);
        _cancellationToken.ThrowIfCancellationRequested();
        if (_matchedLength < _expected!.Length && _expected[_matchedLength] == value)
        {
            _matchedLength++;
            return;
        }

        MaterializeMatchedPrefix();
        AddRepeatedCharacter(value, 1);
    }

    private void FlushPendingTrailingNewlines()
    {
        int remaining = _pendingTrailingNewlines;
        if (remaining == 0)
            return;
        _pendingTrailingNewlines = 0;

        if (IsMatchingExpected)
        {
            while (remaining > 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                string expected = _expected!;
                int available = expected.Length - _matchedLength;
                if (available == 0)
                {
                    MaterializeMatchedPrefix();
                    break;
                }

                int compareLength = Math.Min(Math.Min(remaining, available), ComparisonChunkLength);
                ReadOnlySpan<char> expectedChunk = expected.AsSpan(_matchedLength, compareLength);
                int matchingPrefixLength = 0;
                while (matchingPrefixLength < compareLength && expectedChunk[matchingPrefixLength] == '\n')
                    matchingPrefixLength++;

                _matchedLength += matchingPrefixLength;
                remaining -= matchingPrefixLength;
                if (matchingPrefixLength != compareLength)
                {
                    MaterializeMatchedPrefix();
                    break;
                }
            }
        }

        AddRepeatedNewlines(remaining);
    }

    private void MaterializeMatchedPrefix()
    {
        if (!IsMatchingExpected)
            return;

        int matchedLength = _matchedLength;
        _matchedLength = -1;
        if (matchedLength == 0)
            return;

        if (matchedLength < MaterializedPrefixLimit)
        {
            var builder = new StringBuilder(matchedLength);
            AppendBuilder(builder, _expected!, 0, matchedLength);
            _state = builder;
            return;
        }

        AddSourceSegment(_expected!, 0, matchedLength);
    }

    private void AddPendingTrailingNewlines(int count)
    {
        if (count == 0)
            return;

        EnsureCanAppend(count);
        _pendingTrailingNewlines = checked(_pendingTrailingNewlines + count);
    }

    private int CountTrailingNewlines(ReadOnlySpan<char> value)
    {
        int count = 0;
        while (count < value.Length && value[value.Length - count - 1] == '\n')
        {
            if ((count & (ComparisonChunkLength - 1)) == 0)
                _cancellationToken.ThrowIfCancellationRequested();
            count++;
        }
        return count;
    }

    private void AddRepeatedNewlines(int count)
    {
        int appended = 0;
        while (appended < count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int chunkLength = Math.Min(count - appended, ComparisonChunkLength);
            AddRepeatedCharacter('\n', chunkLength);
            appended += chunkLength;
        }
    }

    private void AddRepeatedCharacter(char value, int count)
    {
        if (count == 0)
            return;

        _cancellationToken.ThrowIfCancellationRequested();
        EnsureCanAppend(count);
        if (Materialized is { } materialized)
        {
            if (_expected is null && Segments?.BuilderFallbackLocked != true)
                AppendColdBuilderRepeated(materialized, value, count);
            else
                AppendBuilderRepeated(materialized, value, count);
            return;
        }

        SemanticTextSegmentCollection segments = GetSegments();
        segments.AddRepeated(value, count);
        MaybeSwitchToBuilder(segments);
    }

    private void AddSourceSegment(string source, int sourceStart, int count)
    {
        if (count == 0)
            return;

        _cancellationToken.ThrowIfCancellationRequested();
        if (Materialized is { } materialized)
        {
            if (_expected is null && Segments?.BuilderFallbackLocked != true)
                AppendColdBuilderSource(materialized, source, sourceStart, count);
            else
                AppendBuilder(materialized, source, sourceStart, count);
            return;
        }

        SemanticTextSegmentCollection segments = GetSegments();
        segments.AddSource(source, sourceStart, count);
        MaybeSwitchToBuilder(segments);
    }

    private void CopyTo(Span<char> destination)
    {
        SemanticTextSegmentCollection segments = Segments!;
        if (destination.Length != segments.Length)
            throw new ArgumentException("The destination length does not match the semantic projection.", nameof(destination));

        int copied = 0;
        CopySegment(destination, ref copied, segments.First);
        if (segments.Additional is { } additional)
        {
            foreach (SemanticTextSegment segment in additional)
                CopySegment(destination, ref copied, segment);
        }

        if (copied != destination.Length)
            throw new InvalidOperationException("The semantic-text fragments do not cover the final projection.");
    }

    private void CopySegment(Span<char> destination, ref int copied, SemanticTextSegment segment)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (segment.Source is null)
        {
            int segmentRemaining = segment.Length;
            while (segmentRemaining > 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                int count = Math.Min(segmentRemaining, ComparisonChunkLength);
                destination.Slice(copied, count).Fill(segment.RepeatedCharacter);
                copied += count;
                segmentRemaining -= count;
            }
            return;
        }

        int sourceOffset = segment.SourceStart;
        int remaining = segment.Length;
        while (remaining > 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(remaining, ComparisonChunkLength);
            segment.Source.AsSpan(sourceOffset, count).CopyTo(destination.Slice(copied, count));
            copied += count;
            sourceOffset += count;
            remaining -= count;
        }
    }

    private void EnsureCanAppend(int count)
    {
        if (count < 0 || count > int.MaxValue - Length)
            throw new OutOfMemoryException("The semantic text exceeds the supported string length.");
    }

    private SemanticTextSegmentCollection GetSegments()
    {
        if (_state is SemanticTextSegmentCollection segments)
            return segments;

        var created = new SemanticTextSegmentCollection();
        _state = created;
        return created;
    }

    private void MaybeSwitchToBuilder(SemanticTextSegmentCollection segments)
    {
        if (segments.Count < MinimumSegmentCountBeforeCompaction)
            return;

        long estimatedMetadataBytes = (long)segments.EstimatedDescriptorCapacity * EstimatedSegmentBytes;
        long estimatedCharacterBufferBytes = (long)segments.Length * sizeof(char);
        if (estimatedMetadataBytes <= Math.Max(2048L, estimatedCharacterBufferBytes / 4))
            return;

        _cancellationToken.ThrowIfCancellationRequested();
        var builder = new StringBuilder(segments.Length);
        AppendSegment(segments.First);
        if (segments.Additional is { } additional)
        {
            foreach (SemanticTextSegment segment in additional)
                AppendSegment(segment);
        }

        segments.ClearForBuilderFallback(builder);

        void AppendSegment(SemanticTextSegment segment)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            if (segment.Source is { } source)
                AppendBuilder(builder, source, segment.SourceStart, segment.Length);
            else
                AppendBuilderRepeated(builder, segment.RepeatedCharacter, segment.Length);
        }
    }

    private void AppendColdBuilderSource(StringBuilder builder, string source, int start, int count)
    {
        int prefixLength = Math.Max(0, MaterializedPrefixLimit - builder.Length);
        if (count <= prefixLength)
        {
            AppendBuilder(builder, source, start, count);
            return;
        }

        // A very large first paragraph can be retained directly without first
        // filling the bounded prefix builder.
        if (builder.Length == 0)
        {
            _state = null;
            AddSourceSegment(source, start, count);
            return;
        }

        if (prefixLength > 0)
            AppendBuilder(builder, source, start, prefixLength);
        PromoteColdBuilderToSegments(builder);
        AddSourceSegment(source, start + prefixLength, count - prefixLength);
    }

    private void AppendColdBuilderSpan(StringBuilder builder, ReadOnlySpan<char> value)
    {
        int prefixLength = Math.Max(0, MaterializedPrefixLimit - builder.Length);
        if (value.Length <= prefixLength)
        {
            AppendBuilder(builder, value);
            return;
        }

        if (builder.Length == 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            string source = value.ToString();
            _state = null;
            AddSourceSegment(source, 0, source.Length);
            return;
        }

        if (prefixLength > 0)
            AppendBuilder(builder, value[..prefixLength]);
        PromoteColdBuilderToSegments(builder);
        _cancellationToken.ThrowIfCancellationRequested();
        string suffix = value[prefixLength..].ToString();
        AddSourceSegment(suffix, 0, suffix.Length);
    }

    private void AppendColdBuilderRepeated(StringBuilder builder, char value, int count)
    {
        int prefixLength = Math.Max(0, MaterializedPrefixLimit - builder.Length);
        if (count <= prefixLength)
        {
            AppendBuilderRepeated(builder, value, count);
            return;
        }

        if (builder.Length == 0)
        {
            _state = null;
            AddRepeatedCharacter(value, count);
            return;
        }

        if (prefixLength > 0)
            AppendBuilderRepeated(builder, value, prefixLength);
        PromoteColdBuilderToSegments(builder);
        AddRepeatedCharacter(value, count - prefixLength);
    }

    private void PromoteColdBuilderToSegments(StringBuilder builder)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        string prefix = builder.ToString();
        _state = null;
        AddSourceSegment(prefix, 0, prefix.Length);
    }

    private void AppendBuilder(StringBuilder builder, ReadOnlySpan<char> value)
    {
        int appended = 0;
        while (appended < value.Length)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(value.Length - appended, ComparisonChunkLength);
            builder.Append(value.Slice(appended, count));
            appended += count;
        }
    }

    private void AppendBuilder(StringBuilder builder, string source, int start, int count)
    {
        int appended = 0;
        while (appended < count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int chunkLength = Math.Min(count - appended, ComparisonChunkLength);
            builder.Append(source, start + appended, chunkLength);
            appended += chunkLength;
        }
    }

    private void AppendBuilderRepeated(StringBuilder builder, char value, int count)
    {
        int appended = 0;
        while (appended < count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int chunkLength = Math.Min(count - appended, ComparisonChunkLength);
            builder.Append(value, chunkLength);
            appended += chunkLength;
        }
    }
}
