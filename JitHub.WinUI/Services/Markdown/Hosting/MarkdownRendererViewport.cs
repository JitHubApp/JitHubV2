using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace JitHub.Services.Markdown.Hosting;

/// <summary>Moves the vertical scroller a renderer owns.</summary>
internal static class MarkdownRendererViewport
{
    /// <summary>Scrolls the renderer's own vertical scroller back to the top, if it has moved.</summary>
    /// <param name="renderer">A renderer that owns its scroll viewport.</param>
    public static void ScrollToTop(DependencyObject renderer)
    {
        ScrollViewer? viewport = FindDescendantVerticalScrollViewer(renderer);
        if (viewport is { VerticalOffset: > 0.5 })
            viewport.ChangeView(null, 0, null, disableAnimation: true);
    }

    private static ScrollViewer? FindDescendantVerticalScrollViewer(DependencyObject root)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer scrollViewer &&
                scrollViewer.VerticalScrollMode != ScrollMode.Disabled)
            {
                return scrollViewer;
            }

            if (FindDescendantVerticalScrollViewer(child) is { } descendant)
                return descendant;
        }

        return null;
    }
}
