using System.Globalization;

namespace MarkdownRenderer.PerformanceHarness;

internal sealed record PerformanceOptions(
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
    internal static PerformanceOptions Parse(IReadOnlyList<string> arguments)
    {
        string? output = null;
        string? reference = null;
        bool baseline = false;
        bool quick = false;
        bool populationOverrideSpecified = false;
        int firstViewportIterations = PerformanceMeasurementContract.ReleaseFirstViewportIterations;
        int scrollFrames = PerformanceMeasurementContract.ReleaseScrollFrames;
        int scrollTrials = PerformanceMeasurementContract.ReleaseScrollTrials;
        int lifecycleCycles = PerformanceMeasurementContract.ReleaseLifecycleCycles;
        int cancellationIterations = PerformanceMeasurementContract.ReleaseCancellationIterations;

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            switch (argument)
            {
                case "--output":
                    output = ReadValue(arguments, ref index, argument);
                    break;
                case "--reference":
                    reference = ReadValue(arguments, ref index, argument);
                    break;
                case "--baseline":
                    baseline = true;
                    break;
                case "--quick":
                    quick = true;
                    firstViewportIterations = PerformanceMeasurementContract.QuickFirstViewportIterations;
                    scrollFrames = PerformanceMeasurementContract.QuickScrollFrames;
                    scrollTrials = PerformanceMeasurementContract.QuickScrollTrials;
                    lifecycleCycles = PerformanceMeasurementContract.QuickLifecycleCycles;
                    cancellationIterations = PerformanceMeasurementContract.QuickCancellationIterations;
                    break;
                case "--first-viewport-iterations":
                    populationOverrideSpecified = true;
                    firstViewportIterations = ParsePositiveInt(ReadValue(arguments, ref index, argument), argument);
                    break;
                case "--scroll-frames":
                    populationOverrideSpecified = true;
                    scrollFrames = ParsePositiveInt(ReadValue(arguments, ref index, argument), argument);
                    break;
                case "--scroll-trials":
                    populationOverrideSpecified = true;
                    scrollTrials = ParsePositiveInt(ReadValue(arguments, ref index, argument), argument);
                    break;
                case "--lifecycle-cycles":
                    populationOverrideSpecified = true;
                    lifecycleCycles = ParsePositiveInt(ReadValue(arguments, ref index, argument), argument);
                    break;
                case "--cancellation-iterations":
                    populationOverrideSpecified = true;
                    cancellationIterations = ParsePositiveInt(ReadValue(arguments, ref index, argument), argument);
                    break;
                default:
                    throw new ArgumentException($"Unknown performance harness argument '{argument}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(output))
            throw new ArgumentException("--output <absolute-json-path> is required.");

        string fullOutput = Path.GetFullPath(output);
        string? fullReference = string.IsNullOrWhiteSpace(reference)
            ? null
            : Path.GetFullPath(reference);
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
        if (baseline && fullReference is not null)
            throw new ArgumentException("--baseline and --reference are mutually exclusive.");
        if (quick && (baseline || fullReference is not null))
            throw new ArgumentException("--quick cannot be combined with --baseline or --reference.");
        if (quick && populationOverrideSpecified)
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

        return new PerformanceOptions(
            fullOutput,
            fullReference,
            baseline,
            quick,
            firstViewportIterations,
            scrollFrames,
            scrollTrials,
            lifecycleCycles,
            cancellationIterations);
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
}
