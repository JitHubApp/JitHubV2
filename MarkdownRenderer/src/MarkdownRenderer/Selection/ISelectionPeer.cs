namespace MarkdownRenderer.Selection;

/// <summary>
/// A renderer registered with a <see cref="MarkdownSelectionCoordinator"/>,
/// which clears its selection when another peer on the same XamlRoot selects.
/// </summary>
internal interface ISelectionPeer
{
    /// <summary>Clears this peer's selection because another peer selected.</summary>
    void ClearSelectionFromCoordinator();
}
