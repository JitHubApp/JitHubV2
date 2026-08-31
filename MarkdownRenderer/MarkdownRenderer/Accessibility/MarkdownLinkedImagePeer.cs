using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Exposes an inline image wrapped in a link as one keyboard-focusable UIA
/// hyperlink. The alt text remains its accessible name and invocation follows
/// the same link pipeline as pointer and keyboard activation.
/// </summary>
internal sealed partial class MarkdownLinkedImagePeer : FrameworkElementAutomationPeer, IInvokeProvider
{
    private readonly MarkdownRendererControl _owner;
    private readonly MarkdownBlockPeer _parent;
    private readonly InlineImageRun _run;

    public MarkdownLinkedImagePeer(
        MarkdownRendererControl owner,
        MarkdownBlockPeer parent,
        InlineImageRun run)
        : base(owner)
    {
        _owner = owner;
        _parent = parent;
        _run = run;
    }

    internal InlineImageRun Run => _run;

    protected override string GetClassNameCore() => "MarkdownLinkedImage";

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Hyperlink;

    protected override string GetNameCore() => string.IsNullOrWhiteSpace(_run.AltText)
        ? MarkdownLocalizedStrings.ImageName
        : _run.AltText;

    protected override string GetHelpTextCore() => _run.LinkUrl ?? string.Empty;

    protected override bool IsKeyboardFocusableCore() => true;

    protected override bool HasKeyboardFocusCore() =>
        _owner.IsKeyboardFocusOnLinkedImage(_run);

    protected override void SetFocusCore()
    {
        _owner.FocusLinkedImageFromAutomation(_run);
    }

    protected override object GetPatternCore(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Invoke
            ? this
            : base.GetPatternCore(patternInterface);

    public void Invoke()
    {
        _owner.RaiseLinkedImageClickFromAutomation(_run);
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

    protected override bool IsOffscreenCore() =>
        _parent.IsOffscreenForChild(GetBoundingRectangleCore());

    internal void RaiseAutomationFocusChanged()
    {
        RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
    }
}
