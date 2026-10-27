using System;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Automation;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Lightweight automation peer that surfaces an inline <see cref="LinkRun"/>
/// as a UIA Hyperlink so screen readers can discover, announce, and invoke
/// links within a markdown block. Bounding rectangle falls back to the
/// owning block's rect (per-character link rectangles are not tracked yet,
/// but reporting the parent block is strictly better than reporting the
/// whole renderer for spatial navigation).
/// </summary>
internal sealed partial class MarkdownLinkPeer : FrameworkElementAutomationPeer, IInvokeProvider, IExpandCollapseProvider
{
    private readonly MarkdownRendererControl _owner;
    private readonly WeakReference<MarkdownBlockPeer> _parentReference;
    private readonly WeakReference<LinkRun> _runReference;
    private readonly MarkdownAutomationPeerLifetime _lifetime;

    public MarkdownLinkPeer(MarkdownRendererControl owner, MarkdownBlockPeer parent, LinkRun run) : base(owner)
    {
        _owner = owner;
        _parentReference = new(parent);
        _runReference = new(run);
        _lifetime = parent.Lifetime;
    }

    internal LinkRun Run
    {
        get { EnsureCurrentDocument(); return _run; }
    }

    protected override string GetClassNameCore()
    {
        EnsureCurrentDocument();
        return _run.IsDisclosure ? "MarkdownDisclosure" : "MarkdownLink";
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
    {
        EnsureCurrentDocument();
        return _run.IsDisclosure ? AutomationControlType.Button : AutomationControlType.Hyperlink;
    }

    protected override string GetNameCore()
    {
        EnsureCurrentDocument();
        return _run.AccessibleText;
    }

    protected override string GetHelpTextCore()
    {
        EnsureCurrentDocument();
        return _run.IsDisclosure ? string.Empty : _run.Url;
    }

    protected override string GetAutomationIdCore()
    {
        EnsureCurrentDocument();
        return MarkdownAutomationIdentity.ForRun("Link", _parent.Box, _run);
    }

    protected override int GetCultureCore()
    {
        EnsureCurrentDocument();
        return _owner.AutomationCultureLcid;
    }

    protected override bool IsKeyboardFocusableCore()
    {
        EnsureCurrentDocument();
        return true;
    }

    protected override bool HasKeyboardFocusCore()
    {
        EnsureCurrentDocument();
        return _owner.IsKeyboardFocusOnLink(_run);
    }

    protected override System.Collections.Generic.IList<AutomationPeer> GetChildrenCore()
    {
        EnsureCurrentDocument();
        // This is a synthetic inline peer whose FrameworkElement owner is the
        // document control. Letting the base implementation enumerate that
        // owner's visual children would create a UIA cycle back into the
        // document/hosted-element tree.
        return System.Array.Empty<AutomationPeer>();
    }

    protected override void SetFocusCore()
    {
        EnsureCurrentDocument();
        _owner.FocusLinkFromAutomation(_run);
    }

    protected override object GetPatternCore(PatternInterface patternIinterface)
    {
        EnsureCurrentDocument();
        if (patternIinterface == PatternInterface.Invoke ||
            (patternIinterface == PatternInterface.ExpandCollapse && _run.IsDisclosure))
        {
            return this;
        }
        return base.GetPatternCore(patternIinterface);
    }

    public void Invoke()
    {
        EnsureCurrentDocument();
        // Route through the renderer's public link click pipeline so external
        // subscribers and keyboard / assistive-tech activation behave the same.
        _owner.RaiseLinkClickFromAutomation(_run);
    }

    public ExpandCollapseState ExpandCollapseState
    {
        get
        {
            EnsureCurrentDocument();
            return _run.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        }
    }

    public void Expand()
    {
        EnsureCurrentDocument();
        _owner.SetDisclosureExpanded(_run, expanded: true);
    }

    public void Collapse()
    {
        EnsureCurrentDocument();
        _owner.SetDisclosureExpanded(_run, expanded: false);
    }

    protected override Windows.Foundation.Rect GetBoundingRectangleCore()
    {
        EnsureCurrentDocument();
        MarkdownBlockPeer parent = _parent;
        InlineContainerBox block = parent.Box;
        LinkRun run = _run;
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

    private void EnsureCurrentDocument() => _lifetime.EnsureCurrent();

    private MarkdownBlockPeer _parent => MarkdownAutomationPeerLifetime.GetPayloadOrThrow(_parentReference);

    private LinkRun _run => MarkdownAutomationPeerLifetime.GetPayloadOrThrow(_runReference);
}
