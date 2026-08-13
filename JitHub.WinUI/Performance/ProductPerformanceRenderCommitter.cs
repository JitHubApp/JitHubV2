using System;
using System.Diagnostics;
using JitHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Performance;

internal static class ProductPerformanceRenderCommitter
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(2);

    public static void ScheduleAfterNextFrame(
        FrameworkElement owner,
        Func<bool> isCurrent,
        Func<bool> isReady,
        Action commit,
        bool scheduleWhenDisabled = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(isReady);
        ArgumentNullException.ThrowIfNull(commit);
        if (!scheduleWhenDisabled && !ProductPerformanceReadiness.IsEnabled)
        {
            return;
        }

        EventHandler<object>? rendering = null;
        RoutedEventHandler? unloaded = null;
        Stopwatch readyTimeout = Stopwatch.StartNew();
        void Detach()
        {
            CompositionTarget.Rendered -= rendering;
            owner.Unloaded -= unloaded;
        }

        rendering = (_, _) =>
        {
            if (!owner.IsLoaded || !isCurrent())
            {
                Detach();
                return;
            }

            if (readyTimeout.Elapsed >= ReadyTimeout)
            {
                Detach();
                ProductPerformanceReadiness.CancelTraversal();
                return;
            }

            if (!isReady()) return;

            Detach();
            commit();
        };
        unloaded = (_, _) => Detach();
        owner.Unloaded += unloaded;
        CompositionTarget.Rendered += rendering;
    }
}
