namespace MarkdownRenderer.PerformanceHarness;

public sealed class FirstViewportTrialResult
{
    public int Ordinal { get; init; }
    public int GlobalOrdinal { get; init; }
    public int ScheduleRow { get; init; }
    public int SchedulePosition { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset CompletedUtc { get; init; }
    public double SettlingElapsedMilliseconds { get; init; }
    public double SettlingPublicationMilliseconds { get; init; }
    public List<double> SamplesMilliseconds { get; init; } = [];
    public List<double> PublicationSamplesMilliseconds { get; init; } = [];
    public double PublicationMaximumMilliseconds { get; init; }
    public double P95Milliseconds { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
    public long ProcessAllocatedBytes { get; init; }
    public int CompletedParseCountBeforePrime { get; init; }
    public int CompletedParseCountAfterPrime { get; init; }
    public int CompletedParseCountAfterTrial { get; init; }
    public long SourceKeyHashCountBeforePrime { get; init; }
    public long SourceKeyHashCountAfterPrime { get; init; }
    public long SourceKeyHashCountAfterTrial { get; init; }
    public bool CacheProofPassed { get; init; }
    public bool Complete { get; init; }
}
