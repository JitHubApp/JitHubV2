using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class RepoCommitsInteractionContractTests
{
    [Fact]
    public void DeferredCommentFlyoutHandlersGuardUnloadedCommentVisuals()
    {
        string source = File.ReadAllText(SourcePath(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCommitsPage.xaml.cs"));

        Assert.Contains("ViewModel.IsCommentsSectionVisible && CommitCommentForm is { IsLoaded: true }", source, StringComparison.Ordinal);
        Assert.Contains("ViewModel.IsCommentsSectionVisible && RepoCommitsOpenCommentButton is { IsLoaded: true }", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.TryEnqueue", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitListHeaderIsSingleLineAndSearchIsOnDemand()
    {
        XDocument xaml = LoadXaml();
        XElement expandedHeader = FindByName(xaml, "CommitListExpandedHeaderSurface");
        XElement branchPicker = FindByAutomationId(xaml, "RepoCommitsBranchComboBox");
        XElement searchButton = FindByAutomationId(xaml, "RepoCommitsSearchButton");
        XElement searchBox = FindByAutomationId(xaml, "RepoCommitsSearchBox");

        Assert.Contains(branchPicker.Ancestors(), ancestor => ReferenceEquals(ancestor, expandedHeader));
        Assert.Contains(searchButton.Ancestors(), ancestor => ReferenceEquals(ancestor, expandedHeader));
        Assert.Contains(searchBox.Ancestors(), ancestor => ancestor.Name.LocalName == "Flyout");
        Assert.Null(searchBox.Attribute("Header"));
        Assert.Contains(xaml.Descendants(), element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Uid" &&
                attribute.Value == "PagesRepoCommitsPageTextBlockSearchCommitMessages"));
        Assert.DoesNotContain(expandedHeader.Descendants(), element =>
            element.Name.LocalName == "Grid.RowDefinitions");

        XElement shyHeader = FindByAutomationId(xaml, "RepoCommitsListShyHeader");
        XElement alignedTitle = Assert.Single(shyHeader.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            HasTransitionId(element, "CommitListHeaderTitle"));
        XElement alignedBranch = Assert.Single(shyHeader.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            HasTransitionId(element, "CommitListHeaderBranch"));
        Assert.Equal("Center", (string?)alignedTitle.Attribute("VerticalAlignment"));
        Assert.Equal("Center", (string?)alignedBranch.Attribute("VerticalAlignment"));
        Assert.Equal(alignedTitle.Attribute("FontFamily")?.Value, alignedBranch.Attribute("FontFamily")?.Value);
        Assert.Equal(alignedTitle.Attribute("FontSize")?.Value, alignedBranch.Attribute("FontSize")?.Value);
        Assert.Equal("Commits", (string?)alignedTitle.Attribute("Text"));
        Assert.Contains("SelectedBranchName", alignedBranch.Attribute("Text")?.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitDiffSearchAndMatchNavigationAreOnDemand()
    {
        XDocument xaml = LoadXaml();
        XElement searchBox = FindByAutomationId(xaml, "RepoCommitsDiffSearchBox");
        XElement previousButton = FindByAutomationId(xaml, "RepoCommitsPreviousDiffMatchButton");
        XElement nextButton = FindByAutomationId(xaml, "RepoCommitsNextDiffMatchButton");
        XElement tabs = FindByAutomationId(xaml, "RepoCommitsSectionSegmented");
        XElement actions = FindByName(xaml, "CommitDiffHeaderActions");
        XElement diffSection = FindByName(xaml, "CommitDiffSection");
        XElement diffSplitView = FindByName(xaml, "CommitDiffSplitView");

        Assert.All(new[] { searchBox, previousButton, nextButton }, element =>
            Assert.Contains(element.Ancestors(), ancestor => ancestor.Name.LocalName == "Flyout"));
        Assert.Null(searchBox.Attribute("Header"));
        Assert.Contains(xaml.Descendants(), element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Uid" &&
                attribute.Value == "PagesRepoCommitsPageTextBlockSearchCommitDiff"));
        Assert.Same(tabs.Parent, actions.Parent);
        Assert.DoesNotContain(diffSection.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") is
                "RepoCommitsDiffSearchBox" or
                "RepoCommitsPreviousDiffMatchButton" or
                "RepoCommitsNextDiffMatchButton");
        Assert.Equal("1", (string?)diffSplitView.Attribute("Grid.Row"));
    }

    [Fact]
    public void CommitListMetadataUsesOneBaselineAndCenteredAvatar()
    {
        XDocument xaml = LoadXaml();
        XElement template = Assert.Single(xaml.Descendants(), element =>
            element.Name.LocalName == "DataTemplate" &&
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Key" &&
                attribute.Value == "RepoCommitListItemTemplate"));
        XElement avatar = Assert.Single(template.Descendants(), element => element.Name.LocalName == "Avatar");
        XElement metadata = Assert.Single(template.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            new[] { "ShortSha", "AuthorDisplayName", "AuthorDate", "Stats.SummaryText" }.All(binding =>
                element.Descendants().Any(run =>
                    run.Name.LocalName == "Run" &&
                    run.Attribute("Text")?.Value.Contains(binding, StringComparison.Ordinal) == true)));

        Assert.Equal("False", (string?)avatar.Attribute("ShowLogin"));
        Assert.Equal("Center", (string?)avatar.Attribute("VerticalAlignment"));
        Assert.Equal("Center", (string?)metadata.Attribute("VerticalAlignment"));
    }

    [Fact]
    public void CommitShyHeaderStartsPromptlyAndMorphsSharedControls()
    {
        string source = File.ReadAllText(SourcePath(
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoCommitsPage.xaml.cs"));

        Assert.Contains("private const double ShyHeaderStartOffset = 24;", source, StringComparison.Ordinal);
        Assert.Contains("Id = \"CommitListHeaderSearch\"", source, StringComparison.Ordinal);
        Assert.Contains("Id = \"CommitListHeaderBranch\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("VisualStateToggleMethod.ByIsVisible", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_detailHeaderSettledTimestamp", source, StringComparison.Ordinal);

        XDocument xaml = LoadXaml();
        Assert.Equal(2, xaml.Descendants().Count(element => HasTransitionId(element, "CommitListHeaderBranch")));
    }

    [Fact]
    public void CompareWorkspaceUsesLabeledRefsOnDemandSearchAndExplicitStates()
    {
        XDocument xaml = LoadXaml();
        XElement compareSection = FindByName(xaml, "CommitCompareSection");
        XElement baseBox = FindByAutomationId(xaml, "RepoCommitsCompareBaseBox");
        XElement headBox = FindByAutomationId(xaml, "RepoCommitsCompareHeadBox");
        XElement swapButton = FindByAutomationId(xaml, "RepoCommitsCompareSwapButton");
        XElement compareButton = FindByAutomationId(xaml, "RepoCommitsCompareButton");
        XElement searchButton = FindByAutomationId(xaml, "RepoCommitsCompareSearchButton");
        XElement searchBox = FindByAutomationId(xaml, "RepoCommitsCompareDiffSearchBox");

        Assert.All(new[] { baseBox, headBox, swapButton, compareButton, searchButton }, element =>
            Assert.Contains(element.Ancestors(), ancestor => ReferenceEquals(ancestor, compareSection)));
        Assert.Contains(searchBox.Ancestors(), ancestor => ancestor.Name.LocalName == "Flyout");
        Assert.DoesNotContain(compareSection.Descendants(), element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == "CommitCompareSearchTools"));
        Assert.All(
            new[]
            {
                "RepoCommitsCompareInitialState",
                "RepoCommitsCompareLoadingState",
                "RepoCommitsCompareNoChangesState",
                "RepoCommitsCompareErrorState"
            },
            automationId => FindByAutomationId(xaml, automationId));
        Assert.Contains("IsCompareDiffVisible", searchButton.Attribute("IsEnabled")?.Value, StringComparison.Ordinal);
    }

    private static XDocument LoadXaml() => XDocument.Load(SourcePath(
        "JitHub.WinUI",
        "Views",
        "Pages",
        "RepoCommitsPage.xaml"));

    private static XElement FindByAutomationId(XDocument xaml, string automationId) =>
        Assert.Single(xaml.Descendants(), element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == automationId);

    private static XElement FindByName(XDocument xaml, string name) =>
        Assert.Single(xaml.Descendants(), element =>
            element.Attributes().Any(attribute =>
                attribute.Name.LocalName == "Name" && attribute.Value == name));

    private static bool HasTransitionId(XElement element, string id) =>
        element.Attributes().Any(attribute =>
            attribute.Name.LocalName.EndsWith(".Id", StringComparison.Ordinal) && attribute.Value == id);

    private static string SourcePath(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return Path.Combine([directory?.FullName ?? throw new DirectoryNotFoundException(), .. segments]);
    }
}
