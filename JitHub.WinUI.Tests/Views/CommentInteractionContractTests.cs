using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using JitHub.WinUI.Helpers;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class CommentInteractionContractTests
{
    [Fact]
    public void InlineReactionBarUsesNativeEmojiAndVisibleCounts()
    {
        XDocument xaml = XDocument.Load(ReadPath(
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "CommentInteractionBar.xaml"));

        Assert.Contains(xaml.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            (string?)element.Attribute("FontFamily") == "{ThemeResource AppEmojiFontFamily}" &&
            (string?)element.Attribute("Text") == "{x:Bind Emoji, Mode=OneWay}");
        Assert.Contains(xaml.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            (string?)element.Attribute("Text") == "{x:Bind Count, Mode=OneWay}");
        Assert.Contains(xaml.Descendants(), element => element.Name.LocalName == "ItemsWrapGrid");
        Assert.Contains(xaml.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute("Style") == "{StaticResource AppReactionChipButtonStyle}");
        Assert.Contains(xaml.Descendants(), element =>
            element.Name.LocalName == "Button" &&
            (string?)element.Attribute("Style") == "{StaticResource AppReactionPickerButtonStyle}");
        Assert.Contains(xaml.Descendants(), element =>
            element.Name.LocalName == "SymbolIcon" &&
            (string?)element.Attribute("Symbol") == "Emoji");
        Assert.Contains(xaml.Descendants(), element =>
            element.Name.LocalName == "Style" &&
            (string?)element.Attribute("BasedOn") == "{StaticResource AppReactionPickerFlyoutPresenterStyle}");
    }

    [Fact]
    public void InteractionMenuExposesGitHubActionsWithCapabilityGuards()
    {
        string control = File.ReadAllText(ReadPath(
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "CommentInteractionBar.xaml.cs"));

        foreach (string action in new[]
        {
            "QuoteReply",
            "CopyLink",
            "CopyMarkdown",
            "Edit",
            "Pin",
            "Unpin",
            "Hide",
            "Unhide",
            "Delete"
        })
        {
            Assert.Contains(action, control, StringComparison.Ordinal);
        }

        Assert.Contains("TargetKind == CommentTargetKind.IssueComment && CanModerate", control, StringComparison.Ordinal);
        Assert.Contains("bool isBody = TargetKind is CommentTargetKind.Issue or CommentTargetKind.PullRequest", control, StringComparison.Ordinal);
        Assert.Contains("bool canEdit = CanEdit || isAuthor", control, StringComparison.Ordinal);
        Assert.Contains("EditItem.Visibility = canEdit", control, StringComparison.Ordinal);
        Assert.Contains("bool canDelete = !isBody && (isAuthor || CanModerate)", control, StringComparison.Ordinal);
        Assert.Contains("bool canHide = !isBody && CanModerate && !string.IsNullOrWhiteSpace(NodeId)", control, StringComparison.Ordinal);
        Assert.Contains("UnhideItem.Visibility = canHide && IsMinimized", control, StringComparison.Ordinal);
        Assert.Contains("ManagementSeparator.Visibility = hasManagementAction || canDelete", control, StringComparison.Ordinal);
        Assert.Contains("DeleteSeparator.Visibility = canDelete && hasManagementAction", control, StringComparison.Ordinal);
        Assert.DoesNotContain("IsMinimized || TargetKind", control, StringComparison.Ordinal);
    }

    [Fact]
    public void BodiesCommentsAndReviewRepliesRenderTheSharedInteractionBar()
    {
        XDocument issue = XDocument.Load(ReadPath(
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Issue",
            "RepoIssueDetailPane.xaml"));
        XDocument pullRequest = XDocument.Load(ReadPath(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml"));

        Assert.Contains(issue.Descendants(), element =>
            element.Name.LocalName == "CommentInteractionBar" &&
            (string?)element.Attribute("TargetKind") == "Issue" &&
            (string?)element.Attribute("Reactions") == "{x:Bind ViewModel.SelectedIssueReactions, Mode=OneWay}");
        Assert.Contains(issue.Descendants(), element =>
            element.Name.LocalName == "CommentInteractionBar" &&
            (string?)element.Attribute("TargetKind") == "IssueComment");

        XElement[] pullRequestBars = pullRequest.Descendants()
            .Where(element => element.Name.LocalName == "CommentInteractionBar")
            .ToArray();
        Assert.Contains(pullRequestBars, element =>
            (string?)element.Attribute("TargetKind") == "PullRequest" &&
            (string?)element.Attribute("Reactions") == "{Binding PullRequestReactions}");
        Assert.Contains(pullRequestBars, element =>
            (string?)element.Attribute("TargetKind") == "PullRequestComment");
        Assert.True(pullRequestBars.Count(element =>
            (string?)element.Attribute("TargetKind") == "PullRequestReviewComment") >= 2);
        Assert.Contains(pullRequest.Descendants(), element =>
            element.Name.LocalName == "ItemsControl" &&
            (string?)element.Attribute("ItemsSource") == "{x:Bind Replies, Mode=OneWay}");

        string issueSource = issue.ToString();
        string pullRequestSource = pullRequest.ToString();
        Assert.DoesNotContain("IssueReactionsButton_Click", issueSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RepoIssuesReactionsButton", issueSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PullRequestReactionsButton_Click", pullRequestSource, StringComparison.Ordinal);
        Assert.DoesNotContain("RepoPullRequestsReactionsButton", pullRequestSource, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubClientSupportsCommentEditingModerationAndReactions()
    {
        string service = File.ReadAllText(ReadPath("JitHub.WinUI", "Services", "GitHubClientService.cs"));

        Assert.Contains("UpdateIssueCommentAsync", service, StringComparison.Ordinal);
        Assert.Contains("DeleteIssueCommentAsync", service, StringComparison.Ordinal);
        Assert.Contains("issues/comments/{commentId}/pin", service, StringComparison.Ordinal);
        Assert.Contains("minimizeComment(input:", service, StringComparison.Ordinal);
        Assert.Contains("unminimizeComment(input:", service, StringComparison.Ordinal);
        Assert.Contains("UpdatePullRequestReviewCommentAsync", service, StringComparison.Ordinal);
        Assert.Contains("DeletePullRequestReviewCommentAsync", service, StringComparison.Ordinal);
        Assert.Contains("pulls/comments/{commentId}/reactions", service, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewDataExercisesReactionCountsAndNestedReviewReplies()
    {
        string issues = File.ReadAllText(ReadPath(
            "JitHub.WinUI",
            "Services",
            "Issues",
            "GitHubIssueQueryService.cs"));
        string pullRequests = File.ReadAllText(ReadPath(
            "JitHub.WinUI",
            "Services",
            "PullRequests",
            "GitHubPullRequestQueryService.cs"));

        Assert.Contains("PlusOne = 3", issues, StringComparison.Ordinal);
        Assert.Contains("PlusOne = 4", pullRequests, StringComparison.Ordinal);
        Assert.Contains("InReplyToId = 200", pullRequests, StringComparison.Ordinal);
        Assert.Contains("Hooray = 1", pullRequests, StringComparison.Ordinal);
        Assert.Contains("_commentMinimizationOverrides", File.ReadAllText(ReadPath(
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "RepoPullRequestPageViewModel.cs")), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "First line", "> First line\n\n")]
    [InlineData("Existing", "First\r\n\r\nThird", "Existing\n\n> First\n>\n> Third\n\n")]
    public void QuoteReplyAppendsMarkdownBlockQuote(string? draft, string body, string expected)
    {
        Assert.Equal(expected, CommentMarkdownFormatter.AppendQuote(draft, body));
    }

    private static string ReadPath(params string[] segments) =>
        Path.Combine([FindRepositoryRoot(), .. segments]);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "JitHub.slnx")))
        {
            current = current.Parent;
        }

        return current?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
