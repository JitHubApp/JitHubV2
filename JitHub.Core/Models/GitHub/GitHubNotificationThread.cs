using System;
using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubNotificationThread
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("subscription_url")]
    public string SubscriptionUrl { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("unread")]
    public bool Unread { get; set; }

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("updated_at")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("last_read_at")]
    public DateTimeOffset? LastReadAt { get; set; }

    [JsonPropertyName("subject")]
    public GitHubNotificationSubject Subject { get; set; } = new();

    [JsonPropertyName("repository")]
    public GitHubRepository Repository { get; set; } = new();
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubNotificationSubscription
{
    [JsonPropertyName("subscribed")]
    public bool Subscribed { get; set; }

    [JsonPropertyName("ignored")]
    public bool Ignored { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

public sealed class GitHubNotificationMarkReadRequest
{
    [JsonPropertyName("last_read_at")]
    public DateTimeOffset LastReadAt { get; set; }
}

public sealed class GitHubNotificationSubscriptionUpdateRequest
{
    [JsonPropertyName("ignored")]
    public bool Ignored { get; set; }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubNotificationSubject
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("latest_comment_url")]
    public string? LatestCommentUrl { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}
