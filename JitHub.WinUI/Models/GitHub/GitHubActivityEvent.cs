using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubActivityEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("public")]
    public bool Public { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset? CreatedAt { get; set; }

    [JsonPropertyName("actor")]
    public GitHubActor Actor { get; set; } = new();

    [JsonPropertyName("repo")]
    public GitHubActivityRepository Repo { get; set; } = new();

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }

    [JsonIgnore]
    public IGitHubActivityPayload? EnrichedPayload { get; set; }

    [JsonIgnore]
    public IGitHubActivityPayload TypedPayload => EnrichedPayload ?? GitHubActivityPayloadFactory.Create(Type, Payload);

    [JsonIgnore]
    public string ActorDisplayName => string.IsNullOrWhiteSpace(Actor.Login) ? "Someone" : Actor.Login;

    [JsonIgnore]
    public string RepoDisplayName => string.IsNullOrWhiteSpace(Repo.Name) ? "a repository" : Repo.Name;

    [JsonIgnore]
    public string TimestampDisplayText => CreatedAt?.LocalDateTime.ToString("g") ?? "Unknown time";

    [JsonIgnore]
    public string Summary => BuildSummary();

    [JsonIgnore]
    public string MetaText => $"{ActorDisplayName}  •  {RepoDisplayName}  •  {TimestampDisplayText}";

    [JsonIgnore]
    public string TargetUrl => BuildTargetUrl();

    private string BuildSummary()
    {
        return Type switch
        {
            "PushEvent" => BuildPushSummary(),
            "IssuesEvent" => BuildIssueSummary("issue"),
            "IssueCommentEvent" => BuildIssueCommentSummary(),
            "PullRequestEvent" => BuildPullRequestSummary(),
            "PullRequestReviewCommentEvent" => BuildPullRequestReviewCommentSummary(),
            "WatchEvent" => "Starred the repository",
            "ForkEvent" => BuildForkSummary(),
            "CreateEvent" => BuildCreateOrDeleteSummary("created"),
            "DeleteEvent" => BuildCreateOrDeleteSummary("deleted"),
            "ReleaseEvent" => BuildReleaseSummary(),
            "PublicEvent" => "Made the repository public",
            "GollumEvent" => "Updated the wiki",
            "CommitCommentEvent" => "Commented on a commit",
            "MemberEvent" => BuildMemberSummary(),
            "SponsorshipEvent" => BuildActionSummary("Updated sponsorship"),
            _ => HumanizeType(Type)
        };
    }

    private string BuildTargetUrl()
    {
        if (TryGetNestedString(Payload, "comment", "html_url", out string? commentUrl))
        {
            return commentUrl!;
        }

        if (TryGetNestedString(Payload, "pull_request", "html_url", out string? pullRequestUrl))
        {
            return pullRequestUrl!;
        }

        if (TryGetNestedString(Payload, "issue", "html_url", out string? issueUrl))
        {
            return issueUrl!;
        }

        if (TryGetNestedString(Payload, "release", "html_url", out string? releaseUrl))
        {
            return releaseUrl!;
        }

        if (TryGetNestedString(Payload, "forkee", "html_url", out string? forkUrl))
        {
            return forkUrl!;
        }

        return string.IsNullOrWhiteSpace(Repo.Name) ? string.Empty : $"https://github.com/{Repo.Name}";
    }

    private string BuildPushSummary()
    {
        int commitCount = GetArrayLength(Payload, "commits");
        string branchName = GetRefName();
        return commitCount switch
        {
            <= 0 => $"Pushed to {branchName}",
            1 => $"Pushed 1 commit to {branchName}",
            _ => $"Pushed {commitCount} commits to {branchName}"
        };
    }

    private string BuildIssueSummary(string itemLabel)
    {
        string action = GetActionOrFallback("Updated");
        int? number = GetNestedInt32(Payload, "issue", "number");
        return number.HasValue ? $"{action} {itemLabel} #{number.Value}" : $"{action} an {itemLabel}";
    }

    private string BuildIssueCommentSummary()
    {
        string action = GetActionOrFallback("Commented on");
        int? number = GetNestedInt32(Payload, "issue", "number");
        return number.HasValue ? $"{action} issue #{number.Value}" : $"{action} an issue";
    }

    private string BuildPullRequestSummary()
    {
        string action = GetActionOrFallback("Updated");
        int? number = GetNestedInt32(Payload, "pull_request", "number");
        return number.HasValue ? $"{action} pull request #{number.Value}" : $"{action} a pull request";
    }

    private string BuildPullRequestReviewCommentSummary()
    {
        int? number = GetNestedInt32(Payload, "pull_request", "number");
        return number.HasValue ? $"Commented on pull request #{number.Value}" : "Commented on a pull request";
    }

    private string BuildForkSummary()
    {
        if (TryGetNestedString(Payload, "forkee", "full_name", out string? forkName))
        {
            return $"Forked to {forkName}";
        }

        return "Forked the repository";
    }

    private string BuildCreateOrDeleteSummary(string verb)
    {
        string refType = GetString(Payload, "ref_type") ?? "resource";
        string? refName = GetString(Payload, "ref");
        return string.IsNullOrWhiteSpace(refName) ? $"{Capitalize(verb)} a {refType}" : $"{Capitalize(verb)} {refType} {refName}";
    }

    private string BuildReleaseSummary()
    {
        if (TryGetNestedString(Payload, "release", "name", out string? releaseName) && !string.IsNullOrWhiteSpace(releaseName))
        {
            return $"Published release {releaseName}";
        }

        if (TryGetNestedString(Payload, "release", "tag_name", out string? tagName) && !string.IsNullOrWhiteSpace(tagName))
        {
            return $"Published release {tagName}";
        }

        return "Published a release";
    }

    private string BuildMemberSummary()
    {
        string action = GetActionOrFallback("Updated");
        if (TryGetNestedString(Payload, "member", "login", out string? memberLogin) && !string.IsNullOrWhiteSpace(memberLogin))
        {
            return $"{action} member @{memberLogin}";
        }

        return $"{action} repository members";
    }

    private string BuildActionSummary(string fallback)
    {
        string? action = GetString(Payload, "action");
        return string.IsNullOrWhiteSpace(action) ? fallback : $"{Capitalize(action)}";
    }

    private string GetRefName()
    {
        string? gitRef = GetString(Payload, "ref");
        if (string.IsNullOrWhiteSpace(gitRef))
        {
            return "the repository";
        }

        int lastSlash = gitRef.LastIndexOf('/');
        return lastSlash >= 0 && lastSlash < gitRef.Length - 1 ? gitRef[(lastSlash + 1)..] : gitRef;
    }

    private string GetActionOrFallback(string fallback)
    {
        string? action = GetString(Payload, "action");
        return string.IsNullOrWhiteSpace(action) ? fallback : Capitalize(action);
    }

    private static string HumanizeType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "Updated the repository";
        }

        string trimmed = type.EndsWith("Event", StringComparison.Ordinal) ? type[..^5] : type;
        return $"{InsertWordBoundaries(trimmed)} activity";
    }

    private static string InsertWordBoundaries(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        System.Text.StringBuilder builder = new(value.Length + 8);
        builder.Append(value[0]);
        for (int i = 1; i < value.Length; i++)
        {
            char current = value[i];
            if (char.IsUpper(current) && !char.IsWhiteSpace(value[i - 1]))
            {
                builder.Append(' ');
            }

            builder.Append(current);
        }

        return builder.ToString();
    }

    private static string Capitalize(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..];
    }

    private static int GetArrayLength(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Array
                ? property.GetArrayLength()
                : 0;
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
    }

    private static int? GetNestedInt32(JsonElement element, string objectPropertyName, string valuePropertyName)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(objectPropertyName, out JsonElement nested)
            || nested.ValueKind != JsonValueKind.Object
            || !nested.TryGetProperty(valuePropertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return property.TryGetInt32(out int value) ? value : null;
    }

    private static bool TryGetNestedString(JsonElement element, string objectPropertyName, string valuePropertyName, out string? value)
    {
        value = null;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(objectPropertyName, out JsonElement nested)
            || nested.ValueKind != JsonValueKind.Object
            || !nested.TryGetProperty(valuePropertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubActivityRepository
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}
