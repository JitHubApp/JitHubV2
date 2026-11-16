using System.Text.Json.Serialization;

namespace JitHub.Core.Models.GitHub.PullRequests;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubPullRequestMergeResult
{
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("merged")]
    public bool Merged { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}
