using System;

namespace JitHub.WinUI.ViewModels.Common;

public static class RepositoryActionDisplayMutation
{
    public static int CalculateOptimisticCount(int currentCount, bool desiredSelection) =>
        Math.Max(0, currentCount + (desiredSelection ? 1 : -1));

    public static void Publish(
        int count,
        bool isSelected,
        Action<int> publishCount,
        Action<bool> publishSelection,
        Action publishDerivedProperties)
    {
        ArgumentNullException.ThrowIfNull(publishCount);
        ArgumentNullException.ThrowIfNull(publishSelection);
        ArgumentNullException.ThrowIfNull(publishDerivedProperties);

        // Counts are part of the same optimistic state as the selection. Publish them first so
        // observers can never render a new Star/Watch label beside the previous count.
        publishCount(Math.Max(0, count));
        publishSelection(isSelected);
        publishDerivedProperties();
    }
}
