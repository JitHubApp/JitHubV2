using JitHub.Models.GitHub;
using JitHub.Services;
using System;
using System.Net;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class IssuePermissionPolicyTests
{
    [Fact]
    public void OrdinaryForbiddenResponseIsPermissionDenied()
    {
        Assert.True(IssuePermissionPolicy.IsPermissionDenied(
            new GitHubApiException(HttpStatusCode.Forbidden, "Forbidden")));
    }

    [Fact]
    public void RateLimitForbiddenResponseIsNotPermissionDenied()
    {
        Assert.False(IssuePermissionPolicy.IsPermissionDenied(
            new GitHubRateLimitException(HttpStatusCode.Forbidden, "Rate limited", TimeSpan.FromSeconds(30))));
    }

    [Fact]
    public void PublicPreviewAndArchivedRepositoriesAreReadOnly()
    {
        GitHubRepository repository = CreateRepository();
        GitHubIssue issue = CreateIssue("viewer");

        IssueViewerCapabilities preview = IssuePermissionPolicy.Evaluate(
            repository, issue, "viewer", isAuthenticated: true, isPublicPreview: true);
        repository.Archived = true;
        IssueViewerCapabilities archived = IssuePermissionPolicy.Evaluate(
            repository, issue, "viewer", isAuthenticated: true, isPublicPreview: false);

        Assert.False(preview.CanCreateIssue);
        Assert.False(preview.CanComment);
        Assert.False(archived.CanEditIssue);
        Assert.False(archived.CanReact);
    }

    [Fact]
    public void IssueAuthorCanEditAndCommentButCannotManageMetadata()
    {
        IssueViewerCapabilities result = IssuePermissionPolicy.Evaluate(
            CreateRepository(),
            CreateIssue("viewer"),
            "viewer",
            isAuthenticated: true,
            isPublicPreview: false);

        Assert.True(result.CanCreateIssue);
        Assert.True(result.CanEditIssue);
        Assert.True(result.CanChangeState);
        Assert.True(result.CanComment);
        Assert.False(result.CanManageMetadata);
    }

    [Fact]
    public void TriagePermissionCanManageMetadataButNotRewriteAnotherAuthorsBody()
    {
        GitHubRepository repository = CreateRepository();
        repository.Permissions = new GitHubRepositoryPermissions { Triage = true, Pull = true };

        IssueViewerCapabilities result = IssuePermissionPolicy.Evaluate(
            repository,
            CreateIssue("author"),
            "viewer",
            isAuthenticated: true,
            isPublicPreview: false);

        Assert.True(result.CanManageMetadata);
        Assert.True(result.CanChangeState);
        Assert.False(result.CanEditIssue);
    }

    [Fact]
    public void LockedIssueAllowsParticipationOnlyWithWritePermission()
    {
        GitHubIssue issue = CreateIssue("author");
        issue.Locked = true;
        GitHubRepository repository = CreateRepository();

        IssueViewerCapabilities reader = IssuePermissionPolicy.Evaluate(
            repository, issue, "viewer", true, false);
        repository.Permissions = new GitHubRepositoryPermissions { Push = true };
        IssueViewerCapabilities writer = IssuePermissionPolicy.Evaluate(
            repository, issue, "viewer", true, false);

        Assert.False(reader.CanComment);
        Assert.False(reader.CanReact);
        Assert.True(writer.CanComment);
        Assert.True(writer.CanReact);
    }

    [Fact]
    public void RepositoryWithIssuesDisabledCannotCreateButExistingIssueRemainsActionable()
    {
        GitHubRepository repository = CreateRepository();
        repository.HasIssues = false;
        repository.Permissions = new GitHubRepositoryPermissions { Push = true };

        IssueViewerCapabilities result = IssuePermissionPolicy.Evaluate(
            repository, CreateIssue("author"), "viewer", true, false);

        Assert.False(result.CanCreateIssue);
        Assert.True(result.CanEditIssue);
        Assert.True(result.CanManageMetadata);
    }

    [Fact]
    public void DeletedIssueAuthorIsPassiveAndDoesNotGainAuthorCapabilities()
    {
        GitHubIssue issue = CreateIssue("author");
        issue.User = null!;

        IssueViewerCapabilities result = IssuePermissionPolicy.Evaluate(
            CreateRepository(), issue, "viewer", true, false);

        Assert.False(result.CanEditIssue);
        Assert.False(result.CanChangeState);
        Assert.True(result.CanComment);
    }

    private static GitHubRepository CreateRepository() =>
        new()
        {
            Name = "app",
            FullName = "octo/app",
            HasIssues = true,
            Owner = new GitHubRepositoryOwner { Login = "octo" }
        };

    private static GitHubIssue CreateIssue(string author) =>
        new()
        {
            Number = 17,
            Title = "Issue",
            State = "open",
            User = new GitHubActor { Login = author }
        };
}
