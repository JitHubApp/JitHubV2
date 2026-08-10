using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class WorkspaceChromeAdoptionTests
{
    public static IEnumerable<object[]> CanonicalPages()
    {
        yield return ["DashboardPage", "Dashboard"];
        yield return ["ProfilePage", "Profile"];
        yield return ["NotificationsPage", "Notifications"];
        yield return ["StarsPage", "Stars"];
        yield return ["GistsPage", "Gists"];
        yield return ["RepoSearchResultPage", "RepositorySearch"];
    }

    [Theory]
    [MemberData(nameof(CanonicalPages))]
    public void CanonicalPage_UsesSharedContractAndVisualComposition(
        string pageName,
        string contractName)
    {
        string source = File.ReadAllText(Path.Combine(PagesPath(), pageName + ".xaml.cs"));

        Assert.Contains($"WorkspaceChromeContracts.{contractName}", source, StringComparison.Ordinal);
        Assert.Contains("WorkspaceChromeVisuals.ApplyRoot", source, StringComparison.Ordinal);
        Assert.Contains("WorkspaceChromeVisuals.ApplyHeader", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DashboardPage")]
    [InlineData("ProfilePage")]
    [InlineData("NotificationsPage")]
    [InlineData("StarsPage")]
    [InlineData("GistsPage")]
    public void ActionBearingPage_UsesSharedLabelAndButtonCollapse(string pageName)
    {
        string source = File.ReadAllText(Path.Combine(PagesPath(), pageName + ".xaml.cs"));

        Assert.Contains("WorkspaceChromeVisuals.ApplyActionLabel", source, StringComparison.Ordinal);
        Assert.Contains("WorkspaceChromeVisuals.ApplyActionButton", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ProfilePage")]
    [InlineData("NotificationsPage")]
    [InlineData("StarsPage")]
    public void ContextBearingPage_UsesSharedOptionalContextPolicy(string pageName)
    {
        string source = File.ReadAllText(Path.Combine(PagesPath(), pageName + ".xaml.cs"));

        Assert.Contains("WorkspaceChromeVisuals.ApplyOptionalContext", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ProfilePage")]
    [InlineData("NotificationsPage")]
    [InlineData("StarsPage")]
    [InlineData("RepoSearchResultPage")]
    public void AdaptiveCommandPage_UsesSharedPlacementPolicy(string pageName)
    {
        string source = File.ReadAllText(Path.Combine(PagesPath(), pageName + ".xaml.cs"));

        Assert.Contains("WorkspaceChromeVisuals.ApplyPlacement", source, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> TitledPages()
    {
        yield return ["DashboardPage.xaml"];
        yield return ["NotificationsPage.xaml"];
        yield return ["StarsPage.xaml"];
        yield return ["GistsPage.xaml"];
    }

    [Theory]
    [MemberData(nameof(TitledPages))]
    public void TitledWorkspace_UsesCanonicalTitleTypography(string fileName)
    {
        XDocument document = XDocument.Load(Path.Combine(PagesPath(), fileName));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        Assert.Contains(
            document.Descendants(presentation + "TextBlock"),
            element => ((string?)element.Attribute("Style"))?.Contains(
                "AppWorkspaceTitleTextBlockStyle",
                StringComparison.Ordinal) == true);
    }

    [Theory]
    [MemberData(nameof(CanonicalPages))]
    public void CanonicalPage_HeaderUsesStableSharedGrid(
        string pageName,
        string contractName)
    {
        _ = contractName;
        XDocument document = XDocument.Load(Path.Combine(PagesPath(), pageName + ".xaml"));

        XElement? header = document
            .Descendants()
            .FirstOrDefault(element =>
                ((string?)element.Attribute("Style"))?.Contains(
                    "AppWorkspaceHeaderStyle",
                    StringComparison.Ordinal) == true);

        Assert.NotNull(header);
        Assert.Equal("Grid", header!.Name.LocalName);
    }

    [Fact]
    public void Profile_SeparatesSelectorStatusAndCompactActionsIntoStableRows()
    {
        XDocument document = XDocument.Load(Path.Combine(PagesPath(), "ProfilePage.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement Find(string name) => document
            .Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == name);

        Assert.Equal("0", (string?)Find("MainColumnHost").Attribute("MinWidth"));
        Assert.Equal("0", (string?)Find("ProfileModeSelectorHost").Attribute("MinWidth"));
        Assert.NotNull(Find("ProfileHeaderStatusHost"));
        Assert.NotNull(Find("ProfileOptionalHeaderContextHost"));
        Assert.NotNull(Find("CompactIdentityActions"));

        string source = File.ReadAllText(Path.Combine(PagesPath(), "ProfilePage.xaml.cs"));
        Assert.Contains("ProfileModeSelectorHost", source, StringComparison.Ordinal);
        Assert.Contains("ProfileHeaderStatusHost", source, StringComparison.Ordinal);
        Assert.Contains("ApplyOptionalContext(ProfileOptionalHeaderContextHost", source, StringComparison.Ordinal);
        Assert.Contains("CompactIdentityActions", source, StringComparison.Ordinal);
        Assert.Contains("ContentBounds.Arrange", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedCatalog_OwnsHeaderTitleAndActionMetrics()
    {
        XDocument document = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Styles",
            "Primitives",
            "ControlCatalog.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        HashSet<string> styleKeys = document
            .Descendants()
            .Where(element => element.Name.LocalName == "Style")
            .Select(element => (string?)element.Attribute(x + "Key"))
            .Where(key => key is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("AppWorkspaceHeaderStyle", styleKeys);
        Assert.Contains("AppWorkspaceTitleTextBlockStyle", styleKeys);
        Assert.Contains("AppWorkspaceHeaderIconStyle", styleKeys);
        Assert.Contains("AppWorkspaceHeaderContextTextBlockStyle", styleKeys);
        Assert.Contains("AppWorkspaceHeaderActionsStyle", styleKeys);
    }

    private static string PagesPath() => Path.Combine(
        FindRepositoryRoot(),
        "JitHub.WinUI",
        "Views",
        "Pages");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
