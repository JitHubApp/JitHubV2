using System.Collections.Generic;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Vectors;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Windows.Foundation;

namespace MarkdownRenderer.Accessibility.Internal.Peers;

/// <summary>
/// What the renderer's automation peers ask of the renderer: the committed
/// document's automation state, stable child peer identity, keyboard-focus
/// queries for virtual children, and the focus, activation, selection and
/// scrolling requests of automation clients.
/// </summary>
internal interface IMarkdownAutomationHost
{
    /// <summary>Effective document-language LCID.</summary>
    int CultureLcid { get; }

    /// <summary>Automation generation of the current document, used while no snapshot is committed.</summary>
    long DocumentGeneration { get; }

    /// <summary>The committed theme, so text attributes match the painted pixels.</summary>
    ThemeSnapshot? Theme { get; }

    DocumentRange SelectionRange { get; }

    /// <summary>The renderer's own vertical scroll offset; zero under an ancestor viewport.</summary>
    double ScrollOffsetY { get; }

    bool TryGetVisibleDocumentRect(out Rect rect);

    MarkdownLinkPeer GetOrCreateLinkPeer(MarkdownBlockPeer parent, LinkRun run);

    MarkdownLinkedImagePeer GetOrCreateLinkedImagePeer(MarkdownBlockPeer parent, InlineImageRun run);

    bool TryGetLinkedImagePeer(InlineImageRun run, out MarkdownLinkedImagePeer peer);

    void InvalidateChildPeerCaches();

    void AppendVisibleSelectionHandlePeers(IList<AutomationPeer> peers);

    bool HasKeyboardFocusOnVirtualChild { get; }

    bool IsKeyboardFocusOnLink(LinkRun run);

    bool IsKeyboardFocusOnLinkedImage(InlineImageRun run);

    bool IsKeyboardFocusOnLinkedInlineVectorScene(InlineVectorSceneRun run);

    bool IsKeyboardFocusOnVectorSemantic(VectorSceneBox box, int semanticIndex);

    bool IsKeyboardFocusOnHorizontalOverflow(IHorizontalOverflowBox overflow);

    bool IsHorizontalOverflowKeyboardFocusable(IHorizontalOverflowBox overflow);

    /// <summary>Focuses a link, linked image or linked inline vector scene.</summary>
    bool FocusLinkedAtomic(InlineRun run);

    bool FocusVectorSemantic(VectorSceneBox box, int semanticIndex);

    bool FocusHorizontalOverflow(IHorizontalOverflowBox overflow);

    bool FocusDocument();

    /// <summary>
    /// Activates a link, linked image or linked inline vector scene the same
    /// way pointer interaction does.
    /// </summary>
    void ActivateLinkedAtomic(InlineRun run);

    /// <summary>Activates a host-approved vector-scene link or action.</summary>
    void ActivateVectorSceneLink(VectorSceneBox box, int semanticIndex);

    void Select(DocumentRange range);

    void ClearSelection();

    void ScrollHorizontalOverflow(IHorizontalOverflowBox overflow, ScrollAmount amount);

    void SetHorizontalOverflowPercent(IHorizontalOverflowBox overflow, double percent);
}
