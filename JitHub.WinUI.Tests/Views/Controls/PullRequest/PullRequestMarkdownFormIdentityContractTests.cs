using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views.Controls.PullRequest;

public sealed class PullRequestMarkdownFormIdentityContractTests
{
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
    public void ReviewCommentBlockResetsItsReplyFormIdentityOnRecycle()
    {
        // MarkdownForm's own peer rescoping and unique anonymous fallback are covered
        // behaviorally by MarkdownFormAutomationIdentityTests.
        string reviewBlock = File.ReadAllText(ReadPath(
            "JitHub.WinUI",
            "Views",
            "Controls",
            "PullRequest",
            "Conversation",
            "ReviewCommentBlock.xaml.cs"));

        Assert.Contains("oldViewModel.ReplyBoxRequested -= self.OnReplyBoxRequested", reviewBlock, StringComparison.Ordinal);
        Assert.Contains("viewModel.ReplyBoxRequested += self.OnReplyBoxRequested", reviewBlock, StringComparison.Ordinal);
        Assert.Contains("ReplyBox.StartBringIntoView", reviewBlock, StringComparison.Ordinal);
        Assert.Contains("ReplyMarkdownForm.AutomationInstanceId = null;", reviewBlock, StringComparison.Ordinal);
        Assert.Contains("Bindings.Update();", reviewBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveHarnessExercisesIdentitylessReplyFormsAndContainerRecreation()
    {
        string harness = File.ReadAllText(ReadPath("JitHub.WinUI.Automation", "Probes", "ProbeCatalog.cs"))
            + string.Concat(Directory
                .EnumerateFiles(ReadPath("JitHub.WinUI.Automation", "Probes", "PullRequests"), "*.cs")
                .Order(StringComparer.Ordinal)
                .Select(File.ReadAllText));
        string queryFixture = File.ReadAllText(ReadPath(
            "JitHub.Core",
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
