using System.Security.Cryptography;
using System.Text;
using System.Xml;
using MarkdownRenderer.Images;

namespace MarkdownRenderer.Svg.Resvg.Internal.Preflight;

/// <summary>
/// The two failure kinds a host preflight reports for untrusted SVG input.
/// </summary>
internal static class SvgPreflightFailures
{
    internal static MarkdownSvgException Unsupported(string message, Exception? inner = null) =>
        new(MarkdownSvgFailureReason.UnsupportedContent, message, inner);

    internal static MarkdownSvgException Resource(string message) =>
        new(MarkdownSvgFailureReason.ResourceLimitExceeded, message);
}
