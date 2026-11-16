using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views.Pages;

/// <summary>
/// Checks the issue and pull request conversations create their composer on demand and
/// share the shy header motion token.
/// </summary>
public sealed class ConversationComposerContractTests
{
    [Fact]
    public void PullRequestConversation_UsesOnDemandComposerAndShyHeader()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml"));
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoPullRequestPage.xaml.cs"));

        Assert.Contains("x:Name=\"RepoPullRequestsOpenCompactCommentButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PullRequestCommentFlyout\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PullRequestCompactCommentForm\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Name=\"PullRequestCommentForm\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PullRequestShySectionComboBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SizeChanged=\"PullRequestScrollableSection_SizeChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PullRequestFilesSection\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Loaded=\"PullRequestScrollableSection_Loaded\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Unloaded=\"PullRequestScrollableSection_Unloaded\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("labs:TransitionHelper.Id=\"PullRequestHeaderSurface\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("labs:TransitionHelper.Id=\"PullRequestHeaderChrome\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, xaml.Split("labs:TransitionHelper.Id=\"PullRequestTitle\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, xaml.Split("labs:TransitionHelper.Id=\"PullRequestSectionSelector\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("Background=\"{ThemeResource AppTransientOverlayBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderStartOffset", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRestoreOffset", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRevealTravel", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRehideTravel", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderScrollPolicy.CanCollapse", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestSectionScrollViewer_ViewChanged", source, StringComparison.Ordinal);
        Assert.Contains("RegisterPropertyChangedCallback", source, StringComparison.Ordinal);
        Assert.Contains("FindPrimaryShyHeaderScrollViewer", source, StringComparison.Ordinal);
        Assert.Contains("AttachShyHeaderScrollViewer(primary);", source, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach (ScrollViewer candidate in scrollViewers)", source, StringComparison.Ordinal);
        Assert.Contains("new TransitionHelper", source, StringComparison.Ordinal);
        Assert.Contains("TargetToggleMethod = VisualStateToggleMethod.ByIsVisible", source, StringComparison.Ordinal);
        Assert.Contains("new TransitionConfig { Id = \"PullRequestHeaderChrome\", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true }", source, StringComparison.Ordinal);
        Assert.Contains("new TransitionConfig { Id = \"PullRequestSectionSelector\", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true }", source, StringComparison.Ordinal);
        Assert.Contains("_headerTransition.StartAsync", source, StringComparison.Ordinal);
        Assert.Contains("_headerTransition.ReverseAsync", source, StringComparison.Ordinal);
        Assert.Contains("forceUpdateAnimatedElements: true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryPrepareIncomingSurface", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestShyHeaderSurface.Visibility = Visibility.Collapsed", source, StringComparison.Ordinal);
        Assert.Contains("RepoPullRequestPage_Unloaded", source, StringComparison.Ordinal);
        Assert.Contains("MorphTransitionSafety.TryStop(_headerTransition)", source, StringComparison.Ordinal);
        Assert.Contains("MorphTransitionSafety.TryResetVisibilityState", source, StringComparison.Ordinal);
        Assert.Contains("ui-repo-pull-request-header-morph", source, StringComparison.Ordinal);
        Assert.Contains("AnimateContentReflow", source, StringComparison.Ordinal);
        Assert.Contains("TranslationTransition", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestDetailLayout.UpdateLayout()", source, StringComparison.Ordinal);
        Assert.Contains("generation != _headerTransitionGeneration", source, StringComparison.Ordinal);
        Assert.Contains("AttachActiveSectionScrollSources", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestSectionComboBox.Visibility = IsCompactWorkspace", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestShySectionComboBox.Visibility = !IsCompactWorkspace", source, StringComparison.Ordinal);
        Assert.Contains("PullRequestCompactCommentForm.FocusEditor()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IssueConversation_UsesOnDemandComposerAndShyHeader()
    {
        string xaml = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Issue",
            "RepoIssueDetailPane.xaml"));
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Issue",
            "RepoIssueDetailPane.xaml.cs"));

        Assert.Contains("AutomationProperties.AutomationId=\"RepoIssuesOpenCommentButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"IssueCommentFlyout\"", xaml, StringComparison.Ordinal);
        Assert.Contains("ViewChanged=\"IssueConversationScrollViewer_ViewChanged\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AdaptiveWorkspaceMode.Narrow or AdaptiveWorkspaceMode.Compact", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderStartOffset", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRestoreOffset", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRevealTravel", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderRehideTravel", source, StringComparison.Ordinal);
        Assert.Contains("ShyHeaderScrollPolicy.CanCollapse", source, StringComparison.Ordinal);
        Assert.DoesNotContain("labs:TransitionHelper.Id=\"IssueHeaderSurface\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("labs:TransitionHelper.Id=\"IssueHeaderChrome\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, xaml.Split("labs:TransitionHelper.Id=\"IssueTitle\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("Background=\"{ThemeResource AppTransientOverlayBrush}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("new TransitionHelper", source, StringComparison.Ordinal);
        Assert.Contains("TargetToggleMethod = VisualStateToggleMethod.ByIsVisible", source, StringComparison.Ordinal);
        Assert.Contains("new TransitionConfig { Id = \"IssueHeaderChrome\", ScaleMode = ScaleMode.Scale, EnableClipAnimation = true }", source, StringComparison.Ordinal);
        Assert.Contains("_headerTransition.StartAsync", source, StringComparison.Ordinal);
        Assert.Contains("_headerTransition.ReverseAsync", source, StringComparison.Ordinal);
        Assert.Contains("forceUpdateAnimatedElements: true", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryPrepareIncomingSurface", source, StringComparison.Ordinal);
        Assert.Contains("RepoIssuesShyHeaderSurface.Visibility = Visibility.Collapsed", source, StringComparison.Ordinal);
        Assert.Contains("RepoIssueDetailPane_Unloaded", source, StringComparison.Ordinal);
        Assert.Contains("MorphTransitionSafety.TryStop(_headerTransition)", source, StringComparison.Ordinal);
        Assert.Contains("MorphTransitionSafety.TryResetVisibilityState", source, StringComparison.Ordinal);
        Assert.Contains("ui-repo-issue-header-morph", source, StringComparison.Ordinal);
        Assert.Contains("AnimateContentReflow", source, StringComparison.Ordinal);
        Assert.Contains("TranslationTransition", source, StringComparison.Ordinal);
        Assert.Contains("RepoIssuesDetailLayout.UpdateLayout()", source, StringComparison.Ordinal);
        Assert.Contains("generation != _headerTransitionGeneration", source, StringComparison.Ordinal);
        Assert.Contains("IssueCommentForm.FocusEditor()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IssueCommentForm.EffectiveEditorHeight", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShyHeaderMorphs_UseSharedMediumMotionToken()
    {
        string root = FindRepositoryRoot();
        string motionTokens = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Styles",
            "Foundation",
            "Tokens.Motion.xaml"));
        string resolver = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Helpers",
            "AppMotionTokens.cs"));
        string[] shyHeaderSources =
        [
            Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "RepoPullRequestPage.xaml.cs"),
            Path.Combine(root, "JitHub.WinUI", "Views", "Controls", "Issue", "RepoIssueDetailPane.xaml.cs"),
            Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "DashboardPage.xaml.cs"),
            Path.Combine(root, "JitHub.WinUI", "Views", "Pages", "ShellPage.xaml.cs")
        ];

        Assert.Contains("<Duration x:Key=\"AppMediumDuration\">0:0:0.18</Duration>", motionTokens, StringComparison.Ordinal);
        Assert.Contains("MediumDurationResourceKey = \"AppMediumDuration\"", resolver, StringComparison.Ordinal);
        Assert.Contains("resources.MergedDictionaries[index]", resolver, StringComparison.Ordinal);
        Assert.Contains("directValue = resources[resourceKey]", resolver, StringComparison.Ordinal);
        Assert.Contains("TimeSpan timeSpan when timeSpan > TimeSpan.Zero", resolver, StringComparison.Ordinal);
        Assert.Contains("Duration xamlDuration when xamlDuration.HasTimeSpan", resolver, StringComparison.Ordinal);
        Assert.Contains("double seconds when double.IsFinite(seconds)", resolver, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(seconds)", resolver, StringComparison.Ordinal);
        Assert.Contains("Interlocked.CompareExchange(ref _mediumDurationTicks", resolver, StringComparison.Ordinal);

        foreach (string path in shyHeaderSources)
        {
            string source = File.ReadAllText(path);
            Assert.Contains("AppMotionTokens.MediumDuration", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TimeSpan.FromMilliseconds(240)", source, StringComparison.Ordinal);
            Assert.DoesNotContain("TimeSpan.FromMilliseconds(220)", source, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) ||
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
