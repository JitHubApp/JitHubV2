namespace MarkdownRenderer.Controls.Internal.Pipeline.Commit;

/// <summary>
/// A layout commit that could not publish its realization plans yet. It
/// keeps only values, never the snapshot: the snapshot generation it belongs
/// to, the scroll anchor to restore and the scroll offset when it deferred.
/// </summary>
internal readonly record struct PendingLayoutCommit(
    long Generation,
    (int BlockIndex, double OffsetFromTop)? Anchor,
    double? ScrollOffset);
