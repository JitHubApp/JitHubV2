using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using MarkdownRenderer.Controls;
using MarkdownRenderer.Gfm;
using MarkdownRenderer.GitHub;
using MarkdownRenderer.Html;
using MarkdownRenderer.Parsing;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

public sealed class EnginePresentationConfigurationTests
{
    [Fact]
    public void EngineFreezesAndRetainsPresentationConfiguration()
    {
        int pipelineConfigurationCalls = 0;
        var builder = new MarkdownEngineBuilder();
        builder.ConfigurePresentation(
            static () => new MarkdownExtensionRegistry(),
            registry => registry.ConfigurePipeline(_ => pipelineConfigurationCalls++));

        using MarkdownEngine engine = builder.Build();
        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);

        Assert.True(registry.IsFrozen);
        Assert.Equal(1, pipelineConfigurationCalls);
        Assert.Same(registry.BuildPipeline(), registry.BuildPipeline());
        Assert.Throws<InvalidOperationException>(() => registry.ConfigurePipeline(static _ => { }));

        using MarkdownEngine derived = engine.ToBuilder().Build();
        Assert.Same(engine.PresentationConfiguration, derived.PresentationConfiguration);
        Assert.Equal(engine.Profile, derived.Profile);
        Assert.Same(engine.ParseLimits, derived.ParseLimits);
    }

    [Fact]
    public void ConfiguringAnIncludedSnapshotUsesAMutableCopy()
    {
        var firstBuilder = new MarkdownEngineBuilder();
        firstBuilder.ConfigurePresentation(
            static () => new MarkdownExtensionRegistry(),
            static registry => registry.ConfigurePipeline(static _ => { }));
        MarkdownEngine first = firstBuilder.Build();

        var secondBuilder = new MarkdownEngineBuilder()
            .IncludePresentation(first.PresentationConfiguration);
        secondBuilder.ConfigurePresentation(
            static () => new MarkdownExtensionRegistry(),
            static registry => registry.ConfigurePipeline(static _ => { }));
        MarkdownEngine second = secondBuilder.Build();

        Assert.NotSame(first.PresentationConfiguration, second.PresentationConfiguration);
        Assert.True(Assert.IsType<MarkdownExtensionRegistry>(second.PresentationConfiguration).IsFrozen);
    }

    [Fact]
    public async Task FeatureHelpersAttachFrozenPresentationToEngineAndDocument()
    {
        var options = new SafeHtmlOptions(
            budgets: new SafeHtmlBudgets(maxNodeCount: 17),
            enableLinks: false,
            enableImages: false);
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme(options)
            .UseMarkdownExtra()
            .Build();

        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        MarkdownRenderer.Document.MarkdownDocument document = await engine.ParseAsync(
            "| A |\n|---|\n| 1 |\n\nTerm\n: definition\n\n<div>safe</div>");

        Assert.True(registry.IsFrozen);
        Assert.Same(registry, document.PresentationConfiguration);
        Assert.True(registry.TryGetRenderer(typeof(Table), out _));
        Assert.True(registry.TryGetRenderer(typeof(DefinitionList), out _));
        Assert.True(registry.TryGetRenderer(typeof(HtmlBlock), out _));
        SafeHtmlRenderPolicy policy = Assert.IsType<SafeHtmlRenderPolicy>(registry.SafeHtmlPolicy);
        Assert.False(policy.EnableLinks);
        Assert.False(policy.EnableImages);
        Assert.Equal(17, policy.Limits.MaxNodeCount);
    }

    [Fact]
    public async Task SharedFeatureEnginesCannotBePoisonedByDispose()
    {
        GfmMarkdownRenderer.SharedEngine.Dispose();
        GitHubReadmeMarkdownRenderer.SharedEngine.Dispose();

        Assert.NotNull(await GfmMarkdownRenderer.SharedEngine.ParseAsync("| A |\n|---|\n| 1 |"));
        Assert.NotNull(await GitHubReadmeMarkdownRenderer.SharedEngine.ParseAsync("> [!NOTE]\n> body"));
    }

    [Fact]
    public void BareViewResolutionReusesEnginePresentationSnapshot()
    {
        MarkdownEngine engine = new MarkdownEngineBuilder().UseGitHubReadme().Build();

        MarkdownExtensionRegistry registry = GitHubReadmeExtensions.GetGitHubReadmeRegistry(
            engine,
            viewRegistry: null,
            safeHtmlOptions: null);

        Assert.Same(engine.PresentationConfiguration, registry);
    }

    [Fact]
    public void ControlBuilderFreezesSafeHtmlOptionsIntoItsEngine()
    {
        var options = new SafeHtmlOptions(enableLinks: false, enableImages: false);

        var builder = new MarkdownRendererControlBuilder().UseGitHubReadme(options);
        using MarkdownEngine engine = GetConfiguredEngine(builder);

        var registry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration);
        SafeHtmlRenderPolicy policy = Assert.IsType<SafeHtmlRenderPolicy>(registry.SafeHtmlPolicy);
        Assert.False(policy.EnableLinks);
        Assert.False(policy.EnableImages);
    }

    [Fact]
    public void ControlBuilderProfileDerivationPreservesParseLimitsAndPresentation()
    {
        var limits = new MarkdownParseLimits(
            maximumSourceLength: 100,
            maximumConcurrentParseCount: 1,
            maximumOutstandingParseCount: 2,
            maximumOutstandingSourceBytes: 200);
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .WithParseCacheBudgetBytes(1234)
            .WithParseLimits(limits)
            .UseSafeHtml(new SafeHtmlOptions(enableImages: false))
            .Build();

        var builder = new MarkdownRendererControlBuilder()
            .WithEngine(engine)
            .UseGitHubFlavoredMarkdown();
        using MarkdownEngine configured = GetConfiguredEngine(builder);

        Assert.Same(limits, configured.ParseLimits);
        Assert.Equal(1234, configured.ParseCacheBudgetBytes);
        var registry = Assert.IsType<MarkdownExtensionRegistry>(configured.PresentationConfiguration);
        Assert.True(registry.TryGetRenderer(typeof(Table), out _));
        Assert.True(registry.TryGetRenderer(typeof(HtmlBlock), out _));
    }

    [Fact]
    public void ProfileOnlyEngineGetsRequiredFrozenViewPresentation()
    {
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict)
            .Build();

        MarkdownExtensionRegistry registry = GfmExtensions.GetGfmRegistry(
            engine,
            viewRegistry: null);

        Assert.True(registry.IsFrozen);
        Assert.True(registry.TryGetRenderer(typeof(Table), out _));
        Assert.True(registry.TryGetRenderer(typeof(ListItemBlock), out _));
    }

    [Fact]
    public void GitHubProfileOnlyEngineGetsFullReadmePresentation()
    {
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GitHubReadme)
            .Build();

        MarkdownExtensionRegistry registry = GitHubReadmeExtensions.GetGitHubReadmeRegistry(
            engine,
            viewRegistry: null,
            safeHtmlOptions: null);

        Assert.True(registry.TryGetRenderer(typeof(Table), out _));
        Assert.True(registry.TryGetRenderer(typeof(QuoteBlock), out _));
        Assert.True(registry.TryGetRenderer(typeof(HtmlBlock), out _));
    }

    [Fact]
    public void ChainedViewHelpersComposePresentationForCompositeProfileOnlyEngine()
    {
        MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseProfile(MarkdownProfiles.GfmStrict.Combine(MarkdownProfiles.MarkdownExtra))
            .Build();

        MarkdownExtensionRegistry gfm = GfmExtensions.GetGfmRegistry(
            engine,
            viewRegistry: null);
        MarkdownExtensionRegistry registry = GfmExtensions.GetMarkdownExtraRegistry(
            engine,
            gfm);

        Assert.True(registry.IsFrozen);
        Assert.True(registry.TryGetRenderer(typeof(Table), out _));
        Assert.True(registry.TryGetRenderer(typeof(DefinitionList), out _));
    }

    [Fact]
    public void PresentationFeatureRegistrationIsIdempotent()
    {
        var registry = new MarkdownExtensionRegistry();
        registry.ConfigureGfmRegistry();
        int revision = registry.Revision;

        registry.ConfigureGfmRegistry();

        Assert.Equal(revision, registry.Revision);
    }

    [Fact]
    public void EngineFeaturesAndIndependentViewOverridesComposeExactlyOnce()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        var customTableRenderer = new CustomTableRenderer();
        MarkdownExtensionRegistry viewOverrides = new MarkdownExtensionRegistry()
            .ConfigureMarkdownExtraRegistry()
            .RegisterRenderer<Table>(customTableRenderer)
            .Freeze();

        MarkdownExtensionRegistry afterGitHubHelper =
            GitHubReadmeExtensions.GetGitHubReadmeRegistry(
                engine,
                viewOverrides,
                safeHtmlOptions: null);
        MarkdownExtensionRegistry composed = MarkdownExtensionRegistry.ComposePresentation(
            Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration),
            afterGitHubHelper);

        Assert.Same(viewOverrides, afterGitHubHelper);
        Assert.True(composed.TryGetRenderer(typeof(Table), out var renderer));
        Assert.Same(customTableRenderer, renderer);
        Assert.All(
            composed.BuildPipeline().Extensions.GroupBy(static extension => extension.GetType()),
            static group => Assert.Single(group));
    }

    [Fact]
    public void SafeHtmlOptionRefreshPreservesAnExplicitViewRenderer()
    {
        using MarkdownEngine engine = new MarkdownEngineBuilder()
            .UseGitHubReadme()
            .Build();
        var customHtmlRenderer = new CustomHtmlRenderer();
        var viewRegistry = Assert.IsType<MarkdownExtensionRegistry>(engine.PresentationConfiguration)
            .CreateMutableCopy()
            .RegisterRenderer<HtmlBlock>(customHtmlRenderer)
            .Freeze();

        MarkdownExtensionRegistry configured = GitHubReadmeExtensions.GetGitHubReadmeRegistry(
            engine,
            viewRegistry,
            new SafeHtmlOptions(enableLinks: false, enableImages: false));

        Assert.True(configured.TryGetRenderer(typeof(HtmlBlock), out var renderer));
        Assert.Same(customHtmlRenderer, renderer);
        SafeHtmlRenderPolicy policy = Assert.IsType<SafeHtmlRenderPolicy>(configured.SafeHtmlPolicy);
        Assert.False(policy.EnableLinks);
        Assert.False(policy.EnableImages);
    }

    [Fact]
    public void GitHubAndMarkdownExtraEngineHelpersAreOrderIndependent()
    {
        using MarkdownEngine first = new MarkdownEngineBuilder()
            .UseGitHubReadme(new SafeHtmlOptions(enableLinks: false))
            .UseMarkdownExtra()
            .Build();
        using MarkdownEngine second = new MarkdownEngineBuilder()
            .UseMarkdownExtra()
            .UseGitHubReadme(new SafeHtmlOptions(enableLinks: false))
            .Build();

        var firstRegistry = Assert.IsType<MarkdownExtensionRegistry>(first.PresentationConfiguration);
        var secondRegistry = Assert.IsType<MarkdownExtensionRegistry>(second.PresentationConfiguration);
        foreach (Type nodeType in new[] { typeof(Table), typeof(DefinitionList), typeof(QuoteBlock), typeof(HtmlBlock) })
        {
            Assert.True(firstRegistry.TryGetRenderer(nodeType, out _));
            Assert.True(secondRegistry.TryGetRenderer(nodeType, out _));
        }

        Assert.False(Assert.IsType<SafeHtmlRenderPolicy>(firstRegistry.SafeHtmlPolicy).EnableLinks);
        Assert.False(Assert.IsType<SafeHtmlRenderPolicy>(secondRegistry.SafeHtmlPolicy).EnableLinks);
        Assert.Equal(
            first.Profile.Features.OrderBy(static value => value, StringComparer.Ordinal),
            second.Profile.Features.OrderBy(static value => value, StringComparer.Ordinal));
    }

    private static MarkdownEngine GetConfiguredEngine(MarkdownRendererControlBuilder builder)
        => builder.CreateEngineSnapshotForTesting();

    private sealed class CustomTableRenderer : MarkdownNodeRenderer<Table>
    {
        public override MarkdownRenderer.Layout.BlockBox? BuildBlock(
            Table node,
            MarkdownRenderer.Layout.MarkdownLayoutContext context) => null;
    }

    private sealed class CustomHtmlRenderer : MarkdownNodeRenderer<HtmlBlock>
    {
        public override MarkdownRenderer.Layout.BlockBox? BuildBlock(
            HtmlBlock node,
            MarkdownRenderer.Layout.MarkdownLayoutContext context) => null;
    }
}
