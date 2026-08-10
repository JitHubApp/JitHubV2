using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class AvatarIdentityContractTests
{
    [Fact]
    public void SharedAvatar_ExposesNativeProfileInvocationContract()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "Common", "Avatar.xaml"));
        string code = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "Common", "Avatar.xaml.cs"));

        Assert.Contains("AppIdentityButtonStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.AutomationId", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name", xaml, StringComparison.Ordinal);
        Assert.Contains("ProfileButton_Click", xaml, StringComparison.Ordinal);
        Assert.Contains("ProfileButton_KeyDown", xaml, StringComparison.Ordinal);
        Assert.Contains("ProfileButton.PointerEntered += ProfileButton_PointerEntered", code, StringComparison.Ordinal);
        Assert.Contains("ProfileButton.PointerPressed += ProfileButton_PointerPressed", code, StringComparison.Ordinal);
        Assert.Contains("HoverRing.Opacity = 1", code, StringComparison.Ordinal);
        Assert.Contains("HoverRing.Opacity = 0", code, StringComparison.Ordinal);
        Assert.Contains("VirtualKey.Enter", code, StringComparison.Ordinal);
        Assert.Contains("OpenUserProfile(", code, StringComparison.Ordinal);
        Assert.Contains("ProfileAutomationId", code, StringComparison.Ordinal);
        Assert.Contains("IsProfileAvailable", code, StringComparison.Ordinal);
        Assert.Contains("UserIdentityNavigationPolicy.CanNavigate(login)", code, StringComparison.Ordinal);
        Assert.Contains("UserIdentityAutomationId.Create(", code, StringComparison.Ordinal);
        Assert.Contains("DisplayNameProperty", code, StringComparison.Ordinal);
        Assert.Contains("AutomationInstanceIdProperty", code, StringComparison.Ordinal);
        Assert.Contains("Text=\"{x:Bind DisplayText, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AvatarsNestedInExistingInvokeControls_DisableInnerProfileNavigation()
    {
        string root = FindRepositoryRoot();
        string viewsRoot = Path.Combine(root, "JitHub.WinUI", "Views");
        List<string> offenders = [];

        foreach (string path in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement avatar in document.Descendants().Where(element => element.Name.LocalName == "Avatar"))
            {
                bool nestedInvokeControl = avatar.Ancestors().Any(element =>
                    element.Name.LocalName is "Button" or "CheckBox" or "ToggleButton" or "MenuFlyoutItem");
                if (nestedInvokeControl &&
                    !string.Equals(avatar.Attribute("IsProfileNavigationEnabled")?.Value, "False", StringComparison.OrdinalIgnoreCase))
                {
                    offenders.Add(Path.GetRelativePath(root, path));
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void SharedCommentAndCurrentIssueComposerBindAuthenticatedLogins()
    {
        string root = FindRepositoryRoot();
        string sharedComment = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "UserCommentBlock.xaml"));
        string myIssues = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Pages", "MyIssuesPage.xaml"));

        Assert.Contains("Login=\"{x:Bind ViewModel.AuthenticatedCommenterLogin, Mode=OneWay}\"", sharedComment, StringComparison.Ordinal);
        Assert.Contains("Text=\"{x:Bind ViewModel.CommenterDisplayName, Mode=OneWay}\"", sharedComment, StringComparison.Ordinal);
        Assert.Contains("NavigationSource=\"comment_avatar\"", sharedComment, StringComparison.Ordinal);
        Assert.Contains("Login=\"{x:Bind AuthenticatedLogin, Mode=OneWay}\"", myIssues, StringComparison.Ordinal);
        Assert.Contains("NavigationSource=\"issue_comment\"", myIssues, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalCommentAvatarHostsExposeRouteSpecificSources()
    {
        string root = FindRepositoryRoot();
        (string Path, string Source)[] hosts =
        [
            (Path.Combine("Views", "Controls", "Issue", "RepoIssueDetailPane.xaml"), "issue_comment"),
            (Path.Combine("Views", "Pages", "MyIssuesPage.xaml"), "issue_comment"),
            (Path.Combine("Views", "Pages", "RepoPullRequestPage.xaml"), "pull_request_comment"),
            (Path.Combine("Views", "Pages", "MyPullRequestsPage.xaml"), "pull_request_comment"),
            (Path.Combine("Views", "Pages", "RepoCommitsPage.xaml"), "commit_comment"),
            (Path.Combine("Views", "Controls", "PullRequest", "Conversation", "ReviewBlock.xaml"), "pull_request_review"),
            (Path.Combine("Views", "Controls", "PullRequest", "Conversation", "PullRequestTimelineItem.xaml"), "pull_request_timeline")
        ];

        foreach ((string relativePath, string source) in hosts)
        {
            string xaml = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", relativePath));
            Assert.Contains($"NavigationSource=\"{source}\"", xaml, StringComparison.Ordinal);

            if (source == "pull_request_timeline")
            {
                string code = File.ReadAllText(Path.Combine(
                    root,
                    "JitHub.WinUI",
                    "Views",
                    "Controls",
                    "PullRequest",
                    "Conversation",
                    "PullRequestTimelineItem.xaml.cs"));
                Assert.Contains("ActorAvatar.Login = viewModel.ActorLogin ?? string.Empty", code, StringComparison.Ordinal);
            }
            else if (source == "pull_request_review")
            {
                string code = File.ReadAllText(Path.Combine(
                    root,
                    "JitHub.WinUI",
                    "Views",
                    "Controls",
                    "PullRequest",
                    "Conversation",
                    "ReviewBlock.xaml.cs"));
                Assert.Contains("ReviewerAvatar.Login = viewModel.AuthenticatedReviewerLogin", code, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains("Login=\"", xaml, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void MyWorkAndRepositoryAuthorAvatarsUseDedicatedRouteKeys()
    {
        string root = FindRepositoryRoot();
        string myIssues = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "MyIssuesPage.xaml"));
        string myPullRequests = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "MyPullRequestsPage.xaml"));
        string repoIssues = string.Join(
            Environment.NewLine,
            File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "RepoIssuePage.xaml")),
            File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Issue", "RepoIssueListPane.xaml")),
            File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Issue", "RepoIssueDetailPane.xaml")));
        string repoPullRequests = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "RepoPullRequestPage.xaml"));
        string repoPullRequestViewModel = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "ViewModels", "Pages", "RepoPullRequestPageViewModel.cs"));
        string timelineViewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "PullRequestViewModels",
            "ConversationViewModels",
            "PullRequestTimelineItemViewModel.cs"));

        Assert.Contains("Login=\"{x:Bind AuthenticatedLogin, Mode=OneWay}\"", myIssues, StringComparison.Ordinal);
        Assert.Contains("Login=\"{x:Bind ViewModel.SelectedIssueAuthorLogin, Mode=OneWay}\"", myIssues, StringComparison.Ordinal);
        Assert.Contains("Login=\"{x:Bind AuthenticatedLogin, Mode=OneWay}\"", myPullRequests, StringComparison.Ordinal);
        Assert.Contains("Login=\"{x:Bind ViewModel.SelectedIssueAuthorLogin, Mode=OneWay}\"", myPullRequests, StringComparison.Ordinal);
        Assert.Contains("Login=\"{x:Bind ViewModel.SelectedIssueAuthorLogin, Mode=OneWay}\"", repoIssues, StringComparison.Ordinal);
        Assert.Contains("Login=\"{Binding SelectedPullRequestAuthorLogin}\"", repoPullRequests, StringComparison.Ordinal);
        Assert.DoesNotContain("Login=\"{Binding SelectedPullRequestAuthor}\"", repoPullRequests, StringComparison.Ordinal);
        Assert.Contains("GetRoutablePullRequestAuthorLogin(SelectedPullRequest?.User)", repoPullRequestViewModel, StringComparison.Ordinal);
        Assert.Contains("UserIdentityNavigationPolicy.CreatePresentation(", timelineViewModel, StringComparison.Ordinal);
        Assert.Contains("ActorLogin = null", timelineViewModel, StringComparison.Ordinal);

        string mePullRequestItems = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "ViewModels", "Pages", "MePullRequestSectionViewItems.cs"));
        string reviewBlock = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "PullRequest", "Conversation", "ReviewBlock.xaml"));
        string reviewBlockCode = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "PullRequest", "Conversation", "ReviewBlock.xaml.cs"));
        Assert.Contains("public string? AuthenticatedLogin", mePullRequestItems, StringComparison.Ordinal);
        Assert.Contains("public string Login => AuthenticatedLogin ?? string.Empty", mePullRequestItems, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ReviewerAvatar\"", reviewBlock, StringComparison.Ordinal);
        Assert.Contains("ReviewerAvatar.Login = viewModel.AuthenticatedReviewerLogin", reviewBlockCode, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding ReviewerDisplayName, Mode=OneWay}\"", reviewBlock, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalRepositoryIdentitySurfacesUseSharedRoutableAvatars()
    {
        string root = FindRepositoryRoot();
        string repoIssues = string.Join(
            Environment.NewLine,
            File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "RepoIssuePage.xaml")),
            File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Issue", "RepoIssueListPane.xaml")),
            File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Issue", "RepoIssueDetailPane.xaml")));
        string repoPullRequests = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "RepoPullRequestPage.xaml"));
        string repoCommits = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "RepoCommitsPage.xaml"));
        string pullRequestViewModel = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "ViewModels", "Pages", "RepoPullRequestPageViewModel.cs"));
        string commitViewModel = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "ViewModels", "Pages", "RepoCommitsPageViewModel.cs"));

        AssertAvatarSource(repoIssues, "issue_list_author");
        AssertAvatarSource(repoPullRequests, "pull_request_list_author");
        AssertAvatarSource(repoPullRequests, "pull_request_commit_author");
        AssertAvatarSource(repoPullRequests, "pull_request_timeline_actor");
        AssertAvatarSource(repoPullRequests, "pull_request_reviewer");
        AssertAvatarSource(repoCommits, "commit_list_author");
        AssertAvatarSource(repoCommits, "commit_detail_author");

        Assert.Contains("AutomationInstanceId=", repoIssues, StringComparison.Ordinal);
        Assert.Contains("AutomationInstanceId=", repoPullRequests, StringComparison.Ordinal);
        Assert.Contains("AutomationInstanceId=", repoCommits, StringComparison.Ordinal);
        Assert.Contains("PullRequestIdentityProjection.Create(", pullRequestViewModel, StringComparison.Ordinal);
        Assert.Contains("ReviewerAvatarUrl =", pullRequestViewModel, StringComparison.Ordinal);
        Assert.Contains("SelectedCommitAuthorLogin", commitViewModel, StringComparison.Ordinal);
        Assert.Contains("UserIdentityNavigationPolicy.GetRoutableLogin(SelectedCommit?.Author?.Login)", commitViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileAvatarProbeExercisesHoverAndCanonicalInternalRoutes()
    {
        string root = FindRepositoryRoot();
        string automation = File.ReadAllText(Path.Combine(root, "JitHub.WinUI.Automation", "Program.cs"));

        Assert.Contains("MoveMouseToEmptyTitleBar(window, commandSearch)", automation, StringComparison.Ordinal);
        Assert.Contains("WaitForScreenshotRegionToStabilize(", automation, StringComparison.Ordinal);
        Assert.Contains("profile-avatar-routing-hover.png", automation, StringComparison.Ordinal);
        Assert.Contains("same issue-list author avatar after profile route Back", automation, StringComparison.Ordinal);
        Assert.Contains("issue-list author avatar focus restoration", automation, StringComparison.Ordinal);
        Assert.Contains("currentAvatar &&", automation, StringComparison.Ordinal);
        Assert.Contains("UserProfile_issue_list_author_", automation, StringComparison.Ordinal);
        Assert.Contains("pull_request_list_author", automation, StringComparison.Ordinal);
        Assert.Contains("profile-avatar-routing-commits", automation, StringComparison.Ordinal);
        Assert.Contains("commit_list_author", automation, StringComparison.Ordinal);
        Assert.Contains("IsUserIdentityForSource", automation, StringComparison.Ordinal);
        Assert.Contains("Repeated issue author avatars exposed duplicate automation IDs", automation, StringComparison.Ordinal);
        Assert.Contains("also invoked or changed the parent issue row", automation, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellRouteFocusRestoration_RejectsHiddenVirtualizedIdentityControls()
    {
        string root = FindRepositoryRoot();
        string shell = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Pages", "ShellPage.xaml.cs"));

        Assert.Contains(
            "FindDescendantByAutomationId<Control>(pageRoot, viewState.FocusTargetId)",
            shell,
            StringComparison.Ordinal);
        Assert.Contains("IsAvailableForRouteStateRestoration(candidate, root)", shell, StringComparison.Ordinal);
        Assert.Contains("element.IsLoaded", shell, StringComparison.Ordinal);
        Assert.Contains("ancestor.Visibility != Visibility.Visible", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRepeatedIdentityTemplateBindsAStableAutomationInstance()
    {
        string root = FindRepositoryRoot();
        string viewsRoot = Path.Combine(root, "JitHub.WinUI", "Views");
        List<string> offenders = [];
        int repeatedAvatarCount = 0;

        foreach (string path in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(path);
            XElement[] repeatedAvatars = document
                .Descendants()
                .Where(element => element.Name.LocalName == "Avatar")
                .Where(element => element.Ancestors().Any(ancestor => ancestor.Name.LocalName == "DataTemplate"))
                .ToArray();

            repeatedAvatarCount += repeatedAvatars.Length;
            offenders.AddRange(repeatedAvatars
                .Where(avatar => string.IsNullOrWhiteSpace(avatar.Attribute("AutomationInstanceId")?.Value))
                .Select(_ => Path.GetRelativePath(root, path)));
        }

        Assert.True(repeatedAvatarCount > 0);
        Assert.Empty(offenders);
    }

    [Fact]
    public void RepeatedSharedControlsForwardTheirLogicalRowIdentityToAvatar()
    {
        string root = FindRepositoryRoot();
        string comment = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "UserCommentBlock.xaml"));
        string activity = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "App", "ActivityCard.xaml"));

        Assert.Contains(
            "AutomationInstanceId=\"{x:Bind ViewModel.CommenterAvatarAutomationId, Mode=OneWay}\"",
            comment,
            StringComparison.Ordinal);
        Assert.Contains(
            "AutomationInstanceId=\"{x:Bind ViewModel.EventId, Mode=OneWay}\"",
            activity,
            StringComparison.Ordinal);
    }

    private static void AssertAvatarSource(string xaml, string source)
    {
        Assert.Contains($"NavigationSource=\"{source}\"", xaml, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
