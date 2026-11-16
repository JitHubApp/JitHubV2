using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Statistics;
using MarkdownRenderer.PerformanceHarness.Probes;

using static MarkdownRenderer.PerformanceHarness.Checks.EvidenceNumbers;

namespace MarkdownRenderer.PerformanceHarness.Checks;

/// <summary>
/// The harness's self-check of its own regression verdict before a release
/// report is written. A baseline run must record a passing baseline; a
/// candidate run must compare exactly the frozen metrics against the bound
/// reference report and every comparison must recompute to the recorded
/// decision.
/// </summary>
internal static class RegressionResultCheck
{
    internal static bool IsCompleteAndPassing(
        PerformanceReport report,
        PerformanceOptions options)
    {
        RegressionResult regression = report.Regression;
        if (options.EstablishBaseline)
            return IsPassingBaseline(regression);

        if (options.Quick)
            return false;

        Dictionary<string, ExpectedComparison> expectedCandidates =
            CreateExpectedComparisons(report);
        bool comparisonsMatch = regression.Comparisons.Count == expectedCandidates.Count &&
            regression.Comparisons.Select(static comparison => comparison.Metric).Distinct().Count() ==
                regression.Comparisons.Count &&
            regression.Comparisons.All(comparison =>
                expectedCandidates.TryGetValue(comparison.Metric, out ExpectedComparison expected) &&
                MatchesExpected(comparison, expected));
        bool referenceBound = options.ReferencePath is not null &&
            string.Equals(regression.ReferencePath, options.ReferencePath, StringComparison.Ordinal) &&
            IsSha256(regression.ReferenceReportSha256) &&
            string.Equals(
                regression.ReferenceReportSha256,
                PerformanceEvidenceHashing.ComputeFileSha256(options.ReferencePath),
                StringComparison.OrdinalIgnoreCase);
        return regression.Mode == "candidate" &&
               regression.DecisionPolicy ==
                   PerformanceMeasurementContract.RegressionDecisionPolicy &&
               regression.LatencyRegressionBudgetPercent == 5 &&
               regression.AllocationRegressionBudgetPercent == 2 &&
               referenceBound &&
               regression.ReferenceEligible &&
               regression.MachineComparable &&
               regression.ComparedLatencyMetrics == 11 &&
               regression.ComparedAllocationMetrics == 1 &&
               comparisonsMatch &&
               regression.Comparisons.All(IsFinitePassingComparison) &&
               regression.Passed;
    }

    private static bool IsPassingBaseline(RegressionResult regression)
        => regression.Mode == "baseline" &&
           regression.DecisionPolicy ==
               PerformanceMeasurementContract.RegressionDecisionPolicy &&
           regression.LatencyRegressionBudgetPercent == 5 &&
           regression.AllocationRegressionBudgetPercent == 2 &&
           string.IsNullOrEmpty(regression.ReferencePath) &&
           string.IsNullOrEmpty(regression.ReferenceReportSha256) &&
           regression.ReferenceEligible &&
           regression.MachineComparable &&
           regression.Passed;

    private static Dictionary<string, ExpectedComparison> CreateExpectedComparisons(
        PerformanceReport report)
    {
        var expectedCandidates = new Dictionary<string, ExpectedComparison>(
            StringComparer.Ordinal);
        foreach (FirstViewportResult viewport in report.FirstUsableViewport)
        {
            expectedCandidates.Add(
                $"firstViewport.{viewport.Mode}.{viewport.SourceUtf16Bytes}.p95Ms.hodgesLehmann",
                new ExpectedComparison(
                    viewport.RegressionP95Milliseconds,
                    PerformanceMeasurementContract.GetFirstViewportNoiseFloorMilliseconds(
                        viewport.Mode,
                        viewport.SourceUtf16Bytes),
                    PerformanceMeasurementContract.FirstViewportDispersionBudgetPercent,
                    "latency",
                    viewport.StationarityEvaluated && viewport.StationarityPassed));
        }
        expectedCandidates.Add(
            "scroll.uiThreadWorkP95Ms.hodgesLehmann",
            new ExpectedComparison(
                report.Scroll.RegressionUiThreadWorkP95Milliseconds,
                PerformanceMeasurementContract.ScrollUiThreadWorkP95NoiseFloorMilliseconds,
                PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent,
                "latency",
                true));
        expectedCandidates.Add(
            "scroll.uiThreadWorkP99Ms.hodgesLehmann",
            new ExpectedComparison(
                report.Scroll.RegressionUiThreadWorkP99Milliseconds,
                PerformanceMeasurementContract.ScrollUiThreadWorkP99NoiseFloorMilliseconds,
                PerformanceMeasurementContract.ScrollUiWorkDispersionBudgetPercent,
                "latency",
                true));
        expectedCandidates.Add(
            "scroll.frameTimeP95Ms.hodgesLehmann",
            new ExpectedComparison(
                report.Scroll.RegressionFrameTimeP95Milliseconds,
                PerformanceMeasurementContract.ScrollFrameTimeP95NoiseFloorMilliseconds,
                PerformanceMeasurementContract.ScrollFrameTimeDispersionBudgetPercent,
                "latency",
                true));
        expectedCandidates.Add(
            "sourceLookup.regressionP95Ns.hodgesLehmann",
            new ExpectedComparison(
                report.SourceLookup.RegressionP95Nanoseconds,
                PerformanceMeasurementContract.SourceLookupNoiseFloorNanoseconds,
                PerformanceMeasurementContract.SourceLookupDispersionBudgetPercent,
                "latency",
                true));
        expectedCandidates.Add(
            "cancellation.p95Ms.hodgesLehmann",
            new ExpectedComparison(
                report.Cancellation.RegressionP95Milliseconds,
                PerformanceMeasurementContract.CancellationNoiseFloorMilliseconds,
                PerformanceMeasurementContract.CancellationDispersionBudgetPercent,
                "latency",
                true));
        expectedCandidates.Add(
            "scroll.rendererOwnedAllocatedBytesPerFrameP95.hodgesLehmann",
            new ExpectedComparison(
                report.Scroll.RegressionRendererOwnedAllocatedBytesPerFrameP95,
                PerformanceMeasurementContract.RendererOwnedAllocationNoiseFloorBytes,
                PerformanceMeasurementContract.AllocationDispersionBudgetPercent,
                "allocation",
                true));
        return expectedCandidates;
    }

    /// <summary>
    /// Recomputes one comparison from its reference and the expected
    /// candidate, and checks every recorded field and the decision.
    /// </summary>
    private static bool MatchesExpected(
        RegressionComparison comparison,
        ExpectedComparison expected)
    {
        double expectedBudget = expected.Kind == "allocation" ? 2 : 5;
        double expectedAbsoluteDelta = comparison.Candidate - comparison.Reference;
        double expectedRelativeAllowance =
            Math.Abs(comparison.Reference) * expectedBudget / 100;
        double expectedAllowedDelta = Math.Max(
            expectedRelativeAllowance,
            expected.NoiseFloor);
        bool expectedPass =
            expected.InternallyStationary &&
            comparison.ReferenceDispersionPercent <= expected.StabilityBudget &&
            comparison.CandidateDispersionPercent <= expected.StabilityBudget &&
            (expectedAbsoluteDelta <= expectedAllowedDelta ||
             NearlyEqual(expectedAbsoluteDelta, expectedAllowedDelta));
        return comparison.Kind == expected.Kind &&
               NearlyEqual(comparison.Candidate, expected.Candidate) &&
               comparison.BudgetPercent == expectedBudget &&
               NearlyEqual(comparison.AbsoluteDelta, expectedAbsoluteDelta) &&
               NearlyEqual(
                   comparison.DeltaPercent,
                   PerformanceStatistics.DeltaPercent(
                       comparison.Candidate,
                       comparison.Reference)) &&
               NearlyEqual(comparison.RelativeAllowance, expectedRelativeAllowance) &&
               comparison.AbsoluteNoiseFloor == expected.NoiseFloor &&
               NearlyEqual(comparison.AllowedAbsoluteDelta, expectedAllowedDelta) &&
               comparison.StabilityBudgetPercent == expected.StabilityBudget &&
                comparison.Passed == expectedPass &&
                comparison.Decision == (expectedPass ? "pass" :
                    !expected.InternallyStationary ||
                    comparison.ReferenceDispersionPercent > expected.StabilityBudget ||
                    comparison.CandidateDispersionPercent > expected.StabilityBudget
                       ? "inconclusive"
                       : "regression");
    }

    private static bool IsFinitePassingComparison(RegressionComparison comparison)
        => !string.IsNullOrWhiteSpace(comparison.Metric) &&
           (comparison.Kind == "latency" || comparison.Kind == "allocation") &&
           double.IsFinite(comparison.Reference) &&
           double.IsFinite(comparison.Candidate) &&
           double.IsFinite(comparison.AbsoluteDelta) &&
           double.IsFinite(comparison.DeltaPercent) &&
           double.IsFinite(comparison.BudgetPercent) &&
           double.IsFinite(comparison.RelativeAllowance) &&
           double.IsFinite(comparison.AbsoluteNoiseFloor) &&
           double.IsFinite(comparison.AllowedAbsoluteDelta) &&
           double.IsFinite(comparison.ReferenceDispersionPercent) &&
           double.IsFinite(comparison.CandidateDispersionPercent) &&
           double.IsFinite(comparison.StabilityBudgetPercent) &&
           comparison.Decision == "pass" &&
           comparison.Passed;

    private readonly record struct ExpectedComparison(
        double Candidate,
        double NoiseFloor,
        double StabilityBudget,
        string Kind,
        bool InternallyStationary);
}
