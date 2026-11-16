using System;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions.Internal;
using MarkdownRenderer.Extensions.Vectors;
using MarkdownRenderer.Layout.Internal.Vectors.Drawing;

namespace MarkdownRenderer.Layout.Internal.Vectors;

/// <summary>
/// Hit-tests a vector scene box against its semantic items: pointer positions,
/// text positions, links and selectable content, choosing the deepest and then
/// smallest eligible semantic under the point.
/// </summary>
internal sealed class VectorSceneSemanticHitTester
{
    private readonly VectorSceneBox _host;

    internal VectorSceneSemanticHitTester(VectorSceneBox host) => _host = host;

    internal bool HitTest(Point point, out DocumentPosition position)
    {
        if (_host.SemanticItems.Count == 0)
        {
            // An unstructured scene has no semantic child index, but remains
            // one atomic document slot. -1 is reserved for semantic metadata;
            // pointer/document positions use the same zero-based slot as the
            // source map and selection-endpoint path.
            position = new DocumentPosition(_host.BlockIndex, 0, point.X <= _host.ContentBounds.Left ? 0 : 1);
            return _host.Bounds.Contains(point);
        }

        if (TryGetSemanticAt(point, VectorSemanticHitKind.Selectable, out int semanticIndex))
        {
            Rect semanticBounds = GetSemanticBounds(semanticIndex);
            position = new DocumentPosition(
                _host.BlockIndex,
                semanticIndex,
                point.X <= semanticBounds.Left + semanticBounds.Width / 2 ? 0 : 1);
            return true;
        }

        position = DocumentPosition.Zero;
        return false;
    }

    internal Rect GetSemanticBounds(int semanticIndex)
    {
        // Semantic metadata survives command-graph retirement. Position still
        // comes from this box's current managed bounds; this is not an immutable
        // geometry snapshot or a replacement for layout read exclusion.
        if ((uint)semanticIndex >= (uint)_host.SemanticItems.Count)
            return _host.ContentBounds;
        VectorViewportTransform transform = VectorViewportTransform.Create(
            _host.SemanticViewport,
            _host.ContentBounds);
        return transform.Transform(_host.SemanticItems[semanticIndex].Bounds);
    }

    internal bool TryGetTextPositionAt(Point point, out DocumentPosition position)
    {
        if (TryGetSemanticAt(point, VectorSemanticHitKind.Text, out int semanticIndex))
        {
            Rect semanticBounds = GetSemanticBounds(semanticIndex);
            position = new DocumentPosition(
                _host.BlockIndex,
                semanticIndex,
                point.X <= semanticBounds.Left + semanticBounds.Width / 2 ? 0 : 1);
            return true;
        }

        position = DocumentPosition.Zero;
        return false;
    }

    internal bool TryGetLinkAt(Point point, out MarkdownVectorLinkAction? link)
    {
        if (TryGetSemanticAt(point, VectorSemanticHitKind.Linked, out int semanticIndex))
            return TryGetLink(semanticIndex, out link);

        link = null;
        return false;
    }

    internal bool TryGetLink(int semanticIndex, out MarkdownVectorLinkAction? link)
    {
        if ((uint)semanticIndex < (uint)_host.SemanticItems.Count)
        {
            MarkdownVectorSemanticItem semantic = _host.SemanticItems[semanticIndex];
            MarkdownVectorLinkAction? action = _host.SemanticLinks[semanticIndex];
            if (MarkdownVectorSemanticPolicy.IsInvokable(semantic.Flags, action is not null))
            {
                link = action;
                return true;
            }
        }

        link = null;
        return false;
    }

    internal bool IsSelectableAt(Point point) =>
        _host.SemanticItems.Count == 0
            ? _host.Bounds.Contains(point)
            : TryGetSemanticAt(point, VectorSemanticHitKind.Selectable, out _);

    private bool TryGetSemanticAt(
        Point point,
        VectorSemanticHitKind kind,
        out int semanticIndex)
    {
        semanticIndex = -1;
        if (!_host.HorizontalViewportBounds.Contains(point))
            return false;

        int bestDepth = -1;
        double bestArea = double.PositiveInfinity;
        ReadOnlySpan<int> semanticDepths = _host.SemanticDepths;
        for (int i = 0; i < _host.SemanticItems.Count; i++)
        {
            MarkdownVectorSemanticItem semantic = _host.SemanticItems[i];
            bool eligible = kind switch
            {
                VectorSemanticHitKind.Selectable => MarkdownVectorSemanticPolicy.IsSelectable(semantic.Flags),
                VectorSemanticHitKind.Linked => MarkdownVectorSemanticPolicy.IsInvokable(
                    semantic.Flags,
                    _host.SemanticLinks[i] is not null),
                VectorSemanticHitKind.Text => MarkdownVectorSemanticPolicy.IsExposed(semantic.Flags) &&
                                        !string.IsNullOrWhiteSpace(semantic.Name) &&
                                        !(semantic.ParentIndex < 0 &&
                                          semantic.Role == MarkdownVectorSemanticRole.Diagram),
                _ => false,
            };
            if (!eligible)
                continue;

            Rect bounds = GetSemanticBounds(i);
            if (!bounds.Contains(point))
                continue;

            int depth = semanticDepths[i];
            double area = bounds.Width * bounds.Height;
            if (depth < bestDepth || (depth == bestDepth && area > bestArea))
                continue;

            semanticIndex = i;
            bestDepth = depth;
            bestArea = area;
        }

        return semanticIndex >= 0;
    }
}
