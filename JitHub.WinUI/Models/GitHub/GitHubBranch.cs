using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

public sealed class GitHubBranch
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}
