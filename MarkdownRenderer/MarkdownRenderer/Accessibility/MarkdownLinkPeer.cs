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
    protected override string GetLocalizedControlTypeCore() => "hyperlink";
    protected override string GetNameCore() => _run.Text;
    protected override string GetHelpTextCore() => _run.Url;

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
        // Fall back to the parent block's rect: it's a much tighter bound than
        // the whole renderer and gives spatial UIA navigation (touch-explore,
        // Narrator scan-mode) the correct hit region for the paragraph/heading
        // that contains this link.
        return _parent.GetBoundingRectangleCoreInternal();
    }
}

