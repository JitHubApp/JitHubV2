using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace JitHub.WinUI.ViewModels.Common;

public static class KeyedObservableReconciler
{
    public static KeyedCollectionDiffResult ApplySnapshot<T>(
        ObservableCollection<T> target,
        IReadOnlyList<T> snapshot,
        Func<T, string> keySelector,
        Func<T, T, bool> areEquivalent)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(keySelector);
        ArgumentNullException.ThrowIfNull(areEquivalent);

        int added = 0;
        int removed = 0;
        int moved = 0;
        int updated = 0;
        int unchanged = 0;
        int targetIndex = 0;
        HashSet<string> keys = new(StringComparer.Ordinal);

        foreach (T incoming in snapshot)
        {
            string key = keySelector(incoming);
            if (string.IsNullOrWhiteSpace(key) || !keys.Add(key))
            {
                continue;
            }

            int existingIndex = FindIndex(target, keySelector, key, targetIndex);
            if (existingIndex < 0)
            {
                target.Insert(targetIndex, incoming);
                added++;
            }
            else
            {
                if (existingIndex != targetIndex)
                {
                    target.Move(existingIndex, targetIndex);
                    moved++;
                }

                if (areEquivalent(target[targetIndex], incoming))
                {
                    unchanged++;
                }
                else
                {
                    target[targetIndex] = incoming;
                    updated++;
                }
            }

            targetIndex++;
        }

        while (target.Count > targetIndex)
        {
            target.RemoveAt(target.Count - 1);
            removed++;
        }

        return new KeyedCollectionDiffResult(added, removed, moved, updated, unchanged);
    }

    private static int FindIndex<T>(
        ObservableCollection<T> target,
        Func<T, string> keySelector,
        string key,
        int startIndex)
    {
        for (int index = startIndex; index < target.Count; index++)
        {
            if (string.Equals(keySelector(target[index]), key, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }
}
