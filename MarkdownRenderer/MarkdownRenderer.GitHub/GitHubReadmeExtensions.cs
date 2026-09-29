using System;
using System.Linq;
using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Parsers;
using Markdig.Syntax;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.Gfm.Renderers;
using MarkdownRenderer.Html;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.GitHub;

/// <summary>
/// Composes the strict GFM profile with the additional syntax and native rendering
/// behavior commonly required by GitHub README documents.
/// </summary>
public static class GitHubReadmeExtensions
{
    private const string GitHubReadmePresentationFeature = "MarkdownRenderer.GitHubReadme.Presentation";
    private const string GitHubMarkdownContainersFeature =
        "MarkdownRenderer.GitHub.MarkdownInHtmlContainers";

    /// <summary>Configures an immutable engine for GitHub README documents.</summary>
    public static MarkdownEngineBuilder UseGitHubReadme(this MarkdownEngineBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return ConfigureGitHubReadme(builder, safeHtmlOptions: null);
    }

    /// <summary>
    /// Configures an immutable engine for GitHub README documents with a
    /// host-lowered safe-HTML policy.
    /// </summary>
    public static MarkdownEngineBuilder UseGitHubReadme(
        this MarkdownEngineBuilder builder,
        SafeHtmlOptions safeHtmlOptions)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(safeHtmlOptions);
        return ConfigureGitHubReadme(builder, safeHtmlOptions);
    }

    /// <summary>Configures a viewport-owning view for GitHub README documents.</summary>
    public static MarkdownScrollView UseGitHubReadme(
        this MarkdownScrollView view,
        MarkdownEngine? engine = null,
        SafeHtmlOptions? safeHtmlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        EngineSelection selection = SelectGitHubEngine(view.Engine, engine);
        ConfigureGitHubReadmeView(
            view,
            selection,
            safeHtmlOptions);
        return view;
    }

    /// <summary>Configures an ancestor-viewport view for GitHub README documents.</summary>
    public static MarkdownDocumentView UseGitHubReadme(
        this MarkdownDocumentView view,
        MarkdownEngine? engine = null,
        SafeHtmlOptions? safeHtmlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        EngineSelection selection = SelectGitHubEngine(view.Engine, engine);
        ConfigureGitHubReadmeView(
            view,
            selection,
            safeHtmlOptions);
        return view;
    }

    /// <summary>
    /// Adds strict GFM, GitHub alerts, footnotes, emoji shortcodes, generic
    /// attributes, and the renderer's native safe-HTML subset.
    /// </summary>
    /// <remarks>
    /// Safe HTML is parsed and painted natively. It does not execute scripts, apply
    /// CSS layout, expose a browser DOM, or perform direct filesystem/network access.
    /// </remarks>
    internal static MarkdownExtensionRegistry ConfigureGitHubReadmeRegistry(
        this MarkdownExtensionRegistry registry,
        SafeHtmlOptions? safeHtmlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        if (registry.HasPresentationFeature(GitHubReadmePresentationFeature))
        {
            if (safeHtmlOptions is not null)
                registry.ConfigureSafeHtmlRegistry(safeHtmlOptions);
            return registry;
        }

        registry.ConfigureGfmRegistry();
        registry.ConfigurePipeline(static pipeline =>
        {
            if (!pipeline.BlockParsers.Contains<GitHubDetailsBlockParser>())
            {
                pipeline.BlockParsers.InsertBefore<HtmlBlockParser>(new GitHubDetailsBlockParser());
            }
            pipeline.UseFootnotes();
            pipeline.UseEmojiAndSmiley();
            ConfigureGitHubImageEmojiParser(pipeline);
            pipeline.UseGenericAttributes();
        });

        registry.RegisterRendererIfAbsent<QuoteBlock>(new AlertRenderer());
        registry.RegisterRendererIfAbsent<FootnoteGroup>(new FootnoteRenderer());
        registry.RegisterRendererIfAbsent<GitHubDetailsBlock>(new GitHubDetailsBlockRenderer());
        registry.AddPresentationFeature(GitHubMarkdownContainersFeature);
        if (safeHtmlOptions is not null || !registry.HasSafeHtmlPresentation())
            registry.ConfigureSafeHtmlRegistry(safeHtmlOptions);
        registry.AddPresentationFeature(GitHubReadmePresentationFeature);

        return registry;
    }

    private static void ConfigureGitHubReadmeView(
        object view,
        EngineSelection selection,
        SafeHtmlOptions? safeHtmlOptions)
    {
        MarkdownEngine engine = selection.Engine;
        if (!engine.Profile.Features.Contains("alerts", StringComparer.Ordinal) ||
            !engine.Profile.Features.Contains("footnotes", StringComparer.Ordinal))
        {
            throw new ArgumentException("The engine must enable the GitHubReadme profile.", nameof(engine));
        }

        switch (view)
        {
            case MarkdownScrollView scrollView:
                AssignEngine(scrollView, selection);
                scrollView.ExtensionRegistry = GetGitHubReadmeRegistry(
                    engine,
                    scrollView.ExtensionRegistry,
                    safeHtmlOptions);
                break;
            case MarkdownDocumentView documentView:
                AssignEngine(documentView, selection);
                documentView.ExtensionRegistry = GetGitHubReadmeRegistry(
                    engine,
                    documentView.ExtensionRegistry,
                    safeHtmlOptions);
                break;
            default:
                throw new ArgumentException("The value must be a markdown view.", nameof(view));
        }
    }

    /// <summary>Configures a control builder with the GitHub README profile.</summary>
    public static MarkdownRendererControlBuilder UseGitHubReadme(
        this MarkdownRendererControlBuilder builder,
        SafeHtmlOptions? safeHtmlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddProfileWithPresentation(
            MarkdownProfiles.GitHubReadme,
            registry => registry.ConfigureGitHubReadmeRegistry(safeHtmlOptions),
            ConfigureGitHubImageEmojiParser);
    }

    private static MarkdownEngineBuilder ConfigureGitHubReadme(
        MarkdownEngineBuilder builder,
        SafeHtmlOptions? safeHtmlOptions)
        => builder
            .AddProfile(MarkdownProfiles.GitHubReadme)
            .ConfigureParser(ConfigureGitHubImageEmojiParser)
            .ConfigurePresentation(
                static () => new MarkdownExtensionRegistry(),
                registry => registry.ConfigureGitHubReadmeRegistry(safeHtmlOptions));

    private static void ConfigureGitHubImageEmojiParser(MarkdownPipelineBuilder pipeline)
    {
        if (!pipeline.Extensions.Contains<GitHubImageEmojiExtension>())
            pipeline.Extensions.Add(new GitHubImageEmojiExtension());
    }

    private static EngineSelection SelectGitHubEngine(
        MarkdownEngine? current,
        MarkdownEngine? requested)
    {
        if (requested is not null)
            return new EngineSelection(requested, IsOwned: false);
        if (current is null ||
            ReferenceEquals(current, MarkdownEngine.Default) ||
            ReferenceEquals(current, GfmMarkdownRenderer.SharedEngine))
        {
            return new EngineSelection(GitHubReadmeMarkdownRenderer.SharedEngine, IsOwned: false);
        }

        if (HasGitHubProfile(current))
            return new EngineSelection(current, IsOwned: false);

        return new EngineSelection(
            current.ToBuilder()
                .UseGitHubReadme()
                .Build(),
            IsOwned: true);
    }

    private static bool HasGitHubProfile(MarkdownEngine engine) =>
        engine.Profile.Features.Contains("alerts", StringComparer.Ordinal) &&
        engine.Profile.Features.Contains("footnotes", StringComparer.Ordinal);

    private static void AssignEngine(MarkdownScrollView view, EngineSelection selection)
    {
        if (selection.IsOwned)
            view.SetOwnedEngine(selection.Engine);
        else if (!ReferenceEquals(view.Engine, selection.Engine))
            view.Engine = selection.Engine;
    }

    private static void AssignEngine(MarkdownDocumentView view, EngineSelection selection)
    {
        if (selection.IsOwned)
            view.SetOwnedEngine(selection.Engine);
        else if (!ReferenceEquals(view.Engine, selection.Engine))
            view.Engine = selection.Engine;
    }

    private readonly record struct EngineSelection(MarkdownEngine Engine, bool IsOwned);

    internal static MarkdownExtensionRegistry GetGitHubReadmeRegistry(
        MarkdownEngine engine,
        MarkdownExtensionRegistry? viewRegistry,
        SafeHtmlOptions? safeHtmlOptions)
    {
        MarkdownExtensionRegistry? engineRegistry =
            engine.PresentationConfiguration as MarkdownExtensionRegistry;
        if (viewRegistry is not null)
        {
            if (engineRegistry?.HasPresentationFeature(GitHubReadmePresentationFeature) == true)
            {
                return safeHtmlOptions is null
                    ? viewRegistry
                    : viewRegistry.CreateMutableCopy()
                        .ConfigureSafeHtmlRegistry(safeHtmlOptions)
                        .Freeze();
            }

            if (viewRegistry.HasPresentationFeature(GitHubReadmePresentationFeature))
                return safeHtmlOptions is null
                    ? viewRegistry
                    : viewRegistry.CreateMutableCopy()
                        .ConfigureSafeHtmlRegistry(safeHtmlOptions)
                        .Freeze();

            return viewRegistry
                .CreateMutableCopy()
                .ConfigureGitHubReadmeRegistry(safeHtmlOptions)
                .Freeze();
        }

        if (engineRegistry?.HasPresentationFeature(GitHubReadmePresentationFeature) == true &&
            safeHtmlOptions is null)
        {
            return engineRegistry;
        }

        MarkdownExtensionRegistry mutable =
            engineRegistry?.CreateMutableCopy() ?? new MarkdownExtensionRegistry();
        return mutable.ConfigureGitHubReadmeRegistry(safeHtmlOptions).Freeze();
    }
}
