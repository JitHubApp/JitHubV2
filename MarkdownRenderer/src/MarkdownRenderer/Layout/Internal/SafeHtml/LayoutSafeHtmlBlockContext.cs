using MarkdownRenderer.Parsing.Internal.SafeHtml;

namespace MarkdownRenderer.Layout.Internal.SafeHtml;

/// <summary>
/// Safe-HTML block state that one layout pass inherits while it builds the
/// children of a sanitized HTML element: alignment, nested block scopes and the
/// inline flow the children continue. Each push returns a scope that restores it.
/// </summary>
internal sealed class LayoutSafeHtmlBlockContext
{
    private SafeHtmlAlignment _alignment;
    private SafeHtmlBlockScopeTracker? _nestedScopes;
    private int? _inlineFlowScopeId;
    private bool _inlineFlowContinuesPrevious;

    /// <summary>Alignment inherited from the enclosing safe-HTML block.</summary>
    internal SafeHtmlAlignment Alignment => _alignment;

    /// <summary>Nested safe-HTML block scopes open around the current construction point.</summary>
    internal SafeHtmlBlockScopeTracker? NestedScopes => _nestedScopes;

    /// <summary>Identity of the safe-HTML inline flow the current blocks belong to, if any.</summary>
    internal int? InlineFlowScopeId => _inlineFlowScopeId;

    /// <summary>True when the current blocks continue the previous block's safe-HTML inline flow.</summary>
    internal bool InlineFlowContinuesPrevious => _inlineFlowContinuesPrevious;

    internal SafeHtmlBlockRenderScope Push(
        SafeHtmlAlignment alignment,
        SafeHtmlBlockScopeTracker? nestedScopes,
        int? inlineFlowScopeId,
        bool inlineFlowContinuesPrevious = false)
    {
        SafeHtmlAlignment previousAlignment = _alignment;
        SafeHtmlBlockScopeTracker? previousNestedScopes = _nestedScopes;
        int? previousInlineFlowScopeId = _inlineFlowScopeId;
        bool previousInlineFlowContinuesPrevious = _inlineFlowContinuesPrevious;
        _alignment = alignment;
        _nestedScopes = nestedScopes;
        _inlineFlowScopeId = inlineFlowScopeId;
        _inlineFlowContinuesPrevious = inlineFlowContinuesPrevious;
        return new SafeHtmlBlockRenderScope(
            this,
            previousAlignment,
            previousNestedScopes,
            previousInlineFlowScopeId,
            previousInlineFlowContinuesPrevious);
    }

    internal void Restore(
        SafeHtmlAlignment alignment,
        SafeHtmlBlockScopeTracker? nestedScopes,
        int? inlineFlowScopeId,
        bool inlineFlowContinuesPrevious)
    {
        _alignment = alignment;
        _nestedScopes = nestedScopes;
        _inlineFlowScopeId = inlineFlowScopeId;
        _inlineFlowContinuesPrevious = inlineFlowContinuesPrevious;
    }
}
