using System;
using System.IO;
using System.Threading;
using System.Xml;

namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// Bounded document-level accessibility metadata and root viewport geometry
/// collected while an SVG XML reader is already visiting the document.
/// </summary>
internal readonly struct SvgSourceMetadata(
    string? Title,
    string? Desc,
    SvgIntrinsics.SvgRootGeometry Geometry)
{
    public string? Title { get; init; } = Title;
    public string? Desc { get; init; } = Desc;
    public SvgIntrinsics.SvgRootGeometry Geometry { get; init; } = Geometry;

    private const int MaxInputBytes = 8 * 1024 * 1024;
    private const int MaxElements = 100_000;
    private const int MaxDepth = 128;
    private const int MaxAttributes = 800_000;
    private const int MaxTextNodes = 65_536;
    private const int MaxTextCharacters = 1024 * 1024;

    internal static SvgSourceMetadata Extract(byte[]? bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes is null || bytes.Length == 0 || bytes.Length > MaxInputBytes)
            return default;

        var collector = new SvgMetadataCollector(cancellationToken);
        try
        {
            using CancellationAwareMemoryStream stream = new(bytes, cancellationToken);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = false,
                DtdProcessing = DtdProcessing.Parse,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                MaxCharactersFromEntities = 1,
                MaxCharactersInDocument = MaxInputBytes,
            });
            return ReadMetadata(reader, collector, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or IOException or ArgumentException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return default;
        }
    }

    // Visits every node once under the element, attribute, text and depth
    // ceilings; any ceiling or root violation yields no metadata.
    private static SvgSourceMetadata ReadMetadata(
        XmlReader reader,
        SvgMetadataCollector collector,
        CancellationToken cancellationToken)
    {
        int elementCount = 0;
        int attributeCount = 0;
        int textNodeCount = 0;
        long textCharacters = 0;
        bool sawRoot = false;
        char[]? geometryAttributeBuffer = null;

        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Depth > MaxDepth)
                return default;

            if (reader.NodeType == XmlNodeType.DocumentType)
            {
                if (!string.IsNullOrWhiteSpace(reader.Value))
                    return default;
                continue;
            }

            if (reader.NodeType == XmlNodeType.Element)
            {
                if (!TryObserveElement(
                        reader,
                        collector,
                        cancellationToken,
                        ref elementCount,
                        ref attributeCount,
                        ref sawRoot,
                        ref geometryAttributeBuffer))
                {
                    return default;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                collector.EndElement(reader.Depth);
            }
            else if (reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.SignificantWhitespace)
            {
                if (++textNodeCount > MaxTextNodes)
                    return default;
                textCharacters += reader.Value.Length;
                if (textCharacters > MaxTextCharacters)
                    return default;
                collector.AppendText(reader.Depth, reader.Value.AsSpan());
            }
            else if (reader.NodeType == XmlNodeType.Whitespace)
            {
                collector.AppendText(reader.Depth, reader.Value.AsSpan());
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        SvgSourceMetadata metadata = sawRoot ? collector.GetMetadata() : default;
        cancellationToken.ThrowIfCancellationRequested();
        return metadata;
    }

    private static bool TryObserveElement(
        XmlReader reader,
        SvgMetadataCollector collector,
        CancellationToken cancellationToken,
        ref int elementCount,
        ref int attributeCount,
        ref bool sawRoot,
        ref char[]? geometryAttributeBuffer)
    {
        if (++elementCount > MaxElements)
            return false;

        int elementDepth = reader.Depth;
        string elementName = reader.LocalName;
        if (!sawRoot)
        {
            if (elementDepth != 0 ||
                !elementName.Equals("svg", StringComparison.OrdinalIgnoreCase) ||
                !SvgMetadataCollector.IsAcceptedRootNamespace(reader.NamespaceURI))
            {
                return false;
            }
            sawRoot = true;
        }

        if (!collector.ObserveElement(
                elementDepth,
                elementName,
                reader.NamespaceURI,
                reader.IsEmptyElement))
        {
            return false;
        }

        // XmlReader already parses and validates all attributes as
        // part of Read(). Count them without visiting every image
        // payload attribute again just to extract root metadata.
        int currentAttributeCount = reader.AttributeCount;
        if (currentAttributeCount > MaxAttributes - attributeCount)
            return false;
        attributeCount += currentAttributeCount;

        if (elementDepth == 0 && currentAttributeCount != 0)
            ObserveRootGeometryAttributes(reader, collector, cancellationToken, ref geometryAttributeBuffer);
        return true;
    }

    private static void ObserveRootGeometryAttributes(
        XmlReader reader,
        SvgMetadataCollector collector,
        CancellationToken cancellationToken,
        ref char[]? geometryAttributeBuffer)
    {
        while (reader.MoveToNextAttribute())
        {
            cancellationToken.ThrowIfCancellationRequested();
            string attributeName = reader.LocalName;
            string? attributeNamespace = reader.NamespaceURI;
            if (string.IsNullOrEmpty(attributeNamespace) &&
                SvgMetadataCollector.IsGeometryAttribute(attributeName))
            {
                string? attributeValue = ReadBoundedAttributeValue(
                    reader,
                    cancellationToken,
                    ref geometryAttributeBuffer);
                if (attributeValue is not null)
                {
                    collector.ObserveAttribute(
                        0,
                        attributeName,
                        attributeNamespace,
                        attributeValue);
                }
            }
        }

        reader.MoveToElement();
    }

    private static string? ReadBoundedAttributeValue(
        XmlReader reader,
        CancellationToken cancellationToken,
        ref char[]? buffer)
    {
        if (!reader.CanReadValueChunk)
        {
            string value = reader.Value;
            return value.Length <= SvgMetadataCollector.MaxGeometryAttributeCharacters ? value : null;
        }

        buffer ??= new char[SvgMetadataCollector.MaxGeometryAttributeCharacters + 1];
        int length = 0;
        while (length < buffer.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = reader.ReadValueChunk(buffer, length, buffer.Length - length);
            if (read == 0)
                return new string(buffer, 0, length);
            length += read;
        }

        // Root geometry attributes are metadata hints. Ignore oversized values
        // after inspecting only the small bounded prefix, while XML parsing
        // continues normally for the rest of the document.
        return null;
    }
}
