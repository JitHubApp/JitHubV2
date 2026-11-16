using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.Core.Tests.Services.GitHub;

public sealed class CommentInteractionContractTests
{
    [Fact]
    public void GitHubClientSupportsCommentEditingModerationAndReactions()
    {
        string service = File.ReadAllText(ReadPath("JitHub.Core", "Services", "GitHub", "GitHubClientService.cs"));

        Assert.Contains("UpdateIssueCommentAsync", service, StringComparison.Ordinal);
        Assert.Contains("DeleteIssueCommentAsync", service, StringComparison.Ordinal);
        Assert.Contains("issues/comments/{commentId}/pin", service, StringComparison.Ordinal);
        Assert.Contains("minimizeComment(input:", service, StringComparison.Ordinal);
        Assert.Contains("unminimizeComment(input:", service, StringComparison.Ordinal);
        Assert.Contains("UpdatePullRequestReviewCommentAsync", service, StringComparison.Ordinal);
        Assert.Contains("DeletePullRequestReviewCommentAsync", service, StringComparison.Ordinal);
        Assert.Contains("pulls/comments/{commentId}/reactions", service, StringComparison.Ordinal);
    }

    private static string ReadPath(params string[] segments) =>
        Path.Combine([FindRepositoryRoot(), .. segments]);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
