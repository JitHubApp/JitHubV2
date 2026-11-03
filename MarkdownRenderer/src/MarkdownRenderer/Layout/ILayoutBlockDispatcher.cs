using Markdig.Syntax;

namespace MarkdownRenderer.Layout;

/// <summary>
/// Dispatches one child block back to the top-level block builder. Container
/// builders reach the dispatcher through this seam because the block builder
/// itself composes them.
/// </summary>
internal interface ILayoutBlockDispatcher
{
    BlockBox? BuildBlock(Block block);
}
