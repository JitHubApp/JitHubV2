using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Performance;

internal static class MarkdownDeferredSceneReadiness
{
    internal static bool HasPendingVisiblePlaceholder(
        bool hasRegisteredWork,
        bool visibleBandVerified,
        IEnumerable<string>? visiblePlaceholderKeys,
        Func<string, bool> isRegisteredMarker)
    {
        if (!hasRegisteredWork)
            return false;
        if (!visibleBandVerified || visiblePlaceholderKeys is null)
            return true;

        ArgumentNullException.ThrowIfNull(isRegisteredMarker);
        foreach (string marker in visiblePlaceholderKeys)
        {
            if (isRegisteredMarker(marker))
                return true;
        }

        return false;
    }
}
