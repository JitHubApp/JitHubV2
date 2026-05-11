using System.Text;
using Microsoft.UI.Xaml.Automation.Peers;
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
internal sealed partial class MarkdownBlockPeer : FrameworkElementAutomationPeer
{
    private readonly MarkdownRendererControl _owner;
    private readonly InlineContainerBox _box;

    public MarkdownBlockPeer(MarkdownRendererControl owner, InlineContainerBox box) : base(owner)
    {
        _owner = owner;
        _box = box;
    }

    protected override string GetClassNameCore() => "MarkdownBlock";

    protected override System.Collections.Generic.IList<AutomationPeer> GetChildrenCore()
    {
        // Surface inline links so screen readers can navigate hyperlinks
        // within a paragraph/heading independent of the surrounding text.
        var list = new System.Collections.Generic.List<AutomationPeer>();
        foreach (var run in _box.Runs)
        {
            if (run is LinkRun lr) list.Add(new MarkdownLinkPeer(_owner, lr));
        }
        return list;
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
        double scale = _owner.XamlRoot?.RasterizationScale ?? 1.0;
        double relX = _box.Bounds.X;
        double relY = _box.Bounds.Y - scrollY;
        double x = ownerScreen.X + relX * scale;
        double y = ownerScreen.Y + relY * scale;
        double w = _box.Bounds.Width * scale;
        double h = _box.Bounds.Height * scale;
        return new Windows.Foundation.Rect(x, y, w, h);
    }
}
