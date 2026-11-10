using MarkdownRenderer.Controls.Selection;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout.Internal.Snapshots;
using MarkdownRenderer.Selection;
using MarkdownRenderer.Selection.Internal;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Input;

/// <summary>
/// The selection unit a press chose (character, word or block) and the
/// word or block it first selected, which a following drag extends by the
/// same unit.
/// </summary>
internal sealed class ClickUnitSelection
{
    private readonly IPointerInteractionHost _host;
    private readonly PublishedLayout _published;
    private readonly SelectionController _selection;

    private ClickSelectionMode _clickMode;
    // When _clickMode is Word or Block, these hold the start/end of the initially
    // selected word/block so that backward drag can correctly extend to the
    // start of the word/block under the pointer rather than always to the end.
    private DocumentPosition _dragAnchorStart;
    private DocumentPosition _dragAnchorEnd;

    /// <param name="host">Embed hit-testing and the layout currency check.</param>
    /// <param name="published">The published snapshot a drag hit-tests.</param>
    /// <param name="selection">The selection the units are applied to.</param>
    internal ClickUnitSelection(
        IPointerInteractionHost host,
        PublishedLayout published,
        SelectionController selection)
    {
        _host = host;
        _published = published;
        _selection = selection;
    }

    /// <summary>The unit the current press selects by.</summary>
    internal ClickSelectionMode Mode => _clickMode;

    /// <summary>The start of the word or block the press first selected.</summary>
    internal DocumentPosition AnchorStart => _dragAnchorStart;

    /// <summary>The end of the word or block the press first selected.</summary>
    internal DocumentPosition AnchorEnd => _dragAnchorEnd;

    /// <summary>Returns to single-click, character-by-character selection.</summary>
    internal void ResetToSingle() => _clickMode = ClickSelectionMode.Single;

    /// <summary>Returns to single-click selection and forgets the first unit.</summary>
    internal void Reset()
    {
        _clickMode = ClickSelectionMode.Single;
        _dragAnchorStart = default;
        _dragAnchorEnd = default;
    }

    /// <summary>
    /// Selects the word or block containing <paramref name="pos"/> and
    /// remembers it as the unit a following drag extends from.
    /// </summary>
    internal void SelectUnit(
        LayoutSnapshot snapshot,
        DocumentPosition pos,
        ClickSelectionMode mode,
        long snapshotGeneration,
        long layoutRevision)
    {
        _clickMode = mode;
        (_dragAnchorStart, _dragAnchorEnd) = mode == ClickSelectionMode.Block
            ? ExpandSelectionToBlock(snapshot, pos, snapshotGeneration, layoutRevision)
            : ExpandSelectionToWord(snapshot, pos, snapshotGeneration, layoutRevision);
    }

    /// <summary>Extends the selection to the drag point by the current unit.</summary>
    internal void ExtendDragSelection(Point dragPoint)
    {
        // Atomic embed inclusion: when the pointer is inside an inline
        // embed rect during a drag, snap the position to either the
        // start or end of the InlineEmbedRun (whichever side the pointer
        // is closer to).  This treats the embed as a single, indivisible
        // unit of selectable content — the user can never have a
        // selection that ends *halfway through* an embedded button or
        // textbox, which matches how browsers handle <input> /
        // <textarea> inside contenteditable text.
        if (_host.TryHitTestEmbed(dragPoint, out var embedPos))
        {
            _selection.ExtendTo(embedPos);
            // No canvas Invalidate(): selection is on the XAML overlay.
        }
        else if (_published.Snapshot!.HitTestSelectionEndpoint(dragPoint, out var pos))
        {
            // For word/block click modes extend selection snapped to the
            // appropriate boundary so dragging after double/triple-click
            // produces word-by-word or block-by-block selection, matching
            // browser / native text editor behaviour.
            if (_clickMode == ClickSelectionMode.Word)
            {
                var icb = DocumentHitTesting.FindInlineContainerAt(_published.Snapshot, pos.BlockIndex);
                if (icb is not null)
                {
                    var (ws, we) = icb.GetWordBoundaries(pos);
                    // Backward drag: anchor at initial-word end, extend to current-word start.
                    // Forward drag: anchor at initial-word start, extend to current-word end.
                    if (pos.CompareTo(_dragAnchorStart) < 0)
                    { _selection.SetAnchor(_dragAnchorEnd); _selection.ExtendTo(ws); }
                    else
                    { _selection.SetAnchor(_dragAnchorStart); _selection.ExtendTo(we); }
                }
                else
                    _selection.ExtendTo(pos); // no ICB (code block/embed) — fall back to char selection
            }
            else if (_clickMode == ClickSelectionMode.Block)
            {
                var icb = DocumentHitTesting.FindInlineContainerAt(_published.Snapshot, pos.BlockIndex);
                if (icb is not null)
                {
                    var (bs, be) = icb.GetBlockBoundaries();
                    if (pos.CompareTo(_dragAnchorStart) < 0)
                    { _selection.SetAnchor(_dragAnchorEnd); _selection.ExtendTo(bs); }
                    else
                    { _selection.SetAnchor(_dragAnchorStart); _selection.ExtendTo(be); }
                }
                else
                    _selection.ExtendTo(pos); // no ICB — fall back to char selection
            }
            else
                _selection.ExtendTo(pos);
            // No canvas Invalidate(): selection is on the XAML overlay.
        }
    }

    /// <summary>
    /// Selects the word containing <paramref name="pos"/> in its inline
    /// container and returns its start and end.
    /// </summary>
    private (DocumentPosition Start, DocumentPosition End) ExpandSelectionToWord(
        LayoutSnapshot snapshot,
        DocumentPosition pos,
        long snapshotGeneration,
        long layoutRevision)
    {
        var icb = DocumentHitTesting.FindInlineContainerAt(snapshot, pos.BlockIndex);
        if (icb is null)
            return AnchorWithoutInlineContainer(snapshot, pos, snapshotGeneration, layoutRevision);
        var (start, end) = icb.GetWordBoundaries(pos);
        _selection.SetAnchor(start);
        if (!_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return (start, end);
        _selection.ExtendTo(end);
        // No canvas Invalidate(): selection is rendered on the XAML overlay only.
        return (start, end);
    }

    /// <summary>
    /// Selects the whole inline container (paragraph or heading line) that
    /// contains <paramref name="pos"/> and returns its start and end.
    /// </summary>
    private (DocumentPosition Start, DocumentPosition End) ExpandSelectionToBlock(
        LayoutSnapshot snapshot,
        DocumentPosition pos,
        long snapshotGeneration,
        long layoutRevision)
    {
        var icb = DocumentHitTesting.FindInlineContainerAt(snapshot, pos.BlockIndex);
        if (icb is null)
            return AnchorWithoutInlineContainer(snapshot, pos, snapshotGeneration, layoutRevision);
        var (start, end) = icb.GetBlockBoundaries();
        _selection.SetAnchor(start);
        if (!_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return (start, end);
        _selection.ExtendTo(end);
        // No canvas Invalidate(): selection is rendered on the XAML overlay only.
        return (start, end);
    }

    private (DocumentPosition Start, DocumentPosition End) AnchorWithoutInlineContainer(
        LayoutSnapshot snapshot,
        DocumentPosition pos,
        long snapshotGeneration,
        long layoutRevision)
    {
        // Block has no inline container (code block, embed row, etc.).
        // Set anchor at the click position so subsequent drag ExtendTo calls
        // always have a valid anchor; this makes the selection empty (start==end)
        // while allowing drag to extend from this point.
        _selection.SetAnchor(pos);
        if (!_host.IsCurrentScrollTarget(snapshot, snapshotGeneration, layoutRevision))
            return (pos, pos);
        // No canvas Invalidate(): selection is rendered on the XAML overlay only.
        return (pos, pos);
    }
}
