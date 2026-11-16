using MarkdownRenderer.Controls.Internal.Selection.TouchSelection;
using MarkdownRenderer.Layout.Internal.Snapshots;

namespace MarkdownRenderer.Controls.Internal.Pipeline.Commit;

/// <summary>
/// What a snapshot replacement decided while it published the new snapshot,
/// which the rest of the commit acts on.
/// </summary>
/// <param name="Old">The snapshot the replacement retired from publication.</param>
/// <param name="ScrollAnchor">The reader's position to restore, if captured.</param>
/// <param name="TouchSelectionRestoreSourceGeneration">The published generation the restore intent was captured against.</param>
/// <param name="TouchSelectionRestoreIntent">The touch selection restore intent at publication.</param>
/// <param name="SemanticInputChanged">Whether automation must treat this as a new document.</param>
/// <param name="PreserveSelection">Whether the selection survives into the new snapshot.</param>
internal sealed record ReplacementPublication(
    LayoutSnapshot? Old,
    ReplacementScrollAnchor? ScrollAnchor,
    long TouchSelectionRestoreSourceGeneration,
    TouchSelectionPresentationRestoreIntent? TouchSelectionRestoreIntent,
    bool SemanticInputChanged,
    bool PreserveSelection);
