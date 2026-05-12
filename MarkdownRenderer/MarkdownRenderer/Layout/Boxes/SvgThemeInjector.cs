using System;
using System.Text;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Pre-processes SVG bytes to inject a <c>color</c> attribute on the root
/// <c>&lt;svg&gt;</c> element so the document's <c>currentColor</c> tokens
/// (commonly used by GitHub Octicons and other UI iconography) resolve to
/// the host control's theme foreground. Operates as a pure byte
/// transformation so both Tier A (Win2D) and Tier B (Skia) benefit.
///
/// The transformation is conservative:
/// <list type="bullet">
///   <item>If the root <c>&lt;svg&gt;</c> already has a <c>color=</c>
///         attribute, the original bytes are returned unchanged (author
///         intent wins).</item>
///   <item>If no <c>&lt;svg</c> opening tag is found, the original bytes
///         are returned unchanged.</item>
///   <item>The injection is a single attribute pair
///         (<c>color="#RRGGBB"</c>); no other content is mutated. Inner
///         <c>currentColor</c> tokens (which CSS resolves through the
///         element's inherited color) pick up the new value automatically
///         via the SVG cascade in both backends.</item>
/// </list>
/// Public so unit tests can assert the byte-level contract directly.
/// </summary>
public static class SvgThemeInjector
{
    /// <summary>
    /// Injects <c>color="#RRGGBB"</c> on the root <c>&lt;svg&gt;</c> tag
    /// using <paramref name="r"/>/<paramref name="g"/>/<paramref name="b"/>.
    /// Returns the original buffer unchanged if injection isn't applicable.
    /// </summary>
    public static byte[] Inject(byte[] svgBytes, byte r, byte g, byte b)
    {
        if (svgBytes is null || svgBytes.Length == 0) return svgBytes!;

        // Decode just enough to locate the root <svg ... > opening. SVG bytes
        // are typically UTF-8 with optional BOM; treat as UTF-8 without
        // round-tripping the entire buffer for performance.
        string text;
        try { text = Encoding.UTF8.GetString(svgBytes); }
        catch { return svgBytes; }

        int openIdx = IndexOfIgnoreCase(text, "<svg", 0);
        if (openIdx < 0) return svgBytes;

        // Find the end of the opening tag — first '>' after openIdx that
        // isn't inside a quoted attribute value. This is a lenient scan
        // sufficient for well-formed SVGs.
        int tagEnd = FindTagEnd(text, openIdx);
        if (tagEnd < 0) return svgBytes;

        string tagBody = text.Substring(openIdx, tagEnd - openIdx);
        if (HasColorAttribute(tagBody)) return svgBytes;

        string colorAttr = $" color=\"#{r:X2}{g:X2}{b:X2}\"";
        // Inject immediately after "<svg" (preserves any namespaces and
        // attributes that follow on the same line). The 4-char "<svg"
        // length is fixed per the OpenIdx match.
        var sb = new StringBuilder(text.Length + colorAttr.Length);
        sb.Append(text, 0, openIdx + 4);
        sb.Append(colorAttr);
        sb.Append(text, openIdx + 4, text.Length - (openIdx + 4));
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static int IndexOfIgnoreCase(string s, string token, int start)
        => s.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);

    private static int FindTagEnd(string s, int from)
    {
        bool inQuote = false;
        char quote = '"';
        for (int i = from; i < s.Length; i++)
        {
            char c = s[i];
            if (inQuote)
            {
                if (c == quote) inQuote = false;
            }
            else
            {
                if (c == '"' || c == '\'') { inQuote = true; quote = c; }
                else if (c == '>') return i;
            }
        }
        return -1;
    }

    private static bool HasColorAttribute(string tagBody)
    {
        // Match only "color=" (whole word), not "stroke-color=" / "fill-color=".
        int idx = 0;
        while (true)
        {
            idx = tagBody.IndexOf("color", idx, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            // Must be preceded by whitespace or start-of-tag.
            if (idx == 0)
            {
                idx += 5;
                continue;
            }
            char prev = tagBody[idx - 1];
            int afterIdx = idx + 5;
            // Must be followed by '=' (allow optional whitespace around it).
            int j = afterIdx;
            while (j < tagBody.Length && char.IsWhiteSpace(tagBody[j])) j++;
            bool isAttr = (prev == ' ' || prev == '\t' || prev == '\r' || prev == '\n')
                          && j < tagBody.Length
                          && tagBody[j] == '=';
            if (isAttr) return true;
            idx = afterIdx;
        }
    }
}
