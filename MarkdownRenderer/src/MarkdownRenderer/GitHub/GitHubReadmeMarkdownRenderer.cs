using System;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.GitHub;

/// <summary>Creates renderer controls configured for GitHub README documents.</summary>
public static class GitHubReadmeMarkdownRenderer
{
    /// <summary>Gets a reusable immutable GitHub README engine.</summary>
    /// <remarks>
    /// Like any engine it is the scope of its caches: every view given this
    /// engine shares its completed-document cache and layout hints. Disposing it
    /// has no effect.
    /// </remarks>
    public static MarkdownEngine SharedEngine { get; } = new MarkdownEngineBuilder()
        .UseGitHubReadme()
        .BuildShared();

    /// <summary>Creates a renderer using the GitHub README profile.</summary>
    public static MarkdownScrollView CreateDefault(
        string? markdown = null,
        MarkdownTheme? theme = null,
        bool isSelectionEnabled = true,
        IMarkdownImageResolver? imageResolver = null,
        Uri? imageBaseUri = null,
        string? imageDocumentPath = null)
        => new MarkdownRendererControlBuilder()
            .UseGitHubReadme()
            .WithMarkdown(markdown)
            .WithTheme(theme)
            .WithImageResolver(imageResolver)
            .WithImageBaseUri(imageBaseUri)
            .WithImageDocumentPath(imageDocumentPath)
            .WithSelectionEnabled(isSelectionEnabled)
            .BuildScrollView();
}
