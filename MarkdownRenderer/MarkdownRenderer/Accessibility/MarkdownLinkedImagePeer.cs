using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Hosting;
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
    private MarkdownImageAccessibilityState _lastImageState;

    public MarkdownLinkedImagePeer(
        MarkdownRendererControl owner,
        MarkdownBlockPeer parent,
        InlineImageRun run)
        : base(owner)
    {
        _owner = owner;
        _parent = parent;
        _run = run;
        _lastImageState = run.Image.AccessibilityState;
    }

    internal InlineImageRun Run => _run;

    protected override string GetClassNameCore() => "MarkdownLinkedImage";

    protected override AutomationControlType GetAutomationControlTypeCore() =>
        AutomationControlType.Hyperlink;

    protected override string GetNameCore() => GetImageName(_run.Image.AccessibilityState);

    protected override string GetHelpTextCore() => _run.LinkUrl ?? string.Empty;

    protected override string GetItemStatusCore() =>
        _run.Image.AccessibilityState == MarkdownImageAccessibilityState.Loading
            ? GetImageName(MarkdownImageAccessibilityState.Loading)
            : string.Empty;

    protected override string GetAutomationIdCore() =>
        MarkdownAutomationIdentity.ForRun("LinkedImage", _parent.Box, _run);

    protected override int GetCultureCore() => _owner.AutomationCultureLcid;

    protected override AutomationLiveSetting GetLiveSettingCore() => AutomationLiveSetting.Polite;

    protected override bool IsKeyboardFocusableCore() => true;

    protected override bool HasKeyboardFocusCore() =>
        _owner.IsKeyboardFocusOnLinkedImage(_run);

    protected override System.Collections.Generic.IList<AutomationPeer> GetChildrenCore()
    {
        // The peer is synthetic and shares the document control as its XAML
        // owner. It is a semantic leaf; base visual-child enumeration would
        // point back at the owner's tree and make UIA navigation cyclic.
        return System.Array.Empty<AutomationPeer>();
    }

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

        return _parent.GetScreenRectForDocumentRect(docRect);
    }

    protected override bool IsOffscreenCore() =>
        _parent.IsOffscreenForChild(GetBoundingRectangleCore());

    internal void RaiseAutomationFocusChanged()
    {
        RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
    }

    internal void NotifyImageStatusChanged()
    {
        MarkdownImageAccessibilityState current = _run.Image.AccessibilityState;
        if (current == _lastImageState)
            return;

        string oldName = GetImageName(_lastImageState);
        string newName = GetImageName(current);
        string oldStatus = _lastImageState == MarkdownImageAccessibilityState.Loading ? oldName : string.Empty;
        string newStatus = current == MarkdownImageAccessibilityState.Loading ? newName : string.Empty;
        _lastImageState = current;
        RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, oldName, newName);
        RaisePropertyChangedEvent(AutomationElementIdentifiers.ItemStatusProperty, oldStatus, newStatus);
        RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private string GetImageName(MarkdownImageAccessibilityState state)
    {
        string description = string.IsNullOrWhiteSpace(_run.AltText)
            ? string.IsNullOrWhiteSpace(_run.LinkTitle)
                ? _owner.ResolveLocalizedString(
                    MarkdownStringKeys.ImageName,
                    MarkdownLocalizedStrings.ImageName)
                : _run.LinkTitle!
            : _run.AltText;

        return state switch
        {
            MarkdownImageAccessibilityState.Loading => _owner.ResolveFormattedLocalizedString(
                MarkdownStringKeys.ImageLoading,
                MarkdownLocalizedStrings.ImageLoadingFormat,
                description),
            MarkdownImageAccessibilityState.Error => _owner.ResolveFormattedLocalizedString(
                MarkdownStringKeys.ImageError,
                MarkdownLocalizedStrings.ImageErrorFormat,
                description),
            _ => description,
        };
    }
}
