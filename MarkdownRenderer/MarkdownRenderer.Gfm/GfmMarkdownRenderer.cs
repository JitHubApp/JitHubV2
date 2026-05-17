using System;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Gfm;

/// <summary>
/// Convenience factory for creating a renderer with GitHub-flavored markdown enabled.
/// </summary>
public static class GfmMarkdownRenderer
{
    /// <summary>
    /// Creates a renderer configured with GitHub-flavored markdown support.
    /// </summary>
    /// <param name="markdown">Initial markdown source text.</param>
    /// <param name="theme">Theme to assign, or null to use the renderer default.</param>
    /// <param name="embedFactory">Embed factory to assign, or null to disable hosted block embeds.</param>
    /// <param name="isSelectionEnabled">True to enable text selection.</param>
    /// <param name="imageResolver">Optional host-specific image resolver used before public URI loading.</param>
    /// <param name="imageBaseUri">Optional base URI used to resolve relative image sources.</param>
    /// <param name="imageDocumentPath">Optional source document path used by host image resolvers.</param>
    /// <returns>A new configured renderer control.</returns>
    public static MarkdownRendererControl CreateDefault(
        string? markdown = null,
        MarkdownTheme? theme = null,
        IMarkdownEmbedFactory? embedFactory = null,
        bool isSelectionEnabled = true,
        IMarkdownImageResolver? imageResolver = null,
        Uri? imageBaseUri = null,
        string? imageDocumentPath = null)
        => new MarkdownRendererControlBuilder()
            .UseGitHubFlavoredMarkdown()
            .WithMarkdown(markdown)
            .WithTheme(theme)
            .WithEmbedFactory(embedFactory)
            .WithImageResolver(imageResolver)
            .WithImageBaseUri(imageBaseUri)
            .WithImageDocumentPath(imageDocumentPath)
            .WithSelectionEnabled(isSelectionEnabled)
            .Build();
}
