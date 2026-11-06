namespace MarkdownRenderer.PerformanceHarness;

public readonly record struct CancellationMeasurementIteration(
    bool IsRecorded,
    int SourceSeed,
    int WinnerOrdinal);
