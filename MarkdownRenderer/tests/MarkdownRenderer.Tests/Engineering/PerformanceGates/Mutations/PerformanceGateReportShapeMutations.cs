using System.Diagnostics;
using System.Text.Json.Nodes;

namespace MarkdownRenderer.Tests.Engineering.PerformanceGates.Mutations;

/// <summary>
/// Gate fixture mutations of the report's identity, timestamps and strict JSON
/// shape: wrong schema or provider, missing, null and unknown members, null
/// collection elements, wrong numeric and element types, and the measurement
/// visibility evidence.
/// </summary>
internal static class PerformanceGateReportShapeMutations
{
    internal static PerformanceGateReportMutations Mutations { get; } = new()
    {
        Shared = new Dictionary<string, Action<JsonObject>>
        {
            ["schema-version"] = static report => report["schemaVersion"] = 9,
            ["provider-name"] = static report => report["providerName"] = "wrong-provider",
            ["timestamp-order"] = static report =>
                report["completedUtc"] = report["startedUtc"]!.DeepClone(),
            ["timestamp-non-utc"] = static report =>
                report["startedUtc"] = PerformanceGateJson.ToNonUtcTimestamp(report["startedUtc"]!),
            ["missing-top-member"] = static report => report.Remove("providerName"),
            ["missing-nested-member"] = static report =>
                report["machine"]!.AsObject().Remove("gpuDriverVersion"),
            ["null-top-member"] = static report => report["buildIdentity"] = null,
            ["null-nested-member"] = static report => report["machine"]!["gpuDriverVersion"] = null,
            ["unknown-top-member"] = static report => report["unexpected"] = 1,
            ["unknown-nested-member"] = static report => report["machine"]!["unexpected"] = 1,
            ["unknown-trial-member"] = static report =>
                report["scroll"]!["trials"]![0]!["unexpected"] = 1,
            ["null-first-viewport-element"] = static report => report["firstUsableViewport"]![0] = null,
            ["null-retained-memory-element"] = static report => report["retainedMemory"]![0] = null,
            ["null-scroll-trial-element"] = static report => report["scroll"]!["trials"]![0] = null,
            ["null-lookup-trial-element"] = static report =>
                report["sourceLookup"]!["regressionTrials"]![0] = null,
            ["null-cancellation-trial-element"] = static report =>
                report["cancellation"]!["trials"]![0] = null,
            ["fractional-int32"] = static report =>
                report["sampleRequirements"]!["firstViewportIterationsRequired"] = 100.5,
            ["fractional-int64"] = static report =>
                report["scroll"]!["stopwatchFrequency"] = Stopwatch.Frequency + 0.5,
            ["string-first-viewport-sample"] = static report =>
                report["firstUsableViewport"]![0]!["samplesMilliseconds"]![0] = "invalid",
            ["null-scroll-raw-sample"] = static report =>
                report["scroll"]!["trials"]![0]!["uiThreadWorkElapsedTicks"]![0] = null,
            ["string-lookup-raw-sample"] = static report =>
                report["sourceLookup"]!["samplesElapsedTicks"]![0] = "invalid",
            ["null-cancellation-sample"] = static report =>
                report["cancellation"]!["samplesMilliseconds"]![0] = null,
            ["number-failure-element"] = static report => report["failures"]!.AsArray().Add(1),
            ["decision-policy"] = static report =>
                report["regression"]!["decisionPolicy"] = "wrong-policy",
            ["observed-refresh"] = static report => report["machine"]!["observedRefreshRateHz"] = 100,
        },
        CandidateOnly = new Dictionary<string, Action<JsonObject>>
        {
            ["null-comparison-element"] = static report =>
                report["regression"]!["comparisons"]![0] = null,
            ["measurement-visibility-missing"] = static report => report.Remove("measurementVisibility"),
            ["measurement-visibility-foreground-loss"] = static report =>
                report["measurementVisibility"]!["foregroundLossEvents"] = 1,
            ["measurement-visibility-occluded"] = static report =>
                report["measurementVisibility"]!["samples"]![1]!["targetWasUnoccluded"] = false,
            ["measurement-visibility-private-source"] = static report =>
                report["measurementVisibility"]!["samples"]![1]!["source"] = "Private document title",
        },
        ReferenceOnly = new Dictionary<string, Action<JsonObject>>
        {
            ["reference-null-comparison-element"] = static report =>
                report["regression"]!["comparisons"]!.AsArray().Add(null),
            ["reference-failures"] = static report => report["failures"]!.AsArray().Add("failure"),
            ["reference-top-passed"] = static report => report["passed"] = false,
        },
    };
}
