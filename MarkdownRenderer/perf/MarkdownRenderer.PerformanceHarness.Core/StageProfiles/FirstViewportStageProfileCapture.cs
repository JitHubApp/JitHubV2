namespace MarkdownRenderer.PerformanceHarness.Core.StageProfiles;

public sealed record FirstViewportStageProfileCapture(
    long AfterSequence,
    long SourceUtf16Bytes,
    int ManagedThreadId,
    long CpuStartedTicks,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);
