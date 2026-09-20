using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Automation peer for <see cref="MarkdownRendererControl"/>. It exposes a
/// native UIA document surface backed by the committed layout snapshot:
/// semantic child peers for structure, plus TextPattern ranges for Narrator
/// word/line navigation and highlight rectangles.
/// </summary>
internal sealed partial class MarkdownAutomationPeer : FrameworkElementAutomationPeer, ITextProvider, ITextProvider2, IScrollProvider
{
    private const double NoScroll = -1;
    private readonly MarkdownRendererControl _owner;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<InlineContainerBox, MarkdownBlockPeer> _peerCache = new();
    private readonly Dictionary<MarkdownSemanticNode, MarkdownNodePeer> _nodePeerCache = new();
    private readonly MarkdownTextFormatCacheStore _textFormatCacheStore = new();
    private LayoutSnapshot? _semanticSnapshot;
    private MarkdownSemanticDocument? _semanticDocument;

    public MarkdownAutomationPeer(MarkdownRendererControl owner) : base(owner)
    {
        _owner = owner;
    }

    internal MarkdownRendererControl OwnerControl => _owner;

    protected override string GetClassNameCore() => "MarkdownRendererControl";
    protected override string GetAutomationIdCore()
    {
        string hostId = AutomationProperties.GetAutomationId(_owner);
        return string.IsNullOrWhiteSpace(hostId) ? MarkdownAutomationIdentity.Document : hostId;
    }
    protected override int GetCultureCore() => _owner.AutomationCultureLcid;
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
    protected override bool HasKeyboardFocusCore() =>
        !_owner.HasKeyboardFocusOnVirtualChild && base.HasKeyboardFocusCore();

    protected override void SetFocusCore()
    {
        _owner.FocusDocumentFromAutomation();
    }

    public ITextRangeProvider DocumentRange
    {
        get
        {
            var doc = GetSemanticDocument();
            return new MarkdownTextRangeProvider(this, 0, doc.Text.Length);
        }
    }

    public SupportedTextSelection SupportedTextSelection => _owner.IsSelectionEnabled
        ? SupportedTextSelection.Single
        : SupportedTextSelection.None;

    protected override string GetNameCore()
    {
        string hostName = AutomationProperties.GetName(_owner);
        if (!string.IsNullOrWhiteSpace(hostName))
            return hostName;

        var doc = GetSemanticDocumentOrNull();
        if (doc is null)
        {
            return _owner.ResolveLocalizedString(
                MarkdownStringKeys.DocumentName,
                MarkdownLocalizedStrings.MarkdownDocumentName);
        }

        foreach (var node in EnumerateSemanticNodes(doc.Root))
        {
            if (node.Role != MarkdownSemanticRole.Heading)
                continue;

            var heading = doc.GetText(node);
            if (!string.IsNullOrWhiteSpace(heading))
                return heading.Length > 120 ? heading.Substring(0, 120) : heading;
        }

        return _owner.ResolveLocalizedString(
            MarkdownStringKeys.DocumentName,
            MarkdownLocalizedStrings.MarkdownDocumentName);
    }

    protected override string GetItemStatusCore()
    {
        if (!_owner.HasVisibleLoadingImagesForAutomation())
            return base.GetItemStatusCore();

        string imageName = _owner.ResolveLocalizedString(
            MarkdownStringKeys.ImageName,
            MarkdownLocalizedStrings.ImageName);
        return _owner.ResolveFormattedLocalizedString(
            MarkdownStringKeys.ImageLoading,
            MarkdownLocalizedStrings.ImageLoadingFormat,
            imageName);
    }

    protected override IList<AutomationPeer> GetChildrenCore()
    {
        var doc = GetSemanticDocumentOrNull();
        IList<AutomationPeer> children = doc is null
            ? new List<AutomationPeer>()
            : GetChildPeersForSemanticNode(doc.Root);
        _owner.AppendVisibleSelectionHandleAutomationPeers(children);
        return children;
    }

    protected override object GetPatternCore(PatternInterface patternIinterface)
    {
        if (patternIinterface == PatternInterface.Text || patternIinterface == PatternInterface.Text2) return this;
        if (patternIinterface == PatternInterface.Scroll && _owner.OwnsScrollViewport) return this;
        return base.GetPatternCore(patternIinterface);
    }

    public bool HorizontallyScrollable => false;

    public double HorizontalScrollPercent => NoScroll;

    public double HorizontalViewSize => 100;

    public bool VerticallyScrollable => TryGetVerticalScrollMetrics(out _, out _, out _);

    public double VerticalScrollPercent
    {
        get
        {
            if (!TryGetVerticalScrollMetrics(out double offset, out _, out double maximum))
                return NoScroll;

            return maximum <= 0 ? NoScroll : (offset / maximum) * 100;
        }
    }

    public double VerticalViewSize
    {
        get
        {
            if (!_owner.TryGetAutomationScrollMetrics(out _, out double viewport, out double extent) ||
                extent <= 0)
            {
                return 100;
            }

            return System.Math.Clamp((viewport / extent) * 100, 0, 100);
        }
    }

    public void Scroll(ScrollAmount horizontalAmount, ScrollAmount verticalAmount)
    {
        if (horizontalAmount != ScrollAmount.NoAmount)
            throw new InvalidOperationException("The markdown document does not scroll horizontally.");
        if (verticalAmount == ScrollAmount.NoAmount)
            return;
        if (!VerticallyScrollable)
            throw new InvalidOperationException("The markdown document has no vertical overflow.");

        _owner.ScrollFromAutomation(verticalAmount);
    }

    public void SetScrollPercent(double horizontalPercent, double verticalPercent)
    {
        ValidateScrollPercent(horizontalPercent, nameof(horizontalPercent));
        ValidateScrollPercent(verticalPercent, nameof(verticalPercent));
        if (horizontalPercent != NoScroll)
            throw new InvalidOperationException("The markdown document does not scroll horizontally.");
        if (verticalPercent == NoScroll)
            return;
        if (!VerticallyScrollable)
            throw new InvalidOperationException("The markdown document has no vertical overflow.");

        _owner.SetAutomationVerticalScrollPercent(verticalPercent);
    }

    public ITextRangeProvider[] GetSelection()
    {
        if (!_owner.IsSelectionEnabled)
            return Array.Empty<ITextRangeProvider>();

        var doc = GetSemanticDocument();
        var range = _owner.CurrentSelectionRange;
        if (!range.IsEmpty)
        {
            var normalized = range.Normalized();
            int start = doc.TextOffsetFromDocumentPosition(normalized.Start);
            int end = doc.TextOffsetFromDocumentPosition(normalized.End);
            return new ITextRangeProvider[] { new MarkdownTextRangeProvider(this, start, end) };
        }

        return new ITextRangeProvider[] { new MarkdownTextRangeProvider(this, 0, 0) };
    }

    public ITextRangeProvider[] GetVisibleRanges()
    {
        var doc = GetSemanticDocument();
        if (!_owner.TryGetVisibleDocumentRect(out var viewport))
            return new ITextRangeProvider[] { new MarkdownTextRangeProvider(this, 0, doc.Text.Length) };

        int? start = null;
        int? end = null;
        foreach (var span in doc.TextSpans)
        {
            var bounds = span.InlineBox?.Bounds ??
                         span.ImageBox?.Bounds ??
                         span.EmbedBox?.Bounds ??
                         span.HostedElementBox?.Bounds ??
                         (span.VectorSceneBox is { } vector
                             ? span.VectorSemanticIndex >= 0
                                  ? vector.GetVisibleSemanticBounds(span.VectorSemanticIndex)
                                  : vector.VisibleContentBounds
                             : default);
            if (bounds.Height <= 0 || bounds.Bottom < viewport.Top || bounds.Top > viewport.Bottom) continue;
            start = start is null ? span.TextStart : System.Math.Min(start.Value, span.TextStart);
            end = end is null ? span.TextEnd : System.Math.Max(end.Value, span.TextEnd);
        }

        return new ITextRangeProvider[] { new MarkdownTextRangeProvider(this, start ?? 0, end ?? 0) };
    }

    public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement)
    {
        var doc = GetSemanticDocument();
        if (TryPeerFromProvider(childElement, out var childPeer) &&
            TryGetRangeForAutomationPeer(childPeer, doc, out var peerStart, out var peerEnd))
        {
            return new MarkdownTextRangeProvider(this, peerStart, peerEnd);
        }

        foreach (var node in EnumerateSemanticNodes(doc.Root))
        {
            if (ReferenceEquals(node, doc.Root))
                continue;

            if (TryGetProviderForSemanticNode(node, out var provider) &&
                ProvidersMatch(provider, childElement))
            {
                return new MarkdownTextRangeProvider(this, node.TextStart, node.TextEnd);
            }
        }

        return new MarkdownTextRangeProvider(this, 0, 0);
    }

    public ITextRangeProvider RangeFromPoint(Windows.Foundation.Point screenLocation)
    {
        var doc = GetSemanticDocument();
        if (_owner.CurrentSnapshot is { } snapshot &&
            TryScreenPointToDocumentPoint(screenLocation, out var docPoint) &&
            (snapshot.TryHitTestVectorText(docPoint, out var position) ||
             snapshot.HitTest(docPoint, out position)))
        {
            int offset = doc.TextOffsetFromDocumentPosition(position);
            return new MarkdownTextRangeProvider(this, offset, offset);
        }

        return new MarkdownTextRangeProvider(this, 0, 0);
    }

    public ITextRangeProvider RangeFromAnnotation(IRawElementProviderSimple annotationElement) =>
        new MarkdownTextRangeProvider(this, 0, 0);

    public ITextRangeProvider GetCaretRange(out bool isActive)
    {
        var selection = GetSelection();
        ITextRangeProvider caretRange = selection.Length > 0
            ? selection[0]
            : new MarkdownTextRangeProvider(this, 0, 0);
        isActive = IsTextCaretActive;
        return caretRange;
    }

    internal bool IsTextCaretActive => IsCaretActive(
        _owner.FocusState,
        _owner.IsSelectionEnabled,
        _owner.HasKeyboardFocusOnVirtualChild);

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

    internal MarkdownTextFormatCache GetTextFormatCache()
    {
        MarkdownSemanticDocument document = GetSemanticDocument();
        return _textFormatCacheStore.GetOrCreate(
            document,
            _owner.CurrentThemeSnapshot);
    }

    internal IList<AutomationPeer> GetChildPeersForSemanticNode(MarkdownSemanticNode node)
    {
        var list = new List<AutomationPeer>();
        foreach (var child in node.Children)
        {
            if (child.Role == MarkdownSemanticRole.Link && child.VectorSceneBox is null)
                continue;

            if (child.Role is MarkdownSemanticRole.Paragraph or MarkdownSemanticRole.Heading or MarkdownSemanticRole.CodeBlock &&
                child.InlineBox is { } inline)
            {
                list.Add(GetOrCreateBlockPeer(inline));
                continue;
            }

            list.Add(GetOrCreateNodePeer(child));
        }

        return list;
    }

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

    internal bool TryGetPeerForSemanticNode(MarkdownSemanticNode node, out AutomationPeer peer)
    {
        // Declarative hosted content keeps a stable semantic wrapper while its
        // XAML child is virtualized in and out. The wrapper carries the
        // extension's role/name/text; a realized native peer is exposed below
        // it by MarkdownNodePeer.
        if (node.HostedElementBox is not null)
        {
            peer = GetOrCreateNodePeer(node);
            return true;
        }

        if (node.Role is MarkdownSemanticRole.Paragraph or MarkdownSemanticRole.Heading or MarkdownSemanticRole.CodeBlock &&
            node.InlineBox is { } blockInline)
        {
            peer = GetOrCreateBlockPeer(blockInline);
            return true;
        }

        if (node.Role == MarkdownSemanticRole.Link &&
            node.InlineBox is { } inline &&
            node.InlineRun is LinkRun link)
        {
            var blockPeer = GetOrCreateBlockPeer(inline);
            peer = _owner.GetOrCreateLinkPeer(blockPeer, link);
            return true;
        }

        if (node.Role == MarkdownSemanticRole.Image &&
            node.InlineBox is { } imageInline &&
            node.InlineRun is InlineImageRun { IsLinked: true } linkedImage)
        {
            var blockPeer = GetOrCreateBlockPeer(imageInline);
            peer = _owner.GetOrCreateLinkedImagePeer(blockPeer, linkedImage);
            return true;
        }

        if (node.Role == MarkdownSemanticRole.Embed)
        {
            var element = node.InlineRun is InlineEmbedRun inlineEmbed
                ? inlineEmbed.RealizedElement
                : node.EmbedBox?.RealizedElement ?? node.HostedElementBox?.RealizedElement;
            if (element is not null)
            {
                var elementPeer = FrameworkElementAutomationPeer.FromElement(element)
                                  ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
                if (elementPeer is not null)
                {
                    peer = elementPeer;
                    return true;
                }
            }
        }

        peer = GetOrCreateNodePeer(node);
        return true;
    }

    internal void RaiseFocusForLink(InlineContainerBox inline, LinkRun link)
    {
        var blockPeer = GetOrCreateBlockPeer(inline);
        var linkPeer = _owner.GetOrCreateLinkPeer(blockPeer, link);
        linkPeer.RaiseAutomationFocusChanged();
    }

    internal void RaiseFocusForLinkedImage(InlineContainerBox inline, InlineImageRun image)
    {
        var blockPeer = GetOrCreateBlockPeer(inline);
        var imagePeer = _owner.GetOrCreateLinkedImagePeer(blockPeer, image);
        imagePeer.RaiseAutomationFocusChanged();
    }

    internal void RaiseFocusForVectorSemantic(VectorSceneBox box, int semanticIndex)
    {
        MarkdownSemanticDocument document = GetSemanticDocument();
        foreach (MarkdownSemanticNode node in EnumerateSemanticNodes(document.Root))
        {
            if (ReferenceEquals(node.VectorSceneBox, box) &&
                node.VectorSemanticIndex == semanticIndex)
            {
                GetOrCreateNodePeer(node).RaiseAutomationFocusChanged();
                return;
            }
        }
    }

    internal void RaiseFocusForHorizontalOverflow(IHorizontalOverflowBox overflow)
    {
        MarkdownSemanticDocument document = GetSemanticDocument();
        MarkdownSemanticNode? node = FindHorizontalOverflowFocusNode(document, overflow);
        if (node is not null)
            GetOrCreateNodePeer(node).RaiseAutomationFocusChanged();
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
            GetOrCreateNodePeer(node).NotifyHorizontalScrollChanged(
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
        foreach (KeyValuePair<MarkdownSemanticNode, MarkdownNodePeer> entry in _nodePeerCache)
        {
            if (ReferenceEquals(entry.Key.ImageBox, image))
                entry.Value.NotifyImageStatusChanged();
        }

        MarkdownSemanticDocument? document = GetSemanticDocumentOrNull();
        if (document is null)
            return;

        foreach (MarkdownSemanticNode node in EnumerateSemanticNodes(document.Root))
        {
            if (ReferenceEquals(node.ImageBox, image) &&
                node.InlineRun is InlineImageRun { IsLinked: true } linkedRun &&
                _owner.TryGetLinkedImagePeer(linkedRun, out MarkdownLinkedImagePeer linkedPeer))
            {
                linkedPeer.NotifyImageStatusChanged();
            }
        }
    }

    internal void NotifyDocumentChanged()
    {
        _textFormatCacheStore.Invalidate();
        InvalidatePeer();
        RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
        RaiseAutomationEvent(AutomationEvents.StructureChanged);
    }

    internal void NotifySelectionChanged()
    {
        RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
    }

    internal IRawElementProviderSimple ProviderFromPeerForTextRange(AutomationPeer peer) => ProviderFromPeer(peer);

    internal Windows.Foundation.Rect GetScreenRectForDocumentRect(Windows.Foundation.Rect docRect)
    {
        var ownerScreen = GetOwnerScreenBounds();
        if (ownerScreen.Width <= 0 || ownerScreen.Height <= 0) return default;

        double scale = _owner.XamlRoot?.RasterizationScale ?? 1.0;
        return new Windows.Foundation.Rect(
            ownerScreen.X + docRect.X * scale,
            ownerScreen.Y + (_owner.CurrentContentOffsetY + docRect.Y - _owner.CurrentScrollOffsetY) * scale,
            docRect.Width * scale,
            docRect.Height * scale);
    }

    private bool TryScreenPointToDocumentPoint(
        Windows.Foundation.Point screenLocation,
        out Windows.Foundation.Point documentPoint)
    {
        var ownerScreen = GetOwnerScreenBounds();
        if (ownerScreen.Width <= 0 || ownerScreen.Height <= 0)
        {
            documentPoint = default;
            return false;
        }

        double scale = _owner.XamlRoot?.RasterizationScale ?? 1.0;
        if (scale <= 0) scale = 1.0;
        var rawPoint = new Windows.Foundation.Point(
            (screenLocation.X - ownerScreen.X) / scale,
            (screenLocation.Y - ownerScreen.Y) / scale - _owner.CurrentContentOffsetY + _owner.CurrentScrollOffsetY);
        documentPoint = GetSemanticDocument().TryCoercePointToNearestTextRect(rawPoint, out var coercedPoint)
            ? coercedPoint
            : rawPoint;
        return true;
    }

    internal Windows.Foundation.Rect GetVisibleScreenBounds()
    {
        Windows.Foundation.Rect ownerBounds = GetOwnerScreenBounds();
        Windows.Foundation.Rect clippedBounds = GetBoundingRectangleCore();
        if (ownerBounds.Width <= 0 || ownerBounds.Height <= 0 ||
            clippedBounds.Width <= 0 || clippedBounds.Height <= 0)
        {
            return default;
        }

        double left = System.Math.Max(ownerBounds.Left, clippedBounds.Left);
        double top = System.Math.Max(ownerBounds.Top, clippedBounds.Top);
        double right = System.Math.Min(ownerBounds.Right, clippedBounds.Right);
        double bottom = System.Math.Min(ownerBounds.Bottom, clippedBounds.Bottom);
        return right > left && bottom > top
            ? new Windows.Foundation.Rect(left, top, right - left, bottom - top)
            : default;
    }

    internal bool IsScreenRectOffscreen(Windows.Foundation.Rect screenRect)
    {
        Windows.Foundation.Rect viewport = GetVisibleScreenBounds();
        return screenRect.Width <= 0 ||
               screenRect.Height <= 0 ||
               viewport.Width <= 0 ||
               viewport.Height <= 0 ||
               screenRect.Right <= viewport.Left ||
               screenRect.Left >= viewport.Right ||
               screenRect.Bottom <= viewport.Top ||
               screenRect.Top >= viewport.Bottom;
    }

    private Windows.Foundation.Rect GetOwnerScreenBounds()
    {
        if (_owner.XamlRoot?.Content is FrameworkElement root)
        {
            AutomationPeer? rootPeer = FrameworkElementAutomationPeer.FromElement(root)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(root);
            Windows.Foundation.Rect rootScreen = rootPeer?.GetBoundingRectangle() ?? default;
            if (rootScreen.Width > 0 && rootScreen.Height > 0)
            {
                // A peer's own bounding rectangle is clipped by ancestor
                // ScrollViewers. Use the element transform for the unclipped
                // layout origin, anchored to the active XamlRoot's physical
                // screen bounds. This remains correct for popup XAML islands
                // while accounting for every outer scroll offset.
                GeneralTransform transform = _owner.TransformToVisual(root);
                Windows.Foundation.Point topLeft = transform.TransformPoint(
                    new Windows.Foundation.Point());
                Windows.Foundation.Point topRight = transform.TransformPoint(
                    new Windows.Foundation.Point(_owner.ActualWidth, 0));
                Windows.Foundation.Point bottomLeft = transform.TransformPoint(
                    new Windows.Foundation.Point(0, _owner.ActualHeight));
                Windows.Foundation.Point bottomRight = transform.TransformPoint(
                    new Windows.Foundation.Point(_owner.ActualWidth, _owner.ActualHeight));
                AccessibilityRect ownerBounds = AccessibilityGeometry.NormalizeTransformedBounds(
                    new AccessibilityPoint(topLeft.X, topLeft.Y),
                    new AccessibilityPoint(topRight.X, topRight.Y),
                    new AccessibilityPoint(bottomLeft.X, bottomLeft.Y),
                    new AccessibilityPoint(bottomRight.X, bottomRight.Y));
                if (ownerBounds.Width <= 0 || ownerBounds.Height <= 0)
                    return default;
                double scale = _owner.XamlRoot.RasterizationScale;
                return new Windows.Foundation.Rect(
                    rootScreen.X + (ownerBounds.X * scale),
                    rootScreen.Y + (ownerBounds.Y * scale),
                    ownerBounds.Width * scale,
                    ownerBounds.Height * scale);
            }
        }

        return GetBoundingRectangleCore();
    }

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

    private MarkdownSemanticDocument? GetSemanticDocumentOrNull()
    {
        var snap = _owner.CurrentSnapshot;
        if (snap is null) return null;

        if (!ReferenceEquals(snap, _semanticSnapshot) || _semanticDocument is null)
        {
            _semanticSnapshot = snap;
            _semanticDocument = snap.SemanticDocument;
            _nodePeerCache.Clear();
            _textFormatCacheStore.Invalidate();
        }

        return _semanticDocument;
    }

    private bool TryGetVerticalScrollMetrics(
        out double offset,
        out double viewport,
        out double maximum)
    {
        if (!_owner.TryGetAutomationScrollMetrics(out offset, out viewport, out double extent))
        {
            maximum = 0;
            return false;
        }

        maximum = System.Math.Max(0, extent - viewport);
        return maximum > 0;
    }

    private static void ValidateScrollPercent(double value, string parameterName)
    {
        if (value != NoScroll &&
            (!double.IsFinite(value) || value < 0 || value > 100))
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    private MarkdownBlockPeer GetOrCreateBlockPeer(InlineContainerBox box)
    {
        if (!_peerCache.TryGetValue(box, out var peer))
        {
            peer = new MarkdownBlockPeer(_owner, this, box);
            _peerCache.Add(box, peer);
        }

        return peer;
    }

    private MarkdownNodePeer GetOrCreateNodePeer(MarkdownSemanticNode node)
    {
        if (!_nodePeerCache.TryGetValue(node, out var peer))
        {
            peer = new MarkdownNodePeer(_owner, this, node);
            _nodePeerCache[node] = peer;
        }

        return peer;
    }

    private bool TryGetRangeForAutomationPeer(AutomationPeer peer, MarkdownSemanticDocument doc, out int start, out int end)
    {
        if (ReferenceEquals(peer, this))
        {
            start = 0;
            end = doc.Text.Length;
            return true;
        }

        foreach (var node in EnumerateSemanticNodes(doc.Root))
        {
            if (ReferenceEquals(node, doc.Root))
                continue;

            if (PeerMatchesSemanticNode(peer, node))
            {
                start = node.TextStart;
                end = node.TextEnd;
                return true;
            }
        }

        start = 0;
        end = 0;
        return false;
    }

    private bool PeerMatchesSemanticNode(AutomationPeer peer, MarkdownSemanticNode node)
    {
        return peer switch
        {
            MarkdownNodePeer nodePeer => ReferenceEquals(nodePeer.Node, node),
            MarkdownBlockPeer blockPeer => node.InlineBox is { } inline &&
                                           ReferenceEquals(blockPeer.Box, inline),
            MarkdownLinkPeer linkPeer => node.InlineRun is LinkRun link &&
                                          ReferenceEquals(linkPeer.Run, link),
            MarkdownLinkedImagePeer imagePeer => node.InlineRun is InlineImageRun image &&
                                                  ReferenceEquals(imagePeer.Run, image),
            _ => PeerMatchesHostedElement(peer, node),
        };
    }

    private static bool PeerMatchesHostedElement(AutomationPeer peer, MarkdownSemanticNode node)
    {
        if (node.Role != MarkdownSemanticRole.Embed)
            return false;

        var element = node.InlineRun is InlineEmbedRun inlineEmbed
            ? inlineEmbed.RealizedElement
            : node.EmbedBox?.RealizedElement ?? node.HostedElementBox?.RealizedElement;
        if (element is null)
            return false;

        var elementPeer = FrameworkElementAutomationPeer.FromElement(element)
                          ?? FrameworkElementAutomationPeer.CreatePeerForElement(element);
        return ReferenceEquals(peer, elementPeer);
    }

    private bool TryPeerFromProvider(IRawElementProviderSimple provider, out AutomationPeer peer)
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

    private static IEnumerable<MarkdownSemanticNode> EnumerateSemanticNodes(MarkdownSemanticNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in EnumerateSemanticNodes(child))
                yield return descendant;
        }
    }

    private static bool ProvidersMatch(IRawElementProviderSimple provider, IRawElementProviderSimple childElement)
    {
        if (ReferenceEquals(provider, childElement)) return true;
        try
        {
            if (provider.Equals(childElement) || childElement.Equals(provider))
                return true;
        }
        catch
        {
        }

        var providerUnknown = System.IntPtr.Zero;
        var childUnknown = System.IntPtr.Zero;
        try
        {
            providerUnknown = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(provider);
            childUnknown = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(childElement);
            return providerUnknown != System.IntPtr.Zero && providerUnknown == childUnknown;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (providerUnknown != System.IntPtr.Zero)
                System.Runtime.InteropServices.Marshal.Release(providerUnknown);
            if (childUnknown != System.IntPtr.Zero)
                System.Runtime.InteropServices.Marshal.Release(childUnknown);
        }
    }
}
