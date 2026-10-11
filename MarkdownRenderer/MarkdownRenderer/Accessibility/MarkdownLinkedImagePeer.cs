using System;
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
    private readonly WeakReference<MarkdownBlockPeer> _parentReference;
    private readonly WeakReference<InlineImageRun> _runReference;
    private readonly MarkdownAutomationPeerLifetime _lifetime;
    private MarkdownImageAccessibilityState _lastImageState;

    public MarkdownLinkedImagePeer(
        MarkdownRendererControl owner,
        MarkdownBlockPeer parent,
        InlineImageRun run)
        : base(owner)
    {
        _owner = owner;
        _parentReference = new(parent);
        _runReference = new(run);
        _lifetime = parent.Lifetime;
        _lastImageState = run.Image.AccessibilityState;
    }

    internal InlineImageRun Run
    {
        get { EnsureCurrentDocument(); return _run; }
    }

    protected override string GetClassNameCore()
    {
        EnsureCurrentDocument();
        return "MarkdownLinkedImage";
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
    {
        EnsureCurrentDocument();
        return AutomationControlType.Hyperlink;
    }

    protected override string GetNameCore()
    {
        EnsureCurrentDocument();
        return GetImageName(_run.Image.AccessibilityState);
    }

    protected override string GetHelpTextCore()
    {
        EnsureCurrentDocument();
        return _run.LinkUrl ?? string.Empty;
    }

    protected override string GetItemStatusCore()
    {
        EnsureCurrentDocument();
        return _run.Image.AccessibilityState == MarkdownImageAccessibilityState.Loading
            ? GetImageName(MarkdownImageAccessibilityState.Loading)
            : string.Empty;
    }

    protected override string GetAutomationIdCore()
    {
        EnsureCurrentDocument();
        return MarkdownAutomationIdentity.ForRun("LinkedImage", _parent.Box, _run);
    }

    protected override int GetCultureCore()
    {
        EnsureCurrentDocument();
        return _owner.AutomationCultureLcid;
    }

    protected override AutomationLiveSetting GetLiveSettingCore()
    {
        EnsureCurrentDocument();
        return AutomationLiveSetting.Polite;
    }

    protected override bool IsKeyboardFocusableCore()
    {
        EnsureCurrentDocument();
        return true;
    }

    protected override bool HasKeyboardFocusCore()
    {
        EnsureCurrentDocument();
        return _owner.IsKeyboardFocusOnLinkedImage(_run);
    }

    protected override System.Collections.Generic.IList<AutomationPeer> GetChildrenCore()
    {
        EnsureCurrentDocument();
        // The peer is synthetic and shares the document control as its XAML
        // owner. It is a semantic leaf; base visual-child enumeration would
        // point back at the owner's tree and make UIA navigation cyclic.
        return System.Array.Empty<AutomationPeer>();
    }

    protected override void SetFocusCore()
    {
        EnsureCurrentDocument();
        _owner.FocusLinkedImageFromAutomation(_run);
    }

    protected override object GetPatternCore(PatternInterface patternInterface)
    {
        EnsureCurrentDocument();
        return patternInterface == PatternInterface.Invoke
            ? this
            : base.GetPatternCore(patternInterface);
    }

    public void Invoke()
    {
        EnsureCurrentDocument();
        _owner.RaiseLinkedImageClickFromAutomation(_run);
    }

    protected override Windows.Foundation.Rect GetBoundingRectangleCore()
    {
        EnsureCurrentDocument();
        MarkdownBlockPeer parent = _parent;
        var block = parent.Box;
        InlineImageRun run = _run;
        MarkdownSemanticDocument document = _lifetime.GetCurrentSemanticDocument();
        if (!parent.Root.TryReadLayoutGeometry(
                document,
                _lifetime.DocumentGeneration,
                (Block: block, Run: run),
                static (snapshot, state) =>
                {
                    using LayoutPlacementTransformContext.Scope placementScope =
                        snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint(state.Block.BlockIndex);
                    Windows.Foundation.Rect runBounds = state.Block.GetRunRect(state.Run.InlineIndex);
                    Windows.Foundation.Rect bounds = runBounds.Width > 0 && runBounds.Height > 0
                        ? runBounds
                        : state.Block.Bounds;
                    return snapshot.ToLogicalRectForBlockIndexUnderPaint(
                        state.Block.BlockIndex, bounds);
                },
                out Windows.Foundation.Rect docRect))
        {
            return default;
        }

        return parent.GetScreenRectForDocumentRect(docRect);
    }

    protected override bool IsOffscreenCore()
    {
        EnsureCurrentDocument();
        return _parent.IsOffscreenForChild(GetBoundingRectangleCore());
    }

    internal void RaiseAutomationFocusChanged()
    {
        if (_lifetime.IsCurrent())
            RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
    }

    internal void NotifyImageStatusChanged()
    {
        if (!_lifetime.TryGetCurrentPayload(_runReference, out InlineImageRun? run) || run is null)
            return;

        MarkdownImageAccessibilityState current = run.Image.AccessibilityState;
        if (current == _lastImageState)
            return;

        string oldName = GetImageName(run, _lastImageState);
        string newName = GetImageName(run, current);
        string oldStatus = _lastImageState == MarkdownImageAccessibilityState.Loading ? oldName : string.Empty;
        string newStatus = current == MarkdownImageAccessibilityState.Loading ? newName : string.Empty;
        _lastImageState = current;
        RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, oldName, newName);
        RaisePropertyChangedEvent(AutomationElementIdentifiers.ItemStatusProperty, oldStatus, newStatus);
        RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private string GetImageName(MarkdownImageAccessibilityState state) => GetImageName(_run, state);

    private string GetImageName(InlineImageRun run, MarkdownImageAccessibilityState state)
    {
        string description = string.IsNullOrWhiteSpace(run.AltText)
            ? string.IsNullOrWhiteSpace(run.LinkTitle)
                ? _owner.ResolveLocalizedString(
                    MarkdownStringKeys.ImageName,
                    MarkdownLocalizedStrings.ImageName)
                : run.LinkTitle!
            : run.AltText;

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

    private void EnsureCurrentDocument() => _lifetime.EnsureCurrent();

    private MarkdownBlockPeer _parent => MarkdownAutomationPeerLifetime.GetPayloadOrThrow(_parentReference);

    private InlineImageRun _run => MarkdownAutomationPeerLifetime.GetPayloadOrThrow(_runReference);
}
