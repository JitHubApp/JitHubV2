using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed partial class ControlCatalogAdoptionTests
{
    private static readonly string[] CanonicalWorkspaceFiles =
    [
        "DashboardPage.xaml",
        "SettingsPage.xaml",
        "MyIssuesPage.xaml",
        "MyPullRequestsPage.xaml",
        "RepoCodePage.xaml",
        "ProfilePage.xaml",
        "RepoSearchResultPage.xaml",
        "StarsPage.xaml",
        "RepoManagePage.xaml",
        "GistsPage.xaml",
        "NotificationsPage.xaml",
        "ShellPage.xaml",
        "RepoIssuePage.xaml",
        "RepoPullRequestPage.xaml",
        "RepoCommitsPage.xaml"
    ];

    private static readonly HashSet<string> ApprovedLocalListViewTemplates =
    [
        "RepoManagePage.xaml/RepositoryLibraryItemStyle"
    ];

    private static readonly IReadOnlyDictionary<string, int> ExpectedLocalRowStyleCounts =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["StarsPage.xaml"] = 2,
            ["RepoManagePage.xaml"] = 1,
            ["GistsPage.xaml"] = 1,
            ["NotificationsPage.xaml"] = 1,
            ["ShellPage.xaml"] = 2
        };

    private static readonly HashSet<string> CanonicalRowBases =
    [
        "AppDenseListRowStyle",
        "AppDenseFlatListRowStyle",
        "AppWorkspaceCardListRowStyle",
        "AppCompactNavigationRowStyle",
        "AppRepositoryListRowStyle"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> CanonicalSemanticSurfaceStyles =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["DashboardPage.xaml"] = ["AppWorkspaceHeaderStyle", "AppStatusInfoBarStyle"],
            ["SettingsPage.xaml"] = ["AppWorkspaceHeaderStyle", "AppErrorInfoBarStyle"],
            ["MyIssuesPage.xaml"] = ["AppEmptyStatePanelStyle"],
            ["MyPullRequestsPage.xaml"] = ["AppEmptyStatePanelStyle"],
            ["RepoCodePage.xaml"] = ["AppStatusInfoBarStyle"],
            ["ProfilePage.xaml"] = ["AppWorkspaceHeaderStyle", "AppEmptyStatePanelStyle"],
            ["RepoSearchResultPage.xaml"] = ["AppWorkspaceHeaderStyle", "AppEmptyStatePanelStyle", "AppStatusInfoBarStyle"],
            ["StarsPage.xaml"] = ["AppWorkspaceHeaderStyle", "AppEmptyStatePanelStyle", "AppStatusInfoBarStyle"],
            ["RepoManagePage.xaml"] = ["AppWorkspaceHeaderStyle"],
            ["GistsPage.xaml"] = ["AppWorkspaceHeaderStyle", "AppEmptyStatePanelStyle", "AppStatusInfoBarStyle", "AppErrorInfoBarStyle"],
            ["NotificationsPage.xaml"] = ["AppWorkspaceHeaderStyle", "AppEmptyStatePanelStyle", "AppErrorInfoBarStyle"],
            ["ShellPage.xaml"] = ["AppStatusInfoBarStyle"],
            ["RepoIssuePage.xaml"] = ["AppWorkspaceHeaderStyle", "AppEmptyStatePanelStyle", "AppStatusInfoBarStyle"],
            ["RepoPullRequestPage.xaml"] = ["AppWorkspaceHeaderStyle", "AppEmptyStatePanelStyle"],
            ["RepoCommitsPage.xaml"] = ["AppWorkspaceHeaderStyle", "AppEmptyStatePanelStyle", "AppStatusInfoBarStyle"]
        };

    private static readonly IReadOnlyDictionary<string, int> ExpectedRuntimeDialogInstancesByFile =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine("Views", "Dialogs", "AccountSignOutDialogFlow.cs")] = 1,
            [Path.Combine("Views", "Pages", "GistsPage.xaml.cs")] = 2,
            [Path.Combine("Views", "Pages", "ProfilePage.xaml.cs")] = 1,
            [Path.Combine("Views", "Pages", "RepoIssuePage.xaml.cs")] = 5,
            [Path.Combine("Views", "Pages", "RepoManagePage.xaml.cs")] = 2,
            [Path.Combine("Views", "Pages", "RepoPullRequestPage.xaml.cs")] = 7,
            [Path.Combine("Views", "Pages", "SettingsPage.xaml.cs")] = 1,
            [Path.Combine("Views", "Pages", "StarsPage.xaml.cs")] = 1
        };

    [Fact]
    public void EveryCatalogStyle_HasAConcreteConsumerOutsideTheCatalog()
    {
        string root = FindRepositoryRoot();
        string productRoot = Path.Combine(root, "JitHub.WinUI");
        string catalogPath = GetCatalogPath(root);
        XDocument catalog = XDocument.Load(catalogPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] styleKeys = catalog.Root!
            .Elements()
            .Where(element => element.Name.LocalName == "Style")
            .Select(element => (string?)element.Attribute(x + "Key"))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key!)
            .ToArray();

        string[] consumerSources = Directory.EnumerateFiles(productRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !string.Equals(path, catalogPath, StringComparison.OrdinalIgnoreCase))
            .Select(File.ReadAllText)
            .ToArray();

        Assert.All(
            styleKeys,
            key => Assert.Contains(
                consumerSources,
                source => source.Contains($"StaticResource {key}", StringComparison.Ordinal)
                    || source.Contains($"\"{key}\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void CatalogStyles_HaveValidAcyclicBasedOnChainsAndStateResources()
    {
        XDocument catalog = XDocument.Load(GetCatalogPath(FindRepositoryRoot()));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Dictionary<string, XElement> styles = catalog.Root!
            .Elements()
            .Where(element => element.Name.LocalName == "Style")
            .Where(element => element.Attribute(x + "Key") is not null)
            .ToDictionary(element => (string)element.Attribute(x + "Key")!, StringComparer.Ordinal);

        foreach ((string key, XElement style) in styles)
        {
            HashSet<string> visited = [key];
            string? basedOn = ReadStaticResourceKey((string?)style.Attribute("BasedOn"));
            while (basedOn is not null && styles.TryGetValue(basedOn, out XElement? parent))
            {
                Assert.True(visited.Add(basedOn), $"Style inheritance cycle detected at {key} -> {basedOn}.");
                basedOn = ReadStaticResourceKey((string?)parent.Attribute("BasedOn"));
            }
        }

        AssertBasedOn(styles, "AppCommandSearchTextBoxStyle", "AppCommandTextBoxStyle");
        AssertBasedOn(styles, "AppDenseFlatListRowStyle", "AppDenseListRowStyle");
        AssertBasedOn(styles, "AppWorkspaceCardListRowStyle", "AppDenseListRowStyle");
        AssertBasedOn(styles, "AppCompactNavigationRowStyle", "AppDenseListRowStyle");
        AssertBasedOn(styles, "AppRepositoryListRowStyle", "AppDenseListRowStyle");
        AssertBasedOn(styles, "AppSearchResultRowStyle", "AppDenseListRowStyle");
        AssertBasedOn(styles, "AppInlineEmptyStatePanelStyle", "AppEmptyStatePanelStyle");
        AssertBasedOn(styles, "AppErrorInfoBarStyle", "AppStatusInfoBarStyle");

        HashSet<string> brushKeys = catalog.Root!
            .Elements()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .Select(element => (string?)element.Attribute(x + "Key"))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key!)
            .ToHashSet(StringComparer.Ordinal);
        string[] interactionStates = ["", "PointerOver", "Pressed", "Disabled"];
        foreach (string state in interactionStates)
        {
            Assert.Contains($"CheckBoxCheckBackgroundFillUnchecked{state}", brushKeys);
            Assert.Contains($"CheckBoxCheckBackgroundFillChecked{state}", brushKeys);
            Assert.Contains($"CheckBoxCheckBackgroundStrokeUnchecked{state}", brushKeys);
            Assert.Contains($"CheckBoxCheckBackgroundStrokeChecked{state}", brushKeys);
            Assert.Contains($"CheckBoxCheckGlyphForegroundChecked{state}", brushKeys);
        }

        Assert.DoesNotContain(brushKeys, key => key.StartsWith("CheckBoxBackground", StringComparison.Ordinal));
        Assert.DoesNotContain(brushKeys, key => key.StartsWith("CheckBoxBorderBrush", StringComparison.Ordinal));

        Assert.All(
            catalog.Root!.Elements().Where(element => element.Name.LocalName == "SolidColorBrush"),
            brush => Assert.StartsWith("{ThemeResource ", (string?)brush.Attribute("Color") ?? string.Empty, StringComparison.Ordinal));
    }

    [Fact]
    public void CompactCalendarDatePicker_UsesTheAppCalendarBaseStyle()
    {
        string catalogSource = File.ReadAllText(GetCatalogPath(FindRepositoryRoot()));
        XDocument catalog = XDocument.Parse(catalogSource);
        XElement style = FindCatalogStyle(catalog, "AppCompactCalendarDatePickerStyle");

        Assert.Equal("CalendarDatePicker", ReadStaticResourceKey((string?)style.Attribute("BasedOn")));
        Assert.DoesNotContain("DefaultCalendarDatePickerStyle", catalogSource, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceCardRowTemplate_OwnsSharedGeometryAndInteractionStates()
    {
        XDocument catalog = XDocument.Load(GetCatalogPath(FindRepositoryRoot()), LoadOptions.SetLineInfo);
        XElement style = FindCatalogStyle(catalog, "AppWorkspaceCardListRowStyle");
        XElement templateSetter = style.Elements().Single(IsTemplateSetter);
        XElement template = templateSetter.Descendants().Single(element => element.Name.LocalName == "ControlTemplate");
        XElement cardRoot = template.Descendants().Single(element =>
            element.Name.LocalName == "Border"
            && string.Equals(ReadXamlName(element), "CardRoot", StringComparison.Ordinal));

        Assert.Equal("76", (string?)cardRoot.Attribute("MinHeight"));
        Assert.Equal("12", (string?)cardRoot.Attribute("Padding"));
        Assert.Equal("{ThemeResource AppCardBrush}", (string?)cardRoot.Attribute("Background"));
        Assert.Equal("{ThemeResource AppHairlineBrush}", (string?)cardRoot.Attribute("BorderBrush"));
        Assert.Equal("1", (string?)cardRoot.Attribute("BorderThickness"));
        Assert.Equal("{ThemeResource AppRadiusSmall}", (string?)cardRoot.Attribute("CornerRadius"));

        string[] requiredStates =
        [
            "Normal",
            "PointerOver",
            "Pressed",
            "Selected",
            "PointerOverSelected",
            "PressedSelected",
            "SelectedUnfocused",
            "Disabled"
        ];
        Dictionary<string, XElement> states = template.Descendants()
            .Where(element => element.Name.LocalName == "VisualState")
            .ToDictionary(element => ReadXamlName(element)!, StringComparer.Ordinal);
        Assert.All(requiredStates, state => Assert.Contains(state, states.Keys));

        AssertStateSetter(states["PointerOver"], "CardRoot.Background", "{ThemeResource AppRowHoverBrush}");
        AssertStateSetter(states["PointerOver"], "CardRoot.BorderBrush", "{ThemeResource AppOutlineStrongBrush}");
        AssertStateSetter(states["Pressed"], "CardRoot.Background", "{ThemeResource AppRowPressedBrush}");
        AssertStateSetter(states["Pressed"], "CardRoot.BorderBrush", "{ThemeResource AppAccentBrush}");
        foreach (string selectedState in new[] { "Selected", "PointerOverSelected", "SelectedUnfocused" })
        {
            AssertStateSetter(states[selectedState], "CardRoot.Background", "{ThemeResource AppRowSelectedBrush}");
            AssertStateSetter(states[selectedState], "CardRoot.BorderBrush", "{ThemeResource AppAccentBrush}");
        }

        AssertStateSetter(states["PressedSelected"], "CardRoot.Background", "{ThemeResource AppRowPressedBrush}");
        AssertStateSetter(states["PressedSelected"], "CardRoot.BorderBrush", "{ThemeResource AppAccentBrush}");
        AssertStateSetter(states["Disabled"], "CardRoot.Opacity", "0.55");

        XElement presenter = template.Descendants().Single(element => element.Name.LocalName == "ContentPresenter");
        Assert.Equal("{TemplateBinding Content}", (string?)presenter.Attribute("Content"));
        Assert.Equal("{TemplateBinding ContentTemplate}", (string?)presenter.Attribute("ContentTemplate"));
        Assert.Equal("{TemplateBinding ContentTemplateSelector}", (string?)presenter.Attribute("ContentTemplateSelector"));
    }

    [Fact]
    public void CanonicalWorkspaceListRows_DeriveFromCatalogAndDoNotRepeatSharedGeometry()
    {
        string pagesRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views", "Pages");
        int discovered = 0;
        foreach (string fileName in CanonicalWorkspaceFiles)
        {
            XDocument document = XDocument.Load(Path.Combine(pagesRoot, fileName), LoadOptions.SetLineInfo);
            XElement[] rowStyles = document.Descendants().Where(IsListViewItemStyle).ToArray();
            int expectedLocalStyles = ExpectedLocalRowStyleCounts.GetValueOrDefault(fileName);
            Assert.Equal(expectedLocalStyles, rowStyles.Length);
            foreach (XElement style in rowStyles)
            {
                discovered++;
                string? baseKey = ReadStaticResourceKey((string?)style.Attribute("BasedOn"));
                Assert.True(
                    baseKey is not null && CanonicalRowBases.Contains(baseKey),
                    $"{fileName} contains a ListViewItem style that does not derive from a catalog row base.");

                string styleKey = ReadXamlKey(style) ?? string.Empty;
                HashSet<string> allowedOverrides = GetAllowedRowOverrides(fileName, styleKey);
                string[] repeatedGeometry = style.Elements()
                    .Where(element => element.Name.LocalName == "Setter")
                    .Select(element => (string?)element.Attribute("Property"))
                    .Where(static property => property is not null)
                    .Select(static property => property!)
                    .Where(property => SharedRowGeometry.Contains(property) && !allowedOverrides.Contains(property))
                    .ToArray();
                Assert.True(
                    repeatedGeometry.Length == 0,
                    $"{fileName}/{styleKey} repeats shared row geometry: {string.Join(", ", repeatedGeometry)}.");

                XElement? template = style.Elements()
                    .FirstOrDefault(element => string.Equals((string?)element.Attribute("Property"), "Template", StringComparison.Ordinal));
                if (template is not null)
                {
                    string localStyleIdentity = $"{fileName}/{styleKey}";
                    Assert.Contains(localStyleIdentity, ApprovedLocalListViewTemplates);
                    Assert.False(
                        IsWorkspaceCardInteractionTemplate(template),
                        $"{localStyleIdentity} duplicates the catalog workspace-row template geometry and states.");
                }
            }
        }

        Assert.Equal(ExpectedLocalRowStyleCounts.Values.Sum(), discovered);

        string[] directWorkspaceRowConsumers =
        [
            "MyIssuesPage.xaml",
            "MyPullRequestsPage.xaml",
            "RepoIssuePage.xaml",
            "RepoPullRequestPage.xaml",
            "RepoCommitsPage.xaml"
        ];
        Assert.All(
            directWorkspaceRowConsumers,
            fileName =>
            {
                string sourcePath = string.Equals(fileName, "RepoIssuePage.xaml", StringComparison.Ordinal)
                    ? Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views", "Controls", "Issue", "RepoIssueListPane.xaml")
                    : Path.Combine(pagesRoot, fileName);
                XDocument document = XDocument.Load(sourcePath, LoadOptions.SetLineInfo);
                Assert.Contains(
                    document.Descendants().Where(element => element.Name.LocalName == "ListView"),
                    listView => string.Equals(
                        ReadStaticResourceKey((string?)listView.Attribute("ItemContainerStyle")),
                        "AppWorkspaceCardListRowStyle",
                        StringComparison.Ordinal));
                Assert.DoesNotContain(
                    document.Descendants().Where(IsListViewItemStyle),
                    style => ReadStaticResourceKey((string?)style.Attribute("BasedOn")) == "AppWorkspaceCardListRowStyle"
                        || style.Elements().Any(IsTemplateSetter));
            });
    }

    [Fact]
    public void CanonicalWorkspaceSemanticSurfaces_ConsumeHeaderEmptyAndStatusPrimitives()
    {
        string pagesRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views", "Pages");
        Assert.Equal(
            CanonicalWorkspaceFiles.Order(StringComparer.Ordinal),
            CanonicalSemanticSurfaceStyles.Keys.Order(StringComparer.Ordinal));
        Assert.All(
            CanonicalSemanticSurfaceStyles,
            expectation =>
            {
                string sourcePath = string.Equals(expectation.Key, "RepoIssuePage.xaml", StringComparison.Ordinal)
                    ? Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views", "Controls", "Issue", "RepoIssueListPane.xaml")
                    : Path.Combine(pagesRoot, expectation.Key);
                XDocument document = XDocument.Load(sourcePath);
                Assert.All(
                    expectation.Value,
                    styleKey => Assert.True(
                        DocumentConsumesStyle(document, styleKey),
                        $"{expectation.Key} does not apply {styleKey} to a concrete element."));
            });

        XDocument emptyStateControl = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "EmptyStateView.xaml"));
        Assert.True(DocumentConsumesStyle(emptyStateControl, "AppInlineEmptyStatePanelStyle"));
        Assert.Contains(
            XDocument.Load(Path.Combine(pagesRoot, "RepoManagePage.xaml")).Descendants(),
            element => element.Name.LocalName == "EmptyStateView");

    }

    [Fact]
    public void SettingsAndProfileInteractiveLists_UseCanonicalRowStyles()
    {
        string pagesRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views", "Pages");
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> expectedStyles =
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
            {
                ["SettingsPage.xaml"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["SettingsSectionList"] = "AppCompactNavigationRowStyle"
                },
                ["ProfilePage.xaml"] = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ProfileRepositoriesList"] = "AppRepositoryListRowStyle",
                    ["ProfileStarsList"] = "AppRepositoryListRowStyle",
                    ["ProfileActivityList"] = "AppDenseFlatListRowStyle",
                    ["ProfileFollowersList"] = "AppDenseFlatListRowStyle",
                    ["ProfileFollowingList"] = "AppDenseFlatListRowStyle"
                }
            };

        Assert.All(
            expectedStyles,
            pageExpectation =>
            {
                XDocument document = XDocument.Load(Path.Combine(pagesRoot, pageExpectation.Key), LoadOptions.SetLineInfo);
                Assert.All(
                    pageExpectation.Value,
                    listExpectation =>
                    {
                        XElement list = document.Descendants().Single(element =>
                            element.Name.LocalName == "ListView"
                            && string.Equals(ReadAutomationId(element), listExpectation.Key, StringComparison.Ordinal));
                        Assert.Equal(
                            listExpectation.Value,
                            ReadStaticResourceKey((string?)list.Attribute("ItemContainerStyle")));
                    });
            });
    }

    [Fact]
    public void CanonicalWorkspaceCommandInputs_UseSharedThirtySixPixelStyles()
    {
        string pagesRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views", "Pages");
        int discovered = 0;
        foreach (string fileName in CanonicalWorkspaceFiles)
        {
            XDocument document = XDocument.Load(Path.Combine(pagesRoot, fileName), LoadOptions.SetLineInfo);
            foreach (XElement control in document.Descendants().Where(IsWorkspaceCommandInput))
            {
                discovered++;
                string automationId = ReadAutomationId(control)!;
                string styleKey = ReadStaticResourceKey((string?)control.Attribute("Style")) ?? string.Empty;
                string[] allowedStyles = control.Name.LocalName switch
                {
                    "TextBox" => ["AppCommandTextBoxStyle", "AppCommandSearchTextBoxStyle", "ShellSearchTextBoxStyle"],
                    "ComboBox" => ["AppCompactComboBoxStyle"],
                    "CalendarDatePicker" => ["AppCompactCalendarDatePickerStyle"],
                    _ => []
                };
                Assert.Contains(styleKey, allowedStyles);
                Assert.Null(control.Attribute("Height"));
                Assert.Null(control.Attribute("MinHeight"));
                Assert.False(string.IsNullOrWhiteSpace(automationId));
            }
        }

        Assert.True(discovered > 0, "No canonical workspace command inputs were discovered.");

        HashSet<string> compactPickerIds = CanonicalWorkspaceFiles
            .Select(fileName => XDocument.Load(Path.Combine(pagesRoot, fileName)))
            .SelectMany(document => document.Descendants())
            .Where(element => element.Name.LocalName is "ComboBox" or "CalendarDatePicker")
            .Select(ReadAutomationId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(
            new[]
            {
                "SettingsCompactSectionPicker",
                "MyIssuesScopeCompactPicker",
                "MyIssuesStateCompactPicker",
                "MyPullRequestsStateCompactPicker"
            },
            id => Assert.Contains(id, compactPickerIds));
    }

    [Fact]
    public void RuntimeContentDialogs_ApplyBothCatalogDialogPrimitivesPerInstance()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        int discovered = 0;
        Dictionary<string, int> discoveredByFile = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(productRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            string source = File.ReadAllText(path);
            MatchCollection declarations = ContentDialogDeclarationRegex().Matches(source);
            for (int index = 0; index < declarations.Count; index++)
            {
                Match declaration = declarations[index];
                string variable = declaration.Groups["variable"].Value;
                int end = index + 1 < declarations.Count ? declarations[index + 1].Index : source.Length;
                string instanceScope = source[declaration.Index..end];
                Assert.Contains($"AppDialogStyleCatalog.Apply({variable});", instanceScope, StringComparison.Ordinal);
                Assert.Contains($"AutomationProperties.SetAutomationId({variable}", instanceScope, StringComparison.Ordinal);
                discovered++;
                string relativePath = Path.GetRelativePath(productRoot, path);
                discoveredByFile[relativePath] = discoveredByFile.GetValueOrDefault(relativePath) + 1;
            }
        }

        Assert.Equal(ExpectedRuntimeDialogInstancesByFile.Values.Sum(), discovered);
        Assert.Equal(
            ExpectedRuntimeDialogInstancesByFile.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase),
            discoveredByFile.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase));
        string catalogSource = File.ReadAllText(Path.Combine(productRoot, "Views", "Dialogs", "AppDialogStyleCatalog.cs"));
        Assert.Contains("GetStyle(\"AppContentDialogStyle\")", catalogSource, StringComparison.Ordinal);
        Assert.Contains("GetStyle(\"AppDialogContentStyle\")", catalogSource, StringComparison.Ordinal);
    }

    private static readonly HashSet<string> SharedRowGeometry =
    [
        "MinHeight",
        "Margin",
        "Padding",
        "Background",
        "HorizontalContentAlignment",
        "VerticalContentAlignment",
        "CornerRadius",
        "UseSystemFocusVisuals"
    ];

    private static HashSet<string> GetAllowedRowOverrides(string fileName, string styleKey)
    {
        if (fileName == "StarsPage.xaml" && styleKey == "StarsRepositoryItemStyle")
        {
            return ["MinHeight", "CornerRadius"];
        }

        if (fileName == "ShellPage.xaml")
        {
            return ["MinHeight", "CornerRadius"];
        }

        return [];
    }

    private static bool IsListViewItemStyle(XElement element) =>
        element.Name.LocalName == "Style"
        && string.Equals((string?)element.Attribute("TargetType"), "ListViewItem", StringComparison.Ordinal);

    private static bool DocumentConsumesStyle(XDocument document, string styleKey) =>
        document.Descendants().Any(element =>
            string.Equals(ReadStaticResourceKey((string?)element.Attribute("Style")), styleKey, StringComparison.Ordinal));

    private static bool IsWorkspaceCommandInput(XElement element)
    {
        if (element.Name.LocalName is not ("TextBox" or "ComboBox" or "CalendarDatePicker"))
        {
            return false;
        }

        if (element.Name.LocalName is "ComboBox" or "CalendarDatePicker")
        {
            return true;
        }

        string? automationId = ReadAutomationId(element);
        if (string.IsNullOrWhiteSpace(automationId) || automationId == "GistsFilePreview")
        {
            return false;
        }

        return CommandInputIdRegex().IsMatch(automationId);
    }

    private static bool IsTemplateSetter(XElement element) =>
        element.Name.LocalName == "Setter"
        && string.Equals((string?)element.Attribute("Property"), "Template", StringComparison.Ordinal);

    private static bool IsWorkspaceCardInteractionTemplate(XElement templateSetter)
    {
        XElement? root = templateSetter.Descendants().FirstOrDefault(element => element.Name.LocalName == "Border");
        if (root is null
            || (string?)root.Attribute("Padding") != "12"
            || (string?)root.Attribute("Background") != "{ThemeResource AppCanvasInsetBrush}"
            || (string?)root.Attribute("BorderBrush") != "{ThemeResource AppHairlineBrush}"
            || (string?)root.Attribute("BorderThickness") != "1"
            || (string?)root.Attribute("CornerRadius") != "{ThemeResource AppRadiusSmall}")
        {
            return false;
        }

        HashSet<string> states = templateSetter.Descendants()
            .Where(element => element.Name.LocalName == "VisualState")
            .Select(ReadXamlName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .ToHashSet(StringComparer.Ordinal);
        return new[] { "Normal", "PointerOver", "Pressed", "Selected", "PointerOverSelected", "PressedSelected", "SelectedUnfocused", "Disabled" }
            .All(states.Contains);
    }

    private static XElement FindCatalogStyle(XDocument catalog, string styleKey)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return catalog.Root!.Elements().Single(element =>
            element.Name.LocalName == "Style"
            && string.Equals((string?)element.Attribute(x + "Key"), styleKey, StringComparison.Ordinal));
    }

    private static void AssertStateSetter(XElement state, string target, string value) =>
        Assert.Contains(
            state.Descendants().Where(element => element.Name.LocalName == "Setter"),
            setter => string.Equals((string?)setter.Attribute("Target"), target, StringComparison.Ordinal)
                && string.Equals((string?)setter.Attribute("Value"), value, StringComparison.Ordinal));

    private static string? ReadAutomationId(XElement element) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName.EndsWith("AutomationId", StringComparison.Ordinal))
            ?.Value;

    private static string? ReadXamlKey(XElement element) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "Key")
            ?.Value;

    private static string? ReadXamlName(XElement element) =>
        element.Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == "Name")
            ?.Value;

    private static string? ReadStaticResourceKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        Match match = StaticResourceRegex().Match(value);
        return match.Success ? match.Groups["key"].Value : null;
    }

    private static void AssertBasedOn(IReadOnlyDictionary<string, XElement> styles, string key, string expectedBase) =>
        Assert.Equal(expectedBase, ReadStaticResourceKey((string?)styles[key].Attribute("BasedOn")));

    private static string GetCatalogPath(string root) =>
        Path.Combine(root, "JitHub.WinUI", "Styles", "Primitives", "ControlCatalog.xaml");

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

    [GeneratedRegex("\\{StaticResource\\s+(?<key>[^}]+)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex StaticResourceRegex();

    [GeneratedRegex("ContentDialog\\s+(?<variable>[A-Za-z_][A-Za-z0-9_]*)\\s*=\\s*new\\s*\\(\\s*\\)", RegexOptions.CultureInvariant)]
    private static partial Regex ContentDialogDeclarationRegex();

    [GeneratedRegex("(Search|Filter|Sort|Scope|Direction|Branch|FilePicker|Compare(Base|Head|Diff))", RegexOptions.CultureInvariant)]
    private static partial Regex CommandInputIdRegex();
}
