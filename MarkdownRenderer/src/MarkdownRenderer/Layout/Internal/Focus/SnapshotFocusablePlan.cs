using System;
using System.Collections.Generic;
using System.Threading;
using MarkdownRenderer.Layout.Internal.Inlines.Runs;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Layout.Internal.Focus;

/// <summary>
/// Keyboard focus plan collected during snapshot construction. Plans without
/// horizontally scrollable entries publish one fixed item array; dynamic
/// entries are filtered against the committed lazy measurement mask.
/// </summary>
internal sealed class SnapshotFocusablePlan
{
    private readonly LayoutSnapshotGeometry _geometry;
    private readonly GeometryRecoveryLatch _recovery;
    private readonly SnapshotRetirement _retirement;
    private readonly FocusablePlanEntry[] _focusablePlan;
    private readonly FocusableItem[]? _fixedFocusableItems;

    internal IReadOnlyList<FocusablePlanEntry> Entries => _focusablePlan;

    internal FocusableItem[]? FixedItems => _fixedFocusableItems;

    internal SnapshotFocusablePlan(
        LayoutSnapshotGeometry geometry,
        GeometryRecoveryLatch recovery,
        SnapshotRetirement retirement,
        FocusablePlanEntry[] focusablePlan)
    {
        _geometry = geometry;
        _recovery = recovery;
        _retirement = retirement;
        _focusablePlan = focusablePlan;        bool hasDynamicFocusable = false;
        for (int index = 0; index < _focusablePlan.Length; index++)
        {
            if (_focusablePlan[index].HorizontalOverflow is not null)
            {
                hasDynamicFocusable = true;
                break;
            }
        }
        if (hasDynamicFocusable)
        {
            // Dynamic overflow entries are filtered against the committed
            // viewport at query time, so a fixed copy would be immediately
            // discarded. Avoid that full-plan allocation for mixed documents.
            _fixedFocusableItems = null;
        }
        else
        {
            var fixedFocusableItems = new FocusableItem[_focusablePlan.Length];
            for (int index = 0; index < _focusablePlan.Length; index++)
                fixedFocusableItems[index] = _focusablePlan[index].Item;
            _fixedFocusableItems = fixedFocusableItems;
        }
    }

    /// <summary>
    /// Returns the cached document-order focus plan, filtered by current
    /// horizontal-overflow eligibility. Used for Tab/Shift+Tab navigation.
    /// </summary>
    internal IReadOnlyList<FocusableItem> CollectFocusableItems()
    {
        if (_recovery.IsRequired)
            return Array.Empty<FocusableItem>();

        if (_fixedFocusableItems is not null)
            return _fixedFocusableItems;

        var list = new List<FocusableItem>(_focusablePlan.Length);
        lock (_geometry.Gate)
        {
            if (_recovery.IsRequired)
                return Array.Empty<FocusableItem>();
            CollectFocusableItemsNoLock(list);
        }
        return list;
    }

    /// <summary>
    /// Collects the current focus plan only while the caller owns a snapshot
    /// read lease. The returned list contains value-only block/run identities;
    /// no box or snapshot reference escapes this method.
    /// </summary>
    internal bool TryCollectFocusableItemsUnderReadLease(
        out IReadOnlyList<FocusableItem> items)
    {
        if (!Monitor.IsEntered(_geometry.Gate))
            throw new InvalidOperationException("A snapshot read lease is required.");

        items = Array.Empty<FocusableItem>();
        if (_recovery.IsRequired || _retirement.IsRetiringOrDisposed)
            return false;

        if (_fixedFocusableItems is { } fixedItems)
        {
            items = fixedItems;
            return true;
        }

        var list = new List<FocusableItem>(_focusablePlan.Length);
        CollectFocusableItemsNoLock(list);
        items = list;
        return true;
    }

    private void CollectFocusableItemsNoLock(List<FocusableItem> list)
    {
        foreach (FocusablePlanEntry entry in _focusablePlan)
        {
            if (entry.HorizontalOverflow is not { } overflow)
            {
                list.Add(entry.Item);
                continue;
            }

            if (_geometry.LazyLayoutEnabled &&
                _geometry.MeasuredTopLevelBlocks is not null &&
                _geometry.TopLevelOrdinalByBlockIndex.TryGetValue(entry.Item.BlockIndex, out int ordinal) &&
                !_geometry.MeasuredTopLevelBlocks[ordinal])
            {
                continue;
            }

            if (overflow.CanScrollHorizontally)
                list.Add(entry.Item);
        }
    }

    internal static bool IsInlineEmbedKeyboardFocusable(InlineEmbedRun embed)
    {
        ArgumentNullException.ThrowIfNull(embed);
        // Task markers are represented by inline embeds even when their host
        // deliberately exposes read-only document semantics. Do not add those
        // inert objects to Tab order; custom embeds without task metadata keep
        // the legacy assumption that their realized element is interactive.
        return embed.AutomationMetadata?.CanToggle != false;
    }
}
