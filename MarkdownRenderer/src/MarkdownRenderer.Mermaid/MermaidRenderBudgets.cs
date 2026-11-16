namespace MarkdownRenderer.Mermaid;

/// <summary>Resource limits enforced before and during native rendering and MMIR decoding.</summary>
public sealed record MermaidRenderBudgets
{
    public const int DefaultMaxSourceBytes = 256 * 1024;
    public const int DefaultMaxNodes = 2_000;
    public const int DefaultMaxEdges = 4_000;
    public const int DefaultMaxDepth = 64;
    public const int DefaultMaxLabelBytes = 16 * 1024;
    public const int DefaultMaxSceneBytes = 16 * 1024 * 1024;
    public const long DefaultMaxWorkingMemoryBytes = 64L * 1024 * 1024;
    public const int DefaultMaxConcurrentRenders = 4;
    public const int DefaultMaxOutstandingRenders = 16;
    public const long DefaultMaxOutstandingSourceBytes = 4L * 1024 * 1024;

    internal const int AbsoluteMaxSourceBytes = 16 * 1024 * 1024;
    internal const int AbsoluteMaxNodes = 1_000_000;
    internal const int AbsoluteMaxEdges = 2_000_000;
    internal const int AbsoluteMaxDepth = 1_024;
    internal const int AbsoluteMaxLabelBytes = 1024 * 1024;
    internal const int AbsoluteMaxSceneBytes = 256 * 1024 * 1024;
    internal const long AbsoluteMaxWorkingMemoryBytes = 2L * 1024 * 1024 * 1024;
    internal const int AbsoluteMaxOutstandingRenders = 4_096;
    internal const long AbsoluteMaxOutstandingSourceBytes = 1024L * 1024 * 1024;

    public static MermaidRenderBudgets Default { get; } = new();

    public int MaxSourceBytes { get; init; } = DefaultMaxSourceBytes;

    public int MaxNodes { get; init; } = DefaultMaxNodes;

    public int MaxEdges { get; init; } = DefaultMaxEdges;

    public int MaxDepth { get; init; } = DefaultMaxDepth;

    public int MaxLabelBytes { get; init; } = DefaultMaxLabelBytes;

    public int MaxSceneBytes { get; init; } = DefaultMaxSceneBytes;

    public long MaxWorkingMemoryBytes { get; init; } = DefaultMaxWorkingMemoryBytes;

    public TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(2);

    public int MaxConcurrentRenders { get; init; } = DefaultMaxConcurrentRenders;

    /// <summary>Maximum running plus queued unique render operations.</summary>
    public int MaxOutstandingRenders { get; init; } = DefaultMaxOutstandingRenders;

    /// <summary>Maximum retained UTF-8 bytes across running plus queued unique sources.</summary>
    public long MaxOutstandingSourceBytes { get; init; } = DefaultMaxOutstandingSourceBytes;

    internal string? Validate(bool allowIncreases)
    {
        if (MaxSourceBytes is <= 0 or > AbsoluteMaxSourceBytes)
        {
            return $"{nameof(MaxSourceBytes)} must be between 1 and {AbsoluteMaxSourceBytes}.";
        }

        if (MaxNodes is <= 0 or > AbsoluteMaxNodes || MaxEdges is <= 0 or > AbsoluteMaxEdges ||
            MaxDepth is <= 0 or > AbsoluteMaxDepth || MaxLabelBytes is <= 0 or > AbsoluteMaxLabelBytes ||
            MaxSceneBytes is <= 0 or > AbsoluteMaxSceneBytes ||
            MaxWorkingMemoryBytes is <= 0 or > AbsoluteMaxWorkingMemoryBytes ||
            MaxSceneBytes > MaxWorkingMemoryBytes ||
            Deadline <= TimeSpan.Zero || Deadline > TimeSpan.FromMinutes(2) ||
            MaxConcurrentRenders is <= 0 or > 64 ||
            MaxOutstandingRenders is <= 0 or > AbsoluteMaxOutstandingRenders ||
            MaxOutstandingSourceBytes is <= 0 or > AbsoluteMaxOutstandingSourceBytes ||
            MaxConcurrentRenders > MaxOutstandingRenders ||
            MaxSourceBytes > MaxOutstandingSourceBytes)
        {
            return "One or more Mermaid resource limits are outside the supported range.";
        }

        if (!allowIncreases &&
            (MaxSourceBytes > DefaultMaxSourceBytes || MaxNodes > DefaultMaxNodes ||
             MaxEdges > DefaultMaxEdges || MaxDepth > DefaultMaxDepth ||
              MaxLabelBytes > DefaultMaxLabelBytes || MaxSceneBytes > DefaultMaxSceneBytes ||
              MaxWorkingMemoryBytes > DefaultMaxWorkingMemoryBytes ||
              Deadline > TimeSpan.FromSeconds(2) || MaxConcurrentRenders > DefaultMaxConcurrentRenders ||
              MaxOutstandingRenders > DefaultMaxOutstandingRenders ||
              MaxOutstandingSourceBytes > DefaultMaxOutstandingSourceBytes))
        {
            return "Increasing a Mermaid limit above its secure default requires AllowBudgetIncreases.";
        }

        return null;
    }
}
