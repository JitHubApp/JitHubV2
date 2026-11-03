using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Markdig;
using MarkdownRenderer.Document;
using MarkdownRenderer.Extensions;

namespace MarkdownRenderer;

/// <summary>
/// Immutable markdown parser configuration with thread-safe parse admission and
/// caching. One engine may parse documents concurrently and its documents may
/// be shared by multiple views. Custom extension callbacks and their captured
/// services must support concurrent invocation.
/// </summary>
public sealed class MarkdownEngine : IDisposable
{
    /// <summary>Default completed-document cache budget: 16 MiB.</summary>
    public const long DefaultParseCacheBudgetBytes = 16L * 1024 * 1024;

    private readonly IReadOnlyList<Action<MarkdownPipelineBuilder>> _pipelineConfigurations;
    private readonly MarkdownDocumentParser _parser;
    private readonly MarkdownParseCoordinator _coordinator;
    private readonly bool _isSharedSingleton;

    internal MarkdownEngine(
        MarkdownProfile profile,
        MarkdownExtensionSet extensions,
        IReadOnlyList<IDisposable> ownedResources,
        MarkdownExtensionCallbackLifetime? extensionCallbackLifetime,
        IReadOnlyList<Action<MarkdownPipelineBuilder>> pipelineConfigurations,
        long parseCacheBudgetBytes,
        MarkdownParseLimits parseLimits,
        IMarkdownPresentationConfiguration? presentationConfiguration,
        bool isSharedSingleton)
    {
        Profile = profile;
        Extensions = extensions;
        ParseCacheBudgetBytes = parseCacheBudgetBytes;
        ParseLimits = parseLimits;
        PresentationConfiguration = presentationConfiguration;
        _isSharedSingleton = isSharedSingleton;
        IReadOnlyList<IDisposable> resources =
            ownedResources ?? throw new ArgumentNullException(nameof(ownedResources));
        _pipelineConfigurations = pipelineConfigurations ??
            throw new ArgumentNullException(nameof(pipelineConfigurations));
        _parser = new MarkdownDocumentParser(
            profile,
            extensions,
            _pipelineConfigurations,
            presentationConfiguration);
        _coordinator = new MarkdownParseCoordinator(
            _parser,
            parseLimits,
            parseCacheBudgetBytes,
            resources,
            extensionCallbackLifetime);
    }

    /// <summary>Gets an engine configured for strict CommonMark.</summary>
    public static MarkdownEngine Default { get; } = new MarkdownEngineBuilder().BuildShared();

    /// <summary>Gets the immutable parsing profile used by this engine.</summary>
    public MarkdownProfile Profile { get; }

    /// <summary>Gets the immutable declarative extension set used by this engine.</summary>
    public MarkdownExtensionSet Extensions { get; }

    /// <summary>Gets the byte budget for completed parsed documents.</summary>
    public long ParseCacheBudgetBytes { get; }

    /// <summary>Gets the bounded admission limits for uncached parses.</summary>
    public MarkdownParseLimits ParseLimits { get; }

    /// <summary>
    /// Gets the frozen UI-package presentation configuration without exposing
    /// WinUI or layout types from MarkdownRenderer.Core.
    /// </summary>
    internal IMarkdownPresentationConfiguration? PresentationConfiguration { get; }

    /// <summary>
    /// Creates a reusable builder initialized from this engine. Opaque
    /// presentation registrations are preserved without exposing UI types.
    /// Owned extension services are recreated independently for each new engine.
    /// </summary>
    public MarkdownEngineBuilder ToBuilder() => new(this);

    internal int ActiveParseCount => _coordinator.ActiveParseCount;

    internal IReadOnlyList<Action<MarkdownPipelineBuilder>> PipelineConfigurations =>
        _pipelineConfigurations;

    internal int CompletedParseCount => _coordinator.CompletedParseCount;

    internal int QueuedParseCount => _coordinator.QueuedParseCount;

    internal int OutstandingParseCount => _coordinator.OutstandingParseCount;

    /// <summary>
    /// Gets the number of content hashes computed for parse-cache keys. This is
    /// internal evidence for cache-path tests; callers should not depend on it.
    /// </summary>
    internal long SourceKeyHashCount => _coordinator.SourceKeyHashCount;

    /// <summary>
    /// Parses markdown away from the calling context and returns an immutable,
    /// reusable document. Source and source spans are preserved as UTF-16.
    /// </summary>
    /// <param name="source">Markdown source. A null value is treated as empty.</param>
    /// <param name="cancellationToken">Token used to cancel queued or post-parse document work.</param>
    public Task<MarkdownDocument> ParseAsync(
        string? source,
        CancellationToken cancellationToken = default)
        => _coordinator.ParseAsync(source, deferOffscreenScenes: false, cancellationToken);

    /// <summary>
    /// Parses for a progressive presentation. Built-in block and inline scene
    /// extensions may defer expensive materialization; public ParseAsync stays
    /// eager and uses an independent cache entry.
    /// </summary>
    internal Task<MarkdownDocument> ParseForProgressivePresentationAsync(
        string? source,
        CancellationToken cancellationToken = default)
        => _coordinator.ParseAsync(source, deferOffscreenScenes: true, cancellationToken);

    internal static long EstimateDocumentWeight(MarkdownDocument document)
        => MarkdownDocumentWeightEstimator.Estimate(document, CancellationToken.None);

    /// <summary>
    /// Renders with the selected profile's specification syntax while allowing
    /// raw HTML passthrough solely for the official-example conformance oracle.
    /// Production parsing continues to enforce the profile's literal-HTML policy.
    /// </summary>
    internal string RenderSpecificationHtmlForValidation(string source)
        => _parser.RenderSpecificationHtmlForValidation(source);

    /// <summary>
    /// Releases services created by optional feature convenience overloads.
    /// Disposal cancels admitted work and defers resource release until running
    /// extension work and cancellation registrations have exited. Ordinary
    /// engines reject subsequent parses after disposal. The process-wide
    /// <see cref="Default"/> and feature-pack shared engines explicitly ignore
    /// disposal.
    /// </summary>
    public void Dispose()
    {
        if (_isSharedSingleton)
            return;

        _coordinator.Dispose();
    }
}
