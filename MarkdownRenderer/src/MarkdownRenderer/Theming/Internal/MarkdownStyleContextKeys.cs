using System;

namespace MarkdownRenderer.Theming.Internal;

/// <summary>Helpers for the renderer's non-role keys carried in style contexts.</summary>
internal static class MarkdownStyleContextKeys
{
    private const string ListDepthPrefix = "ListDepth";

    /// <summary>
    /// Parses the same list-depth metadata syntax used by compiled theme resolution.
    /// A false result means the key remains eligible to represent a custom semantic role.
    /// </summary>
    internal static bool TryParseListNestingDepth(string? key, out int depth)
    {
        depth = 0;
        return key is not null &&
            key.StartsWith(ListDepthPrefix, StringComparison.Ordinal) &&
            int.TryParse(
                key.AsSpan(ListDepthPrefix.Length),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out depth);
    }
}
