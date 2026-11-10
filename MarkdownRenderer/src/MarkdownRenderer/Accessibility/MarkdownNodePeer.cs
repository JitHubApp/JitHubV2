using System.Collections.Generic;
using System;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Extensions.Internal;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// The UIA peer for one semantic node: its properties, children and the grid,
/// table, scroll, invoke and toggle patterns the node supports.
/// </summary>
/// <remarks>
/// <para>
/// <c>partial</c> is required by the CsWinRT AOT generator (CsWinRT1028), which
/// emits the WinRT vtable for a peer that adds provider interfaces.
/// </para>
/// <para>
/// This file is longer than 500 lines because <see cref="GetPatternCore"/>
/// returns the peer itself for every grid, table, scroll, invoke and toggle
/// pattern its node supports, so each pattern's explicit interface members
/// must live on this one WinRT object. Names, help text and control types come
/// from <see cref="MarkdownNodeAutomationDescriptor"/> and
/// <see cref="MarkdownNodeAutomationText"/>.
/// </para>
/// </remarks>
internal sealed partial class MarkdownNodePeer : FrameworkElementAutomationPeer,
    IGridProvider,
    ITableProvider,
    IGridItemProvider,
    ITableItemProvider,
    IScrollProvider,
    IInvokeProvider,
    IToggleProvider
{
    private readonly MarkdownRendererControl _owner;
    private readonly MarkdownAutomationPeer _root;
    private readonly WeakReference<MarkdownSemanticNode> _nodeReference;
    private readonly MarkdownAutomationPeerLifetime _lifetime;
    private MarkdownImageAccessibilityState? _lastImageState;
    private MarkdownCodeBlockCopyPeer? _codeBlockCopyPeer;

    public MarkdownNodePeer(MarkdownRendererControl owner, MarkdownAutomationPeer root, MarkdownSemanticNode node) : base(owner)
    {
        _owner = owner;
        _root = root;
        _nodeReference = new(node);
        _lifetime = MarkdownAutomationPeerLifetime.Capture(owner, root);
        _lastImageState = node.ImageBox?.AccessibilityState;
    }

    internal MarkdownSemanticNode Node
    {
        get
        {
            EnsureCurrentDocument();
            return _node;
        }
    }

    protected override string GetClassNameCore()
    {
        EnsureCurrentDocument();
        return MarkdownNodeAutomationDescriptor.GetClassName(_node, TaskMetadata);
    }

    protected override bool IsControlElementCore()
    {
        EnsureCurrentDocument();
        return true;
    }

    protected override bool IsContentElementCore()
    {
        EnsureCurrentDocument();
        return true;
    }

    protected override bool IsKeyboardFocusableCore()
    {
        EnsureCurrentDocument();
        if (IsVectorSemanticNode)
            return MarkdownVectorSemanticPolicy.IsKeyboardFocusable(_node.VectorSemanticFlags);
        if (LinkedInlineVectorScene is not null)
            return true;
        if (HorizontalOverflow is { } overflow)
            return _owner.AutomationHost.IsHorizontalOverflowKeyboardFocusable(overflow);
        // Ordinary semantic peers share the renderer as their FrameworkElement
        // owner, but they are virtual structure rather than independent focus
        // targets. Never inherit the owner's focusability.
        return false;
    }

    protected override bool HasKeyboardFocusCore()
    {
        EnsureCurrentDocument();
        if (IsVectorSemanticNode && _node.VectorSceneBox is { } vector)
        {
            return _owner.AutomationHost.IsKeyboardFocusOnVectorSemantic(vector, _node.VectorSemanticIndex);
        }

        if (LinkedInlineVectorScene is { } inlineScene)
            return _owner.AutomationHost.IsKeyboardFocusOnLinkedInlineVectorScene(inlineScene);

        if (HorizontalOverflow is { } overflow)
            return _owner.AutomationHost.IsKeyboardFocusOnHorizontalOverflow(overflow);

        return false;
    }

    protected override void SetFocusCore()
    {
        EnsureCurrentDocument();
        if (IsVectorSemanticNode)
        {
            if (MarkdownVectorSemanticPolicy.IsKeyboardFocusable(_node.VectorSemanticFlags) &&
                _node.VectorSceneBox is { } vector)
            {
                _owner.AutomationHost.FocusVectorSemantic(vector, _node.VectorSemanticIndex);
            }
            return;
        }

        if (LinkedInlineVectorScene is { } inlineScene)
        {
            _owner.AutomationHost.FocusLinkedAtomic(inlineScene);
            return;
        }

        if (HorizontalOverflow is { } overflow &&
            _owner.AutomationHost.IsHorizontalOverflowKeyboardFocusable(overflow))
        {
            _owner.AutomationHost.FocusHorizontalOverflow(overflow);
            return;
        }

        // Structural peers cannot take focus. Delegating to the base peer would
        // focus the shared renderer owner and make this child falsely report it.
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
    {
        EnsureCurrentDocument();
        return MarkdownNodeAutomationDescriptor.GetControlType(_node, TaskMetadata);
    }

    protected override AutomationHeadingLevel GetHeadingLevelCore()
    {
        EnsureCurrentDocument();
        return MarkdownNodeAutomationDescriptor.GetHeadingLevel(_node);
    }

    protected override string GetNameCore()
    {
        EnsureCurrentDocument();
        return MarkdownNodeAutomationText.GetName(_owner, _root, _node, TaskMetadata);
    }

    protected override string GetHelpTextCore()
    {
        EnsureCurrentDocument();
        return MarkdownNodeAutomationText.GetHelpText(_owner, _node, TaskMetadata);
    }

    protected override string GetItemStatusCore()
    {
        EnsureCurrentDocument();
        return MarkdownNodeAutomationText.GetItemStatus(_owner, _root, _node);
    }

    protected override string GetAutomationIdCore()
    {
        EnsureCurrentDocument();
        return _node.AutomationId ?? MarkdownAutomationIdentity.ForNode(_node);
    }

    protected override int GetCultureCore()
    {
        EnsureCurrentDocument();
        return _owner.AutomationHost.CultureLcid;
    }

    protected override int GetLevelCore()
    {
        EnsureCurrentDocument();
        return _node.Level > 0 ? _node.Level : base.GetLevelCore();
    }

    protected override int GetPositionInSetCore()
    {
        EnsureCurrentDocument();
        return _node.PositionInSet > 0 ? _node.PositionInSet : base.GetPositionInSetCore();
    }

    protected override int GetSizeOfSetCore()
    {
        EnsureCurrentDocument();
        return _node.SizeOfSet > 0 ? _node.SizeOfSet : base.GetSizeOfSetCore();
    }

    protected override AutomationLiveSetting GetLiveSettingCore()
    {
        EnsureCurrentDocument();
        if (_node.Role == MarkdownSemanticRole.Image ||
            _node.AccessibilityRole == MarkdownAccessibilityRole.Status)
        {
            return AutomationLiveSetting.Polite;
        }

        return base.GetLiveSettingCore();
    }

    protected override IList<AutomationPeer> GetChildrenCore()
    {
        EnsureCurrentDocument();
        var children = _root.GetChildPeersForSemanticNode(_node);
        if (_node.HostedElementBox?.RealizedElement is { } element)
        {
            var elementPeer = FrameworkElementAutomationPeer.FromElement(element)
                              ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
            if (elementPeer is not null)
                children.Add(elementPeer);
        }

        if (_node.Role == MarkdownSemanticRole.CodeBlock &&
            _node.Box is CodeBlockBox { HasCopyButtonAction: true } codeBlock)
        {
            _codeBlockCopyPeer ??= new MarkdownCodeBlockCopyPeer(_owner, _root, codeBlock, _lifetime);
            children.Add(_codeBlockCopyPeer);
        }

        return children;
    }

    protected override object GetPatternCore(PatternInterface patternIinterface)
    {
        EnsureCurrentDocument();
        if (_node.Role == MarkdownSemanticRole.Table &&
            (patternIinterface == PatternInterface.Grid || patternIinterface == PatternInterface.Table))
        {
            return this;
        }

        if (_node.Role == MarkdownSemanticRole.TableCell &&
            (patternIinterface == PatternInterface.GridItem || patternIinterface == PatternInterface.TableItem))
        {
            return this;
        }

        if (patternIinterface == PatternInterface.Scroll &&
            HorizontalOverflow is { CanScrollHorizontally: true })
        {
            return this;
        }

        if (patternIinterface == PatternInterface.Invoke &&
            (IsVectorSemanticInvokable || LinkedInlineVectorScene is not null))
        {
            return this;
        }

        if (patternIinterface == PatternInterface.Toggle && TaskMetadata is { CanToggle: true })
            return this;

        return base.GetPatternCore(patternIinterface);
    }

    protected override Windows.Foundation.Rect GetBoundingRectangleCore()
    {
        EnsureCurrentDocument();
        MarkdownSemanticNode node = _node;
        MarkdownSemanticDocument document = _lifetime.GetCurrentSemanticDocument();
        if (!_root.TryReadNodeBounds(
                node,
                document,
                _lifetime.DocumentGeneration,
                out Windows.Foundation.Rect rect))
        {
            return default;
        }

        // A node without layout geometry (for example an image whose block
        // was rebuilt offscreen and is not measured yet) has no screen rect.
        // Never report the whole renderer's rectangle for it: that claimed the
        // node was on screen while it was far above the viewport.
        // An empty rect makes IsOffscreen true, as UIA expects.
        if (rect.Width <= 0 || rect.Height <= 0)
            return default;
        return _root.GetScreenRectForDocumentRect(rect);
    }

    protected override bool IsOffscreenCore()
    {
        EnsureCurrentDocument();
        return _root.IsScreenRectOffscreen(GetBoundingRectangleCore());
    }

    int IGridProvider.ColumnCount
    {
        get { EnsureCurrentDocument(); return _node.ColumnCount; }
    }

    int IGridProvider.RowCount
    {
        get { EnsureCurrentDocument(); return _node.RowCount; }
    }

    IRawElementProviderSimple IGridProvider.GetItem(int row, int column)
    {
        EnsureCurrentDocument();
        return MarkdownNodeTableNavigation.GetItem(_root, _node, row, column);
    }

    IRawElementProviderSimple[] ITableProvider.GetColumnHeaders()
    {
        EnsureCurrentDocument();
        return MarkdownNodeTableNavigation.GetColumnHeaders(_root, _node);
    }

    IRawElementProviderSimple[] ITableProvider.GetRowHeaders()
    {
        EnsureCurrentDocument();
        return System.Array.Empty<IRawElementProviderSimple>();
    }

    RowOrColumnMajor ITableProvider.RowOrColumnMajor
    {
        get { EnsureCurrentDocument(); return RowOrColumnMajor.RowMajor; }
    }

    int IGridItemProvider.Column
    {
        get { EnsureCurrentDocument(); return _node.Column; }
    }

    int IGridItemProvider.ColumnSpan
    {
        get { EnsureCurrentDocument(); return _node.ColumnSpan; }
    }

    int IGridItemProvider.Row
    {
        get { EnsureCurrentDocument(); return _node.Row; }
    }

    int IGridItemProvider.RowSpan
    {
        get { EnsureCurrentDocument(); return _node.RowSpan; }
    }

    IRawElementProviderSimple IGridItemProvider.ContainingGrid
    {
        get
        {
            EnsureCurrentDocument();
            return MarkdownNodeTableNavigation.GetContainingGrid(_root, _node);
        }
    }

    IRawElementProviderSimple[] ITableItemProvider.GetColumnHeaderItems()
    {
        EnsureCurrentDocument();
        return MarkdownNodeTableNavigation.GetColumnHeaderItems(_root, _node);
    }

    IRawElementProviderSimple[] ITableItemProvider.GetRowHeaderItems()
    {
        EnsureCurrentDocument();
        return System.Array.Empty<IRawElementProviderSimple>();
    }

    bool IScrollProvider.HorizontallyScrollable
    {
        get { EnsureCurrentDocument(); return HorizontalOverflow is { CanScrollHorizontally: true }; }
    }

    double IScrollProvider.HorizontalScrollPercent
    {
        get
        {
            EnsureCurrentDocument();
            var overflow = HorizontalOverflow;
            if (overflow is null || !overflow.CanScrollHorizontally)
                return -1;

            return MarkdownHorizontalScrollPolicy.GetPhysicalPercent(
                overflow.HorizontalOffset,
                overflow.HorizontalExtent,
                overflow.HorizontalViewport,
                overflow.IsRightToLeft);
        }
    }

    double IScrollProvider.HorizontalViewSize
    {
        get
        {
            EnsureCurrentDocument();
            var overflow = HorizontalOverflow;
            if (overflow is null || overflow.HorizontalExtent <= 0)
                return 100;
            return System.Math.Clamp(overflow.HorizontalViewport * 100.0 / overflow.HorizontalExtent, 0, 100);
        }
    }

    bool IScrollProvider.VerticallyScrollable
    {
        get { EnsureCurrentDocument(); return false; }
    }

    double IScrollProvider.VerticalScrollPercent
    {
        get { EnsureCurrentDocument(); return -1; }
    }

    double IScrollProvider.VerticalViewSize
    {
        get { EnsureCurrentDocument(); return 100; }
    }

    void IScrollProvider.Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount)
    {
        EnsureCurrentDocument();
        if (verticalAmount != ScrollAmount.NoAmount)
            throw new System.InvalidOperationException("This markdown block scrolls horizontally only.");
        if (horizontalAmount == ScrollAmount.NoAmount)
            return;

        var overflow = HorizontalOverflow;
        if (overflow is null || !overflow.CanScrollHorizontally)
            throw new System.InvalidOperationException("The markdown block has no horizontal overflow.");
        _owner.AutomationHost.ScrollHorizontalOverflow(overflow, horizontalAmount);
    }

    void IScrollProvider.SetScrollPercent(double horizontalPercent, double verticalPercent)
    {
        EnsureCurrentDocument();
        if (verticalPercent != -1)
            throw new System.ArgumentOutOfRangeException(nameof(verticalPercent));
        if (horizontalPercent == -1)
            return;
        if (horizontalPercent < 0 || horizontalPercent > 100 || !double.IsFinite(horizontalPercent))
            throw new System.ArgumentOutOfRangeException(nameof(horizontalPercent));

        var overflow = HorizontalOverflow;
        if (overflow is null || !overflow.CanScrollHorizontally)
            throw new System.InvalidOperationException("The markdown block has no horizontal overflow.");
        _owner.AutomationHost.SetHorizontalOverflowPercent(overflow, horizontalPercent);
    }

    private bool IsVectorSemanticNode =>
        IsCurrentDocument() && _node.VectorSceneBox is not null && _node.VectorSemanticIndex >= 0;

    private InlineVectorSceneRun? LinkedInlineVectorScene =>
        IsCurrentDocument() && _node.InlineRun is InlineVectorSceneRun { IsLinked: true } inlineScene
            ? inlineScene
            : null;

    private bool IsVectorSemanticInvokable
    {
        get
        {
            if (!IsCurrentDocument())
                return false;
            if (!IsVectorSemanticNode || _node.VectorSceneBox is not { } vector)
                return false;
            return MarkdownVectorSemanticPolicy.IsInvokable(
                _node.VectorSemanticFlags,
                vector.TryGetLink(_node.VectorSemanticIndex, out _));
        }
    }

    private IHorizontalOverflowBox? HorizontalOverflow =>
        IsCurrentDocument() && _node.VectorSemanticIndex < 0 ? _node.Box as IHorizontalOverflowBox : null;
    private InlineEmbedAutomationMetadata? TaskMetadata =>
        IsCurrentDocument() ? (_node.InlineRun as InlineEmbedRun)?.AutomationMetadata : null;

    internal void NotifyImageStatusChanged()
    {
        // Image completions are asynchronous and may race snapshot replacement.
        // A stale peer must not raise UIA events for a retired image.
        if (!_lifetime.TryGetCurrentPayload(_nodeReference, out MarkdownSemanticNode? node) || node is null)
            return;

        if (node.ImageBox is not { } image)
            return;

        MarkdownImageAccessibilityState current = image.AccessibilityState;
        MarkdownImageAccessibilityState previous = _lastImageState ?? current;
        if (previous == current)
            return;

        string text = _root.GetSemanticDocument().GetText(node);
        string oldName = MarkdownNodeAutomationText.GetImageName(_owner, text, previous);
        string newName = MarkdownNodeAutomationText.GetImageName(_owner, text, current);
        string oldStatus = previous == MarkdownImageAccessibilityState.Loading ? oldName : string.Empty;
        string newStatus = current == MarkdownImageAccessibilityState.Loading ? newName : string.Empty;
        _lastImageState = current;
        RaisePropertyChangedEvent(AutomationElementIdentifiers.NameProperty, oldName, newName);
        RaisePropertyChangedEvent(AutomationElementIdentifiers.ItemStatusProperty, oldStatus, newStatus);
        RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    internal void NotifyVectorSceneStatusMayHaveChanged()
    {
        if (_lifetime.TryGetCurrentPayload(_nodeReference, out MarkdownSemanticNode? node) &&
            node?.VectorSceneBox is not null)
            InvalidatePeer();
    }

    internal void RaiseAutomationFocusChanged()
    {
        if (IsCurrentDocument())
            RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
    }

    internal void NotifyHorizontalScrollChanged(
        double oldPhysicalPercent,
        double newPhysicalPercent)
    {
        if (!IsCurrentDocument())
            return;

        if (HorizontalOverflow is null ||
            System.Math.Abs(newPhysicalPercent - oldPhysicalPercent) <= 0.0001)
        {
            return;
        }

        RaisePropertyChangedEvent(
            ScrollPatternIdentifiers.HorizontalScrollPercentProperty,
            oldPhysicalPercent,
            newPhysicalPercent);
    }

    void IInvokeProvider.Invoke()
    {
        EnsureCurrentDocument();
        if (LinkedInlineVectorScene is { } inlineScene)
        {
            _owner.AutomationHost.ActivateLinkedAtomic(inlineScene);
            return;
        }

        if (!IsVectorSemanticInvokable || _node.VectorSceneBox is null)
            throw new System.InvalidOperationException("This semantic item is not invokable.");
        _owner.AutomationHost.ActivateVectorSceneLink(_node.VectorSceneBox, _node.VectorSemanticIndex);
    }

    ToggleState IToggleProvider.ToggleState
    {
        get
        {
            EnsureCurrentDocument();
            return TaskMetadata?.IsChecked == true ? ToggleState.On : ToggleState.Off;
        }
    }

    void IToggleProvider.Toggle()
    {
        EnsureCurrentDocument();
        InlineEmbedAutomationMetadata task = TaskMetadata ??
            throw new System.InvalidOperationException("This semantic item is not a task state.");
        bool previousState = task.IsChecked;
        string previousName = task.CurrentName;
        if (!task.TrySetState(!previousState))
            throw new System.InvalidOperationException("The task state cannot be changed.");

        RaisePropertyChangedEvent(
            TogglePatternIdentifiers.ToggleStateProperty,
            previousState ? ToggleState.On : ToggleState.Off,
            task.IsChecked ? ToggleState.On : ToggleState.Off);
        RaisePropertyChangedEvent(
            AutomationElementIdentifiers.NameProperty,
            previousName,
            task.CurrentName);
    }

    private bool IsCurrentDocument() => _lifetime.IsCurrent();

    private void EnsureCurrentDocument() => _lifetime.EnsureCurrent();

    private MarkdownSemanticNode _node => MarkdownAutomationPeerLifetime.GetPayloadOrThrow(_nodeReference);
}
