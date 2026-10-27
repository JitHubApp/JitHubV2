namespace MarkdownRenderer.PerformanceHarness;

internal static class QuickPerformanceSmokeValidator
{
    private static readonly int[] FirstViewportSourceSizes =
    [
        100 * 1024,
        1024 * 1024,
        10 * 1024 * 1024,
    ];

    internal static IReadOnlyList<string> Validate(
        PerformanceReport report,
        int renderFailures,
        IReadOnlyList<string> providerErrors)
    {
        var failures = new List<string>();
        foreach (string error in providerErrors)
            failures.Add($"ETW provider error: {error}");

        if (!PerformanceArtifactContract.TryValidateStored(report.BuildArtifacts, out _))
            failures.Add("Quick smoke build artifacts did not include the frozen runtimeconfig evidence.");
        if (!PerformanceRuntimeContract.IsValid(report.RuntimeConfiguration))
            failures.Add("Quick smoke runtime configuration violated the frozen non-tiered runtime contract.");
        if (!PerformanceReleaseEvidenceValidator.HasRequiredQuickFirstViewportProtocol(
                report.FirstUsableViewport))
        {
            failures.Add(
                "Quick smoke did not produce the structural first-Williams-row warmup and measured evidence for all six first-viewport conditions.");
        }
        if (!PerformanceReleaseEvidenceValidator.AreFirstViewportTrialsWithinReportInterval(
                report.FirstUsableViewport,
                report.StartedUtc,
                report.CompletedUtc))
        {
            failures.Add(
                "Quick smoke first-viewport trials were not bounded by a positive UTC report interval.");
        }

        ScrollResult scroll = report.Scroll;
        ScrollTrialResult? trial = scroll.Trials.Count == 1 ? scroll.Trials[0] : null;
        bool rawScrollEvidenceValid = ScrollEvidenceValidator.TryValidate(
            scroll,
            PerformanceMeasurementContract.QuickScrollFrames,
            PerformanceMeasurementContract.QuickScrollTrials,
            out _);
        bool trialMetricsValid = trial is not null &&
            AllFiniteNonNegative(
                trial.UiThreadWorkP95Milliseconds,
                trial.UiThreadWorkP99Milliseconds,
                trial.ScrollCallbackWorkP95Milliseconds,
                trial.PaintCallbackWorkP95Milliseconds,
                trial.FrameTimeP95Milliseconds,
                trial.FramesOver16_7Percent,
                trial.MaximumFrameStallMilliseconds,
                trial.AllocatedBytesPerFrameP95,
                trial.RendererOwnedAllocatedBytesPerFrameP95,
                trial.PlatformProjectionAllocatedBytesPerFrameP95,
                trial.ScrollAllocatedBytesPerFrameP95,
                trial.PaintAllocatedBytesPerFrameP95,
                trial.ScrollLazyLayoutAllocatedBytesP95,
                trial.ScrollAdornerAllocatedBytesP95,
                trial.ScrollRealizationAllocatedBytesP95,
                trial.ScrollHighlightingAllocatedBytesP95,
                trial.PaintSchedulingAllocatedBytesP95,
                trial.PaintPlatformSessionAllocatedBytesP95,
                trial.SnapshotPaintAllocatedBytesP95,
                trial.InteractivePaintAllocatedBytesP95,
                trial.InlinePaintAllocatedBytesP95,
                trial.TablePaintAllocatedBytesP95,
                trial.CodePaintAllocatedBytesP95,
                trial.OtherPaintAllocatedBytesP95) &&
            AllNonNegative(
                trial.MaximumAllocatedBytesPerFrame,
                trial.MaximumRendererOwnedAllocatedBytesPerFrame,
                trial.MaximumPlatformProjectionAllocatedBytesPerFrame,
                trial.MaximumRealizedCodeActions) &&
            trial.UiThreadWorkP99Milliseconds >= trial.UiThreadWorkP95Milliseconds &&
            trial.MaximumFrameStallMilliseconds >= trial.FrameTimeP95Milliseconds &&
            trial.FramesOver16_7Percent <= 100 &&
            trial.MaximumAllocatedBytesPerFrame >= trial.AllocatedBytesPerFrameP95 &&
            trial.MaximumRendererOwnedAllocatedBytesPerFrame >=
                trial.RendererOwnedAllocatedBytesPerFrameP95 &&
            trial.MaximumPlatformProjectionAllocatedBytesPerFrame >=
                trial.PlatformProjectionAllocatedBytesPerFrameP95 &&
            trial.MaximumOffscreenRealizedCodeActions == 0 &&
            trial.Gen2Collections >= 0;
        bool aggregateMetricsValid =
            AllFiniteNonNegative(
                scroll.RegressionUiThreadWorkP95Milliseconds,
                scroll.RegressionUiThreadWorkP99Milliseconds,
                scroll.RegressionFrameTimeP95Milliseconds,
                scroll.RegressionRendererOwnedAllocatedBytesPerFrameP95,
                scroll.UiThreadWorkP95Milliseconds,
                scroll.UiThreadWorkP99Milliseconds,
                scroll.ScrollCallbackWorkP95Milliseconds,
                scroll.PaintCallbackWorkP95Milliseconds,
                scroll.FrameTimeP95Milliseconds,
                scroll.FramesOver16_7Percent,
                scroll.MaximumFrameStallMilliseconds,
                scroll.AllocatedBytesPerFrameP95,
                scroll.RendererOwnedAllocatedBytesPerFrameP95,
                scroll.PlatformProjectionAllocatedBytesPerFrameP95,
                scroll.ScrollAllocatedBytesPerFrameP95,
                scroll.PaintAllocatedBytesPerFrameP95,
                scroll.ScrollLazyLayoutAllocatedBytesP95,
                scroll.ScrollAdornerAllocatedBytesP95,
                scroll.ScrollRealizationAllocatedBytesP95,
                scroll.ScrollHighlightingAllocatedBytesP95,
                scroll.PaintSchedulingAllocatedBytesP95,
                scroll.PaintPlatformSessionAllocatedBytesP95,
                scroll.SnapshotPaintAllocatedBytesP95,
                scroll.InteractivePaintAllocatedBytesP95,
                scroll.InlinePaintAllocatedBytesP95,
                scroll.TablePaintAllocatedBytesP95,
                scroll.CodePaintAllocatedBytesP95,
                scroll.OtherPaintAllocatedBytesP95) &&
            AllNonNegative(
                scroll.MaximumAllocatedBytesPerFrame,
                scroll.MaximumRendererOwnedAllocatedBytesPerFrame,
                scroll.MaximumPlatformProjectionAllocatedBytesPerFrame,
                scroll.MaximumRealizedCodeActions) &&
            scroll.UiThreadWorkP99Milliseconds >= scroll.UiThreadWorkP95Milliseconds &&
            scroll.MaximumFrameStallMilliseconds >= scroll.FrameTimeP95Milliseconds &&
            scroll.FramesOver16_7Percent <= 100 &&
            scroll.MaximumAllocatedBytesPerFrame >= scroll.AllocatedBytesPerFrameP95 &&
            scroll.MaximumRendererOwnedAllocatedBytesPerFrame >=
                scroll.RendererOwnedAllocatedBytesPerFrameP95 &&
            scroll.MaximumPlatformProjectionAllocatedBytesPerFrame >=
                scroll.PlatformProjectionAllocatedBytesPerFrameP95 &&
            scroll.AllocationGateBasis == "renderer-owned-managed" &&
            !string.IsNullOrWhiteSpace(scroll.RawAllocationScope) &&
            !string.IsNullOrWhiteSpace(scroll.PlatformProjectionAllocationScope) &&
            scroll.LohAllocationPossible == (scroll.MaximumAllocatedBytesPerFrame >= 85_000) &&
            scroll.RendererOwnedLohAllocationPossible ==
                (scroll.MaximumRendererOwnedAllocatedBytesPerFrame >= 85_000) &&
            scroll.MaximumOffscreenRealizedCodeActions == 0 &&
            scroll.Gen2Collections >= 0;
        bool scrollValid =
            scroll.Corpus == PerformanceDocumentFactory.WarmScrollStressCorpus &&
            scroll.SourceUtf16Bytes == 1024 * 1024 &&
            scroll.StopwatchFrequency == System.Diagnostics.Stopwatch.Frequency &&
            scroll.RequestedFrames == PerformanceMeasurementContract.QuickScrollFrames &&
            scroll.MeasuredFrameIntervals == PerformanceMeasurementContract.QuickScrollFrames &&
            scroll.FramesWithRendererWork == PerformanceMeasurementContract.QuickScrollFrames &&
            scroll.RegressionEstimator == "quick-single-trial-non-gating" &&
            trial is not null &&
            trial.Ordinal == 0 &&
            trial.FirstLogicalFrameId > 0 &&
            trial.RequestedFrames == PerformanceMeasurementContract.QuickScrollFrames &&
            trial.MeasuredFrameIntervals == PerformanceMeasurementContract.QuickScrollFrames &&
            trial.FramesWithRendererWork == PerformanceMeasurementContract.QuickScrollFrames &&
            IsFinitePositive(trial.FrameTimeP95Milliseconds) &&
            trialMetricsValid &&
            aggregateMetricsValid &&
            rawScrollEvidenceValid &&
            AggregatesMatchTrial(scroll, trial) &&
            trial.Complete;
        if (!scrollValid)
            failures.Add("Quick smoke did not produce one complete 30-frame scroll trial.");

        bool retainedMemoryValid = report.RetainedMemory.Count == FirstViewportSourceSizes.Length;
        foreach (int sourceBytes in FirstViewportSourceSizes)
        {
            RetainedMemoryResult[] matches = report.RetainedMemory
                .Where(item => item.SourceUtf16Bytes == sourceBytes)
                .ToArray();
            retainedMemoryValid &= matches.Length == 1 &&
                matches[0].Corpus == PerformanceDocumentFactory.GetCorpusName(sourceBytes) &&
                matches[0].ManagedDeltaBytes >= 0 &&
                matches[0].PrivateDeltaBytes >= 0 &&
                matches[0].BudgetBytes > 0;
        }
        if (!retainedMemoryValid)
            failures.Add("Quick smoke did not produce all three structurally valid retained-memory scenarios.");

        SourceLookupResult lookup = report.SourceLookup;
        double nanosecondsPerTick = lookup.StopwatchFrequency > 0
            ? 1_000_000_000.0 / lookup.StopwatchFrequency
            : double.NaN;
        bool lookupTrialsValid =
            lookup.RegressionTrials.Count ==
                PerformanceMeasurementContract.SourceLookupRegressionTrialCount &&
            lookup.RegressionTrials.Select(static trial => trial.Ordinal).SequenceEqual(
                Enumerable.Range(
                    0,
                    PerformanceMeasurementContract.SourceLookupRegressionTrialCount));
        long lookupTrialAllocatedBytes = 0;
        foreach (SourceLookupTrialResult trialResult in lookup.RegressionTrials)
        {
            lookupTrialsValid &=
                trialResult.ObservationCount ==
                    PerformanceMeasurementContract.SourceLookupRegressionObservationCount &&
                trialResult.OperationCount ==
                    PerformanceMeasurementContract.SourceLookupRegressionOperationCount &&
                trialResult.OperationCount ==
                    lookup.RegressionBatchSize * trialResult.ObservationCount &&
                trialResult.SamplesElapsedTicks.Count == trialResult.ObservationCount &&
                trialResult.SamplesNanosecondsPerLookup.Count == trialResult.ObservationCount &&
                trialResult.SamplesElapsedTicks.Zip(
                        trialResult.SamplesNanosecondsPerLookup,
                        (ticks, nanoseconds) =>
                            ticks > 0 &&
                            IsFinitePositive(nanoseconds) &&
                            NearlyEqual(
                                nanoseconds,
                                ticks * nanosecondsPerTick / lookup.RegressionBatchSize))
                    .All(static matches => matches) &&
                IsFinitePositive(trialResult.P95Nanoseconds) &&
                NearlyEqual(
                    trialResult.P95Nanoseconds,
                    PerformanceStatistics.Percentile(
                        trialResult.SamplesNanosecondsPerLookup,
                        0.95)) &&
                trialResult.AllocatedBytes == 0 &&
                trialResult.Complete;
            lookupTrialAllocatedBytes = checked(
                lookupTrialAllocatedBytes + trialResult.AllocatedBytes);
        }
        bool lookupValid =
            lookup.MappingCount == PerformanceMeasurementContract.SourceLookupMappingCount &&
            lookup.QueryCount == PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount &&
            lookup.StopwatchFrequency == System.Diagnostics.Stopwatch.Frequency &&
            PinnedMeasurementThreadScope.IsEvidenceValid(
                lookup.MeasurementThreadAffinityMask,
                lookup.MeasurementProcessorGroup,
                lookup.MeasurementProcessorNumber,
                lookup.MeasurementThreadPriority,
                ThreadPriority.Highest) &&
            lookup.SamplesElapsedTicks.Count ==
                PerformanceMeasurementContract.SourceLookupAbsoluteQueryCount &&
            lookup.SamplesElapsedTicks.All(static ticks => ticks >= 0) &&
            IsFiniteNonNegative(lookup.P95Nanoseconds) &&
            NearlyEqual(
                lookup.P95Nanoseconds,
                PerformanceStatistics.Percentile(lookup.SamplesElapsedTicks, 0.95) *
                nanosecondsPerTick) &&
            lookup.AllocatedBytes == 0 &&
            lookup.RegressionWarmupPasses ==
                PerformanceMeasurementContract.SourceLookupRegressionWarmupPasses &&
            lookup.RegressionEstimator ==
                PerformanceMeasurementContract.SourceLookupRegressionEstimator &&
            lookup.RegressionTrialCount ==
                PerformanceMeasurementContract.SourceLookupRegressionTrialCount &&
            lookup.RegressionBatchSize ==
                PerformanceMeasurementContract.SourceLookupRegressionBatchSize &&
            lookup.RegressionObservationCount ==
                PerformanceMeasurementContract.SourceLookupRegressionObservationCount &&
            lookup.RegressionOperationCount ==
                PerformanceMeasurementContract.SourceLookupRegressionOperationCount &&
            lookupTrialsValid &&
            IsFinitePositive(lookup.RegressionP95Nanoseconds) &&
            NearlyEqual(
                lookup.RegressionP95Nanoseconds,
                PerformanceStatistics.HodgesLehmann(
                    lookup.RegressionTrials.Select(
                        static trial => trial.P95Nanoseconds))) &&
            lookup.RegressionAllocatedBytes == lookupTrialAllocatedBytes &&
            lookupTrialAllocatedBytes == 0;
        if (!lookupValid)
            failures.Add("Quick smoke source-lookup samples or tick normalization were structurally invalid.");

        LifecyclePlateauResult lifecycle = report.LifecyclePlateau;
        double managedGrowth = PerformanceStatistics.GrowthPercent(
            lifecycle.Checkpoint100ManagedBytes,
            lifecycle.Checkpoint80ManagedBytes);
        double privateGrowth = PerformanceStatistics.GrowthPercent(
            lifecycle.Checkpoint100PrivateBytes,
            lifecycle.Checkpoint80PrivateBytes);
        bool lifecycleValid =
            lifecycle.RequiredCycles == PerformanceMeasurementContract.QuickLifecycleCycles &&
            lifecycle.CompletedCycles == PerformanceMeasurementContract.QuickLifecycleCycles &&
            lifecycle.SyntheticDeviceResets == 0 &&
            lifecycle.Checkpoint80ManagedBytes > 0 &&
            lifecycle.Checkpoint100ManagedBytes > 0 &&
            lifecycle.Checkpoint80PrivateBytes > 0 &&
            lifecycle.Checkpoint100PrivateBytes > 0 &&
            IsFiniteNonNegative(lifecycle.ManagedGrowthPercent) &&
            IsFiniteNonNegative(lifecycle.PrivateGrowthPercent) &&
            IsFiniteNonNegative(lifecycle.GrowthPercent) &&
            NearlyEqual(lifecycle.ManagedGrowthPercent, managedGrowth) &&
            NearlyEqual(lifecycle.PrivateGrowthPercent, privateGrowth) &&
            NearlyEqual(lifecycle.GrowthPercent, Math.Max(managedGrowth, privateGrowth)) &&
            lifecycle.RenderFailures == 0;
        if (!lifecycleValid)
            failures.Add("Quick smoke did not complete eight lifecycle iterations without render failures.");

        CancellationResult cancellation = report.Cancellation;
        bool cancellationValid = CancellationEvidenceValidator.TryValidate(
                cancellation,
                PerformanceMeasurementContract.QuickCancellationIterations,
                PerformanceMeasurementContract.QuickCancellationTrials,
                out CancellationEvidenceStatistics cancellationStatistics) &&
            cancellationStatistics.WarmupStaleCommits == 0 &&
            cancellationStatistics.StaleCommits == 0 &&
            cancellation.Trials.All(static trial =>
                trial.WarmupStaleCommits == 0 && trial.StaleCommits == 0) &&
            cancellation.TrialStartPolicy ==
                PerformanceMeasurementContract.CancellationTrialStartPolicy &&
            cancellation.RegressionEstimator == "quick-single-trial-non-gating";
        if (!cancellationValid)
            failures.Add("Quick smoke did not produce one valid cancellation without a stale commit.");

        RegressionResult regression = report.Regression;
        bool regressionSkipped =
            regression.Mode == "quick-non-gating" &&
            regression.DecisionPolicy ==
                PerformanceMeasurementContract.RegressionDecisionPolicy &&
            regression.LatencyRegressionBudgetPercent == 5 &&
            regression.AllocationRegressionBudgetPercent == 2 &&
            string.IsNullOrEmpty(regression.ReferencePath) &&
            string.IsNullOrEmpty(regression.ReferenceReportSha256) &&
            string.IsNullOrEmpty(regression.ReferenceBuildIdentity) &&
            !regression.ReferenceEligible &&
            !regression.MachineComparable &&
            regression.ComparedLatencyMetrics == 0 &&
            regression.ComparedAllocationMetrics == 0 &&
            regression.Comparisons.Count == 0 &&
            !regression.Passed;
        if (!regressionSkipped)
            failures.Add("Quick smoke unexpectedly evaluated release regression gates.");
        if (renderFailures != 0)
            failures.Add($"Quick smoke observed {renderFailures} renderer failure(s).");

        return failures;
    }

    private static bool IsFinitePositive(double value)
        => double.IsFinite(value) && value > 0;

    private static bool IsFiniteNonNegative(double value)
        => double.IsFinite(value) && value >= 0;

    private static bool AllFiniteNonNegative(params double[] values)
        => values.All(IsFiniteNonNegative);

    private static bool AllNonNegative(params long[] values)
        => values.All(static value => value >= 0);

    private static bool AggregatesMatchTrial(ScrollResult scroll, ScrollTrialResult trial)
        => NearlyEqual(scroll.RegressionUiThreadWorkP95Milliseconds, trial.UiThreadWorkP95Milliseconds) &&
           NearlyEqual(scroll.RegressionUiThreadWorkP99Milliseconds, trial.UiThreadWorkP99Milliseconds) &&
           NearlyEqual(scroll.RegressionFrameTimeP95Milliseconds, trial.FrameTimeP95Milliseconds) &&
           NearlyEqual(
               scroll.RegressionRendererOwnedAllocatedBytesPerFrameP95,
               trial.RendererOwnedAllocatedBytesPerFrameP95) &&
           NearlyEqual(scroll.UiThreadWorkP95Milliseconds, trial.UiThreadWorkP95Milliseconds) &&
           NearlyEqual(scroll.UiThreadWorkP99Milliseconds, trial.UiThreadWorkP99Milliseconds) &&
           NearlyEqual(scroll.ScrollCallbackWorkP95Milliseconds, trial.ScrollCallbackWorkP95Milliseconds) &&
           NearlyEqual(scroll.PaintCallbackWorkP95Milliseconds, trial.PaintCallbackWorkP95Milliseconds) &&
           NearlyEqual(scroll.FrameTimeP95Milliseconds, trial.FrameTimeP95Milliseconds) &&
           NearlyEqual(scroll.FramesOver16_7Percent, trial.FramesOver16_7Percent) &&
           NearlyEqual(scroll.MaximumFrameStallMilliseconds, trial.MaximumFrameStallMilliseconds) &&
           NearlyEqual(scroll.AllocatedBytesPerFrameP95, trial.AllocatedBytesPerFrameP95) &&
           scroll.MaximumAllocatedBytesPerFrame == trial.MaximumAllocatedBytesPerFrame &&
           NearlyEqual(
               scroll.RendererOwnedAllocatedBytesPerFrameP95,
               trial.RendererOwnedAllocatedBytesPerFrameP95) &&
           scroll.MaximumRendererOwnedAllocatedBytesPerFrame ==
               trial.MaximumRendererOwnedAllocatedBytesPerFrame &&
           NearlyEqual(
               scroll.PlatformProjectionAllocatedBytesPerFrameP95,
               trial.PlatformProjectionAllocatedBytesPerFrameP95) &&
           scroll.MaximumPlatformProjectionAllocatedBytesPerFrame ==
               trial.MaximumPlatformProjectionAllocatedBytesPerFrame &&
           NearlyEqual(scroll.ScrollAllocatedBytesPerFrameP95, trial.ScrollAllocatedBytesPerFrameP95) &&
           NearlyEqual(scroll.PaintAllocatedBytesPerFrameP95, trial.PaintAllocatedBytesPerFrameP95) &&
           NearlyEqual(scroll.ScrollLazyLayoutAllocatedBytesP95, trial.ScrollLazyLayoutAllocatedBytesP95) &&
           NearlyEqual(scroll.ScrollAdornerAllocatedBytesP95, trial.ScrollAdornerAllocatedBytesP95) &&
           NearlyEqual(scroll.ScrollRealizationAllocatedBytesP95, trial.ScrollRealizationAllocatedBytesP95) &&
           NearlyEqual(scroll.ScrollHighlightingAllocatedBytesP95, trial.ScrollHighlightingAllocatedBytesP95) &&
           NearlyEqual(scroll.PaintSchedulingAllocatedBytesP95, trial.PaintSchedulingAllocatedBytesP95) &&
           NearlyEqual(scroll.PaintPlatformSessionAllocatedBytesP95, trial.PaintPlatformSessionAllocatedBytesP95) &&
           NearlyEqual(scroll.SnapshotPaintAllocatedBytesP95, trial.SnapshotPaintAllocatedBytesP95) &&
           NearlyEqual(scroll.InteractivePaintAllocatedBytesP95, trial.InteractivePaintAllocatedBytesP95) &&
           NearlyEqual(scroll.InlinePaintAllocatedBytesP95, trial.InlinePaintAllocatedBytesP95) &&
           NearlyEqual(scroll.TablePaintAllocatedBytesP95, trial.TablePaintAllocatedBytesP95) &&
           NearlyEqual(scroll.CodePaintAllocatedBytesP95, trial.CodePaintAllocatedBytesP95) &&
           NearlyEqual(scroll.OtherPaintAllocatedBytesP95, trial.OtherPaintAllocatedBytesP95) &&
           scroll.MaximumRealizedCodeActions == trial.MaximumRealizedCodeActions &&
           scroll.MaximumOffscreenRealizedCodeActions == trial.MaximumOffscreenRealizedCodeActions &&
           scroll.Gen2Collections == trial.Gen2Collections;

    private static bool NearlyEqual(double left, double right)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right))
            return false;
        double scale = Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= scale * 1e-12;
    }
}
