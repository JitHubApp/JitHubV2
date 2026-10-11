using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Source contracts for visible-first scene admission. The policy and ledger
/// have separate behavioral tests; these checks protect the WinUI/layout
/// handoff without claiming to replace live dispatcher or UI automation tests.
/// </summary>
public sealed class VectorCapacityControlContractTests
{
    [Fact]
    public void RealizationMeasuresOverscanButReplacesVectorResidencyForVisibleBandOnly()
    {
        string source = Read("Layout", "LayoutSnapshot.cs");
        Assert.Contains(
            "=> EnsureMeasuredBand(band, band, cancellationToken);",
            source,
            StringComparison.Ordinal);

        string ensure = Section(source,
            "internal LazyLayoutCommit EnsureMeasuredBand(\n        LazyLayoutBand realizationBand,",
            "internal (int BlockIndex, double OffsetFromTop)? CaptureScrollAnchor(");
        int measureRange = ensure.IndexOf(
            "FindViewportRangeNoLock(realizationBand.Top, realizationBand.Bottom)",
            StringComparison.Ordinal);
        int residencyRange = ensure.IndexOf(
            "FindViewportRangeNoLock(visibleVectorBand.Top, visibleVectorBand.Bottom)",
            StringComparison.Ordinal);
        int replace = ensure.IndexOf(
            "residencyPolicy.ReplaceActiveBand(\n                        requestedSceneBandOrdinals,\n                        visibleVectorBand,",
            StringComparison.Ordinal);

        Assert.True(measureRange >= 0 && residencyRange > measureRange && replace > residencyRange);
        Assert.Contains("if (_lazyLayoutEnabled && _measuredTopLevelBlocks is not null)", ensure, StringComparison.Ordinal);
        Assert.Contains("if (_vectorSceneResidencyPolicy is { } residencyPolicy)", ensure, StringComparison.Ordinal);
        Assert.Contains("LayoutPlacementGeneration scenePlacements = CurrentPlacementGenerationNoLock();",
            ensure, StringComparison.Ordinal);
        Assert.Contains("visibleVectorBand,\n                        scenePlacements,\n                        _topLevelOrdinalByBlockIndex,",
            ensure, StringComparison.Ordinal);
    }

    [Fact]
    public void SettlementChecksRealizationCoverageAndVisibleVectorReadinessSeparately()
    {
        string source = Read("Layout", "LayoutSnapshot.cs");
        string settlement = Section(source,
            "internal bool IsBandWorkSettled(\n        LazyLayoutBand realizationBand,",
            "internal bool IsRealizationBandMeasured(");
        Assert.Contains("IsBandMeasured(realizationBand, visibleVectorBand, acceptTerminalFailure: true)",
            settlement, StringComparison.Ordinal);

        string readiness = Section(source,
            "private bool IsBandMeasured(\n        LazyLayoutBand realizationBand,",
            "private bool IsRealizationBandMeasuredNoLock(");
        Assert.Contains("IsRealizationBandMeasuredNoLock(realizationBand)", readiness, StringComparison.Ordinal);
        Assert.Contains("FindViewportRangeNoLock(visibleVectorBand.Top, visibleVectorBand.Bottom)",
            readiness, StringComparison.Ordinal);
        Assert.Contains("policy.IsBandWorkSettled(\n                        _sceneBandOrdinalScratch,\n                        visibleVectorBand,\n                        placements,\n                        _topLevelOrdinalByBlockIndex)",
            readiness, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererUsesActualInitialViewportForVectorResidencyAtEveryOptedInDocumentSize()
    {
        string builder = Read("Layout", "LayoutBuilder.cs");
        string viewportEntry = Section(builder,
            "internal LayoutSnapshot BuildLazyForViewport(",
            "private LayoutSnapshot BuildLazyWithVisibleVectorBand(");
        Assert.Contains("LazyLayoutBand.FromViewport(viewportTop, viewportHeight, overscan: 0)",
            viewportEntry, StringComparison.Ordinal);

        string control = Read("Controls", "MarkdownRendererControl.cs");
        string chooseLayout = Section(control,
            "bool visibleFirstVectorAdmissionRequested = performanceSessionSnapshot is",
            "double initialLazyOverscan =");
        Assert.Contains("Options.DeferOffscreenScenes: true", chooseLayout, StringComparison.Ordinal);
        Assert.Contains("visibleFirstVectorAdmissionRequested || MarkdownLazyLayoutPolicy.ShouldUse(",
            chooseLayout, StringComparison.Ordinal);
        Assert.Contains("builder.BuildLazyForViewport(document, width, viewportTop, viewportHeight, overscan, ct)",
            control, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitEagerBuilderKeepsItsFullMeasureContractAndAllowsResidencyOnlyRetry()
    {
        string builder = Read("Layout", "LayoutBuilder.cs");
        string eager = Section(builder,
            "private LayoutSnapshot BuildEagerCore(",
            "public LayoutSnapshot BuildLazy(");
        int fullMeasure = eager.IndexOf("for (int blockOrdinal = 0; blockOrdinal < blocks.Count; blockOrdinal++)",
            StringComparison.Ordinal);
        int createPolicy = eager.IndexOf("CreateVectorSceneResidencyPolicy(", StringComparison.Ordinal);
        int initialResidency = eager.IndexOf(
            "snapshot.EnsureMeasuredBand(\n                    eagerDocumentBand, eagerDocumentBand,",
            StringComparison.Ordinal);
        Assert.True(fullMeasure >= 0 && createPolicy > fullMeasure && initialResidency > createPolicy);

        string snapshot = Read("Layout", "LayoutSnapshot.cs");
        string ensure = Section(snapshot,
            "internal LazyLayoutCommit EnsureMeasuredBand(\n        LazyLayoutBand realizationBand,",
            "internal (int BlockIndex, double OffsetFromTop)? CaptureScrollAnchor(");
        int lazyMeasure = ensure.IndexOf("if (_lazyLayoutEnabled && _measuredTopLevelBlocks is not null)",
            StringComparison.Ordinal);
        int policyRefresh = ensure.IndexOf("if (_vectorSceneResidencyPolicy is { } residencyPolicy)",
            StringComparison.Ordinal);
        Assert.True(lazyMeasure >= 0 && policyRefresh > lazyMeasure);
        Assert.Contains("if ((!_lazyLayoutEnabled || _measuredTopLevelBlocks is null) &&\n                _vectorSceneResidencyPolicy is null)",
            ensure, StringComparison.Ordinal);
        int rollbackScope = ensure.IndexOf("using var rollbackScope = LayoutPassCancellation.Push(CancellationToken.None);",
            StringComparison.Ordinal);
        Assert.True(rollbackScope >= 0);
        int rollbackGate = ensure.IndexOf("if (firstMeasureAttempted < Blocks.Count)", rollbackScope,
            StringComparison.Ordinal);
        int reflowRollback = ensure.IndexOf("ReflowNoLock(incrementRevision: false,", rollbackGate,
            StringComparison.Ordinal);
        Assert.True(rollbackGate >= 0 && reflowRollback > rollbackGate);

        string control = Read("Controls", "MarkdownRendererControl.cs");
        string viewport = Section(control,
            "private void QueueLazyLayoutForViewport(",
            "private void QueueLazyLayoutForPaintRegion(");
        Assert.Contains("if (!snapshot.IsLazyLayoutEnabled && !capacityDeferred)\n            return;",
            viewport, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewportAndPaintQueueKeepLatestVisibleBandSeparateFromUnionedRealization()
    {
        string control = Read("Controls", "MarkdownRendererControl.cs");
        string viewport = Section(control,
            "private void QueueLazyLayoutForViewport(",
            "private void QueueLazyLayoutForPaintRegion(");
        string paint = Section(control,
            "private void QueueLazyLayoutForPaintRegion(",
            "private void QueueLazyLayoutForBand(");
        string coalescing = Section(control,
            "private void QueueLazyLayoutForBand(",
            "private void ProcessQueuedLazyLayoutRealization(");

        Assert.Contains("overscan: 0", viewport, StringComparison.Ordinal);
        Assert.Contains("QueueLazyLayoutForBand(\n            snapshot,\n            band,\n            visibleVectorBand,", viewport, StringComparison.Ordinal);
        Assert.Contains("QueueLazyLayoutForBand(\n            snapshot,\n            band,\n            visibleVectorBand,", paint, StringComparison.Ordinal);
        Assert.Contains("_pendingLazyLayoutBand = new LazyLayoutBand(", coalescing, StringComparison.Ordinal);
        Assert.Contains("_pendingLazyLayoutVisibleVectorBand = visibleVectorBand;", coalescing, StringComparison.Ordinal);
        Assert.Contains("visibleVectorBand.Top == _activeLazyLayoutVisibleVectorBand.Top", coalescing,
            StringComparison.Ordinal);
        Assert.Contains("visibleVectorBand.Bottom == _activeLazyLayoutVisibleVectorBand.Bottom", coalescing,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CapacityObserverIsWeakGenerationFencedAndHasBoundedRejectedDispatchRetry()
    {
        string control = Read("Controls", "MarkdownRendererControl.VectorCapacity.cs");
        string synchronize = Section(control,
            "private void SynchronizeVectorCapacityObserver(LayoutSnapshot? snapshot)",
            "private bool ConsumePendingVectorCapacityWake(");
        Assert.Contains("snapshot.ActiveCapacityGeneration", synchronize, StringComparison.Ordinal);
        Assert.Contains("RegisterCapacityObserver(observer)", synchronize, StringComparison.Ordinal);
        Assert.Contains("generationAfterRegistration > generationBeforeRegistration", synchronize,
            StringComparison.Ordinal);

        string observer = Section(control,
            "private sealed class VectorCapacityWakeObserver",
            "private void OnRetryClock()");
        Assert.Contains("WeakReference<MarkdownRendererControl> _owner", observer, StringComparison.Ordinal);
        Assert.Contains("WeakReference<VectorCapacityWakeObserver> _self", observer, StringComparison.Ordinal);
        Assert.Contains("Timer _retryTimer", observer, StringComparison.Ordinal);
        Assert.Contains("Interlocked.CompareExchange(ref _pendingGeneration, generation, current)",
            observer, StringComparison.Ordinal);
        Assert.Contains("AcknowledgeWakeDispatch(ticket, resetRetryAttempt: false)", observer,
            StringComparison.Ordinal);

        string retry = Section(control,
            "private void ArmRetryClock()",
            "private void StopRetryClock()");
        Assert.Contains("Math.Min(Interlocked.Increment(ref _retryAttempt), 5)", retry, StringComparison.Ordinal);
        Assert.Contains("Math.Min(1_600, 100 << (attempt - 1))", retry, StringComparison.Ordinal);
        Assert.Contains("_retryTimer.Change(delayMilliseconds, Timeout.Infinite)", retry, StringComparison.Ordinal);
    }

    [Fact]
    public void CapacityObserverIsResetAtReplacementUnloadAndGeometryRecoveryBoundaries()
    {
        string control = Read("Controls", "MarkdownRendererControl.cs");
        string publish = Section(control,
            "_snapshot = snapshot;",
            "_snapshotPipelineStartTimestamp = pipelineStartTimestamp;");
        Assert.Contains("SynchronizeVectorCapacityObserver(snapshot);", publish, StringComparison.Ordinal);

        string releaseResources = Section(control,
            "private void ReleaseLoadedResources()",
            "private void");
        Assert.Contains("ResetVectorCapacityObserver();", releaseResources, StringComparison.Ordinal);

        string recovery = Read("Controls", "MarkdownRendererControl.GeometryRecovery.cs");
        string recover = Section(recovery,
            "private bool RequestGeometryRecoveryIfRequired(LayoutSnapshot? snapshot)",
            "private static bool TryGeometryRecoveryCleanup(Action cleanup)");
        Assert.Contains("TryGeometryRecoveryCleanup(ResetVectorCapacityObserver);", recover, StringComparison.Ordinal);

        string capacity = Read("Controls", "MarkdownRendererControl.VectorCapacity.cs");
        string reset = Section(capacity,
            "private void ResetVectorCapacityObserver()",
            "private sealed class VectorCapacityWakeObserver");
        Assert.Contains("observer?.Deactivate();", reset, StringComparison.Ordinal);
        Assert.Contains("registration?.Dispose()", reset, StringComparison.Ordinal);
    }

    private static string Section(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, $"Missing source section start: {start}");
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, $"Missing source section end: {end}");
        return source[first..last];
    }

    private static string Read(string directory, string file)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string root = Path.Combine(current.FullName, "MarkdownRenderer", "MarkdownRenderer");
            if (File.Exists(Path.Combine(root, "MarkdownRenderer.csproj")))
                return File.ReadAllText(Path.Combine(root, directory, file))
                    .Replace("\r\n", "\n", StringComparison.Ordinal);
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("MarkdownRenderer source root was not found.");
    }
}
