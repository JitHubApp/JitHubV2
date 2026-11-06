using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml;

namespace MarkdownRenderer.Svg.Resvg.Internal;

/// <summary>
/// Keeps the static-snapshot DOM load within the same structural ceilings as
/// the streaming security preflight. Counts are applied as the parser yields
/// each node, before XmlDocument can materialize that node and its attributes.
/// This bounds DOM amplification, not the parser's own input buffers or total
/// process memory.
/// </summary>
internal sealed class XmlStructuralBudget
{
    private int _elementsSeen;
    private int _attributesSeen;

    internal XmlStructuralBudget(int elementsSeen = 0, int attributesSeen = 0)
    {
        if ((uint)elementsSeen > SvgResourceBudget.MaxElements)
            throw new ArgumentOutOfRangeException(nameof(elementsSeen));
        if ((uint)attributesSeen > SvgResourceBudget.MaxAttributes)
            throw new ArgumentOutOfRangeException(nameof(attributesSeen));

        _elementsSeen = elementsSeen;
        _attributesSeen = attributesSeen;
    }

    internal void Observe(XmlReader reader)
    {
        if (reader.Depth > SvgResourceBudget.MaxDepth)
            throw new SvgStaticSnapshotResourceException(
                SvgStaticSnapshotResourceException.ElementDepthReason);

        if (reader.NodeType != XmlNodeType.Element)
            return;

        if (_elementsSeen >= SvgResourceBudget.MaxElements)
            throw new SvgStaticSnapshotResourceException(
                SvgStaticSnapshotResourceException.ElementCountReason);
        _elementsSeen++;

        int currentElementAttributes = reader.AttributeCount;
        if (currentElementAttributes > SvgResourceBudget.MaxAttributes - _attributesSeen)
            throw new SvgStaticSnapshotResourceException(
                SvgStaticSnapshotResourceException.AttributeCountReason);
        _attributesSeen += currentElementAttributes;
    }
}
