using System.Text.Json;
using System.Text.Json.Nodes;
using MarkdownRenderer.PerformanceHarness.Core.Statistics;

namespace MarkdownRenderer.Tests;

/// <summary>
/// Gate fixture mutations of the schema-10 first-viewport repeatability
/// protocol: the interleaved schedule, trial timestamps, cache and GC
/// counters, measurement-thread pinning and the stationarity statistics.
/// Several mutations rewrite whole trial populations so that every derived
/// statistic stays consistent and only the protocol property under test fails.
/// </summary>
internal static class PerformanceGateFirstViewportScheduleMutations
{
    private const string Prefix = "schema10-first-viewport-";

    internal static PerformanceGateReportMutations Mutations { get; } = new()
    {
        Shared = new Dictionary<string, Action<JsonObject>>
        {
            [Prefix + "scenario-order"] = static report =>
            {
                JsonArray viewports = Viewports(report);
                JsonNode first = viewports[0]!.DeepClone();
                viewports[0] = viewports[1]!.DeepClone();
                viewports[1] = first;
            },
            [Prefix + "warmup-count"] = static report =>
                Viewport(report)["warmupTrials"]!.AsArray().RemoveAt(2),
            [Prefix + "warmup-global-ordinal"] = static report =>
            {
                JsonObject warmup = Viewport(report)["warmupTrials"]![0]!.AsObject();
                warmup["globalOrdinal"] = warmup["globalOrdinal"]!.GetValue<int>() + 1;
            },
            [Prefix + "global-ordinal"] = static report =>
            {
                JsonObject trial = Trial(report);
                trial["globalOrdinal"] = trial["globalOrdinal"]!.GetValue<int>() + 1;
            },
            [Prefix + "schedule-position"] = static report =>
            {
                JsonObject trial = Trial(report);
                trial["schedulePosition"] = (trial["schedulePosition"]!.GetValue<int>() + 1) % 6;
            },
            [Prefix + "timestamp"] = static report =>
            {
                JsonObject trial = Trial(report);
                trial["completedUtc"] =
                    PerformanceGateJson.ParseTimestamp(trial["startedUtc"]!).AddMilliseconds(-1);
            },
            [Prefix + "timestamp-overlap"] = static report =>
            {
                JsonObject trial = Trial(report);
                trial["startedUtc"] = DateTimeOffset.UnixEpoch;
                trial["completedUtc"] = DateTimeOffset.UnixEpoch.AddMilliseconds(1);
            },
            [Prefix + "timestamp-equal"] = SetAllTrialTimestampsEqual,
            [Prefix + "timestamp-non-utc"] = static report =>
            {
                JsonObject trial = Trial(report);
                string started = PerformanceGateJson.ToNonUtcTimestamp(trial["startedUtc"]!);
                string completed = PerformanceGateJson.ToNonUtcTimestamp(trial["completedUtc"]!);
                trial["startedUtc"] = started;
                trial["completedUtc"] = completed;
            },
            [Prefix + "timestamp-outside-report"] = static report =>
            {
                JsonObject trial = Trial(report);
                DateTimeOffset reportCompleted = PerformanceGateJson.ParseTimestamp(report["completedUtc"]!);
                trial["startedUtc"] = reportCompleted.AddMilliseconds(1);
                trial["completedUtc"] = reportCompleted.AddMilliseconds(2);
            },
            [Prefix + "first-start-equals-report"] = static report =>
                FindTrialByGlobalOrdinal(Viewports(report), 0)["startedUtc"] =
                    report["startedUtc"]!.DeepClone(),
            [Prefix + "adjacent-timestamps-equal"] = static report =>
            {
                JsonArray viewports = Viewports(report);
                JsonObject precedingTrial = FindTrialByGlobalOrdinal(viewports, 0);
                FindTrialByGlobalOrdinal(viewports, 1)["startedUtc"] =
                    precedingTrial["completedUtc"]!.DeepClone();
            },
            [Prefix + "last-completion-equals-report"] = static report =>
                FindTrialByGlobalOrdinal(Viewports(report), 53)["completedUtc"] =
                    report["completedUtc"]!.DeepClone(),
            [Prefix + "settling"] = static report => Trial(report)["settlingElapsedMilliseconds"] = -1,
            [Prefix + "cache-counter"] = static report => Trial(report)["sourceKeyHashCountAfterTrial"] = 100,
            [Prefix + "cache-proof"] = static report => Trial(report)["cacheProofPassed"] = false,
            [Prefix + "allocation"] = static report => Trial(report)["processAllocatedBytes"] = -1,
            [Prefix + "gc-hierarchy"] = static report =>
            {
                JsonObject trial = Trial(report);
                trial["gen0Collections"] = 0;
                trial["gen1Collections"] = 1;
                trial["gen2Collections"] = 1;
            },
            [Prefix + "pin"] = static report => Viewport(report)["measurementThreadPriority"] = "Highest",
            [Prefix + "pin-consistency"] = static report =>
            {
                JsonNode second = Viewports(report)[1]!;
                second["measurementThreadAffinityMask"] = "0x0000000000000002";
                second["measurementProcessorNumber"] = 1;
            },
            [Prefix + "processor-group"] = static report =>
                Viewport(report)["measurementProcessorGroup"] = int.MaxValue,
            [Prefix + "processor-number"] = static report =>
                Viewport(report)["measurementProcessorNumber"] = 1,
            [Prefix + "stationarity-policy"] = static report =>
                Viewport(report)["stationarityPolicy"] = "wrong-policy",
            [Prefix + "dispersion"] = static report =>
                PerformanceGateJson.AddOne(Viewport(report), "regressionDispersionPercent"),
            [Prefix + "slope"] = static report =>
                PerformanceGateJson.AddOne(Viewport(report), "theilSenSlopeMillisecondsPerGlobalOrdinal"),
            [Prefix + "projected-drift"] = static report =>
                PerformanceGateJson.AddOne(Viewport(report), "projectedDriftMilliseconds"),
            [Prefix + "boundary"] = static report =>
                PerformanceGateJson.AddOne(Viewport(report), "warmupBoundaryShiftMilliseconds"),
            [Prefix + "allowance"] = static report =>
                PerformanceGateJson.AddOne(Viewport(report), "stationarityAllowanceMilliseconds"),
            [Prefix + "stationarity-evaluated"] = static report =>
                Viewport(report)["stationarityEvaluated"] = false,
            [Prefix + "stationarity-passed"] = static report =>
                Viewport(report)["stationarityPassed"] = false,
            [Prefix + "order-drift"] = static report =>
                ApplyOrderDrift(Viewports(report)[1]!.AsObject()),
            [Prefix + "catastrophic-trial"] = static report =>
                ApplyCatastrophicTrial(report, Viewport(report)),
            [Prefix + "boundary-endpoint"] = static report => ApplyBoundaryEndpoint(Viewport(report)),
        },
    };

    private static JsonArray Viewports(JsonObject report)
        => report["firstUsableViewport"]!.AsArray();

    private static JsonObject Viewport(JsonObject report)
        => Viewports(report)[0]!.AsObject();

    private static JsonObject Trial(JsonObject report)
        => Viewport(report)["trials"]![0]!.AsObject();

    private static JsonObject FindTrialByGlobalOrdinal(
        JsonArray viewports,
        int globalOrdinal)
        => viewports
            .SelectMany(static viewport => viewport!["warmupTrials"]!.AsArray()
                .Concat(viewport["trials"]!.AsArray()))
            .Select(static trial => trial!.AsObject())
            .Single(trial => trial["globalOrdinal"]!.GetValue<int>() == globalOrdinal);

    private static void SetAllTrialTimestampsEqual(JsonObject report)
    {
        DateTimeOffset sharedTimestamp =
            PerformanceGateJson.ParseTimestamp(report["startedUtc"]!).AddMilliseconds(100);
        foreach (JsonObject scenario in Viewports(report).Select(static node => node!.AsObject()))
        {
            foreach (JsonObject currentTrial in scenario["warmupTrials"]!.AsArray()
                         .Concat(scenario["trials"]!.AsArray())
                         .Select(static node => node!.AsObject()))
            {
                currentTrial["startedUtc"] = sharedTimestamp;
                currentTrial["completedUtc"] = sharedTimestamp;
            }
        }
    }

    // Reorder the measured trial p95 values to rise with the global ordinal,
    // then rebuild every derived statistic so only the drift check fails.
    private static void ApplyOrderDrift(JsonObject viewport)
    {
        JsonArray trials = viewport["trials"]!.AsArray();
        double[] orderedP95 = trials
            .Select(static trial => trial!["p95Milliseconds"]!.GetValue<double>())
            .Order()
            .ToArray();
        var pooled = new List<double>(600);
        var observations = new List<(int GlobalOrdinal, double Value)>(6);
        for (int ordinal = 0; ordinal < trials.Count; ordinal++)
        {
            JsonObject trial = trials[ordinal]!.AsObject();
            double value = orderedP95[ordinal];
            double[] samples = Enumerable.Repeat(value, 100).ToArray();
            trial["samplesMilliseconds"] = JsonSerializer.SerializeToNode(samples);
            trial["p95Milliseconds"] = value;
            pooled.AddRange(samples);
            observations.Add((trial["globalOrdinal"]!.GetValue<int>(), value));
        }

        viewport["samplesMilliseconds"] = JsonSerializer.SerializeToNode(pooled);
        viewport["p95Milliseconds"] = PerformanceStatistics.Percentile(pooled, 0.95);
        viewport["regressionP95Milliseconds"] =
            PerformanceStatistics.HodgesLehmann(orderedP95);
        viewport["regressionDispersionPercent"] =
            PerformanceStatistics.RobustDispersionPercent(orderedP95);
        viewport["theilSenSlopeMillisecondsPerGlobalOrdinal"] =
            PerformanceStatistics.TheilSenSlope(observations);
        viewport["projectedDriftMilliseconds"] =
            PerformanceStatistics.ProjectedTheilSenDrift(observations);
    }

    // Give one measured trial a catastrophic tail while every derived
    // statistic and the comparison stay consistent and passing, so the gate
    // must reject the trial itself.
    private static void ApplyCatastrophicTrial(
        JsonObject report,
        JsonObject viewport)
    {
        JsonArray warmups = viewport["warmupTrials"]!.AsArray();
        foreach (JsonObject warmup in warmups.Select(static node => node!.AsObject()))
        {
            double[] samples = Enumerable.Repeat(1d, 100).ToArray();
            warmup["samplesMilliseconds"] = JsonSerializer.SerializeToNode(samples);
            warmup["p95Milliseconds"] = 1d;
        }

        JsonArray trials = viewport["trials"]!.AsArray();
        var pooled = new List<double>(600);
        var observations = new List<(int GlobalOrdinal, double Value)>(6);
        for (int ordinal = 0; ordinal < trials.Count; ordinal++)
        {
            JsonObject trial = trials[ordinal]!.AsObject();
            double[] samples = ordinal == 2
                ? Enumerable.Repeat(1d, 94).Concat(Enumerable.Repeat(1_000d, 6)).ToArray()
                : Enumerable.Repeat(1d, 100).ToArray();
            double p95 = PerformanceStatistics.Percentile(samples, 0.95);
            trial["samplesMilliseconds"] = JsonSerializer.SerializeToNode(samples);
            trial["p95Milliseconds"] = p95;
            pooled.AddRange(samples);
            observations.Add((trial["globalOrdinal"]!.GetValue<int>(), p95));
        }

        double[] trialP95 = observations.Select(static item => item.Value).ToArray();
        double regressionP95 = PerformanceStatistics.HodgesLehmann(trialP95);
        viewport["samplesMilliseconds"] = JsonSerializer.SerializeToNode(pooled);
        viewport["p95Milliseconds"] = PerformanceStatistics.Percentile(pooled, 0.95);
        viewport["regressionP95Milliseconds"] = regressionP95;
        viewport["regressionDispersionPercent"] =
            PerformanceStatistics.RobustDispersionPercent(trialP95);
        viewport["theilSenSlopeMillisecondsPerGlobalOrdinal"] =
            PerformanceStatistics.TheilSenSlope(observations);
        viewport["projectedDriftMilliseconds"] =
            PerformanceStatistics.ProjectedTheilSenDrift(observations);
        viewport["warmupBoundaryShiftMilliseconds"] = 0d;
        viewport["stationarityAllowanceMilliseconds"] = 5d;
        viewport["stationarityEvaluated"] = true;
        viewport["stationarityPassed"] = true;
        viewport["passed"] = true;

        JsonObject comparison = report["regression"]!["comparisons"]!.AsArray()
            .Select(static node => node!.AsObject())
            .Single(static item => item["metric"]!.GetValue<string>() ==
                "firstViewport.cache-disabled.102400.p95Ms.hodgesLehmann");
        double reference = comparison["reference"]!.GetValue<double>();
        comparison["candidate"] = regressionP95;
        comparison["absoluteDelta"] = regressionP95 - reference;
        comparison["deltaPercent"] = ((regressionP95 / reference) - 1d) * 100d;
        comparison["candidateDispersionPercent"] = 0d;
        comparison["decision"] = "pass";
        comparison["passed"] = true;
    }

    // Move the last two warm-up trials just outside the stationarity
    // allowance on either side of the measured center. Their own center still
    // matches, so the recorded boundary shift of zero hides endpoints the gate
    // must reject.
    private static void ApplyBoundaryEndpoint(JsonObject viewport)
    {
        JsonArray measuredTrials = viewport["trials"]!.AsArray();
        double measuredCenter = PerformanceStatistics.TwoValueCenter(
            measuredTrials[0]!["p95Milliseconds"]!.GetValue<double>(),
            measuredTrials[1]!["p95Milliseconds"]!.GetValue<double>());
        double allowance = viewport["stationarityAllowanceMilliseconds"]!.GetValue<double>();
        double lower = measuredCenter - allowance - 1d;
        double upper = measuredCenter + allowance + 1d;
        JsonArray warmups = viewport["warmupTrials"]!.AsArray();
        foreach ((int index, double value) in new[] { (1, lower), (2, upper) })
        {
            JsonObject warmup = warmups[index]!.AsObject();
            warmup["samplesMilliseconds"] = JsonSerializer.SerializeToNode(
                Enumerable.Repeat(value, 100).ToArray());
            warmup["p95Milliseconds"] = value;
        }
        viewport["warmupBoundaryShiftMilliseconds"] = 0d;
    }
}
