namespace MarkdownRenderer.PerformanceHarness.Core.Events;

public readonly record struct PipelineEvent(long Sequence, long Generation, long SourceUtf16Bytes);
