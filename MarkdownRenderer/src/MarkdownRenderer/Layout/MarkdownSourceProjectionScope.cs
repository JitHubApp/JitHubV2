using System;

namespace MarkdownRenderer.Layout;

/// <summary>Restores a layout context's previous Markdown or safe-HTML source projection when disposed.</summary>
internal readonly struct MarkdownSourceProjectionScope : IDisposable
{
    private readonly MarkdownLayoutContext? _owner;
    private readonly MarkdownSourceProjection? _previous;
    private readonly bool _safeHtml;

    internal MarkdownSourceProjectionScope(
        MarkdownLayoutContext owner,
        MarkdownSourceProjection? previous,
        bool safeHtml)
    {
        _owner = owner;
        _previous = previous;
        _safeHtml = safeHtml;
    }

    public void Dispose() => _owner?.RestoreSourceProjection(_previous, _safeHtml);
}
