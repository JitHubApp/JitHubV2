using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using JitHub.Models.CodeViewer;
using JitHub.Services.CodeViewer;
using JitHub.WinUI.ViewModels.CodeViewer;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class RepoCodeAccessibilityContractTests
{
    [Fact]
    public void CodeWorkspace_DoesNotForceAHeightLargerThanCompactWindow()
    {
        XDocument document = LoadXaml("JitHub.WinUI", "Views", "Pages", "RepoCodePage.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement rootGrid = document.Descendants()
            .Single(element => string.Equals((string?)element.Attribute(x + "Name"), "RootGrid", StringComparison.Ordinal));

        Assert.Null(rootGrid.Attribute("MinHeight"));
    }

    [Fact]
    public void CodeWorkspace_InteractiveControlsExposeStableAutomationContracts()
    {
        XDocument codePage = LoadXaml("JitHub.WinUI", "Views", "Pages", "RepoCodePage.xaml");
        XDocument breadcrumb = LoadXaml("JitHub.WinUI", "Views", "Controls", "CodeViewer", "RepoCodeBreadcrumb.xaml");
        XDocument tree = LoadXaml("JitHub.WinUI", "Views", "Controls", "CodeViewer", "RepoFileTreeView.xaml");
        XDocument repoChrome = LoadXaml("JitHub.WinUI", "Views", "Pages", "RepoDetailPage.xaml");
        XDocument unsupported = LoadXaml("JitHub.WinUI", "Views", "Controls", "CodeViewer", "Renderers", "UnsupportedPreview.xaml");
        XDocument codePreview = LoadXaml("JitHub.WinUI", "Views", "Controls", "CodeViewer", "Renderers", "CodePreview.xaml");
        XDocument editor = LoadXaml("JitHub.WinUI", "Views", "Controls", "CodeViewer", "CodeEditorControl.xaml");

        AssertAutomationContracts(
            codePage,
            "RepoCodePageRoot",
            "RepoCodeAdaptiveWorkspace",
            "RepoCodeFileTreeHost",
            "RepoCodeCloseFileTreeButton",
            "RepoCodeReadingHost");
        AssertAutomationContracts(
            breadcrumb,
            "RepoCodeBreadcrumbBar",
            "RepoCodeOpenFileTreeButton",
            "RepoCodeBackButton",
            "RepoCodeForwardButton",
            "RepoCodeCompactFileName",
            "RepoCodeCopyPathButton",
            "RepoCodeCopyRawUrlButton",
            "RepoCodeOpenOnGitHubButton",
            "RepoCodeFileActionsOverflowButton",
            "RepoCodeOverflowCopyPath",
            "RepoCodeOverflowCopyRawLink",
            "RepoCodeOverflowOpenOnGitHub");
        AssertAutomationContracts(tree, "RepoCodeFileTreePane", "RepoCodeFileFilter", "RepoCodeFileTree");
        AssertAutomationContracts(
            codePreview,
            "RepoCodeFindButton",
            "RepoCodeSymbolsButton",
            "RepoCodeSymbolsList",
            "RepoCodeCopyLineLinkButton",
            "RepoCodeFindTextBox",
            "RepoCodePreviousMatchButton",
            "RepoCodeNextMatchButton",
            "RepoCodeCloseFindButton");
        Assert.Contains(
            codePreview.Descendants(),
            element => string.Equals(
                (string?)element.Attribute("AutomationProperties.AutomationId"),
                "RepoCodeOutlineEmptyState",
                StringComparison.Ordinal));
        AssertAutomationContracts(editor, "RepoCodeEditor");
        AssertAutomationContracts(
            repoChrome,
            "RepoDetailBranchPicker",
            "RepoDetailActionsMenuButton",
            "RepoDetailWatchMenuItem",
            "RepoDetailStarMenuItem",
            "RepoDetailForkMenuItem");
        AssertAutomationContracts(
            unsupported,
            "RepoCodeUnsupportedOpenOnGitHubButton",
            "RepoCodeUnsupportedCopyRawUrlButton");
    }

    [Fact]
    public void CodeWorkspace_UsesAdaptiveLeadingPaneAndACompactNamedOverflow()
    {
        XDocument codePage = LoadXaml("JitHub.WinUI", "Views", "Pages", "RepoCodePage.xaml");
        XDocument breadcrumb = LoadXaml("JitHub.WinUI", "Views", "Controls", "CodeViewer", "RepoCodeBreadcrumb.xaml");

        XElement workspace = codePage.Descendants()
            .Single(element => string.Equals((string?)element.Attribute("AutomationIdPrefix"), "RepoCode", StringComparison.Ordinal));
        Assert.Equal("False", (string?)workspace.Attribute("ShowPaneButtons"));
        Assert.Equal("980", (string?)workspace.Attribute("MediumBreakpoint"));
        Assert.NotNull(workspace.Elements().SingleOrDefault(element => element.Name.LocalName == "AdaptiveWorkspace.LeadingPane"));
        Assert.NotNull(workspace.Elements().SingleOrDefault(element => element.Name.LocalName == "AdaptiveWorkspace.PrimaryPane"));

        XElement overflow = breadcrumb.Descendants()
            .Single(element => string.Equals(
                (string?)element.Attribute("AutomationProperties.AutomationId"),
                "RepoCodeFileActionsOverflowButton",
                StringComparison.Ordinal));
        Assert.Equal("File actions", (string?)overflow.Attribute("AutomationProperties.Name"));
        Assert.Equal("Collapsed", (string?)overflow.Attribute("Visibility"));

        Assert.DoesNotContain(
            codePage.Descendants(),
            element => element.Name.LocalName == "GridSplitter");
    }

    [Fact]
    public void TreeNodesExposeFilenameAndStablePathBasedAutomationId()
    {
        var model = new RepoTreeNode
        {
            Name = "Program.cs",
            Path = "src/JitHub/Program.cs",
            Sha = "abc123",
            IsDirectory = false,
        };
        var viewModel = new RepoTreeNodeViewModel(model, new StubLanguageResolver());

        Assert.Equal("Program.cs", viewModel.ToString());
        Assert.Equal("Program.cs, file", viewModel.AutomationName);
        Assert.Equal(
            RepoCodeAutomation.CreateId("RepoCodeTreeItem", "path:src/JitHub/Program.cs"),
            viewModel.AutomationId);
        Assert.StartsWith("RepoCodeTreeItem_path_src_JitHub_Program_", viewModel.AutomationId, StringComparison.Ordinal);
    }

    [Fact]
    public void TreeItemTemplateAnnotatesEachRealizedContainer()
    {
        XDocument tree = LoadXaml("JitHub.WinUI", "Views", "Controls", "CodeViewer", "RepoFileTreeView.xaml");
        XElement itemContent = tree.Descendants()
            .Single(element => string.Equals((string?)element.Attribute("Loaded"), "OnTreeItemContentLoaded", StringComparison.Ordinal));

        Assert.Equal("Grid", itemContent.Name.LocalName);
        Assert.Null(itemContent.Attribute("AutomationProperties.AutomationId"));
        Assert.Null(itemContent.Attribute("Tag"));

        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "RepoFileTreeView.xaml.cs"));
        Assert.Contains("DataContext: RepoTreeNodeViewModel boundNode", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.TryEnqueue", source, StringComparison.Ordinal);
        Assert.Contains("FileTreeView.ContainerFromNode(treeNode) is TreeViewItem container", source, StringComparison.Ordinal);
        Assert.Contains("AnnotateRealizedTreeItems(FileTreeView.RootNodes);", source, StringComparison.Ordinal);
        Assert.Contains("ItemContainerStyleSelector = new RepoTreeItemStyleSelector(ConfigureTreeItemContainer);", source, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.SetAutomationId(container, node.AutomationId);", source, StringComparison.Ordinal);

        string selector = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "RepoTreeItemStyleSelector.cs"));
        Assert.Contains("TreeViewNode { Content: RepoTreeNodeViewModel treeNode }", selector, StringComparison.Ordinal);
        Assert.Contains("_configureContainer(treeViewItem, node);", selector, StringComparison.Ordinal);
    }

    [Fact]
    public void FileTreeReadinessIsReappliedAfterResponsiveReparenting()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "RepoFileTreeView.xaml.cs"));

        Assert.Contains(
            "Volatile.Read(ref _lifetimeCts) is { IsCancellationRequested: false }",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "A responsive reparent can unload and reload the same control",
            source,
            StringComparison.Ordinal);
        int loadedStart = source.IndexOf("private void OnLoaded", StringComparison.Ordinal);
        int unloadedStart = source.IndexOf("private void OnUnloaded", loadedStart, StringComparison.Ordinal);
        Assert.True(loadedStart >= 0 && unloadedStart > loadedStart);
        string loadedImplementation = source[loadedStart..unloadedStart];
        Assert.Contains("if (!viewModel.IsLoading)", loadedImplementation, StringComparison.Ordinal);
        Assert.Contains("UpdateTreeView(viewModel);", loadedImplementation, StringComparison.Ordinal);
    }

    [Fact]
    public void InvokingTheSelectedFileStillNotifiesTheWorkspace()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "RepoFileTreeView.xaml.cs"));

        int pointerStart = source.IndexOf("private void OnTreeItemPointerPressed", StringComparison.Ordinal);
        int pointerEnd = source.IndexOf("private static TreeViewItem? FindTreeViewItem", pointerStart, StringComparison.Ordinal);
        int invokeStart = source.IndexOf("private void OnItemInvoked", StringComparison.Ordinal);
        int invokeEnd = source.IndexOf("private void SelectFileNode", invokeStart, StringComparison.Ordinal);
        Assert.True(pointerStart >= 0 && pointerEnd > pointerStart);
        Assert.True(invokeStart >= 0 && invokeEnd > invokeStart);
        Assert.Contains("RaiseFileInvoked(nodeVm);", source[pointerStart..pointerEnd], StringComparison.Ordinal);
        Assert.Contains("container?.Focus(FocusState.Pointer);", source[pointerStart..pointerEnd], StringComparison.Ordinal);
        Assert.Contains("RaiseFileInvoked(nodeVm);", source[invokeStart..invokeEnd], StringComparison.Ordinal);
        Assert.Contains("new RepoFileInvokedEventArgs(node, node.AutomationId)", source, StringComparison.Ordinal);
        Assert.Contains("public bool Handled { get; set; }", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BreadcrumbSegmentsExposeMeaningfulStableAutomationContracts()
    {
        var segment = new BreadcrumbSegment("CodeViewer", "src/CodeViewer", IsRoot: false);

        Assert.Equal("Open CodeViewer", segment.AutomationName);
        Assert.Equal("src/CodeViewer", segment.AutomationPath);
        Assert.Equal(
            RepoCodeAutomation.CreateId("RepoCodeBreadcrumbSegment", "path:src/CodeViewer"),
            segment.AutomationId);
    }

    [Fact]
    public void AutomationIdsHashRawSemanticIdentityWithoutWhitespaceCollisions()
    {
        string pathWithWhitespace = RepoCodeAutomation.CreateId("RepoCodeTreeItem", "path:src/ file.cs ");
        string trimmedPath = RepoCodeAutomation.CreateId("RepoCodeTreeItem", "path:src/ file.cs");
        string root = RepoCodeAutomation.CreateId("RepoCodeBreadcrumbSegment", "root:src/file.cs");
        string path = RepoCodeAutomation.CreateId("RepoCodeBreadcrumbSegment", "path:src/file.cs");

        Assert.NotEqual(pathWithWhitespace, trimmedPath);
        Assert.NotEqual(root, path);
    }

    [Fact]
    public void OutlineSymbolsExposeStableSemanticAutomationIdentity()
    {
        CodeSymbol symbol = new("Render", "method", 42);

        Assert.Equal(
            RepoCodeAutomation.CreateId("RepoCodeOutlineItem", "symbol:method:Render:42"),
            symbol.AutomationId);
        Assert.Equal("method Render, line 42", symbol.AutomationName);
    }

    [Fact]
    public void RepoCodeInteractionSitesEmitTheCanonicalActionTaxonomy()
    {
        string root = FindRepositoryRoot();
        string preview = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "CodeViewer", "Renderers", "CodePreview.xaml.cs"));
        string unsupported = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Controls", "CodeViewer", "Renderers", "UnsupportedPreview.xaml.cs"));
        string breadcrumb = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "ViewModels", "CodeViewer", "RepoCodeBreadcrumbViewModel.cs"));
        string page = File.ReadAllText(Path.Combine(
            root, "JitHub.WinUI", "Views", "Pages", "RepoCodePage.xaml.cs"));

        Assert.Contains("RepoCodeTelemetryActions.Find", preview, StringComparison.Ordinal);
        Assert.Contains("RepoCodeTelemetryActions.Outline", preview, StringComparison.Ordinal);
        Assert.Contains("RepoCodeTelemetryActions.CopyLineLink", preview, StringComparison.Ordinal);
        Assert.Contains("RepoCodeTelemetryActions.CopyPath", breadcrumb, StringComparison.Ordinal);
        Assert.Contains("RepoCodeTelemetryActions.CopyRaw", breadcrumb, StringComparison.Ordinal);
        Assert.Contains("RepoCodeTelemetryActions.ExternalOpen", breadcrumb, StringComparison.Ordinal);
        Assert.Contains("RepoCodeTelemetryActions.ExternalOpen", unsupported, StringComparison.Ordinal);
        Assert.Contains("RepoCodeTelemetryActions.Drawer", page, StringComparison.Ordinal);
    }

    [Fact]
    public void AdaptiveWorkspace_DrawerFocusSentinelsUseRealNonInteractiveButtons()
    {
        XDocument workspace = LoadXaml("JitHub.WinUI", "Views", "Controls", "App", "AdaptiveWorkspace.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] names =
        [
            "LeftDrawerStartFocusSentinel",
            "LeftDrawerEndFocusSentinel",
            "RightDrawerStartFocusSentinel",
            "RightDrawerEndFocusSentinel",
        ];

        foreach (string name in names)
        {
            XElement sentinel = workspace.Descendants()
                .Single(element => string.Equals((string?)element.Attribute(x + "Name"), name, StringComparison.Ordinal));
            Assert.Equal("Button", sentinel.Name.LocalName);
            Assert.Equal("Raw", (string?)sentinel.Attribute("AutomationProperties.AccessibilityView"));
            Assert.Equal("False", (string?)sentinel.Attribute("IsHitTestVisible"));
            Assert.Equal("0", (string?)sentinel.Attribute("Opacity"));
        }
    }

    [Fact]
    public void RepoCodeLocalizedXamlContractsHaveResourceEntries()
    {
        XDocument resources = LoadXaml("JitHub.WinUI", "Strings", "en-US", "Resources.resw");
        HashSet<string> resourceNames = resources.Root!
            .Elements("data")
            .Select(element => (string?)element.Attribute("name"))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XDocument[] documents =
        [
            LoadXaml("JitHub.WinUI", "Views", "Pages", "RepoCodePage.xaml"),
            LoadXaml("JitHub.WinUI", "Views", "Controls", "CodeViewer", "CodeEditorControl.xaml"),
            LoadXaml("JitHub.WinUI", "Views", "Controls", "CodeViewer", "FilePreviewHost.xaml"),
            LoadXaml("JitHub.WinUI", "Views", "Controls", "CodeViewer", "RepoCodeBreadcrumb.xaml"),
            LoadXaml("JitHub.WinUI", "Views", "Controls", "CodeViewer", "RepoFileTreeView.xaml"),
            LoadXaml("JitHub.WinUI", "Views", "Controls", "CodeViewer", "Renderers", "CodePreview.xaml"),
            LoadXaml("JitHub.WinUI", "Views", "Controls", "CodeViewer", "Renderers", "UnsupportedPreview.xaml"),
        ];

        foreach (string uid in documents
            .SelectMany(document => document.Root!.DescendantsAndSelf())
            .Select(element => (string?)element.Attribute(x + "Uid"))
            .Where(static uid => !string.IsNullOrWhiteSpace(uid))
            .Cast<string>())
        {
            Assert.Contains(resourceNames, name => name.StartsWith(uid + ".", StringComparison.Ordinal));
        }

        string[] dynamicResourceKeys =
        [
            "RepoCode/LoadingRefShowingPrevious",
            "RepoCode/Error/CachedTreeRefreshFailed",
            "RepoCode/Error/RefLoadFailedShowingPrevious",
            "RepoCode/Error/RefreshFailedShowingPrevious",
            "RepoCode/Error/FileRefreshFailed",
            "RepoCode/Error/PathMissing",
            "RepoCode/Error/FilterFailed",
            "RepoCode/Error/FolderRefreshFailed",
            "RepoCode/Error/CachedFolderRefreshFailed",
            "RepoCode/Error/PartialTreeRefreshFailed",
            "RepoCode/Breadcrumb/OpenRootAutomationName",
            "RepoCode/Breadcrumb/OpenPathAutomationName",
            "RepoCode/Tree/FolderAutomationName",
            "RepoCode/Tree/FileAutomationName",
            "RepoCode/Unsupported/TooLarge",
            "RepoCode/Unsupported/FileType",
            "RepoCode/EditorHighContrastStatus",
        ];
        Assert.All(dynamicResourceKeys, key => Assert.Contains(key, resourceNames));
    }

    [Fact]
    public void NativeEditorHasExplicitSystemHighContrastTreatment()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "CodeViewer",
            "CodeEditorControl.xaml.cs"));

        Assert.Contains("_accessibilitySettings.HighContrastChanged +=", source, StringComparison.Ordinal);
        Assert.Contains("_accessibilitySettings.HighContrastChanged -=", source, StringComparison.Ordinal);
        Assert.Contains("UIColorType.Background", source, StringComparison.Ordinal);
        Assert.Contains("UIColorType.Foreground", source, StringComparison.Ordinal);
        Assert.Contains("for (int style = 0; style <= 127; style++)", source, StringComparison.Ordinal);
        Assert.Contains("editor.SetSelFore(true", source, StringComparison.Ordinal);
        Assert.Contains("RepoCode/EditorHighContrastStatus", source, StringComparison.Ordinal);
    }

    private static void AssertAutomationContracts(XDocument document, params string[] automationIds)
    {
        Dictionary<string, XElement> elements = document.Descendants()
            .Where(element => element.Attribute("AutomationProperties.AutomationId") is not null)
            .ToDictionary(
                element => (string)element.Attribute("AutomationProperties.AutomationId")!,
                StringComparer.Ordinal);

        foreach (string automationId in automationIds)
        {
            XElement element = Assert.Contains(automationId, elements);
            Assert.False(string.IsNullOrWhiteSpace((string?)element.Attribute("AutomationProperties.Name")));
        }
    }

    private static XDocument LoadXaml(params string[] pathParts) =>
        XDocument.Load(Path.Combine([FindRepositoryRoot(), .. pathParts]));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "JitHub.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }

    private sealed class StubLanguageResolver : ILanguageIdResolver
    {
        public string Resolve(string fileName, ReadOnlySpan<byte> contentSniff = default) => "csharp";

        public bool IsKnown(string fileName) => true;
    }
}
