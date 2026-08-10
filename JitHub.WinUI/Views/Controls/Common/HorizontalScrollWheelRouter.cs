using System;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace JitHub.WinUI.Views.Controls.Common;

public static class HorizontalScrollWheelRouter
{
    private static readonly DependencyProperty IsAttachedProperty = DependencyProperty.RegisterAttached(
        "IsAttached",
        typeof(bool),
        typeof(HorizontalScrollWheelRouter),
        new PropertyMetadata(false));

    private static readonly DependencyProperty AllowedHorizontalOffsetProperty = DependencyProperty.RegisterAttached(
        "AllowedHorizontalOffset",
        typeof(double),
        typeof(HorizontalScrollWheelRouter),
        new PropertyMetadata(0d));

    public static void Attach(ScrollViewer scrollViewer)
    {
        if ((bool)scrollViewer.GetValue(IsAttachedProperty))
        {
            return;
        }

        scrollViewer.SetValue(IsAttachedProperty, true);
        scrollViewer.SetValue(AllowedHorizontalOffsetProperty, scrollViewer.HorizontalOffset);
        scrollViewer.IsHorizontalScrollChainingEnabled = true;
        scrollViewer.IsVerticalScrollChainingEnabled = true;
        scrollViewer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnPointerWheelChanged), true);
    }

    private static void OnPointerWheelChanged(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        int wheelDelta = args.GetCurrentPoint(scrollViewer).Properties.MouseWheelDelta;
        if (wheelDelta == 0)
        {
            return;
        }

        if (IsModifierHorizontalGesture())
        {
            ScrollHorizontally(scrollViewer, wheelDelta);
            args.Handled = true;
            return;
        }

        RestoreHorizontalOffset(scrollViewer);
        TryScrollNearestVerticalHost(scrollViewer, wheelDelta);
        args.Handled = true;
    }

    public static bool IsExplicitHorizontalGesture(PointerRoutedEventArgs args, UIElement relativeTo)
        => IsModifierHorizontalGesture();

    public static bool IsModifierHorizontalGesture()
        => IsKeyDown(VirtualKey.Shift) || IsKeyDown(VirtualKey.Control);

    private static bool IsKeyDown(VirtualKey key)
    {
        CoreVirtualKeyStates state = InputKeyboardSource.GetKeyStateForCurrentThread(key);
        return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
    }

    private static void ScrollHorizontally(ScrollViewer scrollViewer, int wheelDelta)
    {
        double current = (double)scrollViewer.GetValue(AllowedHorizontalOffsetProperty);
        double target = Math.Clamp(
            current - wheelDelta,
            0,
            Math.Max(0, scrollViewer.ScrollableWidth));

        scrollViewer.ChangeView(target, null, null, disableAnimation: true);
        scrollViewer.SetValue(AllowedHorizontalOffsetProperty, target);
    }

    private static void RestoreHorizontalOffset(ScrollViewer scrollViewer)
    {
        double target = (double)scrollViewer.GetValue(AllowedHorizontalOffsetProperty);
        RestoreHorizontalOffsetCore(scrollViewer, target);
        _ = scrollViewer.DispatcherQueue.TryEnqueue(() =>
        {
            RestoreHorizontalOffsetCore(scrollViewer, target);
            _ = scrollViewer.DispatcherQueue.TryEnqueue(() => RestoreHorizontalOffsetCore(scrollViewer, target));
        });

        var timer = scrollViewer.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(40);
        int tickCount = 0;
        timer.Tick += (_, _) =>
        {
            RestoreHorizontalOffsetCore(scrollViewer, target);
            tickCount++;
            if (tickCount >= 2)
            {
                timer.Stop();
            }
        };
        timer.Start();
    }

    private static void RestoreHorizontalOffsetCore(ScrollViewer scrollViewer, double target)
    {
        if (Math.Abs(scrollViewer.HorizontalOffset - target) > 0.1)
        {
            scrollViewer.ChangeView(target, null, null, disableAnimation: true);
        }
    }

    private static bool TryScrollNearestVerticalHost(DependencyObject source, int wheelDelta)
    {
        DependencyObject? current = VisualTreeHelper.GetParent(source);
        while (current is not null)
        {
            if (current is ScrollViewer scrollViewer &&
                scrollViewer.VerticalScrollMode != ScrollMode.Disabled &&
                scrollViewer.ScrollableHeight > 0)
            {
                double target = Math.Clamp(
                    scrollViewer.VerticalOffset - wheelDelta,
                    0,
                    scrollViewer.ScrollableHeight);

                if (Math.Abs(target - scrollViewer.VerticalOffset) > 0.1)
                {
                    scrollViewer.ChangeView(null, target, null, disableAnimation: true);
                    return true;
                }
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
