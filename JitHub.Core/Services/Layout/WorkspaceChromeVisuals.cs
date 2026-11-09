using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace JitHub.Services.Layout;

public static class WorkspaceChromeVisuals
{
    public static Thickness ToThickness(this WorkspaceInsets insets) =>
        new(insets.Left, insets.Top, insets.Right, insets.Bottom);

    public static void ApplyRoot(Grid root, WorkspaceChromeState state)
    {
        root.Padding = state.Insets.ToThickness();
        root.MinWidth = 0;
        root.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    public static void ApplyHeader(Grid header, WorkspaceChromeState state)
    {
        header.MinWidth = 0;
        header.MinHeight = state.Header.MinHeight;
        header.ColumnSpacing = state.Header.ColumnSpacing;
        header.RowSpacing = state.Header.RowSpacing;
        header.HorizontalAlignment = HorizontalAlignment.Stretch;
        header.VerticalAlignment = VerticalAlignment.Center;
    }

    public static void ApplyOptionalContext(FrameworkElement context, WorkspaceChromeState state) =>
        context.Visibility = state.ShowOptionalHeaderContext
            ? Visibility.Visible
            : Visibility.Collapsed;

    public static void ApplyActionLabel(FrameworkElement label, WorkspaceChromeState state) =>
        label.Visibility = state.ShowActionLabels
            ? Visibility.Visible
            : Visibility.Collapsed;

    public static void ApplyActionButton(
        Button button,
        WorkspaceChromeState state,
        bool hasVisibleLabel)
    {
        button.MinHeight = state.Header.ActionHeight;
        button.MinWidth = hasVisibleLabel ? 0 : state.Header.ActionHeight;
        button.Padding = hasVisibleLabel
            ? new Thickness(10, 6, 10, 6)
            : new Thickness(0);
    }

    public static void ApplyPlacement(
        FrameworkElement element,
        WorkspaceChromeState state,
        WorkspaceElementPlacement wide,
        WorkspaceElementPlacement stacked)
    {
        WorkspaceElementPlacement placement = WorkspaceChromeLayout.ChoosePlacement(state, wide, stacked);
        Grid.SetRow(element, placement.Row);
        Grid.SetColumn(element, placement.Column);
        Grid.SetColumnSpan(element, placement.ColumnSpan);
        element.HorizontalAlignment = placement.StretchHorizontally
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Right;
    }
}
