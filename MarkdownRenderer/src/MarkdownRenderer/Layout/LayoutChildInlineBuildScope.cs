using System;
using Markdig.Syntax.Inlines;
using MarkdownRenderer.Layout.Boxes;

namespace MarkdownRenderer.Layout;

/// <summary>Restores the previous child inline builder of a layout context when disposed.</summary>
internal readonly struct LayoutChildInlineBuildScope : IDisposable
{
    private readonly MarkdownLayoutContext? _owner;
    private readonly Action<InlineContainerBox, ContainerInline, Func<Inline, bool>?, int>? _previous;

    internal LayoutChildInlineBuildScope(MarkdownLayoutContext owner,
        Action<InlineContainerBox, ContainerInline, Func<Inline, bool>?, int>? previous)
    {
        _owner = owner;
        _previous = previous;
    }

    public void Dispose() => _owner?.RestoreChildInlineBuilder(_previous);
}
