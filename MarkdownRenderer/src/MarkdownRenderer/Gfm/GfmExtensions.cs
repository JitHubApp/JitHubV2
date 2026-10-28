using Markdig;
using System;
using System.Linq;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Figures;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Hosting;
using MarkdownRenderer.Gfm.Renderers;
using MarkdownRenderer.Parsing;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.Gfm;

/// <summary>
/// Stable configuration entry points for strict GitHub Flavored Markdown.
/// </summary>
public static class GfmExtensions
{
    private const string GfmPresentationFeature = "MarkdownRenderer.Gfm.Presentation";
    private const string MarkdownExtraPresentationFeature = "MarkdownRenderer.MarkdownExtra.Presentation";

    /// <summary>Configures an immutable engine for the GFM 0.29 profile.</summary>
    public static MarkdownEngineBuilder UseGitHubFlavoredMarkdown(this MarkdownEngineBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .AddProfile(MarkdownProfiles.GfmStrict)
            .ConfigurePresentation(
                static () => new MarkdownExtensionRegistry(),
                static registry => registry.ConfigureGfmRegistry());
    }

    /// <summary>Configures a viewport-owning view with strict-GFM parsing and native renderers.</summary>
    public static MarkdownScrollView UseGitHubFlavoredMarkdown(
        this MarkdownScrollView view,
        MarkdownEngine? engine = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        EngineSelection selection = SelectGfmEngine(view.Engine, engine);
        ConfigureGfmView(view, selection);
        return view;
    }

    /// <summary>Configures an ancestor-viewport view with strict-GFM parsing and native renderers.</summary>
    public static MarkdownDocumentView UseGitHubFlavoredMarkdown(
        this MarkdownDocumentView view,
        MarkdownEngine? engine = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        EngineSelection selection = SelectGfmEngine(view.Engine, engine);
        ConfigureGfmView(view, selection);
        return view;
    }

    /// <summary>
    /// Adds the extensions defined by the GFM 0.29 specification to a registry.
    /// GitHub README additions such as alerts, footnotes, emoji shortcodes, generic
    /// attributes, and safe HTML belong to the <c>MarkdownRenderer.GitHub</c> package.
    /// </summary>
    /// <param name="registry">Registry to configure.</param>
    /// <returns>The same registry for fluent chaining.</returns>
    internal static MarkdownExtensionRegistry ConfigureGfmRegistry(this MarkdownExtensionRegistry registry)
    {
        if (registry is null) throw new System.ArgumentNullException(nameof(registry));
        if (registry.HasPresentationFeature(GfmPresentationFeature))
            return registry;

        registry.ConfigurePipeline(p =>
        {
            p.UsePipeTables();
            p.UseTaskLists();
            p.UseAutoLinks();
            p.UseEmphasisExtras(EmphasisExtraOptions.Strikethrough);
        });

        registry.RegisterRendererIfAbsent<Table>(new TableRenderer());
        registry.RegisterRendererIfAbsent<ListItemBlock>(new TaskListItemRenderer());
        registry.AddPresentationFeature(GfmPresentationFeature);

        return registry;
    }

    private static void ConfigureGfmView(object view, EngineSelection selection)
    {
        ArgumentNullException.ThrowIfNull(view);
        MarkdownEngine engine = selection.Engine;
        ArgumentNullException.ThrowIfNull(engine);
        if (!engine.Profile.Features.Contains("pipe-tables", StringComparer.Ordinal) ||
            !engine.Profile.Features.Contains("task-lists", StringComparer.Ordinal))
        {
            throw new ArgumentException("The engine must enable the strict-GFM profile.", nameof(engine));
        }

        switch (view)
        {
            case MarkdownScrollView scrollView:
                AssignEngine(scrollView, selection);
                scrollView.ExtensionRegistry = GetGfmRegistry(engine, scrollView.ExtensionRegistry);
                break;
            case MarkdownDocumentView documentView:
                AssignEngine(documentView, selection);
                documentView.ExtensionRegistry = GetGfmRegistry(engine, documentView.ExtensionRegistry);
                break;
            default:
                throw new ArgumentException("The value must be a markdown view.", nameof(view));
        }
    }

    /// <summary>
    /// Configures a control builder to use GitHub-flavored markdown.
    /// </summary>
    /// <param name="builder">Builder to configure.</param>
    /// <returns>The same builder for fluent chaining.</returns>
    public static MarkdownRendererControlBuilder UseGitHubFlavoredMarkdown(this MarkdownRendererControlBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddProfileWithPresentation(
            MarkdownProfiles.GfmStrict,
            static registry => registry.ConfigureGfmRegistry());
    }

    /// <summary>Adds the Markdown Extra profile to an immutable engine builder.</summary>
    public static MarkdownEngineBuilder UseMarkdownExtra(this MarkdownEngineBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder
            .AddProfile(MarkdownProfiles.MarkdownExtra)
            .ConfigurePresentation(
                static () => new MarkdownExtensionRegistry(),
                static registry => registry.ConfigureMarkdownExtraRegistry());
    }

    /// <summary>Adds Markdown Extra rendering to a viewport-owning view.</summary>
    public static MarkdownScrollView UseMarkdownExtra(
        this MarkdownScrollView view,
        MarkdownEngine? engine = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        EngineSelection selection = SelectMarkdownExtraEngine(view.Engine, engine);
        ConfigureMarkdownExtraView(view, selection);
        return view;
    }

    /// <summary>Adds Markdown Extra rendering to an ancestor-viewport view.</summary>
    public static MarkdownDocumentView UseMarkdownExtra(
        this MarkdownDocumentView view,
        MarkdownEngine? engine = null)
    {
        ArgumentNullException.ThrowIfNull(view);
        EngineSelection selection = SelectMarkdownExtraEngine(view.Engine, engine);
        ConfigureMarkdownExtraView(view, selection);
        return view;
    }

    /// <summary>
    /// Adds Markdown Extra style features that are not part of GitHub-flavored markdown:
    /// definition lists, abbreviations, and figure/caption blocks.
    /// </summary>
    /// <param name="registry">Registry to configure.</param>
    /// <returns>The same registry for fluent chaining.</returns>
    internal static MarkdownExtensionRegistry ConfigureMarkdownExtraRegistry(this MarkdownExtensionRegistry registry)
    {
        if (registry is null) throw new System.ArgumentNullException(nameof(registry));
        if (registry.HasPresentationFeature(MarkdownExtraPresentationFeature))
            return registry;

        registry.ConfigurePipeline(p =>
        {
            p.UseDefinitionLists();
            p.UseAbbreviations();
            p.UseFigures();
        });

        registry.RegisterRendererIfAbsent<DefinitionList>(new DefinitionListRenderer());
        registry.RegisterRendererIfAbsent<Figure>(new FigureRenderer());
        registry.AddPresentationFeature(MarkdownExtraPresentationFeature);

        return registry;
    }

    private static void ConfigureMarkdownExtraView(object view, EngineSelection selection)
    {
        MarkdownEngine engine = selection.Engine;
        if (!engine.Profile.Features.Contains("definition-lists", StringComparer.Ordinal) ||
            !engine.Profile.Features.Contains("abbreviations", StringComparer.Ordinal))
        {
            throw new ArgumentException("The engine must enable the MarkdownExtra profile.", nameof(engine));
        }

        switch (view)
        {
            case MarkdownScrollView scrollView:
                AssignEngine(scrollView, selection);
                scrollView.ExtensionRegistry = GetMarkdownExtraRegistry(engine, scrollView.ExtensionRegistry);
                break;
            case MarkdownDocumentView documentView:
                AssignEngine(documentView, selection);
                documentView.ExtensionRegistry = GetMarkdownExtraRegistry(engine, documentView.ExtensionRegistry);
                break;
            default:
                throw new ArgumentException("The value must be a markdown view.", nameof(view));
        }
    }

    private static EngineSelection SelectGfmEngine(
        MarkdownEngine? current,
        MarkdownEngine? requested)
    {
        if (requested is not null)
            return new EngineSelection(requested, IsOwned: false);
        if (current is null || ReferenceEquals(current, MarkdownEngine.Default))
            return new EngineSelection(GfmMarkdownRenderer.SharedEngine, IsOwned: false);
        if (HasGfmProfile(current))
            return new EngineSelection(current, IsOwned: false);

        return new EngineSelection(
            current.ToBuilder()
                .UseGitHubFlavoredMarkdown()
                .Build(),
            IsOwned: true);
    }

    private static EngineSelection SelectMarkdownExtraEngine(
        MarkdownEngine? current,
        MarkdownEngine? requested)
    {
        if (requested is not null)
            return new EngineSelection(requested, IsOwned: false);

        MarkdownEngine source = current ?? MarkdownEngine.Default;
        if (HasMarkdownExtraProfile(source))
            return new EngineSelection(source, IsOwned: false);

        return new EngineSelection(
            source.ToBuilder()
                .UseMarkdownExtra()
                .Build(),
            IsOwned: true);
    }

    private static bool HasGfmProfile(MarkdownEngine engine) =>
        engine.Profile.Features.Contains("pipe-tables", StringComparer.Ordinal) &&
        engine.Profile.Features.Contains("task-lists", StringComparer.Ordinal);

    private static bool HasMarkdownExtraProfile(MarkdownEngine engine) =>
        engine.Profile.Features.Contains("definition-lists", StringComparer.Ordinal) &&
        engine.Profile.Features.Contains("abbreviations", StringComparer.Ordinal);

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

    /// <summary>
    /// Configures a control builder to use Markdown Extra style features that are
    /// intentionally kept separate from the strict GFM helper.
    /// </summary>
    /// <param name="builder">Builder to configure.</param>
    /// <returns>The same builder for fluent chaining.</returns>
    public static MarkdownRendererControlBuilder UseMarkdownExtra(this MarkdownRendererControlBuilder builder)
    {
        if (builder is null) throw new System.ArgumentNullException(nameof(builder));
        return builder.AddProfileWithPresentation(
            MarkdownProfiles.MarkdownExtra,
            static registry => registry.ConfigureMarkdownExtraRegistry());
    }

    internal static MarkdownExtensionRegistry GetGfmRegistry(
        MarkdownEngine engine,
        MarkdownExtensionRegistry? viewRegistry)
    {
        MarkdownExtensionRegistry? engineRegistry =
            engine.PresentationConfiguration as MarkdownExtensionRegistry;
        if (viewRegistry is not null)
        {
            return engineRegistry?.HasPresentationFeature(GfmPresentationFeature) == true ||
                   viewRegistry.HasPresentationFeature(GfmPresentationFeature)
                ? viewRegistry
                : viewRegistry.CreateMutableCopy().ConfigureGfmRegistry().Freeze();
        }
        if (engineRegistry?.HasPresentationFeature(GfmPresentationFeature) == true)
            return engineRegistry;

        MarkdownExtensionRegistry mutable =
            engineRegistry?.CreateMutableCopy() ?? new MarkdownExtensionRegistry();
        return mutable.ConfigureGfmRegistry().Freeze();
    }

    internal static MarkdownExtensionRegistry GetMarkdownExtraRegistry(
        MarkdownEngine engine,
        MarkdownExtensionRegistry? viewRegistry)
    {
        MarkdownExtensionRegistry? engineRegistry =
            engine.PresentationConfiguration as MarkdownExtensionRegistry;
        if (viewRegistry is not null)
        {
            return engineRegistry?.HasPresentationFeature(MarkdownExtraPresentationFeature) == true ||
                   viewRegistry.HasPresentationFeature(MarkdownExtraPresentationFeature)
                ? viewRegistry
                : viewRegistry.CreateMutableCopy().ConfigureMarkdownExtraRegistry().Freeze();
        }
        if (engineRegistry?.HasPresentationFeature(MarkdownExtraPresentationFeature) == true)
            return engineRegistry;

        MarkdownExtensionRegistry mutable =
            engineRegistry?.CreateMutableCopy() ?? new MarkdownExtensionRegistry();
        return mutable.ConfigureMarkdownExtraRegistry().Freeze();
    }
}
