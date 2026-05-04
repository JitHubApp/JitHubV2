using System.Text.Json.Serialization;

namespace JitHub.Models.GitHub;

public sealed class GitHubCompareResult
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("ahead_by")]
    public int AheadBy { get; set; }

    [JsonPropertyName("behind_by")]
    public int BehindBy { get; set; }

    [JsonPropertyName("total_commits")]
    public int TotalCommits { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("permalink_url")]
    public string? PermalinkUrl { get; set; }

    [JsonPropertyName("diff_url")]
    public string? DiffUrl { get; set; }

    [JsonPropertyName("patch_url")]
    public string? PatchUrl { get; set; }

    [JsonPropertyName("base_commit")]
    public GitHubCommit? BaseCommit { get; set; }

    [JsonPropertyName("merge_base_commit")]
    public GitHubCommit? MergeBaseCommit { get; set; }

    [JsonPropertyName("commits")]
    public GitHubCommit[] Commits { get; set; } = [];

    [JsonPropertyName("files")]
    public GitHubCommitFile[] Files { get; set; } = [];
}
