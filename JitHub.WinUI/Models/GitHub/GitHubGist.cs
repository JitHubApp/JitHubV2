using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubGist
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string ApiUrl { get; set; } = string.Empty;

    [JsonPropertyName("public")]
    public bool Public { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [JsonPropertyName("files")]
    public Dictionary<string, GitHubGistFile> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("owner")]
    public GitHubActor? Owner { get; set; }

    [JsonPropertyName("comments")]
    public int Comments { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubGistFile
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("raw_url")]
    public string? RawUrl { get; set; }

    [JsonPropertyName("size")]
    public int Size { get; set; }

    [JsonPropertyName("content")]
    public string? Content { get; set; }

    [JsonPropertyName("truncated")]
    public bool Truncated { get; set; }
}

public sealed class GitHubGistFileWriteRequest
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

public sealed class GitHubGistFileUpdateRequest
{
    [JsonPropertyName("filename")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filename { get; set; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }
}

public sealed class GitHubGistCreateRequest
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("public")]
    public bool Public { get; set; }

    [JsonPropertyName("files")]
    public Dictionary<string, GitHubGistFileWriteRequest> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class GitHubGistUpdateRequest
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("files")]
    public Dictionary<string, GitHubGistFileUpdateRequest?> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
