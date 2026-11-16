using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Internal.Selection;

/// <summary>
/// The renderer services <see cref="SelectionDismissalHook"/> calls when a
/// press elsewhere in the window may dismiss the selection.
/// </summary>
internal interface ISelectionDismissalHost
{
    /// <summary>True when <paramref name="element"/> is inside a code block's copy action.</summary>
    bool IsElementWithinCodeBlockAction(DependencyObject element);

    /// <summary>True when <paramref name="element"/> is inside a touch selection handle.</summary>
    bool IsElementWithinSelectionHandle(DependencyObject element);

    /// <summary>True when the canvas point is over a hosted embed or a code block action.</summary>
    bool IsPointOverEmbed(Point canvasPoint);

    /// <summary>Ends every selection interaction and clears the selection.</summary>
    void ClearSelectionForExternalInteraction();
}
