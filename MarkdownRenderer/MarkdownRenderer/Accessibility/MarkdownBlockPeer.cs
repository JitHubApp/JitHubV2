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
            return new MarkdownTextRangeProvider(_root, start, end);
        }
    }

    public SupportedTextSelection SupportedTextSelection => _root.SupportedTextSelection;

    protected override string GetClassNameCore() => "MarkdownBlock";
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
    protected override bool IsKeyboardFocusableCore() => false;

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
        var sb = new StringBuilder();
        foreach (var run in _box.Runs) sb.Append(run.Text);
        return sb.ToString();
    }

    protected override string GetHelpTextCore()
    {
        return _box.ElementKey == MarkdownElementKeys.CodeBlock && !string.IsNullOrWhiteSpace(_box.CodeLanguage)
            ? MarkdownLocalizedStrings.CodeLanguageHelp(_box.CodeLanguage)
            : string.Empty;
    }

    protected override object GetPatternCore(PatternInterface patternIinterface)
    {
        if (patternIinterface == PatternInterface.Text || patternIinterface == PatternInterface.Text2) return this;
        return base.GetPatternCore(patternIinterface);
    }

    public ITextRangeProvider[] GetSelection()
    {
        var (blockStart, blockEnd) = GetTextRange();
        var selection = _root.GetSelection();
        var clipped = new List<ITextRangeProvider>();
        foreach (var range in selection)
        {
            if (range is not MarkdownTextRangeProvider markdownRange)
                continue;

            int start = System.Math.Max(blockStart, markdownRange.Start);
            int end = System.Math.Min(blockEnd, markdownRange.End);
            if (end >= start)
                clipped.Add(new MarkdownTextRangeProvider(_root, start, end));
        }

        return clipped.Count > 0
            ? clipped.ToArray()
            : new ITextRangeProvider[] { new MarkdownTextRangeProvider(_root, blockStart, blockStart) };
    }

    public ITextRangeProvider[] GetVisibleRanges()
    {
        var (blockStart, blockEnd) = GetTextRange();
        return new ITextRangeProvider[] { new MarkdownTextRangeProvider(_root, blockStart, blockEnd) };
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
        isActive = _owner.FocusState != Microsoft.UI.Xaml.FocusState.Unfocused;
        var selection = GetSelection();
        return selection.Length > 0
            ? selection[0]
            : new MarkdownTextRangeProvider(_root, GetTextRange().Start, GetTextRange().Start);
    }

    protected override Windows.Foundation.Rect GetBoundingRectangleCore()
    {
        // Default FrameworkElementAutomationPeer reports the owning control's
        // bounding rect in screen coordinates, which means every block claims
        // the same rectangle — breaking Narrator's per-block scan navigation.
        // We compose the owner's screen rect (which already does the
        // window-client → screen conversion correctly, including DPI) with
        // this block's offset inside the renderer.
        var ownerScreen = base.GetBoundingRectangleCore();
        if (ownerScreen.Width <= 0 || ownerScreen.Height <= 0) return ownerScreen;

        double scrollY = _owner.CurrentScrollOffsetY;
        double contentOffsetY = _owner.CurrentContentOffsetY;
        double scale = _owner.XamlRoot?.RasterizationScale ?? 1.0;
        double relX = _box.Bounds.X;
        double relY = _box.Bounds.Y - scrollY;
        double w = _box.Bounds.Width * scale;
        double h = _box.Bounds.Height * scale;
        // Layout coordinates are authored RTL-aware (ListItemBox places its
        // marker at content-width when RightToLeft, TableBox arranges cells
        // similarly), so reporting `ownerScreen.X + relX * scale` matches
        // the rendered visual without an extra reflection. We intentionally
        // do NOT mirror here — applying a blanket reflect-about-right-edge
        // would double-mirror children that are already placed in RTL.
        double x = ownerScreen.X + relX * scale;
        double y = ownerScreen.Y + (contentOffsetY + relY) * scale;
        return new Windows.Foundation.Rect(x, y, w, h);
    }

    internal MarkdownRendererControl OwnerControl => _owner;
    internal InlineContainerBox Box => _box;
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
