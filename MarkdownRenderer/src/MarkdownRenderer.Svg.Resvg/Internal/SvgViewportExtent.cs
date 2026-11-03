using System.Globalization;
using System.Xml;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Reads the root SVG viewport extent from width/height or viewBox.
/// </summary>
internal static class SvgViewportExtent
{
    internal static bool TryReadSvgViewportExtent(
        XmlElement? root,
        bool horizontal,
        CancellationToken cancellationToken,
        out double extent)
    {
        cancellationToken.ThrowIfCancellationRequested();
        extent = double.NaN;
        if (root is null)
            return false;

        string dimension = root.GetAttribute(horizontal ? "width" : "height");
        if (SvgForeignObjectText.TryReadLength(dimension, defaultValue: double.NaN, out extent) && extent > 0)
            return true;

        return TryParseViewBoxExtent(root.GetAttribute("viewBox"), horizontal, cancellationToken, out extent);
    }

    internal static bool TryParseViewBoxExtent(
        string viewBox,
        bool horizontal,
        CancellationToken cancellationToken,
        out double extent)
    {
        cancellationToken.ThrowIfCancellationRequested();
        extent = double.NaN;
        Range requestedToken = default;
        int tokenCount = 0;
        int charactersUntilCancellationCheck = SvgCssScanner.CssCancellationCheckInterval;
        int tokenStart = 0;
        for (int cursor = 0; cursor <= viewBox.Length; cursor++)
        {
            if (cursor < viewBox.Length)
                SvgCssScanner.CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);

            if (cursor < viewBox.Length && viewBox[cursor] is not (' ' or ','))
                continue;

            int trimmedStart = tokenStart;
            int trimmedEnd = cursor;
            while (trimmedStart < trimmedEnd && char.IsWhiteSpace(viewBox[trimmedStart]))
            {
                SvgCssScanner.CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                trimmedStart++;
            }
            while (trimmedEnd > trimmedStart && char.IsWhiteSpace(viewBox[trimmedEnd - 1]))
            {
                SvgCssScanner.CheckCssCancellation(ref charactersUntilCancellationCheck, cancellationToken);
                trimmedEnd--;
            }

            if (trimmedStart < trimmedEnd)
            {
                if (tokenCount == (horizontal ? 2 : 3))
                    requestedToken = new Range(trimmedStart, trimmedEnd);
                tokenCount++;
            }

            tokenStart = cursor + 1;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return tokenCount == 4 &&
            double.TryParse(viewBox.AsSpan(requestedToken), NumberStyles.Float,
                CultureInfo.InvariantCulture, out extent) &&
            double.IsFinite(extent) &&
            extent > 0;
    }
}
