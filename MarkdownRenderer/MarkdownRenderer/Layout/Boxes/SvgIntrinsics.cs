using System;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Pure-logic helpers for SVG image handling. Detects whether a URL points
/// to an SVG resource and extracts the intrinsic size from the SVG XML
/// without parsing the whole document. Used by <see cref="ImageBox"/> and
/// exposed publicly so the test suite can exercise them without requiring
/// a WinUI / Win2D test host.
/// </summary>
internal static class SvgIntrinsics
{
    /// <summary>
    /// Returns true if the URL appears to refer to an SVG resource — either
    /// because its path ends with <c>.svg</c> (ignoring query/hash) or
    /// because it's a <c>data:image/svg+xml</c> URI.
    /// </summary>
    public static bool LooksLikeSvg(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        int q = url.IndexOfAny(new[] { '?', '#' });
        string path = q >= 0 ? url.Substring(0, q) : url;
        return path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("data:image/svg+xml", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses the SVG bytes for the intrinsic width/height. Returns
    /// <c>(0, 0)</c> if neither explicit dimensions nor a usable
    /// <c>viewBox</c> are present. Never throws.
    /// </summary>
    public static (double Width, double Height) TryExtractIntrinsicSize(byte[]? svgBytes)
    {
        if (svgBytes is null || svgBytes.Length == 0) return (0, 0);
        try
        {
            string xml = System.Text.Encoding.UTF8.GetString(svgBytes);
            int rootStart = xml.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
            if (rootStart < 0) return (0, 0);
            int rootEnd = xml.IndexOf('>', rootStart);
            if (rootEnd < 0) return (0, 0);
            string opening = xml.Substring(rootStart, rootEnd - rootStart);

            double width = ParseAttribute(opening, "width");
            double height = ParseAttribute(opening, "height");
            if (width > 0 && height > 0) return (width, height);

            string? vb = ParseStringAttribute(opening, "viewBox");
            if (vb is not null)
            {
                var parts = vb.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4
                    && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vw)
                    && double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vh)
                    && vw > 0 && vh > 0)
                    return (vw, vh);
            }
            return (0, 0);
        }
        catch { return (0, 0); }
    }

    private static double ParseAttribute(string element, string attr)
    {
        var s = ParseStringAttribute(element, attr);
        if (s is null) return 0;
        int len = 0;
        while (len < s.Length && (char.IsDigit(s[len]) || s[len] == '.' || s[len] == '-')) len++;
        if (len == 0) return 0;
        // Reject relative units — percentages on the root <svg> are not intrinsic
        // dimensions and should defer to viewBox per the SVG sizing spec.
        if (len < s.Length && s[len] == '%') return 0;
        return double.TryParse(s.Substring(0, len), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static string? ParseStringAttribute(string element, string attr)
    {
        // Match any XML-whitespace before the attribute name so we don't pick
        // up an attribute substring inside another value (e.g. " stroke-width=").
        int idx = element.IndexOf(' ' + attr + '=', StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = element.IndexOf('\t' + attr + '=', StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = element.IndexOf('\n' + attr + '=', StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = element.IndexOf('\r' + attr + '=', StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        int eq = element.IndexOf('=', idx);
        if (eq < 0) return null;
        int p = eq + 1;
        while (p < element.Length && (element[p] == ' ' || element[p] == '\t')) p++;
        if (p >= element.Length) return null;
        char quote = element[p];
        int start, end;
        if (quote == '"' || quote == '\'')
        {
            start = p + 1;
            end = element.IndexOf(quote, start);
            if (end < 0) end = element.Length;
        }
        else
        {
            // Unquoted: read until whitespace or '>'.
            start = p;
            end = start;
            while (end < element.Length && element[end] != ' ' && element[end] != '\t'
                   && element[end] != '\n' && element[end] != '>') end++;
        }
        return end > start ? element.Substring(start, end - start) : null;
    }
}
