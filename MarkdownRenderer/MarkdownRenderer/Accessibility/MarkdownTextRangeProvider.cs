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
    private readonly InlineContainerBox? _exactRangeScope;
    private int _start;
    private int _end;

    public MarkdownTextRangeProvider(
        MarkdownAutomationPeer peer,
        int start,
        int end,
        InlineContainerBox? exactRangeScope = null)
    {
        _peer = peer;
        _exactRangeScope = exactRangeScope;
        var doc = _peer.GetSemanticDocument();
        _start = Math.Clamp(start, 0, doc.Text.Length);
        _end = Math.Clamp(end, _start, doc.Text.Length);
    }

    public void AddToSelection() => Select();

    public ITextRangeProvider Clone() =>
        new MarkdownTextRangeProvider(_peer, _start, _end, _exactRangeScope);

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
        unit = NormalizeSupportedTextUnit(unit);
        var doc = _peer.GetSemanticDocument();
        switch (unit)
        {
            case TextUnit.Character:
            {
                var (s, e) = doc.TextElementBoundaries.FindBoundaries(_start);
                _start = s;
                _end = e;
                break;
            }
            case TextUnit.Word:
            {
                int pivot = Math.Clamp(_start, 0, Math.Max(0, doc.Text.Length - 1));
                var (s, e) = doc.TextElementBoundaries.FindWordBoundaries(pivot);
                _start = s;
                _end = e;
                break;
            }
            case TextUnit.Format:
            {
                MarkdownTextStyleRun run = GetFormatRunAt(_start);
                _start = run.Start;
                _end = run.End;
                break;
            }
            case TextUnit.Line:
            case TextUnit.Paragraph:
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

        MarkdownTextFormatCache cache = GetFormatRunCache();
        var doc = _peer.GetSemanticDocument();
        int rangeStart = Math.Clamp(_start, 0, doc.Text.Length);
        int rangeEnd = Math.Clamp(_end, rangeStart, doc.Text.Length);
        bool collapsed = rangeStart == rangeEnd;
        int index = backward ? cache.RunCount - 1 : 0;
        int limit = backward ? -1 : cache.RunCount;
        int step = backward ? -1 : 1;
        for (; index != limit; index += step)
        {
            MarkdownTextStyleRun run = cache.GetRun(index);
            if (!SpanIntersects(run.Start, run.End, rangeStart, rangeEnd, collapsed))
                continue;
            var candidate = GetStyleAttributeValue(attribute, run);
            if (AttributeValuesEqual(candidate, value))
            {
                return new MarkdownTextRangeProvider(
                    _peer,
                    collapsed ? rangeStart : Math.Max(rangeStart, run.Start),
                    collapsed ? rangeStart : Math.Min(rangeEnd, run.End));
            }
        }

        return null;
    }

    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var doc = _peer.GetSemanticDocument();
        int rangeStart = Math.Clamp(_start, 0, doc.Text.Length);
        int rangeEnd = Math.Clamp(_end, rangeStart, doc.Text.Length);
        if (rangeEnd - rangeStart < text.Length) return null;

        string segment = doc.Text.Substring(rangeStart, rangeEnd - rangeStart);
        CompareOptions options = ignoreCase ? CompareOptions.IgnoreCase : CompareOptions.None;
        CompareInfo compareInfo = _peer.OwnerControl.AutomationCulture.CompareInfo;
        int relative = backward
            ? compareInfo.LastIndexOf(segment, text, options)
            : compareInfo.IndexOf(segment, text, options);
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
        var visibleBounds = _peer.GetVisibleScreenBounds();
        boundingRectangles = BuildVisibleBoundingRectangles(
            _start,
            _end,
            doc.GetDocumentRects(_start, _end).Select(_peer.GetScreenRectForDocumentRect),
            visibleBounds);
    }

    internal static double[] BuildVisibleBoundingRectangles(
        int start,
        int end,
        IEnumerable<Windows.Foundation.Rect> screenRects,
        Windows.Foundation.Rect visibleBounds)
    {
        return AccessibilityGeometry.BuildVisibleBoundingRectangles(
            start,
            end,
            ConvertRects(),
            new AccessibilityRect(
                visibleBounds.X,
                visibleBounds.Y,
                visibleBounds.Width,
                visibleBounds.Height));

        IEnumerable<AccessibilityRect> ConvertRects()
        {
            foreach (var rect in screenRects)
                yield return new AccessibilityRect(rect.X, rect.Y, rect.Width, rect.Height);
        }
    }

    public IRawElementProviderSimple[] GetChildren()
    {
        var providers = new List<IRawElementProviderSimple>();
        var doc = _peer.GetSemanticDocument();
        MarkdownSemanticNode enclosing = doc.GetEnclosingNodeForTextRange(
            _start,
            _end,
            _exactRangeScope);
        foreach (MarkdownSemanticNode node in
                 doc.GetImmediateChildrenIntersectingTextRange(enclosing, _start, _end))
        {
            if (_peer.TryGetProviderForSemanticNode(node, out var provider))
                providers.Add(provider);
        }

        return providers.ToArray();
    }

    public IRawElementProviderSimple GetEnclosingElement()
    {
        MarkdownSemanticDocument document = _peer.GetSemanticDocument();
        MarkdownSemanticNode enclosing = document.GetEnclosingNodeForTextRange(
            _start,
            _end,
            _exactRangeScope);
        return !ReferenceEquals(enclosing, document.Root) &&
               _peer.TryGetPeerForSemanticNode(enclosing, out AutomationPeer peer)
            ? _peer.ProviderFromPeerForTextRange(peer)
            : _peer.ProviderFromPeerForTextRange(_peer);
    }

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
        unit = NormalizeSupportedTextUnit(unit);
        if (unit == TextUnit.Format)
            return MoveByFormat(count);
        var doc = _peer.GetSemanticDocument();
        string text = doc.Text;
        if (text.Length == 0)
        {
            _start = 0;
            _end = 0;
            return 0;
        }

        int normalizedStart = UnitStart(text, doc.TextElementBoundaries, _start, unit);
        int movedStart = MoveOffset(text, doc.TextElementBoundaries, normalizedStart, unit, count, out int moved);
        _start = UnitStart(text, doc.TextElementBoundaries, movedStart, unit);
        _end = UnitEnd(text, doc.TextElementBoundaries, _start, unit);
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
        unit = NormalizeSupportedTextUnit(unit);
        int current = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int actual;
        var doc = _peer.GetSemanticDocument();
        int moved = unit == TextUnit.Format
            ? MoveFormatOffset(current, count, out actual)
            : MoveOffset(doc.Text, doc.TextElementBoundaries, current, unit, count, out actual);
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
        foreach (var image in doc.GetImagesIntersectingTextRange(_start, _end))
            image.EnsureLoading();

        bool hasDocumentRange = doc.TryGetDocumentRange(_start, _end, out var documentRange);

        foreach (var rect in doc.GetDocumentRects(_start, _end, expandDegenerate: true))
        {
            _peer.OwnerControl.ScrollDocumentRectIntoView(rect, alignToTop);
            return;
        }

        // Lazy layout can expose semantic text before the target block has a
        // realized glyph rectangle. Fall back to its stable document position
        // so the owning control can realize and reveal that viewport band.
        if (hasDocumentRange)
            _peer.OwnerControl.ScrollToBlock(documentRange.Normalized().Start.BlockIndex);
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

    private object? GetFixedAttributeValue(AutomationTextAttributesEnum attribute)
    {
        return attribute switch
        {
            AutomationTextAttributesEnum.CultureAttribute => _peer.OwnerControl.AutomationCultureLcid,
            AutomationTextAttributesEnum.IsActiveAttribute => _peer.IsTextCaretActive,
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

    private object? GetStyleAttributeValue(AutomationTextAttributesEnum attribute, MarkdownTextStyleRun run)
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
            AutomationTextAttributesEnum.IsSubscriptAttribute => run.IsSubscript,
            AutomationTextAttributesEnum.IsSuperscriptAttribute => run.IsSuperscript,
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

    private IEnumerable<MarkdownTextStyleRun> EnumerateTextStyleRuns()
    {
        var doc = _peer.GetSemanticDocument();
        int rangeStart = Math.Clamp(_start, 0, doc.Text.Length);
        int rangeEnd = Math.Clamp(_end, rangeStart, doc.Text.Length);
        bool collapsed = rangeStart == rangeEnd;
        if (collapsed)
        {
            MarkdownTextStyleRun caretRun = GetFormatRunAt(rangeStart);
            yield return caretRun with { Start = rangeStart, End = rangeStart };
            yield break;
        }

        MarkdownTextFormatCache cache = GetFormatRunCache();
        for (int index = 0; index < cache.RunCount; index++)
        {
            MarkdownTextStyleRun run = cache.GetRun(index);
            if (!SpanIntersects(run.Start, run.End, rangeStart, rangeEnd, collapsed))
                continue;

            yield return run with
            {
                Start = collapsed ? rangeStart : Math.Max(rangeStart, run.Start),
                End = collapsed ? rangeStart : Math.Min(rangeEnd, run.End),
            };
        }
    }

    private MarkdownTextStyleRun GetFormatRunAt(int offset)
    {
        var doc = _peer.GetSemanticDocument();
        MarkdownTextFormatCache cache = GetFormatRunCache();
        if (cache.RunCount == 0)
        {
            return new MarkdownTextStyleRun(
                0,
                doc.Text.Length,
                MarkdownElementKeys.Body,
                IsSubscript: false,
                IsSuperscript: false,
                GetStyle(MarkdownElementKeys.Body));
        }

        offset = Math.Clamp(offset, 0, doc.Text.Length);
        return cache.GetRun(FindFormatRunIndex(cache, offset, doc.Text.Length));
    }

    private static int FindFormatRunIndex(
        MarkdownTextFormatCache cache,
        int offset,
        int documentLength)
    {
        int low = 0;
        int high = cache.RunCount - 1;
        while (low <= high)
        {
            int i = low + ((high - low) / 2);
            MarkdownTextStyleRun run = cache.GetRun(i);
            if (ContainsHalfOpenOffset(
                    offset,
                    run.Start,
                    run.End,
                    i == cache.RunCount - 1,
                    documentLength))
                return i;
            if (offset < run.Start)
                high = i - 1;
            else
                low = i + 1;
        }

        return Math.Clamp(low, 0, cache.RunCount - 1);
    }

    internal static bool ContainsHalfOpenOffset(
        int offset,
        int start,
        int end,
        bool isFinalRun,
        int documentLength) =>
        offset >= start &&
        (offset < end ||
         (isFinalRun && offset == documentLength && offset == end));

    private int MoveByFormat(int count)
    {
        MarkdownTextFormatCache cache = GetFormatRunCache();
        if (cache.RunCount == 0)
            return 0;

        int documentLength = _peer.GetSemanticDocument().Text.Length;
        int currentIndex = FindFormatRunIndex(
            cache,
            Math.Clamp(_start, 0, documentLength),
            documentLength);
        int targetIndex = (int)Math.Clamp(
            (long)currentIndex + count,
            0L,
            cache.RunCount - 1L);
        MarkdownTextStyleRun target = cache.GetRun(targetIndex);
        _start = target.Start;
        _end = target.End;
        return targetIndex - currentIndex;
    }

    private int MoveFormatOffset(int offset, int count, out int moved)
    {
        var doc = _peer.GetSemanticDocument();
        int current = Math.Clamp(offset, 0, doc.Text.Length);
        return GetFormatRunCache().MoveAcrossBoundaries(current, count, out moved);
    }

    internal static int MoveAcrossSortedBoundaries(
        int[] boundaries,
        int current,
        int count,
        out int moved)
    {
        ArgumentNullException.ThrowIfNull(boundaries);
        moved = 0;
        if (boundaries.Length == 0 || count == 0)
            return current;

        int found = Array.BinarySearch(boundaries, current);
        int baseIndex;
        if (count > 0)
            baseIndex = found >= 0 ? found : Math.Max(-1, ~found - 1);
        else
            baseIndex = found >= 0 ? found : Math.Min(boundaries.Length, ~found);

        int targetIndex = (int)Math.Clamp(
            (long)baseIndex + count,
            0L,
            boundaries.Length - 1L);
        moved = targetIndex - baseIndex;
        return boundaries[targetIndex];
    }

    private MarkdownTextFormatCache GetFormatRunCache() =>
        _peer.GetTextFormatCache();

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

    private string GetStyleName(string elementKey)
    {
        string fallback = MarkdownLocalizedStrings.StyleName(elementKey);
        string? key = MarkdownLocalizedStrings.StyleNameKey(elementKey);
        return key is null
            ? fallback
            : _peer.OwnerControl.ResolveLocalizedString(key, fallback);
    }

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

    private static int MoveOffset(
        string text,
        TextElementBoundaryIndex textElements,
        int offset,
        TextUnit unit,
        int count,
        out int moved)
    {
        if (unit == TextUnit.Character)
            return textElements.Move(offset, count, out moved);

        moved = 0;
        int current = Math.Clamp(offset, 0, text.Length);
        int direction = Math.Sign(count);
        long steps = Math.Min(Math.Abs((long)count), (long)text.Length + 1);
        for (long i = 0; i < steps; i++)
        {
            int next = unit switch
            {
                TextUnit.Word => direction > 0
                    ? textElements.FindNextWordStart(current)
                    : textElements.FindPreviousWordStart(current),
                TextUnit.Line or TextUnit.Paragraph => direction > 0 ? NextLineStart(text, current) : PreviousLineStart(text, current),
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

    private static int UnitStart(
        string text,
        TextElementBoundaryIndex textElements,
        int offset,
        TextUnit unit)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        if (offset >= text.Length) return text.Length;

        return unit switch
        {
            TextUnit.Character => textElements.FindBoundaries(offset).Start,
            TextUnit.Word => textElements.FindWordBoundaries(offset).Start,
            TextUnit.Line or TextUnit.Paragraph => FindLineBoundaries(text, offset).Start,
            TextUnit.Document => 0,
            _ => offset,
        };
    }

    private static int UnitEnd(
        string text,
        TextElementBoundaryIndex textElements,
        int start,
        TextUnit unit)
    {
        start = Math.Clamp(start, 0, text.Length);
        if (start >= text.Length) return text.Length;

        return unit switch
        {
            TextUnit.Character => textElements.FindBoundaries(start).End,
            TextUnit.Word => textElements.FindWordBoundaries(start).End,
            TextUnit.Line or TextUnit.Paragraph => FindLineBoundaries(text, start).End,
            TextUnit.Document => text.Length,
            _ => start,
        };
    }

    internal static TextUnit NormalizeSupportedTextUnit(TextUnit unit) =>
        unit == TextUnit.Page ? TextUnit.Document : unit;

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
