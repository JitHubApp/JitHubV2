using System;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Extracts the optional <c>&lt;title&gt;</c> and <c>&lt;desc&gt;</c>
/// elements from an SVG document for accessibility (UIA <c>Name</c> /
/// <c>HelpText</c>) per WAI-ARIA SVG mapping. Only the first occurrence of
/// each at or near the document root is considered — nested/inner titles
/// (e.g. on <c>&lt;g&gt;</c>) are intentionally ignored to match assistive-
/// technology expectations and to avoid leaking decorative metadata into
/// the accessible name.
/// Public for unit-test access.
/// </summary>
internal static class SvgTitleExtractor
{
    private static readonly Regex TitleRx = new(
        @"<title\b[^>]*>(?<v>[\s\S]*?)</title>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DescRx = new(
        @"<desc\b[^>]*>(?<v>[\s\S]*?)</desc>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Result of extracting accessibility metadata from SVG bytes.
    /// Both fields are null when the SVG is unparseable or the elements
    /// are missing; callers fall back to alt text.
    /// </summary>
    public readonly record struct Metadata(string? Title, string? Desc);

    /// <summary>
    /// Parses <paramref name="svgBytes"/> as UTF-8 and returns the first
    /// <c>&lt;title&gt;</c> / <c>&lt;desc&gt;</c> text. Whitespace inside
    /// the elements is collapsed to a single space and trimmed. XML entity
    /// references (<c>&amp;amp;</c>, <c>&amp;lt;</c>, <c>&amp;gt;</c>,
    /// <c>&amp;quot;</c>, <c>&amp;apos;</c>) are resolved.
    /// </summary>
    public static Metadata Extract(byte[] svgBytes)
    {
        if (svgBytes is null || svgBytes.Length == 0) return default;
        string text;
        try { text = Encoding.UTF8.GetString(svgBytes); }
        catch { return default; }

        // Restrict the search to the root <svg>'s direct child territory,
        // skipping any <defs>/<symbol>/<mask>/<clipPath>/<pattern> subtrees
        // — titles inside those describe sub-resources, not the document.
        string scope = ExtractRootScope(text);
        string? title = MatchOrNull(scope, TitleRx);
        string? desc = MatchOrNull(scope, DescRx);
        return new Metadata(title, desc);
    }

    private static readonly string[] HiddenContainers =
        { "defs", "symbol", "mask", "clippath", "pattern" };

    /// <summary>
    /// Returns a copy of <paramref name="text"/> with hidden-container
    /// subtrees blanked out so a regex can find the first
    /// <c>&lt;title&gt;</c>/<c>&lt;desc&gt;</c> at the root level without
    /// matching nested ones inside <c>&lt;defs&gt;</c> etc.
    /// </summary>
    private static string ExtractRootScope(string text)
    {
        var sb = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            int lt = text.IndexOf('<', i);
            if (lt < 0)
            {
                sb.Append(text, i, text.Length - i);
                break;
            }
            sb.Append(text, i, lt - i);
            // Detect <containerName ...> opening of a hidden container.
            int hidden = -1;
            for (int k = 0; k < HiddenContainers.Length; k++)
            {
                var name = HiddenContainers[k];
                if (lt + 1 + name.Length < text.Length
                    && string.Compare(text, lt + 1, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    char nx = text[lt + 1 + name.Length];
                    if (nx == ' ' || nx == '\t' || nx == '\r' || nx == '\n' || nx == '>' || nx == '/')
                    { hidden = k; break; }
                }
            }
            if (hidden < 0)
            {
                sb.Append('<');
                i = lt + 1;
                continue;
            }
            // Skip the entire subtree (handles nested same-name containers).
            string n = HiddenContainers[hidden];
            int openTagEnd = text.IndexOf('>', lt);
            if (openTagEnd < 0) { i = text.Length; break; }
            // Self-closing initial container (e.g. <defs/>) has no children
            // to skip — advance past the tag and continue scanning at the
            // sibling level so a following root <title>/<desc> is still
            // visible.
            if (openTagEnd > 0 && text[openTagEnd - 1] == '/')
            {
                i = openTagEnd + 1;
                continue;
            }
            int depth = 1;
            int j = openTagEnd + 1;
            while (j < text.Length && depth > 0)
            {
                int next = text.IndexOf('<', j);
                if (next < 0) { j = text.Length; break; }
                if (next + 1 < text.Length && text[next + 1] == '/'
                    && next + 2 + n.Length <= text.Length
                    && string.Compare(text, next + 2, n, 0, n.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    depth--;
                    int gt = text.IndexOf('>', next);
                    if (gt < 0) { j = text.Length; break; }
                    j = gt + 1;
                    if (depth == 0) break;
                }
                else if (next + 1 + n.Length <= text.Length
                    && string.Compare(text, next + 1, n, 0, n.Length, StringComparison.OrdinalIgnoreCase) == 0)
                {
                    char nx = text[next + 1 + n.Length];
                    if (nx == ' ' || nx == '\t' || nx == '\r' || nx == '\n' || nx == '>' || nx == '/')
                    {
                        // Self-closing? <defs ... />
                        int gt = text.IndexOf('>', next);
                        if (gt < 0) { j = text.Length; break; }
                        if (gt > 0 && text[gt - 1] != '/') depth++;
                        j = gt + 1;
                    }
                    else j = next + 1;
                }
                else j = next + 1;
            }
            i = j;
        }
        return sb.ToString();
    }

    private static string? MatchOrNull(string text, Regex rx)
    {
        var m = rx.Match(text);
        if (!m.Success) return null;
        string raw = m.Groups["v"].Value;
        return Normalize(raw);
    }

    private static string? Normalize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Decode common XML entities — full XML parser would be overkill.
        var s = raw
            .Replace("&amp;", "\uE000\uE001amp\uE002") // protect
            .Replace("&lt;", "<")
            .Replace("&gt;", ">")
            .Replace("&quot;", "\"")
            .Replace("&apos;", "'")
            .Replace("\uE000\uE001amp\uE002", "&");

        // Collapse whitespace — newlines / indentation inside <title> are
        // pure formatting noise and should not surface in the UIA name.
        var sb = new StringBuilder(s.Length);
        bool prevSpace = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!prevSpace) { sb.Append(' '); prevSpace = true; }
            }
            else
            {
                sb.Append(c);
                prevSpace = false;
            }
        }
        var result = sb.ToString().Trim();
        return result.Length == 0 ? null : result;
    }
}
