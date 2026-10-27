using System;
using System.Buffers;
using System.IO;
using System.Text;
using System.Threading;

namespace MarkdownRenderer.Images;

/// <summary>
/// Decodes the self-contained payload of a data URI without passing it through
/// <see cref="Uri"/>, a stream reference, or a whole-payload temporary string.
/// </summary>
internal static class MarkdownDataUriDecoder
{
    private const int MaximumHeaderCharacters = 1024;
    private const int CancellationCheckMask = 0xFFF;

    /// <summary>
    /// Decodes an RFC 2397 payload. Base64 is decoded directly from the source
    /// string in the common case. Percent-escaped base64 is decoded a quartet
    /// at a time, and ordinary payload percent escapes are preserved as raw
    /// bytes (rather than interpreted as UTF-8 or form-url-encoded data).
    /// Literal Unicode text is encoded as strict UTF-8.
    /// </summary>
    internal static byte[] Decode(
        string dataUri,
        int maximumDecodedBytes,
        CancellationToken cancellationToken = default)
    {
        ParsedDataUri parsed = Parse(dataUri, maximumDecodedBytes, cancellationToken);
        return parsed.IsBase64
            ? DecodeBase64(parsed.Payload, maximumDecodedBytes, cancellationToken)
            : DecodeEscapedPayload(parsed.Payload, maximumDecodedBytes, cancellationToken);
    }

    /// <summary>
    /// Validates and measures the decoded payload without allocating a decoded byte array or
    /// a temporary string. This uses the same header and payload scanners as <see cref="Decode"/>.
    /// </summary>
    internal static int GetDecodedLength(
        string dataUri,
        int maximumDecodedBytes,
        CancellationToken cancellationToken = default)
    {
        ParsedDataUri parsed = Parse(dataUri, maximumDecodedBytes, cancellationToken);
        long decodedLength = parsed.IsBase64
            ? ProcessBase64Quartets(parsed.Payload, cancellationToken, out _)
            : CountEscapedPayloadBytes(parsed.Payload, maximumDecodedBytes, cancellationToken);

        if (decodedLength <= 0)
            throw InvalidData(parsed.IsBase64
                ? "The base64 data URI payload is empty or invalid."
                : "The data URI payload is empty.");
        if (decodedLength > maximumDecodedBytes)
            throw InvalidData("The decoded data URI payload exceeds its limit.");

        cancellationToken.ThrowIfCancellationRequested();
        return checked((int)decodedLength);
    }

    private static ParsedDataUri Parse(
        string dataUri,
        int maximumDecodedBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataUri);
        if (maximumDecodedBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(maximumDecodedBytes));

        cancellationToken.ThrowIfCancellationRequested();
        ReadOnlySpan<char> source = dataUri.AsSpan();
        if (source.Length < 5 ||
            !source[..5].Equals("data:", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidData("The image source is not a data URI.");
        }

        int comma = 5;
        while (comma < source.Length && source[comma] != ',')
        {
            CheckCancellation(comma - 5, cancellationToken);
            if (comma - 5 >= MaximumHeaderCharacters)
                throw InvalidData("The data URI header exceeds its limit.");
            comma++;
        }

        if (comma == source.Length)
            throw InvalidData("The data URI has no payload separator.");

        ReadOnlySpan<char> header = source.Slice(5, comma - 5);
        bool isBase64 = header.Length >= 7 &&
            header[^7..].Equals(";base64", StringComparison.OrdinalIgnoreCase);
        ReadOnlySpan<char> payload = source[(comma + 1)..];

        long encodedLimit = isBase64
            ? ((long)maximumDecodedBytes * 4) + 16
            : ((long)maximumDecodedBytes * 3) + 16;
        if (payload.Length > encodedLimit)
            throw InvalidData("The encoded data URI payload exceeds its limit.");

        cancellationToken.ThrowIfCancellationRequested();
        return new ParsedDataUri(payload, isBase64);
    }

    private static byte[] DecodeBase64(
        ReadOnlySpan<char> payload,
        int maximumDecodedBytes,
        CancellationToken cancellationToken)
    {
        long expectedLength = ProcessBase64Quartets(payload, cancellationToken, out bool requiresStreaming);
        if (expectedLength <= 0)
            throw InvalidData("The base64 data URI payload is empty or invalid.");
        if (expectedLength > maximumDecodedBytes)
            throw InvalidData("The decoded data URI payload exceeds its limit.");

        cancellationToken.ThrowIfCancellationRequested();
        var decoded = new byte[(int)expectedLength];
        if (!requiresStreaming)
        {
            if (!Convert.TryFromBase64Chars(payload, decoded, out int written) ||
                written != decoded.Length)
            {
                throw InvalidData("The base64 data URI payload is malformed.");
            }
        }
        else
        {
            ProcessBase64Quartets(payload, cancellationToken, out _, decoded);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return decoded;
    }

    // One strict parser measures without allocation, then optionally writes to
    // an already admitted destination. Both passes enforce identical escaping,
    // whitespace, padding and quartet rules.
    private static long ProcessBase64Quartets(
        ReadOnlySpan<char> payload,
        CancellationToken cancellationToken,
        out bool requiresStreaming,
        Span<byte> destination = default)
    {
        Span<char> quartet = stackalloc char[4];
        Span<byte> decodedQuartet = stackalloc byte[3];
        requiresStreaming = false;
        int symbolCount = 0;
        int paddingCount = 0;
        bool paddingStarted = false;
        int quartetLength = 0;
        int index = 0;
        int outputOffset = 0;
        bool writeOutput = !destination.IsEmpty;
        while (index < payload.Length)
        {
            CheckCancellation(index, cancellationToken);
            char value = ReadBase64Character(payload, ref index, out bool percentEncoded);
            requiresStreaming |= percentEncoded;
            if (IsAsciiWhitespace(value))
            {
                requiresStreaming = true;
                continue;
            }

            symbolCount++;
            if (value == '=')
            {
                paddingStarted = true;
                if (++paddingCount > 2)
                    throw InvalidData("The base64 data URI payload has invalid padding.");
            }
            else
            {
                if (paddingStarted || !IsBase64Character(value))
                    throw InvalidData("The base64 data URI payload contains an invalid character.");
            }

            quartet[quartetLength++] = value;
            if (quartetLength == quartet.Length)
            {
                if (!Convert.TryFromBase64Chars(quartet, decodedQuartet, out int bytesWritten))
                    throw InvalidData("The base64 data URI payload is malformed.");
                if (writeOutput)
                {
                    if (bytesWritten > destination.Length - outputOffset)
                        throw InvalidData("The base64 data URI payload is malformed.");
                    decodedQuartet[..bytesWritten].CopyTo(destination[outputOffset..]);
                    outputOffset += bytesWritten;
                }
                quartetLength = 0;
            }
        }

        if (symbolCount == 0 || (symbolCount & 3) != 0 || quartetLength != 0)
            throw InvalidData("The base64 data URI payload has an invalid length.");

        long decodedBytes = ((long)symbolCount / 4 * 3) - paddingCount;
        if (writeOutput && outputOffset != destination.Length)
            throw InvalidData("The base64 data URI payload is malformed.");
        return decodedBytes;
    }

    private static char ReadBase64Character(
        ReadOnlySpan<char> payload,
        ref int index,
        out bool percentEncoded)
    {
        char value = payload[index++];
        if (value != '%')
        {
            percentEncoded = false;
            return value;
        }

        percentEncoded = true;

        if (payload.Length - index < 2 ||
            !TryHex(payload[index], out int high) ||
            !TryHex(payload[index + 1], out int low))
        {
            throw InvalidData("The base64 data URI contains an invalid percent escape.");
        }

        index += 2;
        int decoded = (high << 4) | low;
        if (decoded > 0x7F)
            throw InvalidData("Percent-escaped base64 must decode to ASCII.");
        return (char)decoded;
    }

    private static byte[] DecodeEscapedPayload(
        ReadOnlySpan<char> payload,
        int maximumDecodedBytes,
        CancellationToken cancellationToken)
    {
        int requiredLength = CountEscapedPayloadBytes(payload, maximumDecodedBytes, cancellationToken);
        if (requiredLength <= 0)
            throw InvalidData("The data URI payload is empty.");

        cancellationToken.ThrowIfCancellationRequested();
        var decoded = new byte[requiredLength];
        int outputOffset = 0;
        int index = 0;
        Span<byte> utf8 = stackalloc byte[4];
        while (index < payload.Length)
        {
            CheckCancellation(index, cancellationToken);
            char value = payload[index];
            if (value == '%')
            {
                if (payload.Length - index < 3 ||
                    !TryHex(payload[index + 1], out int high) ||
                    !TryHex(payload[index + 2], out int low))
                {
                    throw InvalidData("The data URI contains an invalid percent escape.");
                }

                decoded[outputOffset++] = (byte)((high << 4) | low);
                index += 3;
                continue;
            }

            OperationStatus status = Rune.DecodeFromUtf16(payload[index..], out Rune rune, out int charsConsumed);
            if (status != OperationStatus.Done)
                throw InvalidData("The data URI contains an invalid Unicode surrogate.");

            int bytesWritten = rune.EncodeToUtf8(utf8);
            utf8[..bytesWritten].CopyTo(decoded.AsSpan(outputOffset));
            outputOffset += bytesWritten;
            index += charsConsumed;
        }

        if (outputOffset != requiredLength)
            throw InvalidData("The data URI payload length is inconsistent.");
        cancellationToken.ThrowIfCancellationRequested();
        return decoded;
    }

    private static int CountEscapedPayloadBytes(
        ReadOnlySpan<char> payload,
        int maximumDecodedBytes,
        CancellationToken cancellationToken)
    {
        long byteCount = 0;
        int index = 0;
        while (index < payload.Length)
        {
            CheckCancellation(index, cancellationToken);
            if (payload[index] == '%')
            {
                if (payload.Length - index < 3 ||
                    !TryHex(payload[index + 1], out _) ||
                    !TryHex(payload[index + 2], out _))
                {
                    throw InvalidData("The data URI contains an invalid percent escape.");
                }

                byteCount++;
                index += 3;
            }
            else
            {
                OperationStatus status = Rune.DecodeFromUtf16(payload[index..], out Rune rune, out int charsConsumed);
                if (status != OperationStatus.Done)
                    throw InvalidData("The data URI contains an invalid Unicode surrogate.");

                byteCount += rune.Utf8SequenceLength;
                index += charsConsumed;
            }

            if (byteCount > maximumDecodedBytes)
                throw InvalidData("The decoded data URI payload exceeds its limit.");
        }

        return (int)byteCount;
    }

    private static void CheckCancellation(int index, CancellationToken cancellationToken)
    {
        // The scanner may advance by three UTF-16 characters for %HH or by
        // two for a surrogate pair, so check across the boundary rather than
        // requiring an exact multiple of the cadence.
        if ((index & CancellationCheckMask) <= 3)
            cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool IsBase64Character(char value) =>
        (value >= 'A' && value <= 'Z') ||
        (value >= 'a' && value <= 'z') ||
        (value >= '0' && value <= '9') ||
        value is '+' or '/';

    private static bool IsAsciiWhitespace(char value) =>
        value is ' ' or '\t' or '\r' or '\n' or '\f' or '\v';

    private static bool TryHex(char value, out int nibble)
    {
        if (value is >= '0' and <= '9')
        {
            nibble = value - '0';
            return true;
        }

        if (value is >= 'a' and <= 'f')
        {
            nibble = value - 'a' + 10;
            return true;
        }

        if (value is >= 'A' and <= 'F')
        {
            nibble = value - 'A' + 10;
            return true;
        }

        nibble = 0;
        return false;
    }

    private static InvalidDataException InvalidData(string message) => new(message);

    private readonly ref struct ParsedDataUri(ReadOnlySpan<char> payload, bool isBase64)
    {
        internal readonly ReadOnlySpan<char> Payload = payload;
        internal readonly bool IsBase64 = isBase64;
    }
}
