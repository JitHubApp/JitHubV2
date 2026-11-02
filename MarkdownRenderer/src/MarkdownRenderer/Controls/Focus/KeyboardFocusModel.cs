using System;
using System.Collections.Generic;
using MarkdownRenderer.Controls.Input;
using MarkdownRenderer.Document;
using MarkdownRenderer.Layout;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// The renderer's virtual keyboard focus: the focusable items of the
/// published layout in document order, which of them is focused, where Tab
/// resumes after a pointer press, and a Tab waiting for its target's block
/// to be measured. Also answers which link, vector semantic or horizontal
/// overflow region the focused item is.
/// </summary>
internal sealed class KeyboardFocusModel
{
    private readonly PublishedLayout _published;

    internal KeyboardFocusModel(PublishedLayout published) => _published = published;

    /// <summary>The focusable items of the published layout, or null before the first one.</summary>
    internal IReadOnlyList<FocusableItem>? Items { get; private set; }

    /// <summary>The focused item's index in <see cref="Items"/>, or -1.</summary>
    internal int FocusedIndex { get; private set; } = -1;

    /// <summary>The index Tab resumes from after a pointer press, or -1.</summary>
    internal int ResumeIndex { get; private set; } = -1;

    /// <summary>A Tab waiting for its target's top-level block to be measured.</summary>
    internal PendingLazyFocus? PendingLazy { get; private set; }

    /// <summary>The focused item, or null when nothing is focused.</summary>
    internal FocusableItem? Current
    {
        get
        {
            var items = Items;
            return items is not null &&
                   FocusedIndex >= 0 &&
                   FocusedIndex < items.Count
                ? items[FocusedIndex]
                : null;
        }
    }

    internal void SetFocusedIndex(int index) => FocusedIndex = index;

    internal void SetResumeIndex(int index) => ResumeIndex = index;

    internal void ClearResumeIndex() => ResumeIndex = -1;

    internal void BeginLazyFocus(PendingLazyFocus pending) => PendingLazy = pending;

    internal void ClearLazyFocus() => PendingLazy = null;

    /// <summary>Focuses item <paramref name="index"/>, dropping a waiting Tab and the resume point.</summary>
    internal void FocusItem(int index)
    {
        PendingLazy = null;
        FocusedIndex = index;
        ResumeIndex = -1;
    }

    /// <summary>Unfocuses, dropping a waiting Tab. Keeps the resume point.</summary>
    internal void ClearFocus()
    {
        PendingLazy = null;
        FocusedIndex = -1;
    }

    /// <summary>Forgets the items, the focus, the resume point and a waiting Tab.</summary>
    internal void Reset()
    {
        Items = Array.Empty<FocusableItem>();
        FocusedIndex = -1;
        ResumeIndex = -1;
        PendingLazy = null;
    }

    /// <summary>
    /// Replaces the items, keeping focus on the same item when it is still
    /// present. Keeps the resume index.
    /// </summary>
    internal void ReplaceItems(IReadOnlyList<FocusableItem> items)
    {
        FocusableItem? focusedIdentity = Current;
        Items = items;
        FocusedIndex = focusedIdentity is { } identity
            ? FocusableItemQueries.FindFocusableItemIndex(items, identity)
            : -1;
    }

    /// <summary>
    /// Replaces the items, refocusing <paramref name="focusedIdentity"/> and
    /// resuming from <paramref name="resumeIdentity"/> when they are present.
    /// </summary>
    internal void ReplaceItems(
        IReadOnlyList<FocusableItem>? items,
        FocusableItem? focusedIdentity,
        FocusableItem? resumeIdentity)
    {
        Items = items;
        FocusedIndex = focusedIdentity is { } identity
            ? FocusableItemQueries.FindFocusableItemIndex(items, identity)
            : -1;
        ResumeIndex = resumeIdentity is { } resume
            ? FocusableItemQueries.FindFocusableItemIndex(items, resume)
            : -1;
    }

    internal bool TryGetFocusedLink(out InlineContainerBox inline, out LinkRun link)
    {
        inline = null!;
        link = null!;
        if (!TryGetFocusedLinkedAtomic(out inline, out InlineRun run) || run is not LinkRun focusedLink)
            return false;
        link = focusedLink;
        return true;
    }

    internal bool TryGetFocusedLinkedAtomic(out InlineContainerBox inline, out InlineRun run)
    {
        inline = null!;
        run = null!;
        IReadOnlyList<FocusableItem>? items = Items;
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (items is null || snapshot is null ||
            FocusedIndex < 0 || FocusedIndex >= items.Count)
            return false;

        return FocusableItemQueries.TryGetLinkedAtomicForFocusable(snapshot, items[FocusedIndex], out inline, out run);
    }

    internal bool TryGetFocusedVectorSemantic(out VectorSceneBox box, out int semanticIndex)
    {
        box = null!;
        semanticIndex = -1;
        var items = Items;
        if (items is null || FocusedIndex < 0 || FocusedIndex >= items.Count)
            return false;

        return TryGetVectorSemantic(items[FocusedIndex], out box, out semanticIndex);
    }

    internal bool TryGetFocusedHorizontalOverflow(out IHorizontalOverflowBox overflow)
    {
        overflow = null!;
        var items = Items;
        if (items is null ||
            _published.Snapshot is null ||
            FocusedIndex < 0 ||
            FocusedIndex >= items.Count ||
            !items[FocusedIndex].IsHorizontalOverflow ||
            !_published.Snapshot.TryGetHorizontalOverflowForBlockIndex(
                items[FocusedIndex].BlockIndex,
                out IHorizontalOverflowBox? focusedOverflow) ||
            focusedOverflow is null)
        {
            return false;
        }

        overflow = focusedOverflow;
        return true;
    }

    /// <summary>
    /// Finds the item for <paramref name="run"/>, or for the first link
    /// equivalent to it when the run itself is not an item.
    /// </summary>
    internal bool TryGetIndexForLinkedAtomic(InlineRun run, out int index)
    {
        index = -1;
        IReadOnlyList<FocusableItem>? items = Items;
        if (items is null || _published.Snapshot is null)
            return false;

        int equivalentLinkIndex = -1;
        for (int itemIndex = 0; itemIndex < items.Count; itemIndex++)
        {
            if (!items[itemIndex].IsLink ||
                !FocusableItemQueries.TryGetLinkedAtomicForFocusable(_published.Snapshot, items[itemIndex], out _, out InlineRun candidate))
            {
                continue;
            }

            if (ReferenceEquals(candidate, run))
            {
                index = itemIndex;
                return true;
            }

            if (run is LinkRun requestedLink &&
                candidate is LinkRun candidateLink &&
                equivalentLinkIndex < 0 &&
                LinkTargets.AreEquivalentLinks(candidateLink, requestedLink))
            {
                equivalentLinkIndex = itemIndex;
            }
        }

        index = equivalentLinkIndex;
        return index >= 0;
    }

    internal bool TryGetIndexForVectorSemantic(VectorSceneBox box, int semanticIndex, out int index)
    {
        index = -1;
        var items = Items;
        if (items is null)
            return false;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].IsVectorSemantic &&
                items[i].BlockIndex == box.BlockIndex &&
                items[i].InlineIndex == semanticIndex &&
                TryGetVectorSemantic(items[i], out var candidate, out _) &&
                ReferenceEquals(candidate, box))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    internal bool TryGetIndexForHorizontalOverflow(IHorizontalOverflowBox overflow, out int index)
    {
        var items = Items;
        var snapshot = _published.Snapshot;
        if (items is null || snapshot is null)
        {
            index = -1;
            return false;
        }

        return FocusableItemQueries.TryFindHorizontalOverflowFocusableIndex(snapshot, items, overflow, out index);
    }

    /// <summary>Finds the vector scene and semantic item that <paramref name="item"/> names.</summary>
    internal bool TryGetVectorSemantic(FocusableItem item, out VectorSceneBox vector, out int semanticIndex)
    {
        vector = null!;
        semanticIndex = -1;
        if (!item.IsVectorSemantic || _published.Snapshot is null)
            return false;

        foreach (BlockBox block in _published.Snapshot.Blocks)
        {
            if (FocusableItemQueries.TryGetVectorSemanticForFocusable(block, item, out vector, out semanticIndex))
                return true;
        }

        return false;
    }
}
