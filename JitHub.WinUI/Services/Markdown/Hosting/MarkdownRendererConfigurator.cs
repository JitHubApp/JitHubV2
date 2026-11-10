using System;
using CommunityToolkit.Mvvm.DependencyInjection;
using JitHub.Core.Services.Accounts;
using JitHub.Markdown.Localization;
using MarkdownRenderer;
using MarkdownRenderer.CodeBlocks;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Images;
using MarkdownRenderer.Performance;
using MarkdownRenderer.Theming;
using Microsoft.UI.Xaml;

namespace JitHub.Services.Markdown.Hosting;

/// <summary>
/// Applies JitHub's document style and host settings to a renderer through its
/// public property API, using the shared Markdown runtime providers.
/// </summary>
internal static class MarkdownRendererConfigurator
{
    private static readonly Uri DefaultBaseUri = new("https://github.com/", UriKind.Absolute);

    // Holds no state; LocalizedResourceText owns the resource lookup.
    private static readonly JitHubMarkdownStringProvider StringProvider =
        new(LocalizedMarkdownResourceText.Instance);

    private static MarkdownPerformanceSession GetSharedPerformanceSession(JitHubMarkdownRuntime runtime) =>
        runtime.GetPerformanceSession(
            Ioc.Default.GetService<IAccountService>()?.GetUser() ?? 0);

    /// <summary>Resolves a document's base URL, defaulting to github.com.</summary>
    /// <param name="baseUrl">The document's base URL, if any.</param>
    /// <returns>An absolute base URI.</returns>
    public static Uri ResolveImageBaseUri(string? baseUrl)
    {
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri? baseUri)
            ? baseUri
            : DefaultBaseUri;
    }

    /// <summary>Installs JitHub's theme, style sheet and localized strings on a new renderer.</summary>
    /// <param name="renderer">The new renderer.</param>
    /// <param name="theme">The host's theme.</param>
    public static void ApplyDocumentStyle(MarkdownRendererControl renderer, MarkdownTheme theme)
    {
        renderer.Theme = theme;
        // GitHub articles use 16-DIP paragraph spacing, while list paragraphs
        // collapse into the list-item flow. Keep JitHub's compact list token
        // without shortening standalone paragraphs.
        renderer.StyleSheet = new MarkdownStyleSheet(
            new MarkdownStyleRule[]
            {
                new MarkdownStyleRule(
                    new MarkdownStyleSelector(MarkdownStyleRole.Body, minimumNestingDepth: 1),
                    new ElementStyleOverride
                    {
                        Margin = MarkdownRendererResources.ResolveThickness("AppMarkdownBodyMargin", new Thickness(0, 0, 0, 8)),
                    }),
            });
        renderer.StringProvider = StringProvider;
    }

    /// <summary>Applies the host settings to a renderer that has not received Markdown yet.</summary>
    /// <param name="renderer">The new renderer.</param>
    /// <param name="settings">The host settings.</param>
    /// <param name="runtime">The app's shared Markdown providers.</param>
    public static void ApplyInitialSettings(
        MarkdownRendererControl renderer,
        MarkdownRendererSettings settings,
        JitHubMarkdownRuntime runtime)
    {
        renderer.IsSelectionEnabled = settings.IsSelectionEnabled;
        renderer.CodeBlockChromeMode = CodeBlockChromeMode.Enhanced;
        renderer.CodeBlockLineNumberMode = CodeBlockLineNumberMode.AutoMultiline;
        renderer.IsCodeBlockCopyEnabled = settings.IsCodeBlockCopyEnabled;
        renderer.ImageResolver = settings.ImageResolver;
        renderer.SvgRenderer = runtime.SvgRenderer;
        renderer.RasterDecoder = runtime.RasterDecoder;
        renderer.PerformanceSession = GetSharedPerformanceSession(runtime);
        renderer.ImageBaseUri = settings.ImageBaseUri;
        renderer.ImageDocumentPath = settings.DocumentPath;
        renderer.ImageDocumentSource = settings.DocumentSource;
        renderer.AllowThirdPartyRemoteImages = settings.AllowThirdPartyRemoteImages;
    }

    /// <summary>
    /// Applies the host settings, changing only properties whose value
    /// differs, and assigns <paramref name="markdown"/> last.
    /// </summary>
    /// <param name="renderer">A loaded renderer that has completed its first layout.</param>
    /// <param name="settings">The host settings.</param>
    /// <param name="markdown">The Markdown to render.</param>
    /// <param name="runtime">The app's shared Markdown providers.</param>
    public static void ApplySettings(
        MarkdownRendererControl renderer,
        MarkdownRendererSettings settings,
        string markdown,
        JitHubMarkdownRuntime runtime)
    {
        // Apply every renderer setting before assigning Markdown. Several of
        // these properties request a rebuild and advance the pipeline
        // generation; assigning the document last keeps one final generation.
        if (renderer.IsSelectionEnabled != settings.IsSelectionEnabled)
            renderer.IsSelectionEnabled = settings.IsSelectionEnabled;
        CodeBlockChromeMode chromeMode = CodeBlockChromeMode.Enhanced;
        CodeBlockLineNumberMode lineNumberMode = CodeBlockLineNumberMode.AutoMultiline;
        bool copyEnabled = settings.IsCodeBlockCopyEnabled;
        if (renderer.CodeBlockChromeMode != chromeMode)
            renderer.CodeBlockChromeMode = chromeMode;
        if (renderer.CodeBlockLineNumberMode != lineNumberMode)
            renderer.CodeBlockLineNumberMode = lineNumberMode;
        if (renderer.IsCodeBlockCopyEnabled != copyEnabled)
            renderer.IsCodeBlockCopyEnabled = copyEnabled;
        Style? copyButtonStyle = MarkdownRendererResources.TryResolveAppResource("AppToolbarButtonStyle", out object? copyStyle)
            ? copyStyle as Style
            : null;
        if (!ReferenceEquals(renderer.CodeBlockCopyButtonStyle, copyButtonStyle))
            renderer.CodeBlockCopyButtonStyle = copyButtonStyle;
        var codeHighlighter = settings.IsSyntaxHighlightingEnabled
            ? runtime.CodeHighlighter
            : null;
        if (!ReferenceEquals(renderer.CodeHighlighter, codeHighlighter))
            renderer.CodeHighlighter = codeHighlighter;
        if (renderer.IsCodeBlockSyntaxHighlightingEnabled != settings.IsSyntaxHighlightingEnabled)
            renderer.IsCodeBlockSyntaxHighlightingEnabled = settings.IsSyntaxHighlightingEnabled;

        if (!ReferenceEquals(renderer.ImageResolver, settings.ImageResolver))
            renderer.ImageResolver = settings.ImageResolver;
        IMarkdownSvgRenderer svgRenderer = runtime.SvgRenderer;
        if (!ReferenceEquals(renderer.SvgRenderer, svgRenderer))
            renderer.SvgRenderer = svgRenderer;
        MarkdownPerformanceSession performanceSession = GetSharedPerformanceSession(runtime);
        if (!ReferenceEquals(renderer.PerformanceSession, performanceSession))
            renderer.PerformanceSession = performanceSession;
        if (!Equals(renderer.ImageBaseUri, settings.ImageBaseUri))
            renderer.ImageBaseUri = settings.ImageBaseUri;
        if (!string.Equals(renderer.ImageDocumentPath, settings.DocumentPath, StringComparison.Ordinal))
            renderer.ImageDocumentPath = settings.DocumentPath;
        if (!Equals(renderer.ImageDocumentSource, settings.DocumentSource))
            renderer.ImageDocumentSource = settings.DocumentSource;
        if (renderer.AllowThirdPartyRemoteImages != settings.AllowThirdPartyRemoteImages)
            renderer.AllowThirdPartyRemoteImages = settings.AllowThirdPartyRemoteImages;

        renderer.Markdown = markdown;
    }
}
