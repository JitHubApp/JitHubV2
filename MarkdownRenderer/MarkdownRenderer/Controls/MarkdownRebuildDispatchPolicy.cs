using System;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Logical priorities used when dispatching coalesced renderer rebuilds.
/// Kept independent of WinUI so queue promotion can be contract-tested.
/// </summary>
internal enum MarkdownRebuildDispatchPriority
{
    Low,
    Normal,
}

/// <summary>
/// Identifies the one queued rebuild callback that is currently allowed to run.
/// A higher-priority callback supersedes the previous ticket only after enqueue
/// succeeds, so a failed promotion leaves the existing callback authoritative.
/// </summary>
internal readonly record struct MarkdownRebuildDispatchState(
    long Ticket,
    MarkdownRebuildDispatchPriority Priority)
{
    internal bool IsQueued => Ticket != 0;

    internal bool ShouldSchedule(MarkdownRebuildDispatchPriority requestedPriority) =>
        !IsQueued ||
        (Priority == MarkdownRebuildDispatchPriority.Low &&
         requestedPriority == MarkdownRebuildDispatchPriority.Normal);

    internal bool IsCurrent(long ticket) => ticket != 0 && Ticket == ticket;

    internal MarkdownRebuildDispatchState AfterEnqueueAttempt(
        long ticket,
        MarkdownRebuildDispatchPriority requestedPriority,
        bool succeeded)
    {
        if (!succeeded || !ShouldSchedule(requestedPriority))
            return this;

        if (ticket == 0)
            throw new ArgumentOutOfRangeException(nameof(ticket));

        return new MarkdownRebuildDispatchState(ticket, requestedPriority);
    }
}

internal static class MarkdownRebuildDispatchPolicy
{
    internal static MarkdownRebuildDispatchPriority SelectPriority(bool requiresFullRebuild) =>
        requiresFullRebuild
            ? MarkdownRebuildDispatchPriority.Normal
            : MarkdownRebuildDispatchPriority.Low;
}
