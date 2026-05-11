using Microsoft.UI.Xaml.Automation.Peers;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Lightweight automation peer that surfaces an inline <see cref="LinkRun"/>
/// as a UIA Hyperlink so screen readers can discover and announce links
/// within a markdown block. Inherits its bounding rectangle from the owning
/// block peer; per-character link rectangles aren't tracked yet.
/// </summary>
internal sealed partial class MarkdownLinkPeer : FrameworkElementAutomationPeer
{
    private readonly LinkRun _run;

    public MarkdownLinkPeer(MarkdownRendererControl owner, LinkRun run) : base(owner)
    {
        _run = run;
    }

    protected override string GetClassNameCore() => "MarkdownLink";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Hyperlink;
    protected override string GetLocalizedControlTypeCore() => "hyperlink";
    protected override string GetNameCore() => _run.Text;
    protected override string GetHelpTextCore() => _run.Url;
}
