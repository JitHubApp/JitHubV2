using System;
using Windows.Foundation;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions.Vectors;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Layout.Internal.Tables;
using MarkdownRenderer.Layout.Internal.Vectors;

namespace MarkdownRenderer.Controls.Internal.Input.Links;

/// <summary>
/// Finds the link a document position, focusable item or vector scene point
/// resolves to, and classifies how the user activated it.
/// </summary>
internal static class LinkTargets
{
    internal static bool TryCreateLinkedAtomicTarget(InlineRun run, out LinkTarget target)
    {
        ArgumentNullException.ThrowIfNull(run);
        switch (run)
        {
            case LinkRun link:
                target = new LinkTarget(
                    link.Url,
                    link.Title,
                    link.SourceSpan,
                    link);
                return true;
            case InlineImageRun { IsLinked: true, LinkUrl: { } imageUrl } image:
                target = new LinkTarget(
                    imageUrl,
                    image.LinkTitle,
                    image.SourceSpan,
                    null);
                return true;
            case InlineVectorSceneRun { IsLinked: true, LinkUrl: { } sceneUrl } scene:
                target = new LinkTarget(
                    sceneUrl,
                    scene.LinkTitle,
                    scene.SourceSpan,
                    null);
                return true;
            default:
                target = default;
                return false;
        }
    }

    internal static bool TryGetVectorLinkAtPointInBlock(
        BlockBox box,
        Point point,
        out VectorSceneBox vector,
        out MarkdownVectorLinkAction link)
    {
        switch (box)
        {
            case VectorSceneBox candidate
                when candidate.TryGetLinkAt(point, out var candidateLink) && candidateLink is not null:
                vector = candidate;
                link = candidateLink;
                return true;
            case ListItemBox listItem:
                return TryGetVectorLinkAtPointInBlock(listItem.Marker, point, out vector, out link) ||
                       TryGetVectorLinkAtPointInBlock(listItem.Content, point, out vector, out link);
            case TableBox table:
                foreach (InlineContainerBox cell in table.GetCellBoxes())
                {
                    if (TryGetVectorLinkAtPointInBlock(cell, point, out vector, out link))
                        return true;
                }
                break;
            case StackBox stack:
                foreach (BlockBox child in stack.Children)
                {
                    if (TryGetVectorLinkAtPointInBlock(child, point, out vector, out link))
                        return true;
                }
                break;
        }

        vector = null!;
        link = null!;
        return false;
    }

    internal static bool AreEquivalentLinks(LinkRun left, LinkRun right) =>
        ReferenceEquals(left, right) ||
        (left.IsDisclosure && right.IsDisclosure &&
         string.Equals(left.DisclosureId, right.DisclosureId, StringComparison.Ordinal)) ||
        (string.Equals(left.Text, right.Text, StringComparison.Ordinal) &&
         string.Equals(left.Url, right.Url, StringComparison.Ordinal) &&
         string.Equals(left.Title, right.Title, StringComparison.Ordinal));

    internal static LinkTarget? FindLinkTargetInBlock(BlockBox box, DocumentPosition pos, Point? point)
    {
        switch (box)
        {
            case VectorSceneBox vector when vector.BlockIndex == pos.BlockIndex && point is { } vectorPoint:
                if (vector.TryGetLinkAt(vectorPoint, out var vectorLink) && vectorLink is not null)
                {
                    var semantic = vector.GetSemanticItem(vectorLink.SemanticIndex);
                    return new LinkTarget(
                        vectorLink.Target ?? string.Empty,
                        semantic?.Description,
                        semantic?.SourceSpan ?? vector.SourceRange,
                        null,
                        vectorLink.Action,
                        vectorLink.External);
                }
                return null;
            case InlineContainerBox icb when icb.BlockIndex == pos.BlockIndex:
                foreach (var r in icb.Runs)
                {
                    if (r.InlineIndex != pos.InlineIndex)
                        continue;

                    if (point is { } p && !icb.IsPointInsideRunBounds(r, p))
                        return null;

                    return TryCreateLinkedAtomicTarget(r, out LinkTarget target)
                        ? target
                        : null;
                }
                return null;
            case ListItemBox lib:
                if (FindLinkTargetInBlock(lib.Marker, pos, point) is { } lm) return lm;
                return FindLinkTargetInBlock(lib.Content, pos, point);
            case TableBox tb:
                foreach (var cell in tb.GetCellBoxes())
                {
                    if (FindLinkTargetInBlock(cell, pos, point) is { } tf) return tf;
                }
                return null;
            case StackBox sb:
                foreach (var c in sb.Children)
                {
                    if (FindLinkTargetInBlock(c, pos, point) is { } f) return f;
                }
                return null;
        }
        return null;
    }

    internal static bool IsLinkedRun(InlineRun? run)
        => run is LinkRun or InlineImageRun { IsLinked: true } or InlineVectorSceneRun { IsLinked: true };

    internal static MarkdownLinkInputKind GetLinkInputKind(Microsoft.UI.Input.PointerDeviceType deviceType)
        => deviceType switch
        {
            Microsoft.UI.Input.PointerDeviceType.Touch => MarkdownLinkInputKind.Touch,
            Microsoft.UI.Input.PointerDeviceType.Pen => MarkdownLinkInputKind.Pen,
            _ => MarkdownLinkInputKind.Mouse,
        };

    internal static MarkdownLinkDisposition GetRequestedDisposition(MarkdownInputModifiers modifiers)
    {
        if ((modifiers & MarkdownInputModifiers.Alt) != 0)
            return MarkdownLinkDisposition.External;
        if ((modifiers & MarkdownInputModifiers.Control) != 0 &&
            (modifiers & MarkdownInputModifiers.Shift) == 0)
        {
            return MarkdownLinkDisposition.BackgroundView;
        }
        if ((modifiers & MarkdownInputModifiers.Shift) != 0)
            return MarkdownLinkDisposition.NewView;
        return MarkdownLinkDisposition.CurrentView;
    }

    /// <summary>The link of the vector scene item under <paramref name="point"/>, or null.</summary>
    internal static LinkTarget? FindVectorLinkTargetAtPoint(LayoutSnapshot snapshot, Point point)
    {
        foreach (BlockBox block in snapshot.Blocks)
        {
            if (TryGetVectorLinkAtPointInBlock(block, point, out var vector, out var link))
            {
                MarkdownVectorSemanticItem? semantic = vector.GetSemanticItem(link.SemanticIndex);
                return new LinkTarget(
                    link.Target ?? string.Empty,
                    semantic?.Description,
                    semantic?.SourceSpan ?? vector.SourceRange,
                    null,
                    link.Action,
                    link.External);
            }
        }

        return null;
    }

    /// <summary>The link at <paramref name="pos"/>, optionally bounded to <paramref name="point"/>, or null.</summary>
    internal static LinkTarget? FindLinkTargetAt(
        LayoutSnapshot snapshot,
        DocumentPosition pos,
        Point? point = null)
    {
        foreach (var b in snapshot.Blocks)
        {
            if (FindLinkTargetInBlock(b, pos, point) is { } found) return found;
        }
        return null;
    }

    /// <summary>
    /// Captures what activating the link under <paramref name="point"/> would
    /// do, so the activation can run after the snapshot lease ends.
    /// </summary>
    internal static bool TryCreateActivationIntent(
        LayoutSnapshot snapshot,
        Point point,
        MarkdownLinkInputKind inputKind,
        MarkdownInputModifiers modifiers,
        out LinkActivationIntent intent)
    {
        LinkTarget? link = FindVectorLinkTargetAtPoint(snapshot, point);
        if (link is null && snapshot.HitTest(point, out var position))
            link = FindLinkTargetAt(snapshot, position, point);
        if (link is null)
        {
            intent = default;
            return false;
        }

        intent = new LinkActivationIntent(link.Value, inputKind, modifiers);
        return true;
    }
}
