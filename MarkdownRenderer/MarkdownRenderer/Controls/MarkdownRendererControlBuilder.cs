using System;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Fluent builder for creating a configured <see cref="MarkdownRendererControl"/>.
/// </summary>
public sealed class MarkdownRendererControlBuilder
{
    private string _markdown = string.Empty;
    private MarkdownTheme? _theme;
    private MarkdownExtensionRegistry? _registry;
    private IMarkdownEmbedFactory? _embedFactory;
    private bool _isSelectionEnabled = true;

    /// <summary>Sets the markdown source shown by the control.</summary>
    /// <param name="markdown">Markdown source text. A null value is treated as an empty string.</param>
    /// <returns>The current builder.</returns>
    public MarkdownRendererControlBuilder WithMarkdown(string? markdown)
    {
        _markdown = markdown ?? string.Empty;
        return this;
    }

    /// <summary>Sets the theme used by the control.</summary>
    /// <param name="theme">Theme instance to assign, or null to use the renderer default.</param>
    /// <returns>The current builder.</returns>
    public MarkdownRendererControlBuilder WithTheme(MarkdownTheme? theme)
    {
        _theme = theme;
        return this;
    }

    /// <summary>Sets the markdown extension registry used by the control.</summary>
    /// <param name="registry">Registry to assign, or null to use the renderer default CommonMark registry.</param>
    /// <returns>The current builder.</returns>
    public MarkdownRendererControlBuilder WithExtensionRegistry(MarkdownExtensionRegistry? registry)
    {
        _registry = registry;
        return this;
    }

    /// <summary>Configures the extension registry used by the control.</summary>
    /// <param name="configure">Callback that mutates the registry.</param>
    /// <returns>The current builder.</returns>
    public MarkdownRendererControlBuilder ConfigureExtensions(Action<MarkdownExtensionRegistry> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        _registry ??= new MarkdownExtensionRegistry();
        configure(_registry);
        return this;
    }

    /// <summary>Sets the embed factory used for block-level hosted controls.</summary>
    /// <param name="embedFactory">Embed factory to assign, or null to disable hosted block embeds.</param>
    /// <returns>The current builder.</returns>
    public MarkdownRendererControlBuilder WithEmbedFactory(IMarkdownEmbedFactory? embedFactory)
    {
        _embedFactory = embedFactory;
        return this;
    }

    /// <summary>Sets whether text selection is enabled.</summary>
    /// <param name="isEnabled">True to allow text selection; false to disable selection gestures.</param>
    /// <returns>The current builder.</returns>
    public MarkdownRendererControlBuilder WithSelectionEnabled(bool isEnabled)
    {
        _isSelectionEnabled = isEnabled;
        return this;
    }

    /// <summary>Creates the configured markdown renderer control.</summary>
    /// <returns>A new <see cref="MarkdownRendererControl"/> instance.</returns>
    public MarkdownRendererControl Build()
    {
        return new MarkdownRendererControl
        {
            Markdown = _markdown,
            Theme = _theme,
            ExtensionRegistry = _registry,
            EmbedFactory = _embedFactory,
            IsSelectionEnabled = _isSelectionEnabled,
        };
    }
}
