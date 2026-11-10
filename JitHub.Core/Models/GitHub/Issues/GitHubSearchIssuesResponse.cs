using System.Text.Json.Serialization;

namespace JitHub.Core.Models.GitHub.Issues;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubSearchIssuesResponse
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("items")]
    public GitHubIssue[] Items { get; set; } = [];
}
