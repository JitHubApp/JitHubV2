using System;
using System.Text;
using System.Threading;

namespace MarkdownRenderer.Layout.Boxes;

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
