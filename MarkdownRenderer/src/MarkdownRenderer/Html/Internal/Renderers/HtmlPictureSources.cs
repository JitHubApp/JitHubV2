using System;
using System.Collections.Generic;
using System.Linq;
using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Html.Internal.Renderers;

/// <summary>
/// Selects the deterministic subset of picture source elements the
/// renderer can honor without viewport or DPR negotiation.
/// </summary>
internal static class HtmlPictureSources
{
    private static readonly HashSet<string> SupportedPictureSourceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/gif",
        "image/jpeg",
        "image/png",
    };

    internal static SafeHtmlElement? SelectPictureSource(SafeHtmlElement picture, bool isDark)
    {
        SafeHtmlElement[] sources = picture.Children.OfType<SafeHtmlElement>()
            .Where(static element => element.Name == "source")
            .ToArray();
        string expected = isDark ? "dark" : "light";
        foreach (SafeHtmlElement source in sources)
        {
            if (source.TryGetAttribute("media", out string media) &&
                NormalizeMediaQuery(media).Equals($"(prefers-color-scheme:{expected})", StringComparison.OrdinalIgnoreCase) &&
                (!source.TryGetAttribute("type", out string type) ||
                 IsSupportedPictureSourceType(type)) &&
                TryGetSinglePictureSourceCandidate(source, out _))
            {
                return source;
            }
        }

        // Match the deterministic subset of <picture> source selection that we
        // can reproduce without viewport/DPR-dependent srcset negotiation:
        // an explicitly supported raster MIME type, no media/sizes condition,
        // and exactly one URL with no density/width descriptor. Anything more
        // complex remains on the authored <img> fallback rather than guessing.
        foreach (SafeHtmlElement source in sources)
        {
            if (source.TryGetAttribute("media", out _) ||
                source.TryGetAttribute("sizes", out _) ||
                !source.TryGetAttribute("type", out string type) ||
                !IsSupportedPictureSourceType(type) ||
                !TryGetSinglePictureSourceCandidate(source, out _))
            {
                continue;
            }

            return source;
        }

        return null;
    }

    internal static bool TryGetSinglePictureSourceCandidate(SafeHtmlElement source, out string candidate)
    {
        candidate = string.Empty;
        if (!source.TryGetAttribute("srcset", out string sourceSet) ||
            string.IsNullOrWhiteSpace(sourceSet) ||
            sourceSet.Contains(','))
        {
            return false;
        }

        string[] parts = sourceSet.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 1 || !SafeHtmlParser.TryNormalizeImageSource(parts[0], out candidate))
        {
            candidate = string.Empty;
            return false;
        }

        return true;
    }

    private static string NormalizeMediaQuery(string media) =>
        string.Concat(media.Where(character => !char.IsWhiteSpace(character)));

    private static bool IsSupportedPictureSourceType(string type)
    {
        string normalized = type.Trim();
        return SupportedPictureSourceTypes.Contains(normalized) ||
            normalized.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("image/webp", StringComparison.OrdinalIgnoreCase);
    }
}
