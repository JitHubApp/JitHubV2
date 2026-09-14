using System;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Gfm;

/// <summary>
/// Convenience factory for creating a renderer with strict GitHub Flavored Markdown enabled.
/// </summary>
public static class GfmMarkdownRenderer
{
    /// <summary>Gets a reusable immutable strict-GFM engine.</summary>
    public static MarkdownEngine SharedEngine { get; } = new MarkdownEngineBuilder()
        .UseGitHubFlavoredMarkdown()
        .BuildShared();

    /// <summary>
    /// Creates a renderer configured with the extensions in the GFM 0.29 specification.
    /// GitHub README additions are available from <c>MarkdownRenderer.GitHub</c>.
    /// </summary>
    /// <param name="markdown">Initial markdown source text.</param>
    /// <param name="theme">Theme to assign, or null to use the renderer default.</param>
    /// <param name="isSelectionEnabled">True to enable text selection.</param>
    /// <param name="imageResolver">Optional host-specific image resolver used before public URI loading.</param>
    /// <param name="imageBaseUri">Optional base URI used to resolve relative image sources.</param>
    /// <param name="imageDocumentPath">Optional source document path used by host image resolvers.</param>
    /// <returns>A new configured viewport-owning renderer.</returns>
    public static MarkdownScrollView CreateDefault(
        string? markdown = null,
        MarkdownTheme? theme = null,
        bool isSelectionEnabled = true,
        IMarkdownImageResolver? imageResolver = null,
        Uri? imageBaseUri = null,
        string? imageDocumentPath = null)
        => new MarkdownRendererControlBuilder()
            .UseGitHubFlavoredMarkdown()
            .WithMarkdown(markdown)
            .WithTheme(theme)
            .WithImageResolver(imageResolver)
            .WithImageBaseUri(imageBaseUri)
            .WithImageDocumentPath(imageDocumentPath)
            .WithSelectionEnabled(isSelectionEnabled)
            .BuildScrollView();
}
