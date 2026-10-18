using MarkdownRenderer.Controls;
using Windows.Foundation;
using Xunit;

namespace MarkdownRenderer.Tests;

public sealed class TouchTapSuppressionPolicyTests
{
    [Fact]
    public void RestoringSelectionHandlesAfterRelayoutDoesNotReviveRetiredGestureOwner()
    {
        const uint pointerId = 31;
        var policy = new TouchTapSuppressionPolicy();
        var session = new MarkdownTouchSelectionSession();
        Assert.True(policy.TryBeginContact(pointerId, out long oldGesture));
        Assert.True(session.BeginContact(pointerId, new TouchContactGeometry(8, 12, 16, 20)));
        session.Select();
        policy.EndContact(pointerId);
        session.EndContact(pointerId);

        policy.Reset();
        session.Reset();
        Assert.True(session.TryRestoreSelectedPresentation());

        Assert.True(session.ShouldShowHandles);
        Assert.False(session.HasContact);
        Assert.False(policy.CanDispatchRecognizedGesture(oldGesture, hasDeferredOwner: false));
        Assert.True(policy.RequiresAcceptedContactForRecognizedGesture);
    }

    [Fact]
    public void RightTapAfterReleaseRetainsOwnerForTheHeldContactsDuplicateTap()
    {
        var policy = new TouchTapSuppressionPolicy();
        var point = new Point(20, 40);
        Assert.True(policy.TryBeginContact(17, out long heldGesture));
        policy.SuppressTouchTap(heldGesture, point, nowTickMs: 1_000);
        policy.EndContact(17);

        // RightTapped/context-menu routing can happen after PointerReleased.
        // Capturing that event's epoch must not consume the association, since
        // the platform may still raise the held contact's duplicate Tapped.
        long rightTapGesture = policy.GetGestureEpochForRecognizedGesture();
        Assert.Equal(heldGesture, rightTapGesture);
        Assert.Equal(heldGesture, policy.GetGestureEpochForRecognizedGesture());
        Assert.True(policy.ShouldSuppressTap(heldGesture, nowTickMs: 1_300, point));
        policy.CompleteTap(heldGesture);

        Assert.Equal(0L, policy.GetGestureEpochForRecognizedGesture());
        Assert.False(policy.ShouldSuppressTap(0, nowTickMs: 1_301, point));
    }

    [Fact]
    public void TapObservedBeforePointerReleaseDoesNotLeaveACompletedOwner()
    {
        var policy = new TouchTapSuppressionPolicy();
        var point = new Point(20, 40);
        Assert.True(policy.TryBeginContact(17, out long gesture));
        policy.SuppressTouchTap(gesture, point, nowTickMs: 1_000);

        Assert.True(policy.ShouldSuppressTap(gesture, nowTickMs: 1_100, point));
        policy.CompleteTap(gesture);
        policy.EndContact(17);

        Assert.Equal(0L, policy.GetGestureEpochForRecognizedGesture());
        Assert.False(policy.ShouldSuppressTap(gesture, nowTickMs: 1_200, point));
    }

    [Fact]
    public void NewNearbyLinkedTextContactIsNotSuppressedByPriorHold()
    {
        var policy = new TouchTapSuppressionPolicy();
        var holdPoint = new Point(20, 40);
        var nearbyLinkPoint = new Point(52, 40);

        Assert.True(policy.TryBeginContact(17, out long heldGesture));
        policy.SuppressTouchTap(heldGesture, holdPoint, nowTickMs: 1_000);
        policy.EndContact(17);

        // A context-menu gesture after release retains the old owner until a
        // new accepted primary contact starts.
        Assert.Equal(heldGesture, policy.GetGestureEpochForRecognizedGesture());
        Assert.True(policy.TryBeginContact(18, out long linkGesture));
        Assert.NotEqual(heldGesture, linkGesture);
        policy.EndContact(18);

        Assert.False(policy.ShouldSuppressTap(linkGesture, nowTickMs: 1_500, nearbyLinkPoint));
    }

    [Fact]
    public void ReusingPointerIdAfterReleaseCreatesANewGestureEpoch()
    {
        var policy = new TouchTapSuppressionPolicy();
        var point = new Point(20, 40);

        Assert.True(policy.TryBeginContact(7, out long firstGesture));
        policy.SuppressTouchTap(firstGesture, point, nowTickMs: 1_000);
        policy.EndContact(7);

        Assert.True(policy.TryBeginContact(7, out long secondGesture));
        Assert.NotEqual(firstGesture, secondGesture);
        Assert.False(policy.ShouldSuppressTap(secondGesture, nowTickMs: 1_200, point));
    }

    [Fact]
    public void SecondaryContactCannotRetirePrimaryOwnerSuppression()
    {
        var policy = new TouchTapSuppressionPolicy();
        var point = new Point(20, 40);
        Assert.True(policy.TryBeginContact(17, out long primaryGesture));
        policy.SuppressTouchTap(primaryGesture, point, nowTickMs: 1_000);

        Assert.False(policy.TryBeginContact(18, out long secondaryGesture));
        Assert.Equal(0L, secondaryGesture);
        Assert.Equal(primaryGesture, policy.GetGestureEpochForRecognizedGesture());
        Assert.True(policy.ShouldSuppressTap(primaryGesture, nowTickMs: 1_200, point));

        policy.EndContact(17);
        Assert.True(policy.TryBeginContact(18, out long nextPrimaryGesture));
        Assert.NotEqual(primaryGesture, nextPrimaryGesture);
        Assert.False(policy.ShouldSuppressTap(nextPrimaryGesture, nowTickMs: 1_300, point));
    }

    [Fact]
    public void UnownedCancellationDoesNotClearPrimaryOwnerSuppression()
    {
        var policy = new TouchTapSuppressionPolicy();
        var point = new Point(20, 40);
        Assert.True(policy.TryBeginContact(17, out long primaryGesture));
        policy.SuppressTouchTap(primaryGesture, point, nowTickMs: 1_000);

        policy.CancelContact(18);

        Assert.True(policy.ShouldSuppressTap(primaryGesture, nowTickMs: 1_200, point));
    }

    [Fact]
    public void CancelingOwningContactClearsItsDuplicateAndCompletedGestureState()
    {
        var policy = new TouchTapSuppressionPolicy();
        var point = new Point(20, 40);
        Assert.True(policy.TryBeginContact(17, out long canceledGesture));
        policy.SuppressTouchTap(canceledGesture, point, nowTickMs: 1_000);

        policy.CancelContact(17);

        Assert.Equal(0L, policy.GetGestureEpochForRecognizedGesture());
        Assert.False(policy.ShouldSuppressTap(canceledGesture, nowTickMs: 1_200, point));
        Assert.False(policy.ShouldSuppressTap(0, nowTickMs: 1_200, point));
    }

    [Fact]
    public void NoContactFallbackSuppressesOneNearbySyntheticDuplicate()
    {
        var policy = new TouchTapSuppressionPolicy();
        var point = new Point(20, 40);
        policy.SuppressTouchTap(gestureEpoch: 0, point, nowTickMs: 1_000);

        Assert.True(policy.ShouldSuppressTap(gestureEpoch: 0, nowTickMs: 1_200, point));
        policy.CompleteTap(gestureEpoch: 0);

        // A later synthetic or accessibility-generated tap is not allowed to
        // inherit the previous contact's one-shot duplicate token.
        Assert.False(policy.ShouldSuppressTap(gestureEpoch: 0, nowTickMs: 1_300, point));
    }

    [Fact]
    public void AcceptedContactInvalidatesNoContactFallback()
    {
        var policy = new TouchTapSuppressionPolicy();
        var point = new Point(20, 40);
        policy.SuppressTouchTap(gestureEpoch: 0, point, nowTickMs: 1_000);

        Assert.True(policy.TryBeginContact(18, out long gesture));

        Assert.False(policy.ShouldSuppressTap(gesture, nowTickMs: 1_200, point));
    }

    [Fact]
    public void DoubleTapSuppressionBelongsToTheSecondContactOnly()
    {
        var policy = new TouchTapSuppressionPolicy();
        var point = new Point(20, 40);

        Assert.True(policy.TryBeginContact(9, out long firstTapGesture));
        policy.EndContact(9);
        policy.CompleteTap(firstTapGesture);

        Assert.True(policy.TryBeginContact(9, out long doubleTapGesture));
        Assert.NotEqual(firstTapGesture, doubleTapGesture);
        policy.SuppressTouchTap(doubleTapGesture, point, nowTickMs: 1_000);
        policy.EndContact(9);

        Assert.True(policy.ShouldSuppressTap(doubleTapGesture, nowTickMs: 1_200, point));
        Assert.False(policy.ShouldSuppressTap(firstTapGesture, nowTickMs: 1_200, point));
        policy.CompleteTap(doubleTapGesture);

        Assert.True(policy.TryBeginContact(9, out long laterTapGesture));
        Assert.False(policy.ShouldSuppressTap(laterTapGesture, nowTickMs: 1_300, point));
    }

    [Fact]
    public void ResetClearsStaleSuppressionAndKeepsOldDeferredEpochInvalid()
    {
        var policy = new TouchTapSuppressionPolicy();
        var point = new Point(20, 40);
        Assert.True(policy.TryBeginContact(7, out long oldGesture));
        policy.SuppressTouchTap(oldGesture, point, nowTickMs: 1_000);
        policy.EndContact(7);

        policy.Reset();

        Assert.False(policy.ShouldSuppressTap(oldGesture, nowTickMs: 1_200, point));
        Assert.Equal(0L, policy.GetGestureEpochForRecognizedGesture());
        Assert.True(policy.TryBeginContact(7, out long newGesture));
        Assert.NotEqual(oldGesture, newGesture);
        Assert.False(policy.ShouldSuppressTap(oldGesture, nowTickMs: 1_300, point));
    }

    [Fact]
    public void DeadlineAndDistanceRemainBoundsForTheOwningGesture()
    {
        var policy = new TouchTapSuppressionPolicy();
        var origin = new Point(20, 40);
        Assert.True(policy.TryBeginContact(17, out long gesture));
        policy.SuppressTouchTap(gesture, origin, nowTickMs: 1_000);

        Assert.True(policy.ShouldSuppressTap(gesture, nowTickMs: 2_500, origin));
        Assert.False(policy.ShouldSuppressTap(gesture, nowTickMs: 2_501, origin));
        Assert.False(policy.ShouldSuppressTap(gesture, nowTickMs: 1_200, new Point(69, 40)));
    }

    [Fact]
    public void TapOutsideRadiusCompletesOwnerWithoutConsumingIt()
    {
        var policy = new TouchTapSuppressionPolicy();
        var origin = new Point(20, 40);
        Assert.True(policy.TryBeginContact(17, out long gesture));
        policy.SuppressTouchTap(gesture, origin, nowTickMs: 1_000);
        policy.EndContact(17);

        Assert.False(policy.ShouldSuppressTap(gesture, nowTickMs: 1_200, new Point(69, 40)));
        policy.CompleteTap(gesture);
        Assert.False(policy.ShouldSuppressTap(gesture, nowTickMs: 1_200, origin));
    }

    [Fact]
    public void TapWithoutAnyPressHasNoGestureAndDoesNotInheritOldSuppression()
    {
        var policy = new TouchTapSuppressionPolicy();

        Assert.Equal(0L, policy.GetGestureEpochForRecognizedGesture());
        Assert.True(policy.CanDispatchRecognizedGesture(gestureEpoch: 0, hasDeferredOwner: false));
        Assert.False(policy.ShouldSuppressTap(0, nowTickMs: 1_200, new Point(20, 40)));
    }

    [Fact]
    public void DroppedDeferredReleaseEndsContactAndPreservesCommittedSelection()
    {
        const uint pointerId = 17;
        var point = new Point(20, 40);
        var policy = new TouchTapSuppressionPolicy();
        var session = new MarkdownTouchSelectionSession();
        Assert.True(session.BeginContact(pointerId, new TouchContactGeometry(10, 20, 18, 22)));
        Assert.True(policy.TryBeginContact(pointerId, out long gestureEpoch));
        session.Select();
        policy.SuppressTouchTap(gestureEpoch, point, nowTickMs: 1_000);

        var queue = new DeferredInputQueue<DroppedTouchTerminal>(capacity: 2);
        Assert.True(queue.TryEnqueue(
            new DroppedTouchTerminal(pointerId, gestureEpoch, IsCanceled: false),
            pointerId,
            coalescible: false,
            blocksSnapshotInteraction: false));

        // ResetDeferredPointerInput must reconcile a terminal for the current
        // primary epoch before it clears the geometry-bound queue. Otherwise
        // the processed Press remains active forever after its Release is
        // discarded, and every later touch is rejected by TryBeginContact.
        Assert.True(queue.TryGetAt(0, out DroppedTouchTerminal dropped));
        Assert.False(dropped.IsCanceled);
        Assert.True(policy.TryEndContact(dropped.PointerId, dropped.GestureEpoch));
        session.EndContact(dropped.PointerId);
        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.False(session.HasContact);
        Assert.Equal(TouchSelectionState.Selected, session.State);
        Assert.True(session.ShouldShowHandles);
        Assert.Equal(gestureEpoch, policy.GetGestureEpochForRecognizedGesture());
        Assert.True(policy.ShouldSuppressTap(gestureEpoch, nowTickMs: 1_200, point));
        Assert.True(policy.TryBeginContact(18, out long nextGesture));
        Assert.NotEqual(gestureEpoch, nextGesture);
        Assert.True(session.BeginContact(
            18,
            new TouchContactGeometry(30, 40, 18, 22),
            selectionIsActive: true));
        Assert.Equal(TouchSelectionState.Selected, session.State);

        // If the platform reuses the old pointer ID, a terminal retained from
        // an earlier deferred batch cannot end its newer epoch.
        session.EndContact(18);
        policy.EndContact(18);
        Assert.True(policy.TryBeginContact(pointerId, out long reusedGesture));
        Assert.NotEqual(gestureEpoch, reusedGesture);
        Assert.False(policy.TryEndContact(pointerId, gestureEpoch));
        Assert.Equal(reusedGesture, policy.GetGestureEpochForPointer(pointerId));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DroppedDeferredCancelMatchesLiveCancellationSelectionDisposition(
        bool hadCommittedSelectionBeforeContact,
        bool expectedToPreserveSelection)
    {
        const uint pointerId = 17;
        var point = new Point(20, 40);
        var policy = new TouchTapSuppressionPolicy();
        var session = new MarkdownTouchSelectionSession();
        if (hadCommittedSelectionBeforeContact)
            session.Select();

        Assert.True(session.BeginContact(
            pointerId,
            new TouchContactGeometry(10, 20, 18, 22),
            selectionIsActive: hadCommittedSelectionBeforeContact));
        Assert.True(policy.TryBeginContact(pointerId, out long gestureEpoch));
        session.Select();
        policy.SuppressTouchTap(gestureEpoch, point, nowTickMs: 1_000);

        var queue = new DeferredInputQueue<DroppedTouchTerminal>(capacity: 2);
        Assert.True(queue.TryEnqueue(
            new DroppedTouchTerminal(pointerId, gestureEpoch, IsCanceled: true),
            pointerId,
            coalescible: false,
            blocksSnapshotInteraction: false));

        Assert.True(queue.TryGetAt(0, out DroppedTouchTerminal dropped));
        Assert.True(dropped.IsCanceled);
        Assert.True(policy.TryCancelContact(dropped.PointerId, dropped.GestureEpoch));
        TouchSelectionCancelResult actualDisposition = session.CancelContact(dropped.PointerId);
        Assert.Equal(
            expectedToPreserveSelection ? TouchSelectionCancelResult.PreserveSelection : TouchSelectionCancelResult.ClearSelection,
            actualDisposition);
        if (actualDisposition == TouchSelectionCancelResult.ClearSelection)
            session.Reset(); // Mirrors ClearSelectionForExternalInteraction.
        queue.Clear();

        Assert.Equal(0, queue.Count);
        Assert.False(session.HasContact);
        Assert.False(policy.ShouldSuppressTap(gestureEpoch, nowTickMs: 1_200, point));
        Assert.True(policy.TryBeginContact(18, out long nextGesture));
        Assert.NotEqual(gestureEpoch, nextGesture);
        Assert.True(policy.CanDispatchRecognizedGesture(nextGesture, hasDeferredOwner: false));
        Assert.Equal(
            hadCommittedSelectionBeforeContact,
            session.State == TouchSelectionState.Selected && session.ShouldShowHandles);

        policy.EndContact(18);
        Assert.True(policy.TryBeginContact(pointerId, out long reusedGesture));
        Assert.NotEqual(gestureEpoch, reusedGesture);
        Assert.False(policy.TryCancelContact(pointerId, gestureEpoch));
        Assert.Equal(reusedGesture, policy.GetGestureEpochForPointer(pointerId));
    }

    [Fact]
    public void DocumentReplacementRetiresSessionAndRejectsUnmatchedRecognizedGestureUntilNewContact()
    {
        const uint pointerId = 7;
        var policy = new TouchTapSuppressionPolicy();
        var session = new MarkdownTouchSelectionSession();
        Assert.True(session.BeginContact(pointerId, new TouchContactGeometry(1, 2, 3, 4)));
        Assert.True(policy.TryBeginContact(pointerId, out long oldGesture));
        session.Select();

        // Mirrors ResetInputForDocumentReplacement: discard old owner/queue,
        // reset only transient selection-session state (the control does not
        // clear the committed text range), then latch unmatched recognizer
        // events until a new accepted press supplies provenance.
        policy.Reset();
        session.Reset();
        policy.RequireAcceptedContactForRecognizedGesture();

        // Rebuild startup may reset transient state again after the source
        // callback; generic reset must not erase the replacement provenance
        // guard before the next physical touch arrives.
        policy.Reset();

        Assert.False(session.HasContact);
        Assert.Equal(TouchSelectionState.Idle, session.State);
        Assert.Equal(0L, policy.GetGestureEpochForRecognizedGesture());
        Assert.False(policy.CanDispatchRecognizedGesture(gestureEpoch: 0, hasDeferredOwner: false));
        Assert.False(policy.CanDispatchRecognizedGesture(oldGesture, hasDeferredOwner: false));
        Assert.True(policy.CanDispatchRecognizedGesture(gestureEpoch: 0, hasDeferredOwner: true));
        Assert.False(policy.ShouldSuppressTap(oldGesture, nowTickMs: 1_200, new Point(1, 2)));

        Assert.True(policy.TryBeginContact(8, out long newGesture));
        Assert.NotEqual(oldGesture, newGesture);
        Assert.True(session.BeginContact(8, new TouchContactGeometry(10, 20, 18, 22)));
        Assert.True(policy.CanDispatchRecognizedGesture(newGesture, hasDeferredOwner: false));
    }

    [Fact]
    public void TransientRelayoutResetRejectsStaleTouchGesturesUntilFreshContactIsAccepted()
    {
        const uint pointerId = 31;
        var policy = new TouchTapSuppressionPolicy();
        var session = new MarkdownTouchSelectionSession();
        Assert.True(session.BeginContact(pointerId, new TouchContactGeometry(8, 12, 16, 20)));
        Assert.True(policy.TryBeginContact(pointerId, out long oldGesture));
        session.Select();

        // This is the ordinary relayout/reset path, not document replacement:
        // it retains the committed selection in the control, but retires the
        // current contact and therefore must reject its delayed ID-less gestures.
        policy.Reset();
        session.Reset();

        // A second reset after the session has already been cleared must not
        // erase the provenance latch established by the first reset.
        policy.Reset();
        Assert.False(session.HasContact);
        Assert.Equal(TouchSelectionState.Idle, session.State);
        Assert.False(policy.CanDispatchRecognizedGesture(gestureEpoch: 0, hasDeferredOwner: false));
        Assert.False(policy.CanDispatchRecognizedGesture(oldGesture, hasDeferredOwner: false));
        Assert.False(policy.ShouldSuppressTap(oldGesture, nowTickMs: 1_100, new Point(8, 12)));

        // A Press already queued for geometry remains attributable; when FIFO
        // replay accepts it, that new contact supplies fresh provenance.
        Assert.True(policy.CanDispatchRecognizedGesture(gestureEpoch: 0, hasDeferredOwner: true));
        Assert.True(policy.TryBeginContact(pointerId, out long newGesture));
        Assert.NotEqual(oldGesture, newGesture);
        Assert.True(policy.CanDispatchRecognizedGesture(newGesture, hasDeferredOwner: false));
    }

    [Fact]
    public void ResetRetiringJustReleasedTouchRejectsItsLateRecognizedGesture()
    {
        const uint pointerId = 44;
        var policy = new TouchTapSuppressionPolicy();
        Assert.True(policy.TryBeginContact(pointerId, out long oldGesture));
        policy.EndContact(pointerId);
        Assert.Equal(oldGesture, policy.GetGestureEpochForRecognizedGesture());

        // The pointer has already released, but XAML may still deliver the
        // corresponding ID-less Tapped/RightTapped after this relayout reset.
        policy.Reset();

        Assert.False(policy.CanDispatchRecognizedGesture(gestureEpoch: 0, hasDeferredOwner: false));
        Assert.False(policy.CanDispatchRecognizedGesture(oldGesture, hasDeferredOwner: false));

        Assert.True(policy.TryBeginContact(pointerId, out long freshGesture));
        Assert.NotEqual(oldGesture, freshGesture);
        Assert.True(policy.CanDispatchRecognizedGesture(freshGesture, hasDeferredOwner: false));
    }

    [Fact]
    public void ResetWithoutAnyTouchSequenceKeepsSyntheticRecognizerFallback()
    {
        var policy = new TouchTapSuppressionPolicy();
        policy.Reset();

        Assert.False(policy.RequiresAcceptedContactForRecognizedGesture);
        Assert.True(policy.CanDispatchRecognizedGesture(gestureEpoch: 0, hasDeferredOwner: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResetRetiringUnownedOrSyntheticTouchProvenanceRequiresFreshContact(bool syntheticFallback)
    {
        var policy = new TouchTapSuppressionPolicy();
        var origin = new Point(20, 40);
        if (syntheticFallback)
        {
            policy.SuppressTouchTap(gestureEpoch: 0, origin, nowTickMs: 1_000);
        }
        else
        {
            policy.TrackUnownedContact(pointerId: 88, origin);
        }

        policy.Reset();

        Assert.False(policy.CanDispatchRecognizedGesture(gestureEpoch: 0, hasDeferredOwner: false));
        Assert.True(policy.TryBeginContact(pointerId: 99, out long freshGesture));
        Assert.True(policy.CanDispatchRecognizedGesture(freshGesture, hasDeferredOwner: false));
    }

    [Fact]
    public void ReplacementAdmissionGateCoversTheSharedTouchRecognizerDispatchPath()
    {
        string repositoryRoot = FindRepositoryRoot();
        string pointerInputPath = Path.Combine(
            repositoryRoot,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.PointerInput.cs");
        string controlPath = Path.Combine(
            repositoryRoot,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Controls",
            "MarkdownRendererControl.cs");
        string pointerInput = File.ReadAllText(pointerInputPath);
        string control = File.ReadAllText(controlPath);
        int dispatchStart = pointerInput.IndexOf(
            "private bool DispatchRecognizedGesture(",
            StringComparison.Ordinal);
        Assert.True(dispatchStart >= 0);
        int dispatchEnd = pointerInput.IndexOf(
            "private bool EnqueueRecognizedGesture(",
            dispatchStart,
            StringComparison.Ordinal);
        Assert.True(dispatchEnd > dispatchStart);
        string dispatch = pointerInput[dispatchStart..dispatchEnd];

        int touchBranch = dispatch.IndexOf("if (device == PointerDeviceType.Touch)", StringComparison.Ordinal);
        int admissionGate = dispatch.IndexOf("CanDispatchRecognizedGesture(", touchBranch, StringComparison.Ordinal);
        int directDispatch = dispatch.IndexOf("ProcessPointerInput(ref input)", admissionGate, StringComparison.Ordinal);
        int deferredDispatch = dispatch.IndexOf("EnqueueRecognizedGesture(snapshot, input)", admissionGate, StringComparison.Ordinal);
        Assert.True(touchBranch >= 0 && admissionGate > touchBranch);
        Assert.True(directDispatch > admissionGate && deferredDispatch > admissionGate);
        Assert.Contains("input.IgnoreUnattributableTouchGesture = true", dispatch[admissionGate..], StringComparison.Ordinal);

        int droppedTerminalStart = pointerInput.IndexOf(
            "private void RetireTouchOwnerForDroppedTerminalInput()",
            StringComparison.Ordinal);
        Assert.True(droppedTerminalStart >= 0);
        int cancelHelperStart = pointerInput.IndexOf(
            "private bool CancelTouchOwnerIfCurrent(",
            droppedTerminalStart,
            StringComparison.Ordinal);
        Assert.True(cancelHelperStart > droppedTerminalStart);
        string droppedTerminal = pointerInput[droppedTerminalStart..cancelHelperStart];
        Assert.Contains("CancelTouchOwnerIfCurrent(ownerPointerId, ownerGestureEpoch)", droppedTerminal, StringComparison.Ordinal);
        Assert.Contains("_touchSelection.CancelContact(ownerPointerId)", droppedTerminal, StringComparison.Ordinal);
        Assert.Contains(
            "cancelResult == TouchSelectionCancelResult.ClearSelection && _selection.IsActive",
            droppedTerminal,
            StringComparison.Ordinal);

        int replacementStart = control.IndexOf(
            "private void ResetInputForDocumentReplacement()",
            StringComparison.Ordinal);
        Assert.True(replacementStart >= 0);
        int replacementEnd = control.IndexOf("private void OnEngineChanged(", replacementStart, StringComparison.Ordinal);
        Assert.True(replacementEnd > replacementStart);
        string replacement = control[replacementStart..replacementEnd];
        int transientReset = replacement.IndexOf("ResetTransientInteractionState(clearSelection: false)", StringComparison.Ordinal);
        int requireNewContact = replacement.IndexOf("RequireAcceptedContactForRecognizedGesture()", StringComparison.Ordinal);
        Assert.True(transientReset >= 0 && requireNewContact > transientReset);

        int genericResetStart = control.IndexOf(
            "private void ResetTransientInteractionState(bool clearSelection)",
            StringComparison.Ordinal);
        Assert.True(genericResetStart >= 0);
        int genericResetEnd = control.IndexOf("private async Task RebuildAsync(", genericResetStart, StringComparison.Ordinal);
        Assert.True(genericResetEnd > genericResetStart);
        string genericReset = control[genericResetStart..genericResetEnd];
        Assert.Contains("_touchTapSuppression.Reset()", genericReset, StringComparison.Ordinal);
    }

    [Fact]
    public void UnloadRetiresQueuedTouchTerminalBeforeDiscardingItsGestureEpoch()
    {
        // Wiring guard, not a live WinUI unload test. The behavioral tests
        // separately exercise cancellation disposition and stale gestures.
        string control = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "MarkdownRenderer", "MarkdownRenderer", "Controls",
            "MarkdownRendererControl.cs"));
        int start = control.IndexOf("private void OnUnloaded()", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int end = control.IndexOf("private void ReleaseLoadedResources()", start, StringComparison.Ordinal);
        Assert.True(end > start);
        string unload = control[start..end];
        int retireTerminal = unload.IndexOf("ResetDeferredPointerInput();", StringComparison.Ordinal);
        int resetEpoch = unload.IndexOf("_touchTapSuppression.Reset();", StringComparison.Ordinal);
        Assert.True(retireTerminal >= 0 && resetEpoch > retireTerminal,
            "Unload must retire an exact-owner queued terminal before its gesture epoch is discarded.");

        int protectLateGesture = unload.IndexOf("_touchTapSuppression.PrepareForReset();", StringComparison.Ordinal);
        Assert.True(protectLateGesture >= 0 && protectLateGesture < retireTerminal,
            "Cancellation can clear ownership before Reset; protect late recognizer provenance first.");
        Assert.DoesNotContain("ClearSelection", unload, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void DeferredCancellationAtUnloadRetainsTheLateGestureBarrier(
        bool hadCommittedSelectionBeforeContact,
        bool expectedToPreserveSelection)
    {
        const uint pointerId = 17;
        TouchSelectionCancelResult expectedDisposition = expectedToPreserveSelection
            ? TouchSelectionCancelResult.PreserveSelection
            : TouchSelectionCancelResult.ClearSelection;
        var policy = new TouchTapSuppressionPolicy();
        var session = new MarkdownTouchSelectionSession();
        if (hadCommittedSelectionBeforeContact)
            session.Select();
        Assert.True(session.BeginContact(pointerId,
            new TouchContactGeometry(10, 20, 18, 22), hadCommittedSelectionBeforeContact));
        Assert.True(policy.TryBeginContact(pointerId, out long canceledEpoch));
        session.Select();

        // Mirrors only the pure ownership sequence, not the XAML removal:
        // latch provenance, reconcile the exact deferred Cancel, then reset.
        Assert.True(session.HasContact);
        policy.PrepareForReset();
        Assert.True(policy.TryCancelContact(pointerId, canceledEpoch));
        Assert.Equal(expectedDisposition, session.CancelContact(pointerId));
        policy.Reset();
        session.Reset();

        Assert.False(session.HasContact);
        Assert.Equal(0, policy.GetGestureEpochForPointerOrCompleted(pointerId));
        Assert.True(policy.RequiresAcceptedContactForRecognizedGesture);
        Assert.False(policy.CanDispatchRecognizedGesture(0, hasDeferredOwner: false));
        Assert.False(policy.CanDispatchRecognizedGesture(canceledEpoch, hasDeferredOwner: false));

        // Reusing the native pointer ID after reattachment establishes new
        // provenance; a terminal retained from the old epoch cannot cancel it.
        Assert.True(policy.TryBeginContact(pointerId, out long freshEpoch));
        Assert.NotEqual(canceledEpoch, freshEpoch);
        Assert.True(session.BeginContact(pointerId,
            new TouchContactGeometry(30, 40, 18, 22),
            selectionIsActive: expectedDisposition == TouchSelectionCancelResult.PreserveSelection));
        Assert.True(policy.CanDispatchRecognizedGesture(freshEpoch, hasDeferredOwner: false));
        Assert.False(policy.TryCancelContact(pointerId, canceledEpoch));
        Assert.Equal(freshEpoch, policy.GetGestureEpochForPointer(pointerId));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("completed")]
    [InlineData("unowned")]
    [InlineData("fallback")]
    [InlineData("suppressed")]
    [InlineData("empty")]
    public void PreparingForUnloadResetPreservesEveryExistingOwnershipKind(string ownershipKind)
    {
        var policy = new TouchTapSuppressionPolicy();
        var point = new Point(20, 40);
        switch (ownershipKind)
        {
            case "active":
            case "completed":
                Assert.True(policy.TryBeginContact(17, out _));
                if (ownershipKind == "completed")
                    policy.EndContact(17);
                break;
            case "unowned":
                policy.TrackUnownedContact(18, point);
                break;
            case "fallback":
                policy.SuppressTouchTap(0, point, nowTickMs: 1_000);
                break;
            case "suppressed":
                policy.SuppressTouchTap(123, point, nowTickMs: 1_000);
                break;
        }

        long activeEpoch = policy.GetGestureEpochForPointer(17);
        long activeOrCompletedEpoch = policy.GetGestureEpochForPointerOrCompleted(17);
        bool unownedContact = policy.HasUnownedContactNear(point);
        bool fallbackSuppressed = policy.ShouldSuppressTap(0, nowTickMs: 1_001, point);
        bool gestureSuppressed = policy.ShouldSuppressTap(123, nowTickMs: 1_001, point);

        policy.PrepareForReset();

        Assert.Equal(ownershipKind != "empty", policy.RequiresAcceptedContactForRecognizedGesture);
        Assert.Equal(activeEpoch, policy.GetGestureEpochForPointer(17));
        Assert.Equal(activeOrCompletedEpoch, policy.GetGestureEpochForPointerOrCompleted(17));
        Assert.Equal(unownedContact, policy.HasUnownedContactNear(point));
        Assert.Equal(fallbackSuppressed, policy.ShouldSuppressTap(0, nowTickMs: 1_001, point));
        Assert.Equal(gestureSuppressed, policy.ShouldSuppressTap(123, nowTickMs: 1_001, point));
        policy.Reset();
        Assert.Equal(0, policy.GetGestureEpochForPointerOrCompleted(17));
        Assert.False(policy.HasUnownedContactNear(point));
        Assert.False(policy.ShouldSuppressTap(0, nowTickMs: 1_001, point));
        Assert.False(policy.ShouldSuppressTap(123, nowTickMs: 1_001, point));
        Assert.Equal(ownershipKind != "empty", policy.RequiresAcceptedContactForRecognizedGesture);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }

    private readonly record struct DroppedTouchTerminal(
        uint PointerId,
        long GestureEpoch,
        bool IsCanceled);
}
