using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

public sealed class GitHubTree
{
    [JsonPropertyName("sha")]
    public string? Sha { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("tree")]
    public GitHubTreeEntry[]? Tree { get; init; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }
}
