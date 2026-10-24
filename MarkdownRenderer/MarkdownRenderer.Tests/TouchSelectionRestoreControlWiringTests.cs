using System;
using System.IO;
using Xunit;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Verifies the control's restore-token wiring in addition to the state/token
/// behavior tests. This is not a substitute for the live touch relayout probe.
/// </summary>
public sealed class TouchSelectionRestoreControlWiringTests
{
    [Fact]
    public void DispatchCapturesCommittedHandleIntentBeforeResettingTouchOwnership()
    {
        string control = ReadControlSource();
        string dispatch = MethodRegion(
            control,
            "private void ProcessQueuedRebuildCore()",
            "private void CompleteRebuildTask(");
        string capture = MethodRegion(
            control,
            "private void CaptureTouchSelectionPresentationRestoreIntent()",
            "private Task RetireCancellationTokenSource(");

        int captureIndex = dispatch.IndexOf("CaptureTouchSelectionPresentationRestoreIntent();", StringComparison.Ordinal);
        int resetIndex = dispatch.IndexOf("ResetTransientInteractionState(clearSelection: false);", StringComparison.Ordinal);
        Assert.True(captureIndex >= 0 && resetIndex > captureIndex);
        Assert.Contains("_touchSelection.ShouldShowHandles", capture, StringComparison.Ordinal);
        Assert.Contains("_selection.IsActive", capture, StringComparison.Ordinal);
        Assert.Contains("snapshotGeneration", capture, StringComparison.Ordinal);
        Assert.Contains("selectionRange", capture, StringComparison.Ordinal);
        Assert.Contains("pending.Matches(snapshotGeneration, selectionRange)", capture, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionFencesStaleWorkersAndKeepsIntentForQueuedReplacement()
    {
        string control = ReadControlSource();
        string completion = MethodRegion(
            control,
            "private void OnRebuildPipelineCompleted(long generation)",
            "private void CaptureTouchSelectionPresentationRestoreIntent()");

        int generationFence = completion.IndexOf("!_rebuildCoordinator.CompletePipeline(generation)", StringComparison.Ordinal);
        int pendingCheck = completion.IndexOf("if (_rebuildCoordinator.HasPending)", StringComparison.Ordinal);
        int clearIntent = completion.IndexOf("_pendingTouchSelectionPresentationRestore = null;", StringComparison.Ordinal);
        Assert.True(generationFence >= 0 && pendingCheck > generationFence && clearIntent > pendingCheck);
        Assert.Contains("QueuePendingRebuild();", completion, StringComparison.Ordinal);
        Assert.Contains("else", completion, StringComparison.Ordinal);
        Assert.Contains("A failed/canceled current-generation rebuild did not publish", completion, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitRevalidatesLiveRangeAndSemanticIdentityBeforeRestoringOrRebasing()
    {
        string control = ReadControlSource();
        string rebuild = MethodRegion(
            control,
            "private async Task RebuildInternalAsync(",
            "private static LayoutSnapshot? BuildSnapshotOrNullOnCancellation(");

        Assert.Contains("old.SourceFingerprint.Equals(snapshot.SourceFingerprint)", rebuild, StringComparison.Ordinal);
        Assert.Contains("sameAutomationDocument", rebuild, StringComparison.Ordinal);
        Assert.Contains("bool preserveSelection = !_selection.Range.Normalized().IsEmpty && sameDocument;", rebuild, StringComparison.Ordinal);
        Assert.Contains("touchSelectionRestoreIntent =\n            _pendingTouchSelectionPresentationRestore", rebuild, StringComparison.Ordinal);

        int snapshotNotification = rebuild.IndexOf("NotifyAutomationSnapshotChanged();", StringComparison.Ordinal);
        int liveRangeValidation = rebuild.IndexOf("bool touchSelectionRestoreStillMatches", StringComparison.Ordinal);
        Assert.True(liveRangeValidation > snapshotNotification);
        Assert.Contains("_selection.IsActive", rebuild[liveRangeValidation..], StringComparison.Ordinal);
        Assert.Contains("currentRestoreIntent == capturedRestoreIntent", rebuild[liveRangeValidation..], StringComparison.Ordinal);
        Assert.Contains("capturedRestoreIntent.Matches(\n                    touchSelectionRestoreSourceGeneration,\n                    _selection.Range)", rebuild[liveRangeValidation..], StringComparison.Ordinal);
        Assert.Contains("bool hasPendingRebuild = _rebuildCoordinator.HasPending;", rebuild[liveRangeValidation..], StringComparison.Ordinal);
        Assert.Contains("if (touchSelectionRestoreStillMatches && !hasPendingRebuild)", rebuild, StringComparison.Ordinal);
        Assert.Contains("_touchSelection.TryRestoreSelectedPresentation()", rebuild, StringComparison.Ordinal);
        Assert.Contains("else if (touchSelectionRestoreStillMatches && hasPendingRebuild)", rebuild, StringComparison.Ordinal);
        Assert.Contains("new TouchSelectionPresentationRestoreIntent(generation, _selection.Range)", rebuild, StringComparison.Ordinal);
    }

    [Fact]
    public void NoOpSelectionNotificationRetainsOnlyTheSameRangeToken()
    {
        string control = ReadControlSource();
        string selectionChanged = MethodRegion(
            control,
            "private void OnSelectionChanged()",
            "private void QueueSelectionAutomationEvent()");

        Assert.Contains("!restoreIntent.MatchesRange(_selection.Range)", selectionChanged, StringComparison.Ordinal);
        Assert.DoesNotContain("Matches(_snapshotGeneration", selectionChanged, StringComparison.Ordinal);
        Assert.Contains("_pendingTouchSelectionPresentationRestore = null;", selectionChanged, StringComparison.Ordinal);
    }

    private static string MethodRegion(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find source marker '{startMarker}'.");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find end marker '{endMarker}' after '{startMarker}'.");
        return source[start..end];
    }

    private static string ControlPath() => Path.Combine(
        FindRepositoryRoot(),
        "MarkdownRenderer",
        "MarkdownRenderer",
        "Controls",
        "MarkdownRendererControl.cs");

    private static string ReadControlSource() =>
        File.ReadAllText(ControlPath()).Replace("\r\n", "\n", StringComparison.Ordinal);

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
