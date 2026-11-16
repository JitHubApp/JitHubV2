using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Security.Cryptography;
using MarkdownRenderer.PerformanceHarness.Core.Configuration;

namespace MarkdownRenderer.PerformanceHarness.Core.Reports;

public sealed class PerformanceReport
{
    public const int CurrentSchemaVersion = PerformanceMeasurementContract.SchemaVersion;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string ProviderName { get; init; } = PerformanceMeasurementContract.ProviderName;
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; set; }
    public bool IsReleaseEvidence { get; init; }
    public bool Passed { get; set; }
    public string BuildIdentity { get; init; } = string.Empty;
    public BuildArtifactHashes BuildArtifacts { get; init; } = new();
    public RuntimeConfigurationEvidence RuntimeConfiguration { get; init; } = new();
    public MachineMetadata Machine { get; set; } = new();
    public MachineMetadata CompletionMachine { get; set; } = new();
    public SampleRequirements SampleRequirements { get; init; } = new();
    public List<FirstViewportResult> FirstUsableViewport { get; init; } = [];
    public ScrollResult Scroll { get; set; } = new();
    public List<RetainedMemoryResult> RetainedMemory { get; init; } = [];
    public LifecyclePlateauResult LifecyclePlateau { get; set; } = new();
    public SourceLookupResult SourceLookup { get; set; } = new();
    public CancellationResult Cancellation { get; set; } = new();
    public RegressionResult Regression { get; set; } = new();
    public MeasurementVisibilityEvidence MeasurementVisibility { get; set; } = new();
    public List<string> Failures { get; init; } = [];

    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    public static PerformanceReportEvidence ReadEvidence(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Reference performance report was not found.", path);
        byte[] bytes = File.ReadAllBytes(path);
        PerformanceReport report;
        try
        {
            report = JsonSerializer.Deserialize<PerformanceReport>(bytes, JsonOptions)
                ?? throw new InvalidDataException($"Performance report '{path}' was empty.");
            if (!HasNonNullReferenceElements(report))
            {
                throw new InvalidDataException(
                    $"Performance report '{path}' contained a null collection element.");
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Performance report '{path}' did not satisfy the explicit schema contract.",
                exception);
        }

        return new PerformanceReportEvidence(
            report,
            Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(static typeInfo =>
        {
            // Evidence is only trustworthy when every producer-emitted member is
            // explicitly present. Defaults remain useful while constructing a
            // report, but must never repair a truncated reference during read.
            // The rule is scoped to this namespace, so every type the report
            // serializes lives in Reports/.
            if (typeInfo.Kind != JsonTypeInfoKind.Object ||
                typeInfo.Type.Assembly != typeof(PerformanceReport).Assembly ||
                typeInfo.Type.Namespace != typeof(PerformanceReport).Namespace)
            {
                return;
            }

            foreach (JsonPropertyInfo property in typeInfo.Properties)
            {
                // Diagnostic-only additions are intentionally optional so a
                // current reader can still consume frozen reports produced by
                // the same schema before the diagnostics existed.
                property.IsRequired = property.AttributeProvider?.IsDefined(
                    typeof(PerformanceDiagnosticOnlyAttribute),
                    inherit: true) != true;
            }
        });

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            AllowDuplicateProperties = false,
            TypeInfoResolver = resolver,
        };
        // The options are process-wide, so freeze them: no caller can change
        // the evidence schema after the first report is read or written.
        options.MakeReadOnly();
        return options;
    }

    private static bool HasNonNullReferenceElements(PerformanceReport report)
    {
        if (report.RuntimeConfiguration is null ||
            report.RuntimeConfiguration.OverrideEnvironmentVariables is null ||
            report.RuntimeConfiguration.OverrideEnvironmentVariables.Any(static item => item is null) ||
            report.FirstUsableViewport is null ||
            report.FirstUsableViewport.Any(static item => item is null) ||
            report.RetainedMemory is null ||
            report.RetainedMemory.Any(static item => item is null) ||
            report.Failures is null ||
            report.Failures.Any(static item => item is null) ||
            report.Scroll is null ||
            report.Scroll.Trials is null ||
            report.Scroll.Trials.Any(static item => item is null) ||
            report.SourceLookup is null ||
            report.SourceLookup.RegressionTrials is null ||
            report.SourceLookup.RegressionTrials.Any(static item => item is null) ||
            report.Cancellation is null ||
            report.Cancellation.Trials is null ||
            report.Cancellation.Trials.Any(static item => item is null) ||
            report.Regression is null ||
            report.Regression.Comparisons is null ||
            report.Regression.Comparisons.Any(static item => item is null) ||
            report.MeasurementVisibility is null ||
            report.MeasurementVisibility.Samples is null ||
            report.MeasurementVisibility.Samples.Any(static item => item is null))
        {
            return false;
        }

        return report.FirstUsableViewport.All(static result =>
            result.WarmupTrials is not null &&
            !result.WarmupTrials.Any(static trial => trial is null) &&
            result.Trials is not null &&
            !result.Trials.Any(static trial => trial is null) &&
            result.PublicationSamplesMilliseconds is not null &&
            result.WarmupTrials.All(static trial =>
                trial.PublicationSamplesMilliseconds is not null) &&
            result.Trials.All(static trial =>
                trial.PublicationSamplesMilliseconds is not null));
    }
}
