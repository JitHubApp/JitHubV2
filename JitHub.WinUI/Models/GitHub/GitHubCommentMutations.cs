using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

public sealed class GitHubCommentMutationData
{
    [JsonPropertyName("minimizeComment")]
    public GitHubCommentMutationPayload? MinimizeComment { get; init; }

    [JsonPropertyName("unminimizeComment")]
    public GitHubCommentMutationPayload? UnminimizeComment { get; init; }
}

public sealed class GitHubCommentMutationPayload
{
    [JsonPropertyName("minimizedComment")]
    public GitHubMinimizedComment? MinimizedComment { get; init; }

    [JsonPropertyName("unminimizedComment")]
    public GitHubMinimizedComment? UnminimizedComment { get; init; }
}

public sealed class GitHubMinimizedComment
{
    [JsonPropertyName("isMinimized")]
    public bool IsMinimized { get; init; }

    [JsonPropertyName("minimizedReason")]
    public string? MinimizedReason { get; init; }
}
