using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

public sealed class GitHubTreeEntry
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("sha")]
    public string? Sha { get; init; }

    [JsonPropertyName("size")]
    public long? Size { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }
}
