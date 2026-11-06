namespace MarkdownRenderer.PerformanceHarness;

public readonly record struct FirstViewportEvent(
    long Sequence,
    long Generation,
    long SourceUtf16Bytes,
    double ElapsedMilliseconds);
