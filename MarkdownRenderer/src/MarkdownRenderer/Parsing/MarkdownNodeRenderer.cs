using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Parsing;

/// <summary>
/// Typed helper base class. Implementors override the strongly-typed overload;
/// the erased overload forwards to it.
/// </summary>
internal abstract class MarkdownNodeRenderer<TNode> :
    IMarkdownNodeRenderer<TNode>,
    IMarkdownNodeRendererErased
    where TNode : class
{
    /// <inheritdoc />
    public abstract BlockBox? BuildBlock(TNode node, MarkdownLayoutContext context);

    BlockBox? IMarkdownNodeRendererErased.BuildBlock(object node, MarkdownLayoutContext context)
        => node is TNode typed ? BuildBlock(typed, context) : null;
}
