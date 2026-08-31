using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class MyIssuesResponsiveFilterContractTests
{
    [Fact]
    public void MyIssues_UsesResourceBindingsAndCompactOverflowPickers()
    {
        string path = GetPagePath("MyIssuesPage.xaml");
        XDocument document = XDocument.Load(path);
        string source = document.ToString(SaveOptions.DisableFormatting);

        Assert.Contains("ViewModel.AssignedFilterLabel", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.MentionedFilterLabel", source, StringComparison.Ordinal);
        Assert.Contains("MyIssuesScopeCompactPicker", source, StringComparison.Ordinal);
        Assert.Contains("MyIssuesStateCompactPicker", source, StringComparison.Ordinal);
        Assert.Equal(2, document.Descendants().Count(element =>
            element.Name.LocalName == "ComboBox" &&
            string.Equals(
                element.Attribute("Style")?.Value,
                "{StaticResource AppCompactTextComboBoxStyle}",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void MyPullRequests_MatchesMyIssuesResponsiveFilterAffordance()
    {
        string path = GetPagePath("MyPullRequestsPage.xaml");
        XDocument document = XDocument.Load(path);
        string source = document.ToString(SaveOptions.DisableFormatting);
        string codeBehind = File.ReadAllText(path + ".cs");

        Assert.Contains("ViewModel.PullRequestInvolvedFilterLabel", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PullRequestReviewRequestedFilterLabel", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PullRequestAuthoredFilterLabel", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.PullRequestAssignedFilterLabel", source, StringComparison.Ordinal);
        Assert.Contains("ExpandedPullRequestFilters", source, StringComparison.Ordinal);
        Assert.Contains("CompactPullRequestFilters", source, StringComparison.Ordinal);
        Assert.Contains("MyPullRequestsScopeSegmented", source, StringComparison.Ordinal);
        Assert.Contains("MyPullRequestsScopeCompactPicker", source, StringComparison.Ordinal);
        Assert.Contains("MyPullRequestsStateSegmented", source, StringComparison.Ordinal);
        Assert.Contains("MyPullRequestsStateCompactPicker", source, StringComparison.Ordinal);
        Assert.Equal(2, document.Descendants().Count(element =>
            element.Name.LocalName == "ComboBox" &&
            string.Equals(
                element.Attribute("Style")?.Value,
                "{StaticResource AppCompactTextComboBoxStyle}",
                StringComparison.Ordinal)));
        Assert.Contains("SetPullRequestFilter", codeBehind, StringComparison.Ordinal);
        Assert.Contains("GitHubMePullRequestFilter.ReviewRequested", codeBehind, StringComparison.Ordinal);
        Assert.Contains("GitHubMePullRequestFilter.Authored", codeBehind, StringComparison.Ordinal);
        Assert.Contains("GitHubMePullRequestFilter.Assigned", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void MyIssues_PreservesCanonicalMarkdownHostIdentity()
    {
        string path = GetPagePath("MyIssuesPage.xaml");
        XDocument document = XDocument.Load(path);
        XElement[] viewers = document.Descendants().Where(element => element.Name.LocalName == "MarkdownViewer").ToArray();

        Assert.Contains(viewers, viewer =>
            string.Equals(viewer.Attribute("HostKind")?.Value, "Comment", StringComparison.Ordinal) &&
            string.Equals(
                viewer.Attribute("AutomationInstanceId")?.Value,
                "{x:Bind MarkdownAutomationId, Mode=OneWay}",
                StringComparison.Ordinal));
        Assert.Contains(viewers, viewer =>
            string.Equals(viewer.Attribute("HostKind")?.Value, "Conversation", StringComparison.Ordinal) &&
            string.Equals(viewer.Attribute("AutomationInstanceId")?.Value, "MyIssuesBody", StringComparison.Ordinal));
    }

    private static string GetPagePath(string fileName) => Path.Combine(
        FindRepositoryRoot(),
        "JitHub.WinUI",
        "Views",
        "Pages",
        fileName);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
