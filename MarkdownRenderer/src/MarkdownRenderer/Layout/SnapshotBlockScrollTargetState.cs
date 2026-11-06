namespace MarkdownRenderer.Layout;

internal enum SnapshotBlockScrollTargetState
{
    Ready,
    RequiresRealization,
    Busy,
    NotFound,
    Unavailable,
}
