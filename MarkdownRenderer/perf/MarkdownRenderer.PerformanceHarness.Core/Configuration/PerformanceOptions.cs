using System.Globalization;
using MarkdownRenderer.PerformanceHarness.Core.Deployment;

namespace MarkdownRenderer.PerformanceHarness.Core.Configuration;

public sealed record PerformanceOptions(
    string OutputPath,
    string? ReferencePath,
    bool EstablishBaseline,
    bool Quick,
    int FirstViewportIterations,
    int ScrollFrames,
    int ScrollTrials,
    int LifecycleCycles,
    int CancellationIterations)
{
    public static PerformanceOptions Parse(IReadOnlyList<string> arguments)
    {
        ParsedArguments parsed = ReadArguments(arguments);
        if (string.IsNullOrWhiteSpace(parsed.Output))
            throw new ArgumentException("--output <absolute-json-path> is required.");

        string fullOutput = Path.GetFullPath(parsed.Output);
        string? fullReference = string.IsNullOrWhiteSpace(parsed.Reference)
            ? null
            : Path.GetFullPath(parsed.Reference);
        RejectPathsInsideDeployment(fullOutput, fullReference);
        RejectInvalidCombination(parsed, fullOutput, fullReference);

        return new PerformanceOptions(
            fullOutput,
            fullReference,
            parsed.Baseline,
            parsed.Quick,
            parsed.FirstViewportIterations,
            parsed.ScrollFrames,
            parsed.ScrollTrials,
            parsed.LifecycleCycles,
            parsed.CancellationIterations);
    }

    private static ParsedArguments ReadArguments(IReadOnlyList<string> arguments)
    {
        var parsed = new ParsedArguments();
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            switch (argument)
            {
                case "--output":
                    parsed.Output = ReadValue(arguments, ref index, argument);
                    break;
                case "--reference":
                    parsed.Reference = ReadValue(arguments, ref index, argument);
                    break;
                case "--baseline":
                    parsed.Baseline = true;
                    break;
                case "--quick":
                    parsed.Quick = true;
                    parsed.FirstViewportIterations = PerformanceMeasurementContract.QuickFirstViewportIterations;
                    parsed.ScrollFrames = PerformanceMeasurementContract.QuickScrollFrames;
                    parsed.ScrollTrials = PerformanceMeasurementContract.QuickScrollTrials;
                    parsed.LifecycleCycles = PerformanceMeasurementContract.QuickLifecycleCycles;
                    parsed.CancellationIterations = PerformanceMeasurementContract.QuickCancellationIterations;
                    break;
                case "--first-viewport-iterations":
                    parsed.PopulationOverrideSpecified = true;
                    parsed.FirstViewportIterations = ParsePositiveInt(ReadValue(arguments, ref index, argument), argument);
                    break;
                case "--scroll-frames":
                    parsed.PopulationOverrideSpecified = true;
                    parsed.ScrollFrames = ParsePositiveInt(ReadValue(arguments, ref index, argument), argument);
                    break;
                case "--scroll-trials":
                    parsed.PopulationOverrideSpecified = true;
                    parsed.ScrollTrials = ParsePositiveInt(ReadValue(arguments, ref index, argument), argument);
                    break;
                case "--lifecycle-cycles":
                    parsed.PopulationOverrideSpecified = true;
                    parsed.LifecycleCycles = ParsePositiveInt(ReadValue(arguments, ref index, argument), argument);
                    break;
                case "--cancellation-iterations":
                    parsed.PopulationOverrideSpecified = true;
                    parsed.CancellationIterations = ParsePositiveInt(ReadValue(arguments, ref index, argument), argument);
                    break;
                default:
                    throw new ArgumentException($"Unknown performance harness argument '{argument}'.");
            }
        }

        return parsed;
    }

    private static void RejectPathsInsideDeployment(string fullOutput, string? fullReference)
    {
        string executablePath = Environment.ProcessPath ??
            throw new InvalidOperationException(
                "The performance executable path is unavailable.");
        if (PerformanceDeploymentIdentity.IsPathWithinDeploymentDirectory(
                fullOutput,
                executablePath))
        {
            throw new ArgumentException(
                "--output must be outside the executable deployment directory so report creation cannot change the frozen runtime-output manifest.");
        }
        if (fullReference is not null &&
            PerformanceDeploymentIdentity.IsPathWithinDeploymentDirectory(
                fullReference,
                executablePath))
        {
            throw new ArgumentException(
                "--reference must be outside the executable deployment directory so reference files cannot affect the frozen runtime-output manifest.");
        }
    }

    private static void RejectInvalidCombination(
        ParsedArguments parsed,
        string fullOutput,
        string? fullReference)
    {
        bool baseline = parsed.Baseline;
        bool quick = parsed.Quick;
        if (baseline && fullReference is not null)
            throw new ArgumentException("--baseline and --reference are mutually exclusive.");
        if (quick && (baseline || fullReference is not null))
            throw new ArgumentException("--quick cannot be combined with --baseline or --reference.");
        if (quick && parsed.PopulationOverrideSpecified)
        {
            throw new ArgumentException(
                "--quick uses its frozen 1/30/1/8/1 smoke populations and cannot include population overrides.");
        }
        if (fullReference is not null &&
            string.Equals(fullOutput, fullReference, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "--output and --reference must identify different files under Windows path semantics.");
        }
        if (File.Exists(fullOutput) || Directory.Exists(fullOutput))
            throw new IOException($"Output report '{fullOutput}' already exists and will not be overwritten.");
        if (fullReference is not null && !File.Exists(fullReference))
            throw new FileNotFoundException("Reference performance report was not found.", fullReference);
        if (!baseline && fullReference is null && !quick)
        {
            throw new ArgumentException(
                "Release measurement requires --reference <same-machine-report> or --baseline. " +
                "Use --quick only for a non-gating smoke run.");
        }
    }

    private static string ReadValue(IReadOnlyList<string> arguments, ref int index, string option)
    {
        if (++index >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index]))
            throw new ArgumentException($"{option} requires a value.");
        return arguments[index];
    }

    private static int ParsePositiveInt(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) || parsed <= 0)
            throw new ArgumentException($"{option} requires a positive integer.");
        return parsed;
    }

    /// <summary>The switches as read, before paths are resolved and checked.</summary>
    private sealed class ParsedArguments
    {
        public string? Output { get; set; }
        public string? Reference { get; set; }
        public bool Baseline { get; set; }
        public bool Quick { get; set; }
        public bool PopulationOverrideSpecified { get; set; }
        public int FirstViewportIterations { get; set; } =
            PerformanceMeasurementContract.ReleaseFirstViewportIterations;
        public int ScrollFrames { get; set; } = PerformanceMeasurementContract.ReleaseScrollFrames;
        public int ScrollTrials { get; set; } = PerformanceMeasurementContract.ReleaseScrollTrials;
        public int LifecycleCycles { get; set; } = PerformanceMeasurementContract.ReleaseLifecycleCycles;
        public int CancellationIterations { get; set; } =
            PerformanceMeasurementContract.ReleaseCancellationIterations;
    }
}
