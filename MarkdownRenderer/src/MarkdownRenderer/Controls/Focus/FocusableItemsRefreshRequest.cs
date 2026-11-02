using MarkdownRenderer.Layout;

namespace MarkdownRenderer.Controls.Focus;

/// <summary>
/// The pending request to collect the focusable items again. Each request
/// gets a new version, so a collection that finishes after a newer request
/// cannot complete or clear it.
/// </summary>
internal sealed class FocusableItemsRefreshRequest
{
    private readonly PublishedLayout _published;
    private long _lastVersion;
    private FocusableItemsRefreshTicket _pending;

    internal FocusableItemsRefreshRequest(PublishedLayout published) => _published = published;

    /// <summary>The latest request, pending or not.</summary>
    internal FocusableItemsRefreshTicket Pending => _pending;

    /// <summary>Starts a new request for <paramref name="snapshot"/>.</summary>
    internal void Mark(LayoutSnapshot snapshot, long generation)
    {
        long requestVersion = unchecked(_lastVersion + 1);
        if (requestVersion <= 0)
            requestVersion = 1;
        _lastVersion = requestVersion;
        _pending = new FocusableItemsRefreshTicket(
            generation,
            snapshot.AutomationDocumentGeneration,
            snapshot.LayoutRevision,
            requestVersion,
            true);
    }

    /// <summary>Keeps request <paramref name="requestVersion"/> pending for a newer layout revision.</summary>
    internal void Retarget(
        long generation,
        long automationGeneration,
        long layoutRevision,
        long requestVersion)
    {
        _pending = new FocusableItemsRefreshTicket(
            generation,
            automationGeneration,
            layoutRevision,
            requestVersion,
            true);
    }

    /// <summary>
    /// True while <paramref name="request"/> is still the pending request and
    /// <paramref name="snapshot"/> is still the published one.
    /// </summary>
    internal bool IsCurrent(
        in FocusableItemsRefreshTicket request,
        LayoutSnapshot snapshot,
        long generation,
        long automationGeneration)
    {
        var pending = _pending;
        return request.IsPending &&
            pending.IsPending &&
            pending.RequestVersion == request.RequestVersion &&
            pending.SnapshotGeneration == generation &&
            pending.AutomationDocumentGeneration == automationGeneration &&
            pending.LayoutRevision == request.LayoutRevision &&
            ReferenceEquals(snapshot, _published.Snapshot) &&
            generation == _published.Generation &&
            automationGeneration == snapshot.AutomationDocumentGeneration;
    }

    /// <summary>
    /// True when a plan collected for <paramref name="requestVersion"/> may
    /// publish its focusable items: no newer request exists, and a pending
    /// one targets the same snapshot generation and layout revision.
    /// </summary>
    internal bool Admits(
        LayoutSnapshot snapshot,
        long generation,
        long revision,
        long requestVersion)
    {
        var pending = _pending;
        return pending.RequestVersion == requestVersion &&
            (!pending.IsPending ||
             (pending.SnapshotGeneration == generation &&
              pending.AutomationDocumentGeneration == snapshot.AutomationDocumentGeneration &&
              pending.LayoutRevision == revision));
    }

    /// <summary>Ends request <paramref name="requestVersion"/> after its plan published.</summary>
    internal void Complete(
        LayoutSnapshot snapshot,
        long generation,
        long revision,
        long requestVersion)
    {
        var pending = _pending;
        if (pending.IsPending &&
            pending.RequestVersion == requestVersion &&
            pending.SnapshotGeneration == generation &&
            pending.AutomationDocumentGeneration == snapshot.AutomationDocumentGeneration &&
            pending.LayoutRevision == revision &&
            ReferenceEquals(snapshot, _published.Snapshot) &&
            generation == _published.Generation &&
            revision == snapshot.LayoutRevision)
        {
            _pending = pending with { IsPending = false };
        }
    }

    /// <summary>Ends request <paramref name="requestVersion"/> if it is still pending.</summary>
    internal void Clear(long requestVersion)
    {
        var pending = _pending;
        if (pending.IsPending && pending.RequestVersion == requestVersion)
            _pending = pending with { IsPending = false };
    }

    /// <summary>Ends the pending request if it targets the published snapshot.</summary>
    internal void ClearForCurrentSnapshot()
    {
        LayoutSnapshot? snapshot = _published.Snapshot;
        if (snapshot is null)
        {
            Reset();
            return;
        }

        var pending = _pending;
        if (pending.IsPending &&
            pending.SnapshotGeneration == _published.Generation &&
            pending.AutomationDocumentGeneration == snapshot.AutomationDocumentGeneration)
        {
            Clear(pending.RequestVersion);
        }
    }

    /// <summary>Forgets the pending request. The version counter keeps counting.</summary>
    internal void Reset() => _pending = default;
}
