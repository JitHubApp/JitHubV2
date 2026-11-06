using System;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>
/// The owning layout builder's block and inline callbacks, scoped to one
/// construction so extension-owned containers build their structural children
/// with the same builder. Each push returns a scope that restores the previous
/// callback, so a published context never retains a builder or its semantic document.
/// </summary>
internal sealed class LayoutChildBuilders
{
    private readonly MarkdownLayoutContext _context;
    private Action<InlineContainerBox, ContainerInline, Func<Inline, bool>?, int>? _inlineBuilder;
    private Func<Block, BlockBox?>? _blockBuilder;

    internal LayoutChildBuilders(MarkdownLayoutContext context)
    {
        _context = context;
    }

    // Reuse the owning builder for structural children of extension-owned
    // containers. Like the inline callback, this must not survive construction
    // and retain the builder or semantic document in a published layout context.
    internal LayoutChildBlockBuildScope PushBlockBuilder(Func<Block, BlockBox?> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var previous = _blockBuilder;
        _blockBuilder = builder;
        return new LayoutChildBlockBuildScope(this, previous);
    }

    internal void RestoreBlockBuilder(Func<Block, BlockBox?>? previous) => _blockBuilder = previous;

    internal bool TryBuildBlock(Block block, out BlockBox? result)
    {
        result = null;
        if (_blockBuilder is not { } builder)
            return false;
        ArgumentNullException.ThrowIfNull(block);
        result = builder(block);
        return true;
    }

    // Optional built-in container renderers must use the owning layout build's
    // semantic extension fragments, not a second reduced inline implementation.
    // Scope this callback to construction so contexts do not retain a builder
    // or its semantic document after publication, failure or cancellation.
    internal LayoutChildInlineBuildScope PushInlineBuilder(
        Action<InlineContainerBox, ContainerInline, Func<Inline, bool>?, int> builder)
    {
        var previous = _inlineBuilder;
        _inlineBuilder = builder;
        return new LayoutChildInlineBuildScope(this, previous);
    }

    internal void RestoreInlineBuilder(
        Action<InlineContainerBox, ContainerInline, Func<Inline, bool>?, int>? previous) =>
        _inlineBuilder = previous;

    internal bool TryAppendInlines(
        InlineContainerBox box, ContainerInline inlines,
        Func<Inline, bool>? skipFirstIf, int inheritedAliasStart)
    {
        if (_inlineBuilder is not { } builder)
            return false;
        if (!ReferenceEquals(box.Context, _context))
            throw new InvalidOperationException("Child inline construction must use its owning layout context.");
        builder(box, inlines, skipFirstIf, inheritedAliasStart);
        return true;
    }
}
