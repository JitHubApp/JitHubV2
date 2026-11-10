namespace MarkdownRenderer.Controls.Internal.Pipeline.Commit;

/// <summary>
/// Publication state of the published snapshot's realization plans, shared
/// by everything that commits layout: snapshot replacement, lazy layout,
/// image relayout and raster placement.
/// </summary>
internal sealed class LayoutCommitState
{
    /// <summary>
    /// True while a commit publishes its plans. A commit that arrives then is
    /// deferred rather than nested.
    /// </summary>
    internal bool IsPublishing { get; private set; }

    /// <summary>The layout revision whose realization plans were last applied.</summary>
    internal long AppliedRevision { get; private set; }

    /// <summary>A commit waiting for its plans to publish, if any.</summary>
    internal PendingLayoutCommit? Pending { get; private set; }

    /// <summary>
    /// Advances whenever a commit is deferred or a pending commit is dropped
    /// for geometry recovery, so a failed recovery tick can tell whether a
    /// newer commit was deferred meanwhile.
    /// </summary>
    internal long Ticket { get; private set; }

    internal void BeginPublishing() => IsPublishing = true;

    internal void EndPublishing() => IsPublishing = false;

    internal void MarkApplied(long revision) => AppliedRevision = revision;

    internal void Defer(PendingLayoutCommit commit)
    {
        Pending = commit;
        ++Ticket;
    }

    internal void ClearPending() => Pending = null;

    /// <summary>Drops the pending commit and advances the ticket.</summary>
    internal void DropPending()
    {
        Pending = null;
        ++Ticket;
    }
}
