using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace MarkdownRenderer.Controls.Internal.View;

/// <summary>Walks the visual tree upward from an element.</summary>
internal static class VisualTreeAncestry
{
    /// <summary>
    /// True when <paramref name="element"/> is <paramref name="ancestor"/> or
    /// one of its visual descendants. An element whose parent cannot be read,
    /// such as one already detached from its native tree, is not within.
    /// </summary>
    internal static bool IsWithin(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null;)
        {
            if (ReferenceEquals(current, ancestor))
                return true;

            try { current = VisualTreeHelper.GetParent(current); }
            catch { return false; }
        }

        return false;
    }
}
