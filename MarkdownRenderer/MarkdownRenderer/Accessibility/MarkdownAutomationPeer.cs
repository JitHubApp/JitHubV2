using System.Collections.Generic;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Automation peer for <see cref="MarkdownRendererControl"/>. It exposes a
/// native UIA document surface backed by the committed layout snapshot:
/// semantic child peers for structure, plus TextPattern ranges for Narrator
/// word/line navigation and highlight rectangles.
/// </summary>
internal sealed partial class MarkdownAutomationPeer : FrameworkElementAutomationPeer, ITextProvider, ITextProvider2
{
    private readonly MarkdownRendererControl _owner;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<InlineContainerBox, MarkdownBlockPeer> _peerCache = new();
    private readonly Dictionary<MarkdownSemanticNode, MarkdownNodePeer> _nodePeerCache = new();
    private LayoutSnapshot? _semanticSnapshot;
    private MarkdownSemanticDocument? _semanticDocument;

    public MarkdownAutomationPeer(MarkdownRendererControl owner) : base(owner)
    {
        _owner = owner;
    }

    internal MarkdownRendererControl OwnerControl => _owner;

    protected override string GetClassNameCore() => "MarkdownRendererControl";
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    protected override bool IsControlElementCore() => true;
    protected override bool IsContentElementCore() => true;
    protected override bool HasKeyboardFocusCore() =>
        !_owner.HasKeyboardFocusOnPaintedLink && base.HasKeyboardFocusCore();

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

    public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;

    protected override string GetNameCore()
    {
        var doc = GetSemanticDocumentOrNull();
        if (doc is null) return MarkdownLocalizedStrings.MarkdownDocumentName;

        foreach (var node in EnumerateSemanticNodes(doc.Root))
        {
            if (node.Role != MarkdownSemanticRole.Heading)
                continue;

            var heading = doc.GetText(node);
            if (!string.IsNullOrWhiteSpace(heading))
                return heading.Length > 120 ? heading.Substring(0, 120) : heading;
        }

        return MarkdownLocalizedStrings.MarkdownDocumentName;
    }

    protected override IList<AutomationPeer> GetChildrenCore()
    {
        var doc = GetSemanticDocumentOrNull();
        return doc is null ? new List<AutomationPeer>() : GetChildPeersForSemanticNode(doc.Root);
    }

    protected override object GetPatternCore(PatternInterface patternIinterface)
    {
        if (patternIinterface == PatternInterface.Text || patternIinterface == PatternInterface.Text2) return this;
        return base.GetPatternCore(patternIinterface);
    }

    public ITextRangeProvider[] GetSelection()
    {
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
            var bounds = span.InlineBox?.Bounds ?? span.ImageBox?.Bounds ?? span.EmbedBox?.Bounds ?? default;
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
            snapshot.HitTest(docPoint, out var position))
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
        isActive = _owner.FocusState != Microsoft.UI.Xaml.FocusState.Unfocused;
        var selection = GetSelection();
        return selection.Length > 0
            ? selection[0]
            : new MarkdownTextRangeProvider(this, 0, 0);
    }

    internal MarkdownSemanticDocument GetSemanticDocument()
    {
        var doc = GetSemanticDocumentOrNull();
        return doc ?? MarkdownSemanticDocument.Build(new LayoutSnapshot(System.Array.Empty<BlockBox>(), new MarkdownSourceMap(string.Empty), 0, 0));
    }

    internal IList<AutomationPeer> GetChildPeersForSemanticNode(MarkdownSemanticNode node)
    {
        var list = new List<AutomationPeer>();
        foreach (var child in node.Children)
        {
            if (child.Role == MarkdownSemanticRole.Link)
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
        if (doc is null) return new List<AutomationPeer>();

        foreach (var node in EnumerateSemanticNodes(doc.Root))
        {
            if (ReferenceEquals(node.InlineBox, box) &&
                node.Role is MarkdownSemanticRole.Paragraph or MarkdownSemanticRole.Heading or MarkdownSemanticRole.CodeBlock)
            {
                var peers = new List<AutomationPeer>();
                foreach (var child in node.Children)
                {
                    if (TryGetPeerForSemanticNode(child, out var peer))
                        peers.Add(peer);
                }

                return peers;
            }
        }

        return new List<AutomationPeer>();
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

        if (node.Role == MarkdownSemanticRole.Embed)
        {
            var element = node.InlineRun is InlineEmbedRun inlineEmbed
                ? inlineEmbed.RealizedElement
                : node.EmbedBox?.RealizedElement;
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

    internal IRawElementProviderSimple ProviderFromPeerForTextRange(AutomationPeer peer) => ProviderFromPeer(peer);

    internal Windows.Foundation.Rect GetScreenRectForDocumentRect(Windows.Foundation.Rect docRect)
    {
        var ownerScreen = GetBoundingRectangleCore();
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
        var ownerScreen = GetBoundingRectangleCore();
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
        documentPoint = CoerceToNearestTextRect(GetSemanticDocument(), rawPoint);
        return true;
    }

    private static Windows.Foundation.Point CoerceToNearestTextRect(
        MarkdownSemanticDocument doc,
        Windows.Foundation.Point point)
    {
        Windows.Foundation.Rect best = default;
        double bestScore = double.PositiveInfinity;

        foreach (var span in doc.TextSpans)
        {
            foreach (var rect in doc.GetDocumentRects(span.TextStart, span.TextEnd))
            {
                if (rect.Width <= 0 || rect.Height <= 0)
                    continue;

                double x = System.Math.Clamp(point.X, rect.Left, rect.Right);
                double y = System.Math.Clamp(point.Y, rect.Top, rect.Bottom);
                double dx = point.X - x;
                double dy = point.Y - y;
                double score = dx * dx + dy * dy;
                if (score < bestScore)
                {
                    bestScore = score;
                    best = rect;
                }
            }
        }

        if (!double.IsFinite(bestScore))
            return point;

        return new Windows.Foundation.Point(
            System.Math.Clamp(point.X, best.Left, best.Right),
            System.Math.Clamp(point.Y, best.Top, best.Bottom));
    }

    internal bool TryGetTextRangeForInlineBox(InlineContainerBox box, out int start, out int end)
    {
        var doc = GetSemanticDocument();
        foreach (var span in doc.TextSpans)
        {
            if (ReferenceEquals(span.InlineBox, box))
            {
                start = span.TextStart;
                end = span.TextEnd;
                return true;
            }
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
            _semanticDocument = MarkdownSemanticDocument.Build(snap);
            _nodePeerCache.Clear();
        }

        return _semanticDocument;
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
            _ => PeerMatchesHostedElement(peer, node),
        };
    }

    private static bool PeerMatchesHostedElement(AutomationPeer peer, MarkdownSemanticNode node)
    {
        if (node.Role != MarkdownSemanticRole.Embed)
            return false;

        var element = node.InlineRun is InlineEmbedRun inlineEmbed
            ? inlineEmbed.RealizedElement
            : node.EmbedBox?.RealizedElement;
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
