using static MarkdownRenderer.Mermaid.MmirPrimitives;

namespace MarkdownRenderer.Mermaid;

/// <summary>
/// Measures the original source as UTF-8 and translates native UTF-8 source
/// mappings to half-open UTF-16 ranges on scalar boundaries.
/// </summary>
internal static class MermaidSceneSourceText
{
    internal static bool TryMeasureUtf8(
        string source,
        CancellationToken cancellationToken,
        out int byteLength)
    {
        long total = 0;
        for (int i = 0; i < source.Length;)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            if (!TryGetUtf8Width(source, i, out int utf16Width, out int utf8Width))
            {
                byteLength = 0;
                return false;
            }

            total += utf8Width;
            if (total > int.MaxValue)
            {
                byteLength = 0;
                return false;
            }

            i += utf16Width;
        }

        byteLength = (int)total;
        return true;
    }

    internal static MermaidSceneSourceMapping[] TranslateSourceMappings(
        RawSourceMapping[] rawMappings,
        string source,
        int sourceByteLength,
        CancellationToken cancellationToken,
        out MermaidSceneDecodeResult? failure)
    {
        if (rawMappings.Length == 0)
        {
            failure = null;
            return [];
        }

        var endpoints = new int[checked(rawMappings.Length * 2)];
        for (int i = 0; i < rawMappings.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            endpoints[i * 2] = rawMappings[i].Utf8Start;
            endpoints[(i * 2) + 1] = checked(rawMappings[i].Utf8Start + rawMappings[i].Utf8Length);
        }

        Array.Sort(endpoints);
        var utf16Offsets = new int[endpoints.Length];
        int endpointIndex = 0;
        int utf8Offset = 0;
        int utf16Offset = 0;
        while (true)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, utf16Offset);
            while (endpointIndex < endpoints.Length && endpoints[endpointIndex] == utf8Offset)
            {
                utf16Offsets[endpointIndex++] = utf16Offset;
            }

            if (utf16Offset == source.Length)
            {
                break;
            }

            if (!TryGetUtf8Width(source, utf16Offset, out int utf16Width, out int utf8Width))
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidUtf8, utf16Offset, "The source contains an unpaired UTF-16 surrogate.");
                return [];
            }

            utf16Offset += utf16Width;
            utf8Offset += utf8Width;
        }

        if (utf8Offset != sourceByteLength || endpointIndex != endpoints.Length)
        {
            failure = Failure(MermaidSceneDecodeStatus.InvalidSourceMapping, 0, "A native source offset is not on a UTF-8 scalar boundary.");
            return [];
        }

        var result = new MermaidSceneSourceMapping[rawMappings.Length];
        for (int i = 0; i < rawMappings.Length; i++)
        {
            ThrowIfCancellationRequestedPeriodically(cancellationToken, i);
            int startPosition = Array.BinarySearch(endpoints, rawMappings[i].Utf8Start);
            int endPosition = Array.BinarySearch(endpoints, checked(rawMappings[i].Utf8Start + rawMappings[i].Utf8Length));
            if (startPosition < 0 || endPosition < 0)
            {
                failure = Failure(MermaidSceneDecodeStatus.InvalidSourceMapping, 0, "A source mapping could not be translated.");
                return [];
            }

            int utf16Start = utf16Offsets[startPosition];
            int utf16End = utf16Offsets[endPosition];
            result[i] = new MermaidSceneSourceMapping(
                rawMappings[i].SourceId,
                new MermaidSourceRange(utf16Start, utf16End - utf16Start),
                OptionalIndex(rawMappings[i].SemanticIndex));
        }

        failure = null;
        return result;
    }

    private static bool TryGetUtf8Width(string source, int index, out int utf16Width, out int utf8Width)
    {
        char value = source[index];
        if (char.IsHighSurrogate(value))
        {
            if (index + 1 >= source.Length || !char.IsLowSurrogate(source[index + 1]))
            {
                utf16Width = 0;
                utf8Width = 0;
                return false;
            }

            utf16Width = 2;
            utf8Width = 4;
            return true;
        }

        if (char.IsLowSurrogate(value))
        {
            utf16Width = 0;
            utf8Width = 0;
            return false;
        }

        utf16Width = 1;
        utf8Width = value switch
        {
            <= '\u007f' => 1,
            <= '\u07ff' => 2,
            _ => 3,
        };
        return true;
    }
}
