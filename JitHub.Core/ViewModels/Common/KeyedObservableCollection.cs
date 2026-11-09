using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace JitHub.WinUI.ViewModels.Common;

public sealed record KeyedCollectionDiffOptions(bool RemoveMissing = true, bool Reorder = true)
{
    public static KeyedCollectionDiffOptions Default { get; } = new();

    public static KeyedCollectionDiffOptions PreserveMissing { get; } = new(RemoveMissing: false);
}

public readonly record struct KeyedCollectionDiffResult(
    int Added,
    int Removed,
    int Moved,
    int Updated,
    int Unchanged)
{
    public int Changed => Added + Removed + Moved + Updated;
}

public sealed class KeyedObservableCollection<TItem, TSnapshot> : ObservableCollection<TItem>
{
    public KeyedCollectionDiffResult ResetSnapshot(
        IEnumerable<TSnapshot> snapshots,
        Func<TSnapshot, string> snapshotKeySelector,
        Func<TSnapshot, TItem> createItem)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(snapshotKeySelector);
        ArgumentNullException.ThrowIfNull(createItem);

        List<TItem> replacement = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (TSnapshot snapshot in snapshots)
        {
            string key = snapshotKeySelector(snapshot);
            if (!string.IsNullOrWhiteSpace(key) && keys.Add(key))
            {
                replacement.Add(createItem(snapshot));
            }
        }

        CheckReentrancy();
        int removed = Count;
        Items.Clear();
        foreach (TItem item in replacement)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        return new KeyedCollectionDiffResult(replacement.Count, removed, 0, 0, 0);
    }

    public KeyedCollectionDiffResult ApplySnapshot(
        IEnumerable<TSnapshot> snapshots,
        Func<TSnapshot, string> snapshotKeySelector,
        Func<TItem, string> itemKeySelector,
        Func<TSnapshot, TItem> createItem,
        Func<TItem, TSnapshot, bool>? updateItem = null,
        KeyedCollectionDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(snapshotKeySelector);
        ArgumentNullException.ThrowIfNull(itemKeySelector);
        ArgumentNullException.ThrowIfNull(createItem);

        options ??= KeyedCollectionDiffOptions.Default;
        int added = 0;
        int removed = 0;
        int moved = 0;
        int updated = 0;
        int unchanged = 0;
        int targetIndex = 0;
        HashSet<string> targetKeys = new(StringComparer.Ordinal);
        Dictionary<string, TItem> existingItems = new(StringComparer.Ordinal);
        foreach (TItem item in this)
        {
            existingItems.TryAdd(itemKeySelector(item), item);
        }

        foreach (TSnapshot snapshot in snapshots)
        {
            string key = snapshotKeySelector(snapshot);
            if (string.IsNullOrWhiteSpace(key) || !targetKeys.Add(key))
            {
                continue;
            }

            int currentIndex;
            if (targetIndex < Count && string.Equals(itemKeySelector(this[targetIndex]), key, StringComparison.Ordinal))
            {
                currentIndex = targetIndex;
            }
            else if (existingItems.TryGetValue(key, out TItem? existingItem))
            {
                currentIndex = IndexOf(existingItem);
            }
            else
            {
                currentIndex = -1;
            }

            if (currentIndex < 0)
            {
                TItem newItem = createItem(snapshot);
                Insert(targetIndex, newItem);
                existingItems[key] = newItem;
                added++;
                targetIndex++;
                continue;
            }

            TItem item = this[currentIndex];
            if (updateItem?.Invoke(item, snapshot) == true)
            {
                updated++;
            }
            else
            {
                unchanged++;
            }

            if (options.Reorder && currentIndex != targetIndex)
            {
                Move(currentIndex, targetIndex);
                moved++;
            }

            targetIndex++;
        }

        if (options.RemoveMissing)
        {
            for (int index = Count - 1; index >= 0; index--)
            {
                string key = itemKeySelector(this[index]);
                if (!targetKeys.Contains(key))
                {
                    RemoveAt(index);
                    removed++;
                }
            }
        }

        return new KeyedCollectionDiffResult(added, removed, moved, updated, unchanged);
    }
}
