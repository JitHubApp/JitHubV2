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
        // Link peers are cached per LinkRun on the owner so peer identity is
        // stable across repeated UIA traversals.
        var list = new System.Collections.Generic.List<AutomationPeer>();
        foreach (var run in _box.Runs)
        {
            if (run is LinkRun lr) list.Add(_owner.GetOrCreateLinkPeer(this, lr));
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
        double w = _box.Bounds.Width * scale;
        double h = _box.Bounds.Height * scale;
        double x;
        if (_owner.FlowDirection == Microsoft.UI.Xaml.FlowDirection.RightToLeft)
        {
            // In RTL, layout coordinates remain LTR but the visual is mirrored
            // about the renderer's right edge. Reflect x within the owner's
            // screen rect so screen readers get the visually-correct rect.
            x = ownerScreen.X + ownerScreen.Width - (relX * scale) - w;
        }
        else
        {
            x = ownerScreen.X + relX * scale;
        }
        double y = ownerScreen.Y + relY * scale;
        return new Windows.Foundation.Rect(x, y, w, h);
    }

    internal MarkdownRendererControl OwnerControl => _owner;
    internal InlineContainerBox Box => _box;
    /// <summary>Internal accessor that exposes the computed bounding rect so
    /// child link peers can compose against the same screen-space math without
    /// duplicating it.</summary>
    internal Windows.Foundation.Rect GetBoundingRectangleCoreInternal() => GetBoundingRectangleCore();
}
