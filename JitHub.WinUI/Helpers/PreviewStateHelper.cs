using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.WinUI.Helpers;

public static class PreviewStateHelper
{
    public static readonly DependencyProperty VisualStateProperty = DependencyProperty.RegisterAttached(
        "VisualState",
        typeof(string),
        typeof(PreviewStateHelper),
        new PropertyMetadata(string.Empty, OnVisualStateChanged));

    public static string GetVisualState(DependencyObject obj)
    {
        return (string)obj.GetValue(VisualStateProperty);
    }

    public static void SetVisualState(DependencyObject obj, string value)
    {
        obj.SetValue(VisualStateProperty, value);
    }

    private static void OnVisualStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control control)
        {
            return;
        }

        string state = e.NewValue as string ?? string.Empty;
        control.IsEnabled = !string.Equals(state, "Disabled", System.StringComparison.OrdinalIgnoreCase);

        if (string.Equals(state, "Focused", System.StringComparison.OrdinalIgnoreCase))
        {
            _ = control.Focus(FocusState.Programmatic);
            state = "Normal";
        }

        if (!string.IsNullOrWhiteSpace(state))
        {
            _ = VisualStateManager.GoToState(control, state, false);
        }
    }
}
