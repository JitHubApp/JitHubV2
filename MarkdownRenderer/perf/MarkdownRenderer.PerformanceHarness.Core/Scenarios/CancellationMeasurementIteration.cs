namespace MarkdownRenderer.PerformanceHarness.Core.Scenarios;

public readonly record struct CancellationMeasurementIteration(
    bool IsRecorded,
    int SourceSeed,
    int WinnerOrdinal);
