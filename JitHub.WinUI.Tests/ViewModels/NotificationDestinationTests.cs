using JitHub.Models.GitHub;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.ViewModels;

public sealed class NotificationDestinationTests
{
    [Theory]
    [InlineData("Issue", "issues", "42", NotificationInternalDestinationKind.Issue, 42, "")]
    [InlineData("PullRequest", "pulls", "37", NotificationInternalDestinationKind.PullRequest, 37, "")]
    [InlineData("Commit", "commits", "0123456789abcdef0123456789abcdef01234567", NotificationInternalDestinationKind.Commit, 0, "0123456789abcdef0123456789abcdef01234567")]
    public void ResolveInternalNotification_UsesValidatedGitHubApiDestination(
        string type,
        string resource,
        string identifier,
        NotificationInternalDestinationKind expectedKind,
        int expectedNumber,
        string expectedGitRef)
    {
        GitHubNotificationThread notification = Notification(
            type,
            $"https://api.github.com/repos/JitHubApp/JitHubV2/{resource}/{identifier}");

        bool resolved = NotificationDestinationPolicy.TryResolveInternal(notification, out NotificationInternalDestination destination);

        Assert.True(resolved);
        Assert.Equal(expectedKind, destination.Kind);
        Assert.Equal(expectedNumber, destination.Number);
        Assert.Equal(expectedGitRef, destination.GitRef);
    }

    [Theory]
    [InlineData("CheckSuite", "https://api.github.com/repos/JitHubApp/JitHubV2/check-suites/987654")]
    [InlineData("Commit", "https://api.github.com/repos/JitHubApp/JitHubV2/commits/not-a-sha")]
    [InlineData("Issue", "https://api.github.com/repos/Other/Repository/issues/42")]
    [InlineData("PullRequest", "https://example.test/repos/JitHubApp/JitHubV2/pulls/37")]
    public void ResolveInternalNotification_RejectsFabricatedOrMismatchedDestinations(string type, string apiUrl)
    {
        Assert.False(NotificationDestinationPolicy.TryResolveInternal(Notification(type, apiUrl), out _));
    }

    [Theory]
    [InlineData("Release", "https://api.github.com/repos/JitHubApp/JitHubV2/releases/8", "https://github.com/JitHubApp/JitHubV2/releases")]
    [InlineData("Discussion", "https://api.github.com/repos/JitHubApp/JitHubV2/discussions/42", "https://github.com/JitHubApp/JitHubV2/discussions/42")]
    [InlineData("WorkflowRun", "https://api.github.com/repos/JitHubApp/JitHubV2/actions/runs/17", "https://github.com/JitHubApp/JitHubV2/actions/runs/17")]
    [InlineData("Repository", "https://api.github.com/repos/JitHubApp/JitHubV2", "https://github.com/JitHubApp/JitHubV2")]
    [InlineData("CheckSuite", "https://api.github.com/repos/JitHubApp/JitHubV2/check-suites/987654", "https://github.com/JitHubApp/JitHubV2/actions")]
    [InlineData("RepositoryInvitation", "https://api.github.com/user/repository_invitations/4", "https://github.com/notifications")]
    [InlineData("UnknownType", null, "https://github.com/notifications")]
    public void ResolveNotificationWebUri_UsesTypeSpecificGitHubDestination(
        string type,
        string? apiUrl,
        string expected)
    {
        GitHubNotificationThread notification = Notification(type, apiUrl);

        Assert.Equal(expected, NotificationDestinationPolicy.ResolveWebUri(notification)?.AbsoluteUri.TrimEnd('/'));
    }

    [Theory]
    [InlineData("https://api.github.com/repos/JitHubApp/JitHubV2/check-suites/987654")]
    [InlineData(null)]
    public void ResolveNotificationWebUri_CheckSuiteNeverTreatsSuiteIdAsCommitSha(string? apiUrl)
    {
        GitHubNotificationThread notification = Notification("CheckSuite", apiUrl);

        Uri? destination = NotificationDestinationPolicy.ResolveWebUri(notification);

        Assert.Equal("https://github.com/JitHubApp/JitHubV2/actions", destination?.AbsoluteUri.TrimEnd('/'));
        Assert.DoesNotContain("987654", destination?.AbsoluteUri ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("/commit/", destination?.AbsoluteUri ?? string.Empty, StringComparison.Ordinal);
    }

    private static GitHubNotificationThread Notification(string type, string? apiUrl) => new()
    {
        Repository = new GitHubRepository { FullName = "JitHubApp/JitHubV2" },
        Subject = new GitHubNotificationSubject { Type = type, Url = apiUrl }
    };
}
