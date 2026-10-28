using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
                    if (++elementCount > MaxElements)
                        return default;

                    int elementDepth = reader.Depth;
                    string elementName = reader.LocalName;
                    if (!sawRoot)
                    {
                        if (elementDepth != 0 ||
                            !elementName.Equals("svg", StringComparison.OrdinalIgnoreCase) ||
                            !SvgMetadataCollector.IsAcceptedRootNamespace(reader.NamespaceURI))
                        {
                            return default;
                        }
                        sawRoot = true;
                    }

                    if (!collector.ObserveElement(
                            elementDepth,
                            elementName,
                            reader.NamespaceURI,
                            reader.IsEmptyElement))
                    {
                        return default;
                    }

                    // XmlReader already parses and validates all attributes as
                    // part of Read(). Count them without visiting every image
                    // payload attribute again just to extract root metadata.
                    int currentAttributeCount = reader.AttributeCount;
                    if (currentAttributeCount > MaxAttributes - attributeCount)
                        return default;
                    attributeCount += currentAttributeCount;

                    if (elementDepth == 0 && currentAttributeCount != 0)
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
                                        elementDepth,
                                        attributeName,
                                        attributeNamespace,
                                        attributeValue);
                                }
                            }
                        }

                        reader.MoveToElement();
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

/// <summary>
/// Receives already-decoded XML reader values without moving the reader. It
/// stores only bounded root geometry and the first direct-child title/desc.
/// </summary>
internal sealed class SvgMetadataCollector
{
    private const int MaxMetadataCharacters = 4_096;
    internal const int MaxGeometryAttributeCharacters = 256;
    private const string SvgNamespace = "http://www.w3.org/2000/svg";

    private MetadataTextBuilder _title;
    private MetadataTextBuilder _description;
    private readonly CancellationToken _cancellationToken;
    private string? _rootNamespace;
    private string? _width;
    private string? _height;
    private string? _viewBox;
    private int _activeTextDepth = -1;
    private MetadataField _activeField;
    private bool _rootSeen;
    private bool _titleSeen;
    private bool _descriptionSeen;

    internal SvgMetadataCollector(CancellationToken cancellationToken = default)
    {
        _cancellationToken = cancellationToken;
    }

    internal static bool IsAcceptedRootNamespace(string? namespaceUri) =>
        string.IsNullOrEmpty(namespaceUri) ||
        namespaceUri.Equals(SvgNamespace, StringComparison.Ordinal);

    internal static bool IsGeometryAttribute(string localName) =>
        localName.Equals("width", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("height", StringComparison.OrdinalIgnoreCase) ||
        localName.Equals("viewBox", StringComparison.OrdinalIgnoreCase);

    /// <returns>False when the first element is not an SVG root.</returns>
    internal bool ObserveElement(int depth, string localName, string? namespaceUri, bool isEmptyElement)
    {
        if (depth == 0)
        {
            if (_rootSeen)
                return true;

            if (!localName.Equals("svg", StringComparison.OrdinalIgnoreCase) ||
                !IsAcceptedRootNamespace(namespaceUri))
            {
                return false;
            }

            _rootSeen = true;
            _rootNamespace = namespaceUri ?? string.Empty;
            return true;
        }

        if (depth != 1 || !_rootSeen ||
            !string.Equals(namespaceUri, _rootNamespace, StringComparison.Ordinal))
        {
            return true;
        }

        if (localName.Equals("title", StringComparison.OrdinalIgnoreCase) && !_titleSeen)
        {
            _titleSeen = true;
            if (!isEmptyElement)
            {
                _activeField = MetadataField.Title;
                _activeTextDepth = depth;
            }
        }
        else if (localName.Equals("desc", StringComparison.OrdinalIgnoreCase) && !_descriptionSeen)
        {
            _descriptionSeen = true;
            if (!isEmptyElement)
            {
                _activeField = MetadataField.Description;
                _activeTextDepth = depth;
            }
        }

        return true;
    }

    internal void ObserveAttribute(int ownerDepth, string localName, string? namespaceUri, string value)
    {
        if (ownerDepth != 0 || !_rootSeen || !string.IsNullOrEmpty(namespaceUri) ||
            value.Length > MaxGeometryAttributeCharacters)
        {
            return;
        }

        if (localName.Equals("width", StringComparison.OrdinalIgnoreCase))
            _width = value;
        else if (localName.Equals("height", StringComparison.OrdinalIgnoreCase))
            _height = value;
        else if (localName.Equals("viewBox", StringComparison.OrdinalIgnoreCase))
            _viewBox = value;
    }

    internal void AppendText(int depth, ReadOnlySpan<char> value)
    {
        if (_activeTextDepth < 0 || depth <= _activeTextDepth)
            return;

        if (_activeField == MetadataField.Title)
            _title.Append(value, _cancellationToken);
        else if (_activeField == MetadataField.Description)
            _description.Append(value, _cancellationToken);
    }

    internal void EndElement(int depth)
    {
        if (depth == _activeTextDepth)
        {
            _activeField = MetadataField.None;
            _activeTextDepth = -1;
        }
    }

    internal SvgSourceMetadata GetMetadata()
    {
        if (!_rootSeen)
            return default;

        return new SvgSourceMetadata(
            _title.GetValue(),
            _description.GetValue(),
            SvgIntrinsics.ParseRootGeometry(_width, _height, _viewBox));
    }

    private enum MetadataField : byte
    {
        None,
        Title,
        Description,
    }

    private struct MetadataTextBuilder
    {
        private StringBuilder? _builder;
        private bool _pendingSpace;
        private bool _exhausted;

        public void Append(ReadOnlySpan<char> value, CancellationToken cancellationToken)
        {
            if (_exhausted)
                return;

            int index = 0;
            while (index < value.Length)
            {
                if ((index & 0xff) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                char current = value[index];
                if (char.IsWhiteSpace(current))
                {
                    if (_builder is { Length: > 0 })
                        _pendingSpace = true;
                    index++;
                    continue;
                }

                int scalarLength = 1;
                if (char.IsHighSurrogate(current))
                {
                    if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                    {
                        _exhausted = true;
                        return;
                    }
                    scalarLength = 2;
                }
                else if (char.IsLowSurrogate(current))
                {
                    _exhausted = true;
                    return;
                }

                int currentLength = _builder?.Length ?? 0;
                int separatorLength = _pendingSpace && currentLength > 0 ? 1 : 0;
                if (currentLength + separatorLength + scalarLength > MaxMetadataCharacters)
                {
                    _exhausted = true;
                    return;
                }

                _builder ??= new StringBuilder(Math.Min(64, MaxMetadataCharacters));
                if (separatorLength != 0)
                    _builder.Append(' ');
                _builder.Append(value.Slice(index, scalarLength));
                _pendingSpace = false;
                index += scalarLength;
            }
        }

        public string? GetValue()
        {
            if (_builder is null || _builder.Length == 0)
                return null;
            return _builder.ToString();
        }
    }
}

/// <summary>
/// A borrowed byte-array stream that checks cancellation at each read chunk.
/// The stream is not published; disposing it never owns or modifies the source.
/// </summary>
internal sealed class CancellationAwareMemoryStream : MemoryStream
{
    private readonly CancellationToken _cancellationToken;

    public CancellationAwareMemoryStream(byte[] buffer, CancellationToken cancellationToken)
        : base(buffer, writable: false)
    {
        _cancellationToken = cancellationToken;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return base.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return base.Read(buffer);
    }

    public override int ReadByte()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        return base.ReadByte();
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cancellationToken.ThrowIfCancellationRequested();
        return base.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _cancellationToken.ThrowIfCancellationRequested();
        return base.ReadAsync(buffer, cancellationToken);
    }
}
