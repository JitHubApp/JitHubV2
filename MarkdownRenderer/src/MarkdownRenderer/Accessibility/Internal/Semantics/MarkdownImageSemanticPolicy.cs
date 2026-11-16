using System;

namespace MarkdownRenderer.Accessibility.Internal.Semantics;

/// <summary>Pure policy for projecting image alt/link metadata into UIA text.</summary>
internal static class MarkdownImageSemanticPolicy
{
    public static string? GetInlineAccessibleName(
        string? altText,
        bool isLinked,
        string? linkTitle,
        string? linkUrl,
        string localizedImageName)
    {
        if (!string.IsNullOrEmpty(altText))
            return altText;
        if (!isLinked)
            return null;
        return GetLinkedImageFallbackName(linkTitle, linkUrl, localizedImageName);
    }

    /// <summary>
    /// The name of a linked image that has no alt text. The link still needs a
    /// name that says where it goes, so this uses the link title, then the link
    /// target (as screen readers announce an unnamed link on the web), and only
    /// then the generic localized image name.
    /// </summary>
    public static string GetLinkedImageFallbackName(
        string? linkTitle,
        string? linkUrl,
        string localizedImageName)
    {
        if (!string.IsNullOrWhiteSpace(linkTitle))
            return linkTitle;
        return GetReadableLinkTarget(linkUrl) ?? localizedImageName;
    }

    public static bool IsDecorativeBlock(string? altText)
        => string.IsNullOrEmpty(altText);

    /// <summary>
    /// A web link reads as its host and path ("crates.io/crates/deno"); the
    /// scheme, query and fragment are left to the full URL in the help text.
    /// Other targets read as written. A bare fragment says nothing, so it has none.
    /// </summary>
    internal static string? GetReadableLinkTarget(string? linkUrl)
    {
        if (string.IsNullOrWhiteSpace(linkUrl))
            return null;

        string target = linkUrl.Trim();
        if (Uri.TryCreate(target, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
            uri.Host.Length > 0)
        {
            string path = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');
            return string.Concat(uri.Host, path);
        }

        return target.TrimStart('#').Length == 0 ? null : target;
    }
}
