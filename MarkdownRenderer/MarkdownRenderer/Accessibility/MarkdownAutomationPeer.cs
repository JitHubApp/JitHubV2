using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using MarkdownRenderer.Controls;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Automation peer for <see cref="MarkdownRendererControl"/>. Exposes the Document
/// control type so screen readers can announce content correctly.
/// </summary>
public sealed partial class MarkdownAutomationPeer : FrameworkElementAutomationPeer
{
    private readonly MarkdownRendererControl _owner;

    public MarkdownAutomationPeer(MarkdownRendererControl owner) : base(owner)
    {
        _owner = owner;
    }

    protected override string GetClassNameCore() => "MarkdownRendererControl";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override string GetNameCore() => "Markdown document";
    protected override AutomationLandmarkType GetLandmarkTypeCore() => AutomationLandmarkType.Custom;
}
