namespace MarkdownRenderer.Layout.Boxes;

/// <summary>
/// A snapshot of an image box's admitted work: how many operations have not
/// drained, and whether disposal has completed after they all drained.
/// </summary>
internal readonly record struct ImageWorkState(int ActiveWorkCount, bool LifetimeDisposed);
