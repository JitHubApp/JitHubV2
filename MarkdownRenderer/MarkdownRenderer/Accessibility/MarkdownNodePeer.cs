using System.Collections.Generic;
using System;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Extensions;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Accessibility;

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
        if (TaskMetadata is { } task)
            return task.CanToggle ? "MarkdownTaskCheckBox" : "MarkdownTaskState";

        if (_node.HostedElementBox is not null)
        {
            return _node.AccessibilityRole switch
            {
                MarkdownAccessibilityRole.Text => "MarkdownHostedText",
                MarkdownAccessibilityRole.Group => "MarkdownHostedGroup",
                MarkdownAccessibilityRole.Paragraph => "MarkdownHostedParagraph",
                MarkdownAccessibilityRole.Heading => "MarkdownHostedHeading",
                MarkdownAccessibilityRole.Link => "MarkdownHostedLink",
                MarkdownAccessibilityRole.Image => "MarkdownHostedImage",
                MarkdownAccessibilityRole.Code => "MarkdownHostedCode",
                MarkdownAccessibilityRole.List => "MarkdownHostedList",
                MarkdownAccessibilityRole.ListItem => "MarkdownHostedListItem",
                MarkdownAccessibilityRole.Table => "MarkdownHostedTable",
                MarkdownAccessibilityRole.Row => "MarkdownHostedRow",
                MarkdownAccessibilityRole.Cell => "MarkdownHostedCell",
                MarkdownAccessibilityRole.Math => "MarkdownHostedMath",
                MarkdownAccessibilityRole.Diagram => "MarkdownHostedDiagram",
                MarkdownAccessibilityRole.Status => "MarkdownHostedStatus",
                _ => "MarkdownHostedElement",
            };
        }

        if (_node.VectorSceneBox is not null && _node.VectorSemanticIndex >= 0)
        {
            return _node.Role == MarkdownSemanticRole.Link
                ? "MarkdownDiagramLink"
                : _node.VectorSemanticRole switch
                {
                    MarkdownVectorSemanticRole.Node => "MarkdownDiagramNode",
                    MarkdownVectorSemanticRole.Edge => "MarkdownDiagramEdge",
                    MarkdownVectorSemanticRole.Label => "MarkdownDiagramLabel",
                    MarkdownVectorSemanticRole.Legend => "MarkdownDiagramLegend",
                    _ => "MarkdownDiagramGroup",
                };
        }

        return _node.Role switch
        {
            MarkdownSemanticRole.Heading => "MarkdownHeading",
            MarkdownSemanticRole.CodeBlock => "MarkdownCodeBlock",
            MarkdownSemanticRole.List => "MarkdownList",
            MarkdownSemanticRole.ListItem => "MarkdownListItem",
            MarkdownSemanticRole.Table => "MarkdownTable",
            MarkdownSemanticRole.TableCell => "MarkdownTableCell",
            MarkdownSemanticRole.Image => "MarkdownImage",
            MarkdownSemanticRole.Math => "MarkdownMath",
            MarkdownSemanticRole.Diagram => "MarkdownDiagram",
            MarkdownSemanticRole.Embed => "MarkdownEmbed",
            MarkdownSemanticRole.Abbreviation => "MarkdownAbbreviation",
            _ => "MarkdownGroup",
        };
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
            return _owner.IsHorizontalOverflowKeyboardFocusable(overflow);
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
            return _owner.IsKeyboardFocusOnVectorSemantic(vector, _node.VectorSemanticIndex);
        }

        if (LinkedInlineVectorScene is { } inlineScene)
            return _owner.IsKeyboardFocusOnLinkedInlineVectorScene(inlineScene);

        if (HorizontalOverflow is { } overflow)
            return _owner.IsKeyboardFocusOnHorizontalOverflow(overflow);

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
                _owner.FocusVectorSemanticFromAutomation(vector, _node.VectorSemanticIndex);
            }
            return;
        }

        if (LinkedInlineVectorScene is { } inlineScene)
        {
            _owner.FocusInlineVectorSceneFromAutomation(inlineScene);
            return;
        }

        if (HorizontalOverflow is { } overflow &&
            _owner.IsHorizontalOverflowKeyboardFocusable(overflow))
        {
            _owner.FocusHorizontalOverflowFromAutomation(overflow);
            return;
        }

        // Structural peers cannot take focus. Delegating to the base peer would
        // focus the shared renderer owner and make this child falsely report it.
    }

    protected override AutomationControlType GetAutomationControlTypeCore()
    {
        EnsureCurrentDocument();
        if (TaskMetadata is { } task)
            return task.CanToggle ? AutomationControlType.CheckBox : AutomationControlType.Text;

        if (_node.HostedElementBox is not null)
        {
            return _node.AccessibilityRole switch
            {
                MarkdownAccessibilityRole.Text or MarkdownAccessibilityRole.Paragraph or
                MarkdownAccessibilityRole.Status => AutomationControlType.Text,
                MarkdownAccessibilityRole.Heading => AutomationControlType.Header,
                MarkdownAccessibilityRole.Link => AutomationControlType.Hyperlink,
                MarkdownAccessibilityRole.Image or MarkdownAccessibilityRole.Math or
                MarkdownAccessibilityRole.Diagram => AutomationControlType.Image,
                MarkdownAccessibilityRole.List => AutomationControlType.List,
                MarkdownAccessibilityRole.ListItem => AutomationControlType.ListItem,
                MarkdownAccessibilityRole.Table => AutomationControlType.Table,
                MarkdownAccessibilityRole.Cell => AutomationControlType.DataItem,
                MarkdownAccessibilityRole.Row or MarkdownAccessibilityRole.Group => AutomationControlType.Group,
                _ => AutomationControlType.Custom,
            };
        }

        if (LinkedInlineVectorScene is not null)
            return AutomationControlType.Hyperlink;

        if (_node.VectorSceneBox is not null && _node.VectorSemanticIndex >= 0)
        {
            if (_node.Role == MarkdownSemanticRole.Link)
                return AutomationControlType.Hyperlink;
            return _node.VectorSemanticRole == MarkdownVectorSemanticRole.Label
                ? AutomationControlType.Text
                : AutomationControlType.Group;
        }

        return _node.Role switch
        {
            MarkdownSemanticRole.Heading => AutomationControlType.Header,
            MarkdownSemanticRole.CodeBlock => AutomationControlType.Group,
            MarkdownSemanticRole.List => AutomationControlType.List,
            MarkdownSemanticRole.ListItem => AutomationControlType.ListItem,
            MarkdownSemanticRole.Table => AutomationControlType.Table,
            MarkdownSemanticRole.TableCell => AutomationControlType.DataItem,
            MarkdownSemanticRole.Image => AutomationControlType.Image,
            MarkdownSemanticRole.Math => AutomationControlType.Image,
            MarkdownSemanticRole.Diagram => AutomationControlType.Image,
            MarkdownSemanticRole.Link when _node.VectorSceneBox is not null => AutomationControlType.Hyperlink,
            MarkdownSemanticRole.Embed => AutomationControlType.Custom,
            MarkdownSemanticRole.Abbreviation => AutomationControlType.Text,
            _ => AutomationControlType.Group,
        };
    }

    protected override AutomationHeadingLevel GetHeadingLevelCore()
    {
        EnsureCurrentDocument();
        return _node.HeadingLevel switch
        {
            1 => AutomationHeadingLevel.Level1,
            2 => AutomationHeadingLevel.Level2,
            3 => AutomationHeadingLevel.Level3,
            4 => AutomationHeadingLevel.Level4,
            5 => AutomationHeadingLevel.Level5,
            6 => AutomationHeadingLevel.Level6,
            _ => AutomationHeadingLevel.None,
        };
    }

    protected override string GetNameCore()
    {
        EnsureCurrentDocument();
        if (TaskMetadata is { } task)
            return task.CurrentName;

        if (!string.IsNullOrWhiteSpace(_node.AccessibilityName))
            return _node.AccessibilityName;

        if (_node.Role == MarkdownSemanticRole.Diagram)
        {
            return _owner.ResolveLocalizedString(
                MarkdownStringKeys.DiagramName,
                MarkdownLocalizedStrings.DiagramName);
        }
        if (_node.VectorSceneBox is not null && _node.VectorSemanticIndex >= 0)
            return _node.HelpText ?? string.Empty;

        if (_node.Role == MarkdownSemanticRole.CodeBlock && _node.Box is CodeBlockBox)
        {
            return _owner.ResolveLocalizedString(
                MarkdownStringKeys.StyleCodeBlock,
                MarkdownLocalizedStrings.StyleName(MarkdownElementKeys.CodeBlock));
        }

        var text = _root.GetSemanticDocument().GetText(_node);
        if (_node.Role == MarkdownSemanticRole.Image)
            return GetImageName(text, _node.ImageBox?.AccessibilityState);

        if (!string.IsNullOrWhiteSpace(text) &&
            !string.Equals(text, MarkdownRenderer.Layout.InlineEmbedRun.PlaceholderChar, System.StringComparison.Ordinal))
            return text;
        return _node.Role switch
        {
            MarkdownSemanticRole.Image => _owner.ResolveLocalizedString(
                MarkdownStringKeys.ImageName,
                MarkdownLocalizedStrings.ImageName),
            MarkdownSemanticRole.Table => _owner.ResolveLocalizedString(
                MarkdownStringKeys.TableName,
                MarkdownLocalizedStrings.TableName),
            MarkdownSemanticRole.List => _owner.ResolveLocalizedString(
                MarkdownStringKeys.ListName,
                MarkdownLocalizedStrings.ListName),
            MarkdownSemanticRole.Embed => _owner.ResolveLocalizedString(
                MarkdownStringKeys.EmbeddedContentName,
                MarkdownLocalizedStrings.EmbeddedContentName),
            _ => string.Empty,
        };
    }

    protected override string GetHelpTextCore()
    {
        EnsureCurrentDocument();
        if (TaskMetadata is { } task)
            return task.CurrentHelpText;

        if (_node.Role == MarkdownSemanticRole.CodeBlock &&
            !string.IsNullOrWhiteSpace(_node.CodeLanguage))
        {
            return _owner.ResolveFormattedLocalizedString(
                MarkdownStringKeys.CodeLanguageHelp,
                MarkdownLocalizedStrings.CodeLanguageHelpFormat,
                _node.CodeLanguage);
        }

        return _node.AccessibilityDescription ??
               (_node.Role == MarkdownSemanticRole.Image ? _node.ImageBox?.SvgDesc : null) ??
               _node.HelpText ??
               string.Empty;
    }

    protected override string GetItemStatusCore()
    {
        EnsureCurrentDocument();
        if (_node.Box is Layout.Boxes.VectorSceneBox { GraphRebindFailed: true })
            return _owner.ResolveLocalizedString(MarkdownStringKeys.EmbeddedContentUnavailable,
                MarkdownLocalizedStrings.EmbeddedContentUnavailable);
        if (_node.Role == MarkdownSemanticRole.Image &&
            _node.ImageBox?.AccessibilityState == MarkdownImageAccessibilityState.Loading)
        {
            string text = _root.GetSemanticDocument().GetText(_node);
            return GetImageName(text, MarkdownImageAccessibilityState.Loading);
        }

        return string.Empty;
    }

    protected override string GetAutomationIdCore()
    {
        EnsureCurrentDocument();
        return _node.AutomationId ?? MarkdownAutomationIdentity.ForNode(_node);
    }

    protected override int GetCultureCore()
    {
        EnsureCurrentDocument();
        return _owner.AutomationCultureLcid;
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

        if (IsVectorSemanticNode && (rect.Width <= 0 || rect.Height <= 0))
            return default;
        return rect.Width <= 0 || rect.Height <= 0
            ? base.GetBoundingRectangleCore()
            : _root.GetScreenRectForDocumentRect(rect);
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
        foreach (var child in _node.Children)
        {
            if (child.Role == MarkdownSemanticRole.TableCell &&
                child.Row == row &&
                child.Column == column &&
                _root.TryGetProviderForSemanticNode(child, out var provider))
            {
                return provider;
            }
        }

        return null!;
    }

    IRawElementProviderSimple[] ITableProvider.GetColumnHeaders()
    {
        EnsureCurrentDocument();
        var providers = new List<IRawElementProviderSimple>();
        foreach (var child in _node.Children)
        {
            if (child.Role == MarkdownSemanticRole.TableCell &&
                child.IsHeader &&
                _root.TryGetProviderForSemanticNode(child, out var provider))
            {
                providers.Add(provider);
            }
        }

        return providers.ToArray();
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
            var table = FindAncestorTable(_node);
            return table is not null && _root.TryGetProviderForSemanticNode(table, out var provider)
                ? provider
                : null!;
        }
    }

    IRawElementProviderSimple[] ITableItemProvider.GetColumnHeaderItems()
    {
        EnsureCurrentDocument();
        var table = FindAncestorTable(_node);
        if (table is null) return System.Array.Empty<IRawElementProviderSimple>();

        var providers = new List<IRawElementProviderSimple>();
        foreach (var child in table.Children)
        {
            if (child.Role == MarkdownSemanticRole.TableCell &&
                child.IsHeader &&
                child.Column == _node.Column &&
                _root.TryGetProviderForSemanticNode(child, out var provider))
            {
                providers.Add(provider);
            }
        }

        return providers.ToArray();
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
        _owner.ScrollHorizontalOverflowForAutomation(overflow, horizontalAmount);
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
        _owner.SetHorizontalOverflowPercentForAutomation(overflow, horizontalPercent);
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
        string oldName = GetImageName(text, previous);
        string newName = GetImageName(text, current);
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
            _owner.RaiseInlineVectorSceneClickFromAutomation(inlineScene);
            return;
        }

        if (!IsVectorSemanticInvokable || _node.VectorSceneBox is null)
            throw new System.InvalidOperationException("This semantic item is not invokable.");
        _owner.RaiseVectorSceneLinkClickFromAutomation(_node.VectorSceneBox, _node.VectorSemanticIndex);
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

    private static MarkdownSemanticNode? FindAncestorTable(MarkdownSemanticNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current.Role == MarkdownSemanticRole.Table) return current;
        }

        return null;
    }

    private bool IsCurrentDocument() => _lifetime.IsCurrent();

    private void EnsureCurrentDocument() => _lifetime.EnsureCurrent();

    private MarkdownSemanticNode _node => MarkdownAutomationPeerLifetime.GetPayloadOrThrow(_nodeReference);

    private string GetImageName(string text, MarkdownImageAccessibilityState? state)
    {
        string description = string.IsNullOrWhiteSpace(text)
            ? _owner.ResolveLocalizedString(MarkdownStringKeys.ImageName, MarkdownLocalizedStrings.ImageName)
            : text;
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
