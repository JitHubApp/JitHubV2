namespace MarkdownRenderer.PerformanceHarness;

public readonly record struct CancellationEvent(long Sequence, long Generation, double ElapsedMilliseconds);
