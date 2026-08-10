using System;
using System.IO;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepoIssueProgressiveLoadingContractTests
{
    [Fact]
    public void RepositoryIssueList_UsesProgressiveCancellationAwareKeyedPublication()
    {
        string source = ReadViewModel();

        Assert.Contains("GetAllIssuesProgressivelyAsync(", source, StringComparison.Ordinal);
        Assert.Contains("ApplyProgressiveIssueListAsync(", source, StringComparison.Ordinal);
        Assert.Contains("KeyedObservableReconciler.ApplySnapshot(", source, StringComparison.Ordinal);
        Assert.Contains("int selectionToPreserve = SelectedIssue?.Number ?? preferredIssueNumber;", source, StringComparison.Ordinal);
        Assert.Contains("issueNumberToSelect = SelectedIssue?.Number ?? issueNumberToSelect;", source, StringComparison.Ordinal);
        Assert.Contains("BeginListLoad()", source, StringComparison.Ordinal);
        Assert.Contains("CompleteListLoad(listLoad)", source, StringComparison.Ordinal);
        Assert.Contains("catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryIssueDetail_PublishesBodyBeforeProgressiveComments()
    {
        string source = ReadViewModel();
        int showIssueStart = source.IndexOf(
            "private async Task ShowIssueAsync(GitHubIssue? issue, bool preserveCurrentState",
            StringComparison.Ordinal);
        int showIssueEnd = source.IndexOf(
            "private Task ApplyProgressiveIssueCommentsAsync(",
            showIssueStart,
            StringComparison.Ordinal);
        Assert.True(showIssueStart >= 0 && showIssueEnd > showIssueStart);

        string showIssue = source[showIssueStart..showIssueEnd];
        int issueRead = showIssue.IndexOf("GetIssueAsync(", StringComparison.Ordinal);
        int bodyPublication = showIssue.IndexOf("PopulateIssue(displayIssue);", StringComparison.Ordinal);
        int commentRead = showIssue.IndexOf("GetAllIssueCommentsProgressivelyAsync(", StringComparison.Ordinal);
        Assert.True(issueRead >= 0);
        Assert.True(bodyPublication > issueRead);
        Assert.True(commentRead > bodyPublication);
        Assert.DoesNotContain("Task.WhenAll(issueTask, commentsTask)", showIssue, StringComparison.Ordinal);
        Assert.Contains("ApplyIssueComments(comments);", source, StringComparison.Ordinal);
        Assert.Contains("AreIssueCommentSnapshotsEquivalent", source, StringComparison.Ordinal);
        Assert.Contains("BeginDetailLoad()", source, StringComparison.Ordinal);
        Assert.Contains("CompleteDetailLoad(detailLoad)", source, StringComparison.Ordinal);
    }

    private static string ReadViewModel()
    {
        string root = FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "RepoIssuePageViewModel.cs"));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "JitHub.WinUI")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
