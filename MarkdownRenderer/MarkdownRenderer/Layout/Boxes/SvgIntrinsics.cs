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
    public readonly struct SvgRootGeometry
    {
        public SvgRootGeometry(
            double IntrinsicWidth,
            double IntrinsicHeight,
            double ViewBoxX,
            double ViewBoxY,
            double ViewBoxWidth,
            double ViewBoxHeight)
        {
            this.IntrinsicWidth = IntrinsicWidth;
            this.IntrinsicHeight = IntrinsicHeight;
            this.ViewBoxX = ViewBoxX;
            this.ViewBoxY = ViewBoxY;
            this.ViewBoxWidth = ViewBoxWidth;
            this.ViewBoxHeight = ViewBoxHeight;
        }

        public readonly double IntrinsicWidth;
        public readonly double IntrinsicHeight;
        public readonly double ViewBoxX;
        public readonly double ViewBoxY;
        public readonly double ViewBoxWidth;
        public readonly double ViewBoxHeight;

        public bool HasIntrinsicSize => IntrinsicWidth > 0 && IntrinsicHeight > 0;
        public bool HasViewBox => ViewBoxWidth > 0 && ViewBoxHeight > 0;

        public double UserUnitToViewportScaleX =>
            HasViewBox && IntrinsicWidth > 0 ? IntrinsicWidth / ViewBoxWidth : 1;

        public double UserUnitToViewportScaleY =>
            HasViewBox && IntrinsicHeight > 0 ? IntrinsicHeight / ViewBoxHeight : 1;
    }

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
        var geometry = TryExtractRootGeometry(svgBytes);
        return (geometry.IntrinsicWidth, geometry.IntrinsicHeight);
    }

    /// <summary>
    /// Parses the SVG root for both its displayed viewport size and its
    /// internal viewBox coordinate space. Badge SVGs often declare a small
    /// CSS viewport such as 154x20 while drawing in a 1540x200 viewBox; image
    /// layout must use the viewport, while SVG text coordinates must use the
    /// viewBox mapping.
    /// </summary>
    public static SvgRootGeometry TryExtractRootGeometry(byte[]? svgBytes)
    {
        if (svgBytes is null || svgBytes.Length == 0) return default;
        try
        {
            string xml = System.Text.Encoding.UTF8.GetString(svgBytes);
            int rootStart = xml.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
            if (rootStart < 0) return default;
            int rootEnd = xml.IndexOf('>', rootStart);
            if (rootEnd < 0) return default;
            string opening = xml.Substring(rootStart, rootEnd - rootStart);

            return ParseRootGeometry(
                ParseStringAttribute(opening, "width"),
                ParseStringAttribute(opening, "height"),
                ParseStringAttribute(opening, "viewBox"));
        }
        catch { return default; }
    }

    internal static SvgRootGeometry ParseRootGeometry(
        string? widthValue,
        string? heightValue,
        string? viewBoxValue)
    {
        double width = ParseLength(widthValue);
        double height = ParseLength(heightValue);
        double viewBoxX = 0;
        double viewBoxY = 0;
        double viewBoxWidth = 0;
        double viewBoxHeight = 0;
        if (viewBoxValue is not null)
        {
            var parts = viewBoxValue.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4
                && double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vx)
                && double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vy)
                && double.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vw)
                && double.TryParse(parts[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var vh)
                && vw > 0 && vh > 0)
            {
                viewBoxX = vx;
                viewBoxY = vy;
                viewBoxWidth = vw;
                viewBoxHeight = vh;
            }
        }

        if (width <= 0 || height <= 0)
        {
            width = viewBoxWidth;
            height = viewBoxHeight;
        }

        return new SvgRootGeometry(width, height, viewBoxX, viewBoxY, viewBoxWidth, viewBoxHeight);
    }

    private static double ParseLength(string? value)
    {
        if (value is null) return 0;
        int len = 0;
        while (len < value.Length && (char.IsDigit(value[len]) || value[len] == '.' || value[len] == '-')) len++;
        if (len == 0) return 0;
        // Reject relative units — percentages on the root <svg> are not intrinsic
        // dimensions and should defer to viewBox per the SVG sizing spec.
        if (len < value.Length && value[len] == '%') return 0;
        return double.TryParse(value.Substring(0, len), System.Globalization.NumberStyles.Float,
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
