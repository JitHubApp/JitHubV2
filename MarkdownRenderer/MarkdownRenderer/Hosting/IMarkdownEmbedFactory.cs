using Markdig.Syntax;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Hosting;

/// <summary>
/// Factory for hosted WinUI elements. Implementations are passed to
/// <c>MarkdownRendererControl.EmbedFactory</c> and queried during layout for
/// each AST node that opts into embedding.
/// </summary>
public interface IMarkdownEmbedFactory
{
    /// <summary>
    /// Returns true if this factory can render the given AST node as a hosted
    /// WinUI element. The renderer dispatches block-level embeds to a XAML
    /// overlay positioned at the block's bounds.
    /// </summary>
    bool CanCreate(Block block);

    /// <summary>
    /// Build a FrameworkElement to host inside the overlay for the given block.
    /// The element's layout slot will be set by the renderer based on the
    /// block's bounds.
    /// </summary>
    FrameworkElement Create(Block block);
}
