using System;
using MarkdownRenderer.CodeBlocks;
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
    private bool _isCodeBlockCopyEnabled = true;
    private string? _codeBlockCopyButtonLabel;
    private string? _codeBlockCopiedButtonLabel;
    private bool _isCodeBlockSyntaxHighlightingEnabled = true;
    private ICodeBlockSyntaxHighlighter? _codeBlockSyntaxHighlighter;
    private CodeBlockLineNumberMode _codeBlockLineNumberMode = CodeBlockLineNumberMode.AutoMultiline;

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

    /// <summary>Sets whether code block copy buttons are shown.</summary>
    /// <param name="enabled">True to show copy buttons on code blocks; false to hide them.</param>
    /// <returns>The current builder.</returns>
    public MarkdownRendererControlBuilder WithCodeBlockCopyEnabled(bool enabled = true)
    {
        _isCodeBlockCopyEnabled = enabled;
        return this;
    }

    /// <summary>Sets the accessible label and tooltip used for code-block copy buttons.</summary>
    /// <param name="label">Accessible label and tooltip text, or null to use the renderer default.</param>
    /// <returns>The current builder.</returns>
    public MarkdownRendererControlBuilder WithCodeBlockCopyButtonLabel(string? label)
    {
        _codeBlockCopyButtonLabel = label;
        return this;
    }

    /// <summary>Sets the accessible label and tooltip shown after a code-block copy succeeds.</summary>
    /// <param name="label">Accessible label and tooltip text, or null to use the renderer default.</param>
    /// <returns>The current builder.</returns>
    public MarkdownRendererControlBuilder WithCodeBlockCopiedButtonLabel(string? label)
    {
        _codeBlockCopiedButtonLabel = label;
        return this;
    }

    /// <summary>Sets whether configured syntax highlighters may color code blocks.</summary>
    public MarkdownRendererControlBuilder WithCodeBlockSyntaxHighlightingEnabled(bool enabled = true)
    {
        _isCodeBlockSyntaxHighlightingEnabled = enabled;
        return this;
    }

    /// <summary>Sets the optional syntax highlighter used for code blocks.</summary>
    public MarkdownRendererControlBuilder WithCodeBlockSyntaxHighlighter(ICodeBlockSyntaxHighlighter? highlighter)
    {
        _codeBlockSyntaxHighlighter = highlighter;
        return this;
    }

    /// <summary>Sets when code blocks show line numbers.</summary>
    public MarkdownRendererControlBuilder WithCodeBlockLineNumberMode(CodeBlockLineNumberMode mode)
    {
        _codeBlockLineNumberMode = mode;
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
            IsCodeBlockCopyEnabled = _isCodeBlockCopyEnabled,
            CodeBlockCopyButtonLabel = _codeBlockCopyButtonLabel,
            CodeBlockCopiedButtonLabel = _codeBlockCopiedButtonLabel,
            IsCodeBlockSyntaxHighlightingEnabled = _isCodeBlockSyntaxHighlightingEnabled,
            CodeBlockSyntaxHighlighter = _codeBlockSyntaxHighlighter,
            CodeBlockLineNumberMode = _codeBlockLineNumberMode,
        };
    }
}
