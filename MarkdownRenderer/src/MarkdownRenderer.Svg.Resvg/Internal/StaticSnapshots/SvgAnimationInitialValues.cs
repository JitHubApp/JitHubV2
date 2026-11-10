using System.Globalization;
using System.Text;
using System.Xml;
using MarkdownRenderer.Svg.Resvg.Internal.Preparation;

namespace MarkdownRenderer.Svg.Resvg.Internal.StaticSnapshots;

/// <summary>
/// Freezes SMIL animation elements at their initial value so the static
/// snapshot shows the first frame.
/// </summary>
internal static class SvgAnimationInitialValues
{
    internal static bool IsAnimationElement(XmlElement element)
    {
        if (!string.IsNullOrEmpty(element.NamespaceURI) &&
            !element.NamespaceURI.Equals(SvgContentRules.SvgNamespace, StringComparison.Ordinal))
        {
            return false;
        }

        return element.LocalName.Equals("animate", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("animateColor", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("animateMotion", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("animateTransform", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("set", StringComparison.OrdinalIgnoreCase) ||
            element.LocalName.Equals("discard", StringComparison.OrdinalIgnoreCase);
    }

    internal static void ApplyInitialValue(XmlElement animation, CancellationToken cancellationToken)
    {
        if (animation.ParentNode is not XmlElement target || !BeginsImmediately(animation))
            return;

        string localName = animation.LocalName;
        if (localName.Equals("animateMotion", StringComparison.OrdinalIgnoreCase) ||
            localName.Equals("discard", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string? initialValue = GetInitialValue(animation, cancellationToken);
        if (string.IsNullOrWhiteSpace(initialValue))
            return;

        if (localName.Equals("animateTransform", StringComparison.OrdinalIgnoreCase))
        {
            string type = animation.GetAttribute("type").Trim();
            if (!IsSupportedTransformType(type))
                return;

            string transform = $"{type}({initialValue.Trim()})";
            if (animation.GetAttribute("additive").Equals("sum", StringComparison.OrdinalIgnoreCase) &&
                target.HasAttribute("transform"))
            {
                transform = $"{target.GetAttribute("transform").Trim()} {transform}";
            }

            target.SetAttribute("transform", transform);
            return;
        }

        string attributeName = animation.GetAttribute("attributeName").Trim();
        if (attributeName.Length == 0 ||
            attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase) ||
            attributeName.IndexOf(':') >= 0)
        {
            return;
        }

        target.SetAttribute(attributeName, initialValue.Trim());
    }

    internal static string? GetInitialValue(XmlElement animation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string values = animation.GetAttribute("values");
        if (!string.IsNullOrWhiteSpace(values))
        {
            // Enumerate ranges rather than materializing every unused value.
            // The legacy Split contract includes leading/trailing empty tokens.
            var candidates = values.AsSpan().Split(';');
            candidates.MoveNext();
            Range selected = candidates.Current;

            string keyTimes = animation.GetAttribute("keyTimes");
            if (!string.IsNullOrWhiteSpace(keyTimes))
            {
                var times = keyTimes.AsSpan().Split(';');
                int index = 0;
                do
                {
                    if ((index++ & 0xFF) == 0)
                        cancellationToken.ThrowIfCancellationRequested();
                    if (!times.MoveNext())
                        break;
                    if (!double.TryParse(
                            keyTimes.AsSpan()[times.Current].Trim(),
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double keyTime) ||
                        keyTime > 0)
                    {
                        break;
                    }

                    // Multiple values at keyTime zero represent an immediate
                    // transition. The final zero-time value is the visible
                    // first frame (common in rotating README badge artwork).
                    selected = candidates.Current;
                }
                while (candidates.MoveNext());
            }

            cancellationToken.ThrowIfCancellationRequested();
            return values[selected];
        }

        string from = animation.GetAttribute("from");
        if (!string.IsNullOrWhiteSpace(from))
            return from;

        return animation.LocalName.Equals("set", StringComparison.OrdinalIgnoreCase)
            ? animation.GetAttribute("to")
            : null;
    }

    internal static bool BeginsImmediately(XmlElement animation)
    {
        string begin = animation.GetAttribute("begin").Trim();
        if (begin.Length == 0)
            return true;

        int separator = begin.IndexOf(';');
        if (separator >= 0)
            begin = begin[..separator].Trim();
        if (begin.Equals("indefinite", StringComparison.OrdinalIgnoreCase) ||
            begin.IndexOfAny(['.', '+']) >= 0)
        {
            return false;
        }

        if (begin.EndsWith("ms", StringComparison.OrdinalIgnoreCase))
            begin = begin[..^2];
        else if (begin.EndsWith("s", StringComparison.OrdinalIgnoreCase))
            begin = begin[..^1];

        return double.TryParse(
                begin,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double start) &&
            start <= 0;
    }

    internal static bool IsSupportedTransformType(string value) =>
        value.Equals("translate", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("scale", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("rotate", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("skewX", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("skewY", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("matrix", StringComparison.OrdinalIgnoreCase);
}
