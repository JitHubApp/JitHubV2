namespace MarkdownRenderer.Controls.Internal.Selection;

// The selection unit chosen by the current press (single, double or triple
// click); governs how a drag extends the selection.
internal enum ClickSelectionMode { Single, Word, Block }
