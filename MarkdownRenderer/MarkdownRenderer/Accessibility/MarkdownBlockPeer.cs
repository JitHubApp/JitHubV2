using System;
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
    private readonly WeakReference<InlineContainerBox> _boxReference;
    private readonly MarkdownAutomationPeerLifetime _lifetime;

    public MarkdownBlockPeer(MarkdownRendererControl owner, MarkdownAutomationPeer root, InlineContainerBox box) : base(owner)
    {
        _owner = owner;
        _root = root;
        _boxReference = new(box);
        _lifetime = MarkdownAutomationPeerLifetime.Capture(owner, root);
    }

    public ITextRangeProvider DocumentRange
    {
        get
        {
            EnsureCurrentDocument();
            var (start, end) = GetTextRange();
            return new MarkdownTextRangeProvider(_root, start, end, _box);
        }
    }

    public SupportedTextSelection SupportedTextSelection
    {
        get { EnsureCurrentDocument(); return _root.SupportedTextSelection; }
    }

    protected override string GetClassNameCore()
    {
        EnsureCurrentDocument();
        return _box.ElementKey == MarkdownElementKeys.CodeBlock ? "MarkdownCodeBlock" : "MarkdownBlock";
    }

    protected override string GetAutomationIdCore()
    {
        EnsureCurrentDocument();
        return MarkdownAutomationIdentity.ForBlock(_box);
    }

    protected override int GetCultureCore()
    {
        EnsureCurrentDocument();
        return _owner.AutomationCultureLcid;
    }

    protected override bool IsControlElementCore()
    {
        EnsureCurrentDocument();
        return true;
    }

    protected override bool IsContentElementCore()
    {
        EnsureCurrentDocument();
        return true;
    }

    protected override bool IsKeyboardFocusableCore()
    {
        EnsureCurrentDocument();
        return false;
    }

    protected override bool HasKeyboardFocusCore()
    {
        EnsureCurrentDocument();
        return false;
    }

    protected override void SetFocusCore()
    {
        EnsureCurrentDocument();
        // This virtual text peer shares the renderer's FrameworkElement owner;
        // it is not an independent keyboard focus target.
    }

    protected override System.Collections.Generic.IList<AutomationPeer> GetChildrenCore()
    {
        EnsureCurrentDocument();
        // Surface inline links so screen readers can navigate hyperlinks
        // within a paragraph/heading independent of the surrounding text, and
        // keep inline images/embeds as real semantic children instead of
        // flattening them into the paragraph text peer.
        return _root.GetInlineChildPeers(_box);
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
    {
        EnsureCurrentDocument();
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
        EnsureCurrentDocument();
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
        EnsureCurrentDocument();
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
        EnsureCurrentDocument();
        return _box.ElementKey == MarkdownElementKeys.CodeBlock && !string.IsNullOrWhiteSpace(_box.CodeLanguage)
            ? _owner.ResolveFormattedLocalizedString(
                Hosting.MarkdownStringKeys.CodeLanguageHelp,
                MarkdownLocalizedStrings.CodeLanguageHelpFormat,
                _box.CodeLanguage)
            : string.Empty;
    }

    protected override object GetPatternCore(PatternInterface patternIinterface)
    {
        EnsureCurrentDocument();
        if (patternIinterface == PatternInterface.Text || patternIinterface == PatternInterface.Text2) return this;
        return base.GetPatternCore(patternIinterface);
    }

    public ITextRangeProvider[] GetSelection()
    {
        EnsureCurrentDocument();
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
        EnsureCurrentDocument();
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

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement)
    {
        EnsureCurrentDocument();
        return _root.RangeFromChild(childElement);
    }

    public ITextRangeProvider RangeFromPoint(Windows.Foundation.Point screenLocation)
    {
        EnsureCurrentDocument();
        var range = _root.RangeFromPoint(screenLocation);
        if (range is not MarkdownTextRangeProvider markdownRange)
            return range;

        var (blockStart, blockEnd) = GetTextRange();
        int offset = System.Math.Clamp(markdownRange.Start, blockStart, blockEnd);
        return new MarkdownTextRangeProvider(_root, offset, offset);
    }

    public ITextRangeProvider RangeFromAnnotation(IRawElementProviderSimple annotationElement)
    {
        EnsureCurrentDocument();
        int start = GetTextRange().Start;
        return new MarkdownTextRangeProvider(_root, start, start);
    }

    public ITextRangeProvider GetCaretRange(out bool isActive)
    {
        EnsureCurrentDocument();
        var selection = GetSelection();
        ITextRangeProvider caretRange = selection.Length > 0
            ? selection[0]
            : new MarkdownTextRangeProvider(_root, GetTextRange().Start, GetTextRange().Start);
        isActive = _root.IsTextCaretActive;
        return caretRange;
    }

    protected override Windows.Foundation.Rect GetBoundingRectangleCore()
    {
        EnsureCurrentDocument();
        InlineContainerBox box = _box;
        MarkdownSemanticDocument document = _lifetime.GetCurrentSemanticDocument();
        if (!_root.TryReadLayoutGeometry(
                document,
                _lifetime.DocumentGeneration,
                box,
                static (snapshot, currentBox) => snapshot.ToLogicalRectForBlockIndexUnderPaint(
                    currentBox.BlockIndex, currentBox.Bounds),
                out Windows.Foundation.Rect bounds))
        {
            return default;
        }

        // Route every semantic block through the root's nested-scroll-aware
        // document-to-screen conversion so Narrator and pointer geometry stay
        // aligned when the renderer is hosted inside a page ScrollViewer.
        return _root.GetScreenRectForDocumentRect(bounds);
    }

    protected override bool IsOffscreenCore()
    {
        EnsureCurrentDocument();
        return _root.IsScreenRectOffscreen(GetBoundingRectangleCore());
    }

    internal MarkdownRendererControl OwnerControl
    {
        get { EnsureCurrentDocument(); return _owner; }
    }

    internal InlineContainerBox Box
    {
        get { EnsureCurrentDocument(); return _box; }
    }

    internal MarkdownAutomationPeerLifetime Lifetime => _lifetime;
    internal MarkdownAutomationPeer Root => _root;

    internal bool IsOffscreenForChild(Windows.Foundation.Rect screenRect)
    {
        EnsureCurrentDocument();
        return _root.IsScreenRectOffscreen(screenRect);
    }

    internal Windows.Foundation.Rect GetScreenRectForDocumentRect(Windows.Foundation.Rect documentRect)
    {
        EnsureCurrentDocument();
        return _root.GetScreenRectForDocumentRect(documentRect);
    }
    /// <summary>Internal accessor that exposes the computed bounding rect so
    /// child link peers can compose against the same screen-space math without
    /// duplicating it.</summary>
    internal Windows.Foundation.Rect GetBoundingRectangleCoreInternal()
    {
        EnsureCurrentDocument();
        return GetBoundingRectangleCore();
    }

    private (int Start, int End) GetTextRange()
    {
        EnsureCurrentDocument();
        return _root.TryGetTextRangeForInlineBox(_box, out int start, out int end)
            ? (start, end)
            : (0, 0);
    }

    private void EnsureCurrentDocument() => _lifetime.EnsureCurrent();

    private InlineContainerBox _box => MarkdownAutomationPeerLifetime.GetPayloadOrThrow(_boxReference);
}
