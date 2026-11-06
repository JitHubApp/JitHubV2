using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Parsing;

/// <summary>
/// Non-generic erased interface for custom node renderers. Used for AOT-safe
/// dispatch in <see cref="MarkdownExtensionRegistry"/>; avoids reflection.
/// </summary>
internal interface IMarkdownNodeRendererErased
{
    /// <summary>Build a <see cref="BlockBox"/> for the given AST node.</summary>
    BlockBox? BuildBlock(object node, MarkdownLayoutContext context);
}
