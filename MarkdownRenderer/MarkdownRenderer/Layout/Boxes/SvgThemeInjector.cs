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

        int openIdx = FindRootSvgOpen(text);
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

    /// <summary>Locates the root <c>&lt;svg</c> opening, skipping XML
    /// declarations (<c>&lt;?xml ?&gt;</c>), comments
    /// (<c>&lt;!-- --&gt;</c>), and DOCTYPEs (<c>&lt;!DOCTYPE ...&gt;</c>)
    /// that may appear before it. Returns -1 if no root svg is found.</summary>
    private static int FindRootSvgOpen(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            int lt = s.IndexOf('<', i);
            if (lt < 0) return -1;
            if (lt + 4 <= s.Length && string.Compare(s, lt, "<svg", 0, 4, StringComparison.OrdinalIgnoreCase) == 0
                && (lt + 4 == s.Length || s[lt + 4] == ' ' || s[lt + 4] == '\t' || s[lt + 4] == '\r' || s[lt + 4] == '\n' || s[lt + 4] == '>' || s[lt + 4] == '/'))
            {
                return lt;
            }
            // Skip <!-- ... -->
            if (lt + 4 <= s.Length && s[lt + 1] == '!' && s[lt + 2] == '-' && s[lt + 3] == '-')
            {
                int end = s.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                if (end < 0) return -1;
                i = end + 3;
                continue;
            }
            // Skip <?xml ... ?>
            if (lt + 1 < s.Length && s[lt + 1] == '?')
            {
                int end = s.IndexOf("?>", lt + 2, StringComparison.Ordinal);
                if (end < 0) return -1;
                i = end + 2;
                continue;
            }
            // Skip <!DOCTYPE ...> (no quoted-content edge cases needed for SVG)
            if (lt + 1 < s.Length && s[lt + 1] == '!')
            {
                int end = s.IndexOf('>', lt + 2);
                if (end < 0) return -1;
                i = end + 1;
                continue;
            }
            // Some other element before <svg> — bail out, malformed SVG.
            return -1;
        }
        return -1;
    }

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
        // Walk attributes name-only, skipping over any quoted values. This
        // avoids false positives for the substring "color=" appearing inside
        // an attribute value (e.g. inline style="background-color:red").
        int i = 0;
        while (i < tagBody.Length)
        {
            // Skip whitespace between attributes.
            while (i < tagBody.Length && (tagBody[i] == ' ' || tagBody[i] == '\t' || tagBody[i] == '\r' || tagBody[i] == '\n')) i++;
            if (i >= tagBody.Length) break;
            // Stop at tag terminators — a trailing '/' (self-closing root
            // <svg/>) or '>' (defensive; FindTagEnd should have stripped it).
            if (tagBody[i] == '/' || tagBody[i] == '>') break;

            // Read an attribute name up to '=' or whitespace.
            int nameStart = i;
            while (i < tagBody.Length && tagBody[i] != '=' && tagBody[i] != ' ' && tagBody[i] != '\t' && tagBody[i] != '\r' && tagBody[i] != '\n' && tagBody[i] != '/' && tagBody[i] != '>')
                i++;
            int nameLen = i - nameStart;
            if (nameLen == 5 && string.Compare(tagBody, nameStart, "color", 0, 5, StringComparison.OrdinalIgnoreCase) == 0)
            {
                int j = i;
                while (j < tagBody.Length && (tagBody[j] == ' ' || tagBody[j] == '\t')) j++;
                if (j < tagBody.Length && tagBody[j] == '=') return true;
            }

            // Skip optional '=' and quoted value to advance past this attribute.
            while (i < tagBody.Length && (tagBody[i] == ' ' || tagBody[i] == '\t')) i++;
            if (i < tagBody.Length && tagBody[i] == '=')
            {
                i++;
                while (i < tagBody.Length && (tagBody[i] == ' ' || tagBody[i] == '\t')) i++;
                if (i < tagBody.Length && (tagBody[i] == '"' || tagBody[i] == '\''))
                {
                    char q = tagBody[i++];
                    while (i < tagBody.Length && tagBody[i] != q) i++;
                    if (i < tagBody.Length) i++; // skip closing quote
                }
                else
                {
                    while (i < tagBody.Length && tagBody[i] != ' ' && tagBody[i] != '\t' && tagBody[i] != '\r' && tagBody[i] != '\n' && tagBody[i] != '/' && tagBody[i] != '>') i++;
                }
            }
        }
        return false;
    }
}
