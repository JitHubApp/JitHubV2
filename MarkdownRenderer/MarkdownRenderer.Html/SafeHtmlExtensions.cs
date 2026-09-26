using Markdig.Syntax;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Html.Renderers;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Html;

/// <summary>Configures the native, non-executable safe-HTML feature pack.</summary>
public static class SafeHtmlExtensions
{
    private const string SafeHtmlPresentationFeature = "MarkdownRenderer.SafeHtml.Presentation";

    /// <summary>
    /// Adds native safe-HTML presentation to an immutable engine builder. Pair
    /// this with a profile that preserves HTML nodes, normally GitHub README.
    /// </summary>
    public static MarkdownEngineBuilder UseSafeHtml(
        this MarkdownEngineBuilder builder,
        SafeHtmlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ConfigurePresentation(
            static () => new MarkdownExtensionRegistry(),
            registry => registry.ConfigureSafeHtmlRegistry(options));
    }

    /// <summary>
    /// Adds safe-HTML rendering to a viewport-owning view. The view's engine must
    /// use a profile that preserves HTML nodes, such as
    /// <see cref="MarkdownProfiles.GitHubReadme"/>; strict profiles remain literal.
    /// </summary>
    public static MarkdownScrollView UseSafeHtml(
        this MarkdownScrollView view,
        SafeHtmlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        view.ExtensionRegistry = CreateSafeHtmlRegistry(
            view.ExtensionRegistry,
            view.Engine?.PresentationConfiguration as MarkdownExtensionRegistry,
            options);
        return view;
    }

    /// <summary>
    /// Adds safe-HTML rendering to an ancestor-viewport view. The view's engine
    /// must use a profile that preserves HTML nodes; strict profiles remain literal.
    /// </summary>
    public static MarkdownDocumentView UseSafeHtml(
        this MarkdownDocumentView view,
        SafeHtmlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        view.ExtensionRegistry = CreateSafeHtmlRegistry(
            view.ExtensionRegistry,
            view.Engine?.PresentationConfiguration as MarkdownExtensionRegistry,
            options);
        return view;
    }

    /// <summary>
    /// Adds native safe-HTML rendering to a control builder. Pair this with an
    /// engine profile that preserves HTML nodes, normally the GitHub README profile.
    /// </summary>
    public static MarkdownRendererControlBuilder UseSafeHtml(
        this MarkdownRendererControlBuilder builder,
        SafeHtmlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.ConfigureEnginePresentation(
            registry => registry.ConfigureSafeHtmlRegistry(options));
    }

    internal static MarkdownExtensionRegistry ConfigureSafeHtmlRegistry(
        this MarkdownExtensionRegistry registry,
        SafeHtmlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        SafeHtmlOptions effectiveOptions = options ?? SafeHtmlOptions.Default;
        registry.ConfigureSafeHtmlPolicy(new SafeHtmlRenderPolicy(
            effectiveOptions.EnableLinks,
            effectiveOptions.EnableImages,
            effectiveOptions.UnknownElementBehavior == SafeHtmlUnknownElementBehavior.RenderLiteral,
            effectiveOptions.AllowedStyleClasses,
            new SafeHtmlParseLimits(
                effectiveOptions.Budgets.MaxInputLength,
                effectiveOptions.Budgets.MaxNodeCount,
                effectiveOptions.Budgets.MaxNestingDepth,
                effectiveOptions.Budgets.MaxAttributeCount,
                effectiveOptions.Budgets.MaxAttributeValueLength,
                effectiveOptions.Budgets.MaxTagLength)));
        registry.RegisterOrRefreshFeatureRenderer<HtmlBlock, HtmlBlockRenderer>(
            new HtmlBlockRenderer(effectiveOptions));
        registry.AddPresentationFeature(SafeHtmlPresentationFeature);
        return registry;
    }

    internal static bool HasSafeHtmlPresentation(this MarkdownExtensionRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.HasPresentationFeature(SafeHtmlPresentationFeature);
    }

    private static MarkdownExtensionRegistry CreateSafeHtmlRegistry(
        MarkdownExtensionRegistry? viewRegistry,
        MarkdownExtensionRegistry? engineRegistry,
        SafeHtmlOptions? options)
    {
        MarkdownExtensionRegistry registry = viewRegistry ?? engineRegistry ?? new MarkdownExtensionRegistry();
        return registry
            .CreateMutableCopy()
            .ConfigureSafeHtmlRegistry(options)
            .Freeze();
    }
}
