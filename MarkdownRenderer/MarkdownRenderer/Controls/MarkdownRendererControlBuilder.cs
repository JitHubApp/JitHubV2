using Markdig;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Images;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;
using System;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Fluent builder for creating a configured <see cref="MarkdownRendererControl"/>.
/// </summary>
public sealed class MarkdownRendererControlBuilder
{
    private string _markdown = string.Empty;
    private MarkdownEngine? _engine;
    private MarkdownEngineBuilder? _derivedEngineBuilder;
    private MarkdownTheme? _theme;
    private MarkdownStyleSheet? _styleSheet;
    private MarkdownExtensionRegistry? _registry;
    private IMarkdownEmbedFactory? _embedFactory;
    private IMarkdownHostedElementFactory? _hostedElementFactory;
    private IMarkdownImageResolver? _imageResolver;
    private IMarkdownSvgRenderer? _svgRenderer;
    private Uri? _imageBaseUri;
    private string? _imageDocumentPath;
    private MarkdownDocumentSource? _imageDocumentSource;
    private bool _allowThirdPartyRemoteImages;
    private bool _isSelectionEnabled = true;
    private bool _isCodeBlockCopyEnabled = true;
    private bool _isTaskListEditingEnabled;
    private IMarkdownCommandProvider? _commandProvider;
    private IMarkdownStringProvider? _stringProvider;
    private string? _codeBlockCopyButtonLabel;
    private string? _codeBlockCopiedButtonLabel;
    private bool _isCodeBlockSyntaxHighlightingEnabled = true;
#pragma warning disable CS0618 // Stored solely for the source-compatible builder alias.
    private ICodeBlockSyntaxHighlighter? _codeHighlighter;
#pragma warning restore CS0618
    private Func<ICodeHighlighter>? _ownedCodeHighlighterFactory;
    private CodeBlockLineNumberMode _codeBlockLineNumberMode = CodeBlockLineNumberMode.AutoMultiline;
    private CodeBlockWrappingMode _codeBlockWrappingMode = CodeBlockWrappingMode.NoWrap;

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

    /// <summary>Sets the immutable semantic style sheet used by the control.</summary>
    /// <param name="styleSheet">Style sheet to assign, or null to use theme styles only.</param>
    /// <returns>The current builder.</returns>
    public MarkdownRendererControlBuilder WithStyleSheet(MarkdownStyleSheet? styleSheet)
    {
        _styleSheet = styleSheet;
        return this;
    }

    internal MarkdownRendererControlBuilder WithExtensionRegistry(MarkdownExtensionRegistry? registry)
    {
        _registry = registry;
        return this;
    }

    internal MarkdownRendererControlBuilder ConfigureLegacyExtensions(Action<MarkdownExtensionRegistry> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        _registry ??= new MarkdownExtensionRegistry();
        configure(_registry);
        return this;
    }

    /// <summary>
    /// Adds a profile and its WinUI presentation registrations to one immutable
    /// engine snapshot. Optional feature packs use this path so controls and
    /// bare views share the same frozen registry as the parser engine.
    /// </summary>
    internal MarkdownRendererControlBuilder AddProfileWithPresentation(
        MarkdownProfile profile,
        Action<MarkdownExtensionRegistry> configure,
        Action<MarkdownPipelineBuilder>? configureParser = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(configure);
        MarkdownEngineBuilder engineBuilder = GetOrCreateDerivedEngineBuilder()
            .AddProfile(profile);
        if (configureParser is not null)
            engineBuilder.ConfigureParser(configureParser);
        engineBuilder.ConfigurePresentation(
                static () => new MarkdownExtensionRegistry(),
                configure);
        return this;
    }

    /// <summary>Adds presentation registrations to the current engine snapshot.</summary>
    internal MarkdownRendererControlBuilder ConfigureEnginePresentation(
        Action<MarkdownExtensionRegistry> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        GetOrCreateDerivedEngineBuilder().ConfigurePresentation(
            static () => new MarkdownExtensionRegistry(),
            configure);
        return this;
    }

    internal MarkdownRendererControlBuilder WithEmbedFactory(IMarkdownEmbedFactory? embedFactory)
    {
        _embedFactory = embedFactory;
        return this;
    }

    /// <summary>Sets the optional resolver used before built-in image loading.</summary>
    /// <param name="imageResolver">Image resolver to assign, or null to use built-in public loading only.</param>
    /// <returns>The current builder.</returns>
    public MarkdownRendererControlBuilder WithImageResolver(IMarkdownImageResolver? imageResolver)
    {
        _imageResolver = imageResolver;
        return this;
    }

    /// <summary>Sets the optional provider used to render admitted static SVG images.</summary>
    /// <param name="svgRenderer">
    /// Shared renderer to borrow, or null to disable provider-backed SVG rendering.
    /// Controls created by this builder never dispose the renderer.
    /// </param>
    /// <returns>The current builder.</returns>
    public MarkdownRendererControlBuilder WithSvgRenderer(IMarkdownSvgRenderer? svgRenderer)
    {
        _svgRenderer = svgRenderer;
        return this;
    }

    /// <summary>Sets the base URI used to resolve relative image sources.</summary>
    /// <param name="baseUri">Document base URI, or null when relative image resolution is not needed.</param>
    /// <returns>The current builder.</returns>
    public MarkdownRendererControlBuilder WithImageBaseUri(Uri? baseUri)
    {
        _imageBaseUri = baseUri;
        return this;
    }

    /// <summary>Sets the source document path used by host image resolvers.</summary>
    /// <param name="documentPath">Source document path, or null when unavailable.</param>
    /// <returns>The current builder.</returns>
    public MarkdownRendererControlBuilder WithImageDocumentPath(string? documentPath)
    {
        _imageDocumentPath = documentPath;
        return this;
    }

    /// <summary>Sets the stable identity and repository context of the source document.</summary>
    public MarkdownRendererControlBuilder WithImageDocumentSource(MarkdownDocumentSource? documentSource)
    {
        _imageDocumentSource = documentSource;
        return this;
    }

    /// <summary>
    /// Sets whether the host's image policy permits third-party HTTPS images. This can
    /// represent an application-wide default or explicit per-document consent. Insecure
    /// HTTP images remain blocked regardless of this value.
    /// </summary>
    public MarkdownRendererControlBuilder WithThirdPartyRemoteImagesAllowed(bool allowed)
    {
        _allowThirdPartyRemoteImages = allowed;
        return this;
    }

    /// <summary>Sets whether unified read-only text selection is enabled.</summary>
    /// <param name="isEnabled">
    /// True to allow mouse, keyboard, pen, touch, and UI Automation selection;
    /// false to disable selection for every input modality.
    /// </param>
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
#pragma warning disable CS0618 // Declares the source-compatible builder alias.
    [Obsolete("Use WithCodeHighlighter(ICodeHighlighter?) for cancellation-aware highlighting.")]
    public MarkdownRendererControlBuilder WithCodeBlockSyntaxHighlighter(ICodeBlockSyntaxHighlighter? highlighter)
    {
        _codeHighlighter = highlighter;
        _ownedCodeHighlighterFactory = null;
        return this;
    }

    /// <summary>Sets the viewport-aware factory for declarative hosted content.</summary>
    public MarkdownRendererControlBuilder WithHostedElementFactory(
        IMarkdownHostedElementFactory? hostedElementFactory)
    {
        _hostedElementFactory = hostedElementFactory;
        return this;
    }

    /// <summary>Sets the immutable parser engine shared by the configured view.</summary>
    public MarkdownRendererControlBuilder WithEngine(MarkdownEngine? engine)
    {
        _engine = engine;
        _derivedEngineBuilder = null;
        return this;
    }

    /// <summary>
    /// Adds a parsing profile while preserving the current engine's cache budget
    /// and declarative extensions.
    /// </summary>
    public MarkdownRendererControlBuilder AddProfile(MarkdownProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        GetOrCreateDerivedEngineBuilder().AddProfile(profile);
        return this;
    }
#pragma warning restore CS0618

    /// <summary>Enables editable task markers backed by explicit host commands.</summary>
    public MarkdownRendererControlBuilder WithTaskListEditingEnabled(bool enabled = true)
    {
        _isTaskListEditingEnabled = enabled;
        return this;
    }

    /// <summary>Sets the host provider for target-aware commands.</summary>
    public MarkdownRendererControlBuilder WithCommandProvider(IMarkdownCommandProvider? provider)
    {
        _commandProvider = provider;
        return this;
    }

    /// <summary>Sets the host provider for localized renderer strings.</summary>
    public MarkdownRendererControlBuilder WithStringProvider(IMarkdownStringProvider? provider)
    {
        _stringProvider = provider;
        return this;
    }

    /// <summary>Sets the stable asynchronous, cancellation-aware code highlighter.</summary>
    public MarkdownRendererControlBuilder WithCodeHighlighter(ICodeHighlighter? highlighter)
    {
        _codeHighlighter = highlighter;
        _ownedCodeHighlighterFactory = null;
        return this;
    }

    /// <summary>
    /// Configures a fresh highlighter owned by each control produced from this
    /// reusable builder. Used only by source-compatible optional-pack helpers.
    /// </summary>
    internal MarkdownRendererControlBuilder WithOwnedCodeHighlighterFactory(
        Func<ICodeHighlighter> create)
    {
        _ownedCodeHighlighterFactory = create ?? throw new ArgumentNullException(nameof(create));
        _codeHighlighter = null;
        return this;
    }

    /// <summary>Sets when code blocks show line numbers.</summary>
    public MarkdownRendererControlBuilder WithCodeBlockLineNumberMode(CodeBlockLineNumberMode mode)
    {
        _codeBlockLineNumberMode = mode;
        return this;
    }

    /// <summary>Sets whether long code lines wrap or use local horizontal scrolling.</summary>
    public MarkdownRendererControlBuilder WithCodeBlockWrappingMode(CodeBlockWrappingMode mode)
    {
        _codeBlockWrappingMode = mode;
        return this;
    }

    /// <summary>Creates a configured view that owns its vertical viewport.</summary>
    public MarkdownScrollView BuildScrollView() => Configure(new MarkdownScrollView());

    /// <summary>Creates a configured view for an ancestor-owned effective viewport.</summary>
    public MarkdownDocumentView BuildDocumentView() => Configure(new MarkdownDocumentView());

    /// <summary>Creates the obsolete 1.x compatibility facade.</summary>
    [Obsolete("Use BuildScrollView or BuildDocumentView to make viewport ownership explicit.")]
    public MarkdownRendererControl Build()
        => Configure(new MarkdownRendererControl());

    private T Configure<T>(T control) where T : MarkdownRendererControl
    {
        MarkdownEngine? unassignedDerivedEngine = null;
        IDisposable? unassignedHighlighterOwner = null;
        try
        {
            if (_derivedEngineBuilder is not null)
            {
                unassignedDerivedEngine = _derivedEngineBuilder.Build();
                MarkdownEngine derivedEngine = unassignedDerivedEngine;
                unassignedDerivedEngine = null;
                control.SetOwnedEngine(derivedEngine);
            }
            else
            {
                // Engines supplied through WithEngine are borrowed. The
                // control never disposes them.
                control.Engine = _engine ?? control.Engine ?? MarkdownEngine.Default;
            }

            control.Markdown = _markdown;
            control.Theme = _theme;
            control.StyleSheet = _styleSheet;
            control.ExtensionRegistry = _registry;
            control.EmbedFactory = _embedFactory;
            control.HostedElementFactory = _hostedElementFactory;
            control.ImageResolver = _imageResolver;
            control.SvgRenderer = _svgRenderer;
            control.ImageBaseUri = _imageBaseUri;
            control.ImageDocumentPath = _imageDocumentPath;
            control.ImageDocumentSource = _imageDocumentSource;
            control.AllowThirdPartyRemoteImages = _allowThirdPartyRemoteImages;
            control.IsSelectionEnabled = _isSelectionEnabled;
            control.IsCodeBlockCopyEnabled = _isCodeBlockCopyEnabled;
            control.IsTaskListEditingEnabled = _isTaskListEditingEnabled;
            control.CommandProvider = _commandProvider;
            control.StringProvider = _stringProvider;
            control.CodeBlockCopyButtonLabel = _codeBlockCopyButtonLabel;
            control.CodeBlockCopiedButtonLabel = _codeBlockCopiedButtonLabel;
            control.IsCodeBlockSyntaxHighlightingEnabled = _isCodeBlockSyntaxHighlightingEnabled;
            if (_ownedCodeHighlighterFactory is not null)
            {
                ICodeHighlighter ownedHighlighter = _ownedCodeHighlighterFactory() ??
                    throw new InvalidOperationException("The code-highlighter factory returned null.");
                unassignedHighlighterOwner = ownedHighlighter as IDisposable ??
                    throw new InvalidOperationException("An owned code-highlighter factory must return an IDisposable service.");
                IDisposable highlighterOwner = unassignedHighlighterOwner;
                control.SetOwnedCodeHighlighter(ownedHighlighter, highlighterOwner);
                unassignedHighlighterOwner = null;
            }
            else if (_codeHighlighter is ICodeHighlighter highlighter)
                control.CodeHighlighter = highlighter;
            else
#pragma warning disable CS0618 // Assign the compatibility alias only for legacy providers.
                control.CodeBlockSyntaxHighlighter = _codeHighlighter;
#pragma warning restore CS0618
            control.CodeBlockLineNumberMode = _codeBlockLineNumberMode;
            control.CodeBlockWrappingMode = _codeBlockWrappingMode;
            return control;
        }
        catch
        {
            unassignedDerivedEngine?.Dispose();
            unassignedHighlighterOwner?.Dispose();
            control.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Materializes the effective engine for headless helper tests. The caller
    /// owns the returned value when this builder contains derived configuration.
    /// </summary>
    internal MarkdownEngine CreateEngineSnapshotForTesting()
        => _derivedEngineBuilder?.Build() ?? _engine ?? MarkdownEngine.Default;

    private MarkdownEngineBuilder GetOrCreateDerivedEngineBuilder()
        => _derivedEngineBuilder ??= (_engine ?? MarkdownEngine.Default).ToBuilder();
}
