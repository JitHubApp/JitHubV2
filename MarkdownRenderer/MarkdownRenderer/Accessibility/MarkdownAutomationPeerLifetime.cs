using System;
using Microsoft.UI.Xaml.Automation;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Accessibility;

/// <summary>
/// Non-retaining lifetime fence for a virtual child peer. A logical text range
/// may survive a same-document reflow, but a child peer captures box/native
/// geometry from one immutable semantic snapshot and must not retarget it.
/// </summary>
internal sealed class MarkdownAutomationPeerLifetime
{
    private const string UnavailablePayloadMessage =
        "The markdown automation peer payload is no longer available.";

    private readonly MarkdownRendererControl _owner;
    private readonly long _documentGeneration;
    private readonly WeakReference<MarkdownSemanticDocument> _semanticDocument;

    internal long DocumentGeneration => _documentGeneration;

    private MarkdownAutomationPeerLifetime(
        MarkdownRendererControl owner,
        MarkdownSemanticDocument semanticDocument,
        long documentGeneration)
    {
        _owner = owner;
        _semanticDocument = new WeakReference<MarkdownSemanticDocument>(semanticDocument);
        _documentGeneration = documentGeneration;
    }

    internal static MarkdownAutomationPeerLifetime Capture(
        MarkdownRendererControl owner,
        MarkdownAutomationPeer root)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(root);
        MarkdownSemanticDocument semanticDocument = root.GetCurrentTextRangeDocument(out long generation);
        return new MarkdownAutomationPeerLifetime(owner, semanticDocument, generation);
    }

    internal bool IsCurrent()
    {
        LayoutSnapshot? snapshot = _owner.CurrentSnapshot;
        long currentGeneration = snapshot?.AutomationDocumentGeneration ??
            _owner.CurrentAutomationDocumentGeneration;
        return IsCurrentSnapshot(
            _documentGeneration,
            currentGeneration,
            _semanticDocument,
            snapshot?.SemanticDocument);
    }

    internal void EnsureCurrent()
    {
        LayoutSnapshot? snapshot = _owner.CurrentSnapshot;
        long currentGeneration = snapshot?.AutomationDocumentGeneration ??
            _owner.CurrentAutomationDocumentGeneration;
        MarkdownAutomationDocumentGeneration.EnsureCurrent(_documentGeneration, currentGeneration);
        if (!IsCurrentSnapshot(
                _documentGeneration,
                currentGeneration,
                _semanticDocument,
                snapshot?.SemanticDocument))
        {
            throw new ElementNotAvailableException(
                "The markdown automation peer belongs to a layout snapshot that is no longer available.");
        }
    }

    internal MarkdownSemanticDocument GetCurrentSemanticDocument()
    {
        EnsureCurrent();
        if (_semanticDocument.TryGetTarget(out MarkdownSemanticDocument? document) && document is not null)
            return document;

        throw new ElementNotAvailableException(
            "The markdown automation peer semantic document is no longer available.");
    }

    internal static T GetPayloadOrThrow<T>(WeakReference<T> payload) where T : class =>
        payload.TryGetTarget(out T? target) && target is not null
            ? target
            : throw new ElementNotAvailableException(UnavailablePayloadMessage);

    internal bool TryGetCurrentPayload<T>(WeakReference<T> payload, out T? target) where T : class
    {
        if (!IsCurrent())
        {
            target = null;
            return false;
        }

        return payload.TryGetTarget(out target);
    }

    internal static bool IsCurrentSnapshot(
        long expectedGeneration,
        long currentGeneration,
        WeakReference<MarkdownSemanticDocument> expectedDocument,
        MarkdownSemanticDocument? currentDocument) =>
        expectedGeneration == currentGeneration &&
        currentDocument is not null &&
        expectedDocument.TryGetTarget(out MarkdownSemanticDocument? capturedDocument) &&
        ReferenceEquals(capturedDocument, currentDocument);
}
