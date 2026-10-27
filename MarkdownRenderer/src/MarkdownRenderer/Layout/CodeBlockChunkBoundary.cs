using System;
using System.Globalization;
using System.Threading;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Finds a code-chunk boundary that does not divide an extended grapheme.
/// The scan begins at the previous safe chunk boundary, so the total work
/// across a code block is linear in its UTF-16 length.
/// </summary>
internal static class CodeBlockChunkBoundary
{
    private const int CancellationCheckInterval = 1_024;

    /// <summary>
    /// Keeps <paramref name="preferredLength"/> when it is a grapheme boundary;
    /// otherwise backs up to the last complete grapheme. If the first grapheme
    /// itself crosses the preferred boundary, it is kept whole to guarantee
    /// progress. Consequently, a single grapheme longer than
    /// <paramref name="maximumLength"/> is the one explicit case that may
    /// exceed the normal chunk cap; splitting it or returning zero would both
    /// violate the chunk contract.
    /// </summary>
    internal static int GetSafeLength(
        string text,
        int offset,
        int preferredLength,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (offset < 0 || offset >= text.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (maximumLength <= 0 || maximumLength > text.Length - offset)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        if (preferredLength <= 0 || preferredLength > maximumLength)
            throw new ArgumentOutOfRangeException(nameof(preferredLength));

        cancellationToken.ThrowIfCancellationRequested();

        int preferredEnd = offset + preferredLength;
        int position = offset;
        long scannedSinceCancellationCheck = 0;
        while (position < preferredEnd)
        {
            if (scannedSinceCancellationCheck >= CancellationCheckInterval)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scannedSinceCancellationCheck = 0;
            }

            int elementLength = StringInfo.GetNextTextElementLength(text.AsSpan(position));
            if (elementLength <= 0 || elementLength > text.Length - position)
                throw new InvalidOperationException("StringInfo returned an invalid text-element length.");

            scannedSinceCancellationCheck += elementLength;
            if (scannedSinceCancellationCheck >= CancellationCheckInterval)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scannedSinceCancellationCheck = 0;
            }

            int elementEnd = position + elementLength;
            if (elementEnd > preferredEnd)
            {
                // A cut before the first complete element would otherwise
                // produce a zero-length chunk. Include that element whole;
                // this may exceed the hard cap only when the element itself
                // is longer than the cap.
                return position == offset ? elementLength : position - offset;
            }

            position = elementEnd;
        }

        return preferredLength;
    }

    /// <summary>
    /// Preserves the preferred chunk boundary when it already ends at an
    /// authored line break. If the preferred boundary would divide a logical
    /// line, extends the chunk through that line's LF (or through the end of
    /// the text when the final line has no LF). The nominal maximum is a soft
    /// target in this case: splitting a single authored line into independent
    /// native layouts changes wrapping, bidi ordering, and shaping.
    /// </summary>
    internal static int GetLinePreservingLength(
        string text,
        int offset,
        int preferredLength,
        int maximumLength,
        CancellationToken cancellationToken)
    {
        int safeLength = GetSafeLength(
            text,
            offset,
            preferredLength,
            maximumLength,
            cancellationToken);

        int safeEnd = checked(offset + safeLength);
        if (safeEnd == text.Length || text[safeEnd - 1] == '\n')
            return safeLength;

        long scannedSinceCancellationCheck = 0;
        for (int position = safeEnd; position < text.Length; position++)
        {
            if (scannedSinceCancellationCheck >= CancellationCheckInterval)
            {
                cancellationToken.ThrowIfCancellationRequested();
                scannedSinceCancellationCheck = 0;
            }

            scannedSinceCancellationCheck++;
            if (text[position] == '\n')
                return checked(position - offset + 1);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return text.Length - offset;
    }
}
