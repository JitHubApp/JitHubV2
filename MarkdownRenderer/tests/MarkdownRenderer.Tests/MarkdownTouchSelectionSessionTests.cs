using MarkdownRenderer.Controls;
using MarkdownRenderer.Document;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class MarkdownTouchSelectionSessionTests
{
    [Fact]
    public void ContactSelectionMenuAndCloseFollowExpectedStates()
    {
        var session = new MarkdownTouchSelectionSession();

        session.BeginContact(7, new TouchContactGeometry(10, 20, 18, 22));
        Assert.Equal(TouchSelectionState.Contact, session.State);
        Assert.True(session.HasContact);

        session.Select();
        Assert.Equal(TouchSelectionState.Selected, session.State);
        Assert.True(session.ShouldShowHandles);

        session.EndContact(7);
        session.OpenContextMenu();
        Assert.Equal(TouchSelectionState.ContextMenu, session.State);
        session.CloseContextMenu(selectionIsActive: true);
        Assert.Equal(TouchSelectionState.Selected, session.State);
    }

    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 4)]
    public void HandleDragCancellationPreservesCommittedSelection(
        bool startHandle,
        int expectedState)
    {
        var session = new MarkdownTouchSelectionSession();
        session.Select();

        Assert.True(session.BeginHandleDrag(11, startHandle));
        Assert.Equal((TouchSelectionState)expectedState, session.State);
        session.Select();
        Assert.Equal((TouchSelectionState)expectedState, session.State);
        session.Cancel(11);

        Assert.Equal(TouchSelectionState.Selected, session.State);
        Assert.True(session.ShouldShowHandles);
        Assert.False(session.HasContact);
    }

    [Fact]
    public void SwipeContactEndsWithoutCreatingSelection()
    {
        var session = new MarkdownTouchSelectionSession();
        session.BeginContact(3, new TouchContactGeometry(2, 4, 20, 24));
        session.UpdateContact(3, new TouchContactGeometry(2, 80, 20, 24));
        session.EndContact(3);

        Assert.Equal(TouchSelectionState.Idle, session.State);
        Assert.False(session.ShouldShowHandles);
    }

    [Fact]
    public void SecondaryContactCannotReplacePrimaryOwnerOrGeometry()
    {
        var session = new MarkdownTouchSelectionSession();
        var primary = new TouchContactGeometry(2, 4, 20, 24);

        Assert.True(session.BeginContact(3, primary));
        Assert.False(session.BeginContact(4, new TouchContactGeometry(100, 120, 40, 44)));

        Assert.Equal((uint)3, session.ContactPointerId);
        Assert.Equal(primary, session.Contact);
        Assert.False(session.Cancel(4));
        Assert.Equal(TouchSelectionState.Contact, session.State);
        Assert.True(session.HasContact);
    }

    [Fact]
    public void SecondaryContactCannotTakeOverHandleDrag()
    {
        var session = new MarkdownTouchSelectionSession();
        var primary = new TouchContactGeometry(2, 4, 20, 24);

        Assert.True(session.BeginContact(3, primary));
        session.Select();

        Assert.False(session.BeginHandleDrag(4, startHandle: true));
        Assert.Equal(TouchSelectionState.Selected, session.State);
        Assert.Equal((uint)3, session.ContactPointerId);
        Assert.Equal(primary, session.Contact);
    }

    [Fact]
    public void UnownedCancellationCannotClearCommittedSelection()
    {
        var session = new MarkdownTouchSelectionSession();
        session.Select();

        Assert.False(session.Cancel(99));

        Assert.Equal(TouchSelectionState.Selected, session.State);
        Assert.True(session.ShouldShowHandles);
    }

    [Fact]
    public void CancelingLaterOrdinaryContactPreservesCommittedSelectionAndReleasesContact()
    {
        var session = new MarkdownTouchSelectionSession();
        session.Select();

        var transientContact = new TouchContactGeometry(30, 40, 20, 24);
        Assert.True(session.BeginContact(12, transientContact, selectionIsActive: true));
        Assert.Equal(TouchSelectionState.Selected, session.State);
        Assert.True(session.HasContact);

        Assert.True(session.Cancel(12));

        Assert.Equal(TouchSelectionState.Selected, session.State);
        Assert.True(session.ShouldShowHandles);
        Assert.False(session.HasContact);
        Assert.Equal(default, session.Contact);
    }

    [Fact]
    public void CancelingFirstContactThatCreatedSelectionDoesNotKeepCanceledGesture()
    {
        var session = new MarkdownTouchSelectionSession();
        Assert.True(session.BeginContact(12, new TouchContactGeometry(30, 40, 20, 24)));
        session.Select();

        Assert.True(session.Cancel(12));

        Assert.Equal(TouchSelectionState.Canceled, session.State);
        Assert.False(session.ShouldShowHandles);
        Assert.False(session.HasContact);
    }

    [Fact]
    public void ResetAndDisposeClearAllTransientState()
    {
        var session = new MarkdownTouchSelectionSession();
        session.BeginContact(5, new TouchContactGeometry(1, 2, 3, 4));
        session.Select();
        session.Reset();

        Assert.Equal(TouchSelectionState.Idle, session.State);
        Assert.False(session.HasContact);

        session.Dispose();
        session.BeginContact(9, new TouchContactGeometry(4, 5, 6, 7));
        Assert.Equal(TouchSelectionState.Disposed, session.State);
        Assert.False(session.HasContact);
    }

    [Fact]
    public void RelayoutRestoreReestablishesOnlyHandlePresentationForBoundSnapshotAndRange()
    {
        var session = new MarkdownTouchSelectionSession();
        Assert.True(session.BeginContact(5, new TouchContactGeometry(1, 2, 3, 4)));
        session.Select();
        session.EndContact(5);

        var range = new DocumentRange(
            new DocumentPosition(2, 1, 3),
            new DocumentPosition(2, 1, 8));
        var intent = new TouchSelectionPresentationRestoreIntent(17, range);

        // A relayout retires the old touch/contact owner while retaining its
        // committed document selection in the control.
        session.Reset();
        Assert.Equal(TouchSelectionState.Idle, session.State);
        Assert.False(session.HasContact);

        Assert.True(intent.Matches(17, range));
        Assert.False(intent.Matches(18, range));
        Assert.True(intent.MatchesRange(range));
        Assert.False(intent.MatchesRange(default));
        Assert.False(intent.Matches(
            17,
            new DocumentRange(
                range.Start,
                new DocumentPosition(2, 1, 9))));

        var rebasedIntent = new TouchSelectionPresentationRestoreIntent(18, range);
        Assert.True(rebasedIntent.Matches(18, range));
        Assert.False(rebasedIntent.Matches(17, range));
        Assert.True(session.TryRestoreSelectedPresentation());

        Assert.Equal(TouchSelectionState.Selected, session.State);
        Assert.True(session.ShouldShowHandles);
        Assert.False(session.HasContact);
        Assert.Equal(default, session.Contact);
    }

    [Fact]
    public void RelayoutRestoreDoesNotReplaceAContactAcceptedDuringRebuild()
    {
        var session = new MarkdownTouchSelectionSession();
        session.Reset();

        var freshContact = new TouchContactGeometry(11, 13, 17, 19);
        Assert.True(session.BeginContact(23, freshContact));

        Assert.False(session.TryRestoreSelectedPresentation());
        Assert.Equal(TouchSelectionState.Contact, session.State);
        Assert.Equal((uint)23, session.ContactPointerId);
        Assert.Equal(freshContact, session.Contact);
    }
}
