using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Text;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Lightweight peer representing a single block (heading, paragraph, list
/// item, etc.) in the rendered document. Exposes its aggregated plain text
/// as the accessible name and maps headings/links to the appropriate
/// AutomationControlType so screen readers can announce structure.
/// </summary>
internal sealed partial class MarkdownBlockPeer : FrameworkElementAutomationPeer, ITextProvider, ITextProvider2
{
    private readonly MarkdownRendererControl _owner;
    private readonly MarkdownAutomationPeer _root;
    private readonly InlineContainerBox _box;

    public MarkdownBlockPeer(MarkdownRendererControl owner, MarkdownAutomationPeer root, InlineContainerBox box) : base(owner)
    {
        _owner = owner;
        _root = root;
        _box = box;
    }

    public ITextRangeProvider DocumentRange
    {
        get
        {
            var (start, end) = GetTextRange();
            return new MarkdownTextRangeProvider(_root, start, end, _box);
        }
    }

    public SupportedTextSelection SupportedTextSelection => _root.SupportedTextSelection;

    protected override string GetClassNameCore() =>
        _box.ElementKey == MarkdownElementKeys.CodeBlock ? "MarkdownCodeBlock" : "MarkdownBlock";
    protected override string GetAutomationIdCore() => MarkdownAutomationIdentity.ForBlock(_box);
    protected override int GetCultureCore() => _owner.AutomationCultureLcid;
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
    protected override bool IsKeyboardFocusableCore() => false;
    protected override bool HasKeyboardFocusCore() => false;
    protected override void SetFocusCore()
    {
        // This virtual text peer shares the renderer's FrameworkElement owner;
        // it is not an independent keyboard focus target.
    }

    protected override System.Collections.Generic.IList<AutomationPeer> GetChildrenCore()
    {
        // Surface inline links so screen readers can navigate hyperlinks
        // within a paragraph/heading independent of the surrounding text, and
        // keep inline images/embeds as real semantic children instead of
        // flattening them into the paragraph text peer.
        return _root.GetInlineChildPeers(_box);
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
    {
        // Map by element key. Headings → Header, links → Hyperlink, code → Custom.
        return _box.ElementKey switch
        {
            MarkdownElementKeys.Heading1 or MarkdownElementKeys.Heading2 or
            MarkdownElementKeys.Heading3 or MarkdownElementKeys.Heading4 or
            MarkdownElementKeys.Heading5 or MarkdownElementKeys.Heading6 => AutomationControlType.Header,
            _ => AutomationControlType.Text,
        };
    }

    protected override AutomationHeadingLevel GetHeadingLevelCore()
    {
        return _box.ElementKey switch
        {
            MarkdownElementKeys.Heading1 => AutomationHeadingLevel.Level1,
            MarkdownElementKeys.Heading2 => AutomationHeadingLevel.Level2,
            MarkdownElementKeys.Heading3 => AutomationHeadingLevel.Level3,
            MarkdownElementKeys.Heading4 => AutomationHeadingLevel.Level4,
            MarkdownElementKeys.Heading5 => AutomationHeadingLevel.Level5,
            MarkdownElementKeys.Heading6 => AutomationHeadingLevel.Level6,
            _ => AutomationHeadingLevel.None,
        };
    }

    protected override string GetNameCore()
    {
        if (_root.TryGetTextRangeForInlineBox(_box, out int start, out int end))
        {
            var document = _root.GetSemanticDocument();
            start = System.Math.Clamp(start, 0, document.Text.Length);
            end = System.Math.Clamp(end, start, document.Text.Length);
            return document.Text.Substring(start, end - start);
        }

        return string.Empty;
    }

    protected override string GetHelpTextCore()
    {
        return _box.ElementKey == MarkdownElementKeys.CodeBlock && !string.IsNullOrWhiteSpace(_box.CodeLanguage)
            ? _owner.ResolveFormattedLocalizedString(
                Hosting.MarkdownStringKeys.CodeLanguageHelp,
                MarkdownLocalizedStrings.CodeLanguageHelpFormat,
                _box.CodeLanguage)
            : string.Empty;
    }

    protected override object GetPatternCore(PatternInterface patternIinterface)
    {
        if (patternIinterface == PatternInterface.Text || patternIinterface == PatternInterface.Text2) return this;
        return base.GetPatternCore(patternIinterface);
    }

    public ITextRangeProvider[] GetSelection()
    {
        if (!_owner.IsSelectionEnabled)
            return System.Array.Empty<ITextRangeProvider>();

        var (blockStart, blockEnd) = GetTextRange();
        var selection = _root.GetSelection();
        var clipped = new List<ITextRangeProvider>();
        foreach (var range in selection)
        {
            if (range is not MarkdownTextRangeProvider markdownRange)
                continue;

            if (markdownRange.End < blockStart || markdownRange.Start > blockEnd)
                continue;

            int start = System.Math.Max(blockStart, markdownRange.Start);
            int end = System.Math.Min(blockEnd, markdownRange.End);
            if (end >= start)
                clipped.Add(new MarkdownTextRangeProvider(_root, start, end));
        }

        return clipped.ToArray();
    }

    public ITextRangeProvider[] GetVisibleRanges()
    {
        var (blockStart, blockEnd) = GetTextRange();
        var clipped = new List<ITextRangeProvider>();
        foreach (ITextRangeProvider range in _root.GetVisibleRanges())
        {
            if (range is not MarkdownTextRangeProvider markdownRange ||
                markdownRange.End < blockStart ||
                markdownRange.Start > blockEnd)
            {
                continue;
            }

            clipped.Add(new MarkdownTextRangeProvider(
                _root,
                System.Math.Max(blockStart, markdownRange.Start),
                System.Math.Min(blockEnd, markdownRange.End)));
        }

        return clipped.ToArray();
    }

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) =>
        _root.RangeFromChild(childElement);

    public ITextRangeProvider RangeFromPoint(Windows.Foundation.Point screenLocation)
    {
        var range = _root.RangeFromPoint(screenLocation);
        if (range is not MarkdownTextRangeProvider markdownRange)
            return range;

        var (blockStart, blockEnd) = GetTextRange();
        int offset = System.Math.Clamp(markdownRange.Start, blockStart, blockEnd);
        return new MarkdownTextRangeProvider(_root, offset, offset);
    }

    public ITextRangeProvider RangeFromAnnotation(IRawElementProviderSimple annotationElement) =>
        new MarkdownTextRangeProvider(_root, GetTextRange().Start, GetTextRange().Start);

    public ITextRangeProvider GetCaretRange(out bool isActive)
    {
        var selection = GetSelection();
        ITextRangeProvider caretRange = selection.Length > 0
            ? selection[0]
            : new MarkdownTextRangeProvider(_root, GetTextRange().Start, GetTextRange().Start);
        isActive = _root.IsTextCaretActive;
        return caretRange;
    }

    protected override Windows.Foundation.Rect GetBoundingRectangleCore()
    {
        // Route every semantic block through the root's nested-scroll-aware
        // document-to-screen conversion so Narrator and pointer geometry stay
        // aligned when the renderer is hosted inside a page ScrollViewer.
        return _root.GetScreenRectForDocumentRect(_box.Bounds);
    }

    protected override bool IsOffscreenCore() =>
        _root.IsScreenRectOffscreen(GetBoundingRectangleCore());

    internal MarkdownRendererControl OwnerControl => _owner;
    internal InlineContainerBox Box => _box;
    internal bool IsOffscreenForChild(Windows.Foundation.Rect screenRect) =>
        _root.IsScreenRectOffscreen(screenRect);
    internal Windows.Foundation.Rect GetScreenRectForDocumentRect(Windows.Foundation.Rect documentRect) =>
        _root.GetScreenRectForDocumentRect(documentRect);
    /// <summary>Internal accessor that exposes the computed bounding rect so
    /// child link peers can compose against the same screen-space math without
    /// duplicating it.</summary>
    internal Windows.Foundation.Rect GetBoundingRectangleCoreInternal() => GetBoundingRectangleCore();

    private (int Start, int End) GetTextRange()
    {
        return _root.TryGetTextRangeForInlineBox(_box, out int start, out int end)
            ? (start, end)
            : (0, 0);
    }
}
