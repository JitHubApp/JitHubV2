using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// The semantic document the root automation peer currently publishes, and the
/// virtual child peers built from it. A newly published layout snapshot
/// replaces the document and drops every cached peer and text format, so a
/// retired projection is never reachable from a fresh UIA query.
/// </summary>
internal sealed class MarkdownAutomationProjection
{
    private readonly MarkdownRendererControl _owner;
    private readonly MarkdownAutomationPeer _root;
    private readonly ConditionalWeakTable<InlineContainerBox, MarkdownBlockPeer> _peerCache = new();
    private Dictionary<MarkdownSemanticNode, MarkdownNodePeer> _nodePeerCache = new();
    private readonly Dictionary<VectorSceneBox, List<MarkdownNodePeer>> _vectorPeerCache = new();
    private readonly MarkdownTextFormatCacheStore _textFormatCacheStore = new();
    private LayoutSnapshot? _semanticSnapshot;
    private MarkdownSemanticDocument? _semanticDocument;

    internal MarkdownAutomationProjection(MarkdownRendererControl owner, MarkdownAutomationPeer root)
    {
        _owner = owner;
        _root = root;
    }

    internal MarkdownSemanticDocument? GetDocumentOrNull(LayoutSnapshot? snap)
    {
        if (snap is null)
        {
            if (_semanticSnapshot is not null || _semanticDocument is not null)
            {
                _semanticSnapshot = null;
                _semanticDocument = null;
                _peerCache.Clear();
                _nodePeerCache = new Dictionary<MarkdownSemanticNode, MarkdownNodePeer>();
                _vectorPeerCache.Clear();
                _owner.InvalidateAutomationChildPeerCaches();
                _textFormatCacheStore.Invalidate();
            }

            return null;
        }

        if (!ReferenceEquals(snap, _semanticSnapshot) || _semanticDocument is null)
        {
            _semanticSnapshot = snap;
            _semanticDocument = snap.SemanticDocument;
            _peerCache.Clear();
            _nodePeerCache = new Dictionary<MarkdownSemanticNode, MarkdownNodePeer>();
            _vectorPeerCache.Clear();
            _owner.InvalidateAutomationChildPeerCaches();
            _textFormatCacheStore.Invalidate();
        }

        return _semanticDocument;
    }

    internal MarkdownTextFormatCache GetTextFormatCache(MarkdownSemanticDocument document) =>
        _textFormatCacheStore.GetOrCreate(
            document,
            _owner.CurrentThemeSnapshot);

    internal void InvalidateTextFormats() => _textFormatCacheStore.Invalidate();

    internal IList<AutomationPeer> GetChildPeersForSemanticNode(MarkdownSemanticNode node)
    {
        var list = new List<AutomationPeer>();
        foreach (var child in node.Children)
        {
            if (child.Role == MarkdownSemanticRole.Link && child.VectorSceneBox is null)
                continue;

            if (child.Role is MarkdownSemanticRole.Paragraph or MarkdownSemanticRole.Heading or MarkdownSemanticRole.CodeBlock &&
                child.InlineRun is null &&
                child.InlineBox is { } inline)
            {
                list.Add(GetOrCreateBlockPeer(inline));
                continue;
            }

            list.Add(GetOrCreateNodePeer(child));
        }

        return list;
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
            node.InlineRun is null &&
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
            // A realized element in the Raw view (the read-only task marker)
            // is decorative. Exposing its peer would drop the task state from
            // the Control and Content views whenever the marker scrolls into
            // view, so the semantic node peer keeps representing it.
            if (element is not null &&
                AutomationProperties.GetAccessibilityView(element) != AccessibilityView.Raw)
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

    internal void NotifyImageStatusChanged(ImageBox image)
    {
        foreach (KeyValuePair<MarkdownSemanticNode, MarkdownNodePeer> entry in _nodePeerCache)
        {
            if (ReferenceEquals(entry.Key.ImageBox, image))
                entry.Value.NotifyImageStatusChanged();
        }

        MarkdownSemanticDocument? document = GetDocumentOrNull(_owner.CurrentSnapshot);
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

    internal void NotifyVectorSceneStatusChanged(VectorSceneBox box)
    {
        if (!_vectorPeerCache.TryGetValue(box, out List<MarkdownNodePeer>? peers))
            return;
        // Only existing peers for this changed scene are refreshed. Neither
        // document traversal nor deferred semantic realization is necessary.
        for (int index = 0; index < peers.Count; index++)
            peers[index].NotifyVectorSceneStatusMayHaveChanged();
    }

    /// <summary>
    /// Reads mutable box geometry only while holding the committed snapshot's
    /// nonblocking interaction lease. The callback must return detached values
    /// (including fully materialized enumerations) and must not invoke UIA or
    /// other external callbacks. Screen transforms and provider publication
    /// happen after the lease has been released.
    /// </summary>
    internal bool TryReadLayoutGeometry<TState, T>(
        MarkdownSemanticDocument expectedDocument,
        long? expectedDocumentGeneration,
        TState state,
        Func<LayoutSnapshot, TState, T> read,
        out T value)
    {
        ArgumentNullException.ThrowIfNull(expectedDocument);
        ArgumentNullException.ThrowIfNull(read);

        LayoutSnapshot? snapshot = _semanticSnapshot;
        if (snapshot is null || !IsCurrentLayoutDocument(snapshot, expectedDocument, expectedDocumentGeneration))
        {
            value = default!;
            return false;
        }

        if (!snapshot.TryBeginInteraction())
        {
            value = default!;
            return false;
        }

        T candidate = default!;
        bool candidateIsCurrent = false;
        try
        {
            if (!IsCurrentLayoutDocument(snapshot, expectedDocument, expectedDocumentGeneration))
            {
                value = default!;
                return false;
            }

            candidate = read(snapshot, state);
            candidateIsCurrent = IsCurrentLayoutDocument(
                snapshot,
                expectedDocument,
                expectedDocumentGeneration);
        }
        finally
        {
            snapshot.EndInteraction();
        }

        // Snapshot publication does not need the old layout lock. Recheck once
        // after releasing it so a concurrently committed replacement cannot
        // turn a safe copied rectangle into a stale UIA result.
        if (!candidateIsCurrent ||
            !IsCurrentLayoutDocument(snapshot, expectedDocument, expectedDocumentGeneration))
        {
            value = default!;
            return false;
        }

        value = candidate;
        return true;
    }

    internal MarkdownBlockPeer GetOrCreateBlockPeer(InlineContainerBox box)
    {
        if (!_peerCache.TryGetValue(box, out var peer))
        {
            peer = new MarkdownBlockPeer(_owner, _root, box);
            _peerCache.Add(box, peer);
        }

        return peer;
    }

    internal MarkdownNodePeer GetOrCreateNodePeer(MarkdownSemanticNode node)
    {
        if (!_nodePeerCache.TryGetValue(node, out var peer))
        {
            peer = new MarkdownNodePeer(_owner, _root, node);
            _nodePeerCache[node] = peer;
            if (node.VectorSceneBox is { } vector)
            {
                if (!_vectorPeerCache.TryGetValue(vector, out List<MarkdownNodePeer>? peers))
                    _vectorPeerCache.Add(vector, peers = []);
                peers.Add(peer);
            }
        }

        return peer;
    }

    internal bool TryGetRangeForAutomationPeer(AutomationPeer peer, MarkdownSemanticDocument doc, out int start, out int end)
    {
        if (ReferenceEquals(peer, _root))
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

    internal static IEnumerable<MarkdownSemanticNode> EnumerateSemanticNodes(MarkdownSemanticNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in EnumerateSemanticNodes(child))
                yield return descendant;
        }
    }

    internal static bool ProvidersMatch(IRawElementProviderSimple provider, IRawElementProviderSimple childElement)
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

    private bool IsCurrentLayoutDocument(
        LayoutSnapshot snapshot,
        MarkdownSemanticDocument expectedDocument,
        long? expectedDocumentGeneration)
    {
        LayoutSnapshot? currentSnapshot = _owner.CurrentSnapshot;
        long currentGeneration = currentSnapshot?.AutomationDocumentGeneration ??
            _owner.CurrentAutomationDocumentGeneration;
        if (expectedDocumentGeneration is { } expectedGeneration)
            MarkdownAutomationDocumentGeneration.EnsureCurrent(expectedGeneration, currentGeneration);

        return ReferenceEquals(currentSnapshot, snapshot) &&
               ReferenceEquals(_semanticSnapshot, snapshot) &&
               ReferenceEquals(_semanticDocument, expectedDocument) &&
               ReferenceEquals(snapshot.SemanticDocument, expectedDocument);
    }

    private static bool PeerMatchesSemanticNode(AutomationPeer peer, MarkdownSemanticNode node)
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
}
