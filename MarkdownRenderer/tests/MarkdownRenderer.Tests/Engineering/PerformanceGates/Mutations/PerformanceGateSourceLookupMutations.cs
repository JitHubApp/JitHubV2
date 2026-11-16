using System.Diagnostics;
using System.Text.Json.Nodes;

namespace MarkdownRenderer.Tests.Engineering.PerformanceGates.Mutations;

/// <summary>
/// Gate fixture mutations of the source-lookup evidence: required populations,
/// protocol fields, per-trial and aggregate statistics, measurement-thread
/// pinning and the optional per-trial diagnostic telemetry.
/// </summary>
internal static class PerformanceGateSourceLookupMutations
{
    private const string DiagnosticPrefix = "source-lookup-diagnostic-";

    internal static PerformanceGateReportMutations Mutations { get; } = new()
    {
        Shared = CreateShared(),
        CandidateOnly = new Dictionary<string, Action<JsonObject>>
        {
            ["lookup-required-observations"] = static report =>
                report["sampleRequirements"]!["sourceLookupRegressionObservationCountRequired"] = 127,
            ["lookup-required-operations"] = static report =>
                report["sampleRequirements"]!["sourceLookupRegressionOperationCountRequired"] = 524_287,
            ["lookup-top-observations"] = static report => Lookup(report)["regressionObservationCount"] = 127,
            ["lookup-top-operations"] = static report => Lookup(report)["regressionOperationCount"] = 524_287,
            ["lookup-declared-trial-count"] = static report => Lookup(report)["regressionTrialCount"] = 4,
            ["lookup-trial-list-count"] = static report =>
                Lookup(report)["regressionTrials"]!.AsArray().RemoveAt(4),
            ["lookup-trial-ordinal"] = static report => Trial(report)["ordinal"] = 1,
            ["lookup-trial-observation-count"] = static report => Trial(report)["observationCount"] = 127,
            ["lookup-trial-operation-count"] = static report => Trial(report)["operationCount"] = 524_287,
            ["lookup-trial-sample-count"] = static report =>
                Trial(report)["samplesElapsedTicks"]!.AsArray().RemoveAt(127),
            ["lookup-trial-complete"] = static report => Trial(report)["complete"] = false,
            ["lookup-trial-allocation"] = static report => Trial(report)["allocatedBytes"] = 1,
            ["lookup-aggregate-allocation"] = static report => Lookup(report)["regressionAllocatedBytes"] = 1,
            ["lookup-legacy-flat-samples"] = static report =>
            {
                JsonObject lookup = Lookup(report);
                lookup["regressionSamplesElapsedTicks"] = JsonNode.Parse("[1]");
                lookup["regressionSamplesNanosecondsPerLookup"] = JsonNode.Parse("[1]");
            },
            ["lookup-p95"] = static report =>
                PerformanceGateJson.AddOne(Lookup(report), "regressionP95Nanoseconds"),
            ["lookup-affinity-format"] = static report =>
                Lookup(report)["measurementThreadAffinityMask"] = "0x1",
            ["lookup-affinity-multiple-bits"] = static report =>
                Lookup(report)["measurementThreadAffinityMask"] = "0x0000000000000003",
            ["lookup-processor-group"] = static report => Lookup(report)["measurementProcessorGroup"] = -1,
            ["lookup-processor-number"] = static report => Lookup(report)["measurementProcessorNumber"] = 1,
            ["lookup-thread-priority"] = static report =>
                Lookup(report)["measurementThreadPriority"] = "Normal",
        },
        ReferenceOnly = new Dictionary<string, Action<JsonObject>>
        {
            ["reference-lookup-mapping-population"] = static report =>
                report["sampleRequirements"]!["sourceLookupMappingCountRequired"] = 99_999,
            ["reference-lookup-query-population"] = static report =>
                report["sampleRequirements"]!["sourceLookupAbsoluteQueryCountRequired"] = 19_999,
            ["reference-lookup-mapping-count"] = static report => Lookup(report)["mappingCount"] = 99_999,
            ["reference-lookup-query-count"] = static report => Lookup(report)["queryCount"] = 19_999,
            ["reference-lookup-top-median"] = static report =>
                PerformanceGateJson.AddOne(Lookup(report), "regressionP95Nanoseconds"),
            ["reference-lookup-affinity-mismatch"] = static report =>
            {
                JsonObject lookup = Lookup(report);
                lookup["measurementThreadAffinityMask"] = "0x0000000000000002";
                lookup["measurementProcessorNumber"] = 1;
            },
            ["reference-lookup-group-mismatch"] = static report =>
                Lookup(report)["measurementProcessorGroup"] = 1,
        },
        BothReports = new Dictionary<string, Action<JsonObject>>
        {
            [DiagnosticPrefix + "fields-absent"] = RemoveDiagnosticFields,
            [DiagnosticPrefix + "unsupported-counters"] = SetUnavailableDiagnosticCounters,
        },
    };

    private static Dictionary<string, Action<JsonObject>> CreateShared()
    {
        var shared = new Dictionary<string, Action<JsonObject>>
        {
            ["lookup-required-trials"] = static report =>
                report["sampleRequirements"]!["sourceLookupRegressionTrialsRequired"] = 4,
            ["lookup-required-warmups"] = static report =>
                report["sampleRequirements"]!["sourceLookupRegressionWarmupPassesRequired"] = 7,
            ["lookup-required-batch"] = static report =>
                report["sampleRequirements"]!["sourceLookupRegressionBatchSizeRequired"] = 4_095,
            ["lookup-estimator"] = static report => Lookup(report)["regressionEstimator"] = "wrong-estimator",
            ["lookup-frequency"] = static report => Lookup(report)["stopwatchFrequency"] = Stopwatch.Frequency + 1,
            ["lookup-top-batch"] = static report => Lookup(report)["regressionBatchSize"] = 4_095,
            ["lookup-top-warmups"] = static report => Lookup(report)["regressionWarmupPasses"] = 7,
            ["lookup-normalization"] = static report =>
            {
                JsonObject trial = Trial(report);
                trial["samplesNanosecondsPerLookup"]![0] =
                    trial["samplesNanosecondsPerLookup"]![0]!.GetValue<double>() + 1;
            },
            ["lookup-trial-p95"] = static report => PerformanceGateJson.AddOne(Trial(report), "p95Nanoseconds"),
            ["lookup-absolute-sample"] = static report => Lookup(report)["samplesElapsedTicks"]![0] = -1,
            ["lookup-absolute-p95"] = static report => PerformanceGateJson.AddOne(Lookup(report), "p95Nanoseconds"),
            ["lookup-absolute-budget"] = static report => Lookup(report)["p95BudgetNanoseconds"] = 24_999,
            ["lookup-absolute-allocation"] = static report => Lookup(report)["allocatedBytes"] = 1,
            ["lookup-report-passed"] = static report => Lookup(report)["passed"] = false,
            ["source-lookup-trial-unknown-member"] = static report => Trial(report)["unexpectedDiagnostic"] = 1,
        };

        var diagnostics = new Dictionary<string, Action<JsonObject>>
        {
            ["timestamp-order"] = static report =>
                Trial(report)["completedUtc"] = Trial(report)["startedUtc"]!.DeepClone(),
            ["timestamp-pair"] = static report => Trial(report).Remove("completedUtc"),
            ["elapsed-negative"] = static report => Trial(report)["elapsedStopwatchTicks"] = -1,
            ["elapsed-fractional"] = static report => Trial(report)["elapsedStopwatchTicks"] = 1.5,
            ["elapsed-shorter-than-samples"] = static report => Trial(report)["elapsedStopwatchTicks"] = 1,
            ["cpu-negative"] = static report => Trial(report)["threadCpuTime100NsTicks"] = -2,
            ["cpu-fractional"] = static report => Trial(report)["threadCpuTime100NsTicks"] = 1.5,
            ["cpu-exceeds-stopwatch"] = static report => Trial(report)["threadCpuTime100NsTicks"] = long.MaxValue,
            ["cycles-negative"] = static report => Trial(report)["threadCycleDelta"] = -1,
            ["cycles-fractional"] = static report => Trial(report)["threadCycleDelta"] = 1.5,
            ["gc-negative"] = static report => Trial(report)["gen0Collections"] = -1,
            ["gc-fractional"] = static report => Trial(report)["gen0Collections"] = 0.5,
        };
        foreach ((string name, Action<JsonObject> mutate) in diagnostics)
            shared.Add(DiagnosticPrefix + name, mutate);
        return shared;
    }

    private static JsonObject Lookup(JsonObject report)
        => report["sourceLookup"]!.AsObject();

    private static JsonObject Trial(JsonObject report)
        => Lookup(report)["regressionTrials"]![0]!.AsObject();

    // The legacy trial shape: none of the optional diagnostic fields.
    private static void RemoveDiagnosticFields(JsonObject report)
    {
        string[] names =
        [
            "startedUtc",
            "completedUtc",
            "elapsedStopwatchTicks",
            "threadCpuTime100NsTicks",
            "threadCycleDelta",
            "gen0Collections",
            "gen1Collections",
            "gen2Collections",
        ];
        foreach (JsonNode? trial in Lookup(report)["regressionTrials"]!.AsArray())
        {
            JsonObject trialObject = trial!.AsObject();
            foreach (string name in names)
                trialObject.Remove(name);
        }
    }

    // A platform that cannot read thread CPU time or cycle counters.
    private static void SetUnavailableDiagnosticCounters(JsonObject report)
    {
        foreach (JsonNode? trial in Lookup(report)["regressionTrials"]!.AsArray())
        {
            JsonObject trialObject = trial!.AsObject();
            trialObject["threadCpuTime100NsTicks"] = -1;
            trialObject["threadCycleDelta"] = null;
        }
    }
}
