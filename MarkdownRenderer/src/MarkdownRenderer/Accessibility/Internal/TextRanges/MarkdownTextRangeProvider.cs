using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Text;
using MarkdownRenderer.Accessibility.Internal.Peers;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Accessibility.Internal.TextRanges;

/// <summary>
/// A UIA text range over the markdown semantic text, pinned to the document
/// generation it was created for.
/// </summary>
/// <remarks>
/// <para>
/// <c>partial</c> is required by the CsWinRT AOT generator (CsWinRT1028), which
/// emits the WinRT vtable for this <see cref="ITextRangeProvider"/> implementation.
/// </para>
/// <para>
/// This file is longer than 500 lines because it implements the whole
/// <see cref="ITextRangeProvider"/> contract, which UIA calls on this one range
/// object, and every member checks the range's document generation and moves
/// the same two endpoints. Unit boundaries come from
/// <see cref="MarkdownTextUnitNavigation"/>, format runs from
/// <see cref="MarkdownTextFormatRunSearch"/> and attribute values from
/// <see cref="MarkdownTextAttributeValues"/>.
/// </para>
/// </remarks>
internal sealed partial class MarkdownTextRangeProvider : ITextRangeProvider
{
    private readonly MarkdownAutomationPeer _peer;
    private readonly int? _exactRangeScopeBlockIndex;
    private readonly long _documentGeneration;
    private int _start;
    private int _end;

    public MarkdownTextRangeProvider(
        MarkdownAutomationPeer peer,
        int start,
        int end,
        InlineContainerBox? exactRangeScope = null)
    {
        _peer = peer;
        _exactRangeScopeBlockIndex = exactRangeScope?.BlockIndex;
        var doc = _peer.GetCurrentTextRangeDocument(out long generation);
        _documentGeneration = generation;
        _start = Math.Clamp(start, 0, doc.Text.Length);
        _end = Math.Clamp(end, _start, doc.Text.Length);
    }

    internal MarkdownTextRangeProvider(
        MarkdownAutomationPeer peer,
        int start,
        int end,
        int? exactRangeScopeBlockIndex,
        long documentGeneration,
        int documentLength)
    {
        _peer = peer;
        _exactRangeScopeBlockIndex = exactRangeScopeBlockIndex;
        _documentGeneration = documentGeneration;
        _start = Math.Clamp(start, 0, documentLength);
        _end = Math.Clamp(end, _start, documentLength);
    }

    public void AddToSelection() => Select();

    public ITextRangeProvider Clone()
    {
        MarkdownSemanticDocument document = GetCurrentDocument();
        return new MarkdownTextRangeProvider(
            _peer,
            _start,
            _end,
            _exactRangeScopeBlockIndex,
            _documentGeneration,
            document.Text.Length);
    }

    public bool Compare(ITextRangeProvider range)
    {
        _ = GetCurrentDocument();
        if (range is not MarkdownTextRangeProvider other)
            return false;

        _ = other.GetCurrentDocument();

        return
               ReferenceEquals(other._peer, _peer) &&
               other._documentGeneration == _documentGeneration &&
               other._start == _start &&
               other._end == _end;
    }

    public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        _ = GetCurrentDocument();
        if (targetRange is not MarkdownTextRangeProvider other) return 0;
        _ = other.GetCurrentDocument();
        if (!ReferenceEquals(other._peer, _peer))
            return 0;

        if (other._documentGeneration != _documentGeneration)
            return 0;
        int a = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int b = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other._end;
        return a.CompareTo(b);
    }

    public void ExpandToEnclosingUnit(TextUnit unit)
    {
        unit = MarkdownTextUnitNavigation.NormalizeSupportedTextUnit(unit);
        var doc = GetCurrentDocument();
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
                var (s, e) = MarkdownTextUnitNavigation.FindLineBoundaries(doc.Text, _start);
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
        _ = GetCurrentDocument();
        var attribute = (AutomationTextAttributesEnum)attributeId;
        var fixedValue = MarkdownTextAttributeValues.GetFixedAttributeValue(_peer, attribute);
        if (fixedValue is not MarkdownTextAttributeValues.UnsupportedAttributeValue)
        {
            return MarkdownTextAttributeValues.AttributeValuesEqual(fixedValue, value) ? Clone() : null;
        }

        MarkdownTextFormatCache cache = GetFormatRunCache();
        var doc = GetCurrentDocument();
        int rangeStart = Math.Clamp(_start, 0, doc.Text.Length);
        int rangeEnd = Math.Clamp(_end, rangeStart, doc.Text.Length);
        bool collapsed = rangeStart == rangeEnd;
        int index = backward ? cache.RunCount - 1 : 0;
        int limit = backward ? -1 : cache.RunCount;
        int step = backward ? -1 : 1;
        for (; index != limit; index += step)
        {
            MarkdownTextStyleRun run = cache.GetRun(index);
            if (!MarkdownTextFormatRunSearch.SpanIntersects(run.Start, run.End, rangeStart, rangeEnd, collapsed))
                continue;
            var candidate = MarkdownTextAttributeValues.GetStyleAttributeValue(_peer, attribute, run);
            if (MarkdownTextAttributeValues.AttributeValuesEqual(candidate, value))
            {
                return CreateRange(
                    collapsed ? rangeStart : Math.Max(rangeStart, run.Start),
                    collapsed ? rangeStart : Math.Min(rangeEnd, run.End));
            }
        }

        return null;
    }

    public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
    {
        var doc = GetCurrentDocument();
        if (string.IsNullOrEmpty(text)) return null;
        int rangeStart = Math.Clamp(_start, 0, doc.Text.Length);
        int rangeEnd = Math.Clamp(_end, rangeStart, doc.Text.Length);
        if (rangeEnd - rangeStart < text.Length) return null;

        string segment = doc.Text.Substring(rangeStart, rangeEnd - rangeStart);
        CompareOptions options = ignoreCase ? CompareOptions.IgnoreCase : CompareOptions.None;
        CompareInfo compareInfo = _peer.OwnerControl.Localization.Culture.CompareInfo;
        int relative = backward
            ? compareInfo.LastIndexOf(segment, text, options)
            : compareInfo.IndexOf(segment, text, options);
        int index = relative >= 0 ? rangeStart + relative : -1;

        return index >= 0
            ? CreateRange(index, index + text.Length)
            : null;
    }

    public object GetAttributeValue(int attributeId)
    {
        _ = GetCurrentDocument();
        var attribute = (AutomationTextAttributesEnum)attributeId;
        var fixedValue = MarkdownTextAttributeValues.GetFixedAttributeValue(_peer, attribute);
        if (fixedValue is not MarkdownTextAttributeValues.UnsupportedAttributeValue)
            return fixedValue!;

        bool haveValue = false;
        object? first = null;
        foreach (var run in EnumerateTextStyleRuns())
        {
            var value = MarkdownTextAttributeValues.GetStyleAttributeValue(_peer, attribute, run);
            if (value is null)
                return UiaReservedAttributeValues.NotSupported;

            if (!haveValue)
            {
                first = value;
                haveValue = true;
                continue;
            }

            if (!MarkdownTextAttributeValues.AttributeValuesEqual(first, value))
                return UiaReservedAttributeValues.Mixed;
        }

        return haveValue ? first! : UiaReservedAttributeValues.NotSupported;
    }

    public void GetBoundingRectangles(out double[] boundingRectangles)
    {
        var doc = GetCurrentDocument();
        if (!_peer.TryReadLayoutGeometry(
                doc,
                _documentGeneration,
                (Document: doc, Range: this),
                static (snapshot, state) => state.Document.GetDocumentRects(
                    state.Range._start,
                    state.Range._end,
                    expandDegenerate: false,
                    placementTransform: snapshot.ToLogicalRectForBlockIndexUnderPaint,
                    placementScope: snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint).ToArray(),
                out Windows.Foundation.Rect[] documentRects))
        {
            boundingRectangles = Array.Empty<double>();
            return;
        }

        var visibleBounds = _peer.GetVisibleScreenBounds();
        for (int index = 0; index < documentRects.Length; index++)
            documentRects[index] = _peer.GetScreenRectForDocumentRect(documentRects[index]);

        boundingRectangles = MarkdownTextRangeGeometry.BuildVisibleBoundingRectangles(
            _start,
            _end,
            documentRects,
            visibleBounds);
    }

    public IRawElementProviderSimple[] GetChildren()
    {
        var doc = GetCurrentDocument();
        var providers = new List<IRawElementProviderSimple>();
        MarkdownSemanticNode enclosing = doc.GetEnclosingNodeForTextRangeByBlockIndex(
            _start,
            _end,
            _exactRangeScopeBlockIndex);
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
        MarkdownSemanticDocument document = GetCurrentDocument();
        MarkdownSemanticNode enclosing = document.GetEnclosingNodeForTextRangeByBlockIndex(
            _start,
            _end,
            _exactRangeScopeBlockIndex);
        return !ReferenceEquals(enclosing, document.Root) &&
               _peer.TryGetPeerForSemanticNode(enclosing, out AutomationPeer peer)
            ? _peer.ProviderFromPeerForTextRange(peer)
            : _peer.ProviderFromPeerForTextRange(_peer);
    }

    public string GetText(int maxLength)
    {
        var doc = GetCurrentDocument();
        int start = Math.Clamp(_start, 0, doc.Text.Length);
        int end = Math.Clamp(_end, start, doc.Text.Length);
        int length = end - start;
        if (maxLength >= 0) length = Math.Min(length, maxLength);
        return length <= 0 ? string.Empty : doc.Text.Substring(start, length);
    }

    public int Move(TextUnit unit, int count)
    {
        var doc = GetCurrentDocument();
        if (count == 0) return 0;
        unit = MarkdownTextUnitNavigation.NormalizeSupportedTextUnit(unit);
        if (unit == TextUnit.Format)
            return MoveByFormat(count);
        string text = doc.Text;
        if (text.Length == 0)
        {
            _start = 0;
            _end = 0;
            return 0;
        }

        int normalizedStart = MarkdownTextUnitNavigation.UnitStart(text, doc.TextElementBoundaries, _start, unit);
        int movedStart = MarkdownTextUnitNavigation.MoveOffset(text, doc.TextElementBoundaries, normalizedStart, unit, count, out int moved);
        _start = MarkdownTextUnitNavigation.UnitStart(text, doc.TextElementBoundaries, movedStart, unit);
        _end = MarkdownTextUnitNavigation.UnitEnd(text, doc.TextElementBoundaries, _start, unit);
        return moved;
    }

    public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
    {
        _ = GetCurrentDocument();
        if (targetRange is not MarkdownTextRangeProvider other) return;
        _ = other.GetCurrentDocument();
        if (!ReferenceEquals(other._peer, _peer))
            throw new InvalidOperationException("Text ranges from different documents cannot be combined.");
        if (other._documentGeneration != _documentGeneration)
            throw new InvalidOperationException("Text ranges from different document generations cannot be combined.");
        int target = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other._end;
        SetEndpoint(endpoint, target);
    }

    public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
    {
        var doc = GetCurrentDocument();
        unit = MarkdownTextUnitNavigation.NormalizeSupportedTextUnit(unit);
        int current = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
        int actual;
        int moved = unit == TextUnit.Format
            ? MoveFormatOffset(current, count, out actual)
            : MarkdownTextUnitNavigation.MoveOffset(doc.Text, doc.TextElementBoundaries, current, unit, count, out actual);
        SetEndpoint(endpoint, moved);
        return actual;
    }

    public void RemoveFromSelection()
    {
        _ = GetCurrentDocument();
        _peer.OwnerControl.AutomationHost.ClearSelection();
    }

    public void ScrollIntoView(bool alignToTop)
    {
        var doc = GetCurrentDocument();
        foreach (var image in doc.GetImagesIntersectingTextRange(_start, _end))
            image.EnsureLoading();

        bool hasDocumentRange = doc.TryGetDocumentRange(_start, _end, out var documentRange);

        if (_peer.TryReadLayoutGeometry(
                doc,
                _documentGeneration,
                (Document: doc, Range: this),
                static (snapshot, state) => state.Document.GetDocumentRects(
                    state.Range._start,
                    state.Range._end,
                    expandDegenerate: true,
                    placementTransform: snapshot.ToLogicalRectForBlockIndexUnderPaint,
                    placementScope: snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint).ToArray(),
                out Windows.Foundation.Rect[] documentRects))
        {
            if (MarkdownTextRangeGeometry.TryGetFirstNonEmptyScrollRect(documentRects, out var rect))
            {
                _peer.OwnerControl.AutomationScrolling.ScrollIntoView(rect, alignToTop);
                return;
            }
        }

        // Lazy layout can expose semantic text before the target block has a
        // realized glyph rectangle. Fall back to its stable document position
        // so the owning control can realize and reveal that viewport band.
        if (hasDocumentRange)
            _peer.OwnerControl.ScrollToBlock(documentRange.Normalized().Start.BlockIndex);
    }

    public void Select()
    {
        var doc = GetCurrentDocument();
        if (doc.TryGetDocumentRange(_start, _end, out var range))
            _peer.OwnerControl.AutomationHost.Select(range);
    }

    internal int Start
    {
        get
        {
            _ = GetCurrentDocument();
            return _start;
        }
    }

    internal int End
    {
        get
        {
            _ = GetCurrentDocument();
            return _end;
        }
    }

    private MarkdownSemanticDocument GetCurrentDocument() =>
        _peer.GetTextRangeDocument(_documentGeneration);

    private MarkdownTextRangeProvider CreateRange(int start, int end)
    {
        MarkdownSemanticDocument document = GetCurrentDocument();
        return new MarkdownTextRangeProvider(
            _peer,
            start,
            end,
            null,
            _documentGeneration,
            document.Text.Length);
    }

    private IEnumerable<MarkdownTextStyleRun> EnumerateTextStyleRuns()
    {
        var doc = GetCurrentDocument();
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
            if (!MarkdownTextFormatRunSearch.SpanIntersects(run.Start, run.End, rangeStart, rangeEnd, collapsed))
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
        var doc = GetCurrentDocument();
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
        return cache.GetRun(MarkdownTextFormatRunSearch.FindFormatRunIndex(cache, offset, doc.Text.Length));
    }

    private int MoveByFormat(int count)
    {
        MarkdownTextFormatCache cache = GetFormatRunCache();
        if (cache.RunCount == 0)
            return 0;

        int documentLength = GetCurrentDocument().Text.Length;
        int currentIndex = MarkdownTextFormatRunSearch.FindFormatRunIndex(
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
        var doc = GetCurrentDocument();
        int current = Math.Clamp(offset, 0, doc.Text.Length);
        return GetFormatRunCache().MoveAcrossBoundaries(current, count, out moved);
    }

    private MarkdownTextFormatCache GetFormatRunCache() =>
        _peer.GetTextFormatCache();

    private ElementStyle GetStyle(string elementKey) =>
        _peer.OwnerControl.AutomationHost.Theme?.GetStyle(elementKey) ?? new ElementStyle();

    private void SetEndpoint(TextPatternRangeEndpoint endpoint, int value)
    {
        var doc = GetCurrentDocument();
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
}
