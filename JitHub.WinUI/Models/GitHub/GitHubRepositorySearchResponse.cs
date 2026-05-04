using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

public sealed class GitHubRepositorySearchResponse
{
    [JsonPropertyName("items")]
    public GitHubRepository[] Items { get; init; } = [];
}
