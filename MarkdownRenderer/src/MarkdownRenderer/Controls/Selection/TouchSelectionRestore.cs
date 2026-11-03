using MarkdownRenderer.Document;
using MarkdownRenderer.Selection;

namespace MarkdownRenderer.Controls.Selection;

/// <summary>
/// Carries a touch selection's handle presentation across a relayout. Owns
/// the one pending restore, bound to the committed snapshot and exact range
/// that started the rebuild, and decides at commit whether it still applies.
/// </summary>
internal sealed class TouchSelectionRestore
{
    private readonly PublishedLayout _published;
    private readonly SelectionController _selection;
    private readonly MarkdownTouchSelectionSession _touchSelection;
    private TouchSelectionPresentationRestoreIntent? _pending;

    internal TouchSelectionRestore(
        PublishedLayout published,
        SelectionController selection,
        MarkdownTouchSelectionSession touchSelection)
    {
        _published = published;
        _selection = selection;
        _touchSelection = touchSelection;
    }

    /// <summary>The pending restore, captured with a replacement publication.</summary>
    internal TouchSelectionPresentationRestoreIntent? Pending => _pending;

    internal void Cancel() => _pending = null;

    /// <summary>
    /// Records the restore for a rebuild that starts now: a touch selection
    /// showing handles is restored, and an earlier restore survives only for
    /// the same snapshot and range.
    /// </summary>
    internal void Capture()
    {
        if (_published.Snapshot is null || !_selection.IsActive)
        {
            _pending = null;
            return;
        }

        long snapshotGeneration = _published.Generation;
        DocumentRange selectionRange = _selection.Range.Normalized();
        if (_touchSelection.ShouldShowHandles)
        {
            _pending = new TouchSelectionPresentationRestoreIntent(snapshotGeneration, selectionRange);
            return;
        }

        if (_pending is not { } pending ||
            !pending.Matches(snapshotGeneration, selectionRange))
        {
            _pending = null;
        }
    }

    /// <summary>
    /// SelectionController may publish a change notification for a
    /// presentation-only update. Keep a pending restore when the exact range
    /// is unchanged; a real range change invalidates it. Generation is
    /// intentionally checked at commit, because snapshot publication can
    /// notify automation before the restore phase finishes.
    /// </summary>
    internal void OnSelectionChanged()
    {
        if (_pending is { } restoreIntent &&
            !restoreIntent.MatchesRange(_selection.Range))
        {
            _pending = null;
        }
    }

    /// <summary>
    /// Applies the restore captured with a same-document replacement that
    /// preserved the selection. Returns true when the touch presentation was
    /// restored and the handles need an update.
    /// </summary>
    internal bool TryRestoreAfterReplacement(
        TouchSelectionPresentationRestoreIntent? captured,
        long sourceGeneration,
        long generation,
        bool hasPendingRebuild)
    {
        // Snapshot publication and dependency-property callbacks can reenter
        // selection or request a newer rebuild. Revalidate the original token
        // against live state at the point of use rather than relying on
        // eligibility captured before those callbacks.
        bool stillMatches =
            _selection.IsActive &&
            captured is { } capturedRestoreIntent &&
            _pending is { } currentRestoreIntent &&
            currentRestoreIntent == capturedRestoreIntent &&
            capturedRestoreIntent.Matches(sourceGeneration, _selection.Range);

        if (stillMatches && !hasPendingRebuild)
        {
            bool restored = _touchSelection.TryRestoreSelectedPresentation();
            _pending = null;
            return restored;
        }

        if (stillMatches && hasPendingRebuild)
        {
            // Rebase only across a successful same-document commit while a
            // newer rebuild is already queued. The request stays bound to
            // this exact range and the just-published snapshot.
            _pending = new TouchSelectionPresentationRestoreIntent(generation, _selection.Range);
        }
        else
        {
            _pending = null;
        }
        return false;
    }
}
