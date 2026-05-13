using Markdig.Syntax;
using Microsoft.UI.Xaml;

namespace MarkdownRenderer.Hosting;

/// <summary>
/// Factory for hosted WinUI elements that replace block-level markdown nodes.
/// Implementations are passed to <c>MarkdownRendererControl.EmbedFactory</c>
/// and queried during layout for each AST node that opts into embedding.
/// </summary>
/// <remarks>
/// <para>
/// Layout (<c>Measure</c>) runs on a background thread. Implementations of
/// <see cref="CanCreate"/> must be thread-safe and free of WinUI dependencies.
/// </para>
/// <para>
/// <see cref="CreateBlock"/> is invoked on the UI thread after layout completes.
/// The returned element is hosted on a transparent <c>Canvas</c> overlay above
/// the Win2D paint surface and sized to the block's measured bounds.
/// </para>
/// </remarks>
public interface IMarkdownEmbedFactory
{
    /// <summary>
    /// Background-thread safe: returns true if this factory wants to replace
    /// the given AST node with a hosted WinUI element. The renderer reserves
    /// space for it during layout and instantiates it on the UI thread later.
    /// </summary>
    bool CanCreate(Block block);

    /// <summary>
    /// Returns the desired height (px) for the embed at the given content
    /// width. Called on the background layout thread, so this must NOT touch
    /// any WinUI APIs. Return a fixed number based on the block content.
    /// </summary>
    float MeasureHeight(Block block, float availableWidth);

    /// <summary>
    /// UI-thread only: build the <see cref="FrameworkElement"/> to host. The
    /// element is added to the overlay <c>Canvas</c>; the renderer sets
    /// <c>Width</c> and uses <c>Canvas.SetLeft/SetTop</c> to position it.
    /// </summary>
    FrameworkElement CreateBlock(Block block);

    /// <summary>
    /// UI-thread only: optional hook invoked when a previously-realised embed
    /// is about to be removed from the visual tree because it has scrolled
    /// off-screen and the renderer is virtualising embeds. Factories may
    /// disconnect handlers, dispose resources, or simply do nothing. The
    /// element will not be reused — a future <see cref="CreateBlock"/> call
    /// will produce a fresh instance when needed.
    /// </summary>
    void RecycleBlock(Block block, FrameworkElement element) { }
}
