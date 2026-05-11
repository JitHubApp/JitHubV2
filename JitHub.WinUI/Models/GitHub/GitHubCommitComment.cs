using System;
using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubCommitComment
{
    [JsonPropertyName("node_id")]
    public string? NodeId { get; set; }

    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("commit_id")]
    public string? CommitId { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("position")]
    public int? Position { get; set; }

    [JsonPropertyName("line")]
    public int? Line { get; set; }

    [JsonPropertyName("user")]
    public GitHubActor User { get; set; } = new();

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("reactions")]
    public GitHubReactionSummary Reactions { get; set; } = new();
}
