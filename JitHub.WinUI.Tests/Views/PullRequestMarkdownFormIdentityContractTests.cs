using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class PullRequestMarkdownFormIdentityContractTests
{
    [Fact]
    public void SimultaneousReviewThreadsUseDistinctStableReplyFormScopes()
    {
        string first = CreateScope(id: 101, nodeId: "PRRC_first") + "_ReplyForm";
        string second = CreateScope(id: 102, nodeId: "PRRC_second") + "_ReplyForm";
        string recycledFirst = CreateScope(id: 101, nodeId: "PRRC_first") + "_ReplyForm";

        Assert.NotEqual(first, second);
        Assert.Equal(first, recycledFirst);
        Assert.EndsWith("_ReplyForm", first, StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroIdReviewThreadsUseStableGitHubFallbackIdentity()
    {
        string first = CreateScope(id: 0, nodeId: "PRRC_zero_first") + "_ReplyForm";
        string second = CreateScope(id: 0, nodeId: "PRRC_zero_second") + "_ReplyForm";
        string recycledFirst = CreateScope(id: 0, nodeId: "PRRC_zero_first") + "_ReplyForm";

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.NotEqual(first, second);
        Assert.Equal(first, recycledFirst);
    }

    [Fact]
    public void NullNodeIdentityFallsBackToStableReviewCoordinates()
    {
        DateTimeOffset createdAt = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);
        string first = PullRequestReviewAutomationIdentity.CreateScope(
            "PullRequestReviewThread",
            id: 0,
            nodeId: null,
            reviewId: 11,
            position: null,
            originalPosition: 7,
            createdAt);
        string recycled = PullRequestReviewAutomationIdentity.CreateScope(
            "PullRequestReviewThread",
            id: 0,
            nodeId: null,
            reviewId: 11,
            position: null,
            originalPosition: 7,
            createdAt);
        string other = PullRequestReviewAutomationIdentity.CreateScope(
            "PullRequestReviewThread",
            id: 0,
            nodeId: null,
            reviewId: 12,
            position: null,
            originalPosition: 7,
            createdAt);

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, recycled);
        Assert.NotEqual(first, other);
    }

    [Fact]
    public void FullyIdentitylessReviewCommentsUseStableOwnerOrdinalScopes()
    {
        string first = CreateIdentitylessScope("pr:42:thread:0");
        string recreatedFirst = CreateIdentitylessScope("pr:42:thread:0");
        string second = CreateIdentitylessScope("pr:42:thread:1");

        Assert.False(string.IsNullOrWhiteSpace(first));
        Assert.Equal(first, recreatedFirst);
        Assert.NotEqual(first, second);
        Assert.DoesNotContain("pr_42", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SimultaneousNullZeroIdReplyFormsStayUniqueAcrossRecreation()
    {
        string[] initial =
        [
            $"{CreateIdentitylessScope("pr:73:thread:0")}_ReplyForm",
            $"{CreateIdentitylessScope("pr:73:thread:1")}_ReplyForm"
        ];
        string[] recreated =
        [
            $"{CreateIdentitylessScope("pr:73:thread:0")}_ReplyForm",
            $"{CreateIdentitylessScope("pr:73:thread:1")}_ReplyForm"
        ];

        Assert.Equal(2, initial.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(initial, recreated);
    }

    [Fact]
    public void FullyIdentitylessScopeRequiresCallerOwnedContext()
    {
        Assert.Throws<ArgumentException>(() => PullRequestReviewAutomationIdentity.CreateScope(
            "PullRequestReviewThread",
            id: 0,
            nodeId: null,
            reviewId: null,
            position: null,
            originalPosition: null,
            createdAt: default));
    }

    [Fact]
    public void SectionProjectionPreservesSimultaneousIdentitylessReviewCommentsByStableOrdinal()
    {
        GitHubPullRequestReviewComment[] comments =
        [
            new() { Id = 0, NodeId = null },
            new() { Id = 0, NodeId = null }
        ];
        PullRequestSectionState complete = new(
            CacheState.Fresh,
            Completeness: PagedDataCompleteness.Complete);

        GitHubPullRequestReviewComment[] projected = PullRequestSectionProjectionPolicy.ProjectSection(
            comments,
            [],
            complete,
            static (_, ordinal) => $"pr:73:review-comment:{ordinal}");

        Assert.Equal(2, projected.Length);
        Assert.Same(comments[0], projected[0]);
        Assert.Same(comments[1], projected[1]);
    }

    [Fact]
    public void RepeatedPullRequestReplyFormsBindTheirItemScopedIdentity()
    {
        XDocument page = XDocument.Load(ReadPath(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml"));
        XElement repeatedForm = Assert.Single(page.Descendants(), element =>
                element.Name.LocalName == "MarkdownForm" &&
                string.Equals(
                    element.Attribute("AutomationInstanceId")?.Value,
                    "{x:Bind ReplyFormAutomationId, Mode=OneWay}",
                    StringComparison.Ordinal));

        Assert.Contains(
            repeatedForm.Ancestors(),
            ancestor => ancestor.Name.LocalName == "DataTemplate");

        XDocument block = XDocument.Load(ReadPath(
            "JitHub.WinUI",
            "Views",
            "Controls",
            "PullRequest",
            "Conversation",
            "ReviewCommentBlock.xaml"));
        XElement blockForm = Assert.Single(
            block.Descendants(),
            element => element.Name.LocalName == "MarkdownForm");
        Assert.Equal("ReplyMarkdownForm", blockForm.Attributes().Single(attribute =>
            attribute.Name.LocalName == "Name").Value);
        Assert.Contains(
            "ViewModel.ReplyFormAutomationId",
            blockForm.Attribute("AutomationInstanceId")?.Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownFormRescopesEveryInteractivePeerAndHasUniqueNullFallback()
    {
        string source = File.ReadAllText(ReadPath(
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownForm.xaml.cs"));
        string reviewBlock = File.ReadAllText(ReadPath(
            "JitHub.WinUI",
            "Views",
            "Controls",
            "PullRequest",
            "Conversation",
            "ReviewCommentBlock.xaml.cs"));

        Assert.Contains("Interlocked.Increment(ref _nextFallbackAutomationIdentity)", source, StringComparison.Ordinal);
        Assert.Contains("OnAutomationInstanceIdChanged", source, StringComparison.Ordinal);
        Assert.Contains("form.ApplyAutomationIdentity();", source, StringComparison.Ordinal);
        foreach (string suffix in new[] { "_Mode", "_Mode_Write", "_Mode_Preview", "_Editor", "_Preview" })
        {
            Assert.Contains($"{{prefix}}{suffix}", source, StringComparison.Ordinal);
        }

        Assert.Contains("oldViewModel.ReplyBoxRequested -= self.OnReplyBoxRequested", reviewBlock, StringComparison.Ordinal);
        Assert.Contains("viewModel.ReplyBoxRequested += self.OnReplyBoxRequested", reviewBlock, StringComparison.Ordinal);
        Assert.Contains("ReplyBox.StartBringIntoView", reviewBlock, StringComparison.Ordinal);
        Assert.Contains("ReplyMarkdownForm.AutomationInstanceId = null;", reviewBlock, StringComparison.Ordinal);
        Assert.Contains("Bindings.Update();", reviewBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveHarnessExercisesIdentitylessReplyFormsAndContainerRecreation()
    {
        string harness = File.ReadAllText(ReadPath("JitHub.WinUI.Automation", "Program.cs"));
        string queryFixture = File.ReadAllText(ReadPath(
            "JitHub.WinUI",
            "Services",
            "PullRequests",
            "GitHubPullRequestQueryService.cs"));

        Assert.Contains("pull-request-reply-identities", harness, StringComparison.Ordinal);
        Assert.Contains("--scenario=pr-reply-identities", harness, StringComparison.Ordinal);
        Assert.Contains("PullRequestReviewThread_context_", harness, StringComparison.Ordinal);
        Assert.Contains("initialEditors.SequenceEqual(recreatedEditors", harness, StringComparison.Ordinal);
        Assert.Contains("RepoDetailCompactCommandsButton", harness, StringComparison.Ordinal);
        Assert.DoesNotContain("RepoDetailActionsMenuButton", harness, StringComparison.Ordinal);
        Assert.Contains("IsReplyIdentityAutomationScenario", queryFixture, StringComparison.Ordinal);
        Assert.Contains("User = null!", queryFixture, StringComparison.Ordinal);
        Assert.Contains("Id = 0", queryFixture, StringComparison.Ordinal);
    }

    private static string CreateScope(long id, string? nodeId) =>
        PullRequestReviewAutomationIdentity.CreateScope(
            "PullRequestReviewThread",
            id,
            nodeId,
            reviewId: 9,
            position: 4,
            originalPosition: 4,
            new DateTimeOffset(2026, 7, 29, 12, 0, 0, TimeSpan.Zero));

    private static string CreateIdentitylessScope(string deterministicContext) =>
        PullRequestReviewAutomationIdentity.CreateScope(
            "PullRequestReviewThread",
            id: 0,
            nodeId: null,
            reviewId: null,
            position: null,
            originalPosition: null,
            createdAt: default,
            deterministicContext);

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
