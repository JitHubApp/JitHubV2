using System.Globalization;
using System.Text;
using System.Xml;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Structural cost accounting for the streaming preflight: attribute reads,
/// path complexity and length parsing within the configured ceilings.
/// </summary>
internal static class SvgPreflightStructure
{
    internal static void ChargeStructuralPayload(
        ReadOnlySpan<char> value,
        ResvgMarkdownSvgRendererOptions options,
        ref int structuralCost,
        string message)
    {
        if (value.Length == 0)
            return;
        int utf8Bytes = Encoding.UTF8.GetByteCount(value);
        int payloadCost = utf8Bytes / 64;
        if (payloadCost == 0)
            return;
        ChargeStructural(
            payloadCost,
            options,
            ref structuralCost,
            message);
    }

    internal static ReadOnlySpan<char> ReadAttributeValue(
        XmlReader reader,
        ref char[]? scratch,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        // XmlReader keeps ordinary attribute characters in its parse buffer.
        // Reuse a bounded scratch buffer instead of materializing a string for
        // every geometry/data-URI attribute in dense contributor grids. Every
        // value still goes through the same structural, reference and CSS
        // checks. The span is consumed before this buffer can be reused.
        // The reader already enforces the document character ceiling. Grow
        // only to the largest attribute actually encountered, reuse it for the
        // rest of this inspection, and release it before normalization awaits.
        // Large repeated data URIs must not allocate a StringBuilder plus a
        // flattened string for every reference. The extra two characters let
        // the final read detect EOF without splitting a UTF-16 surrogate pair.
        int maximumCapacity = checked(maximumCharacters + 2);
        scratch ??= new char[Math.Min(1026, maximumCapacity)];
        int length = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scratch.Length - length < 2)
            {
                int capacity = Math.Min(checked(scratch.Length * 2), maximumCapacity);
                if (capacity <= scratch.Length)
                    throw SvgPreflightFailures.Resource("The SVG attribute exceeds the source-character ceiling.");
                Array.Resize(ref scratch, capacity);
            }
            // Keep cancellation checks frequent even after a large data URI has
            // expanded the reusable destination buffer. Two or more characters
            // remain available so the reader need not split a surrogate pair.
            int count = reader.ReadValueChunk(scratch, length, Math.Min(4096, scratch.Length - length));
            if (count == 0)
                return scratch.AsSpan(0, length);
            length = checked(length + count);
        }
    }

    internal static void ChargeStructural(
        int amount,
        ResvgMarkdownSvgRendererOptions options,
        ref int structuralCost,
        string message)
    {
        long next = (long)structuralCost + amount;
        if (next > options.MaxStructuralCost)
            throw SvgPreflightFailures.Resource(message);
        structuralCost = checked((int)next);
    }

    internal static int ComputePathComplexity(ReadOnlySpan<char> pathData)
    {
        long commands = 0;
        long numbers = 0;
        ReadOnlySpan<char> path = pathData;
        int cursor = 0;
        while (cursor < path.Length)
        {
            char current = path[cursor];
            if ("MmZzLlHhVvCcSsQqTtAa".Contains(current))
            {
                commands++;
                cursor++;
                continue;
            }

            int start = cursor;
            if (current is '+' or '-')
                cursor++;
            bool hasDigits = false;
            while (cursor < path.Length && char.IsAsciiDigit(path[cursor]))
            {
                hasDigits = true;
                cursor++;
            }
            if (cursor < path.Length && path[cursor] == '.')
            {
                cursor++;
                while (cursor < path.Length && char.IsAsciiDigit(path[cursor]))
                {
                    hasDigits = true;
                    cursor++;
                }
            }
            if (!hasDigits)
            {
                cursor = start + 1;
                continue;
            }
            if (cursor < path.Length && path[cursor] is 'e' or 'E')
            {
                int exponent = cursor++;
                if (cursor < path.Length && path[cursor] is '+' or '-')
                    cursor++;
                int exponentDigits = cursor;
                while (cursor < path.Length && char.IsAsciiDigit(path[cursor]))
                    cursor++;
                if (cursor == exponentDigits)
                    cursor = exponent;
            }
            numbers++;
        }

        long payload = (Encoding.UTF8.GetByteCount(pathData) + 63L) / 64L;
        // Commands and coordinates are much cheaper than independent DOM
        // elements in resvg. Keep a weighted charge so adversarial paths remain
        // bounded, but do not price a legitimate chart or outlined-font path as
        // hundreds of thousands of elements. The 8 MiB source ceiling and the
        // payload charge still reject a maximally dense path before admission.
        long cost = ((commands + 7) / 8) + ((numbers + 15) / 16) + payload;
        return cost > int.MaxValue ? int.MaxValue : (int)cost;
    }

    internal static double? ParseCssLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        value = value.Trim();
        double scale = 1;
        string number = value;
        if (value.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            number = value[..^2];
        else if (value.EndsWith("pt", StringComparison.OrdinalIgnoreCase))
        {
            number = value[..^2];
            scale = 96d / 72d;
        }
        else if (value.EndsWith("in", StringComparison.OrdinalIgnoreCase))
        {
            number = value[..^2];
            scale = 96;
        }
        else if (value.EndsWith("cm", StringComparison.OrdinalIgnoreCase))
        {
            number = value[..^2];
            scale = 96d / 2.54d;
        }
        else if (value.EndsWith("mm", StringComparison.OrdinalIgnoreCase))
        {
            number = value[..^2];
            scale = 96d / 25.4d;
        }
        else if (value.EndsWith('%') || value.EndsWith("em", StringComparison.OrdinalIgnoreCase) ||
                 value.EndsWith("ex", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) &&
               double.IsFinite(parsed) && parsed > 0
            ? parsed * scale
            : null;
    }
}
