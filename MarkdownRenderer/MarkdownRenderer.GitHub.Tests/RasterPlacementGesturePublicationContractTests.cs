using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Source-wiring checks only. They do not instantiate WinUI or prove a live
/// pointer/selection gesture survives publication on a dispatcher.
/// </summary>
public sealed class RasterPlacementGesturePublicationContractTests
{
    private const string PublishSignature =
        "private bool TryPublishRasterPlacement(LayoutSnapshot source, RasterPlacementPlan plan, long generation)";

    [Fact]
    public void PublicationUsesTheScopeAwareResolverFence_SourceContractOnly()
    {
        string method = ReadMethod(PublishSignature);
        Assert.Contains("RasterPlacementResolverFence.Matches(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("ReferenceEquals(ImageResolver, context.ImageResolver)", method, StringComparison.Ordinal);
        Assert.Contains("PerformanceSession as IMarkdownPerformanceSessionInternal", method, StringComparison.Ordinal);
        Assert.Contains("context.ImageBaseUri, context.ImageDocumentPath", method, StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredPointerWaiterIsCanceledOnlyForItsKnownSourceAfterCommitAndRetirement_SourceContractOnly()
    {
        string method = ReadMethod(PublishSignature);
        int rejectsUnknownSnapshot = method.IndexOf(
            "(_deferredPointerSnapshot is not null && !ReferenceEquals(_deferredPointerSnapshot, source))",
            StringComparison.Ordinal);
        int pointerOwnerGate = method.IndexOf("HasSnapshotPointerInteraction", StringComparison.Ordinal);
        int snapshotSwap = method.IndexOf("_snapshot = owner.TransferToControl();", StringComparison.Ordinal);
        int publication = method.IndexOf("Volatile.Write(ref _imagePlanPublication, plan.Publication);", StringComparison.Ordinal);
        int committed = method.IndexOf("committed = true;", publication, StringComparison.Ordinal);
        int retirement = method.IndexOf("RetireSnapshot(source);", StringComparison.Ordinal);
        int waiterIdentity = method.IndexOf(
            "if (ReferenceEquals(_deferredPointerSnapshot, source))",
            retirement,
            StringComparison.Ordinal);
        int waiterReset = method.IndexOf("ResetDeferredPointerInput();", waiterIdentity, StringComparison.Ordinal);

        Assert.True(rejectsUnknownSnapshot >= 0);
        Assert.True(pointerOwnerGate >= 0 && snapshotSwap > pointerOwnerGate);
        Assert.True(snapshotSwap > rejectsUnknownSnapshot);
        Assert.True(publication > snapshotSwap && committed > publication && retirement > committed);
        Assert.True(waiterIdentity > retirement && waiterReset > waiterIdentity);
        Assert.DoesNotContain("_deferredPointerSnapshot = candidate", method, StringComparison.Ordinal);
    }

    [Fact]
    public void GeometryOnlyPublicationClearsAliasesAndCancelsOnlyCommittedSourceWaiters_SourceContractOnly()
    {
        string method = ReadMethod(PublishSignature);
        int retirement = method.IndexOf("RetireSnapshot(source);", StringComparison.Ordinal);
        Assert.True(retirement >= 0);
        foreach (string alias in new[]
                 {
                     "_lastHoveredRun = null;",
                     "_lastHoveredBox = null;",
                     "_lastHoveredAbbreviation = null;",
                     "_pendingAbbreviationTooltipRun = null;",
                 })
        {
            int clear = method.IndexOf(alias, StringComparison.Ordinal);
            Assert.True(clear >= 0 && clear < retirement, $"'{alias}' must be cleared before source retirement.");
        }

        foreach (string destructiveReset in new[]
                 {
                     "ResetTransientInteractionState(",
                     "ResetSelectionHandleDrag()",
                     "_touchSelection.Reset()",
                     "ReleasePointerCaptures()",
                     "CloseSelectionContextMenu()",
                 })
            Assert.DoesNotContain(destructiveReset, method, StringComparison.Ordinal);

        int waiterIdentity = method.IndexOf(
            "if (ReferenceEquals(_deferredPointerSnapshot, source))",
            retirement,
            StringComparison.Ordinal);
        int waiterReset = method.IndexOf("ResetDeferredPointerInput();", waiterIdentity, StringComparison.Ordinal);
        Assert.True(waiterIdentity > retirement && waiterReset > waiterIdentity);
        Assert.DoesNotContain("_deferredPointerSnapshot = candidate", method, StringComparison.Ordinal);

        int automation = method.IndexOf("NotifyAutomationSnapshotChanged();", StringComparison.Ordinal);
        int tooltip = method.IndexOf("HideAbbreviationTooltip();", StringComparison.Ordinal);
        int tooltipRecheck = method.IndexOf(
            "if (!IsCurrentRasterCodeActionPublication(",
            tooltip,
            StringComparison.Ordinal);
        int cursor = method.IndexOf("SetCursorShape(null);", tooltip);
        int cursorRecheck = method.IndexOf(
            "if (!IsCurrentRasterCodeActionPublication(",
            cursor,
            StringComparison.Ordinal);
        Assert.True(automation >= 0 && tooltip > automation && tooltipRecheck > tooltip);
        Assert.True(cursor > tooltipRecheck && cursorRecheck > cursor);
    }

    [Fact]
    public void PublicationRejectsLiveAndPendingPointerOwnershipBeforeSnapshotTransfer_SourceContractOnly()
    {
        string method = ReadMethod(PublishSignature);
        int gate = method.IndexOf("HasSnapshotPointerInteraction", StringComparison.Ordinal);
        int swap = method.IndexOf("_snapshot = owner.TransferToControl();", StringComparison.Ordinal);
        Assert.True(gate >= 0 && swap > gate);

        string input = ReadControlSource("MarkdownRendererControl.PointerInput.cs");
        int predicate = input.IndexOf("private bool HasSnapshotPointerInteraction =>", StringComparison.Ordinal);
        int end = input.IndexOf(';', predicate);
        string body = input[predicate..end];
        Assert.Contains("_pointerSession.IsActive", body, StringComparison.Ordinal);
        Assert.Contains("_horizontalOverflowPointerId != 0", body, StringComparison.Ordinal);
        Assert.Contains("_selectionHandlePointerId != 0", body, StringComparison.Ordinal);
        Assert.Contains("_drainingPointerInput", body, StringComparison.Ordinal);
        Assert.Contains("_deferredPointerCaptures.Count != 0", body, StringComparison.Ordinal);
        Assert.Contains("HasBlockingSnapshotInteractions", body, StringComparison.Ordinal);
        Assert.DoesNotContain("_deferredPointerInput?.Count", body, StringComparison.Ordinal);

        string pointer = ReadControlSource("MarkdownRendererControl.PointerInput.cs");
        string logicalEnqueue = ReadPointerMethod(pointer, "private bool EnqueueRecognizedGesture(");
        Assert.Contains("blocksSnapshotInteraction: false", logicalEnqueue, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalSvgProviderReadPrecedesTheEntireLocalCommitFence_SourceContractOnly()
    {
        string method = ReadMethod(PublishSignature);
        int provider = method.IndexOf("if (!owner.HasCurrentSourceProviders())", StringComparison.Ordinal);
        int lifecycle = method.IndexOf("if (_isDisposed || _isUnloaded || _publishingLayoutCommit", StringComparison.Ordinal);
        int source = method.IndexOf("!ReferenceEquals(source, _snapshot)", lifecycle, StringComparison.Ordinal);
        int localRaster = method.IndexOf("!owner.HasCurrentSourceRaster()", source, StringComparison.Ordinal);
        int commit = method.IndexOf("_publishingLayoutCommit = true;", localRaster, StringComparison.Ordinal);
        Assert.True(provider >= 0 && lifecycle > provider && source > lifecycle && localRaster > source && commit > localRaster);
        Assert.DoesNotContain("HasCurrentSourceProviders()", method[commit..], StringComparison.Ordinal);
    }

    [Fact]
    public void PaintOnlyCompletionKeepsUiaAndRechecksMembershipAfterNotification_SourceContractOnly()
    {
        string source = ReadControlSource("MarkdownRendererControl.cs");
        int start = source.IndexOf("private void ApplyImageLoadCompleted(", StringComparison.Ordinal);
        int end = source.IndexOf("private void QueueImageRelayout(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string method = source[start..end];
        int status = method.IndexOf(".NotifyImageStatusChanged(completedImage);", StringComparison.Ordinal);
        int recheck = method.IndexOf(
            "if (_isDisposed || _isUnloaded || !_subscribedImages.Contains(completedImage))",
            status, StringComparison.Ordinal);
        int proof = method.IndexOf("snapshot.TryCanUsePaintOnlyImageCompletion(", StringComparison.Ordinal);
        int paintOnly = method.IndexOf("layoutInvalidated = false;", proof, StringComparison.Ordinal);
        Assert.True(status >= 0 && recheck > status && proof > recheck && paintOnly > proof);
        Assert.Contains("PerformanceSession is IMarkdownPerformanceSessionInternal session", method, StringComparison.Ordinal);
        Assert.Contains("!session.IsDisposed && ReferenceEquals(session, completedImage.Context.PerformanceSession)", method, StringComparison.Ordinal);
        Assert.Contains("!_publishingLayoutCommit", method, StringComparison.Ordinal);
        Assert.Contains("QueueImageRelayout(completedImage.BlockIndex);", method, StringComparison.Ordinal);
        Assert.Contains("InvalidateCanvas();", method, StringComparison.Ordinal);
        Assert.Contains("InvalidateSelectionAdorner();", method, StringComparison.Ordinal);
        Assert.DoesNotContain("NotifyAutomationSnapshotChanged()", method, StringComparison.Ordinal);
    }

    [Fact]
    public void QueuedImageWorkWaitsWithoutConsumingItsRecoveryState_SourceContractOnly()
    {
        string source = ReadControlSource("MarkdownRendererControl.cs");
        int start = source.IndexOf("private void ProcessQueuedImageRelayout(long ticket)", StringComparison.Ordinal);
        int gate = source.IndexOf("if (HasSnapshotPointerInteraction)", start, StringComparison.Ordinal);
        int consume = source.IndexOf("_imageRelayoutQueued = false;", gate, StringComparison.Ordinal);
        int run = source.IndexOf("_imageRelayoutTask = RunImageRelayoutAsync(", consume, StringComparison.Ordinal);
        Assert.True(start >= 0 && gate > start && consume > gate && run > consume);
        Assert.Contains("return;", source[gate..consume], StringComparison.Ordinal);
        Assert.DoesNotContain("StopImageRelayoutRecovery()", source[gate..consume], StringComparison.Ordinal);
    }

    [Fact]
    public void GestureStartedDuringPrivateWorkRequeuesImagesWithoutResetOrFullRebuild_SourceContractOnly()
    {
        string source = ReadControlSource("MarkdownRendererControl.cs");
        int complete = source.IndexOf("private void CompleteImageRelayout(", StringComparison.Ordinal);
        int start = source.IndexOf("if (HasSnapshotPointerInteraction)", complete, StringComparison.Ordinal);
        int end = source.IndexOf("else if (!TryPublishRasterPlacement(", start, StringComparison.Ordinal);
        Assert.True(complete >= 0 && start > complete && end > start);
        string retry = source[start..end];
        Assert.Contains("_pendingImageRelayoutBlockIndices.Add(image.BlockIndex);", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestRebuild(", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearHorizontalOverflowPointerState()", retry, StringComparison.Ordinal);
        Assert.DoesNotContain("ReleasePointerCapture", retry, StringComparison.Ordinal);
        Assert.Contains("finally { candidate?.Dispose(); }", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private bool HandleHorizontalOverflowPointerReleased(ref PointerInput e)", "if (captured && _canvas is not null)\n        {")]
    [InlineData("private bool HandleHorizontalOverflowPointerCanceled(ref PointerInput e)", "if (captured)\n        {")]
    public void PendingHorizontalContactResumesWorkEvenWithoutCapture_SourceContractOnly(string signature, string capturedBranch)
    {
        string source = ReadControlSource("MarkdownRendererControl.cs");
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        int clear = source.IndexOf("ClearHorizontalOverflowPointerState();", start, StringComparison.Ordinal);
        int resume = source.IndexOf("e.ResumeRebuildAfterInteraction = true;", clear, StringComparison.Ordinal);
        int captured = source.IndexOf(capturedBranch, clear, StringComparison.Ordinal);
        Assert.True(start >= 0 && clear > start && resume > clear && captured > resume);
    }

    [Theory]
    [InlineData("private void ProcessSelectionHandlePointerReleased(ref PointerInput e)", "private void ProcessSelectionHandlePointerCanceled(")]
    [InlineData("private void ProcessSelectionHandlePointerCanceled(ref PointerInput e)", "private void StartSelectionHandleFrameClock()")]
    public void HandleCompletionResumesGeometryWork_SourceContractOnly(string signature, string nextSignature)
    {
        string source = ReadControlSource("MarkdownRendererControl.cs");
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        int end = source.IndexOf(nextSignature, start + signature.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        Assert.Contains("e.ResumeRebuildAfterInteraction = true;", source[start..end], StringComparison.Ordinal);

        int resume = source.IndexOf("private void ResumePendingRebuildAfterPointerInteraction()", StringComparison.Ordinal);
        int resumeEnd = source.IndexOf("\n    }", resume, StringComparison.Ordinal);
        Assert.Contains("QueueImageRelayout(pending);", source[resume..resumeEnd], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private bool TryBeginHorizontalOverflowPointer(ref PointerInput e)", "private bool HandleHorizontalOverflowPointerMoved(")]
    [InlineData("private bool HandleHorizontalOverflowPointerMoved(ref PointerInput e, Point point)", "private bool HandleHorizontalOverflowPointerReleased(")]
    [InlineData("private void ProcessDoubleTapped(ref PointerInput e)", "private void OnHolding(")]
    [InlineData("private void ProcessHolding(ref PointerInput e)", "private void EndCapturedSelectionForContextGesture(")]
    public void PendingHorizontalContactYieldCaptureFailureAndContextGesturesResumeWork_SourceContractOnly(
        string signature, string nextSignature)
    {
        string source = ReadControlSource("MarkdownRendererControl.cs");
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        int end = source.IndexOf(nextSignature, start + signature.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string body = source[start..end];
        string[] exits = body.Split("ClearHorizontalOverflowPointerState();", StringSplitOptions.None);
        Assert.True(exits.Length > 1);
        foreach (string exit in exits.Skip(1))
            Assert.StartsWith("e.ResumeRebuildAfterInteraction = true;", exit.TrimStart(), StringComparison.Ordinal);
    }

    private static string ReadMethod(string signature)
    {
        string source = ReadImageCandidateSource();
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find '{signature}'.");
        int bodyStart = source.IndexOf('{', member + signature.Length);
        Assert.True(bodyStart >= 0, $"Could not find the body for '{signature}'.");

        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        throw new InvalidOperationException($"Could not find the end of '{signature}'.");
    }

    [Fact]
    public void InteractionLeaseEndsBeforeDeferredRebuildOrRelayoutIsReadmitted_SourceContractOnly()
    {
        string pointer = ReadControlSource("MarkdownRendererControl.PointerInput.cs");
        string dispatch = ReadPointerMethod(pointer, "private void DispatchPointerInput(");
        int interactionGate = dispatch.IndexOf(
            "if (!_drainingPointerInput && (_deferredPointerInput?.Count ?? 0) == 0 && snapshot.TryBeginInteraction())",
            StringComparison.Ordinal);
        int leaseRelease = dispatch.IndexOf("snapshot.EndInteraction();", interactionGate, StringComparison.Ordinal);
        int postLeaseWork = dispatch.IndexOf("if (input.HasPostInteractionWork)", leaseRelease, StringComparison.Ordinal);
        int completion = dispatch.IndexOf("CompletePointerPostInteraction(", postLeaseWork, StringComparison.Ordinal);
        Assert.True(interactionGate >= 0 && leaseRelease > interactionGate);
        Assert.True(postLeaseWork > leaseRelease && completion > postLeaseWork);
        Assert.DoesNotContain(
            "CompletePointerPostInteraction(",
            dispatch[interactionGate..leaseRelease],
            StringComparison.Ordinal);

        string complete = ReadPointerMethod(pointer, "private bool CompletePointerPostInteraction(");
        int currentFence = complete.IndexOf("if (!IsCurrentPointerInteraction(", StringComparison.Ordinal);
        int rebuildRequest = complete.IndexOf("if (input.ResumeRebuildAfterInteraction)", currentFence, StringComparison.Ordinal);
        int resume = complete.IndexOf("ResumePendingRebuildAfterPointerInteraction();", rebuildRequest, StringComparison.Ordinal);
        Assert.Contains("_drainingPointerInput = true;", complete, StringComparison.Ordinal);
        Assert.True(currentFence >= 0 && rebuildRequest > currentFence && resume > rebuildRequest);
        Assert.Contains("QueueImageRelayout(pending);", ReadControlSource("MarkdownRendererControl.cs"), StringComparison.Ordinal);
    }

    private static string ReadPointerMethod(string source, string signature)
    {
        int member = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(member >= 0, $"Could not find pointer method '{signature}'.");
        int bodyStart = source.IndexOf('{', member + signature.Length);
        Assert.True(bodyStart >= 0, $"Could not find the body for pointer method '{signature}'.");

        int depth = 0;
        for (int index = bodyStart; index < source.Length; index++)
        {
            if (source[index] == '{')
                depth++;
            else if (source[index] == '}' && --depth == 0)
                return source[bodyStart..(index + 1)];
        }

        throw new InvalidOperationException($"Could not find the end of pointer method '{signature}'.");
    }

    private static string ReadImageCandidateSource()
        => ReadControlSource("MarkdownRendererControl.ImageCandidate.cs");

    private static string ReadControlSource(string fileName)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "MarkdownRenderer", "MarkdownRenderer", "Controls",
                fileName);
            if (File.Exists(path))
                return File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        }

        throw new FileNotFoundException("Could not locate the raster candidate source from the test base directory.");
    }
}
