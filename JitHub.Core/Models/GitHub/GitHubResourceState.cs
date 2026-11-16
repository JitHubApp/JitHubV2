using System.Text.Json.Serialization;

namespace JitHub.Core.Models.GitHub;

public sealed class GitHubResourceState
{
    [JsonPropertyName("exists")]
    public bool Exists { get; set; }
}
