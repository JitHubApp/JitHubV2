using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ThemeResourceCoverageTests
{
    [Fact]
    public void FoundationColors_HighContrastCoversEveryLightThemeToken()
    {
        string path = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Styles", "Foundation", "Tokens.Colors.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement light = FindTheme(document, x, "Light");
        XElement highContrast = FindTheme(document, x, "HighContrast");
        HashSet<string> lightKeys = light.Elements()
            .Select(element => (string?)element.Attribute(x + "Key"))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key!)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> highContrastKeys = highContrast.Elements()
            .Select(element => (string?)element.Attribute(x + "Key"))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Subset(highContrastKeys, lightKeys);
        Assert.Subset(lightKeys, highContrastKeys);
        Assert.All(
            highContrast.Elements(),
            element => Assert.StartsWith(
                "SystemColor",
                (string?)element.Attribute("ResourceKey") ?? string.Empty,
                StringComparison.Ordinal));
    }

    [Fact]
    public void FoundationColors_LightThemeDefinesSemanticSurfaceRoles()
    {
        string path = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Styles", "Foundation", "Tokens.Colors.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement defaults = FindTheme(document, x, "Default");
        XElement light = FindTheme(document, x, "Light");

        Dictionary<string, string> defaultColors = ReadThemeColors(defaults, x);
        Dictionary<string, string> lightColors = ReadThemeColors(light, x);
        Assert.Equal(lightColors, defaultColors);
        string[] semanticSurfaceKeys =
        [
            "AppCanvasColor",
            "AppCanvasRaisedColor",
            "AppCanvasInsetColor",
            "AppRailColor",
            "AppSurfaceColor",
            "AppSurfaceSubtleColor",
            "AppCardColor",
            "AppInputColor",
            "AppInputHoverColor",
            "AppRowColor",
            "AppRowHoverColor",
            "AppRowPressedColor",
            "AppRowSelectedColor",
            "AppSelectionColor"
        ];

        Assert.All(semanticSurfaceKeys, key => Assert.Contains(key, lightColors.Keys));
    }

    [Fact]
    public void Typography_SeparatesUiAndBodyTextFromMonospacedMetadata()
    {
        string path = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Styles", "Foundation", "Tokens.Typography.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        Dictionary<string, string> fonts = document.Root!
            .Elements()
            .Where(static element => element.Name.LocalName == "FontFamily")
            .ToDictionary(
                element => (string)element.Attribute(x + "Key")!,
                element => element.Value,
                StringComparer.Ordinal);

        Assert.Equal("Segoe UI Variable Text", fonts["AppUiFontFamily"]);
        Assert.Equal("Segoe UI Variable Text", fonts["AppBodyFontFamily"]);
        Assert.Contains("JetBrains Mono", fonts["AppMonoFontFamily"], StringComparison.Ordinal);
        Assert.NotEqual(fonts["AppUiFontFamily"], fonts["AppMonoFontFamily"]);
    }

    [Fact]
    public void ReachableXaml_DoesNotHardcodeForegroundOrSurfaceColors()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        string[] excludedFiles =
        [
            Path.Combine("Styles", "Foundation", "Tokens.Colors.xaml"),
            Path.Combine("Styles", "Color.xaml"),
            Path.Combine("Styles", "WinUICommonColor.xaml")
        ];

        string[] violations = Directory.EnumerateFiles(productRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !excludedFiles.Any(excluded => path.EndsWith(excluded, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(path => FindHardcodedColorAttributes(path))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void ReachableCustomColorControls_DoNotConstructUnguardedFixedColors()
    {
        string root = FindRepositoryRoot();
        string[] relativePaths =
        [
            Path.Combine("JitHub.WinUI", "Views", "Controls", "Commit", "CommitDiffViewer.xaml.cs"),
            Path.Combine("JitHub.WinUI", "Views", "Controls", "Commit", "CommitDiffSearchHighlight.cs"),
            Path.Combine("JitHub.WinUI", "Views", "Controls", "PullRequest", "DiffTextBlock.xaml.cs"),
            Path.Combine("JitHub.WinUI", "Views", "Controls", "Common", "RepoLabel.xaml.cs"),
            Path.Combine("JitHub.WinUI", "Views", "Controls", "Profile", "ProfileContributionGraph.xaml.cs")
        ];

        Regex namedFixedColor = new(@"(?<![A-Za-z0-9_])(?:Microsoft\.UI\.)?Colors\.(?!Transparent\b)[A-Za-z]+", RegexOptions.CultureInvariant);
        Regex numericArgb = new(@"(?:Windows\.UI\.)?Color\.FromArgb\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*,\s*\d+\s*\)", RegexOptions.CultureInvariant);
        List<string> violations = [];

        foreach (string relativePath in relativePaths)
        {
            string source = File.ReadAllText(Path.Combine(root, relativePath));
            violations.AddRange(namedFixedColor.Matches(source)
                .Select(match => $"{relativePath}: fixed named color {match.Value}"));
            violations.AddRange(numericArgb.Matches(source)
                .Select(match => $"{relativePath}: fixed ARGB color {match.Value}"));
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void HighContrastSensitiveControls_UseSemanticResourcesAndExplicitSystemBranches()
    {
        string root = FindRepositoryRoot();
        string commitViewer = ReadProductSource(root, "Commit", "CommitDiffViewer.xaml.cs");
        string commitSearch = ReadProductSource(root, "Commit", "CommitDiffSearchHighlight.cs");
        string pullRequestDiff = ReadProductSource(root, "PullRequest", "DiffTextBlock.xaml.cs");
        string repoLabel = ReadProductSource(root, "Common", "RepoLabel.xaml.cs");
        string contributionGraph = ReadProductSource(root, "Profile", "ProfileContributionGraph.xaml.cs");

        Assert.Contains("AppWarmAccentBrush", commitViewer, StringComparison.Ordinal);
        Assert.Contains("AppWarmAccentForegroundBrush", commitViewer, StringComparison.Ordinal);
        Assert.Contains("AppAccentBrush", commitViewer, StringComparison.Ordinal);
        Assert.Contains("AppAccentForegroundBrush", commitViewer, StringComparison.Ordinal);
        Assert.Contains("AppAccentForegroundBrush", commitSearch, StringComparison.Ordinal);
        Assert.Contains("AppSuccessBrush", pullRequestDiff, StringComparison.Ordinal);
        Assert.Contains("AppDangerBrush", pullRequestDiff, StringComparison.Ordinal);
        Assert.Contains("IsHighContrastActive()", repoLabel, StringComparison.Ordinal);
        Assert.Contains("HighContrastVisualPolicy.GetRepositoryLabelPolicy", repoLabel, StringComparison.Ordinal);
        Assert.Contains("HighContrastChanged", repoLabel, StringComparison.Ordinal);
        Assert.Contains("HighContrastVisualPolicy.GetContributionCellBrushKey", contributionGraph, StringComparison.Ordinal);
        Assert.Contains("HighContrastChanged", contributionGraph, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlCatalog_DefinesCanonicalInteractivePrimitives()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Styles",
            "Primitives",
            "ControlCatalog.xaml");
        XDocument document = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> keys = document.Root!
            .Elements()
            .Select(element => (string?)element.Attribute(x + "Key"))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key!)
            .ToHashSet(StringComparer.Ordinal);

        string[] required =
        [
            "AppCommandTextBoxStyle",
            "AppCommandSearchTextBoxStyle",
            "AppCompactComboBoxStyle",
            "AppCompactCalendarDatePickerStyle",
            "AppCommandButtonStyle",
            "AppSelectionCheckBoxStyle",
            "AppDenseListRowStyle",
            "AppDenseFlatListRowStyle",
            "AppWorkspaceCardListRowStyle",
            "AppCompactNavigationRowStyle",
            "AppRepositoryListRowStyle",
            "AppSearchResultRowStyle",
            "AppWorkspaceHeaderStyle",
            "AppEmptyStatePanelStyle",
            "AppInlineEmptyStatePanelStyle",
            "AppStatusInfoBarStyle",
            "AppErrorInfoBarStyle",
            "AppDialogContentStyle",
            "AppContentDialogStyle"
        ];

        Assert.All(required, key => Assert.Contains(key, keys));
    }

    private static IEnumerable<string> FindHardcodedColorAttributes(string path)
    {
        XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
        string[] colorAttributes = ["Foreground", "Background", "BorderBrush", "Fill"];
        foreach (XElement element in document.Descendants())
        {
            foreach (XAttribute attribute in element.Attributes().Where(attribute => colorAttributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal)))
            {
                string value = attribute.Value.Trim();
                if (value.Equals("Black", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("White", StringComparison.OrdinalIgnoreCase)
                    || System.Text.RegularExpressions.Regex.IsMatch(value, "^#[0-9a-fA-F]{3,8}$"))
                {
                    IXmlLineInfo lineInfo = element;
                    yield return $"{Path.GetRelativePath(FindRepositoryRoot(), path)}:{lineInfo.LineNumber} {attribute.Name.LocalName}=\"{value}\"";
                }
            }
        }
    }

    private static Dictionary<string, string> ReadThemeColors(XElement theme, XNamespace x) =>
        theme.Elements()
            .Where(static element => element.Name.LocalName == "Color")
            .ToDictionary(
                element => (string)element.Attribute(x + "Key")!,
                element => element.Value,
                StringComparer.Ordinal);

    private static string ReadProductSource(string root, string folder, string fileName) =>
        File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            folder,
            fileName));

    private static XElement FindTheme(XDocument document, XNamespace x, string key) =>
        document.Descendants()
            .Single(element =>
                element.Name.LocalName == "ResourceDictionary" &&
                string.Equals((string?)element.Attribute(x + "Key"), key, StringComparison.Ordinal));

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
}
