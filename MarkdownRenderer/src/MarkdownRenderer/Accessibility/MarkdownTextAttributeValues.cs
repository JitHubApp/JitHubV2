using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using MarkdownRenderer.Theming;
using Windows.UI;
using Windows.UI.Text;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// UIA text attribute values for a markdown text range: the fixed document
/// attributes, per-run style attributes and the comparison FindAttribute uses.
/// </summary>
internal static class MarkdownTextAttributeValues
{
    internal sealed class UnsupportedAttributeValue
    {
        public static readonly UnsupportedAttributeValue Instance = new();
        private UnsupportedAttributeValue() { }
    }

    internal static object? GetFixedAttributeValue(MarkdownAutomationPeer peer, AutomationTextAttributesEnum attribute)
    {
        return attribute switch
        {
            AutomationTextAttributesEnum.CultureAttribute => peer.OwnerControl.AutomationHost.CultureLcid,
            AutomationTextAttributesEnum.IsActiveAttribute => peer.IsTextCaretActive,
            AutomationTextAttributesEnum.IsHiddenAttribute => false,
            AutomationTextAttributesEnum.IsReadOnlyAttribute => true,
            AutomationTextAttributesEnum.CaretBidiModeAttribute => peer.OwnerControl.FlowDirection == FlowDirection.RightToLeft
                ? AutomationCaretBidiMode.RTL
                : AutomationCaretBidiMode.LTR,
            AutomationTextAttributesEnum.TextFlowDirectionsAttribute => peer.OwnerControl.FlowDirection == FlowDirection.RightToLeft
                ? AutomationFlowDirections.RightToLeft
                : AutomationFlowDirections.Default,
            _ => UnsupportedAttributeValue.Instance,
        };
    }

    internal static object? GetStyleAttributeValue(
        MarkdownAutomationPeer peer,
        AutomationTextAttributesEnum attribute,
        MarkdownTextStyleRun run)
    {
        var style = run.Style;
        return attribute switch
        {
            AutomationTextAttributesEnum.BackgroundColorAttribute => ToColorRef(
                style.Background ?? peer.OwnerControl.AutomationHost.Theme?.SurfaceColor ?? Microsoft.UI.Colors.Transparent),
            AutomationTextAttributesEnum.FontNameAttribute => style.FontFamily,
            AutomationTextAttributesEnum.FontSizeAttribute => (double)style.FontSize,
            AutomationTextAttributesEnum.FontWeightAttribute => (int)style.FontWeight.Weight,
            AutomationTextAttributesEnum.ForegroundColorAttribute => ToColorRef(style.Foreground),
            AutomationTextAttributesEnum.IsItalicAttribute => style.FontStyle == FontStyle.Italic,
            AutomationTextAttributesEnum.IsSubscriptAttribute => run.IsSubscript,
            AutomationTextAttributesEnum.IsSuperscriptAttribute => run.IsSuperscript,
            AutomationTextAttributesEnum.OverlineColorAttribute => ToColorRef(style.Foreground),
            AutomationTextAttributesEnum.OverlineStyleAttribute => AutomationTextDecorationLineStyle.None,
            AutomationTextAttributesEnum.StrikethroughColorAttribute => ToColorRef(style.Foreground),
            AutomationTextAttributesEnum.StrikethroughStyleAttribute => style.Strikethrough
                ? AutomationTextDecorationLineStyle.Single
                : AutomationTextDecorationLineStyle.None,
            AutomationTextAttributesEnum.StyleIdAttribute => GetStyleId(run.ElementKey),
            AutomationTextAttributesEnum.StyleNameAttribute => GetStyleName(peer, run.ElementKey),
            AutomationTextAttributesEnum.UnderlineColorAttribute => ToColorRef(style.Foreground),
            AutomationTextAttributesEnum.UnderlineStyleAttribute => style.Underline
                ? AutomationTextDecorationLineStyle.Single
                : AutomationTextDecorationLineStyle.None,
            _ => null,
        };
    }

    private static AutomationStyleId GetStyleId(string elementKey) => elementKey switch
    {
        MarkdownElementKeys.Heading1 => AutomationStyleId.Heading1,
        MarkdownElementKeys.Heading2 => AutomationStyleId.Heading2,
        MarkdownElementKeys.Heading3 => AutomationStyleId.Heading3,
        MarkdownElementKeys.Heading4 => AutomationStyleId.Heading4,
        MarkdownElementKeys.Heading5 => AutomationStyleId.Heading5,
        MarkdownElementKeys.Heading6 => AutomationStyleId.Heading6,
        MarkdownElementKeys.Quote => AutomationStyleId.Quote,
        MarkdownElementKeys.Emphasis => AutomationStyleId.Emphasis,
        MarkdownElementKeys.ListMarker => AutomationStyleId.BulletedList,
        _ => AutomationStyleId.Normal,
    };

    private static string GetStyleName(MarkdownAutomationPeer peer, string elementKey)
    {
        string fallback = MarkdownLocalizedStrings.StyleName(elementKey);
        string? key = MarkdownLocalizedStrings.StyleNameKey(elementKey);
        return key is null
            ? fallback
            : peer.OwnerControl.Localization.Resolve(key, fallback);
    }

    private static int ToColorRef(Color color) =>
        color.R | (color.G << 8) | (color.B << 16);

    internal static bool AttributeValuesEqual(object? left, object? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;

        if (left is string ls && right is string rs)
            return string.Equals(ls, rs, StringComparison.Ordinal);

        if (TryConvertToDouble(left, out var dl) &&
            TryConvertToDouble(right, out var dr))
            return Math.Abs(dl - dr) < 0.001;

        return left.Equals(right);
    }

    private static bool TryConvertToDouble(object value, out double result)
    {
        var type = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();
        if (type.IsEnum)
        {
            result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return true;
        }

        switch (Type.GetTypeCode(type))
        {
            case TypeCode.Byte:
            case TypeCode.SByte:
            case TypeCode.Int16:
            case TypeCode.UInt16:
            case TypeCode.Int32:
            case TypeCode.UInt32:
            case TypeCode.Int64:
            case TypeCode.UInt64:
            case TypeCode.Single:
            case TypeCode.Double:
            case TypeCode.Decimal:
                result = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
