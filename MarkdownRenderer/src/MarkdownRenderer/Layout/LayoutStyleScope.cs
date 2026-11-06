using System;

namespace MarkdownRenderer.Layout;

/// <summary>Restores a layout style stack's context keys, aliases and list depth when disposed.</summary>
internal readonly struct LayoutStyleScope : IDisposable
{
    private readonly LayoutStyleStack? _owner;
    private readonly int _contextCount;
    private readonly int _aliasCount;
    private readonly int _listDepth;

    internal LayoutStyleScope(LayoutStyleStack owner, int contextCount, int aliasCount, int listDepth)
    {
        _owner = owner;
        _contextCount = contextCount;
        _aliasCount = aliasCount;
        _listDepth = listDepth;
    }

    public void Dispose()
        => _owner?.Restore(_contextCount, _aliasCount, _listDepth);
}
