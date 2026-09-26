using System;
using System.Collections.Generic;
using Markdig;

namespace MarkdownRenderer.Parsing;

/// <summary>
/// Configures the Markdig pipeline and custom block renderers used by a renderer control.
/// </summary>
internal sealed class MarkdownExtensionRegistry : IMarkdownPresentationConfiguration
{
    private readonly List<Action<MarkdownPipelineBuilder>> _pipelineConfiguration;
    private readonly Dictionary<Type, IMarkdownNodeRendererErased> _renderers;
    private readonly HashSet<string> _presentationFeatures;
    private readonly object _gate = new();
    private readonly MarkdownPipeline? _frozenPipeline;
    private readonly MarkdownExtensionRegistry? _baseSnapshot;
    private SafeHtmlRenderPolicy? _safeHtmlPolicy;

    internal MarkdownExtensionRegistry()
    {
        _pipelineConfiguration = new List<Action<MarkdownPipelineBuilder>>();
        _renderers = new Dictionary<Type, IMarkdownNodeRendererErased>();
        _presentationFeatures = new HashSet<string>(StringComparer.Ordinal);
    }

    private MarkdownExtensionRegistry(
        List<Action<MarkdownPipelineBuilder>> pipelineConfiguration,
        Dictionary<Type, IMarkdownNodeRendererErased> renderers,
        HashSet<string> presentationFeatures,
        SafeHtmlRenderPolicy? safeHtmlPolicy,
        int revision,
        bool freeze,
        MarkdownExtensionRegistry? baseSnapshot = null)
    {
        _pipelineConfiguration = pipelineConfiguration;
        _renderers = renderers;
        _presentationFeatures = presentationFeatures;
        _safeHtmlPolicy = safeHtmlPolicy;
        Revision = revision;
        _baseSnapshot = baseSnapshot;
        _frozenPipeline = freeze ? BuildPipelineCore(pipelineConfiguration) : null;
    }

    /// <summary>Gets a monotonically increasing value that changes whenever the registry configuration changes.</summary>
    public int Revision { get; private set; }

    public bool IsFrozen => _frozenPipeline is not null;

    internal SafeHtmlRenderPolicy? SafeHtmlPolicy
    {
        get
        {
            if (IsFrozen)
                return _safeHtmlPolicy;
            lock (_gate)
                return _safeHtmlPolicy;
        }
    }

    internal MarkdownExtensionRegistry ConfigureSafeHtmlPolicy(SafeHtmlRenderPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        lock (_gate)
        {
            ThrowIfFrozen();
            _safeHtmlPolicy = policy;
            Revision++;
        }

        return this;
    }

    internal bool HasPresentationFeature(string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        if (IsFrozen)
            return _presentationFeatures.Contains(featureId);

        lock (_gate)
            return _presentationFeatures.Contains(featureId);
    }

    internal MarkdownExtensionRegistry AddPresentationFeature(string featureId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        lock (_gate)
        {
            ThrowIfFrozen();
            if (_presentationFeatures.Add(featureId))
                Revision++;
        }

        return this;
    }

    /// <summary>
    /// Applies Markdig pipeline configuration.
    /// </summary>
    /// <param name="configure">The configuration callback to run under the registry lock.</param>
    /// <returns>The current registry for fluent chaining.</returns>
    public MarkdownExtensionRegistry ConfigurePipeline(Action<MarkdownPipelineBuilder> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        lock (_gate)
        {
            ThrowIfFrozen();
            _pipelineConfiguration.Add(configure);
            Revision++;
        }
        return this;
    }

    /// <summary>
    /// Register a typed renderer. TNode must be a concrete Markdig AST node type.
    /// Use <see cref="MarkdownNodeRenderer{TNode}"/> as your base class to satisfy
    /// both the typed and the AOT-safe erased interfaces.
    /// </summary>
    public MarkdownExtensionRegistry RegisterRenderer<TNode>(IMarkdownNodeRenderer<TNode> renderer)
        where TNode : class
    {
        if (renderer is null) throw new ArgumentNullException(nameof(renderer));
        lock (_gate)
        {
            ThrowIfFrozen();
            if (renderer is IMarkdownNodeRendererErased erased)
                _renderers[typeof(TNode)] = erased;
            else
                _renderers[typeof(TNode)] = new ErasedAdapter<TNode>(renderer);
            Revision++;
        }
        return this;
    }

    /// <summary>
    /// Registers a feature-pack default only when a host/view has not already
    /// supplied an exact renderer for the same node type.
    /// </summary>
    internal MarkdownExtensionRegistry RegisterRendererIfAbsent<TNode>(
        IMarkdownNodeRenderer<TNode> renderer)
        where TNode : class
    {
        ArgumentNullException.ThrowIfNull(renderer);
        lock (_gate)
        {
            ThrowIfFrozen();
            if (_renderers.ContainsKey(typeof(TNode)))
                return this;

            _renderers[typeof(TNode)] = renderer is IMarkdownNodeRendererErased erased
                ? erased
                : new ErasedAdapter<TNode>(renderer);
            Revision++;
        }

        return this;
    }

    /// <summary>
    /// Registers a feature-pack renderer when no exact renderer exists, or
    /// refreshes a renderer previously supplied by that same feature pack.
    /// An explicit host/view renderer for the node type keeps precedence.
    /// </summary>
    internal MarkdownExtensionRegistry RegisterOrRefreshFeatureRenderer<TNode, TRenderer>(
        TRenderer renderer)
        where TNode : class
        where TRenderer : class, IMarkdownNodeRenderer<TNode>, IMarkdownNodeRendererErased
    {
        ArgumentNullException.ThrowIfNull(renderer);
        lock (_gate)
        {
            ThrowIfFrozen();
            if (_renderers.TryGetValue(typeof(TNode), out IMarkdownNodeRendererErased? existing) &&
                existing is not TRenderer)
            {
                return this;
            }

            _renderers[typeof(TNode)] = renderer;
            Revision++;
        }

        return this;
    }

    internal bool TryGetRenderer(Type nodeType, out IMarkdownNodeRendererErased? renderer)
    {
        // Exact-type lookup only — O(1), AOT-safe. Callers must register the
        // concrete node type; base-type or interface matches are not performed.
        if (IsFrozen)
            return _renderers.TryGetValue(nodeType, out renderer);

        lock (_gate)
        {
            if (_renderers.TryGetValue(nodeType, out renderer))
                return true;
            renderer = null;
            return false;
        }
    }

    /// <summary>Builds a Markdig pipeline from the registered configuration callbacks.</summary>
    public MarkdownPipeline BuildPipeline()
    {
        if (_frozenPipeline is not null)
            return _frozenPipeline;

        lock (_gate)
            return BuildPipelineCore(_pipelineConfiguration);
    }

    internal MarkdownExtensionRegistry Freeze()
    {
        lock (_gate)
        {
            if (IsFrozen)
                return this;

            return new MarkdownExtensionRegistry(
                new List<Action<MarkdownPipelineBuilder>>(_pipelineConfiguration),
                new Dictionary<Type, IMarkdownNodeRendererErased>(_renderers),
                new HashSet<string>(_presentationFeatures, StringComparer.Ordinal),
                _safeHtmlPolicy,
                Revision,
                freeze: true,
                _baseSnapshot);
        }
    }

    internal MarkdownExtensionRegistry CreateMutableCopy()
    {
        lock (_gate)
        {
            return new MarkdownExtensionRegistry(
                new List<Action<MarkdownPipelineBuilder>>(_pipelineConfiguration),
                new Dictionary<Type, IMarkdownNodeRendererErased>(_renderers),
                new HashSet<string>(_presentationFeatures, StringComparer.Ordinal),
                _safeHtmlPolicy,
                Revision,
                freeze: false,
                IsFrozen ? this : _baseSnapshot);
        }
    }

    /// <summary>
    /// Creates one frozen presentation snapshot in which explicit per-view
    /// registrations override the engine/document defaults. Exact renderer
    /// types and safe-HTML policy use overlay precedence; feature and pipeline
    /// registrations are composed in base-then-overlay order.
    /// </summary>
    internal static MarkdownExtensionRegistry ComposePresentation(
        MarkdownExtensionRegistry baseRegistry,
        MarkdownExtensionRegistry? viewOverrides)
    {
        ArgumentNullException.ThrowIfNull(baseRegistry);
        MarkdownExtensionRegistry frozenBase = baseRegistry.Freeze();
        if (viewOverrides is null || ReferenceEquals(baseRegistry, viewOverrides))
            return frozenBase;

        MarkdownExtensionRegistry frozenOverrides = viewOverrides.Freeze();
        if (ReferenceEquals(frozenBase, frozenOverrides))
            return frozenBase;
        if (frozenOverrides.ContainsSnapshot(frozenBase))
        {
            // View helpers configure a full mutable copy of the engine
            // snapshot. It already contains every base registration, so using
            // it directly preserves override precedence without replaying GFM,
            // GitHub, or other pipeline callbacks a second time.
            return frozenOverrides;
        }

        var pipeline = new List<Action<MarkdownPipelineBuilder>>(
            frozenBase._pipelineConfiguration.Count + frozenOverrides._pipelineConfiguration.Count);
        var seenPipelineConfiguration = new HashSet<Action<MarkdownPipelineBuilder>>(
            ReferenceEqualityComparer.Instance);
        foreach (Action<MarkdownPipelineBuilder> configure in frozenBase._pipelineConfiguration)
        {
            if (seenPipelineConfiguration.Add(configure))
                pipeline.Add(configure);
        }
        foreach (Action<MarkdownPipelineBuilder> configure in frozenOverrides._pipelineConfiguration)
        {
            // Full helper snapshots can share lineage with an earlier engine
            // than the current composite base. Delegate identity is retained
            // across snapshot copies, so suppressing repeated callbacks keeps
            // order-independent helper composition at one effect per feature.
            if (seenPipelineConfiguration.Add(configure))
                pipeline.Add(configure);
        }

        var renderers = new Dictionary<Type, IMarkdownNodeRendererErased>(frozenBase._renderers);
        foreach (KeyValuePair<Type, IMarkdownNodeRendererErased> registration in frozenOverrides._renderers)
            renderers[registration.Key] = registration.Value;

        var features = new HashSet<string>(frozenBase._presentationFeatures, StringComparer.Ordinal);
        features.UnionWith(frozenOverrides._presentationFeatures);
        int revision = frozenBase.Revision == int.MaxValue || frozenOverrides.Revision == int.MaxValue
            ? int.MaxValue
            : System.Math.Max(frozenBase.Revision, frozenOverrides.Revision) + 1;
        return new MarkdownExtensionRegistry(
            pipeline,
            renderers,
            features,
            frozenOverrides._safeHtmlPolicy ?? frozenBase._safeHtmlPolicy,
            revision,
            freeze: true);
    }

    private bool ContainsSnapshot(MarkdownExtensionRegistry candidate)
    {
        for (MarkdownExtensionRegistry? current = _baseSnapshot;
             current is not null;
             current = current._baseSnapshot)
        {
            if (ReferenceEquals(current, candidate))
                return true;
        }

        return false;
    }

    IMarkdownPresentationConfiguration IMarkdownPresentationConfiguration.Freeze() => Freeze();

    IMarkdownPresentationConfiguration IMarkdownPresentationConfiguration.CreateMutableCopy() =>
        CreateMutableCopy();

    private static MarkdownPipeline BuildPipelineCore(
        IReadOnlyList<Action<MarkdownPipelineBuilder>> configurations)
    {
        var builder = new MarkdownPipelineBuilder();
        for (int index = 0; index < configurations.Count; index++)
            configurations[index](builder);
        return builder.Build();
    }

    private void ThrowIfFrozen()
    {
        if (IsFrozen)
            throw new InvalidOperationException("A frozen markdown presentation registry cannot be modified.");
    }

    // Thin wrapper for callers who implement IMarkdownNodeRenderer<T> directly
    // without inheriting MarkdownNodeRenderer<T>.
    private sealed class ErasedAdapter<TNode>(IMarkdownNodeRenderer<TNode> inner)
        : IMarkdownNodeRendererErased where TNode : class
    {
        public Layout.BlockBox? BuildBlock(object node, Layout.MarkdownLayoutContext context)
            => node is TNode typed ? inner.BuildBlock(typed, context) : null;
    }
}
