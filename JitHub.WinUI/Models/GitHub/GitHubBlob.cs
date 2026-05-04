using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

public sealed class GitHubBlob
{
    [JsonPropertyName("node_id")]
    public string? NodeId { get; set; }

    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; }
}
