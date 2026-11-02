using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>What the focus ring needs from the control.</summary>
internal interface IFocusRingHost
{
    /// <summary>The XAML root, or null while the control is not in a tree.</summary>
    XamlRoot? XamlRoot { get; }

    /// <summary>The theme's focus visual color, or null before a theme is resolved.</summary>
    Windows.UI.Color? FocusVisualColor { get; }

    /// <summary>The focused item now, or null when nothing is focused.</summary>
    Layout.FocusableItem? GetCurrentFocusableIdentity();

    /// <summary>Repaints the interactive text adorner the ring sits beside.</summary>
    void InvalidateInteractiveTextAdorner();

    /// <summary>Asks for focus geometry to be read again later.</summary>
    void RequestFocusGeometryRetry();
}
