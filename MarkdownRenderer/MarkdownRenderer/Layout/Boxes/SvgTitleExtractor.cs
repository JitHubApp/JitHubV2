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
/// Public for unit-test access (mirrors <see cref="SvgFeatureScanner"/>).
/// </summary>
public static class SvgTitleExtractor
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

        string? title = MatchOrNull(text, TitleRx);
        string? desc = MatchOrNull(text, DescRx);
        return new Metadata(title, desc);
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
