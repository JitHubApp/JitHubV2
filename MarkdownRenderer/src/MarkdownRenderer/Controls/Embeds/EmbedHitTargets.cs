using System;
using System.Collections.Generic;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Boxes;
using MarkdownRenderer.Layout.Internal.CodeBlocks;
using MarkdownRenderer.Layout.Internal.Inlines;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Embeds;

/// <summary>
/// The rectangles of realized hosted elements and copy buttons, in document
/// coordinates and document order, rebuilt by each visible-realization pass.
/// Pointer input consults them so that:
/// <list type="bullet">
/// <item>the hosted element's own cursor and hover win over link hover and
/// the text I-beam;</item>
/// <item>a press inside a hosted element routes to it instead of starting a
/// selection;</item>
/// <item>a drag snaps to the start or end of an inline element, so it is
/// selected or not as one unit.</item>
/// </list>
/// </summary>
internal sealed class EmbedHitTargets
{
    private readonly List<(InlineContainerBox Box, InlineEmbedRun Run, Rect Rect)> _inline = new();

    // Block hosted elements, including declarative ones.
    private readonly List<(BlockBox Box, Rect Rect)> _block = new();

    private List<(CodeBlockBox Box, Rect Rect, CodeBlockHostedElementKind Kind)> _codeActions = new();

    internal void AddInline(InlineContainerBox box, InlineEmbedRun run, Rect rect) => _inline.Add((box, run, rect));

    internal void AddBlock(BlockBox box, Rect rect) => _block.Add((box, rect));

    internal void AddCodeAction(CodeBlockBox box, Rect rect, CodeBlockHostedElementKind kind) =>
        _codeActions.Add((box, rect, kind));

    /// <summary>Forgets every rectangle.</summary>
    internal void Clear()
    {
        _inline.Clear();
        _block.Clear();
        _codeActions.Clear();
    }

    /// <summary>Forgets hosted element rectangles, keeping copy buttons'.</summary>
    internal void ClearHostedElements()
    {
        _inline.Clear();
        _block.Clear();
    }

    /// <summary>Takes over the copy button rectangles a raster-candidate publication prepared.</summary>
    internal void ReplaceCodeActions(List<(CodeBlockBox Box, Rect Rect, CodeBlockHostedElementKind Kind)> rects) =>
        _codeActions = rects;

    /// <summary>Forgets the copy button rectangle of <paramref name="box"/>.</summary>
    internal void RemoveCodeAction(CodeBlockBox box) =>
        _codeActions.RemoveAll(rect => ReferenceEquals(rect.Box, box));

    /// <summary>
    /// True when the given point in canvas coordinates is inside the
    /// rectangle of any realised inline or block embed.
    /// </summary>
    internal bool IsPointOverEmbed(Point pt)
    {
        if (IsPointOverCodeBlockAction(pt))
            return true;

        for (int i = 0; i < _inline.Count; i++)
        {
            var r = _inline[i].Rect;
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
                return true;
        }
        for (int i = 0; i < _block.Count; i++)
        {
            var r = _block[i].Rect;
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
                return true;
        }
        return false;
    }

    internal bool IsPointOverCodeBlockAction(Point pt)
    {
        for (int i = 0; i < _codeActions.Count; i++)
        {
            var r = _codeActions[i].Rect;
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
                return true;
        }

        return false;
    }

    /// <summary>
    /// If the point is inside an inline embed's rectangle, returns a
    /// DocumentPosition that snaps to the start (left half) or end (right
    /// half) of the embed run — making the embed an atomic, indivisible
    /// unit of selection.  Returns false when the point is not over any
    /// embed.
    /// </summary>
    internal bool TryHitTest(Point pt, bool isRightToLeft, out DocumentPosition position)
    {
        for (int i = 0; i < _inline.Count; i++)
        {
            var (box, run, r) = _inline[i];
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
            {
                bool rightHalf = pt.X >= r.X + r.Width / 2.0;
                // In RTL flow the logical start of a run is visually on the
                // right, so the right half maps to charOffset 0 and the left
                // half maps to charOffset Text.Length. In LTR it's the
                // opposite.
                int charOffset = isRightToLeft
                    ? (rightHalf ? 0 : run.TextLength)
                    : (rightHalf ? run.TextLength : 0);
                position = new DocumentPosition(box.BlockIndex, run.InlineIndex, charOffset);
                return true;
            }
        }

        for (int i = 0; i < _block.Count; i++)
        {
            var (box, r) = _block[i];
            if (pt.X >= r.X && pt.X < r.X + r.Width &&
                pt.Y >= r.Y && pt.Y < r.Y + r.Height)
            {
                bool afterMidpoint = pt.Y >= r.Y + r.Height / 2.0;
                position = new DocumentPosition(box.BlockIndex, 0, afterMidpoint ? 1 : 0);
                return true;
            }
        }

        position = default;
        return false;
    }
}
