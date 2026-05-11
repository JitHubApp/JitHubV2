using System;
using System.Text.Json.Serialization;
using JitHub.WinUI.Helpers;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubIssueComment
{
    [JsonPropertyName("node_id")]
    public string? NodeId { get; set; }

    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("body")]
    public string Body { get; set; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("user")]
    public GitHubActor User { get; set; } = new();

    [JsonPropertyName("reactions")]
    public GitHubReactionSummary Reactions { get; set; } = new();

    [JsonPropertyName("author_association")]
    public string? AuthorAssociation { get; set; }

    [JsonIgnore]
    public string ReactionsButtonText => LocalizedResourceText.GetString("Common.ReactionsButton", "Reactions");
}
