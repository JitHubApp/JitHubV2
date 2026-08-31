using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace JitHub.WinUI.Performance;

internal static class DeferredFrameAction
{
    private static readonly TimeSpan FallbackDelay = TimeSpan.FromSeconds(2);

    public static void Schedule(
        FrameworkElement owner,
        Func<bool> isCurrent,
        Action action)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(isCurrent);
        ArgumentNullException.ThrowIfNull(action);

        EventHandler<object>? rendered = null;
        RoutedEventHandler? unloaded = null;
        DispatcherQueueTimer fallback = owner.DispatcherQueue.CreateTimer();
        bool completed = false;

        void Detach()
        {
            CompositionTarget.Rendered -= rendered;
            owner.Unloaded -= unloaded;
            fallback.Stop();
            fallback.Tick -= OnFallback;
        }

        void Complete()
        {
            if (completed)
            {
                return;
            }

            completed = true;
            Detach();
            if (!owner.IsLoaded || !isCurrent())
            {
                return;
            }

            if (!owner.DispatcherQueue.TryEnqueue(
                    DispatcherQueuePriority.Low,
                    () =>
                    {
                        if (owner.IsLoaded && isCurrent())
                        {
                            action();
                        }
                    }))
            {
                action();
            }
        }

        void OnFallback(DispatcherQueueTimer sender, object args) => Complete();

        rendered = (_, _) => Complete();
        unloaded = (_, _) =>
        {
            completed = true;
            Detach();
        };
        fallback.Interval = FallbackDelay;
        fallback.IsRepeating = false;
        fallback.Tick += OnFallback;
        owner.Unloaded += unloaded;
        CompositionTarget.Rendered += rendered;
        fallback.Start();
    }
}
