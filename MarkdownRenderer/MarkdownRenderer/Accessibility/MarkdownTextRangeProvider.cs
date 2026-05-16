using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;
using Windows.UI;
using Windows.UI.Text;

namespace MarkdownRenderer.Accessibility;

internal sealed partial class MarkdownTextRangeProvider : ITextRangeProvider
{
    private readonly MarkdownAutomationPeer _peer;
    private int _start;
    private int _end;

    public MarkdownTextRangeProvider(MarkdownAutomationPeer peer, int start, int end)
    {
        _peer = peer;
        var doc = _peer.GetSemanticDocument();
        _start = Math.Clamp(start, 0, doc.Text.Length);
        _end = Math.Clamp(end, _start, doc.Text.Length);
    }

    public void AddToSelection() => Select();

    public ITextRangeProvider Clone() => new MarkdownTextRangeProvider(_peer, _start, _end);

    public bool Compare(ITextRangeProvider range)
    {
        return range is MarkdownTextRangeProvider other &&
               ReferenceEquals(other._peer, _peer) &&
               other._start == _start &&
               other._end == _end;
    }

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not MarkdownTextRangeProvider other) return 0;
        int a = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int b = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other._end;
        return a.CompareTo(b);
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        var doc = _peer.GetSemanticDocument();
        switch (unit)
        {
            case TextUnit.Character:
                if (_start == _end && _start < doc.Text.Length) _end = _start + 1;
                break;
            case TextUnit.Word:
            case TextUnit.Format:
            {
                int pivot = Math.Clamp(_start, 0, Math.Max(0, doc.Text.Length - 1));
                var (s, e) = FindWordBoundaries(doc.Text, pivot);
                _start = s;
                _end = e;
                break;
            }
            case TextUnit.Line:
            case TextUnit.Paragraph:
            case TextUnit.Page:
            {
                var (s, e) = FindLineBoundaries(doc.Text, _start);
                _start = s;
                _end = e;
                break;
            }
            case TextUnit.Document:
                _start = 0;
                _end = doc.Text.Length;
                break;
        }
    }

    public ITextRangeProvider? FindAttribute(int attributeId, object value, bool backward)
    {
        var attribute = (AutomationTextAttributesEnum)attributeId;
        var fixedValue = GetFixedAttributeValue(attribute);
        if (fixedValue is not UnsupportedAttributeValue)
        {
            return AttributeValuesEqual(fixedValue, value) ? Clone() : null;
        }

        var runs = EnumerateTextStyleRuns().ToList();
        if (backward) runs.Reverse();
        foreach (var run in runs)
        {
            var candidate = GetStyleAttributeValue(attribute, run);
            if (AttributeValuesEqual(candidate, value))
                return new MarkdownTextRangeProvider(_peer, run.Start, run.End);
        }

        return null;
    }

    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var doc = _peer.GetSemanticDocument();
        var comparison = ignoreCase ? StringComparison.CurrentCultureIgnoreCase : StringComparison.CurrentCulture;
        int rangeStart = Math.Clamp(_start, 0, doc.Text.Length);
        int rangeEnd = Math.Clamp(_end, rangeStart, doc.Text.Length);
        if (rangeEnd - rangeStart < text.Length) return null;

        string segment = doc.Text.Substring(rangeStart, rangeEnd - rangeStart);
        int relative = backward
            ? segment.LastIndexOf(text, comparison)
            : segment.IndexOf(text, comparison);
        int index = relative >= 0 ? rangeStart + relative : -1;

        return index >= 0
            ? new MarkdownTextRangeProvider(_peer, index, index + text.Length)
            : null;
    }

    public object GetAttributeValue(int attributeId)
    {
        var attribute = (AutomationTextAttributesEnum)attributeId;
        var fixedValue = GetFixedAttributeValue(attribute);
        if (fixedValue is not UnsupportedAttributeValue)
            return fixedValue!;

        bool haveValue = false;
        object? first = null;
        foreach (var run in EnumerateTextStyleRuns())
        {
            var value = GetStyleAttributeValue(attribute, run);
            if (value is null)
                return UiaReservedAttributeValues.NotSupported;

            if (!haveValue)
            {
                first = value;
                haveValue = true;
                continue;
            }

            if (!AttributeValuesEqual(first, value))
                return UiaReservedAttributeValues.Mixed;
        }

        return haveValue ? first! : UiaReservedAttributeValues.NotSupported;
    }

    public void GetBoundingRectangles(out double[] boundingRectangles)
    {
        var doc = _peer.GetSemanticDocument();
        var values = new List<double>();
        foreach (var rect in doc.GetDocumentRects(_start, _end))
        {
            var screen = _peer.GetScreenRectForDocumentRect(rect);
            if (screen.Width <= 0 || screen.Height <= 0) continue;
            values.Add(screen.X);
            values.Add(screen.Y);
            values.Add(screen.Width);
            values.Add(screen.Height);
        }

        boundingRectangles = values.ToArray();
    }

    public IRawElementProviderSimple[] GetChildren()
    {
        var providers = new List<IRawElementProviderSimple>();
        var doc = _peer.GetSemanticDocument();
        foreach (var node in doc.GetNodesIntersectingTextRange(_start, _end))
        {
            if (_peer.TryGetProviderForSemanticNode(node, out var provider))
                providers.Add(provider);
        }

        return providers.ToArray();
    }

    public IRawElementProviderSimple GetEnclosingElement() => _peer.ProviderFromPeerForTextRange(_peer);

    public string GetText(int maxLength)
    {
        var doc = _peer.GetSemanticDocument();
        int start = Math.Clamp(_start, 0, doc.Text.Length);
        int end = Math.Clamp(_end, start, doc.Text.Length);
        int length = end - start;
        if (maxLength >= 0) length = Math.Min(length, maxLength);
        return length <= 0 ? string.Empty : doc.Text.Substring(start, length);
    }

    public int Move(TextUnit unit, int count)
    {
        if (count == 0) return 0;
        var doc = _peer.GetSemanticDocument();
        string text = doc.Text;
        if (text.Length == 0)
        {
            _start = 0;
            _end = 0;
            return 0;
        }

        int normalizedStart = UnitStart(text, _start, unit);
        int movedStart = MoveOffset(text, normalizedStart, unit, count, out int moved);
        _start = UnitStart(text, movedStart, unit);
        _end = UnitEnd(text, _start, unit);
        return moved;
    }

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        if (targetRange is not MarkdownTextRangeProvider other) return;
        int target = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other._end;
        SetEndpoint(endpoint, target);
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        int current = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int moved = MoveOffset(_peer.GetSemanticDocument().Text, current, unit, count, out int actual);
        SetEndpoint(endpoint, moved);
        return actual;
    }

    public void RemoveFromSelection()
    {
        _peer.OwnerControl.ClearAutomationSelection();
    }

    public void ScrollIntoView(bool alignToTop)
    {
        var doc = _peer.GetSemanticDocument();
        foreach (var rect in doc.GetDocumentRects(_start, _end))
        {
            _peer.OwnerControl.ScrollDocumentRectIntoView(rect, alignToTop);
            return;
        }
    }

    public void Select()
    {
        var doc = _peer.GetSemanticDocument();
        if (doc.TryGetDocumentRange(_start, _end, out var range))
            _peer.OwnerControl.SelectAutomationRange(range);
    }

    internal int Start => _start;
    internal int End => _end;

    private sealed class UnsupportedAttributeValue
    {
        public static readonly UnsupportedAttributeValue Instance = new();
        private UnsupportedAttributeValue() { }
    }

    private static class UiaReservedAttributeValues
    {
        public static object Mixed => GetReservedValue(UiaGetReservedMixedAttributeValue, FallbackMixedAttributeValue.Instance);
        public static object NotSupported => GetReservedValue(UiaGetReservedNotSupportedValue, FallbackNotSupportedValue.Instance);

        private delegate int ReservedValueFactory(out IntPtr value);

        [DllImport("UIAutomationCore.dll", PreserveSig = true)]
        private static extern int UiaGetReservedMixedAttributeValue(out IntPtr value);

        [DllImport("UIAutomationCore.dll", PreserveSig = true)]
        private static extern int UiaGetReservedNotSupportedValue(out IntPtr value);

        private static object GetReservedValue(ReservedValueFactory factory, object fallback)
        {
            IntPtr value = IntPtr.Zero;
            try
            {
                int hr = factory(out value);
                if (hr >= 0 && value != IntPtr.Zero)
                    return Marshal.GetObjectForIUnknown(value);
            }
            catch
            {
            }
            finally
            {
                if (value != IntPtr.Zero)
                    Marshal.Release(value);
            }

            return fallback;
        }

        private sealed class FallbackMixedAttributeValue
        {
            public static readonly FallbackMixedAttributeValue Instance = new();
            private FallbackMixedAttributeValue() { }
        }

        private sealed class FallbackNotSupportedValue
        {
            public static readonly FallbackNotSupportedValue Instance = new();
            private FallbackNotSupportedValue() { }
        }
    }

    private readonly record struct TextStyleRun(
        int Start,
        int End,
        string ElementKey,
        InlineRun? Run,
        ElementStyle Style);

    private object? GetFixedAttributeValue(AutomationTextAttributesEnum attribute)
    {
        return attribute switch
        {
            AutomationTextAttributesEnum.CultureAttribute => CultureInfo.CurrentUICulture.LCID,
            AutomationTextAttributesEnum.IsActiveAttribute => _peer.OwnerControl.FocusState != FocusState.Unfocused,
            AutomationTextAttributesEnum.IsHiddenAttribute => false,
            AutomationTextAttributesEnum.IsReadOnlyAttribute => true,
            AutomationTextAttributesEnum.CaretBidiModeAttribute => _peer.OwnerControl.FlowDirection == FlowDirection.RightToLeft
                ? AutomationCaretBidiMode.RTL
                : AutomationCaretBidiMode.LTR,
            AutomationTextAttributesEnum.TextFlowDirectionsAttribute => _peer.OwnerControl.FlowDirection == FlowDirection.RightToLeft
                ? AutomationFlowDirections.RightToLeft
                : AutomationFlowDirections.Default,
            _ => UnsupportedAttributeValue.Instance,
        };
    }

    private object? GetStyleAttributeValue(AutomationTextAttributesEnum attribute, TextStyleRun run)
    {
        var style = run.Style;
        return attribute switch
        {
            AutomationTextAttributesEnum.BackgroundColorAttribute => ToColorRef(
                style.Background ?? _peer.OwnerControl.CurrentThemeSnapshot?.SurfaceColor ?? Microsoft.UI.Colors.Transparent),
            AutomationTextAttributesEnum.FontNameAttribute => style.FontFamily,
            AutomationTextAttributesEnum.FontSizeAttribute => (double)style.FontSize,
            AutomationTextAttributesEnum.FontWeightAttribute => (int)style.FontWeight.Weight,
            AutomationTextAttributesEnum.ForegroundColorAttribute => ToColorRef(style.Foreground),
            AutomationTextAttributesEnum.IsItalicAttribute => style.FontStyle == FontStyle.Italic,
            AutomationTextAttributesEnum.IsSubscriptAttribute => false,
            AutomationTextAttributesEnum.IsSuperscriptAttribute => run.Run is LinkRun { IsSuperscript: true },
            AutomationTextAttributesEnum.OverlineColorAttribute => ToColorRef(style.Foreground),
            AutomationTextAttributesEnum.OverlineStyleAttribute => AutomationTextDecorationLineStyle.None,
            AutomationTextAttributesEnum.StrikethroughColorAttribute => ToColorRef(style.Foreground),
            AutomationTextAttributesEnum.StrikethroughStyleAttribute => style.Strikethrough
                ? AutomationTextDecorationLineStyle.Single
                : AutomationTextDecorationLineStyle.None,
            AutomationTextAttributesEnum.StyleIdAttribute => GetStyleId(run.ElementKey),
            AutomationTextAttributesEnum.StyleNameAttribute => GetStyleName(run.ElementKey),
            AutomationTextAttributesEnum.UnderlineColorAttribute => ToColorRef(style.Foreground),
            AutomationTextAttributesEnum.UnderlineStyleAttribute => style.Underline
                ? AutomationTextDecorationLineStyle.Single
                : AutomationTextDecorationLineStyle.None,
            _ => null,
        };
    }

    private IEnumerable<TextStyleRun> EnumerateTextStyleRuns()
    {
        var doc = _peer.GetSemanticDocument();
        int rangeStart = Math.Clamp(_start, 0, doc.Text.Length);
        int rangeEnd = Math.Clamp(_end, rangeStart, doc.Text.Length);
        bool collapsed = rangeStart == rangeEnd;
        bool yielded = false;

        foreach (var span in doc.TextSpans)
        {
            if (span.TextEnd < rangeStart || span.TextStart > rangeEnd)
                continue;

            if (span.InlineBox is { } inline)
            {
                foreach (var run in EnumerateInlineStyleRuns(inline, span.TextStart, rangeStart, rangeEnd, collapsed))
                {
                    yielded = true;
                    yield return run;
                }
            }
            else if (span.ImageBox is not null || span.EmbedBox is not null)
            {
                if (!SpanIntersects(span.TextStart, span.TextEnd, rangeStart, rangeEnd, collapsed))
                    continue;

                yielded = true;
                yield return new TextStyleRun(
                    collapsed ? rangeStart : Math.Max(rangeStart, span.TextStart),
                    collapsed ? rangeStart : Math.Min(rangeEnd, span.TextEnd),
                    MarkdownElementKeys.Body,
                    null,
                    GetStyle(MarkdownElementKeys.Body));
            }
        }

        if (!yielded)
        {
            yield return new TextStyleRun(
                rangeStart,
                rangeEnd,
                MarkdownElementKeys.Body,
                null,
                GetStyle(MarkdownElementKeys.Body));
        }
    }

    private IEnumerable<TextStyleRun> EnumerateInlineStyleRuns(
        InlineContainerBox inline,
        int textSpanStart,
        int rangeStart,
        int rangeEnd,
        bool collapsed)
    {
        int cumulative = 0;
        foreach (var run in inline.Runs)
        {
            int length = run.Text.Length;
            if (length <= 0)
                continue;

            int runStart = textSpanStart + cumulative;
            int runEnd = runStart + length;
            cumulative += length;

            if (!SpanIntersects(runStart, runEnd, rangeStart, rangeEnd, collapsed))
                continue;

            var elementKey = string.IsNullOrEmpty(run.ElementKey) ? inline.ElementKey : run.ElementKey;
            yield return new TextStyleRun(
                collapsed ? rangeStart : Math.Max(rangeStart, runStart),
                collapsed ? rangeStart : Math.Min(rangeEnd, runEnd),
                elementKey,
                run,
                GetStyle(elementKey));
        }
    }

    private ElementStyle GetStyle(string elementKey) =>
        _peer.OwnerControl.CurrentThemeSnapshot?.GetStyle(elementKey) ?? new ElementStyle();

    private static bool SpanIntersects(int spanStart, int spanEnd, int rangeStart, int rangeEnd, bool collapsed)
    {
        if (collapsed)
            return rangeStart >= spanStart && rangeStart <= spanEnd;

        return spanEnd > rangeStart && spanStart < rangeEnd;
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

    private static string GetStyleName(string elementKey) => MarkdownLocalizedStrings.StyleName(elementKey);

    private static int ToColorRef(Color color) =>
        color.R | (color.G << 8) | (color.B << 16);

    private static bool AttributeValuesEqual(object? left, object? right)
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

    private void SetEndpoint(TextPatternRangeEndpoint endpoint, int value)
    {
        var doc = _peer.GetSemanticDocument();
        value = Math.Clamp(value, 0, doc.Text.Length);
        if (endpoint == TextPatternRangeEndpoint.Start)
        {
            if (value > _end)
                _end = value;
            _start = value;
        }
        else
        {
            if (value < _start)
                _start = value;
            _end = value;
        }
    }

    private static int MoveOffset(string text, int offset, TextUnit unit, int count, out int moved)
    {
        moved = 0;
        int current = Math.Clamp(offset, 0, text.Length);
        int direction = Math.Sign(count);
        int steps = Math.Abs(count);
        for (int i = 0; i < steps; i++)
        {
            int next = unit switch
            {
                TextUnit.Character => current + direction,
                TextUnit.Word or TextUnit.Format => direction > 0 ? NextWordStart(text, current) : PreviousWordStart(text, current),
                TextUnit.Line or TextUnit.Paragraph or TextUnit.Page => direction > 0 ? NextLineStart(text, current) : PreviousLineStart(text, current),
                TextUnit.Document => direction > 0 ? text.Length : 0,
                _ => current,
            };
            next = Math.Clamp(next, 0, text.Length);
            if (next == current) break;
            current = next;
            moved += direction;
        }

        return current;
    }

    private static int UnitStart(string text, int offset, TextUnit unit)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        if (offset >= text.Length) return text.Length;

        return unit switch
        {
            TextUnit.Word or TextUnit.Format => FindWordBoundaries(text, offset).Start,
            TextUnit.Line or TextUnit.Paragraph or TextUnit.Page => FindLineBoundaries(text, offset).Start,
            TextUnit.Document => 0,
            _ => offset,
        };
    }

    private static int UnitEnd(string text, int start, TextUnit unit)
    {
        start = Math.Clamp(start, 0, text.Length);
        if (start >= text.Length) return text.Length;

        return unit switch
        {
            TextUnit.Character => Math.Min(text.Length, start + 1),
            TextUnit.Word or TextUnit.Format => FindWordBoundaries(text, start).End,
            TextUnit.Line or TextUnit.Paragraph or TextUnit.Page => FindLineBoundaries(text, start).End,
            TextUnit.Document => text.Length,
            _ => start,
        };
    }

    private static (int Start, int End) FindWordBoundaries(string text, int offset)
    {
        if (string.IsNullOrEmpty(text)) return (0, 0);
        return TextBoundaryHelper.FindWordBoundaries(text, offset);
    }

    private static (int Start, int End) FindLineBoundaries(string text, int offset)
    {
        if (text.Length == 0) return (0, 0);
        offset = Math.Clamp(offset, 0, text.Length);
        int start = offset;
        while (start > 0 && text[start - 1] != '\n') start--;
        int end = offset;
        while (end < text.Length && text[end] != '\n') end++;
        return (start, end);
    }

    private static int NextWordStart(string text, int offset)
    {
        return TextBoundaryHelper.FindNextWordStart(text, offset);
    }

    private static int PreviousWordStart(string text, int offset)
    {
        return TextBoundaryHelper.FindPreviousWordStart(text, offset);
    }

    private static int NextLineStart(string text, int offset)
    {
        int i = Math.Clamp(offset, 0, text.Length);
        while (i < text.Length && text[i] != '\n') i++;
        return Math.Min(text.Length, i + 1);
    }

    private static int PreviousLineStart(string text, int offset)
    {
        int i = Math.Clamp(offset - 1, 0, Math.Max(0, text.Length - 1));
        while (i > 0 && text[i - 1] != '\n') i--;
        return i;
    }
}
