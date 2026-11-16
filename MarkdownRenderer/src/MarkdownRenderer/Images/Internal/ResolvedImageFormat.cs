using System;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Images.Internal;

/// <summary>
/// Classifies a resolved image asset as SVG from its bytes and content type.
/// The resolved representation, not the requested URI, is authoritative.
/// </summary>
internal static class ResolvedImageFormat
{
    internal static bool LooksLikeSvg(MarkdownImageAsset asset)
    {
        // Resolved payload metadata supersedes the spelling of the requested URI.
        // Redirecting image services commonly retain an .svg source URL while
        // returning a raster placeholder. Treating ResolvedUri as a format signal
        // in that case feeds PNG/JPEG bytes to the SVG provider and produces a
        // misleading unavailable state.
        if (LooksLikeSvgBytes(asset.Bytes))
            return true;

        string? contentType = asset.ContentType;
        if (string.IsNullOrWhiteSpace(contentType))
            return false;

        ReadOnlySpan<char> mediaType = contentType.AsSpan();
        int parameterSeparator = mediaType.IndexOf(';');
        if (parameterSeparator >= 0)
            mediaType = mediaType[..parameterSeparator];
        mediaType = mediaType.Trim();

        return mediaType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase) ||
               mediaType.Equals("image/svg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSvgBytes(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
            return false;

        int index = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            index = 3;

        while (index < bytes.Length && char.IsWhiteSpace((char)bytes[index]))
            index++;

        int length = Math.Min(bytes.Length - index, 512);
        if (length <= 0)
            return false;

        string prefix = System.Text.Encoding.UTF8.GetString(bytes, index, length);
        return prefix.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
               (prefix.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) &&
                prefix.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0);
    }
}
