using System;
using System.IO;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Source guards for the friend-only probe used by the sample-driven actual
/// control regression. These do not substitute for running that live test.
/// </summary>
public sealed class DeferredTouchProbeSourceContractTests
{
    [Fact]
    public void ProbeStateIsInternalImmutableAndIncludesTheActualDeferredControlState()
    {
        string source = File.ReadAllText(GetProbePath());
        Assert.Contains("internal sealed record DeferredTouchProbeState(", source, StringComparison.Ordinal);
        Assert.Contains("ImmutableArray<DeferredTouchProbeTerminal> QueuedTerminals", source, StringComparison.Ordinal);
        Assert.Contains("int MatchingTerminalCount", source, StringComparison.Ordinal);
        Assert.Contains("DocumentRange SelectionRange", source, StringComparison.Ordinal);
        Assert.Contains("string TouchState", source, StringComparison.Ordinal);
        Assert.Contains("CaptureDeferredTouchProbeState()", source, StringComparison.Ordinal);
        Assert.Contains("_deferredPointerInput?.Count ?? 0", source, StringComparison.Ordinal);
        Assert.Contains("queue.TryGetAt(index, out PointerInput queued)", source, StringComparison.Ordinal);
        Assert.Contains("_deferredPointerCaptures.Count", source, StringComparison.Ordinal);
        Assert.Contains("_pointerRetryTimer is not null", source, StringComparison.Ordinal);
        Assert.Contains("CurrentSelectionRange", source, StringComparison.Ordinal);
        Assert.Contains("_selection.IsActive", source, StringComparison.Ordinal);
        Assert.Contains("_selectionStartHandle?.Visibility == Visibility.Visible", source, StringComparison.Ordinal);
        Assert.Contains("_selectionEndHandle?.Visibility == Visibility.Visible", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureProbeRequiresTheExactLiveTouchOwnerAndUsesTheProductionFailureCallback()
    {
        string source = File.ReadAllText(GetProbePath());
        string body = ExtractMethodBody(source, "internal DeferredTouchProbeState FailDeferredTouchTerminalForProbe(");

        Assert.Contains("VerifyDeferredTouchProbeDispatcher();", body, StringComparison.Ordinal);
        Assert.Contains("_isDisposed || _isUnloaded || !IsLoaded", body, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(snapshot, _snapshot)", body, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(snapshot, _deferredPointerSnapshot)", body, StringComparison.Ordinal);
        Assert.Contains("_touchSelection.ContactPointerId != pointerId", body, StringComparison.Ordinal);
        Assert.Contains("GetGestureEpochForPointer(pointerId) != gestureEpoch", body, StringComparison.Ordinal);
        Assert.Contains("PointerInputKind expectedKind = canceled ? PointerInputKind.Cancel : PointerInputKind.Release", body, StringComparison.Ordinal);
        Assert.Contains("pointer.PointerDeviceType != PointerDeviceType.Touch", body, StringComparison.Ordinal);
        Assert.Contains("queued.TouchTapGestureEpoch != gestureEpoch", body, StringComparison.Ordinal);
        Assert.Contains("queued.Kind != expectedKind", body, StringComparison.Ordinal);
        Assert.Contains("if (!canceled && matchingTerminalCount != 1)", body, StringComparison.Ordinal);
        Assert.Contains("OnDeferredGestureLayoutCompletion(snapshot, succeeded: false);", body, StringComparison.Ordinal);
        Assert.Contains("DeferredTouchProbeState after = CaptureDeferredTouchProbeState();", body, StringComparison.Ordinal);
        Assert.Contains("after.ActivePointerId != 0", body, StringComparison.Ordinal);
        Assert.Contains("after.GestureEpoch != 0", body, StringComparison.Ordinal);
        Assert.Contains("after.QueuedCount != 0", body, StringComparison.Ordinal);
        Assert.Contains("after.RetryTimerExists", body, StringComparison.Ordinal);
        Assert.Contains("after.CaptureCount != 0", body, StringComparison.Ordinal);

        Assert.DoesNotContain("TryEnqueue", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TryDequeue", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ResetDeferredPointerInput", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TryBeginInteraction", body, StringComparison.Ordinal);
        Assert.DoesNotContain("EndInteraction", body, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateExactCancelNotificationsAreAllowedButReleaseMustBeUnique()
    {
        string source = File.ReadAllText(GetProbePath());
        string body = ExtractMethodBody(source, "internal DeferredTouchProbeState FailDeferredTouchTerminalForProbe(");

        Assert.Contains("if (!canceled && matchingTerminalCount != 1)", body, StringComparison.Ordinal);
        Assert.Contains("Multiple same-owner Cancel notifications are legitimate", body, StringComparison.Ordinal);
        Assert.Contains("queued.Kind != expectedKind", body, StringComparison.Ordinal);
        Assert.Contains("queued.TouchTapGestureEpoch != gestureEpoch", body, StringComparison.Ordinal);
        Assert.Contains("pointer.PointerDeviceType != PointerDeviceType.Touch", body, StringComparison.Ordinal);
    }

    [Fact]
    public void SampleProbeRecordsActualPreContactRangeAndRequiresContentionBeforeTerminal()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MarkdownRenderer", "MarkdownRenderer.Sample", "DeferredTouchProbeWindow.cs"));
        Assert.Contains("_preContact = current;", source, StringComparison.Ordinal);
        Assert.Contains("current.MatchingTerminalCount != 0", source, StringComparison.Ordinal);
        Assert.Contains("_heldSnapshot.TryBeginInteraction, _heldSnapshot.EndInteraction", source, StringComparison.Ordinal);
        Assert.Contains("_preContact!.SelectionRange", source, StringComparison.Ordinal);
        Assert.Contains("_after.SelectionRange != expectedRange", source, StringComparison.Ordinal);
        Assert.Contains("Renderer.FailDeferredTouchTerminalForProbe(", source, StringComparison.Ordinal);
        Assert.Contains("current.GestureEpoch == _gestureEpoch", source, StringComparison.Ordinal);
        Assert.Contains("_lease?.Release();", source, StringComparison.Ordinal);
        Assert.Contains("_workerHeldAtDrop = _lease.IsHeld;", source, StringComparison.Ordinal);
        Assert.Contains("_receipt.Height = (double)Application.Current.Resources[\"SampleDiagnosticsMaxHeight\"]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_receipt.MaxHeight =", source, StringComparison.Ordinal);
        Assert.Contains("DeferredTouchProbeJsonContext.Default.DeferredTouchProbeReport", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SelectAutomationRange(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new PointerInput", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CapturePointer(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Result", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".GetAwaiter().GetResult(", source, StringComparison.Ordinal);
        // Wiring guards are not proof that the real queued touch ran.
    }

    [Fact]
    public void SampleProbeExposesReadOnlyRoutedObservationForTheGuestDriverInBothViewports()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MarkdownRenderer", "MarkdownRenderer.Sample", "DeferredTouchProbeWindow.cs"));
        Assert.Contains("AutomationProperties.SetAutomationId(_ancestor, \"TouchSelectionAncestorViewport\")", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetAutomationId(_touchContactStatus, \"TouchContactObservation\")", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetAccessibilityView(_touchContactStatus, AccessibilityView.Raw)", source, StringComparison.Ordinal);
        Assert.Contains("_touchContactObservation = new TouchContactObservation(_touchContactStatus);", source, StringComparison.Ordinal);
        Assert.Contains("_touchContactObservation.Attach(Renderer);", source, StringComparison.Ordinal);
        Assert.Contains("_touchContactObservation.Detach();", source, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible = false", source, StringComparison.Ordinal);
        Assert.True(source.IndexOf("_touchContactObservation = new TouchContactObservation", StringComparison.Ordinal) <
                    source.IndexOf("SetViewport(false);", StringComparison.Ordinal));
        Assert.True(source.LastIndexOf("_touchContactObservation.Detach();", StringComparison.Ordinal) <
                    source.IndexOf("_owned.Dispose();", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedProbeCannotReplaceTheRendererBeforeItsWorkerAndContactRetire()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "MarkdownRenderer", "MarkdownRenderer.Sample", "DeferredTouchProbeWindow.cs"));
        int start = source.IndexOf("private bool SetViewport(bool ancestor)", StringComparison.Ordinal);
        int end = source.IndexOf("private void Arm(bool canceled)", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string method = source[start..end];
        Assert.Contains("_lease is { Completed.IsCompleted: false }", method, StringComparison.Ordinal);
        Assert.Contains("current.ActivePointerId != 0 || current.QueuedCount != 0", method, StringComparison.Ordinal);
        Assert.Contains("current.CaptureCount != 0 || current.RetryTimerExists || current.SelectionContextMenuOpen", method, StringComparison.Ordinal);
        Assert.True(method.IndexOf("return false;", StringComparison.Ordinal) <
                    method.IndexOf("_touchContactObservation.Detach();", StringComparison.Ordinal));
        Assert.Contains("if (!SetViewport(ancestor) && _viewport.IsChecked != _usesAncestor)", source, StringComparison.Ordinal);
        Assert.Contains("_viewport.IsChecked = _usesAncestor;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("throw new InvalidOperationException", method, StringComparison.Ordinal);
    }

    private static string ExtractMethodBody(string source, string methodSignature)
    {
        int methodStart = source.IndexOf(methodSignature, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Could not find method '{methodSignature}'.");
        int bodyStart = source.IndexOf('{', methodStart);
        int nextMethod = source.IndexOf("    private void VerifyDeferredTouchProbeDispatcher()", bodyStart, StringComparison.Ordinal);
        Assert.True(bodyStart >= 0 && nextMethod > bodyStart, "Could not isolate the fail-probe method body.");
        return source[bodyStart..nextMethod];
    }

    private static string GetProbePath() => Path.Combine(
        FindRepositoryRoot(),
        "MarkdownRenderer",
        "MarkdownRenderer",
        "Controls",
        "MarkdownRendererControl.DeferredTouchProbe.cs");

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
}
