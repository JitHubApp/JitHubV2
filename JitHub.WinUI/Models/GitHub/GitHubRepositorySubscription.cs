using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

public sealed class GitHubRepositorySubscription
{
    [JsonPropertyName("subscribed")]
    public bool Subscribed { get; set; }

    [JsonPropertyName("ignored")]
    public bool Ignored { get; set; }
}
