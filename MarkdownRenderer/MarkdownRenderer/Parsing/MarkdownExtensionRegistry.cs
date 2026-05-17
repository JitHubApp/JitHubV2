using System;
using System.Collections.Generic;
using Markdig;

namespace MarkdownRenderer.Parsing;

/// <summary>
/// Configures the Markdig pipeline and custom block renderers used by a renderer control.
/// </summary>
public sealed class MarkdownExtensionRegistry
{
    private readonly MarkdownPipelineBuilder _builder = new();
    private readonly Dictionary<Type, IMarkdownNodeRendererErased> _renderers = new();
    private readonly object _gate = new();

    /// <summary>Gets a monotonically increasing value that changes whenever the registry configuration changes.</summary>
    public int Revision { get; private set; }

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
            configure(_builder);
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
            if (renderer is IMarkdownNodeRendererErased erased)
                _renderers[typeof(TNode)] = erased;
            else
                _renderers[typeof(TNode)] = new ErasedAdapter<TNode>(renderer);
            Revision++;
        }
        return this;
    }

    internal bool TryGetRenderer(Type nodeType, out IMarkdownNodeRendererErased? renderer)
    {
        // Exact-type lookup only — O(1), AOT-safe. Callers must register the
        // concrete node type; base-type or interface matches are not performed.
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
        lock (_gate)
            return _builder.Build();
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
