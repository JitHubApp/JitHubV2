using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class ProfileNativeWorkspaceContractTests
{
    [Fact]
    public void PageUsesFixedWorkspaceAndOnlyActiveContentOwnsScrolling()
    {
        XDocument document = XDocument.Load(XamlPath());
        XElement board = Assert.Single(document.Descendants(), static element =>
            element.Attribute(XName.Get("Name", XamlNamespace))?.Value == "ProfileBoard");
        XElement selector = Assert.Single(document.Descendants(), static element =>
            element.Name.LocalName == "SelectorBar" &&
            element.Attribute("AutomationProperties.AutomationId")?.Value == "ProfileModeSelector");

        Assert.NotNull(board);
        Assert.Equal(5, selector.Elements().Count(static element => element.Name.LocalName == "SelectorBarItem"));
        Assert.Equal("Profile sections", selector.Attribute("AutomationProperties.Name")?.Value);
        Assert.DoesNotContain(document.Root!.Elements(), static element => element.Name.LocalName == "ScrollViewer");

        string source = File.ReadAllText(CodeBehindPath());
        Assert.DoesNotContain("MainRail.Children", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildProfileWorkspace", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Children.Clear()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FullCollectionsAreVirtualizedAndNotNestedInsideScrollViewers()
    {
        XDocument document = XDocument.Load(XamlPath());
        string[] ids =
        [
            "ProfileRepositoriesList",
            "ProfileStarsList",
            "ProfileActivityList",
            "ProfileFollowersList",
            "ProfileFollowingList"
        ];

        foreach (string id in ids)
        {
            XElement list = Assert.Single(document.Descendants(), element =>
                element.Name.LocalName == "ListView" &&
                element.Attribute("AutomationProperties.AutomationId")?.Value == id);
            Assert.False(string.IsNullOrWhiteSpace(list.Attribute("AutomationProperties.Name")?.Value));
            Assert.False(string.IsNullOrWhiteSpace(list.Attribute(XName.Get("Load", XamlNamespace))?.Value));
            Assert.DoesNotContain(list.Ancestors(), static ancestor => ancestor.Name.LocalName == "ScrollViewer");
        }
    }

    [Fact]
    public void KeyedRowsNotifyRetainedBindingsWithoutUiTypesInTheViewModel()
    {
        string xaml = File.ReadAllText(XamlPath());
        string viewModel = File.ReadAllText(ViewModelPath());

        Assert.Contains("ProfileRepositoryViewItem : ObservableObject", viewModel, StringComparison.Ordinal);
        Assert.Contains("ProfilePersonItem : ObservableObject", viewModel, StringComparison.Ordinal);
        Assert.Contains("ProfileActivityItem : ObservableObject", viewModel, StringComparison.Ordinal);
        Assert.Contains("LanguageColor, Converter={StaticResource ProfileHexColorBrushConverter}, Mode=OneWay", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{x:Bind FullName, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{x:Bind Summary, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("SolidColorBrush", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.UI.Xaml", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileActionsAndRowsExposeStableAutomationContracts()
    {
        XDocument document = XDocument.Load(XamlPath());
        string[] ids =
        [
            "ProfileEditButton",
            "ProfileFollowButton",
            "ProfileMoreButton",
            "ProfileReposStatTile",
            "ProfileFollowersStatTile",
            "ProfileFollowingStatTile",
            "ProfileGistsStatTile",
            "ProfilePeopleBackButton",
            "ProfileContributionGraph"
        ];

        foreach (string id in ids)
        {
            Assert.Contains(document.Descendants(), element =>
                element.Attribute("AutomationProperties.AutomationId")?.Value == id);
        }

        string xaml = File.ReadAllText(XamlPath());
        Assert.Contains("ProfileRepository_{", File.ReadAllText(ViewModelPath()), StringComparison.Ordinal);
        Assert.Contains("ContainerContentChanging=\"ProfileList_ContainerContentChanging\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileFactsUseOneNativeActionSurfaceAndSafeOpenCopyCommands()
    {
        XDocument document = XDocument.Load(XamlPath());
        XElement template = Assert.Single(document.Descendants(), static element =>
            element.Name.LocalName == "DataTemplate" &&
            element.Attribute(XName.Get("Key", XamlNamespace))?.Value == "ProfileFactTemplate");
        XElement action = Assert.Single(template.Descendants(), static element =>
            element.Name.LocalName == "Button" &&
            element.Attribute("Click")?.Value == "ProfileFactButton_Click");

        Assert.Equal("{StaticResource AppQuietButtonStyle}", action.Attribute("Style")?.Value);
        Assert.NotNull(action.Attribute("AutomationProperties.AutomationId"));
        Assert.NotNull(action.Attribute("AutomationProperties.Name"));
        Assert.Contains(template.Descendants(), static element =>
            element.Name.LocalName == "MenuFlyoutItem" &&
            element.Attribute("Click")?.Value == "ProfileFactOpenMenuItem_Click" &&
            !string.IsNullOrWhiteSpace(element.Attribute("AutomationProperties.Name")?.Value));
        Assert.Contains(template.Descendants(), static element =>
            element.Name.LocalName == "MenuFlyoutItem" &&
            element.Attribute("Click")?.Value == "ProfileFactCopyMenuItem_Click" &&
            !string.IsNullOrWhiteSpace(element.Attribute("AutomationProperties.Name")?.Value));

        string viewModel = File.ReadAllText(ViewModelPath());
        Assert.Contains("MarkdownLinkNavigationPolicy.IsAllowedLaunchUri(uri)", viewModel, StringComparison.Ordinal);
        Assert.Contains("ProfileFactActionPolicy.CreateWebsite", viewModel, StringComparison.Ordinal);
        Assert.Contains("ProfileFactActionPolicy.CreateEmail", viewModel, StringComparison.Ordinal);
        Assert.Contains("ProfileFactActionPolicy.CreateTwitter", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactIdentityStatsKeepVisibleCountAndScopeLabels()
    {
        XDocument document = XDocument.Load(XamlPath());
        string xaml = File.ReadAllText(XamlPath());
        string[] ids =
        [
            "ProfileCompactReposStatTile",
            "ProfileCompactFollowersStatTile",
            "ProfileCompactFollowingStatTile",
            "ProfileCompactGistsStatTile"
        ];

        foreach (string id in ids)
        {
            XElement button = Assert.Single(document.Descendants(), element =>
                element.Name.LocalName == "Button" &&
                element.Attribute("AutomationProperties.AutomationId")?.Value == id);
            Assert.Equal(2, button.Descendants().Count(static element => element.Name.LocalName == "TextBlock"));
        }

        Assert.Contains("ProfileCompactIdentityDetailsContent", xaml, StringComparison.Ordinal);
        Assert.Contains("ProfileCompactIdentityDetailsScrollViewer", xaml, StringComparison.Ordinal);
        Assert.Contains("MaxHeight=\"{ThemeResource AppDimension420}\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(xaml, "ItemsSource=\"{x:Bind ViewModel.Highlights"));
        Assert.Equal(1, CountOccurrences(xaml, "ItemsSource=\"{x:Bind ViewModel.Organizations"));
        Assert.Equal(1, CountOccurrences(xaml, "ItemsSource=\"{x:Bind ViewModel.CompactOrganizations"));
        Assert.Contains("<ItemsControl.ItemsPanel>", xaml, StringComparison.Ordinal);

        string viewModel = File.ReadAllText(ViewModelPath());
        Assert.Contains("CompactOrganizationLimit = 6", viewModel, StringComparison.Ordinal);
        Assert.Contains(".Take(CompactOrganizationLimit)", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void StarsUsesScopedModeAndCanonicalLibraryContracts()
    {
        string xaml = File.ReadAllText(XamlPath());
        string codeBehind = File.ReadAllText(CodeBehindPath());
        string viewModel = File.ReadAllText(ViewModelPath());

        Assert.DoesNotContain("StarsPreviewTitle", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("StarsPreviewTitle", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("StarsPreviewTitle", viewModel, StringComparison.Ordinal);
        Assert.Contains("ViewModel.StarsModeLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("ProfileStarsLibraryButton", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenStarsLibrary", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void OverviewIsIdentityFocusedAndFullModesLoadIncrementally()
    {
        XDocument document = XDocument.Load(XamlPath());
        XElement overview = Assert.Single(document.Descendants(), static element =>
            element.Name.LocalName == "ScrollViewer" &&
            element.Attribute("AutomationProperties.AutomationId")?.Value == "ProfileOverviewScrollViewer");
        XElement readme = Assert.Single(document.Descendants(), static element =>
            element.Name.LocalName == "MarkdownViewer" &&
            element.Attribute("AutomationProperties.AutomationId")?.Value == "ProfileReadme");
        string viewModel = File.ReadAllText(ViewModelPath());
        string service = File.ReadAllText(ServicePath());

        Assert.DoesNotContain(overview.Descendants(), static element => element.Name.LocalName == "MarkdownViewer");
        Assert.NotNull(readme);
        Assert.Contains("LoadNextPageAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("ProfilePagingState", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.Repositories.Value", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("snapshot.StarredRepositories.Value", viewModel, StringComparison.Ordinal);
        Assert.Contains("await ApplySnapshotAsync(snapshot, authenticatedView, cancellationToken)", viewModel, StringComparison.Ordinal);
        Assert.Contains("await YieldOverviewFrameAsync(cancellationToken)", viewModel, StringComparison.Ordinal);
        Assert.Contains("IsProfileLoading => IsLoading || IsOverviewLoading", viewModel, StringComparison.Ordinal);
        Assert.Contains("TryCreateRepositoryFile", viewModel, StringComparison.Ordinal);
        Assert.Contains("per_page={RepositoryPageSize}&page={page}", service, StringComparison.Ordinal);
        Assert.Contains("per_page={PeoplePageSize}&page={page}", service, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundedOverviewCollectionsUseInPlaceDiffsDuringRefresh()
    {
        string viewModel = File.ReadAllText(ViewModelPath());

        Assert.Contains("ApplyIndexedSnapshot(ContributionWeeks", viewModel, StringComparison.Ordinal);
        Assert.Contains("ApplyIndexedSnapshot(Facts", viewModel, StringComparison.Ordinal);
        Assert.Contains("ApplyIndexedSnapshot(Highlights", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Facts.Clear();", viewModel, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(viewModel, "ContributionWeeks.Clear();"));
        Assert.Equal(1, CountOccurrences(viewModel, "Highlights.Clear();"));
    }

    [Fact]
    public void ContributionGraphObservesLazyCollectionUpdatesWithoutLeakingThePage()
    {
        string control = File.ReadAllText(Path.Combine(
            Root(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Profile",
            "ProfileContributionGraph.xaml.cs"));

        Assert.Contains("INotifyCollectionChanged", control, StringComparison.Ordinal);
        Assert.Contains("collection.CollectionChanged += WeeksCollection_CollectionChanged", control, StringComparison.Ordinal);
        Assert.Contains("_weeksCollection.CollectionChanged -= WeeksCollection_CollectionChanged", control, StringComparison.Ordinal);
        Assert.Contains("AttachWeeksCollection();", control, StringComparison.Ordinal);
        Assert.Contains("DetachWeeksCollection();", control, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Exchange(ref _renderQueued, 1)", control, StringComparison.Ordinal);
        Assert.Contains("RequestRender();", control, StringComparison.Ordinal);
        Assert.Contains("CanvasControl _calendarCanvas", control, StringComparison.Ordinal);
        Assert.Contains("ToolTipService.SetToolTip(_calendarCanvas, _cellToolTip);", control, StringComparison.Ordinal);
        Assert.Contains("_calendarCanvas.Invalidate();", control, StringComparison.Ordinal);
        Assert.Contains("_contributionColors.TryGetValue(day.ColorHex", control, StringComparison.Ordinal);
        Assert.DoesNotContain("_calendarGrid.Children.Add", control, StringComparison.Ordinal);
        Assert.DoesNotContain("new Border", control, StringComparison.Ordinal);
        Assert.DoesNotContain("Days.Take(7).ToArray()", control, StringComparison.Ordinal);
        Assert.DoesNotContain("WeeksCollection_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)\n    {\n        if (DispatcherQueue.HasThreadAccess)", control.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("_preserveUserSelection && TryGetSelectedCell", control, StringComparison.Ordinal);
        Assert.Contains("_preserveUserSelection = true;", control, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileOwnedIdentityRowsUseTheReusableInternalAvatarRoute()
    {
        XDocument document = XDocument.Load(XamlPath());
        XElement[] actionableAvatars = document.Descendants()
            .Where(static element => element.Name.LocalName == "Avatar"
                && element.Attribute("IsProfileNavigationEnabled")?.Value == "True")
            .ToArray();

        Assert.NotEmpty(actionableAvatars);
        Assert.All(actionableAvatars, static avatar =>
        {
            Assert.NotNull(avatar.Attribute("Login"));
            Assert.NotNull(avatar.Attribute("NavigationSource"));
        });
    }

    [Fact]
    public void SharedMarkdownRouterKeepsGitHubWorkItemsInsideTheApp()
    {
        string root = Root();
        string viewer = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));
        string policy = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "MarkdownLinkNavigationPolicy.cs"));

        Assert.Contains("MarkdownGitHubRouteKind.Issue", policy, StringComparison.Ordinal);
        Assert.Contains("MarkdownGitHubRouteKind.PullRequest", policy, StringComparison.Ordinal);
        Assert.Contains("new IssueNavArg(repository, route.Number", viewer, StringComparison.Ordinal);
        Assert.Contains("new PullRequestPageNavArg(repository, route.Number", viewer, StringComparison.Ordinal);
        Assert.Contains("await Launcher.LaunchUriAsync(uri)", viewer, StringComparison.Ordinal);
    }

    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string XamlPath() => Path.Combine(Root(), "JitHub.WinUI", "Views", "Pages", "ProfilePage.xaml");
    private static string CodeBehindPath() => Path.Combine(Root(), "JitHub.WinUI", "Views", "Pages", "ProfilePage.xaml.cs");
    private static string ViewModelPath() => Path.Combine(Root(), "JitHub.WinUI", "ViewModels", "Pages", "ProfilePageViewModel.cs");
    private static string ServicePath() => Path.Combine(Root(), "JitHub.WinUI", "Services", "Profile", "GitHubProfileQueryService.cs");

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int start = 0;
        while ((start = source.IndexOf(value, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += value.Length;
        }

        return count;
    }

    private static string Root()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
