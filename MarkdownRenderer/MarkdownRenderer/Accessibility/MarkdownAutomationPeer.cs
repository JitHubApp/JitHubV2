using System.Collections.Generic;
using System.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Automation peer for <see cref="MarkdownRendererControl"/>. Exposes the
/// Document control type so screen readers can announce content correctly,
/// aggregates document text into <see cref="GetNameCore"/>, and surfaces
/// inline-container blocks (paragraphs, headings, list-item content, etc.)
/// as structural children with appropriate heading levels and control types.
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
    protected override AutomationLandmarkType GetLandmarkTypeCore() => AutomationLandmarkType.Custom;

    protected override string GetNameCore()
    {
        var snap = _owner.CurrentSnapshot;
        if (snap is null) return "Markdown document";

        var sb = new StringBuilder();
        foreach (var b in snap.Blocks) AppendText(b, sb);
        var text = sb.ToString().Trim();
        if (text.Length == 0) return "Markdown document";
        // Cap to a reasonable size — narrators typically truncate anyway, and
        // very large strings make automation traversal slow.
        return text.Length > 4096 ? text.Substring(0, 4096) : text;
    }

    protected override IList<AutomationPeer> GetChildrenCore()
    {
        var children = new List<AutomationPeer>();
        var snap = _owner.CurrentSnapshot;
        if (snap is null) return children;
        foreach (var b in snap.Blocks) CollectChildren(b, children);
        return children;
    }

    private void CollectChildren(BlockBox box, List<AutomationPeer> sink)
    {
        switch (box)
        {
            case InlineContainerBox icb:
                sink.Add(new MarkdownBlockPeer(_owner, icb));
                break;
            case ListItemBox lib:
                CollectChildren(lib.Marker, sink);
                CollectChildren(lib.Content, sink);
                break;
            case TableBox tb:
                foreach (var c in tb.GetCellBoxes()) CollectChildren(c, sink);
                break;
            case StackBox sb:
                foreach (var c in sb.Children) CollectChildren(c, sink);
                break;
        }
    }

    private static void AppendText(BlockBox box, StringBuilder sb)
    {
        switch (box)
        {
            case InlineContainerBox icb:
                foreach (var run in icb.Runs) sb.Append(run.Text);
                sb.Append('\n');
                break;
            case ListItemBox lib:
                AppendText(lib.Marker, sb);
                sb.Append(' ');
                AppendText(lib.Content, sb);
                break;
            case TableBox tb:
                foreach (var c in tb.GetCellBoxes()) { AppendText(c, sb); sb.Append(' '); }
                sb.Append('\n');
                break;
            case StackBox stack:
                foreach (var c in stack.Children) AppendText(c, sb);
                break;
        }
    }
}
