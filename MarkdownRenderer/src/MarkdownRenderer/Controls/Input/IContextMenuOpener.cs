using MarkdownRenderer.Layout;
using Windows.Foundation;

namespace MarkdownRenderer.Controls.Input;

/// <summary>Opens the document context menu for a recognized gesture.</summary>
internal interface IContextMenuOpener
{
    /// <summary>
    /// Opens the menu at <paramref name="placementPoint"/> while
    /// <paramref name="requiredSnapshot"/>, its generation and layout revision
    /// are still published. Returns whether the menu opened.
    /// </summary>
    bool Show(
        Point placementPoint,
        bool touchSelection,
        LayoutSnapshot? requiredSnapshot,
        long requiredSnapshotGeneration,
        long requiredLayoutRevision);
}
