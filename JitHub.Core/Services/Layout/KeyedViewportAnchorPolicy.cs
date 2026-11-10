using System;
using System.Collections.Generic;

namespace JitHub.Services.Layout;

public static class KeyedViewportAnchorPolicy
{
    public static T? FindByKey<T>(IEnumerable<T> items, string key, Func<T, string?> keySelector)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(keySelector);

        foreach (T item in items)
        {
            if (string.Equals(keySelector(item), key, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return default;
    }

    public static double ResolveTargetVerticalOffset(
        double currentVerticalOffset,
        double currentAnchorViewportOffset,
        double capturedAnchorViewportOffset,
        double scrollableHeight)
    {
        double target = currentVerticalOffset + currentAnchorViewportOffset - capturedAnchorViewportOffset;
        return Math.Clamp(target, 0, Math.Max(0, scrollableHeight));
    }
}
