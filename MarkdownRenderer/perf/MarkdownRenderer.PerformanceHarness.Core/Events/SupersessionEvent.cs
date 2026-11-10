namespace MarkdownRenderer.PerformanceHarness.Core.Events;

public readonly record struct SupersessionEvent(long Sequence, long Generation);
