namespace MarkdownRenderer.Layout;

/// <summary>
/// A block that can contribute a host realization plan, paired with the
/// top-level root whose committed placement owns its geometry. Candidates are
/// captured in document traversal order while the semantic snapshot is built.
/// </summary>
internal readonly record struct LayoutPlanCandidate(BlockBox Box, int TopLevelOrdinal);
