using System;
using System.IO;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Wiring guards only; actual touch lifecycle behavior remains unverified
/// until the opt-in probe is exercised on a touch device.
/// </summary>
public sealed class TouchLifecycleProbeSourceContractTests
{
    [Fact]
    public void ProbeUsesRealControlActionsAndRequiresPhysicalTerminalEvidence()
    {
        string sample = File.ReadAllText(RepositoryFile("MarkdownRenderer.Sample", "TouchLifecycleProbeWindow.cs"));
        string app = File.ReadAllText(RepositoryFile("MarkdownRenderer.Sample", "App.xaml.cs"));

        Assert.Contains("--renderer-touch-lifecycle-probe", app, StringComparison.Ordinal);
        Assert.Contains("new TouchLifecycleProbeWindow()", app, StringComparison.Ordinal);
        Assert.Contains("renderer.Dispose();", sample, StringComparison.Ordinal);
        // Capture loss is a real capture move performed inside the routed,
        // in-contact handle PointerMoved: WinUI raises PointerCaptureLost only
        // for a release made with a current pointer point (T1-022).
        Assert.Contains("_stealCaptureOnNextMove = true;", sample, StringComparison.Ordinal);
        Assert.Contains("_stealCaptureOnNextMove && kind == \"PointerMoved\" && pointerId == _handlePointerId &&", sample, StringComparison.Ordinal);
        Assert.Contains("args.Pointer.IsInContact", sample, StringComparison.Ordinal);
        Assert.Contains("handle.ReleasePointerCapture(args.Pointer);", sample, StringComparison.Ordinal);
        Assert.Contains("if (!_captureTarget.CapturePointer(args.Pointer))", sample, StringComparison.Ordinal);
        Assert.True(
            sample.IndexOf("handle.ReleasePointerCapture(args.Pointer);", StringComparison.Ordinal) <
            sample.IndexOf("if (!_captureTarget.CapturePointer(args.Pointer))", StringComparison.Ordinal),
            "The handle must release before the capture target can capture.");
        Assert.DoesNotContain("ReleasePointerCaptures();", sample, StringComparison.Ordinal);
        // T1-027: every phase change is published before the phase's own wait
        // can return, because the host coordinates input on the published phase.
        string tick = MethodBody(sample, "private void OnTick(DispatcherQueueTimer sender, object args)", "private void PerformLifecycleAction(");
        int phasePublish = tick.IndexOf("if (!string.Equals(_phase, _publishedPhase, StringComparison.Ordinal))", StringComparison.Ordinal);
        Assert.True(phasePublish >= 0 && phasePublish < tick.IndexOf("switch (_phase)", StringComparison.Ordinal),
            "A changed phase must be published before the phase switch can return early.");
        Assert.Contains("_publishedPhase = _phase;", MethodBody(sample, "private void Publish()", "private "), StringComparison.Ordinal);
        // WinUI routes no PointerCaptureLost for an app-initiated touch release;
        // the probe reports that limit only after the capture really moved and
        // the renderer's terminal cleanup was verified.
        Assert.Contains("private const string CaptureLossDeliveryLimit = \"TOUCH-CAPTURE-LOST-NOT-ROUTED:\";", sample, StringComparison.Ordinal);
        string captureLost = MethodBody(sample, "case \"awaitCaptureLost\":", "case \"awaitUnloaded\":");
        Assert.Contains("if (!_captureMoved)", captureLost, StringComparison.Ordinal);
        // The kept selection is the one at the capture move: earlier pending
        // handle moves may legitimately apply after the action tick.
        Assert.Contains("expectedSelection: _captureMoveState!", captureLost, StringComparison.Ordinal);
        Assert.Contains("_captureMoveState = Renderer.CaptureTouchLifecycleProbeState();", sample, StringComparison.Ordinal);
        Assert.True(
            captureLost.IndexOf("RequireCleanAfterContactAction(snapshot, preserveSelection: true", StringComparison.Ordinal) <
            captureLost.IndexOf("throw new InvalidOperationException(CaptureLossDeliveryLimit", StringComparison.Ordinal),
            "The delivery limit must be reported only after terminal cleanup is verified.");
        Assert.Contains("DetachRendererFromHost();", sample, StringComparison.Ordinal);
        Assert.Contains("HasLifecycleEvent(\"Unloaded\"", sample, StringComparison.Ordinal);
        Assert.Contains("HasLifecycleEvent(\"DisposalCompleted\"", sample, StringComparison.Ordinal);
        Assert.Contains("FindPointerTerminalAfter(_handlePointerId, _actionSequence)", sample, StringComparison.Ordinal);
        Assert.Contains("item.Device == \"Touch\" && item.IsInContact == false", sample, StringComparison.Ordinal);
        Assert.Contains("terminal.CorrelatedGestureEpoch != _handleGestureEpoch", sample, StringComparison.Ordinal);
        Assert.Contains("_phase = \"failed\"", sample, StringComparison.Ordinal);
        Assert.Contains("A failed contact is deliberately not replaced", sample, StringComparison.Ordinal);
        Assert.Contains("Capture loss is lifecycle evidence, not proof", sample, StringComparison.Ordinal);

        Assert.DoesNotContain("new PointerInput", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectAutomationRange", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("TouchSelectionSession", sample, StringComparison.Ordinal);
        string terminal = MethodBody(sample,
            "private TouchLifecycleProbeEvent? FindPointerTerminalAfter(",
            "private void DetachRendererFromHost()");
        Assert.Contains("item.Kind is \"PointerReleased\" or \"PointerCanceled\"", terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("PointerCaptureLost", terminal, StringComparison.Ordinal);

        // T1-026: a contact routed away from the window is a separate, narrower
        // terminal, used only for unload/dispose after an ordinary up had time
        // to arrive, and never the handle's own capture loss.
        string routedAway = MethodBody(sample,
            "private TouchLifecycleProbeEvent? FindRoutedAwayTerminalAfter(",
            "private TouchLifecycleProbeEvent? FindPointerTerminalAfter(");
        Assert.Contains("item.IsInContact == true && item.Kind == \"PointerCaptureLost\"", routedAway, StringComparison.Ordinal);
        Assert.Contains("item.Observer == \"Root\" && item.SourceAutomationId is not (StartHandleId or EndHandleId)", routedAway, StringComparison.Ordinal);
        string actionTerminal = MethodBody(sample, "case \"awaitActionTerminal\":", "case \"awaitReattached\":");
        Assert.Contains("_case is not (\"unload\" or \"dispose\")", actionTerminal, StringComparison.Ordinal);
        Assert.Contains("RoutedAwaySettleMilliseconds", actionTerminal, StringComparison.Ordinal);
        Assert.True(
            actionTerminal.IndexOf("FindPointerTerminalAfter(_handlePointerId, _actionSequence)", StringComparison.Ordinal) <
            actionTerminal.IndexOf("FindRoutedAwayTerminalAfter(_handlePointerId, _actionSequence)", StringComparison.Ordinal),
            "An ordinary terminal must be preferred over a routed-away report.");
    }

    [Fact]
    public void ControlSnapshotIsReadOnlyAndReportsTransientLifecycleState()
    {
        string control = File.ReadAllText(RepositoryFile("MarkdownRenderer", "Controls", "MarkdownRendererControl.DeferredTouchProbe.cs"));

        Assert.Contains("internal TouchLifecycleProbeState CaptureTouchLifecycleProbeState", control, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue?.HasThreadAccess != true", control, StringComparison.Ordinal);
        Assert.Contains("_touchSelection.ContactPointerId", control, StringComparison.Ordinal);
        Assert.Contains("_touchSelection.State.ToString()", control, StringComparison.Ordinal);
        Assert.Contains("_selectionHandleFrameSubscribed", control, StringComparison.Ordinal);
        Assert.Contains("_deferredPointerInput?.Count ?? 0", control, StringComparison.Ordinal);
        Assert.Contains("_deferredPointerCaptures.Count", control, StringComparison.Ordinal);
        Assert.Contains("_pointerRetryTimer is not null", control, StringComparison.Ordinal);
        Assert.Contains("_contextMenuOpen", control, StringComparison.Ordinal);
        Assert.Contains("_selection.IsActive", control, StringComparison.Ordinal);
        Assert.Contains("internal sealed record TouchLifecycleProbeState", control, StringComparison.Ordinal);

        string method = MethodBody(control,
            "internal TouchLifecycleProbeState CaptureTouchLifecycleProbeState(",
            "    /// <summary>");
        Assert.DoesNotContain("Reset", method, StringComparison.Ordinal);
        Assert.DoesNotContain("CapturePointer(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleasePointer", method, StringComparison.Ordinal);
        Assert.DoesNotContain("OnDeferredGesture", method, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureCleanupObservesRetainedNativeButtonsRatherThanVisualTreeAbsence()
    {
        string sample = File.ReadAllText(RepositoryFile("MarkdownRenderer.Sample", "TouchLifecycleProbeWindow.cs"));
        Assert.Contains("_startHandle ??= FindHandle(renderer, StartHandleId);", sample, StringComparison.Ordinal);
        Assert.Contains("_endHandle ??= FindHandle(renderer, EndHandleId);", sample, StringComparison.Ordinal);
        Assert.Contains("CaptureHandle(_startHandle, StartHandleId)", sample, StringComparison.Ordinal);
        Assert.Contains("CaptureHandle(_endHandle, EndHandleId)", sample, StringComparison.Ordinal);

        string capture = MethodBody(sample, "private static TouchLifecycleHandleCapture? CaptureHandle(", "private static Button? FindHandle(");
        Assert.Contains("button.PointerCaptures", capture, StringComparison.Ordinal);
        Assert.Contains("captures is null", capture, StringComparison.Ordinal);
        Assert.Contains("Array.Empty<uint>()", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("catch", capture, StringComparison.Ordinal);

        string clean = MethodBody(sample, "private void RequireCleanAfterContactAction(", "private TouchLifecycleProbeSnapshot CaptureSnapshot(");
        Assert.Contains("snapshot.StartHandle?.PointerIds.Length > 0", clean, StringComparison.Ordinal);
        Assert.Contains("snapshot.EndHandle?.PointerIds.Length > 0", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void CapturedHandleDoesNotRequireAnUnnecessarilySustainedFrameClock()
    {
        string sample = File.ReadAllText(RepositoryFile("MarkdownRenderer.Sample", "TouchLifecycleProbeWindow.cs"));
        string captured = MethodBody(sample, "private bool HasActualCapturedHandleContact(", "private bool HasRoutedContactPress(");
        Assert.Contains("actualButton.PointerIds.Contains(pointerId)", captured, StringComparison.Ordinal);
        Assert.Contains("HasRoutedContactPress(pointerId", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("HandleFrameClockSubscribed", captured, StringComparison.Ordinal);
        Assert.DoesNotContain("HandleFrameTimestamp", captured, StringComparison.Ordinal);

        // Frame work still must be gone after the actual action; it simply
        // isn't the definition of native capture while outside an edge band.
        string clean = MethodBody(sample, "private void RequireCleanAfterContactAction(", "private TouchLifecycleProbeSnapshot CaptureSnapshot(");
        Assert.Contains("state.HandleFrameClockSubscribed", clean, StringComparison.Ordinal);
    }

    [Fact]
    public void ReattachmentRediscoversHandlesOnlyAfterOldContactCleanup()
    {
        string sample = File.ReadAllText(RepositoryFile("MarkdownRenderer.Sample", "TouchLifecycleProbeWindow.cs"));
        string terminal = MethodBody(sample, "case \"awaitActionTerminal\":", "case \"awaitReattached\":");
        int verify = terminal.IndexOf("RequireCleanAfterContactAction(", StringComparison.Ordinal);
        int retireButtons = terminal.IndexOf("_startHandle = _endHandle = _capturedHandle = null;", StringComparison.Ordinal);
        int attach = terminal.IndexOf("AttachRendererToHost();", StringComparison.Ordinal);
        Assert.True(verify >= 0 && retireButtons > verify && attach > retireButtons);
        Assert.Contains("FindPointerTerminalAfter(_handlePointerId, _actionSequence)", terminal, StringComparison.Ordinal);
        Assert.Contains("terminal.CorrelatedGestureEpoch != _handleGestureEpoch", terminal, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalObservationSurvivesRemovalOfTheCapturedElementFromTheRootRoute()
    {
        string sample = File.ReadAllText(RepositoryFile("MarkdownRenderer.Sample", "TouchLifecycleProbeWindow.cs"));
        Assert.Contains("ObserveCapturedTerminal(_capturedHandle);", sample, StringComparison.Ordinal);
        string attach = MethodBody(sample, "private void ObserveCapturedTerminal(", "private void DetachCapturedTerminalObserver(");
        Assert.Contains("_terminalObserverHandle = handle;", attach, StringComparison.Ordinal);
        Assert.Contains("handle.AddHandler(UIElement.PointerReleasedEvent", attach, StringComparison.Ordinal);
        Assert.Contains("handle.AddHandler(UIElement.PointerCanceledEvent", attach, StringComparison.Ordinal);
        Assert.DoesNotContain("CapturePointer(", attach, StringComparison.Ordinal);

        string pointer = MethodBody(sample, "private void RecordPointerEvent(", "private TouchLifecycleProbeState? CaptureEventState(");
        Assert.Contains("args.Pointer.IsInContact", pointer, StringComparison.Ordinal);
        Assert.DoesNotContain("GetCurrentPoint", pointer, StringComparison.Ordinal);
        string close = MethodBody(sample, "private void OnClosed(", "internal sealed record TouchLifecycleProbeSnapshot(");
        Assert.Contains("DetachCapturedTerminalObserver();", close, StringComparison.Ordinal);
    }

    [Fact]
    public void ReceiptsDistinguishRootAndCapturedButtonObservationsOfOneRoutedTerminal()
    {
        string sample = File.ReadAllText(RepositoryFile("MarkdownRenderer.Sample", "TouchLifecycleProbeWindow.cs"));
        string pointer = MethodBody(sample, "private void RecordPointerEvent(", "private TouchLifecycleProbeState? CaptureEventState(");
        Assert.Contains("ReferenceEquals(observer, _root) ? \"Root\" : \"CapturedButton\"", pointer, StringComparison.Ordinal);
        Assert.Contains("RecordPointerEvent(\"PointerReleased\", args, sender)", sample, StringComparison.Ordinal);
        Assert.Contains("RecordPointerEvent(\"PointerCanceled\", args, sender)", sample, StringComparison.Ordinal);
        Assert.Contains("string Observer,", sample, StringComparison.Ordinal);
    }

    [Fact]
    public void HostCoordinationDeadlineEndsAtTheFirstMatchingRealContact()
    {
        string sample = File.ReadAllText(RepositoryFile("MarkdownRenderer.Sample", "TouchLifecycleProbeWindow.cs"));
        Assert.Contains("TimeSpan.FromSeconds(12)", sample, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(150)", sample, StringComparison.Ordinal);

        string arm = MethodBody(sample, "private void Arm(", "private void OnTick(");
        Assert.Contains("_phase = \"awaitLongPress\"", arm, StringComparison.Ordinal);
        Assert.Contains("StartCoordinatorWaitDeadline();", arm, StringComparison.Ordinal);

        string tick = MethodBody(sample, "private void OnTick(", "private void PerformLifecycleAction(");
        Assert.Contains("_phase = \"awaitHandleContact\"", tick, StringComparison.Ordinal);
        Assert.Contains("_phase = \"awaitFreshLongPress\"", tick, StringComparison.Ordinal);
        Assert.Contains("StartCoordinatorWaitDeadline();", tick, StringComparison.Ordinal);
        Assert.Contains("StartPhaseDeadline();", tick, StringComparison.Ordinal);
        Assert.Contains("host-coordinator wait (", tick, StringComparison.Ordinal);
        Assert.Contains("phase deadline (", tick, StringComparison.Ordinal);

        string pointer = MethodBody(sample, "private void RecordPointerEvent(", "private TouchLifecycleProbeState? CaptureEventState(");
        int eventRecord = pointer.IndexOf("AddEvent(probeEvent);", StringComparison.Ordinal);
        int contactDeadline = pointer.IndexOf("if (startsContactPhaseDeadline && !_coordinatorInputDeadlineStarted)", StringComparison.Ordinal);
        Assert.True(eventRecord >= 0 && contactDeadline > eventRecord,
            "The contact deadline must be reset only after recording an accepted matching press.");
        Assert.Contains("_phase == \"awaitLongPress\" && kind == \"PointerPressed\"", pointer, StringComparison.Ordinal);
        Assert.Contains("_phase == \"awaitHandleContact\" && kind == \"PointerPressed\"", pointer, StringComparison.Ordinal);
        Assert.Contains("_phase == \"awaitFreshLongPress\" && kind == \"PointerPressed\"", pointer, StringComparison.Ordinal);
        Assert.Contains("pointerId != 0 && pointerId == _longPressPointerId", pointer, StringComparison.Ordinal);
        Assert.Contains("pointerId != 0 && pointerId == _handlePointerId", pointer, StringComparison.Ordinal);
        Assert.Contains("pointerId != 0 && pointerId == _freshPointerId", pointer, StringComparison.Ordinal);

        string phaseDeadline = MethodBody(sample, "private void StartPhaseDeadline()", "private int NextEventSequence()");
        Assert.Contains("_coordinatorWaitDeadlineActive = false", phaseDeadline, StringComparison.Ordinal);
        Assert.Contains("_deadline = Environment.TickCount64 + (long)PhaseTimeout.TotalMilliseconds", phaseDeadline, StringComparison.Ordinal);
        Assert.Contains("_coordinatorInputDeadlineStarted = false", phaseDeadline, StringComparison.Ordinal);
        Assert.Contains("_coordinatorWaitDeadlineActive = true", phaseDeadline, StringComparison.Ordinal);
        Assert.Contains("(long)HostCoordinatorTimeout.TotalMilliseconds", phaseDeadline, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshSelectionRequiresNativeMenuOpenHostDismissalAndCurrentCleanupSnapshot()
    {
        string sample = File.ReadAllText(RepositoryFile("MarkdownRenderer.Sample", "TouchLifecycleProbeWindow.cs"));
        string terminal = MethodBody(sample, "case \"awaitFreshTerminal\":", "case \"awaitFreshMenuOpen\":");
        Assert.Contains("_freshReleaseSequence = freshRelease.Sequence", terminal, StringComparison.Ordinal);
        Assert.Contains("_phase = \"awaitFreshMenuOpen\"", terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("Complete();", terminal, StringComparison.Ordinal);

        string menuOpen = MethodBody(sample, "case \"awaitFreshMenuOpen\":", "case \"awaitFreshMenuDismissal\":");
        Assert.Contains("state.IsContextMenuOpen", menuOpen, StringComparison.Ordinal);
        Assert.Contains("ObserveFreshContextMenuOpen(snapshot)", menuOpen, StringComparison.Ordinal);

        string menuDismissal = MethodBody(sample, "case \"awaitFreshMenuDismissal\":", "case \"awaitFreshTerminalCleanup\":");
        Assert.Contains("_freshMenuDismissed = true", menuDismissal, StringComparison.Ordinal);
        Assert.Contains("_phase = \"awaitFreshTerminalCleanup\"", menuDismissal, StringComparison.Ordinal);
        Assert.Contains("StartPhaseDeadline();", menuDismissal, StringComparison.Ordinal);
        Assert.Contains("Publish();", menuDismissal, StringComparison.Ordinal);

        string cleanup = MethodBody(sample, "case \"awaitFreshTerminalCleanup\":", "default:");
        Assert.Contains("IsTransientStateClean(state)", cleanup, StringComparison.Ordinal);
        Assert.Contains("state.SelectionRange != _freshSelection!.Control.SelectionRange", cleanup, StringComparison.Ordinal);
        Assert.Contains("snapshot.StartHandle?.PointerIds.Length > 0", cleanup, StringComparison.Ordinal);
        Assert.Contains("_afterFreshTerminal = snapshot", cleanup, StringComparison.Ordinal);
        Assert.Contains("Complete();", cleanup, StringComparison.Ordinal);

        string observation = MethodBody(sample, "private void ObserveFreshContextMenuOpen(", "private TouchLifecycleProbeEvent CreateFreshMenuStateEvent(");
        Assert.Contains("_freshContextMenuObserved = true", observation, StringComparison.Ordinal);
        Assert.Contains("_freshContextMenuOpen = snapshot", observation, StringComparison.Ordinal);
        Assert.Contains("StartCoordinatorWaitDeadline();", observation, StringComparison.Ordinal);
        Assert.Contains("Publish();", observation, StringComparison.Ordinal);
        Assert.Contains("FreshMenuDismissedSequence", sample, StringComparison.Ordinal);
        Assert.Contains("2, _run, _phase, _case, _usesAncestor ? \"ancestor\" : \"owned\"", sample, StringComparison.Ordinal);

        string runner = File.ReadAllText(RepositoryFile("eng", "Invoke-MarkdownTouchLifecycleSandboxTests.ps1"));
        int waitForOpen = runner.IndexOf("AcceptPhase @('awaitFreshMenuDismissal')", StringComparison.Ordinal);
        int guardedEscape = runner.IndexOf("'send-keys','esc'", waitForOpen, StringComparison.Ordinal);
        int complete = runner.IndexOf("AcceptPhase @('complete') -TimeoutSeconds 12", guardedEscape, StringComparison.Ordinal);
        Assert.True(waitForOpen >= 0 && guardedEscape > waitForOpen && complete > guardedEscape,
            "The host must observe the latched open menu, send guarded Escape, then accept the final complete receipt.");
        Assert.Contains("Assert-TouchLifecycleFreshMenuReadyForDismissal", runner, StringComparison.Ordinal);
        Assert.Contains("-RejectPhase @('awaitFreshTerminalCleanup','complete')", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactNativeCasePickerAndBoundedReceiptLeaveFlexibleDocumentSpace()
    {
        string sample = File.ReadAllText(RepositoryFile("MarkdownRenderer.Sample", "TouchLifecycleProbeWindow.cs"));
        Assert.Contains("private readonly ComboBox _casePicker", sample, StringComparison.Ordinal);
        Assert.Contains("_casePicker.Items.Add(\"Capture loss\")", sample, StringComparison.Ordinal);
        Assert.Contains("_casePicker.Items.Add(\"Unload and reuse\")", sample, StringComparison.Ordinal);
        Assert.Contains("_casePicker.Items.Add(\"Dispose\")", sample, StringComparison.Ordinal);
        Assert.Contains("_receipt.MaxHeight = (double)Application.Current.Resources[\"SampleDiagnosticsMaxHeight\"]", sample, StringComparison.Ordinal);
        Assert.Contains("new GridLength(2, GridUnitType.Star)", sample, StringComparison.Ordinal);
        Assert.DoesNotContain("_receipt.Height =", sample, StringComparison.Ordinal);
    }

    private static string MethodBody(string source, string signature, string nextMarker)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find method '{signature}'.");
        // One guarded method is expression-bodied. Searching for an opening
        // brace would skip it and incorrectly search inside the next method.
        int nextMethod = source.IndexOf(nextMarker, start + signature.Length, StringComparison.Ordinal);
        Assert.True(nextMethod > start, "Could not isolate the guarded method.");
        return source[start..nextMethod];
    }

    private static string RepositoryFile(params string[] path)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
                return Path.Combine(directory.FullName, "MarkdownRenderer", Path.Combine(path));
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
