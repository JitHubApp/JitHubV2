using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubRepositorySubscription
{
    [JsonPropertyName("subscribed")]
    public bool Subscribed { get; set; }

    [JsonPropertyName("ignored")]
    public bool Ignored { get; set; }
}
