using System;
using System.Runtime.InteropServices;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace JitHub.WinUI.Helpers;

internal static class MorphTransitionSafety
{
    public static bool TryResetVisibilityState(
        TransitionHelper transition,
        FrameworkElement source,
        FrameworkElement target,
        bool toInitialState)
    {
        if (!HasLiveVisualTree(source, target))
        {
            return false;
        }

        try
        {
            transition.Reset(toInitialState);
            return TrySetStableState(
                transition,
                source,
                target,
                isTargetState: !toInitialState);
        }
        catch (Exception ex) when (IsVisualTreeRace(ex))
        {
            return TrySetStableState(
                transition,
                source,
                target,
                isTargetState: !toInitialState);
        }
    }

    public static bool TryReset(
        TransitionHelper transition,
        FrameworkElement source,
        FrameworkElement target,
        bool toInitialState)
    {
        if (!HasLiveVisualTree(source, target))
        {
            return false;
        }

        try
        {
            transition.Reset(toInitialState);
            return TrySetStableState(
                transition,
                source,
                target,
                isTargetState: !toInitialState);
        }
        catch (Exception ex) when (IsVisualTreeRace(ex))
        {
            return false;
        }
    }

    public static bool TrySetStableState(
        TransitionHelper transition,
        FrameworkElement source,
        FrameworkElement target,
        bool isTargetState)
    {
        try
        {
            SetStableSurfaceState(
                source,
                transition.SourceToggleMethod,
                isVisible: !isTargetState);
            SetStableSurfaceState(
                target,
                transition.TargetToggleMethod,
                isVisible: isTargetState);
            return true;
        }
        catch (Exception ex) when (IsVisualTreeRace(ex))
        {
            return false;
        }
    }

    public static void TryStop(TransitionHelper transition)
    {
        try
        {
            transition.Stop();
        }
        catch (Exception ex) when (IsVisualTreeRace(ex))
        {
        }
    }

    private static bool HasLiveVisualTree(FrameworkElement source, FrameworkElement target)
    {
        try
        {
            return source.IsLoaded &&
                target.IsLoaded &&
                source.XamlRoot is not null &&
                target.XamlRoot is not null;
        }
        catch (Exception ex) when (IsVisualTreeRace(ex))
        {
            return false;
        }
    }

    private static void SetStableSurfaceState(
        FrameworkElement surface,
        VisualStateToggleMethod toggleMethod,
        bool isVisible)
    {
        surface.Opacity = 1;
        if (toggleMethod == VisualStateToggleMethod.ByVisibility)
        {
            surface.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            ElementCompositionPreview.GetElementVisual(surface).IsVisible = true;
        }
        else
        {
            surface.Visibility = Visibility.Visible;
            ElementCompositionPreview.GetElementVisual(surface).IsVisible = isVisible;
        }

        surface.IsHitTestVisible = isVisible;
    }

    private static bool IsVisualTreeRace(Exception exception) =>
        exception is COMException or InvalidOperationException or ArgumentException;
}
