namespace MarkdownRenderer.PerformanceHarness.Core.Events;

public readonly record struct CancellationEvent(long Sequence, long Generation, double ElapsedMilliseconds);
