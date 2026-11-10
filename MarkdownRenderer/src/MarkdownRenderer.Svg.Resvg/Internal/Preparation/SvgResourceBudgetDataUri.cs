using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MarkdownRenderer.Images.Raster;

namespace MarkdownRenderer.Svg.Resvg.Internal.Preparation;

/// <summary>
/// Decodes <c>data:</c> URIs for the resource-budget pass and checks that embedded
/// payloads are a supported raster or SVG media type.
/// </summary>
internal static class SvgResourceBudgetDataUri
{
    internal static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    internal static bool TryDecodeDataUri(string value, out string mediaType, out byte[] payload)
    {
        mediaType = string.Empty;
        payload = Array.Empty<byte>();
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        int comma = value.IndexOf(',');
        if (comma <= 5)
            return false;

        string metadata = value.Substring(5, comma - 5);
        int semicolon = metadata.IndexOf(';');
        mediaType = (semicolon >= 0 ? metadata[..semicolon] : metadata).Trim();
        bool base64 = metadata.EndsWith(";base64", StringComparison.OrdinalIgnoreCase) ||
            metadata.IndexOf(";base64;", StringComparison.OrdinalIgnoreCase) >= 0;
        string encoded = value[(comma + 1)..];
        try
        {
            payload = base64 ? Convert.FromBase64String(encoded) : PercentDecode(encoded);
            if (payload.Length == 0 || payload.Length > SvgResourceBudget.MaxInputBytes)
                return false;

            // Match browser data-URI tolerance without weakening the resource
            // boundary: a positively identified supported raster payload is
            // authoritative even when a generator mislabeled it (for example,
            // a PNG avatar declared as image/gif). This also admits
            // OpenCollective's `data:false;base64,...` PNG avatars. Arbitrary
            // bytes and undeclared nested SVG remain rejected.
            string? detectedRasterType = DetectEmbeddedRasterMediaType(payload);
            if (detectedRasterType is not null)
            {
                mediaType = detectedRasterType;
                return true;
            }

            return mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is FormatException or EncoderFallbackException)
        {
            payload = Array.Empty<byte>();
            return false;
        }
    }

    internal static byte[] PercentDecode(string value)
    {
        using MemoryStream output = new(capacity: Math.Min(value.Length, SvgResourceBudget.MaxInputBytes));
        Span<char> chars = stackalloc char[2];
        Span<byte> utf8 = stackalloc byte[4];
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current == '%' && index + 2 < value.Length &&
                TryHex(value[index + 1], out int high) && TryHex(value[index + 2], out int low))
            {
                output.WriteByte((byte)((high << 4) | low));
                index += 2;
            }
            else if (current <= 0x7F)
            {
                output.WriteByte((byte)current);
            }
            else
            {
                chars[0] = current;
                int charCount = 1;
                if (char.IsHighSurrogate(current) && index + 1 < value.Length &&
                    char.IsLowSurrogate(value[index + 1]))
                {
                    chars[1] = value[++index];
                    charCount = 2;
                }

                int count = Encoding.UTF8.GetBytes(chars[..charCount], utf8);
                output.Write(utf8[..count]);
            }

            if (output.Length > SvgResourceBudget.MaxInputBytes)
                throw new FormatException("Decoded data URI exceeds the SVG resource budget.");
        }

        return output.ToArray();
    }

    internal static bool TryHex(char value, out int digit)
    {
        if (value is >= '0' and <= '9')
        {
            digit = value - '0';
            return true;
        }
        if (value is >= 'a' and <= 'f')
        {
            digit = value - 'a' + 10;
            return true;
        }
        if (value is >= 'A' and <= 'F')
        {
            digit = value - 'A' + 10;
            return true;
        }

        digit = 0;
        return false;
    }

    internal static bool IsSupportedEmbeddedMediaType(string value) =>
        value.Equals("image/png", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("image/jpg", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("image/gif", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("image/webp", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);

    internal static string? DetectEmbeddedRasterMediaType(ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 8 &&
            payload[..8].SequenceEqual(PngSignature))
        {
            return "image/png";
        }
        if (payload.Length >= 3 && payload[0] == 0xff && payload[1] == 0xd8 && payload[2] == 0xff)
            return "image/jpeg";
        if (payload.Length >= 6 &&
            (payload[..6].SequenceEqual("GIF87a"u8) || payload[..6].SequenceEqual("GIF89a"u8)))
        {
            return "image/gif";
        }
        if (payload.Length >= 12 &&
            payload[..4].SequenceEqual("RIFF"u8) && payload[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        return null;
    }

    internal static bool IsSupportedEmbeddedRasterType(string mediaType, MarkdownRasterFormat? detectedFormat) =>
        (mediaType.Equals("image/png", StringComparison.OrdinalIgnoreCase) && detectedFormat == MarkdownRasterFormat.Png) ||
        ((mediaType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
          mediaType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase)) && detectedFormat == MarkdownRasterFormat.Jpeg) ||
        (mediaType.Equals("image/gif", StringComparison.OrdinalIgnoreCase) && detectedFormat == MarkdownRasterFormat.Gif) ||
        (mediaType.Equals("image/webp", StringComparison.OrdinalIgnoreCase) && detectedFormat == MarkdownRasterFormat.WebP);
}
