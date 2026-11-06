using System;
using System.Threading;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Images;

/// <summary>
/// Builds a <see cref="MarkdownSvgSourceInfo"/> from the XML events of a pass
/// an <see cref="IMarkdownSvgRenderer"/> already makes over a document, such
/// as its validation in <see cref="IMarkdownSvgRenderer.PrepareSource"/>, so
/// the view does not read the document a second time.
/// </summary>
/// <remarks>
/// Report events in document order with depths relative to the root element,
/// which is depth 0. The reader keeps only bounded root geometry and the first
/// direct-child title and description, and never moves the caller's reader.
/// </remarks>
public sealed class MarkdownSvgSourceInfoReader
{
    private readonly SvgMetadataCollector _collector;

    /// <summary>Initializes a reader for one document.</summary>
    /// <param name="cancellationToken">Cancels collection of long title or description text.</param>
    public MarkdownSvgSourceInfoReader(CancellationToken cancellationToken = default)
        => _collector = new SvgMetadataCollector(cancellationToken);

    /// <summary>Reports the start of an element.</summary>
    /// <param name="depth">The element's depth; the root is 0.</param>
    /// <param name="localName">The element's local name.</param>
    /// <param name="namespaceUri">The element's namespace URI, or null.</param>
    /// <param name="isEmptyElement">Whether the element has no content.</param>
    /// <returns>False when the first element is not an SVG root.</returns>
    public bool ObserveElement(int depth, string localName, string? namespaceUri, bool isEmptyElement)
        => _collector.ObserveElement(depth, localName, namespaceUri, isEmptyElement);

    /// <summary>Reports an attribute of the most recently reported element.</summary>
    /// <param name="ownerDepth">The depth of the element that owns the attribute.</param>
    /// <param name="localName">The attribute's local name.</param>
    /// <param name="namespaceUri">The attribute's namespace URI, or null.</param>
    /// <param name="value">The decoded attribute value.</param>
    public void ObserveAttribute(int ownerDepth, string localName, string? namespaceUri, string value)
        => _collector.ObserveAttribute(ownerDepth, localName, namespaceUri, value);

    /// <summary>Reports decoded text or CDATA content.</summary>
    /// <param name="depth">The depth of the text node.</param>
    /// <param name="value">The decoded text.</param>
    public void ObserveText(int depth, ReadOnlySpan<char> value)
        => _collector.AppendText(depth, value);

    /// <summary>Reports the end of an element.</summary>
    /// <param name="depth">The element's depth.</param>
    public void ObserveEndElement(int depth)
        => _collector.EndElement(depth);

    /// <summary>Gets the information collected so far.</summary>
    public MarkdownSvgSourceInfo GetResult() => new(_collector.GetMetadata());
}
