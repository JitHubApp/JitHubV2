using System.Text;
using System.Xml;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Reads a DOM subtree's text without recursion, checking cancellation per
/// node and per bounded chunk, so a deep or very large subtree (such as an
/// author style element) cannot overflow the stack or delay teardown.
/// </summary>
internal static class SvgDomText
{
    internal static string GetInnerTextIterative(XmlNode root, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (root.FirstChild is { NextSibling: null } onlyChild &&
            onlyChild.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return onlyChild.Value ?? string.Empty;
        }

        var builder = new StringBuilder();
        XmlNode? current = root.FirstChild;
        while (current is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or
                XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace)
            {
                AppendStringWithCancellation(builder, current.Value, cancellationToken);
            }

            if (current.FirstChild is { } firstChild)
            {
                current = firstChild;
                continue;
            }

            current = AdvanceAfterSubtree(current, root, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return builder.ToString();
    }

    internal static XmlNode? AdvanceAfterSubtree(
        XmlNode completedNode,
        XmlNode root,
        CancellationToken cancellationToken)
    {
        XmlNode? current = completedNode;
        while (current is not null && !ReferenceEquals(current, root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (current.NextSibling is { } nextSibling)
                return nextSibling;
            current = current.ParentNode;
        }

        return null;
    }

    internal static void AppendStringWithCancellation(
        StringBuilder builder,
        string? value,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(value))
            return;

        for (int offset = 0; offset < value.Length; offset += SvgCssScanner.CssCancellationCheckInterval)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(SvgCssScanner.CssCancellationCheckInterval, value.Length - offset);
            builder.Append(value, offset, count);
        }
    }
}
