using System.Collections.Generic;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Provider;
using MarkdownRenderer.Accessibility.Internal.TextRanges;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout.Internal.Inlines;

namespace MarkdownRenderer.Accessibility.Internal.Peers;

/// <summary>
/// The UIA TextPattern of a child peer whose text is one contiguous range of
/// the document text, such as a paragraph or a code block. The peer implements
/// <see cref="ITextProvider"/> and forwards here with its current range.
/// Ranges are created against the root peer so they share its projection, and
/// selection, visible, point and caret ranges are clipped to the peer's range.
/// </summary>
internal sealed class MarkdownScopedTextPattern
{
    private readonly MarkdownRendererControl _owner;
    private readonly MarkdownAutomationPeer _root;

    internal MarkdownScopedTextPattern(MarkdownRendererControl owner, MarkdownAutomationPeer root)
    {
        _owner = owner;
        _root = root;
    }

    internal SupportedTextSelection SupportedTextSelection => _root.SupportedTextSelection;

    internal ITextRangeProvider GetDocumentRange(int start, int end, InlineContainerBox? exactRangeScope) =>
        new MarkdownTextRangeProvider(_root, start, end, exactRangeScope);

    internal ITextRangeProvider[] GetSelection(int scopeStart, int scopeEnd)
    {
        if (!_owner.IsSelectionEnabled)
            return System.Array.Empty<ITextRangeProvider>();

        var clipped = new List<ITextRangeProvider>();
        foreach (var range in _root.GetSelection())
        {
            if (range is not MarkdownTextRangeProvider markdownRange)
                continue;

            if (markdownRange.End < scopeStart || markdownRange.Start > scopeEnd)
                continue;

            int start = System.Math.Max(scopeStart, markdownRange.Start);
            int end = System.Math.Min(scopeEnd, markdownRange.End);
            if (end >= start)
                clipped.Add(new MarkdownTextRangeProvider(_root, start, end));
        }

        return clipped.ToArray();
    }

    internal ITextRangeProvider[] GetVisibleRanges(int scopeStart, int scopeEnd)
    {
        var clipped = new List<ITextRangeProvider>();
        foreach (ITextRangeProvider range in _root.GetVisibleRanges())
        {
            if (range is not MarkdownTextRangeProvider markdownRange ||
                markdownRange.End < scopeStart ||
                markdownRange.Start > scopeEnd)
            {
                continue;
            }

            clipped.Add(new MarkdownTextRangeProvider(
                _root,
                System.Math.Max(scopeStart, markdownRange.Start),
                System.Math.Min(scopeEnd, markdownRange.End)));
        }

        return clipped.ToArray();
    }

    internal ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) =>
        _root.RangeFromChild(childElement);

    internal ITextRangeProvider RangeFromPoint(
        Windows.Foundation.Point screenLocation,
        int scopeStart,
        int scopeEnd)
    {
        var range = _root.RangeFromPoint(screenLocation);
        if (range is not MarkdownTextRangeProvider markdownRange)
            return range;

        int offset = System.Math.Clamp(markdownRange.Start, scopeStart, scopeEnd);
        return new MarkdownTextRangeProvider(_root, offset, offset);
    }

    internal ITextRangeProvider RangeFromAnnotation(int scopeStart) =>
        new MarkdownTextRangeProvider(_root, scopeStart, scopeStart);

    internal ITextRangeProvider GetCaretRange(int scopeStart, int scopeEnd, out bool isActive)
    {
        var selection = GetSelection(scopeStart, scopeEnd);
        ITextRangeProvider caretRange = selection.Length > 0
            ? selection[0]
            : new MarkdownTextRangeProvider(_root, scopeStart, scopeStart);
        isActive = _root.IsTextCaretActive;
        return caretRange;
    }
}
