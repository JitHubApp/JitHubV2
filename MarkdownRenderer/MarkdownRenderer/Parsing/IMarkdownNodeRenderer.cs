using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Parsing;

/// <summary>
/// Builds a renderer-specific layout block for a Markdig syntax node.
/// </summary>
/// <typeparam name="TNode">The concrete Markdig syntax node type handled by the renderer.</typeparam>
public interface IMarkdownNodeRenderer<in TNode> where TNode : class
{
    /// <summary>
    /// Creates a block for <paramref name="node"/>, or returns <see langword="null"/> to let the default renderer handle it.
    /// </summary>
    /// <param name="node">The Markdig syntax node being rendered.</param>
    /// <param name="context">The background layout context for this render pass.</param>
    /// <returns>A custom block, or <see langword="null"/> when the renderer does not handle the node.</returns>
    BlockBox? BuildBlock(TNode node, MarkdownLayoutContext context);
}

/// <summary>
/// Non-generic erased interface for custom node renderers. Used for AOT-safe
/// dispatch in <see cref="MarkdownExtensionRegistry"/>; avoids reflection.
/// </summary>
internal interface IMarkdownNodeRendererErased
{
    /// <summary>Build a <see cref="BlockBox"/> for the given AST node.</summary>
    BlockBox? BuildBlock(object node, MarkdownLayoutContext context);
}

/// <summary>
/// Typed helper base class. Implementors override the strongly-typed overload;
/// the erased overload forwards to it.
/// </summary>
public abstract class MarkdownNodeRenderer<TNode> : IMarkdownNodeRenderer<TNode>
    where TNode : class
{
    /// <inheritdoc />
    public abstract BlockBox? BuildBlock(TNode node, MarkdownLayoutContext context);
}

