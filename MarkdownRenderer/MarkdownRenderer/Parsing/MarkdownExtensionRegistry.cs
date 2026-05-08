using System;
using System.Collections.Generic;
using Markdig;

namespace MarkdownRenderer.Parsing;

public sealed class MarkdownExtensionRegistry
{
    private readonly MarkdownPipelineBuilder _builder = new();
    private readonly Dictionary<Type, IMarkdownNodeRendererErased> _renderers = new();

    public MarkdownExtensionRegistry ConfigurePipeline(Action<MarkdownPipelineBuilder> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        configure(_builder);
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
        if (renderer is IMarkdownNodeRendererErased erased)
            _renderers[typeof(TNode)] = erased;
        else
            _renderers[typeof(TNode)] = new ErasedAdapter<TNode>(renderer);
        return this;
    }

    public bool TryGetRenderer(Type nodeType, out IMarkdownNodeRendererErased? renderer)
    {
        // Exact-type lookup only — O(1), AOT-safe. Callers must register the
        // concrete node type; base-type or interface matches are not performed.
        if (_renderers.TryGetValue(nodeType, out renderer))
            return true;
        renderer = null;
        return false;
    }

    public MarkdownPipeline BuildPipeline() => _builder.Build();

    // Thin wrapper for callers who implement IMarkdownNodeRenderer<T> directly
    // without inheriting MarkdownNodeRenderer<T>.
    private sealed class ErasedAdapter<TNode>(IMarkdownNodeRenderer<TNode> inner)
        : IMarkdownNodeRendererErased where TNode : class
    {
        public Layout.BlockBox? BuildBlock(object node, Layout.MarkdownLayoutContext context)
            => node is TNode typed ? inner.BuildBlock(typed, context) : null;
    }
}
