namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// SVG content rules shared by the host's preparation budget and its open-time
/// preflight. Rules that only one pass applies stay with that pass; the static
/// snapshot's narrower motion-removal list is deliberately separate.
/// </summary>
internal static class SvgContentRules
{
    /// <summary>The SVG element namespace.</summary>
    internal const string SvgNamespace = "http://www.w3.org/2000/svg";

    /// <summary>
    /// CSS properties that animate a document. Both validation passes reject
    /// a declaration of any of them; prefixed WebKit spellings are included.
    /// </summary>
    internal static bool IsMotionProperty(string property)
        => property.Equals("animation", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("animation-", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("transition", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("transition-", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("-webkit-animation", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("-webkit-animation-", StringComparison.OrdinalIgnoreCase) ||
            property.Equals("-webkit-transition", StringComparison.OrdinalIgnoreCase) ||
            property.StartsWith("-webkit-transition-", StringComparison.OrdinalIgnoreCase);
}
