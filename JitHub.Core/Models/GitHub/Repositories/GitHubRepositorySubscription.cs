using System.Text.Json.Serialization;

namespace JitHub.Core.Models.GitHub.Repositories;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubRepositorySubscription
{
    [JsonPropertyName("subscribed")]
    public bool Subscribed { get; set; }

    [JsonPropertyName("ignored")]
    public bool Ignored { get; set; }
}
