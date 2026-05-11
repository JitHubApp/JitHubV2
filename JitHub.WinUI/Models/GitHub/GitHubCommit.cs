using System;
using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubCommit
{
    [JsonPropertyName("node_id")]
    public string? NodeId { get; set; }

    [JsonPropertyName("sha")]
    public string Sha { get; set; } = string.Empty;

    [JsonPropertyName("html_url")]
    public string HtmlUrl { get; set; } = string.Empty;

    [JsonPropertyName("commit")]
    public GitHubCommitInfo Commit { get; set; } = new();

    [JsonPropertyName("author")]
    public GitHubActor? Author { get; set; }

    [JsonPropertyName("committer")]
    public GitHubActor? Committer { get; set; }

    [JsonPropertyName("stats")]
    public GitHubCommitStats? Stats { get; set; }

    [JsonPropertyName("files")]
    public GitHubCommitFile[] Files { get; set; } = [];

    [JsonIgnore]
    public string SummaryMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Commit.Message))
            {
                return "(No commit message)";
            }

            string[] lines = Commit.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0])
                ? lines[0]
                : "(No commit message)";
        }
    }

    [JsonIgnore]
    public string AuthorDisplayName => Commit.Author.Name ?? Author?.Login ?? "Unknown author";

    [JsonIgnore]
    public string TimestampDisplayText => Commit.Author.Date?.LocalDateTime.ToString("g") ?? "Unknown time";

    [JsonIgnore]
    public string ShortSha => Sha[..Math.Min(7, Sha.Length)];
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubCommitInfo
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("comment_count")]
    public int CommentCount { get; set; }

    [JsonPropertyName("author")]
    public GitHubCommitSignature Author { get; set; } = new();

    [JsonPropertyName("committer")]
    public GitHubCommitSignature Committer { get; set; } = new();
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubCommitSignature
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("date")]
    public DateTimeOffset? Date { get; set; }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubCommitStats
{
    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("additions")]
    public int Additions { get; set; }

    [JsonPropertyName("deletions")]
    public int Deletions { get; set; }

    [JsonIgnore]
    public string SummaryText => $"+{Additions}  -{Deletions}  ({Total} changes)";
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class GitHubCommitFile
{
    [JsonPropertyName("sha")]
    public string? Sha { get; set; }

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("additions")]
    public int Additions { get; set; }

    [JsonPropertyName("deletions")]
    public int Deletions { get; set; }

    [JsonPropertyName("changes")]
    public int Changes { get; set; }

    [JsonPropertyName("blob_url")]
    public string? BlobUrl { get; set; }

    [JsonPropertyName("raw_url")]
    public string? RawUrl { get; set; }

    [JsonPropertyName("contents_url")]
    public string? ContentsUrl { get; set; }

    [JsonPropertyName("patch")]
    public string? Patch { get; set; }

    [JsonPropertyName("previous_filename")]
    public string? PreviousFilename { get; set; }

    [JsonIgnore]
    public string DisplayStatus => string.IsNullOrWhiteSpace(Status) ? "modified" : Status!;

    [JsonIgnore]
    public string ChangeSummary => $"+{Additions}  -{Deletions}  ({Changes} changes)";

    [JsonIgnore]
    public string PatchDisplayText => string.IsNullOrWhiteSpace(Patch) ? "Binary file or diff unavailable for this file." : Patch!;

    [JsonIgnore]
    public bool HasPreviousFilename => !string.IsNullOrWhiteSpace(PreviousFilename);

    [JsonIgnore]
    public string PreviousFilenameDisplayText => string.IsNullOrWhiteSpace(PreviousFilename) ? string.Empty : $"Renamed from {PreviousFilename}";

    [JsonIgnore]
    public string HeaderText => HasPreviousFilename ? $"{Filename} (from {PreviousFilename})" : Filename;

    [JsonIgnore]
    public string MetadataText => $"{DisplayStatus}  •  {ChangeSummary}";
}
