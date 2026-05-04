using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

public sealed class GitHubUser
{
    [JsonPropertyName("id")]
    public long Id { get; init; }

    [JsonPropertyName("login")]
    public string Login { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("avatar_url")]
    public string? AvatarUrl { get; init; }

    [JsonPropertyName("bio")]
    public string? Bio { get; init; }

    [JsonPropertyName("company")]
    public string? Company { get; init; }

    [JsonPropertyName("location")]
    public string? Location { get; init; }

    [JsonPropertyName("blog")]
    public string? Blog { get; init; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; init; }

    [JsonPropertyName("public_repos")]
    public int PublicRepos { get; init; }

    [JsonPropertyName("followers")]
    public int Followers { get; init; }

    [JsonPropertyName("following")]
    public int Following { get; init; }
}
