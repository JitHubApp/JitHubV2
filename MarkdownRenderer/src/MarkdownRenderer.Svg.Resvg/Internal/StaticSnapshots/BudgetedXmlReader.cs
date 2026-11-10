using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;

namespace MarkdownRenderer.Svg.Resvg.Internal.StaticSnapshots;

/// <summary>
/// Transparent synchronous reader facade that checks cancellation and
/// structural budgets before exposing each parsed node to XmlDocument.
/// Parser settings and node/attribute navigation continue to come from the
/// underlying XmlReader.
/// </summary>
internal sealed class BudgetedXmlReader : XmlReader
{
    private readonly XmlReader _reader;
    private readonly CancellationToken _cancellationToken;
    private readonly XmlStructuralBudget _budget;

    internal BudgetedXmlReader(
        XmlReader reader,
        CancellationToken cancellationToken,
        XmlStructuralBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        _cancellationToken = cancellationToken;
        _budget = budget ?? new XmlStructuralBudget();
    }

    public override XmlReaderSettings? Settings => _reader.Settings;
    public override XmlNodeType NodeType => _reader.NodeType;
    public override string LocalName => _reader.LocalName;
    public override string NamespaceURI => _reader.NamespaceURI;
    public override string Prefix => _reader.Prefix;
    public override string Value => _reader.Value;
    public override int Depth => _reader.Depth;
    public override string BaseURI => _reader.BaseURI;
    public override bool IsEmptyElement => _reader.IsEmptyElement;
    public override int AttributeCount => _reader.AttributeCount;
    public override bool EOF => _reader.EOF;
    public override ReadState ReadState => _reader.ReadState;
    public override XmlNameTable NameTable => _reader.NameTable;
    public override string Name => _reader.Name;
    public override bool HasValue => _reader.HasValue;
    public override bool IsDefault => _reader.IsDefault;
    public override char QuoteChar => _reader.QuoteChar;
    public override XmlSpace XmlSpace => _reader.XmlSpace;
    public override string XmlLang => _reader.XmlLang;
    public override System.Xml.Schema.IXmlSchemaInfo? SchemaInfo => _reader.SchemaInfo;
    public override Type ValueType => _reader.ValueType;
    public override bool CanResolveEntity => _reader.CanResolveEntity;
    public override bool HasAttributes => _reader.HasAttributes;

    public override string this[int i] => _reader[i];
    public override string? this[string name] => _reader[name];
    public override string? this[string name, string? namespaceURI] => _reader[name, namespaceURI];

    public override string? GetAttribute(string name) => _reader.GetAttribute(name);
    public override string? GetAttribute(string name, string? namespaceURI) =>
        _reader.GetAttribute(name, namespaceURI);
    public override string GetAttribute(int i) => _reader.GetAttribute(i);

    public override bool MoveToAttribute(string name) => _reader.MoveToAttribute(name);
    public override bool MoveToAttribute(string name, string? namespaceURI) =>
        _reader.MoveToAttribute(name, namespaceURI);
    public override void MoveToAttribute(int i) => _reader.MoveToAttribute(i);
    public override bool MoveToFirstAttribute() => _reader.MoveToFirstAttribute();
    public override bool MoveToNextAttribute() => _reader.MoveToNextAttribute();
    public override bool MoveToElement() => _reader.MoveToElement();

    public override bool ReadAttributeValue()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        bool result = _reader.ReadAttributeValue();
        _cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public override bool Read()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        bool result = _reader.Read();
        _cancellationToken.ThrowIfCancellationRequested();
        if (result)
            _budget.Observe(_reader);
        return result;
    }

    public override string? LookupNamespace(string prefix) => _reader.LookupNamespace(prefix);

    public override void ResolveEntity()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        _reader.ResolveEntity();
        _cancellationToken.ThrowIfCancellationRequested();
    }

    public override void Close() => _reader.Close();
}
