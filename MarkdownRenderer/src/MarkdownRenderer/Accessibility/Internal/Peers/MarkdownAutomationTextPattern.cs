using System;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Provider;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Accessibility.Internal.TextRanges;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Accessibility.Internal.Peers;

/// <summary>
/// The document-level UIA TextPattern of <see cref="MarkdownAutomationPeer"/>:
/// the document, selection, visible, child, point and caret ranges. The root
/// peer implements <see cref="ITextProvider"/> and forwards here; ranges are
/// created against the root peer so they share its projection.
/// </summary>
internal sealed class MarkdownAutomationTextPattern
{
    private readonly MarkdownRendererControl _owner;
    private readonly MarkdownAutomationPeer _root;
    private readonly MarkdownAutomationProjection _projection;
    private readonly MarkdownAutomationScreenGeometry _screen;

    internal MarkdownAutomationTextPattern(
        MarkdownRendererControl owner,
        MarkdownAutomationPeer root,
        MarkdownAutomationProjection projection,
        MarkdownAutomationScreenGeometry screen)
    {
        _owner = owner;
        _root = root;
        _projection = projection;
        _screen = screen;
    }

    internal ITextRangeProvider DocumentRange
    {
        get
        {
            var doc = _root.GetSemanticDocument();
            return new MarkdownTextRangeProvider(_root, 0, doc.Text.Length);
        }
    }

    internal SupportedTextSelection SupportedTextSelection => _owner.IsSelectionEnabled
        ? SupportedTextSelection.Single
        : SupportedTextSelection.None;

    internal ITextRangeProvider[] GetSelection()
    {
        if (!_owner.IsSelectionEnabled)
            return Array.Empty<ITextRangeProvider>();

        var doc = _root.GetSemanticDocument();
        var range = _owner.AutomationHost.SelectionRange;
        if (!range.IsEmpty)
        {
            var normalized = range.Normalized();
            int start = doc.TextOffsetFromDocumentPosition(normalized.Start);
            int end = doc.TextOffsetFromDocumentPosition(normalized.End);
            return new ITextRangeProvider[] { new MarkdownTextRangeProvider(_root, start, end) };
        }

        return new ITextRangeProvider[] { new MarkdownTextRangeProvider(_root, 0, 0) };
    }

    internal ITextRangeProvider[] GetVisibleRanges()
    {
        var doc = _root.GetCurrentTextRangeDocument(out long generation);
        if (!_owner.AutomationHost.TryGetVisibleDocumentRect(out var viewport))
        {
            return new ITextRangeProvider[]
            {
                new MarkdownTextRangeProvider(_root, 0, doc.Text.Length, null, generation, doc.Text.Length),
            };
        }

        if (!_projection.TryReadLayoutGeometry(
                doc,
                generation,
                (Document: doc, Viewport: viewport),
                static (snapshot, state) =>
            {
                int? visibleStart = null;
                int? visibleEnd = null;
                for (int index = 0; index < state.Document.TextSpans.Count; index++)
                {
                    MarkdownTextSpan span = state.Document.TextSpans[index];
                    Windows.Foundation.Rect bounds = GetVisibleSpanBounds(span, snapshot);
                    if (bounds.Height <= 0 ||
                        bounds.Bottom < state.Viewport.Top ||
                        bounds.Top > state.Viewport.Bottom)
                        continue;

                    visibleStart = visibleStart is null
                        ? span.TextStart
                        : System.Math.Min(visibleStart.Value, span.TextStart);
                    visibleEnd = visibleEnd is null
                        ? span.TextEnd
                        : System.Math.Max(visibleEnd.Value, span.TextEnd);
                }

                return (Start: visibleStart ?? 0, End: visibleEnd ?? 0);
            },
                out var visibleRange))
        {
            return new ITextRangeProvider[]
            {
                new MarkdownTextRangeProvider(_root, 0, 0, null, generation, doc.Text.Length),
            };
        }

        return new ITextRangeProvider[]
        {
            new MarkdownTextRangeProvider(_root, visibleRange.Start, visibleRange.End, null, generation, doc.Text.Length),
        };
    }

    internal ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement)
    {
        var doc = _root.GetSemanticDocument();
        if (_root.TryPeerFromProvider(childElement, out var childPeer) &&
            _projection.TryGetRangeForAutomationPeer(childPeer, doc, out var peerStart, out var peerEnd))
        {
            return new MarkdownTextRangeProvider(_root, peerStart, peerEnd);
        }

        foreach (var node in MarkdownAutomationProjection.EnumerateSemanticNodes(doc.Root))
        {
            if (ReferenceEquals(node, doc.Root))
                continue;

            if (_root.TryGetProviderForSemanticNode(node, out var provider) &&
                MarkdownAutomationProjection.ProvidersMatch(provider, childElement))
            {
                return new MarkdownTextRangeProvider(_root, node.TextStart, node.TextEnd);
            }
        }

        return new MarkdownTextRangeProvider(_root, 0, 0);
    }

    internal ITextRangeProvider RangeFromPoint(Windows.Foundation.Point screenLocation)
    {
        var doc = _root.GetCurrentTextRangeDocument(out long generation);
        if (_screen.TryScreenPointToDocumentPoint(screenLocation, out var rawDocumentPoint) &&
            _projection.TryReadLayoutGeometry(
                doc,
                generation,
                (Document: doc, DocumentPoint: rawDocumentPoint),
                static (snapshot, state) =>
            {
                Windows.Foundation.Point hitTestPoint = state.Document.TryCoercePointToNearestTextRect(
                    state.DocumentPoint,
                    snapshot.ToLogicalRectForBlockIndexUnderPaint,
                    snapshot.EnterPlacementTransformScopeForBlockIndexUnderPaint,
                    out Windows.Foundation.Point coercedPoint)
                    ? coercedPoint
                    : state.DocumentPoint;
                bool hit = snapshot.TryHitTestVectorText(hitTestPoint, out DocumentPosition position) ||
                           snapshot.HitTest(hitTestPoint, out position);
                return (Hit: hit, Offset: hit ? state.Document.TextOffsetFromDocumentPosition(position) : 0);
            },
                out var hitTest) && hitTest.Hit)
        {
            return new MarkdownTextRangeProvider(_root, hitTest.Offset, hitTest.Offset, null, generation, doc.Text.Length);
        }

        return new MarkdownTextRangeProvider(_root, 0, 0, null, generation, doc.Text.Length);
    }

    internal ITextRangeProvider RangeFromAnnotation(IRawElementProviderSimple annotationElement) =>
        new MarkdownTextRangeProvider(_root, 0, 0);

    internal ITextRangeProvider GetCaretRange(out bool isActive)
    {
        var selection = GetSelection();
        ITextRangeProvider caretRange = selection.Length > 0
            ? selection[0]
            : new MarkdownTextRangeProvider(_root, 0, 0);
        isActive = _root.IsTextCaretActive;
        return caretRange;
    }

    private static Windows.Foundation.Rect GetVisibleSpanBounds(
        MarkdownTextSpan span,
        LayoutSnapshot snapshot)
    {
        Windows.Foundation.Rect bounds = span.InlineBox?.Bounds ??
        span.ImageBox?.Bounds ??
        span.EmbedBox?.Bounds ??
        span.HostedElementBox?.Bounds ??
        (span.VectorSceneBox is { } vector
            ? span.VectorSemanticIndex >= 0
                ? vector.GetVisibleSemanticBounds(span.VectorSemanticIndex)
                : vector.VisibleContentBounds
            : default);
        return snapshot.ToLogicalRectForBlockIndexUnderPaint(span.LayoutBlockIndex, bounds);
    }
}
