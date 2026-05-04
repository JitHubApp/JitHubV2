using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

public sealed class GitHubPullRequestMergeResult
{
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("merged")]
    public bool Merged { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
