using System;
using Markdig;
using Markdig.Helpers;

namespace MarkdownRenderer;

/// <summary>
/// Narrows current Markdig behavior to the grammar and reference serialization
/// pinned by the 0.29-gfm profile. This remains internal so parser internals do
/// not leak into the stable public contract.
/// </summary>
internal static class Gfm029Compatibility
{
    internal const char MaskedTableSeparator = '\uE000';

    /// <summary>
    /// Lets parser extensions that retain source coordinates in private block
    /// state rebuild those projections after the compatibility preparation
    /// pass, without making the core parser depend on an extension assembly.
    /// </summary>
    internal interface IPreparedSourceRemappable
    {
        void RemapPreparedSource(PreparedSource prepared, string source);
    }

    internal static void NormalizeSyntaxTree(
        Markdig.Syntax.MarkdownDocument document,
        string source,
        bool enableAutoLinks,
        PreparedSource prepared)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(prepared);
        Gfm029SyntaxTreeNormalizer.Normalize(document, source, enableAutoLinks, prepared);
    }

    internal static PreparedSource PrepareSource(string source, bool enableTables)
        => Gfm029TableSourcePreparer.Prepare(source, enableTables);

    internal static string RenderReferenceHtml(
        Markdig.Syntax.MarkdownDocument document,
        MarkdownPipeline pipeline,
        bool applyTagFilter)
        => Gfm029ReferenceHtml.Render(document, pipeline, applyTagFilter);

    internal sealed class PreparedSource
    {
        private readonly int[] _insertions;
        private readonly int[] _maskedSeparators;

        internal PreparedSource(
            string text,
            int[] insertions,
            int[] maskedSeparators)
        {
            Text = text;
            _insertions = insertions;
            _maskedSeparators = maskedSeparators;
            HasChanges = insertions.Length > 0 || maskedSeparators.Length > 0;
        }

        /// <summary>The transformed-text offsets of inserted separators, ascending.</summary>
        internal ReadOnlySpan<int> Insertions => _insertions;

        /// <summary>The transformed-text offsets of masked separators, ascending.</summary>
        internal ReadOnlySpan<int> MaskedSeparators => _maskedSeparators;

        internal readonly string Text;

        internal readonly bool HasChanges;

        internal static PreparedSource Unchanged(string source)
            => new(source, Array.Empty<int>(), Array.Empty<int>());

        /// <summary>
        /// Rebinds parser-owned code/HTML line slices to the original source
        /// only when every parser character is accounted for by this prepared
        /// source's recorded insertions/masks. This restores inserted table
        /// separators and masked dashes without searching body text or
        /// accepting an unrelated/synthetic line buffer.
        /// </summary>
        internal bool TryRemapLines(StringLineGroup lines, string source)
        {
            // Validate the complete logical group before mutating any entry.
            // Lines may be backed by a pooled array with unused capacity; only
            // Count entries are part of Markdig's source contract.
            for (int index = 0; index < lines.Count; index++)
            {
                if (!TryGetMappedLine(lines.Lines[index], source, out _, out _, out _, out _))
                    return false;
            }

            for (int index = 0; index < lines.Count; index++)
            {
                StringLine line = lines.Lines[index];
                if (!TryGetMappedLine(
                        line,
                        source,
                        out int mappedStart,
                        out int mappedEnd,
                        out int mappedPosition,
                        out bool needsRemap))
                {
                    return false;
                }
                if (!needsRemap)
                    continue;

                line.Slice = new StringSlice(
                    source,
                    mappedStart,
                    mappedEnd - 1,
                    line.Slice.NewLine);
                line.Position = mappedPosition;
                lines.Lines[index] = line;
            }

            return true;
        }

        private bool TryGetMappedLine(
            StringLine line,
            string source,
            out int mappedStart,
            out int mappedEnd,
            out int mappedPosition,
            out bool needsRemap)
        {
            mappedStart = 0;
            mappedEnd = 0;
            mappedPosition = 0;
            needsRemap = false;

            StringSlice slice = line.Slice;
            string? text = slice.Text;
            int length = slice.Length;
            if (text is null || length < 0 || slice.Start < 0 ||
                slice.Start > text.Length - length ||
                line.Position < 0 || line.Position > text.Length)
            {
                return false;
            }

            if (ReferenceEquals(text, source))
            {
                mappedStart = slice.Start;
                mappedEnd = slice.Start + length;
                mappedPosition = line.Position;
                return true;
            }

            if (!ReferenceEquals(text, Text))
                return false;

            int transformedEnd = slice.Start + length;
            mappedStart = MapBoundary(slice.Start);
            mappedEnd = MapBoundary(transformedEnd);
            mappedPosition = MapBoundary(line.Position);
            if (mappedEnd < mappedStart || mappedEnd > source.Length ||
                mappedPosition > source.Length ||
                mappedEnd - mappedStart != length - CountInsertionsInRange(slice.Start, transformedEnd) ||
                !MatchesOriginalCharacters(source, slice.Start, transformedEnd))
            {
                return false;
            }

            if (length == 0 && !IsSourceLineBreakValid(source, mappedEnd, line.NewLine))
                return false;

            needsRemap = true;
            return true;
        }

        private int MapBoundary(int transformedOffset)
            => transformedOffset - CountLessThan(transformedOffset);

        private int CountInsertionsInRange(int start, int end)
            => CountLessThan(end) - CountLessThan(start);

        private bool MatchesOriginalCharacters(string source, int start, int end)
        {
            int insertionIndex = CountLessThan(start);
            int maskedIndex = LowerBound(_maskedSeparators, start);
            int originalIndex = start - insertionIndex;

            for (int transformedIndex = start; transformedIndex < end; transformedIndex++)
            {
                if (insertionIndex < _insertions.Length && _insertions[insertionIndex] == transformedIndex)
                {
                    if (Text[transformedIndex] != '|')
                        return false;
                    insertionIndex++;
                    continue;
                }

                if (originalIndex >= source.Length)
                    return false;

                char transformedChar = Text[transformedIndex];
                char sourceChar = source[originalIndex];
                bool isRecordedMask = maskedIndex < _maskedSeparators.Length &&
                    _maskedSeparators[maskedIndex] == transformedIndex;
                if (transformedChar != sourceChar &&
                    !(isRecordedMask && transformedChar == MaskedTableSeparator && sourceChar == '-'))
                {
                    return false;
                }

                if (isRecordedMask)
                    maskedIndex++;
                originalIndex++;
            }

            return true;
        }

        private static bool IsSourceLineBreakValid(string source, int boundary, NewLine newLine)
        {
            return newLine switch
            {
                NewLine.None => boundary == source.Length,
                NewLine.CarriageReturn => boundary < source.Length && source[boundary] == '\r' &&
                    (boundary + 1 >= source.Length || source[boundary + 1] != '\n'),
                NewLine.LineFeed => boundary < source.Length && source[boundary] == '\n' &&
                    (boundary == 0 || source[boundary - 1] != '\r'),
                NewLine.CarriageReturnLineFeed => boundary <= source.Length - 2 &&
                    source[boundary] == '\r' && source[boundary + 1] == '\n',
                _ => false,
            };
        }

        private static int LowerBound(int[] values, int target)
        {
            int low = 0;
            int high = values.Length;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (values[middle] < target)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        internal bool TryRestoreMaskedLiteral(
            Markdig.Syntax.SourceSpan parserSpan,
            StringSlice content,
            out StringSlice restoredContent)
        {
            restoredContent = content;
            int sourceLength = parserSpan.Length;
            if (_maskedSeparators.Length == 0 ||
                parserSpan.Start < 0 ||
                sourceLength <= 0 ||
                parserSpan.Start > Text.Length - sourceLength)
            {
                return false;
            }

            int maskIndex = LowerBound(_maskedSeparators, parserSpan.Start);
            int sourceEnd = parserSpan.Start + sourceLength;
            if (maskIndex >= _maskedSeparators.Length || _maskedSeparators[maskIndex] >= sourceEnd)
                return false;

            string contentText = content.ToString();
            if (content.Length != sourceLength ||
                !contentText.AsSpan().SequenceEqual(Text.AsSpan(parserSpan.Start, sourceLength)))
            {
                return false;
            }

            char[] restoredCharacters = contentText.ToCharArray();
            while (maskIndex < _maskedSeparators.Length && _maskedSeparators[maskIndex] < sourceEnd)
            {
                int maskOffset = _maskedSeparators[maskIndex++];
                if (Text[maskOffset] != MaskedTableSeparator)
                    return false;

                restoredCharacters[maskOffset - parserSpan.Start] = '-';
            }

            restoredContent = new StringSlice(new string(restoredCharacters));
            return true;
        }

        /// <summary>
        /// Maps a range in the prepared parser text back to its exact authored
        /// substring. Inserted compatibility characters are omitted and
        /// recorded masks are accepted only when they occupy the expected
        /// source dash; unrelated parser text never receives a guessed map.
        /// </summary>
        internal bool TryGetOriginalTextRange(
            int preparedStart,
            int preparedLength,
            string source,
            out int sourceStart,
            out string sourceText)
        {
            sourceStart = 0;
            sourceText = string.Empty;
            if (preparedStart < 0 || preparedLength < 0 ||
                preparedStart > Text.Length - preparedLength)
            {
                return false;
            }

            int preparedEnd = preparedStart + preparedLength;
            int mappedStart = MapBoundary(preparedStart);
            int mappedEnd = MapBoundary(preparedEnd);
            int expectedSourceLength = preparedLength - CountInsertionsInRange(preparedStart, preparedEnd);
            if (mappedStart < 0 || mappedEnd < mappedStart || mappedEnd > source.Length ||
                mappedEnd - mappedStart != expectedSourceLength ||
                !MatchesOriginalCharacters(source, preparedStart, preparedEnd))
            {
                return false;
            }

            sourceStart = mappedStart;
            sourceText = source.Substring(mappedStart, mappedEnd - mappedStart);
            return true;
        }

        internal Markdig.Syntax.SourceSpan MapSpan(Markdig.Syntax.SourceSpan span)
        {
            if (_insertions.Length == 0 || span.Start < 0 || span.End < 0)
                return span;

            int start = span.Start - CountLessThan(span.Start);
            int end = span.End - CountLessThanOrEqual(span.End);
            if (end < start)
                end = start;
            return new Markdig.Syntax.SourceSpan(start, end);
        }

        private int CountLessThan(int value)
        {
            int low = 0;
            int high = _insertions.Length;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (_insertions[middle] < value)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }

        private int CountLessThanOrEqual(int value)
        {
            int low = 0;
            int high = _insertions.Length;
            while (low < high)
            {
                int middle = low + ((high - low) / 2);
                if (_insertions[middle] <= value)
                    low = middle + 1;
                else
                    high = middle;
            }
            return low;
        }
    }
}
