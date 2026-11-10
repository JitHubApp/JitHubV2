namespace MarkdownRenderer.Controls.Internal.Pipeline.Rebuild;

/// <summary>
/// Logical priorities used when dispatching coalesced renderer rebuilds.
/// Kept independent of WinUI so queue promotion can be contract-tested.
/// </summary>
internal enum MarkdownRebuildDispatchPriority
{
    Low,
    Normal,
}
