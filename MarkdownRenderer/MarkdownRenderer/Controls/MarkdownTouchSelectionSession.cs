namespace MarkdownRenderer.Controls;

/// <summary>
/// Pure state for touch text selection. Pointer routing remains in the WinUI
/// control, while this type makes gesture ordering, cancellation, and teardown
/// deterministic and independently testable.
/// </summary>
internal sealed class MarkdownTouchSelectionSession
{
    public TouchSelectionState State { get; private set; }
    public uint ContactPointerId { get; private set; }
    public TouchContactGeometry Contact { get; private set; }
    public bool HasContact => ContactPointerId != 0;
    public bool ShouldShowHandles => State is
        TouchSelectionState.Selected or
        TouchSelectionState.DraggingStartHandle or
        TouchSelectionState.DraggingEndHandle or
        TouchSelectionState.ContextMenu;

    public bool BeginContact(uint pointerId, TouchContactGeometry contact)
    {
        if (State == TouchSelectionState.Disposed || pointerId == 0)
            return false;

        // A touch-selection gesture has exactly one owner. Secondary contacts
        // (including palm contacts synthesized by some digitizers) must not
        // replace the primary pointer or its contact geometry.
        if (ContactPointerId != 0 && ContactPointerId != pointerId)
            return false;

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
        if (State == TouchSelectionState.Contact)
            State = TouchSelectionState.Idle;
    }

    public bool Cancel(uint pointerId)
    {
        if (State == TouchSelectionState.Disposed || pointerId == 0 ||
            ContactPointerId != pointerId)
        {
            return false;
        }

        ContactPointerId = 0;
        Contact = default;
        State = State is TouchSelectionState.DraggingStartHandle or TouchSelectionState.DraggingEndHandle
            ? TouchSelectionState.Selected
            : TouchSelectionState.Canceled;
        return true;
    }

    public void Reset()
    {
        if (State == TouchSelectionState.Disposed)
            return;

        ContactPointerId = 0;
        Contact = default;
        State = TouchSelectionState.Idle;
    }

    public void Dispose()
    {
        ContactPointerId = 0;
        Contact = default;
        State = TouchSelectionState.Disposed;
    }
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

internal readonly record struct TouchContactGeometry(
    double X,
    double Y,
    double Width,
    double Height)
{
    public double CenterX => X + Width / 2.0;
    public double CenterY => Y + Height / 2.0;
}
