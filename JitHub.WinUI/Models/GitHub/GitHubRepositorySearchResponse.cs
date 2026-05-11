using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubRepositorySearchResponse
{
    [JsonPropertyName("items")]
    public GitHubRepository[] Items { get; init; } = [];
}
