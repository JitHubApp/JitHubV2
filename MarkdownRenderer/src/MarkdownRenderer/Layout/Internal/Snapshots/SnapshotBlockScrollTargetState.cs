namespace MarkdownRenderer.Layout.Internal.Snapshots;

internal enum SnapshotBlockScrollTargetState
{
    Ready,
    RequiresRealization,
    Busy,
    NotFound,
    Unavailable,
}
