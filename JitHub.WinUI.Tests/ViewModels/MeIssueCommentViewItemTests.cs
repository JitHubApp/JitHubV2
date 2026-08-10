using JitHub.Models.GitHub;
using JitHub.WinUI.ViewModels.Common;
using JitHub.WinUI.ViewModels.Pages;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class MeIssueCommentViewItemTests
{
    [Fact]
    public void ApplySnapshot_UpdatesSameKeyCommentWithoutReplacingRowInstance()
    {
        GitHubIssueComment original = CreateComment("Original body", "octocat");
        KeyedObservableCollection<MeIssueCommentViewItem, GitHubIssueComment> comments = [];
        comments.ApplySnapshot(
            [original],
            MeIssueCommentViewItem.GetStableKey,
            static item => item.StableKey,
            static comment => new MeIssueCommentViewItem(comment),
            static (item, comment) => item.ApplyComment(comment));
        MeIssueCommentViewItem row = comments.Single();

        GitHubIssueComment edited = CreateComment("Edited body", "hubot");
        KeyedCollectionDiffResult diff = comments.ApplySnapshot(
            [edited],
            MeIssueCommentViewItem.GetStableKey,
            static item => item.StableKey,
            static comment => new MeIssueCommentViewItem(comment),
            static (item, comment) => item.ApplyComment(comment));

        Assert.Same(row, comments.Single());
        Assert.Equal(1, diff.Updated);
        Assert.Equal("Edited body", row.Body);
        Assert.Equal("hubot", row.AuthorDisplayName);
        Assert.Equal("hubot", row.AuthenticatedLogin);
    }

    [Fact]
    public void ApplyComment_IgnoresIdenticalProjection()
    {
        GitHubIssueComment comment = CreateComment("Unchanged", "octocat");
        MeIssueCommentViewItem row = new(comment);

        Assert.False(row.ApplyComment(CreateComment("Unchanged", "octocat")));
    }

    private static GitHubIssueComment CreateComment(string body, string login) => new()
    {
        Id = 42,
        Body = body,
        CreatedAt = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
        UpdatedAt = new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero),
        HtmlUrl = "https://github.com/octo/repo/issues/1#issuecomment-42",
        User = new GitHubActor { Login = login, AvatarUrl = $"https://avatars.example/{login}" }
    };
}
