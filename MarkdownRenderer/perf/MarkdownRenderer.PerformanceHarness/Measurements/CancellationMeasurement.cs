using MarkdownRenderer.PerformanceHarness.Core.Configuration;
using MarkdownRenderer.PerformanceHarness.Core.Events;
using MarkdownRenderer.PerformanceHarness.Core.Reports;
using MarkdownRenderer.PerformanceHarness.Core.Scenarios;
using MarkdownRenderer.PerformanceHarness.Core.Statistics;
using MarkdownRenderer.PerformanceHarness.Hosting;

namespace MarkdownRenderer.PerformanceHarness.Measurements;

/// <summary>
/// Measures how quickly a superseded 10 MiB parse is cancelled when a small
/// winner source replaces it, and counts stale commits from the superseded
/// generation, over the planned warmup and recorded iterations of each trial.
/// </summary>
internal sealed class CancellationMeasurement
{
    private const int SupersededBytes = 10 * 1024 * 1024;

    private readonly PerformanceOptions _options;
    private readonly PerformanceReport _report;
    private readonly PerformanceEventListener _events;
    private readonly MeasurementRendererHost _rendererHost;

    internal CancellationMeasurement(
        PerformanceOptions options,
        PerformanceReport report,
        PerformanceEventListener events,
        MeasurementRendererHost rendererHost)
    {
        _options = options;
        _report = report;
        _events = events;
        _rendererHost = rendererHost;
    }

    internal async Task<CancellationResult> MeasureAsync()
    {
        int requiredTrials = _report.SampleRequirements.CancellationTrialsRequired;
        int totalRequestedIterations = checked(
            _options.CancellationIterations * requiredTrials);
        var totals = new RunTotals(totalRequestedIterations, requiredTrials);
        var trials = new List<CancellationTrialResult>(requiredTrials);

        for (int trialOrdinal = 0; trialOrdinal < requiredTrials; trialOrdinal++)
            trials.Add(await RunTrialAsync(trialOrdinal, totals));

        return CreateResult(
            requiredTrials,
            totalRequestedIterations,
            trials,
            totals);
    }

    private async Task<CancellationTrialResult> RunTrialAsync(int trialOrdinal, RunTotals totals)
    {
        await _rendererHost.ForceManagedCollectionAsync();
        IReadOnlyList<CancellationMeasurementIteration> plan =
            CancellationMeasurementPlan.CreateTrial(
                trialOrdinal,
                _options.CancellationIterations);
        var trialSamples = new List<double>(_options.CancellationIterations);
        double trialWarmupElapsedMilliseconds = double.NaN;
        int trialWarmupStaleCommits = 0;
        int trialStaleCommits = 0;

        foreach (CancellationMeasurementIteration iteration in plan)
        {
            (double elapsedMilliseconds, int iterationStaleCommits) =
                await RunIterationAsync(iteration);
            if (!iteration.IsRecorded)
            {
                totals.WarmupIterations++;
                trialWarmupElapsedMilliseconds = elapsedMilliseconds;
                trialWarmupStaleCommits += iterationStaleCommits;
                totals.WarmupSamples.Add(elapsedMilliseconds);
                totals.WarmupStaleCommits += iterationStaleCommits;
                continue;
            }

            trialSamples.Add(elapsedMilliseconds);
            totals.Samples.Add(elapsedMilliseconds);
            trialStaleCommits += iterationStaleCommits;
            totals.StaleCommits += iterationStaleCommits;
        }

        double trialP95 = PerformanceStatistics.Percentile(trialSamples, 0.95);
        return new CancellationTrialResult
        {
            Ordinal = trialOrdinal,
            WarmupSourceSeed = plan[0].SourceSeed,
            WarmupElapsedMilliseconds = trialWarmupElapsedMilliseconds,
            WarmupStaleCommits = trialWarmupStaleCommits,
            FirstRecordedSourceSeed = plan[1].SourceSeed,
            RequestedIterations = _options.CancellationIterations,
            ObservedCancellations = trialSamples.Count,
            SamplesMilliseconds = trialSamples,
            P95Milliseconds = trialP95,
            StaleCommits = trialStaleCommits,
            Complete = double.IsFinite(trialWarmupElapsedMilliseconds) &&
                       trialWarmupElapsedMilliseconds >= 0 &&
                       trialWarmupStaleCommits >= 0 &&
                       trialSamples.Count == _options.CancellationIterations &&
                       trialSamples.All(
                            static value => double.IsFinite(value) && value >= 0) &&
                       double.IsFinite(trialP95) &&
                       trialP95 >= 0 &&
                       trialStaleCommits >= 0,
        };
    }

    private async Task<(double ElapsedMilliseconds, int StaleCommits)> RunIterationAsync(
        CancellationMeasurementIteration iteration)
    {
        MarkdownEngine engine = PerformanceEngineFactory.Create(SupersededBytes);
        await _rendererHost.ReplaceRendererAsync(engine);
        string superseded = PerformanceDocumentFactory.Create(
            SupersededBytes,
            iteration.SourceSeed);
        long sequence = _events.CurrentSequence;
        _rendererHost.Renderer!.Markdown = superseded;
        PipelineEvent oldPipeline = await _events.WaitForPipelineAsync(
            sequence,
            SupersededBytes,
            MeasurementRendererHost.ViewportTimeout);

        string winner = $"# cancellation winner {iteration.WinnerOrdinal}\n";
        long winnerBytes = (long)winner.Length * sizeof(char);
        long winnerStartSequence = _events.CurrentSequence;
        _rendererHost.Renderer!.Markdown = winner;
        SupersessionEvent supersession = await _events.WaitForSupersessionAsync(
            winnerStartSequence,
            oldPipeline.Generation,
            MeasurementRendererHost.ViewportTimeout);
        _ = await _events.WaitForPipelineAsync(
            winnerStartSequence,
            winnerBytes,
            MeasurementRendererHost.ViewportTimeout);
        CancellationEvent cancellation = await _events.WaitForCancellationAsync(
            supersession.Sequence,
            oldPipeline.Generation,
            MeasurementRendererHost.ViewportTimeout);
        _ = await _events.WaitForFirstViewportAsync(
            winnerStartSequence,
            winnerBytes,
            MeasurementRendererHost.ViewportTimeout);

        await _rendererHost.WaitUntilAsync(
            () => engine.ActiveParseCount == 0,
            MeasurementRendererHost.ViewportTimeout,
            "abandoned parser worker to retire");
        await _rendererHost.WaitForCompositionFramesAsync(2);
        int staleCommits = _events.FirstViewports.Count(item =>
            item.Generation == oldPipeline.Generation &&
            item.Sequence > winnerStartSequence);
        await _rendererHost.DisposeRendererAsync();
        return (cancellation.ElapsedMilliseconds, staleCommits);
    }

    private CancellationResult CreateResult(
        int requiredTrials,
        int totalRequestedIterations,
        List<CancellationTrialResult> trials,
        RunTotals totals)
    {
        List<double> samples = totals.Samples;
        List<double> warmupSamples = totals.WarmupSamples;
        int warmupIterations = totals.WarmupIterations;
        int warmupStaleCommits = totals.WarmupStaleCommits;
        int staleCommits = totals.StaleCommits;
        double p95 = PerformanceStatistics.Percentile(samples, 0.95);
        double regressionP95 = PerformanceStatistics.HodgesLehmann(
            trials.Select(static trial => trial.P95Milliseconds));
        double regressionDispersion = PerformanceStatistics.RobustDispersionPercent(
            trials.Select(static trial => trial.P95Milliseconds));
        double warmupP95 = PerformanceStatistics.Percentile(warmupSamples, 0.95);
        return new CancellationResult
        {
            RequestedIterations = totalRequestedIterations,
            IterationsPerTrial = _options.CancellationIterations,
            WarmupIterations = warmupIterations,
            WarmupElapsedMilliseconds = warmupP95,
            WarmupStaleCommits = warmupStaleCommits,
            ObservedCancellations = samples.Count,
            TrialStartPolicy = PerformanceMeasurementContract.CancellationTrialStartPolicy,
            RegressionEstimator = _options.Quick
                ? "quick-single-trial-non-gating"
                : PerformanceMeasurementContract.CancellationRegressionEstimator,
            Trials = trials,
            SamplesMilliseconds = samples,
            P95Milliseconds = p95,
            RegressionP95Milliseconds = regressionP95,
            StaleCommits = staleCommits,
            Passed = warmupIterations == requiredTrials &&
                     warmupSamples.Count == requiredTrials &&
                      warmupStaleCommits == 0 &&
                      trials.Count == requiredTrials &&
                      trials.All(static trial =>
                          trial.Complete &&
                          CancellationMeasurementPlan.IsWarmupEvidencePassing(
                              CancellationMeasurementPlan.WarmupIterationsPerTrial,
                              trial.WarmupElapsedMilliseconds,
                              trial.WarmupStaleCommits) &&
                          trial.P95Milliseconds <=
                              CancellationMeasurementPlan.AbsoluteBudgetMilliseconds &&
                          trial.StaleCommits == 0) &&
                     samples.Count == totalRequestedIterations &&
                     p95 <= CancellationMeasurementPlan.AbsoluteBudgetMilliseconds &&
                     double.IsFinite(regressionP95) &&
                     double.IsFinite(regressionDispersion) &&
                     regressionDispersion <=
                         PerformanceMeasurementContract.CancellationDispersionBudgetPercent &&
                     staleCommits == 0,
        };
    }

    /// <summary>
    /// The pooled samples and counters of a run, accumulated per iteration
    /// across trials.
    /// </summary>
    private sealed class RunTotals(int totalRequestedIterations, int requiredTrials)
    {
        internal List<double> Samples { get; } = new(totalRequestedIterations);
        internal List<double> WarmupSamples { get; } = new(requiredTrials);
        internal int WarmupIterations { get; set; }
        internal int WarmupStaleCommits { get; set; }
        internal int StaleCommits { get; set; }
    }
}
