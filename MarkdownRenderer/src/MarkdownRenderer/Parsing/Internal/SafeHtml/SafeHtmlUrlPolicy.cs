using System;
using System.IO;

namespace MarkdownRenderer.Parsing.Internal.SafeHtml;

/// <summary>
/// Admits link and image URLs from already-decoded attribute values, and
/// derives the accessible fallback name for images without alt text.
/// </summary>
internal static class SafeHtmlUrlPolicy
{
    internal static bool TryGetSafeLink(SafeHtmlElement element, out string url)
    {
        url = string.Empty;
        return element.TryGetAttribute("href", out string value) &&
            TryNormalizeUrl(value, isImage: false, out url);
    }

    internal static bool TryGetSafeLink(SafeHtmlTag tag, out string url)
    {
        url = string.Empty;
        return tag.TryGetAttribute("href", out string value) &&
            TryNormalizeUrl(value, isImage: false, out url);
    }

    internal static bool TryGetSafeImageSource(SafeHtmlElement element, out string source)
    {
        source = string.Empty;
        return element.TryGetAttribute("src", out string value) &&
            TryNormalizeUrl(value, isImage: true, out source);
    }

    internal static bool TryGetSafeImageSource(SafeHtmlTag tag, out string source)
    {
        source = string.Empty;
        return tag.TryGetAttribute("src", out string value) &&
            TryNormalizeUrl(value, isImage: true, out source);
    }

    internal static bool TryNormalizeImageSource(string value, out string source) =>
        TryNormalizeUrl(value, isImage: true, out source);

    private static bool TryNormalizeUrl(string value, bool isImage, out string normalized)
    {
        // ParseAttributes has already decoded HTML references, including the
        // poster/srcset values passed directly by media and picture handling.
        // A second decode would change authored URL paths and query values.
        normalized = (value ?? string.Empty).Trim();
        if (normalized.Length == 0 || normalized.Length > SafeHtmlParser.MaxAttributeValueLength)
        {
            normalized = string.Empty;
            return false;
        }

        foreach (char character in normalized)
        {
            if (char.IsControl(character))
            {
                normalized = string.Empty;
                return false;
            }
        }

        if (!Uri.TryCreate(normalized, UriKind.RelativeOrAbsolute, out Uri? uri) || !uri.IsAbsoluteUri)
        {
            return true;
        }

        bool allowed = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            (!isImage && uri.Scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase)) ||
            (isImage && uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase) &&
             normalized.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase));
        if (allowed)
        {
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    internal static string ResolveMissingImageAlternative(
        string source,
        string localizedImageName)
    {
        // GitHub assigns the file name as the accessible alternative for
        // authored animated images that omit alt text. Matching that behavior
        // keeps large animation galleries useful to screen readers while
        // leaving ordinary missing-alt images on the localized generic name.
        // Camo URLs do not expose the original extension in their path, so
        // remote GIFs continue to receive the same generic name as GitHub.
        string path = source ?? string.Empty;
        int suffix = path.IndexOfAny(['?', '#']);
        if (suffix >= 0)
            path = path[..suffix];

        string fileName;
        try
        {
            fileName = Uri.UnescapeDataString(Path.GetFileName(path.Replace('\\', '/')));
        }
        catch (UriFormatException)
        {
            return localizedImageName;
        }

        return fileName.Length is > 0 and <= 256 &&
               Path.GetExtension(fileName).Equals(".gif", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : localizedImageName;
    }
}
