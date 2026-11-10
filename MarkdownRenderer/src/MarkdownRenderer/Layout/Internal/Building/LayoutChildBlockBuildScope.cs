using System;
using Markdig.Syntax;
using MarkdownRenderer.Layout.Internal.Boxes;

namespace MarkdownRenderer.Layout.Internal.Building;

/// <summary>Restores the previous child block builder of a layout pass when disposed.</summary>
internal readonly struct LayoutChildBlockBuildScope : IDisposable
{
    private readonly LayoutChildBuilders? _owner;
    private readonly Func<Block, BlockBox?>? _previous;

    internal LayoutChildBlockBuildScope(LayoutChildBuilders owner, Func<Block, BlockBox?>? previous)
    {
        _owner = owner;
        _previous = previous;
    }

    public void Dispose() => _owner?.RestoreBlockBuilder(_previous);
}
