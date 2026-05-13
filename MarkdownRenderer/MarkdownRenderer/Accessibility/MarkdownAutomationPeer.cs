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
    // Reusable per-block peer cache keyed on the underlying box's identity.
    // We don't cache the children *list* itself because returning the same
    // List<AutomationPeer> across UIA traversals affects how XAML's UIA
    // tree-walker enumerates descendants and ends up double-counting
    // realised embed buttons under both this peer and the underlying
    // visual tree. Caching just the individual MarkdownBlockPeer instances
    // preserves identity-based focus tracking (Narrator's "where am I"
    // remains stable) without disturbing the broader tree shape.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<InlineContainerBox, MarkdownBlockPeer> _peerCache = new();

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
                if (!_peerCache.TryGetValue(icb, out var peer))
                {
                    peer = new MarkdownBlockPeer(_owner, icb);
                    _peerCache.Add(icb, peer);
                }
                sink.Add(peer);
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
            case ImageBox ib:
                // Prefer the explicit alt text. When the SVG provides
                // <title>/<desc> and alt is empty, fall back to those so
                // assistive tech still has a meaningful name.
                if (!string.IsNullOrEmpty(ib.Alt)) { sb.Append(ib.Alt); sb.Append('\n'); }
                else if (!string.IsNullOrEmpty(ib.SvgTitle)) { sb.Append(ib.SvgTitle); sb.Append('\n'); }
                if (!string.IsNullOrEmpty(ib.SvgDesc)) { sb.Append(ib.SvgDesc); sb.Append('\n'); }
                break;
        }
    }
}
