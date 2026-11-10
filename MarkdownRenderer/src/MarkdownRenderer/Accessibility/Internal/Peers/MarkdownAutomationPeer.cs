using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Accessibility.Internal.TextRanges;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Hosting.Internal;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;
using MarkdownRenderer.Performance;

namespace MarkdownRenderer.Accessibility.Internal.Peers;

/// <summary>
/// Automation peer for <see cref="MarkdownRendererControl"/>. It exposes a
/// native UIA document surface backed by the committed layout snapshot:
/// semantic child peers for structure, plus TextPattern ranges for Narrator
/// word/line navigation and highlight rectangles. The published projection
/// and its child peers live in <see cref="MarkdownAutomationProjection"/>, and
/// coordinate conversion in <see cref="MarkdownAutomationScreenGeometry"/>.
/// The class is partial because it implements WinRT interfaces and crosses
/// the WinRT ABI: the CsWinRT source generator emits its vtable, and reports
/// CsWinRT1028 when the class is not partial.
/// </summary>
internal sealed partial class MarkdownAutomationPeer : FrameworkElementAutomationPeer, ITextProvider, ITextProvider2, IScrollProvider, IMarkdownAutomationClipSource
{
    private readonly MarkdownRendererControl _owner;
    private readonly MarkdownAutomationProjection _projection;
    private readonly MarkdownAutomationScreenGeometry _screen;
    private readonly MarkdownAutomationTextPattern _text;
    private readonly MarkdownAutomationScrollPattern _scroll;

    public MarkdownAutomationPeer(MarkdownRendererControl owner) : base(owner)
    {
        _owner = owner;
        _projection = new MarkdownAutomationProjection(owner, this);
        _screen = new MarkdownAutomationScreenGeometry(owner, this);
        _text = new MarkdownAutomationTextPattern(owner, this, _projection, _screen);
        _scroll = new MarkdownAutomationScrollPattern(owner);
    }

    internal MarkdownRendererControl OwnerControl => _owner;

    protected override string GetClassNameCore() => "MarkdownRendererControl";
    protected override string GetAutomationIdCore()
    {
        string hostId = AutomationProperties.GetAutomationId(_owner);
        return string.IsNullOrWhiteSpace(hostId) ? MarkdownAutomationIdentity.Document : hostId;
    }
    protected override int GetCultureCore() => _owner.AutomationHost.CultureLcid;
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
    protected override bool HasKeyboardFocusCore() =>
        !_owner.AutomationHost.HasKeyboardFocusOnVirtualChild && base.HasKeyboardFocusCore();

    protected override void SetFocusCore()
    {
        _owner.AutomationHost.FocusDocument();
    }

    public ITextRangeProvider DocumentRange => _text.DocumentRange;

    public SupportedTextSelection SupportedTextSelection => _text.SupportedTextSelection;

    protected override string GetNameCore()
    {
        string hostName = AutomationProperties.GetName(_owner);
        if (!string.IsNullOrWhiteSpace(hostName))
            return hostName;

        var doc = GetSemanticDocumentOrNull();
        if (doc is null)
        {
            return _owner.Localization.Resolve(
                MarkdownStringKeys.DocumentName,
                MarkdownLocalizedStrings.MarkdownDocumentName);
        }

        foreach (var node in MarkdownAutomationProjection.EnumerateSemanticNodes(doc.Root))
        {
            if (node.Role != MarkdownSemanticRole.Heading)
                continue;

            var heading = doc.GetText(node);
            if (!string.IsNullOrWhiteSpace(heading))
                return heading.Length > 120 ? heading.Substring(0, 120) : heading;
        }

        return _owner.Localization.Resolve(
            MarkdownStringKeys.DocumentName,
            MarkdownLocalizedStrings.MarkdownDocumentName);
    }

    protected override string GetItemStatusCore()
    {
        MarkdownVisibleSceneReadiness sceneStatus = _owner.DeferredSceneReadiness.GetVisibleStatus();
        if (sceneStatus == MarkdownVisibleSceneReadiness.Failed)
            return _owner.Localization.Resolve(MarkdownStringKeys.EmbeddedContentUnavailable,
                MarkdownLocalizedStrings.EmbeddedContentUnavailable);
        if (_owner.ImageReadiness.HasLoadingImages())
        {
            string imageName = _owner.Localization.Resolve(
                MarkdownStringKeys.ImageName,
                MarkdownLocalizedStrings.ImageName);
            return _owner.Localization.ResolveFormatted(
                MarkdownStringKeys.ImageLoading,
                MarkdownLocalizedStrings.ImageLoadingFormat,
                imageName);
        }

        if (sceneStatus != MarkdownVisibleSceneReadiness.Ready)
        {
            string sceneName = _owner.Localization.Resolve(
                MarkdownStringKeys.EmbeddedContentName,
                MarkdownLocalizedStrings.EmbeddedContentName);
            return _owner.Localization.ResolveFormatted(
                MarkdownStringKeys.ImageLoading,
                MarkdownLocalizedStrings.ImageLoadingFormat,
                sceneName);
        }

        return base.GetItemStatusCore();
    }

    protected override IList<AutomationPeer> GetChildrenCore()
    {
        var doc = GetSemanticDocumentOrNull();
        IList<AutomationPeer> children = doc is null
            ? new List<AutomationPeer>()
            : GetChildPeersForSemanticNode(doc.Root);
        _owner.AutomationHost.AppendVisibleSelectionHandlePeers(children);
        return children;
    }

    protected override object GetPatternCore(PatternInterface patternIinterface)
    {
        if (patternIinterface == PatternInterface.Text || patternIinterface == PatternInterface.Text2) return this;
        if (patternIinterface == PatternInterface.Scroll && _owner.OwnsScrollViewport) return this;
        return base.GetPatternCore(patternIinterface);
    }

    public bool HorizontallyScrollable => _scroll.HorizontallyScrollable;

    public double HorizontalScrollPercent => _scroll.HorizontalScrollPercent;

    public double HorizontalViewSize => _scroll.HorizontalViewSize;

    public bool VerticallyScrollable => _scroll.VerticallyScrollable;

    public double VerticalScrollPercent => _scroll.VerticalScrollPercent;

    public double VerticalViewSize => _scroll.VerticalViewSize;

    public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount) =>
        _scroll.Scroll(horizontalAmount, verticalAmount);

    public void SetScrollPercent(double horizontalPercent, double verticalPercent) =>
        _scroll.SetScrollPercent(horizontalPercent, verticalPercent);

    public ITextRangeProvider[] GetSelection() => _text.GetSelection();

    public ITextRangeProvider[] GetVisibleRanges() => _text.GetVisibleRanges();

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) =>
        _text.RangeFromChild(childElement);

    public ITextRangeProvider RangeFromPoint(Windows.Foundation.Point screenLocation) =>
        _text.RangeFromPoint(screenLocation);

    public ITextRangeProvider RangeFromAnnotation(IRawElementProviderSimple annotationElement) =>
        _text.RangeFromAnnotation(annotationElement);

    public ITextRangeProvider GetCaretRange(out bool isActive) => _text.GetCaretRange(out isActive);

    internal bool IsTextCaretActive => IsCaretActive(
        _owner.FocusState,
        _owner.IsSelectionEnabled,
        _owner.AutomationHost.HasKeyboardFocusOnVirtualChild);

    internal static bool IsCaretActive(
        FocusState focusState,
        bool selectionEnabled,
        bool hasVirtualChildFocus) =>
        selectionEnabled &&
        !hasVirtualChildFocus &&
        focusState != FocusState.Unfocused;

    internal MarkdownSemanticDocument GetSemanticDocument()
    {
        var doc = GetSemanticDocumentOrNull();
        return doc ?? MarkdownSemanticDocument.Empty;
    }

    internal MarkdownSemanticDocument GetCurrentTextRangeDocument(out long generation)
    {
        LayoutSnapshot? snapshot = _owner.CurrentSnapshot;
        generation = snapshot?.AutomationDocumentGeneration ?? _owner.AutomationHost.DocumentGeneration;
        return GetSemanticDocumentOrNull(snapshot) ?? MarkdownSemanticDocument.Empty;
    }

    internal MarkdownSemanticDocument GetTextRangeDocument(long expectedGeneration)
    {
        LayoutSnapshot? snapshot = _owner.CurrentSnapshot;
        MarkdownSemanticDocument? document = GetSemanticDocumentOrNull(snapshot);
        MarkdownAutomationDocumentGeneration.EnsureCurrent(
            expectedGeneration,
            snapshot?.AutomationDocumentGeneration ?? _owner.AutomationHost.DocumentGeneration);
        return document ?? MarkdownSemanticDocument.Empty;
    }

    internal MarkdownTextFormatCache GetTextFormatCache()
    {
        MarkdownSemanticDocument document = GetSemanticDocument();
        return _projection.GetTextFormatCache(document);
    }

    internal IList<AutomationPeer> GetChildPeersForSemanticNode(MarkdownSemanticNode node) =>
        _projection.GetChildPeersForSemanticNode(node);

    internal IList<AutomationPeer> GetInlineChildPeers(InlineContainerBox box)
    {
        var doc = GetSemanticDocumentOrNull();
        if (doc is null ||
            !doc.TryGetInlineContainerNode(box, out MarkdownSemanticNode node) ||
            node.Role is not (MarkdownSemanticRole.Paragraph or
                MarkdownSemanticRole.Heading or
                MarkdownSemanticRole.CodeBlock))
        {
            return new List<AutomationPeer>();
        }

        var peers = new List<AutomationPeer>(node.Children.Count);
        foreach (MarkdownSemanticNode child in node.Children)
        {
            if (TryGetPeerForSemanticNode(child, out AutomationPeer peer))
                peers.Add(peer);
        }

        return peers;
    }

    internal bool TryGetProviderForSemanticNode(MarkdownSemanticNode node, out IRawElementProviderSimple provider)
    {
        if (TryGetPeerForSemanticNode(node, out var peer))
        {
            provider = ProviderFromPeer(peer);
            return true;
        }

        provider = null!;
        return false;
    }

    internal bool TryGetPeerForSemanticNode(MarkdownSemanticNode node, out AutomationPeer peer) =>
        _projection.TryGetPeerForSemanticNode(node, out peer);

    internal void RaiseFocusForLink(InlineContainerBox inline, LinkRun link)
    {
        var blockPeer = _projection.GetOrCreateBlockPeer(inline);
        var linkPeer = _owner.AutomationHost.GetOrCreateLinkPeer(blockPeer, link);
        linkPeer.RaiseAutomationFocusChanged();
    }

    internal void RaiseFocusForLinkedImage(InlineContainerBox inline, InlineImageRun image)
    {
        var blockPeer = _projection.GetOrCreateBlockPeer(inline);
        var imagePeer = _owner.AutomationHost.GetOrCreateLinkedImagePeer(blockPeer, image);
        imagePeer.RaiseAutomationFocusChanged();
    }

    internal void RaiseFocusForInlineVectorScene(InlineVectorSceneRun scene)
    {
        MarkdownSemanticDocument document = GetSemanticDocument();
        foreach (MarkdownSemanticNode node in MarkdownAutomationProjection.EnumerateSemanticNodes(document.Root))
        {
            if (node.Role == MarkdownSemanticRole.Math &&
                ReferenceEquals(node.InlineRun, scene))
            {
                _projection.GetOrCreateNodePeer(node).RaiseAutomationFocusChanged();
                return;
            }
        }
    }

    internal void RaiseFocusForVectorSemantic(VectorSceneBox box, int semanticIndex)
    {
        MarkdownSemanticDocument document = GetSemanticDocument();
        foreach (MarkdownSemanticNode node in MarkdownAutomationProjection.EnumerateSemanticNodes(document.Root))
        {
            if (ReferenceEquals(node.VectorSceneBox, box) &&
                node.VectorSemanticIndex == semanticIndex)
            {
                _projection.GetOrCreateNodePeer(node).RaiseAutomationFocusChanged();
                return;
            }
        }
    }

    internal void RaiseFocusForHorizontalOverflow(IHorizontalOverflowBox overflow)
    {
        MarkdownSemanticDocument document = GetSemanticDocument();
        MarkdownSemanticNode? node = FindHorizontalOverflowFocusNode(document, overflow);
        if (node is not null)
            _projection.GetOrCreateNodePeer(node).RaiseAutomationFocusChanged();
    }

    internal void NotifyHorizontalOverflowScrolled(
        IHorizontalOverflowBox overflow,
        double oldPhysicalPercent,
        double newPhysicalPercent)
    {
        MarkdownSemanticDocument document = GetSemanticDocument();
        MarkdownSemanticNode? node = FindHorizontalOverflowFocusNode(document, overflow);
        if (node is not null)
        {
            _projection.GetOrCreateNodePeer(node).NotifyHorizontalScrollChanged(
                oldPhysicalPercent,
                newPhysicalPercent);
        }
    }

    internal static MarkdownSemanticNode? FindHorizontalOverflowFocusNode(
        MarkdownSemanticDocument document,
        IHorizontalOverflowBox overflow)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(overflow);
        return document.TryGetHorizontalOverflowNode(overflow, out MarkdownSemanticNode node)
            ? node
            : null;
    }

    internal void NotifyImageStatusChanged(ImageBox image)
    {
        NotifyLoadingStatusMayHaveChanged();
        _projection.NotifyImageStatusChanged(image);
    }

    internal void NotifyLoadingStatusMayHaveChanged()
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged))
            return;

        // InvalidatePeer recomputes the root's main UIA properties and raises
        // property-changed events only when their values changed. This refreshes
        // the aggregate ItemStatus without emitting text/structure events.
        InvalidatePeer();
    }

    internal void NotifyVectorSceneStatusChanged(VectorSceneBox box)
    {
        if (!AutomationPeer.ListenerExists(AutomationEvents.PropertyChanged))
            return;
        _projection.NotifyVectorSceneStatusChanged(box);
    }

    internal void NotifyDocumentChanged()
    {
        NotifySnapshotChanged();
        _projection.InvalidateTextFormats();
        InvalidatePeer();
        RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
        RaiseAutomationEvent(AutomationEvents.StructureChanged);
    }

    internal void NotifySnapshotChanged()
    {
        // Keep only the currently published projection even when its logical
        // text generation is stable (for example, during a width-only reflow).
        // This also releases externally unreachable old semantic trees without
        // requiring a client to make another UIA query.
        _ = GetSemanticDocumentOrNull();
    }

    internal void NotifySelectionChanged()
    {
        RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
    }

    internal IRawElementProviderSimple ProviderFromPeerForTextRange(AutomationPeer peer) => ProviderFromPeer(peer);

    internal Windows.Foundation.Rect GetScreenRectForDocumentRect(Windows.Foundation.Rect docRect) =>
        _screen.GetScreenRectForDocumentRect(docRect);

    /// <inheritdoc cref="MarkdownAutomationProjection.TryReadLayoutGeometry"/>
    internal bool TryReadLayoutGeometry<TState, T>(
        MarkdownSemanticDocument expectedDocument,
        long? expectedDocumentGeneration,
        TState state,
        Func<LayoutSnapshot, TState, T> read,
        out T value) =>
        _projection.TryReadLayoutGeometry(expectedDocument, expectedDocumentGeneration, state, read, out value);

    internal bool TryReadNodeBounds(
        MarkdownSemanticNode node,
        MarkdownSemanticDocument expectedDocument,
        long expectedDocumentGeneration,
        out Windows.Foundation.Rect bounds) =>
        TryReadLayoutGeometry(
            expectedDocument,
            expectedDocumentGeneration,
            node,
            static (snapshot, currentNode) =>
            {
                using LayoutPlacementTransformContext.Scope placementScope =
                    snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint(currentNode.LayoutBlockIndex);
                Windows.Foundation.Rect currentBounds = currentNode.VectorSceneBox is { } vector
                    ? currentNode.VectorSemanticIndex >= 0
                        ? vector.GetVisibleSemanticBounds(currentNode.VectorSemanticIndex)
                        : vector.Bounds
                    : currentNode.Bounds;
                return snapshot.ToLogicalRectForBlockIndexUnderPaint(
                    currentNode.LayoutBlockIndex, currentBounds);
            },
            out bounds);

    internal Windows.Foundation.Rect GetVisibleScreenBounds() => _screen.GetVisibleScreenBounds();

    internal bool IsScreenRectOffscreen(Windows.Foundation.Rect screenRect) =>
        _screen.IsScreenRectOffscreen(screenRect);

    internal bool TryGetTextRangeForInlineBox(InlineContainerBox box, out int start, out int end)
    {
        MarkdownSemanticDocument document = GetSemanticDocument();
        if (document.TryGetInlineContainerNode(box, out MarkdownSemanticNode node))
        {
            start = node.TextStart;
            end = node.TextEnd;
            return true;
        }

        start = 0;
        end = 0;
        return false;
    }

    private MarkdownSemanticDocument? GetSemanticDocumentOrNull() =>
        GetSemanticDocumentOrNull(_owner.CurrentSnapshot);

    private MarkdownSemanticDocument? GetSemanticDocumentOrNull(LayoutSnapshot? snap) =>
        _projection.GetDocumentOrNull(snap);

    Windows.Foundation.Rect IMarkdownAutomationClipSource.GetClippedBoundingRectangle() => GetBoundingRectangleCore();

    internal bool TryPeerFromProvider(IRawElementProviderSimple provider, out AutomationPeer peer)
    {
        try
        {
            peer = PeerFromProvider(provider);
            return peer is not null;
        }
        catch
        {
            peer = null!;
            return false;
        }
    }
}
