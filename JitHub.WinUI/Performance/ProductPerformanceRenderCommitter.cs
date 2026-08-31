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
        EventHandler<object>? renderingWakeUp = null;
        RoutedEventHandler? unloaded = null;
        Stopwatch readyTimeout = Stopwatch.StartNew();
        void Detach()
        {
            CompositionTarget.Rendered -= rendering;
            CompositionTarget.Rendering -= renderingWakeUp;
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
        renderingWakeUp = static (_, _) =>
            ProductPerformanceReadiness.RecordTraversalStage("render.frame_started");
        unloaded = (_, _) => Detach();
        owner.Unloaded += unloaded;
        CompositionTarget.Rendering += renderingWakeUp;
        CompositionTarget.Rendered += rendering;
    }
}
