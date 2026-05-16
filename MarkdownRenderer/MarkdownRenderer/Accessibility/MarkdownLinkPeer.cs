using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Lightweight automation peer that surfaces an inline <see cref="LinkRun"/>
/// as a UIA Hyperlink so screen readers can discover, announce, and invoke
/// links within a markdown block. Bounding rectangle falls back to the
/// owning block's rect (per-character link rectangles are not tracked yet,
/// but reporting the parent block is strictly better than reporting the
/// whole renderer for spatial navigation).
/// </summary>
internal sealed partial class MarkdownLinkPeer : FrameworkElementAutomationPeer, IInvokeProvider
{
    private readonly MarkdownRendererControl _owner;
    private readonly MarkdownBlockPeer _parent;
    private readonly LinkRun _run;

    public MarkdownLinkPeer(MarkdownRendererControl owner, MarkdownBlockPeer parent, LinkRun run) : base(owner)
    {
        _owner = owner;
        _parent = parent;
        _run = run;
    }

    internal LinkRun Run => _run;

    protected override string GetClassNameCore() => "MarkdownLink";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Hyperlink;
    protected override string GetNameCore() => _run.Text;
    protected override string GetHelpTextCore() => _run.Url;
    protected override bool IsKeyboardFocusableCore() => true;
    protected override bool HasKeyboardFocusCore() => _owner.IsKeyboardFocusOnLink(_run);

    protected override void SetFocusCore()
    {
        _owner.FocusLinkFromAutomation(_run);
    }

    protected override object GetPatternCore(PatternInterface patternInterface)
    {
        if (patternInterface == PatternInterface.Invoke) return this;
        return base.GetPatternCore(patternInterface);
    }

    public void Invoke()
    {
        // Route through the renderer's public link click pipeline so external
        // subscribers and keyboard / assistive-tech activation behave the same.
        _owner.RaiseLinkClickFromAutomation(_run);
    }

    protected override Windows.Foundation.Rect GetBoundingRectangleCore()
    {
        var docRect = _parent.Box.GetRunRect(_run.InlineIndex);
        if (docRect.Width <= 0 || docRect.Height <= 0)
            return _parent.GetBoundingRectangleCoreInternal();

        var ownerScreen = base.GetBoundingRectangleCore();
        if (ownerScreen.Width <= 0 || ownerScreen.Height <= 0)
            return _parent.GetBoundingRectangleCoreInternal();

        double scale = _owner.XamlRoot?.RasterizationScale ?? 1.0;
        return new Windows.Foundation.Rect(
            ownerScreen.X + docRect.X * scale,
            ownerScreen.Y + (_owner.CurrentContentOffsetY + docRect.Y - _owner.CurrentScrollOffsetY) * scale,
            docRect.Width * scale,
            docRect.Height * scale);
    }

    internal void RaiseAutomationFocusChanged()
    {
        RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
    }
}
