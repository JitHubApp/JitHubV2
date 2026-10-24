using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// These checks validate call-site wiring only. Behavioral state transitions
/// are exercised against the compiled production MarkdownRebuildCoordinator
/// by MarkdownRebuildCoordinatorTests; this is not a control integration test.
/// </summary>
public sealed class MarkdownRendererControlRebuildWiringSourceContractTests
{
    [Fact]
    public void SceneDeferralReturnsBeforeAnyPipelineInvalidation()
    {
        string control = File.ReadAllText(FindControlSource());
        int request = control.IndexOf("private void RequestRebuild(", StringComparison.Ordinal);
        int defer = control.IndexOf(
            "if (disposition == MarkdownRebuildRequestDisposition.Deferred)",
            request,
            StringComparison.Ordinal);
        int probeCancellation = control.IndexOf("CancelAutomationFirstViewportImagesReadyProbe();", request, StringComparison.Ordinal);
        int generationAdvance = control.IndexOf("++_pipelineGeneration;", request, StringComparison.Ordinal);
        int ctsDetachment = control.IndexOf("_pipelineCts = null;", request, StringComparison.Ordinal);

        Assert.True(request >= 0);
        Assert.True(defer > request && probeCancellation > defer);
        Assert.True(generationAdvance > defer);
        Assert.True(ctsDetachment > defer);
    }

    [Fact]
    public void PipelineIsRegisteredBeforeReentrantRenderCompletedCanRun()
    {
        string control = File.ReadAllText(FindControlSource());
        int begin = control.IndexOf("_rebuildCoordinator.TryBeginPipeline(", StringComparison.Ordinal);
        int lifetimePublish = control.IndexOf("_pipelineTask = lifetime.Task;", begin, StringComparison.Ordinal);
        int invoke = control.IndexOf("task = RebuildAsync(", begin, StringComparison.Ordinal);

        Assert.True(begin >= 0);
        Assert.True(lifetimePublish > begin && invoke > lifetimePublish);
        Assert.Contains("task.ContinueWith(", control[invoke..], StringComparison.Ordinal);
        Assert.Contains("CompleteRebuildTask(completed, lifetime, generation, completionDispatcher)", control[invoke..], StringComparison.Ordinal);
    }

    [Fact]
    public void WorkerCompletionUsesQueueCapturedBeforeReentrantBuildStarts()
    {
        string control = File.ReadAllText(FindControlSource());
        int capture = control.IndexOf("completionDispatcher = DispatcherQueue;", StringComparison.Ordinal);
        int invoke = control.IndexOf("task = RebuildAsync(", capture, StringComparison.Ordinal);
        int completion = control.IndexOf("private void CompleteRebuildTask(", invoke, StringComparison.Ordinal);
        int end = control.IndexOf("private void OnRebuildPipelineCompleted(", completion, StringComparison.Ordinal);

        Assert.True(capture >= 0 && invoke > capture && completion > invoke);
        Assert.DoesNotContain("= DispatcherQueue;", control[completion..end], StringComparison.Ordinal);
    }

    [Fact]
    public void PointerBlockingOccursBeforeTheCoordinatorConsumesPendingWork()
    {
        string control = File.ReadAllText(FindControlSource());
        int process = control.IndexOf("private void ProcessQueuedRebuildCore()", StringComparison.Ordinal);
        int pointerGate = control.IndexOf("if (HasSnapshotPointerInteraction)", process, StringComparison.Ordinal);
        int begin = control.IndexOf("_rebuildCoordinator.TryBeginPipeline(", process, StringComparison.Ordinal);

        Assert.True(process >= 0);
        Assert.True(pointerGate > process && begin > pointerGate);
    }

    [Fact]
    public void PointerReleaseRetriesPendingWorkWithoutCreatingANewRequest()
    {
        string control = File.ReadAllText(FindControlSource());
        int resume = control.IndexOf("private void ResumePendingRebuildAfterPointerInteraction()", StringComparison.Ordinal);
        int end = control.IndexOf("\n    }", resume, StringComparison.Ordinal);
        string method = control[resume..end];

        Assert.Contains("QueuePendingRebuild();", method, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestRebuild(", method, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectedCrossThreadRequestIsCoalescedAndDrainedOnTheUiThread()
    {
        string control = File.ReadAllText(FindControlSource());
        Assert.Contains("RecordRejectedCrossThreadRebuildRequest(reason);", control, StringComparison.Ordinal);
        Assert.Contains("Interlocked.CompareExchange(", control, StringComparison.Ordinal);
        Assert.Contains("DrainRejectedCrossThreadRebuildRequest();", control, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _rejectedCrossThreadRebuildReason, -1)", control, StringComparison.Ordinal);
    }

    [Fact]
    public void RebuildAttributionIsOptInAndRetainsTheOriginalCrossThreadCallSite()
    {
        string control = File.ReadAllText(FindControlSource());
        int request = control.IndexOf("private void RequestRebuild(", StringComparison.Ordinal);
        int observer = control.IndexOf("if (AutomationRebuildRequestObserver is { } observer)", request, StringComparison.Ordinal);
        int format = control.IndexOf("observer($\"rebuild-request:", request, StringComparison.Ordinal);
        int defer = control.IndexOf("if (disposition == MarkdownRebuildRequestDisposition.Deferred)", request, StringComparison.Ordinal);
        Assert.True(request >= 0 && observer > request && format > observer && defer > format);
        Assert.Contains("[CallerMemberName] string requestOrigin", control[request..observer], StringComparison.Ordinal);
        Assert.Contains("[CallerLineNumber] int requestLine", control[request..observer], StringComparison.Ordinal);
        Assert.Contains("RequestRebuild(reason, requestOrigin, requestLine)", control[request..observer], StringComparison.Ordinal);
        Assert.Contains("catch (Exception)", control[observer..defer], StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", control[request..defer], StringComparison.Ordinal);
    }

    [Fact]
    public void PipelineFailureStillReachesTheSamePendingDrainBoundary()
    {
        string control = File.ReadAllText(FindControlSource());
        int completion = control.IndexOf("private void CompleteRebuildTask(", StringComparison.Ordinal);
        int faultObservation = control.IndexOf("if (completed.IsFaulted)", completion, StringComparison.Ordinal);
        int lifetimeSignal = control.IndexOf(
            "MarkdownRebuildCoordinator.CompletePipelineLifetime(completed, lifetime);",
            completion,
            StringComparison.Ordinal);
        int dispatcherHandling = control.IndexOf("OnRebuildPipelineCompleted(generation);", lifetimeSignal, StringComparison.Ordinal);

        Assert.True(completion >= 0);
        Assert.True(faultObservation > completion);
        Assert.True(lifetimeSignal > faultObservation);
        Assert.True(dispatcherHandling > lifetimeSignal);
        Assert.Contains("_rebuildCoordinator.CompletePipeline(generation);", control[completion..], StringComparison.Ordinal);
        Assert.Contains("Dispatcher did not accept rebuild completion callback.", control[completion..], StringComparison.Ordinal);
    }

    [Fact]
    public void FailedBuildRetirementsAreAwaitedByTheirPipelineAndCanFailTheCapturedShutdownEpoch()
    {
        string control = File.ReadAllText(FindControlSource());
        int rebuild = control.IndexOf("private async Task RebuildInternalAsync(", StringComparison.Ordinal);
        int sink = control.IndexOf("RetirementSink = _snapshotRetirements.Track", rebuild, StringComparison.Ordinal);
        int pipelineDrain = control.IndexOf(
            "await ctx.DrainOwnedRetirementsAsync(primaryFailure).ConfigureAwait(true);",
            sink,
            StringComparison.Ordinal);
        int typedRethrow = control.IndexOf("catch (MarkdownLayoutRetirementException retirementFailure)", StringComparison.Ordinal);
        int retirementRethrow = control.IndexOf("throw;", typedRethrow, StringComparison.Ordinal);
        int methodEnd = control.IndexOf(
            "private static LayoutSnapshot? BuildSnapshotOrNullOnCancellation(",
            rebuild,
            StringComparison.Ordinal);
        string rebuildBody = control[rebuild..methodEnd];

        Assert.True(rebuild >= 0);
        Assert.True(sink > rebuild);
        Assert.True(pipelineDrain > sink);
        Assert.True(typedRethrow >= 0);
        Assert.True(retirementRethrow > typedRethrow);
        Assert.Equal(2, rebuildBody.Split("ctx.RegisterRetirement(snapshot.Retire());", StringSplitOptions.None).Length - 1);
        Assert.Contains("ctx.RegisterRetirement(RetireSnapshot(old));", rebuildBody, StringComparison.Ordinal);
        Assert.Contains("retirementFailure.TakePrimaryFailure()", control, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot?.Dispose();", rebuildBody, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.Dispose();", rebuildBody, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionAdornerRetryMeasuresOnlyActiveWorkAgainstItsCapturedLogicalFrame()
    {
        string control = File.ReadAllText(FindControlSource());
        int retry = control.IndexOf("private void OnSelectionAdornerRetryFrame(", StringComparison.Ordinal);
        int nextMethod = control.IndexOf("private void ScheduleSelectionAdornerRetry()", retry, StringComparison.Ordinal);
        Assert.True(retry >= 0 && nextMethod > retry);
        string method = control[retry..nextMethod];

        int pendingWork = method.IndexOf(
            "_selectionAdornerRetryState.IsPending || _selectionAdornerRefreshInProgress",
            StringComparison.Ordinal);
        int measurementEnabled = method.IndexOf("performance.IsMeasurementEnabled()", StringComparison.Ordinal);
        int captureFrame = method.IndexOf("MarkdownPerformanceEventSource.GetLogicalFrameId()", StringComparison.Ordinal);
        int measuredAttempt = method.LastIndexOf("TrySelectionAdornerRefreshAttempt();", StringComparison.Ordinal);
        int finallyBlock = method.LastIndexOf("finally", StringComparison.Ordinal);
        int paintWork = method.IndexOf("performance.PaintWork(", StringComparison.Ordinal);

        Assert.True(pendingWork >= 0 && measurementEnabled > pendingWork);
        Assert.True(captureFrame > measurementEnabled && measuredAttempt > captureFrame);
        Assert.Equal(1, method.Split("GetLogicalFrameId()", StringSplitOptions.None).Length - 1);
        Assert.Contains("!_canvasDeviceRecoveryQueued", method, StringComparison.Ordinal);
        Assert.Contains("if (!measure)", method, StringComparison.Ordinal);
        Assert.True(finallyBlock > measuredAttempt && paintWork > finallyBlock);
        Assert.Contains("logicalFrameId,", method[paintWork..], StringComparison.Ordinal);
        Assert.Contains("Stopwatch.GetTimestamp() - started", method[paintWork..], StringComparison.Ordinal);
        Assert.Contains("GC.GetAllocatedBytesForCurrentThread() - allocatedBefore", method[paintWork..], StringComparison.Ordinal);
    }

    [Fact]
    public void ScrollHarnessLogicalFrameIdLabelsTheOpenIntervalOnEitherSideOfItsRenderingSample()
    {
        string source = File.ReadAllText(FindPerformanceWindowSource());
        int runFrames = source.IndexOf("private async Task RunScrollFramesAsync(", StringComparison.Ordinal);
        Assert.True(runFrames >= 0);
        int nextMethod = source.IndexOf("private Task WaitForScrollTopAsync(", runFrames, StringComparison.Ordinal);
        Assert.True(nextMethod > runFrames);
        string method = source[runFrames..nextMethod];

        int timestampSample = method.IndexOf("long now = Stopwatch.GetTimestamp();", StringComparison.Ordinal);
        Assert.True(timestampSample >= 0);
        int intervalCapture = method.IndexOf(
            "intervalsMilliseconds.Add(elapsedTicks * 1000.0 / Stopwatch.Frequency);",
            timestampSample,
            StringComparison.Ordinal);
        Assert.True(intervalCapture > timestampSample);
        int nextFrameId = method.IndexOf(
            "MarkdownPerformanceEventSource.SetLogicalFrameId(firstFrameId + frame);",
            intervalCapture,
            StringComparison.Ordinal);
        int frameWork = method.IndexOf("_renderer.SetAutomationVerticalScrollPercent(", nextFrameId, StringComparison.Ordinal);

        Assert.True(nextFrameId > intervalCapture && frameWork > nextFrameId);
    }

    private static string FindPerformanceWindowSource()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string path = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer.PerformanceHarness",
                "PerformanceWindow.cs");
            if (File.Exists(path))
                return path;
        }

        throw new FileNotFoundException("Could not locate PerformanceWindow.cs from the test base directory.");
    }

    [Fact]
    public void CompletionDoesNotRetirePublishedPipelineLifetime()
    {
        string control = File.ReadAllText(FindControlSource());
        int completion = control.IndexOf("private void CompleteRebuildTask(", StringComparison.Ordinal);
        int nextMethod = control.IndexOf("private void OnRebuildPipelineCompleted(", completion, StringComparison.Ordinal);
        string method = control[completion..nextMethod];

        Assert.DoesNotContain("_pipelineCts = null", method, StringComparison.Ordinal);
        Assert.DoesNotContain("_activePipelineGeneration = 0", method, StringComparison.Ordinal);
        Assert.Contains("published snapshot's image and lazy-layout lifetime", method, StringComparison.Ordinal);
    }

    private static string FindControlSource()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string direct = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "MarkdownRenderer",
                "Controls",
                "MarkdownRendererControl.cs");
            if (File.Exists(direct))
                return direct;

            string nested = Path.Combine(
                directory.FullName,
                "MarkdownRenderer",
                "Controls",
                "MarkdownRendererControl.cs");
            if (File.Exists(nested))
                return nested;
        }

        throw new FileNotFoundException("Could not locate MarkdownRendererControl.cs from the test base directory.");
    }
}
