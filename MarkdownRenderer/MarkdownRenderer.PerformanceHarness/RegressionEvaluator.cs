namespace MarkdownRenderer.PerformanceHarness;

internal static class RegressionEvaluator
{
    private const double LatencyBudgetPercent = 5;
    private const double AllocationBudgetPercent = 2;

    internal static RegressionResult Evaluate(
        PerformanceReport candidate,
        PerformanceOptions options)
    {
        if (options.EstablishBaseline)
        {
            return new RegressionResult
            {
                Mode = "baseline",
                ReferenceEligible = true,
                MachineComparable = true,
                Passed = true,
            };
        }

        if (options.Quick)
        {
            return new RegressionResult
            {
                Mode = "quick-non-gating",
                MachineComparable = false,
                Passed = false,
            };
        }

        if (options.ReferencePath is null)
        {
            return new RegressionResult
            {
                Mode = "missing-reference",
                MachineComparable = false,
                Passed = false,
            };
        }

        PerformanceReportEvidence referenceEvidence =
            PerformanceReport.ReadEvidence(options.ReferencePath);
        PerformanceReport reference = referenceEvidence.Report;
        string referenceReportSha256 = referenceEvidence.Sha256;
        bool sameBuildIdentity =
            !string.IsNullOrWhiteSpace(candidate.BuildIdentity) &&
            string.Equals(candidate.BuildIdentity, reference.BuildIdentity, StringComparison.Ordinal);
        bool sameBuildArtifacts = PerformanceArtifactContract.HaveIdenticalHashes(
            candidate.BuildArtifacts,
            reference.BuildArtifacts);
        // Equal deployment manifests must also agree on every explicitly named
        // critical artifact. Different manifests may legitimately share those
        // five hashes when another private runtime dependency changed.
        bool buildIdentityMatchesArtifacts =
            !sameBuildIdentity || sameBuildArtifacts;
        bool referenceEligible =
            PerformanceReleaseEvidenceValidator.IsEligibleBaselineReference(reference) &&
            buildIdentityMatchesArtifacts;
        bool candidateEvidenceComplete =
            PerformanceReleaseEvidenceValidator.HasCompleteCurrentMeasurementPayload(candidate);
        bool machineComparable = candidateEvidenceComplete &&
            PerformanceReleaseEvidenceValidator.IsMachineComparable(
                candidate.Machine,
                reference.Machine) &&
            PerformanceReleaseEvidenceValidator.HasSameFirstViewportExecutionEnvironment(
                candidate.FirstUsableViewport,
                reference.FirstUsableViewport) &&
            PerformanceReleaseEvidenceValidator.HasSameSourceLookupExecutionEnvironment(
                candidate.SourceLookup,
                reference.SourceLookup);
        var comparisons = new List<RegressionComparison>();

        foreach (FirstViewportResult candidateResult in candidate.FirstUsableViewport)
        {
            FirstViewportResult? referenceResult = reference.FirstUsableViewport.FirstOrDefault(
                item => item.Mode == candidateResult.Mode &&
                        item.Corpus == candidateResult.Corpus &&
                        item.SourceUtf16Bytes == candidateResult.SourceUtf16Bytes);
            if (referenceResult is null)
                continue;

            comparisons.Add(Compare(
                $"firstViewport.{candidateResult.Mode}.{candidateResult.SourceUtf16Bytes}.p95Ms.hodgesLehmann",
                "latency",
                referenceResult.RegressionP95Milliseconds,
                candidateResult.RegressionP95Milliseconds,
                referenceResult.Trials.Select(static trial => trial.P95Milliseconds),
                candidateResult.Trials.Select(static trial => trial.P95Milliseconds),
                LatencyBudgetPercent,
                PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
                    candidateResult.Mode,
                    candidateResult.SourceUtf16Bytes),
                PerformanceMeasurementContract.FirstViewportDispersionBudgetPercent,
                referenceResult.StationarityEvaluated && referenceResult.StationarityPassed,
                candidateResult.StationarityEvaluated && candidateResult.StationarityPassed));
        }

        AddLatency(
            comparisons,
            "scroll.uiThreadWorkP95Ms.hodgesLehmann",
            reference.Scroll.RegressionUiThreadWorkP95Milliseconds,
            candidate.Scroll.RegressionUiThreadWorkP95Milliseconds,
            reference.Scroll.Trials.Select(static trial => trial.UiThreadWorkP95Milliseconds),
            candidate.Scroll.Trials.Select(static trial => trial.UiThreadWorkP95Milliseconds),
            PerformanceMeasurementContract.ScrollUiThreadWorkP95NoiseFloorMilliseconds,
            PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent);
        AddLatency(
            comparisons,
            "scroll.uiThreadWorkP99Ms.hodgesLehmann",
            reference.Scroll.RegressionUiThreadWorkP99Milliseconds,
            candidate.Scroll.RegressionUiThreadWorkP99Milliseconds,
            reference.Scroll.Trials.Select(static trial => trial.UiThreadWorkP99Milliseconds),
            candidate.Scroll.Trials.Select(static trial => trial.UiThreadWorkP99Milliseconds),
            PerformanceMeasurementContract.ScrollUiThreadWorkP99NoiseFloorMilliseconds,
            PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent);
        AddLatency(
            comparisons,
            "scroll.frameTimeP95Ms.hodgesLehmann",
            reference.Scroll.RegressionFrameTimeP95Milliseconds,
            candidate.Scroll.RegressionFrameTimeP95Milliseconds,
            reference.Scroll.Trials.Select(static trial => trial.FrameTimeP95Milliseconds),
            candidate.Scroll.Trials.Select(static trial => trial.FrameTimeP95Milliseconds),
            PerformanceMeasurementContract.ScrollFrameTimeP95NoiseFloorMilliseconds,
            PerformanceMeasurementContract.ScrollFrameTimeDispersionBudgetPercent);
        AddLatency(
            comparisons,
            "sourceLookup.regressionP95Ns.hodgesLehmann",
            reference.SourceLookup.RegressionP95Nanoseconds,
            candidate.SourceLookup.RegressionP95Nanoseconds,
            reference.SourceLookup.RegressionTrials.Select(static trial => trial.P95Nanoseconds),
            candidate.SourceLookup.RegressionTrials.Select(static trial => trial.P95Nanoseconds),
            PerformanceMeasurementContract.SourceLookupNoiseFloorNanoseconds,
            PerformanceMeasurementContract.SourceLookupDispersionBudgetPercent);
        AddLatency(
            comparisons,
            "cancellation.p95Ms.hodgesLehmann",
            reference.Cancellation.RegressionP95Milliseconds,
            candidate.Cancellation.RegressionP95Milliseconds,
            reference.Cancellation.Trials.Select(static trial => trial.P95Milliseconds),
            candidate.Cancellation.Trials.Select(static trial => trial.P95Milliseconds),
            PerformanceMeasurementContract.CancellationNoiseFloorMilliseconds,
            PerformanceMeasurementContract.CancellationDispersionBudgetPercent);
        comparisons.Add(Compare(
            "scroll.rendererOwnedAllocatedBytesPerFrameP95.hodgesLehmann",
            "allocation",
            reference.Scroll.RegressionRendererOwnedAllocatedBytesPerFrameP95,
            candidate.Scroll.RegressionRendererOwnedAllocatedBytesPerFrameP95,
            reference.Scroll.Trials.Select(
                static trial => trial.RendererOwnedAllocatedBytesPerFrameP95),
            candidate.Scroll.Trials.Select(
                static trial => trial.RendererOwnedAllocatedBytesPerFrameP95),
            AllocationBudgetPercent,
            PerformanceMeasurementContract.RendererOwnedAllocationNoiseFloorBytes,
            PerformanceMeasurementContract.AllocationDispersionBudgetPercent));

        int latencyCount = comparisons.Count(item => item.Kind == "latency");
        int allocationCount = comparisons.Count(item => item.Kind == "allocation");
        bool complete = latencyCount == 11 && allocationCount == 1;
        return new RegressionResult
        {
            Mode = "candidate",
            ReferencePath = options.ReferencePath,
            ReferenceReportSha256 = referenceReportSha256,
            ReferenceBuildIdentity = reference.BuildIdentity,
            ReferenceEligible = referenceEligible,
            MachineComparable = machineComparable,
            ComparedLatencyMetrics = latencyCount,
            ComparedAllocationMetrics = allocationCount,
            Comparisons = comparisons,
            Passed = referenceEligible &&
                     candidateEvidenceComplete &&
                     machineComparable &&
                     complete &&
                     comparisons.All(static item => item.Passed),
        };
    }

    private static void AddLatency(
        ICollection<RegressionComparison> comparisons,
        string name,
        double reference,
        double candidate,
        IEnumerable<double> referenceTrials,
        IEnumerable<double> candidateTrials,
        double absoluteNoiseFloor,
        double stabilityBudgetPercent)
        => comparisons.Add(Compare(
            name,
            "latency",
            reference,
            candidate,
            referenceTrials,
            candidateTrials,
            LatencyBudgetPercent,
            absoluteNoiseFloor,
            stabilityBudgetPercent));

    private static RegressionComparison Compare(
        string metric,
        string kind,
        double reference,
        double candidate,
        IEnumerable<double> referenceTrials,
        IEnumerable<double> candidateTrials,
        double budgetPercent,
        double absoluteNoiseFloor,
        double stabilityBudgetPercent,
        bool referenceInternallyStationary = true,
        bool candidateInternallyStationary = true)
    {
        double absoluteDelta = candidate - reference;
        double deltaPercent = PerformanceStatistics.DeltaPercent(candidate, reference);
        double relativeAllowance = Math.Abs(reference) * budgetPercent / 100;
        double allowedAbsoluteDelta = Math.Max(relativeAllowance, absoluteNoiseFloor);
        double referenceDispersion =
            PerformanceStatistics.RobustDispersionPercent(referenceTrials);
        double candidateDispersion =
            PerformanceStatistics.RobustDispersionPercent(candidateTrials);
        bool valid = double.IsFinite(reference) &&
                     reference >= 0 &&
                     double.IsFinite(candidate) &&
                     candidate >= 0 &&
                     double.IsFinite(deltaPercent) &&
                     double.IsFinite(relativeAllowance) &&
                     double.IsFinite(absoluteNoiseFloor) &&
                     absoluteNoiseFloor >= 0 &&
                     double.IsFinite(allowedAbsoluteDelta) &&
                     double.IsFinite(referenceDispersion) &&
                     double.IsFinite(candidateDispersion) &&
                     double.IsFinite(stabilityBudgetPercent) &&
                     stabilityBudgetPercent >= 0;
        bool stable = valid &&
                      referenceInternallyStationary &&
                      candidateInternallyStationary &&
                      referenceDispersion <= stabilityBudgetPercent &&
                      candidateDispersion <= stabilityBudgetPercent;
        bool withinAllowance = stable &&
            (absoluteDelta <= allowedAbsoluteDelta ||
             NearlyEqual(absoluteDelta, allowedAbsoluteDelta));
        return new RegressionComparison
        {
            Metric = metric,
            Kind = kind,
            Reference = reference,
            Candidate = candidate,
            AbsoluteDelta = absoluteDelta,
            DeltaPercent = deltaPercent,
            BudgetPercent = budgetPercent,
            RelativeAllowance = relativeAllowance,
            AbsoluteNoiseFloor = absoluteNoiseFloor,
            AllowedAbsoluteDelta = allowedAbsoluteDelta,
            ReferenceDispersionPercent = referenceDispersion,
            CandidateDispersionPercent = candidateDispersion,
            StabilityBudgetPercent = stabilityBudgetPercent,
            Decision = !valid || !stable
                ? "inconclusive"
                : withinAllowance ? "pass" : "regression",
            Passed = withinAllowance,
        };
    }

    private static bool NearlyEqual(double left, double right)
    {
        if (!double.IsFinite(left) || !double.IsFinite(right))
            return false;
        double scale = Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));
        return Math.Abs(left - right) <= scale * 1e-12;
    }
}
