using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Parsing;

/// <summary>Generic typed renderer interface.</summary>
public interface IMarkdownNodeRenderer<in TNode> where TNode : class
{
    BlockBox? BuildBlock(TNode node, MarkdownLayoutContext context);
}

/// <summary>
/// Non-generic erased interface for custom node renderers. Used for AOT-safe
/// dispatch in <see cref="MarkdownExtensionRegistry"/>; avoids reflection.
/// </summary>
public interface IMarkdownNodeRendererErased
{
    /// <summary>Build a <see cref="BlockBox"/> for the given AST node.</summary>
    BlockBox? BuildBlock(object node, MarkdownLayoutContext context);
}

/// <summary>
/// Typed helper base class. Implementors override the strongly-typed overload;
/// the erased overload forwards to it.
/// </summary>
public abstract class MarkdownNodeRenderer<TNode> : IMarkdownNodeRendererErased, IMarkdownNodeRenderer<TNode>
    where TNode : class
{
    public BlockBox? BuildBlock(object node, MarkdownLayoutContext context)
        => node is TNode typed ? BuildBlock(typed, context) : null;

    public abstract BlockBox? BuildBlock(TNode node, MarkdownLayoutContext context);
}

