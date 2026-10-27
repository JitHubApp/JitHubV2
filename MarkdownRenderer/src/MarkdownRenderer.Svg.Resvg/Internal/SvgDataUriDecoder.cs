using System.Text;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>Decodes one embedded payload directly into an exact, bounded array.</summary>
internal static class SvgDataUriDecoder
{
    public static byte[] DecodeBase64(
        ReadOnlySpan<char> value,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        int length = GetBase64DecodedLength(value, maximumBytes, cancellationToken);
        byte[] decoded = new byte[length];
        DecodeBase64Into(value, decoded, cancellationToken);
        return decoded;
    }

    internal static int GetBase64DecodedLength(
        ReadOnlySpan<char> value,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int significant = 0;
        int padding = 0;
        for (int index = 0; index < value.Length; index++)
        {
            if ((index & 1023) == 0)
                cancellationToken.ThrowIfCancellationRequested();
            char current = value[index];
            if (current is ' ' or '\t' or '\r' or '\n')
                continue;
            if (current == '=')
            {
                if (++padding > 2)
                    throw new FormatException("An embedded image contains malformed base64 padding.");
            }
            else if (padding != 0 ||
                     !(char.IsAsciiLetterOrDigit(current) || current is '+' or '/'))
            {
                throw new FormatException("An embedded image contains malformed base64 data.");
            }
            significant++;
        }

        if ((significant & 3) != 0)
            throw new FormatException("An embedded image contains malformed base64 length.");
        int length = checked(significant / 4 * 3 - padding);
        ValidateLength(length, maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        return length;
    }

    /// <summary>Fills exact-sized caller-owned storage after bounded length validation.</summary>
    internal static void DecodeBase64Into(
        ReadOnlySpan<char> value,
        Span<byte> destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Convert.TryFromBase64Chars(value, destination, out int written) || written != destination.Length)
            throw new FormatException("An embedded image contains malformed base64 data.");
        cancellationToken.ThrowIfCancellationRequested();
    }

    public static byte[] DecodePercentEncoded(
        ReadOnlySpan<char> value,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int length = 0;
        int cursor = 0;
        // Count before allocating. Encoding whole non-percent runs preserves
        // surrogate pairs, unlike encoding each UTF-16 code unit separately.
        while (cursor < value.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int relativePercent = value[cursor..].IndexOf('%');
            int runLength = relativePercent < 0 ? value.Length - cursor : relativePercent;
            length = checked(length + Encoding.UTF8.GetByteCount(value.Slice(cursor, runLength)));
            ValidateLength(length, maximumBytes);
            cursor += runLength;
            if (cursor == value.Length)
                break;
            _ = ReadPercentByte(value, cursor);
            length = checked(length + 1);
            ValidateLength(length, maximumBytes);
            cursor += 3;
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] decoded = new byte[length];
        cursor = 0;
        int output = 0;
        while (cursor < value.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int relativePercent = value[cursor..].IndexOf('%');
            int runLength = relativePercent < 0 ? value.Length - cursor : relativePercent;
            output += Encoding.UTF8.GetBytes(value.Slice(cursor, runLength), decoded.AsSpan(output));
            cursor += runLength;
            if (cursor == value.Length)
                break;
            decoded[output++] = ReadPercentByte(value, cursor);
            cursor += 3;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return decoded;
    }

    private static byte ReadPercentByte(ReadOnlySpan<char> value, int cursor)
    {
        if (cursor + 2 >= value.Length ||
            HexValue(value[cursor + 1]) is not int high ||
            HexValue(value[cursor + 2]) is not int low)
        {
            throw new FormatException("An embedded image contains malformed percent encoding.");
        }
        return (byte)((high << 4) | low);
    }

    private static int? HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => null,
    };

    private static void ValidateLength(int length, long maximumBytes)
    {
        if (length > maximumBytes)
            throw new MarkdownSvgException(
                MarkdownSvgFailureReason.ResourceLimitExceeded,
                "An embedded image exceeds the decoded-byte ceiling.");
    }
}
