using System;
using MarkdownRenderer.Parsing;

namespace MarkdownRenderer.Layout;

/// <summary>Restores the previous safe-HTML alignment, nested scopes and inline flow when disposed.</summary>
internal readonly struct SafeHtmlBlockRenderScope : IDisposable
{
    private readonly LayoutSafeHtmlBlockContext? _owner;
    private readonly SafeHtmlAlignment _alignment;
    private readonly SafeHtmlBlockScopeTracker? _nestedScopes;
    private readonly int? _inlineFlowScopeId;
    private readonly bool _inlineFlowContinuesPrevious;

    internal SafeHtmlBlockRenderScope(
        LayoutSafeHtmlBlockContext owner,
        SafeHtmlAlignment alignment,
        SafeHtmlBlockScopeTracker? nestedScopes,
        int? inlineFlowScopeId,
        bool inlineFlowContinuesPrevious)
    {
        _owner = owner;
        _alignment = alignment;
        _nestedScopes = nestedScopes;
        _inlineFlowScopeId = inlineFlowScopeId;
        _inlineFlowContinuesPrevious = inlineFlowContinuesPrevious;
    }

    public void Dispose()
        => _owner?.Restore(
            _alignment,
            _nestedScopes,
            _inlineFlowScopeId,
            _inlineFlowContinuesPrevious);
}
