using System;
using Markdig.Syntax;

namespace MarkdownRenderer.Layout;

/// <summary>Restores the previous child block builder of a layout context when disposed.</summary>
internal readonly struct LayoutChildBlockBuildScope : IDisposable
{
    private readonly MarkdownLayoutContext? _owner;
    private readonly Func<Block, BlockBox?>? _previous;

    internal LayoutChildBlockBuildScope(MarkdownLayoutContext owner, Func<Block, BlockBox?>? previous)
    {
        _owner = owner;
        _previous = previous;
    }

    public void Dispose() => _owner?.RestoreChildBlockBuilder(_previous);
}
