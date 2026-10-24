using MarkdownRenderer.Document;

namespace MarkdownRenderer.Controls;

/// <summary>
/// Pure state for touch text selection. Pointer routing remains in the WinUI
/// control, while this type makes gesture ordering, cancellation, and teardown
/// deterministic and independently testable.
/// </summary>
internal sealed class MarkdownTouchSelectionSession
{
    private bool _selectionWasActiveBeforeContact;

    public TouchSelectionState State { get; private set; }
    public uint ContactPointerId { get; private set; }
    public TouchContactGeometry Contact { get; private set; }
    public bool HasContact => ContactPointerId != 0;
    public bool ShouldShowHandles => State is
        TouchSelectionState.Selected or
        TouchSelectionState.DraggingStartHandle or
        TouchSelectionState.DraggingEndHandle or
        TouchSelectionState.ContextMenu;

    public bool BeginContact(
        uint pointerId,
        TouchContactGeometry contact,
        bool selectionIsActive = false)
    {
        if (State == TouchSelectionState.Disposed || pointerId == 0)
            return false;

        // A touch-selection gesture has exactly one owner. Secondary contacts
        // (including palm contacts synthesized by some digitizers) must not
        // replace the primary pointer or its contact geometry.
        if (ContactPointerId != 0 && ContactPointerId != pointerId)
            return false;

        if (ContactPointerId == 0)
            _selectionWasActiveBeforeContact = selectionIsActive;

        ContactPointerId = pointerId;
        Contact = contact;
        if (State is TouchSelectionState.Idle or TouchSelectionState.Canceled)
            State = TouchSelectionState.Contact;
        return true;
    }

    public void UpdateContact(uint pointerId, TouchContactGeometry contact)
    {
        if (State == TouchSelectionState.Disposed || ContactPointerId != pointerId)
            return;

        Contact = contact;
    }

    public void Select()
    {
        if (State != TouchSelectionState.Disposed &&
            State is not TouchSelectionState.DraggingStartHandle and
            not TouchSelectionState.DraggingEndHandle)
        {
            State = TouchSelectionState.Selected;
        }
    }

    public bool BeginHandleDrag(uint pointerId, bool startHandle)
    {
        if (State == TouchSelectionState.Disposed || pointerId == 0 || !ShouldShowHandles ||
            (ContactPointerId != 0 && ContactPointerId != pointerId))
        {
            return false;
        }

        ContactPointerId = pointerId;
        State = startHandle
            ? TouchSelectionState.DraggingStartHandle
            : TouchSelectionState.DraggingEndHandle;
        return true;
    }

    public void CompleteHandleDrag(uint pointerId)
    {
        if (State == TouchSelectionState.Disposed || ContactPointerId != pointerId)
            return;

        ContactPointerId = 0;
        Contact = default;
        _selectionWasActiveBeforeContact = false;
        State = TouchSelectionState.Selected;
    }

    public void OpenContextMenu()
    {
        if (State != TouchSelectionState.Disposed)
            State = TouchSelectionState.ContextMenu;
    }

    public void CloseContextMenu(bool selectionIsActive)
    {
        if (State != TouchSelectionState.ContextMenu)
            return;

        State = selectionIsActive
            ? TouchSelectionState.Selected
            : TouchSelectionState.Idle;
    }

    public void EndContact(uint pointerId)
    {
        if (State == TouchSelectionState.Disposed || ContactPointerId != pointerId)
            return;

        ContactPointerId = 0;
        Contact = default;
        _selectionWasActiveBeforeContact = false;
        if (State == TouchSelectionState.Contact)
            State = TouchSelectionState.Idle;
    }

    public bool Cancel(uint pointerId)
        => CancelContact(pointerId) != TouchSelectionCancelResult.NotOwner;

    /// <summary>
    /// Cancels the current contact and reports whether its selection range was
    /// committed before that contact or was created by the canceled gesture.
    /// The control uses this result to clear only a range that cancellation
    /// invalidates.
    /// </summary>
    public TouchSelectionCancelResult CancelContact(uint pointerId)
    {
        if (State == TouchSelectionState.Disposed || pointerId == 0 ||
            ContactPointerId != pointerId)
        {
            return TouchSelectionCancelResult.NotOwner;
        }

        ContactPointerId = 0;
        Contact = default;
        bool selectionWasActiveBeforeContact = _selectionWasActiveBeforeContact;
        _selectionWasActiveBeforeContact = false;
        bool wasDraggingHandle = State is
            TouchSelectionState.DraggingStartHandle or TouchSelectionState.DraggingEndHandle;
        bool preserveSelection = selectionWasActiveBeforeContact || wasDraggingHandle;
        State = preserveSelection
            ? TouchSelectionState.Selected
            : TouchSelectionState.Canceled;
        return preserveSelection
            ? TouchSelectionCancelResult.PreserveSelection
            : TouchSelectionCancelResult.ClearSelection;
    }

    public void Reset()
    {
        if (State == TouchSelectionState.Disposed)
            return;

        ContactPointerId = 0;
        Contact = default;
        _selectionWasActiveBeforeContact = false;
        State = TouchSelectionState.Idle;
    }

    /// <summary>
    /// Restores only the selected-handle presentation after a successful
    /// same-document relayout. A new or stale pointer owner must never be
    /// carried across the snapshot boundary.
    /// </summary>
    public bool TryRestoreSelectedPresentation()
    {
        if (State != TouchSelectionState.Idle || ContactPointerId != 0)
            return false;

        State = TouchSelectionState.Selected;
        return true;
    }

    public void Dispose()
    {
        ContactPointerId = 0;
        Contact = default;
        _selectionWasActiveBeforeContact = false;
        State = TouchSelectionState.Disposed;
    }
}

internal enum TouchSelectionCancelResult
{
    NotOwner,
    PreserveSelection,
    ClearSelection,
}

/// <summary>
/// Binds a pending touch-handle presentation restore to the exact committed
/// snapshot and selection range that initiated the relayout.
/// </summary>
internal readonly record struct TouchSelectionPresentationRestoreIntent
{
    public TouchSelectionPresentationRestoreIntent(long snapshotGeneration, DocumentRange selectionRange)
    {
        SnapshotGeneration = snapshotGeneration;
        SelectionRange = selectionRange.Normalized();
    }

    public long SnapshotGeneration { get; }
    public DocumentRange SelectionRange { get; }

    public bool MatchesRange(DocumentRange selectionRange) =>
        !selectionRange.IsEmpty &&
        SelectionRange == selectionRange.Normalized();

    public bool Matches(long snapshotGeneration, DocumentRange selectionRange) =>
        SnapshotGeneration == snapshotGeneration &&
        MatchesRange(selectionRange);
}

internal enum TouchSelectionState
{
    Idle,
    Contact,
    Selected,
    DraggingStartHandle,
    DraggingEndHandle,
    ContextMenu,
    Canceled,
    Disposed,
}

internal readonly struct TouchContactGeometry
{
    public TouchContactGeometry(double X, double Y, double Width, double Height)
    {
        this.X = X;
        this.Y = Y;
        this.Width = Width;
        this.Height = Height;
    }

    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;
}
