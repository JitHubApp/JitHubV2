using System;
using System.Text;
using System.Threading;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Reconstructs semantic text while opportunistically reusing an exact prior
/// text projection. Matching is always ordinal UTF-16; a mismatch materializes
/// the already-verified prefix and continues as an ordinary StringBuilder.
/// </summary>
internal sealed class SemanticTextProjectionAccumulator
{
    private const int ComparisonChunkLength = 4096;

    private readonly string? _expected;
    private readonly CancellationToken _cancellationToken;
    private StringBuilder? _materialized;
    private int _matchedLength;
    private int _pendingTrailingNewlines;

    internal SemanticTextProjectionAccumulator(string? expected = null)
    {
        _expected = expected;
        _cancellationToken = LayoutPassCancellation.GetEffectiveToken(CancellationToken.None);
        if (expected is null)
            _materialized = new StringBuilder();
    }

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
            if (_materialized is { } materialized)
                materialized.Length = value;
            else
                _matchedLength = value;
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

            return _materialized is { } materialized
                ? materialized[index]
                : _expected![index];
        }
    }

    internal void Append(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return;

        Append(value.AsSpan());
    }

    internal void Append(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
            return;

        if (_expected is null || _materialized is not null)
        {
            FlushPendingTrailingNewlines();
            EnsureCanAppend(value.Length);
            AppendMaterialized(value, 0, value.Length);
            return;
        }

        int trailingNewlines = CountTrailingNewlines(value);
        int contentLength = value.Length - trailingNewlines;
        if (contentLength > 0)
        {
            FlushPendingTrailingNewlines();
            AppendCompared(value, 0, contentLength);
        }

        AddPendingTrailingNewlines(trailingNewlines);
    }

    // Consume an immutable source slice without constructing a substring. The
    // existing comparison/buffer path preserves ordinal UTF-16 and cancellation.
    internal void AppendSlice(string source, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(source);
        Append(source.AsSpan(start, count));
    }

    internal void Append(char value)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (_expected is null || _materialized is not null)
        {
            FlushPendingTrailingNewlines();
            EnsureCanAppend(1);
            _materialized!.Append(value);
            return;
        }

        if (value == '\n')
        {
            AddPendingTrailingNewlines(1);
            return;
        }

        FlushPendingTrailingNewlines();
        AppendCompared(value);
    }

    internal string GetText()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        // Normally the semantic builder trims these before completion. Treat an
        // untrimmed terminal separator as actual content for defensive callers.
        FlushPendingTrailingNewlines();
        if (_materialized is { } materialized)
            return materialized.ToString();

        string expected = _expected!;
        if (_matchedLength == expected.Length)
            return expected;

        MaterializePrefix();
        return _materialized!.ToString();
    }

    private int CommittedLength => _materialized?.Length ?? _matchedLength;

    private void AppendCompared(ReadOnlySpan<char> value, int start, int count)
    {
        EnsureCanAppend(count);
        if (_materialized is not null)
        {
            AppendMaterialized(value, start, count);
            return;
        }

        int sourceOffset = start;
        int remaining = count;
        while (remaining > 0)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            string expected = _expected!;
            if (_matchedLength == expected.Length)
            {
                MaterializePrefix();
                AppendMaterialized(value, sourceOffset, remaining);
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
            MaterializePrefix();
            AppendMaterialized(value, sourceOffset, remaining);
            return;
        }
    }

    private void AppendCompared(char value)
    {
        EnsureCanAppend(1);
        if (_materialized is { } materialized)
        {
            materialized.Append(value);
            return;
        }

        _cancellationToken.ThrowIfCancellationRequested();
        if (_matchedLength < _expected!.Length && _expected[_matchedLength] == value)
        {
            _matchedLength++;
            return;
        }

        MaterializePrefix();
        _materialized!.Append(value);
    }

    private void FlushPendingTrailingNewlines()
    {
        while (_pendingTrailingNewlines > 0 && _materialized is null)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            string expected = _expected!;
            int available = expected.Length - _matchedLength;
            if (available <= 0)
            {
                MaterializePrefix();
                break;
            }

            int chunkLength = Math.Min(_pendingTrailingNewlines, ComparisonChunkLength);
            int compareLength = Math.Min(chunkLength, available);
            ReadOnlySpan<char> expectedChunk = expected.AsSpan(_matchedLength, compareLength);
            int matchingPrefixLength = 0;
            while (matchingPrefixLength < compareLength && expectedChunk[matchingPrefixLength] == '\n')
                matchingPrefixLength++;

            _matchedLength += matchingPrefixLength;
            _pendingTrailingNewlines -= matchingPrefixLength;
            if (matchingPrefixLength != compareLength)
            {
                MaterializePrefix();
                break;
            }

            if (compareLength != chunkLength)
            {
                MaterializePrefix();
                break;
            }
        }

        if (_pendingTrailingNewlines > 0 && _materialized is not null)
        {
            int count = _pendingTrailingNewlines;
            EnsureCanAppendToMaterialized(count);
            AppendMaterializedNewlines(count);
            _pendingTrailingNewlines = 0;
        }
    }

    private void MaterializePrefix()
    {
        if (_materialized is not null)
            return;

        var materialized = new StringBuilder(Math.Min(_matchedLength, ComparisonChunkLength));
        int offset = 0;
        while (offset < _matchedLength)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(ComparisonChunkLength, _matchedLength - offset);
            materialized.Append(_expected!, offset, count);
            offset += count;
        }
        _materialized = materialized;
    }

    private void AddPendingTrailingNewlines(int count)
    {
        if (count == 0)
            return;

        EnsureCanAppend(count);
        _pendingTrailingNewlines += count;
    }

    private void AppendMaterialized(ReadOnlySpan<char> value, int start, int count)
    {
        int appended = 0;
        while (appended < count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int chunkLength = Math.Min(ComparisonChunkLength, count - appended);
            _materialized!.Append(value.Slice(start + appended, chunkLength));
            appended += chunkLength;
        }
    }

    private void AppendMaterializedNewlines(int count)
    {
        int appended = 0;
        while (appended < count)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            int chunkLength = Math.Min(ComparisonChunkLength, count - appended);
            _materialized!.Append('\n', chunkLength);
            appended += chunkLength;
        }
    }

    private void EnsureCanAppend(int count)
    {
        if (count < 0 || count > int.MaxValue - Length)
            throw new OutOfMemoryException("The semantic text exceeds the supported StringBuilder length.");
    }

    private void EnsureCanAppendToMaterialized(int count)
    {
        if (count < 0 || count > int.MaxValue - _materialized!.Length)
            throw new OutOfMemoryException("The semantic text exceeds the supported StringBuilder length.");
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
}
