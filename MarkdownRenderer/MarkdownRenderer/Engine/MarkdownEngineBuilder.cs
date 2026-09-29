using System;
using System.Collections.Generic;
using MarkdownRenderer.Extensions;
using MarkdownPipelineBuilder = Markdig.MarkdownPipelineBuilder;

namespace MarkdownRenderer;

/// <summary>Builds immutable <see cref="MarkdownEngine"/> instances.</summary>
public sealed class MarkdownEngineBuilder
{
    private readonly MarkdownExtensionBuilder _extensions = new();
    private readonly List<Action<MarkdownPipelineBuilder>> _pipelineConfigurations = [];
    private long _parseCacheBudgetBytes = MarkdownEngine.DefaultParseCacheBudgetBytes;
    private MarkdownParseLimits _parseLimits = MarkdownParseLimits.Default;
    private MarkdownProfile _profile = MarkdownProfiles.CommonMark;
    private IMarkdownPresentationConfiguration? _presentationConfiguration;

    /// <summary>Creates a builder with the default CommonMark configuration.</summary>
    public MarkdownEngineBuilder()
    {
    }

    /// <summary>
    /// Creates a reusable derivation of an existing engine configuration,
    /// preserving its profile, extensions, admission/cache limits, and opaque
    /// presentation snapshot.
    /// </summary>
    public MarkdownEngineBuilder(MarkdownEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _profile = engine.Profile;
        _extensions.Include(engine.Extensions);
        _pipelineConfigurations.AddRange(engine.PipelineConfigurations);
        _parseCacheBudgetBytes = engine.ParseCacheBudgetBytes;
        _parseLimits = engine.ParseLimits;
        _presentationConfiguration = engine.PresentationConfiguration;
    }

    /// <summary>
    /// Replaces the parsing profile. The default is <see cref="MarkdownProfiles.CommonMark"/>.
    /// </summary>
    public MarkdownEngineBuilder UseProfile(MarkdownProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = profile;
        return this;
    }

    /// <summary>
    /// Adds the syntax features from <paramref name="profile"/> to the current
    /// profile. This supports combinations such as strict GFM plus Markdown Extra.
    /// </summary>
    public MarkdownEngineBuilder AddProfile(MarkdownProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        _profile = _profile.Combine(profile);
        return this;
    }

    /// <summary>Adds a parser-independent declarative extension.</summary>
    /// <remarks>
    /// The extension and any state captured by its registrations are borrowed;
    /// engines built from this builder do not dispose them. Use
    /// <see cref="UseOwnedExtensionFactory"/> when each engine should receive and
    /// own an independent disposable service.
    /// </remarks>
    public MarkdownEngineBuilder UseExtension(IMarkdownExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        _extensions.Add(extension);
        return this;
    }

    /// <summary>
    /// Includes an already frozen declarative extension set. Reusable owned
    /// factories in the set create fresh resources for each subsequent build.
    /// </summary>
    public MarkdownEngineBuilder UseExtensions(MarkdownExtensionSet extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        _extensions.Include(extensions);
        return this;
    }

    /// <summary>
    /// Adds a factory that creates an independent extension and engine-owned
    /// resource for every <see cref="Build"/> call.
    /// </summary>
    /// <remarks>
    /// The factory itself is retained by reusable builders and frozen extension
    /// sets; values returned by it are not. On a successful build, ownership of
    /// <see cref="MarkdownOwnedExtension.Resource"/> transfers to the new engine.
    /// The engine disposes that resource only after its active extension callbacks
    /// and cancellation registrations have retired. The resource is also disposed
    /// if extension binding or engine construction fails.
    ///
    /// The factory must return a fresh pair whose extension identifier exactly
    /// matches <paramref name="extensionId"/>. It must not depend on a UI thread,
    /// and must be safe to invoke concurrently when the same frozen extension set
    /// is used by independent builders.
    /// </remarks>
    /// <param name="extensionId">Stable, package-qualified identifier produced by every factory result.</param>
    /// <param name="create">Factory for one fresh extension/resource pair per engine.</param>
    /// <returns>The current builder.</returns>
    public MarkdownEngineBuilder UseOwnedExtensionFactory(
        string extensionId,
        Func<MarkdownOwnedExtension> create)
    {
        _extensions.AddOwnedExtensionFactory(
            new MarkdownOwnedExtensionFactory(extensionId, create));
        return this;
    }

    /// <summary>
    /// Configures an opaque presentation registry supplied by the WinUI
    /// assembly. Keeping this internal preserves the Core package boundary.
    /// </summary>
    internal MarkdownEngineBuilder ConfigurePresentation<TConfiguration>(
        Func<TConfiguration> create,
        Action<TConfiguration> configure)
        where TConfiguration : class, IMarkdownPresentationConfiguration
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(configure);

        if (_presentationConfiguration is null)
        {
            _presentationConfiguration = create() ??
                throw new InvalidOperationException("The presentation configuration factory returned null.");
        }
        else if (_presentationConfiguration.IsFrozen)
        {
            _presentationConfiguration = _presentationConfiguration.CreateMutableCopy();
        }

        if (_presentationConfiguration is not TConfiguration typed)
        {
            throw new InvalidOperationException(
                $"The builder already contains presentation configuration of type '{_presentationConfiguration.GetType().FullName}'.");
        }

        configure(typed);
        return this;
    }

    /// <summary>
    /// Adds an AOT-safe parser pipeline contribution owned by an optional
    /// feature pack. Presentation callbacks remain separate from syntax so the
    /// engine and every control builder parse the same document shape.
    /// </summary>
    internal MarkdownEngineBuilder ConfigureParser(Action<MarkdownPipelineBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (!_pipelineConfigurations.Contains(configure))
            _pipelineConfigurations.Add(configure);
        return this;
    }

    /// <summary>Includes a presentation snapshot when deriving another engine.</summary>
    internal MarkdownEngineBuilder IncludePresentation(
        IMarkdownPresentationConfiguration? configuration)
    {
        if (configuration is null)
            return this;
        if (_presentationConfiguration is not null)
            throw new InvalidOperationException("Presentation configuration is already attached to this builder.");

        _presentationConfiguration = configuration;
        return this;
    }

    /// <summary>
    /// Sets the maximum estimated retained bytes used by completed parsed
    /// documents. Zero disables completed-document caching while preserving
    /// in-flight request deduplication.
    /// </summary>
    /// <param name="byteBudget">Non-negative completed-document cache budget in bytes.</param>
    public MarkdownEngineBuilder WithParseCacheBudgetBytes(long byteBudget)
    {
        if (byteBudget < 0)
            throw new ArgumentOutOfRangeException(nameof(byteBudget), "The parse cache budget cannot be negative.");

        _parseCacheBudgetBytes = byteBudget;
        return this;
    }

    /// <summary>
    /// Sets bounded admission limits for unique uncached parse operations.
    /// Same-source requests continue to share one admitted operation.
    /// </summary>
    public MarkdownEngineBuilder WithParseLimits(MarkdownParseLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _parseLimits = limits;
        return this;
    }

    /// <summary>Creates an immutable engine snapshot.</summary>
    public MarkdownEngine Build()
        => BuildCore(isSharedSingleton: false);

    /// <summary>
    /// Creates a process-wide feature-pack singleton whose disposal is a no-op.
    /// Only built-in static engine properties use this path.
    /// </summary>
    internal MarkdownEngine BuildShared()
        => BuildCore(isSharedSingleton: true);

    private MarkdownEngine BuildCore(bool isSharedSingleton)
    {
        IMarkdownPresentationConfiguration? presentation = _presentationConfiguration?.Freeze();
        MarkdownRuntimeExtensionSet runtime = _extensions.Build().BindOwnedExtensions();
        try
        {
            return new MarkdownEngine(
                _profile,
                runtime.Extensions,
                runtime.OwnedResources,
                runtime.CallbackLifetime,
                _pipelineConfigurations.ToArray(),
                _parseCacheBudgetBytes,
                _parseLimits,
                presentation,
                isSharedSingleton);
        }
        catch
        {
            for (int index = runtime.OwnedResources.Count - 1; index >= 0; index--)
            {
                try
                {
                    runtime.OwnedResources[index].Dispose();
                }
                catch
                {
                    // Preserve the engine-construction failure while visiting
                    // every engine-local owned extension service.
                }
            }

            throw;
        }
    }
}
