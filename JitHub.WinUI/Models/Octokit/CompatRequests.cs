using System;
using System.Collections.Generic;

namespace Octokit;

public class ApiOptions
{
    public int? StartPage { get; set; }

    public int? PageCount { get; set; }

    public int? PageSize { get; set; }
}

public class RequestParameters : ApiOptions
{
}

public class RepositoryIssueRequest : RequestParameters
{
    public ItemStateFilter State { get; set; } = ItemStateFilter.Open;

    public IssueSort SortProperty { get; set; } = IssueSort.Created;

    public SortDirection SortDirection { get; set; } = SortDirection.Descending;

    public DateTimeOffset? Since { get; set; }

    public ICollection<string> Labels { get; set; } = [];

    public string Milestone { get; set; } = string.Empty;

    public string Assignee { get; set; } = string.Empty;

    public string Creator { get; set; } = string.Empty;

    public string Mentioned { get; set; } = string.Empty;

    public IssueFilter Filter { get; set; } = IssueFilter.Assigned;
}

public class IssueRequest : RequestParameters
{
    public ItemStateFilter State { get; set; } = ItemStateFilter.Open;

    public IssueSort SortProperty { get; set; } = IssueSort.Created;

    public SortDirection SortDirection { get; set; } = SortDirection.Descending;

    public DateTimeOffset? Since { get; set; }

    public ICollection<string> Labels { get; set; } = [];

    public IssueFilter Filter { get; set; } = IssueFilter.Assigned;
}

public class PullRequestRequest : RequestParameters
{
    public ItemStateFilter State { get; set; } = ItemStateFilter.Open;

    public PullRequestSort SortProperty { get; set; } = PullRequestSort.Created;

    public SortDirection SortDirection { get; set; } = SortDirection.Descending;

    public string Head { get; set; } = string.Empty;

    public string Base { get; set; } = string.Empty;
}

public class CommitRequest : RequestParameters
{
    public string Author { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public string Sha { get; set; } = string.Empty;

    public DateTimeOffset? Since { get; set; }

    public DateTimeOffset? Until { get; set; }
}

public class CheckRunRequest : RequestParameters
{
    public string CheckName { get; set; } = string.Empty;

    public StringEnum<CheckRunCompletedAtFilter>? Filter { get; set; }

    public StringEnum<CheckStatusFilter>? Status { get; set; }
}

public class SearchRepositoriesRequest : RequestParameters
{
    public SearchRepositoriesRequest()
    {
    }

    public SearchRepositoriesRequest(string term)
    {
        Term = term;
    }

    public string Term { get; set; } = string.Empty;

    public string User { get; set; } = string.Empty;

    public int Page { get; set; } = 1;

    public int PerPage { get; set; } = 100;

    public SortDirection Order { get; set; } = SortDirection.Descending;

    public string Sort { get; set; } = string.Empty;
}

public class StarredRequest : RequestParameters
{
    public StarredSort SortProperty { get; set; } = StarredSort.Created;

    public SortDirection SortDirection { get; set; } = SortDirection.Ascending;
}

public class NewRepository
{
    public NewRepository(string name)
    {
        Name = name;
    }

    public string Name { get; set; }

    public string Description { get; set; } = string.Empty;

    public string Homepage { get; set; } = string.Empty;

    public bool? AutoInit { get; set; }

    public bool? Private { get; set; }

    public string GitignoreTemplate { get; set; } = string.Empty;

    public bool? HasIssues { get; set; }

    public bool? HasProjects { get; set; }

    public bool? HasWiki { get; set; }

    public string LicenseTemplate { get; set; } = string.Empty;

    public RepositoryVisibility? Visibility { get; set; }
}

public class NewRepositoryFork
{
    public string Organization { get; set; } = string.Empty;
}

public class IssueUpdate
{
    public ICollection<string> Assignees { get; set; } = [];

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public ICollection<string> Labels { get; set; } = [];

    public int? Milestone { get; set; }

    public global::Octokit.ItemState? State { get; set; }
}

public class NewPullRequest
{
    public NewPullRequest(string title, string head, string @base)
    {
        Title = title;
        Head = head;
        Base = @base;
    }

    public string Title { get; set; }

    public string Head { get; set; }

    public string Base { get; set; }

    public string Body { get; set; } = string.Empty;

    public bool? Draft { get; set; }

    public long? IssueId { get; set; }

    public bool? MaintainerCanModify { get; set; }
}

public class MergePullRequest
{
    public string CommitMessage { get; set; } = string.Empty;

    public string CommitTitle { get; set; } = string.Empty;

    public PullRequestMergeMethod? MergeMethod { get; set; }

    public string Sha { get; set; } = string.Empty;
}

public class PullRequestUpdate
{
    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string Base { get; set; } = string.Empty;

    public bool? MaintainerCanModify { get; set; }

    public global::Octokit.ItemState? State { get; set; }
}
