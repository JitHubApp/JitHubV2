using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// Moves keyboard focus among the focusable controls inside a hosted embed.
/// </summary>
internal static class HostedFocusNavigation
{
    internal static void SuppressHostedTabStops(
        DependencyObject root,
        List<(Control Control, bool IsTabStop)> suppressed,
        HashSet<Control> seen)
    {
        if (root is Control control && seen.Add(control))
        {
            suppressed.Add((control, control.IsTabStop));
            control.IsTabStop = false;
        }

        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < childCount; i++)
        {
            SuppressHostedTabStops(VisualTreeHelper.GetChild(root, i), suppressed, seen);
        }
    }

    internal static bool TryMoveFocusWithinHostedEmbed(FrameworkElement host, object originalSource, bool reverse)
    {
        var focusables = CollectHostedFocusableControls(host);
        if (focusables.Count <= 1)
            return false;

        var current = FindCurrentHostedFocusable(host, originalSource, focusables);
        if (current is null)
            return false;

        int index = focusables.IndexOf(current);
        if (index < 0)
            return false;

        int next = reverse ? index - 1 : index + 1;
        if (next < 0 || next >= focusables.Count)
            return false;

        return focusables[next].Focus(FocusState.Keyboard);
    }

    internal static bool TryFocusHostedDescendant(FrameworkElement host, bool reverse)
    {
        var focusables = CollectHostedFocusableControls(host);
        if (focusables.Count == 0)
            return false;

        var target = reverse ? focusables[^1] : focusables[0];
        return target.Focus(FocusState.Keyboard);
    }

    internal static Control? FindCurrentHostedFocusable(
        FrameworkElement host,
        object originalSource,
        HostedFocusableControlBuffer focusables)
    {
        DependencyObject? focused = null;
        try
        {
            if (host.XamlRoot is not null)
                focused = FocusManager.GetFocusedElement(host.XamlRoot) as DependencyObject;
        }
        catch
        {
        }

        var current = FindFocusableAncestorWithin(host, focused);
        if (current is not null && ContainsHostedFocusable(focusables, current))
            return current;

        current = FindFocusableAncestorWithin(host, originalSource as DependencyObject);
        return current is not null && ContainsHostedFocusable(focusables, current) ? current : null;
    }

    internal static bool ContainsHostedFocusable(HostedFocusableControlBuffer focusables, Control control)
    {
        for (int i = 0; i < focusables.Count; i++)
        {
            if (ReferenceEquals(focusables[i], control))
                return true;
        }

        return false;
    }

    internal static Control? FindFocusableAncestorWithin(FrameworkElement host, DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, host))
                return node is Control hostControl && IsHostedFocusableControl(hostControl) ? hostControl : null;

            if (node is Control control && IsHostedFocusableControl(control))
                return control;

            DependencyObject? parent = null;
            try { parent = VisualTreeHelper.GetParent(node); }
            catch { }
            node = parent;
        }

        return null;
    }

    internal static HostedFocusableControlBuffer CollectHostedFocusableControls(DependencyObject root)
    {
        var result = new HostedFocusableControlBuffer();
        CollectHostedFocusableControls(root, result);
        return result;
    }

    internal static void CollectHostedFocusableControls(DependencyObject node, HostedFocusableControlBuffer result)
    {
        if (node is Control control && IsHostedFocusableControl(control))
            result.Add(control);

        int childCount;
        try { childCount = VisualTreeHelper.GetChildrenCount(node); }
        catch { return; }

        for (int i = 0; i < childCount; i++)
        {
            DependencyObject child;
            try { child = VisualTreeHelper.GetChild(node, i); }
            catch { continue; }
            CollectHostedFocusableControls(child, result);
        }
    }

    internal static bool IsHostedFocusableControl(Control control) =>
        control.Visibility == Visibility.Visible &&
        control.IsEnabled &&
        control.IsTabStop;
}
