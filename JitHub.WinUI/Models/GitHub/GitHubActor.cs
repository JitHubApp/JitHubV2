using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubActor
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }
}
