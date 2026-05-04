using System;

namespace JitHub.Models.GitHub;

public sealed class GitHubIssueQueryOptions
{
    public string State { get; set; } = "open";

    public string Sort { get; set; } = "updated";

    public string Direction { get; set; } = "desc";

    public DateTimeOffset? Since { get; set; }

    public string? Labels { get; set; }

    public string? Milestone { get; set; }

    public string? Assignee { get; set; }

    public string? Creator { get; set; }

    public string? Mentioned { get; set; }

    public string? Filter { get; set; }
}

public sealed class GitHubPullRequestQueryOptions
{
    public string State { get; set; } = "open";

    public string Sort { get; set; } = "updated";

    public string Direction { get; set; } = "desc";

    public string? Head { get; set; }

    public string? Base { get; set; }
}
