using System.Text.Json.Serialization;

namespace JitHub.Core.Models.GitHub.Repositories;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubRepositorySearchResponse
{
    [JsonPropertyName("total_count")]
    public int TotalCount { get; init; }

    [JsonPropertyName("incomplete_results")]
    public bool IncompleteResults { get; init; }

    [JsonPropertyName("items")]
    public GitHubRepository[] Items { get; init; } = [];
}
