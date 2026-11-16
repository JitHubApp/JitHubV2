using System;

namespace MarkdownRenderer.Layout.Internal.SourceProjection;

/// <summary>Restores the previous Markdown or safe-HTML source projection when disposed.</summary>
internal readonly struct MarkdownSourceProjectionScope : IDisposable
{
    private readonly LayoutSourceProjections? _owner;
    private readonly MarkdownSourceProjection? _previous;
    private readonly bool _safeHtml;

    internal MarkdownSourceProjectionScope(
        LayoutSourceProjections owner,
        MarkdownSourceProjection? previous,
        bool safeHtml)
    {
        _owner = owner;
        _previous = previous;
        _safeHtml = safeHtml;
    }

    public void Dispose() => _owner?.Restore(_previous, _safeHtml);
}
