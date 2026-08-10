using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Views;

public sealed class RepositoryLibraryWorkspaceContractTests
{
    [Fact]
    public void Workspace_KeepsCommandsFixedAndOnlyTheVirtualizedListScrolls()
    {
        XDocument document = XDocument.Load(XamlPath());
        XElement root = Assert.Single(document.Descendants(), static element =>
            element.Name.LocalName == "Grid" && element.Attribute(XName.Get("Name", XamlNamespace))?.Value == "WorkspaceRoot");
        XElement list = Assert.Single(document.Descendants(), static element =>
            element.Name.LocalName == "ListView" &&
            element.Attribute("AutomationProperties.AutomationId")?.Value == "RepositoryLibraryList");

        Assert.DoesNotContain(list.Ancestors(), static ancestor => ancestor.Name.LocalName == "ScrollViewer");
        Assert.Equal("RepoManagePageRoot", root.Attribute("AutomationProperties.AutomationId")?.Value);
        Assert.Equal("3", list.Attribute("Grid.Row")?.Value ?? list.Parent?.Attribute("Grid.Row")?.Value);
        Assert.Contains(root.Descendants(), static element =>
            element.Attribute("AutomationProperties.AutomationId")?.Value == "RepositoryLibrarySearch");
        Assert.Contains(root.Descendants(), static element =>
            element.Attribute("AutomationProperties.AutomationId")?.Value == "RepositoryLibraryFilter");
        Assert.Contains(root.Descendants(), static element =>
            element.Attribute("AutomationProperties.AutomationId")?.Value == "RepositoryLibrarySort");
    }

    [Fact]
    public void BrowseAndSelectionModesExposeNativeAccessibleActionsWithoutReload()
    {
        XDocument document = XDocument.Load(XamlPath());
        string source = File.ReadAllText(XamlPath());

        Assert.DoesNotContain("Reload", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(document.Descendants(), static element =>
            element.Attribute("AutomationProperties.AutomationId")?.Value == "RepositoryLibraryNew");
        Assert.Contains(document.Descendants(), static element =>
            element.Attribute("AutomationProperties.AutomationId")?.Value == "RepositoryLibrarySelectionMode");
        Assert.Contains(document.Descendants(), static element =>
            element.Attribute("AutomationProperties.AutomationId")?.Value == "RepositoryLibrarySelectionMode" &&
            element.Attribute("IsEnabled")?.Value.Contains("IsSelectionModeAvailable", StringComparison.Ordinal) == true);
        Assert.Contains(document.Descendants(), static element =>
            element.Name.LocalName == "CheckBox" &&
            element.Attribute("AutomationProperties.Name")?.Value.Contains("SelectionAutomationName", StringComparison.Ordinal) == true);
        Assert.Contains(document.Descendants(), static element =>
            element.Name.LocalName == "ListView" &&
            element.Attribute("AutomationProperties.AutomationId")?.Value == "RepositoryLibraryList" &&
            element.Attribute("SelectionMode")?.Value == "None" &&
            element.Attribute("SelectionChanged")?.Value == "RepositoriesList_SelectionChanged" &&
            element.Attribute("IsMultiSelectCheckBoxEnabled")?.Value == "False");

        string codeBehind = File.ReadAllText(Path.ChangeExtension(XamlPath(), ".xaml.cs"));
        string[] contextActions = ["RepositoryLibraryContextOpen", "RepositoryLibraryContextOwner", "RepositoryLibraryContextCopy", "RepositoryLibraryContextDelete"];
        Assert.All(contextActions, action => Assert.Contains(action, codeBehind, StringComparison.Ordinal));
        Assert.Contains("container.ContextFlyout = CreateRepositoryContextFlyout(item)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ListViewSelectionMode.Multiple", codeBehind, StringComparison.Ordinal);
        Assert.Contains("RepositoriesList.SelectedItems", codeBehind, StringComparison.Ordinal);
        Assert.Contains("container.IsSelected = selected", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ViewModel.SelectionStateChanged += ViewModel_SelectionStateChanged", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryRowsExposeDistinctPointerPressedAndSelectedStates()
    {
        XDocument document = XDocument.Load(XamlPath());
        XElement style = Assert.Single(document.Descendants(), static element =>
            element.Name.LocalName == "Style" &&
            element.Attribute(XName.Get("Key", XamlNamespace))?.Value == "RepositoryLibraryItemStyle");
        string[] requiredStates = ["PointerOver", "Pressed", "Selected", "PointerOverSelected", "Disabled"];

        Assert.All(requiredStates, state => Assert.Contains(style.Descendants(), element =>
            element.Name.LocalName == "VisualState" &&
            element.Attribute(XName.Get("Name", XamlNamespace))?.Value == state));
        Assert.Contains(style.Descendants(), static element =>
            element.Name.LocalName == "Setter" &&
            element.Attribute("Target")?.Value == "RowRoot.Background" &&
            element.Attribute("Value")?.Value.Contains("AppSurfaceSubtleBrush", StringComparison.Ordinal) == true);

        string codeBehind = File.ReadAllText(Path.ChangeExtension(XamlPath(), ".xaml.cs"));
        Assert.Contains("UIElement.PointerEnteredEvent", codeBehind, StringComparison.Ordinal);
        Assert.Contains("UIElement.PointerPressedEvent", codeBehind, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", codeBehind, StringComparison.Ordinal);
        Assert.Contains("DetachRepositoryRowPointerHandlers(container)", codeBehind, StringComparison.Ordinal);
        Assert.Contains("FindRepositoryRowSurface", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void CompactStateReflowsSearchAboveFiltersInsteadOfClippingCommands()
    {
        XDocument document = XDocument.Load(XamlPath());
        XElement compact = Assert.Single(document.Descendants(), static element =>
            element.Name.LocalName == "VisualState" &&
            element.Attribute(XName.Get("Name", XamlNamespace))?.Value == "Compact");
        string setters = string.Join(",", compact.Descendants()
            .Where(static element => element.Name.LocalName == "Setter")
            .Select(static element => element.Attribute("Target")?.Value));

        Assert.Contains("SearchHost.(Grid.ColumnSpan)", setters, StringComparison.Ordinal);
        Assert.Contains("CommandSecondaryColumn.Width", setters, StringComparison.Ordinal);
        Assert.Contains("CommandTertiaryColumn.Width", setters, StringComparison.Ordinal);
        Assert.Contains("FilterComboBox.(Grid.Row)", setters, StringComparison.Ordinal);
        Assert.Contains("SortComboBox.(Grid.Row)", setters, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellRailUsesCanonicalRepositoryIndexAndKeyedRows()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ShellPageViewModel.cs"));

        Assert.Contains("IGitHubRepositoryIndexService", source, StringComparison.Ordinal);
        Assert.Contains("RepositoryItems.ApplySnapshot", source, StringComparison.Ordinal);
        Assert.Contains("ApplyRepositoryIndexSnapshot", source, StringComparison.Ordinal);
        Assert.DoesNotContain("_gitHubPilotQueryService.GetRecentRepositoriesAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomationFixtureIsIsolatedAndCoversLargePermissionDiverseAccounts()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Pages",
            "RepoManagePage.xaml.cs"));

        Assert.Contains("AppDataPathPolicy.TryGetAutomationRoots", source, StringComparison.Ordinal);
        Assert.Contains("repository-library", source, StringComparison.Ordinal);
        Assert.Contains("new(135)", source, StringComparison.Ordinal);
        Assert.Contains("GitHubRepositoryPermissions", source, StringComparison.Ordinal);
        Assert.Contains("Admin = index % 4 == 0", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CachedWorkspace_DeactivatesAndReactivatesWithoutDisposingItsViewModel()
    {
        XDocument document = XDocument.Load(XamlPath());
        XElement page = document.Root ?? throw new InvalidDataException("Repository workspace XAML has no root page.");
        Assert.Equal("Page_Unloaded", page.Attribute("Unloaded")?.Value);

        string codeBehind = File.ReadAllText(Path.ChangeExtension(XamlPath(), ".xaml.cs"));
        Assert.Contains("Page_Unloaded", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ViewModel.Deactivate()", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ActivateAsync", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.Dispose()", codeBehind, StringComparison.Ordinal);

        string viewModel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "RepoManagePageViewModel.cs"));
        Assert.Contains("public async Task ActivateAsync()", viewModel, StringComparison.Ordinal);
        Assert.Contains("public void Deactivate()", viewModel, StringComparison.Ordinal);
        Assert.Contains("CancellationTokenSource session = new();", viewModel, StringComparison.Ordinal);
        Assert.Contains("SubscribeToRepositoryIndex()", viewModel, StringComparison.Ordinal);
        Assert.Contains("UnsubscribeFromRepositoryIndex()", viewModel, StringComparison.Ordinal);
        Assert.Contains("_activeSession = null", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("readonly CancellationTokenSource _lifetime", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewRepositoryFixturesAreExplicitlyLabeledInWorkspaceAndShell()
    {
        string root = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "RepoManagePageViewModel.cs"));
        string shell = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "ShellPageViewModel.cs"));
        string resources = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Strings",
            "en-US",
            "Resources.resw"));

        Assert.Contains("preview repositories", viewModel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preview public", shell, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("preview repositories", resources, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("public profile repositories", viewModel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("public profile repos", shell, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FilterAndSortChangesEmitTheirOwnTelemetryActions()
    {
        string viewModel = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "ViewModels",
            "Pages",
            "RepoManagePageViewModel.cs"));

        int filterHandler = viewModel.IndexOf(
            "partial void OnSelectedFilterOptionChanged",
            StringComparison.Ordinal);
        int sortHandler = viewModel.IndexOf(
            "partial void OnSelectedSortOptionChanged",
            StringComparison.Ordinal);
        Assert.True(filterHandler >= 0 && sortHandler > filterHandler);

        string filterBody = viewModel[filterHandler..sortHandler];
        string sortBody = viewModel[sortHandler..];
        Assert.Contains("TrackAction(\"filter_changed\"", filterBody, StringComparison.Ordinal);
        Assert.DoesNotContain("TrackAction(\"sort_changed\"", filterBody, StringComparison.Ordinal);
        Assert.Contains("TrackAction(\"sort_changed\"", sortBody, StringComparison.Ordinal);
    }

    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string XamlPath() => Path.Combine(
        FindRepositoryRoot(),
        "JitHub.WinUI",
        "Views",
        "Pages",
        "RepoManagePage.xaml");

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")) ||
                Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
