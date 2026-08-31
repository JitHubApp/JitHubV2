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
    public void ReachableXaml_UsesFoundationFontWeightTokens()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        string tokenPath = Path.Combine(
            productRoot,
            "Styles",
            "Foundation",
            "Tokens.Typography.xaml");
        List<string> violations = [];

        foreach (string path in Directory.EnumerateFiles(productRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Equals(tokenPath, StringComparison.OrdinalIgnoreCase)))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                XAttribute? directWeight = element.Attribute("FontWeight");
                if (directWeight is not null && !IsResourceReference(directWeight.Value))
                {
                    IXmlLineInfo lineInfo = element;
                    violations.Add($"{Path.GetRelativePath(FindRepositoryRoot(), path)}:{lineInfo.LineNumber} FontWeight=\"{directWeight.Value}\"");
                }

                if (element.Name.LocalName == "Setter" &&
                    string.Equals((string?)element.Attribute("Property"), "FontWeight", StringComparison.Ordinal) &&
                    element.Attribute("Value") is { } setterValue &&
                    !IsResourceReference(setterValue.Value))
                {
                    IXmlLineInfo lineInfo = element;
                    violations.Add($"{Path.GetRelativePath(FindRepositoryRoot(), path)}:{lineInfo.LineNumber} FontWeight setter Value=\"{setterValue.Value}\"");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ReachableXaml_UsesFoundationOpacityTokens()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        string tokenPath = Path.Combine(
            productRoot,
            "Styles",
            "Foundation",
            "Tokens.Effects.xaml");
        HashSet<string> directProperties = new(StringComparer.Ordinal)
        {
            "Opacity",
            "TintOpacity",
            "TintLuminosityOpacity"
        };
        List<string> violations = [];

        foreach (string path in Directory.EnumerateFiles(productRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Equals(tokenPath, StringComparison.OrdinalIgnoreCase)))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                foreach (XAttribute attribute in element.Attributes()
                    .Where(attribute => directProperties.Contains(attribute.Name.LocalName)))
                {
                    AddLiteralViolation(path, element, attribute.Name.LocalName, attribute.Value, violations);
                }

                if (element.Name.LocalName == "Setter" &&
                    element.Attribute("Value") is { } setterValue &&
                    ((string?)element.Attribute("Property") == "Opacity" ||
                     ((string?)element.Attribute("Target"))?.EndsWith(".Opacity", StringComparison.Ordinal) == true))
                {
                    AddLiteralViolation(path, element, "Opacity setter", setterValue.Value, violations);
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ReachableXaml_UsesFoundationGapTokens()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        HashSet<string> gapProperties = new(StringComparer.Ordinal)
        {
            "ColumnSpacing",
            "RowSpacing",
            "Spacing"
        };
        List<string> violations = [];

        foreach (string path in Directory.EnumerateFiles(productRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                foreach (XAttribute attribute in element.Attributes()
                    .Where(attribute => gapProperties.Contains(attribute.Name.LocalName)))
                {
                    AddLiteralViolation(path, element, attribute.Name.LocalName, attribute.Value, violations);
                }

                if (element.Name.LocalName == "Setter" &&
                    element.Attribute("Value") is { } setterValue &&
                    element.Attribute("Property") is { } setterProperty &&
                    gapProperties.Contains(setterProperty.Value))
                {
                    AddLiteralViolation(path, element, setterProperty.Value, setterValue.Value, violations);
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ReachableXaml_UsesFoundationStrokeAndLineHeightTokens()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        string[] excludedFiles =
        [
            Path.Combine("Styles", "Foundation", "Tokens.Spacing.xaml"),
            Path.Combine("Styles", "Foundation", "Tokens.Typography.xaml")
        ];
        HashSet<string> properties = new(StringComparer.Ordinal)
        {
            "BorderThickness",
            "LineHeight"
        };
        List<string> violations = [];

        foreach (string path in Directory.EnumerateFiles(productRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !excludedFiles.Any(excluded => path.EndsWith(excluded, StringComparison.OrdinalIgnoreCase))))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                foreach (XAttribute attribute in element.Attributes()
                    .Where(attribute => properties.Contains(attribute.Name.LocalName)))
                {
                    AddLiteralViolation(path, element, attribute.Name.LocalName, attribute.Value, violations);
                }

                if (element.Name.LocalName == "Setter" &&
                    element.Attribute("Value") is { } setterValue &&
                    element.Attribute("Property") is { } setterProperty &&
                    properties.Contains(setterProperty.Value))
                {
                    AddLiteralViolation(path, element, setterProperty.Value, setterValue.Value, violations);
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ReachableXaml_UsesFoundationMarginAndPaddingTokens()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        string tokenPath = Path.Combine(
            productRoot,
            "Styles",
            "Foundation",
            "Tokens.Layout.xaml");
        HashSet<string> properties = new(StringComparer.Ordinal)
        {
            "FocusVisualMargin",
            "Margin",
            "Padding"
        };
        List<string> violations = [];

        foreach (string path in Directory.EnumerateFiles(productRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Equals(tokenPath, StringComparison.OrdinalIgnoreCase)))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                foreach (XAttribute attribute in element.Attributes()
                    .Where(attribute => properties.Contains(attribute.Name.LocalName)))
                {
                    AddLiteralViolation(path, element, attribute.Name.LocalName, attribute.Value, violations);
                }

                if (element.Name.LocalName == "Setter" && element.Attribute("Value") is { } setterValue)
                {
                    string? property = (string?)element.Attribute("Property");
                    string? targetProperty = ((string?)element.Attribute("Target"))?.Split('.').LastOrDefault();
                    if ((property is not null && properties.Contains(property)) ||
                        (targetProperty is not null && properties.Contains(targetProperty)))
                    {
                        AddLiteralViolation(path, element, property ?? targetProperty!, setterValue.Value, violations);
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ReachableXaml_UsesFoundationDimensionTokens()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        HashSet<string> properties = new(StringComparer.Ordinal)
        {
            "Height",
            "MaxHeight",
            "MaxWidth",
            "MinHeight",
            "MinWidth",
            "Width"
        };
        List<string> violations = [];

        foreach (string path in Directory.EnumerateFiles(productRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                foreach (XAttribute attribute in element.Attributes()
                    .Where(attribute => properties.Contains(attribute.Name.LocalName)))
                {
                    if (IsNumericLiteral(attribute.Value))
                    {
                        AddLiteralViolation(path, element, attribute.Name.LocalName, attribute.Value, violations);
                    }
                }

                if (element.Name.LocalName == "Setter" && element.Attribute("Value") is { } setterValue)
                {
                    string? property = (string?)element.Attribute("Property");
                    string? targetProperty = ((string?)element.Attribute("Target"))?.Split('.').LastOrDefault();
                    if ((property is not null && properties.Contains(property)) ||
                        (targetProperty is not null && properties.Contains(targetProperty)))
                    {
                        if (IsNumericLiteral(setterValue.Value))
                        {
                            AddLiteralViolation(path, element, property ?? targetProperty!, setterValue.Value, violations);
                        }
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void SharedControlDictionaries_UseFoundationDesignTokens()
    {
        string root = FindRepositoryRoot();
        string[] relativePaths =
        [
            Path.Combine("JitHub.WinUI", "Styles", "Primitives", "ControlCatalog.xaml"),
            Path.Combine("JitHub.WinUI", "Styles", "Foundation", "WinUIResourceBridge.xaml")
        ];
        HashSet<string> designProperties = new(StringComparer.Ordinal)
        {
            "BorderThickness",
            "ColumnSpacing",
            "CornerRadius",
            "FocusVisualMargin",
            "FontSize",
            "Height",
            "Margin",
            "MaxHeight",
            "MaxWidth",
            "MinHeight",
            "MinWidth",
            "Opacity",
            "Padding",
            "RowSpacing",
            "Spacing",
            "Width"
        };
        HashSet<string> primitiveResourceTypes = new(StringComparer.Ordinal)
        {
            "CornerRadius",
            "FontFamily",
            "GridLength",
            "Thickness",
            "Double"
        };
        List<string> violations = [];

        foreach (string relativePath in relativePaths)
        {
            string path = Path.Combine(root, relativePath);
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                foreach (XAttribute attribute in element.Attributes()
                    .Where(attribute => designProperties.Contains(attribute.Name.LocalName)))
                {
                    AddLiteralViolation(path, element, attribute.Name.LocalName, attribute.Value, violations);
                }

                if (element.Name.LocalName == "Setter" &&
                    element.Attribute("Value") is { } setterValue)
                {
                    string? property = (string?)element.Attribute("Property");
                    string? targetProperty = ((string?)element.Attribute("Target"))?.Split('.').LastOrDefault();
                    if ((property is not null && designProperties.Contains(property)) ||
                        (targetProperty is not null && designProperties.Contains(targetProperty)))
                    {
                        AddLiteralViolation(path, element, property ?? targetProperty!, setterValue.Value, violations);
                    }
                }

                if (primitiveResourceTypes.Contains(element.Name.LocalName) &&
                    !string.IsNullOrWhiteSpace(element.Value))
                {
                    IXmlLineInfo lineInfo = element;
                    violations.Add($"{Path.GetRelativePath(root, path)}:{lineInfo.LineNumber} primitive resource {element.Name.LocalName}=\"{element.Value}\"");
                }
            }
        }

        Assert.Empty(violations);
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
        string paletteFolder = Path.Combine(productRoot, "Styles", "Foundation", "Palettes");

        string[] violations = Directory.EnumerateFiles(productRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.StartsWith(paletteFolder, StringComparison.OrdinalIgnoreCase))
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
        string themeSettingsHelper = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Helpers",
            "ThemeSettingsHelper.cs"));

        Assert.Contains("AppWarmAccentBrush", commitViewer, StringComparison.Ordinal);
        Assert.Contains("AppWarmAccentForegroundBrush", commitViewer, StringComparison.Ordinal);
        Assert.Contains("AppAccentBrush", commitViewer, StringComparison.Ordinal);
        Assert.Contains("AppAccentForegroundBrush", commitViewer, StringComparison.Ordinal);
        Assert.Contains("AppAccentForegroundBrush", commitSearch, StringComparison.Ordinal);
        Assert.Contains("AppSuccessBrush", pullRequestDiff, StringComparison.Ordinal);
        Assert.Contains("AppDangerBrush", pullRequestDiff, StringComparison.Ordinal);
        Assert.Contains("IsHighContrastActive()", repoLabel, StringComparison.Ordinal);
        Assert.Contains("HighContrastVisualPolicy.GetRepositoryLabelPolicy", repoLabel, StringComparison.Ordinal);
        Assert.Contains("ThemeSettingsHelper.TryGetFor(this)", repoLabel, StringComparison.Ordinal);
        Assert.Contains("AppThemeSettingsMonitor? _themeSettings", repoLabel, StringComparison.Ordinal);
        Assert.Contains("_themeSettings.Changed +=", repoLabel, StringComparison.Ordinal);
        Assert.Contains("_themeSettings.Changed -=", repoLabel, StringComparison.Ordinal);
        Assert.Contains("ThemeSettingsHelper.IsHighContrastActive(_themeSettings)", repoLabel, StringComparison.Ordinal);
        Assert.Contains("HighContrastVisualPolicy.GetContributionCellBrushKey", contributionGraph, StringComparison.Ordinal);
        Assert.Contains("ThemeSettingsHelper.TryGetFor(this)", contributionGraph, StringComparison.Ordinal);
        Assert.Contains("AppThemeSettingsMonitor? _themeSettings", contributionGraph, StringComparison.Ordinal);
        Assert.Contains("_themeSettings.Changed +=", contributionGraph, StringComparison.Ordinal);
        Assert.Contains("_themeSettings.Changed -=", contributionGraph, StringComparison.Ordinal);
        Assert.Contains("ThemeSettingsHelper.IsHighContrastActive(_themeSettings)", contributionGraph, StringComparison.Ordinal);
        Assert.Contains("Dictionary<WindowId, AppThemeSettingsMonitor>", themeSettingsHelper, StringComparison.Ordinal);
        Assert.Contains("_settings.Changed += ThemeSettings_Changed", themeSettingsHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("_settings.Changed -=", themeSettingsHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("using Microsoft.UI.System;", repoLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("using Microsoft.UI.System;", contributionGraph, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessibilitySettings", repoLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessibilitySettings", contributionGraph, StringComparison.Ordinal);
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
            "AppCommandLeadingIconSearchTextBoxStyle",
            "AppCompactComboBoxStyle",
            "AppCompactTextComboBoxStyle",
            "AppLabeledComboBoxStyle",
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

    private static bool IsResourceReference(string value) =>
        value.TrimStart().StartsWith('{');

    private static bool IsNumericLiteral(string value) =>
        Regex.IsMatch(
            value.Trim(),
            @"^[+-]?(?:\d+(?:\.\d+)?|\.\d+)$",
            RegexOptions.CultureInvariant);

    private static void AddLiteralViolation(
        string path,
        XElement element,
        string property,
        string value,
        ICollection<string> violations)
    {
        if (IsResourceReference(value))
        {
            return;
        }

        IXmlLineInfo lineInfo = element;
        violations.Add($"{Path.GetRelativePath(FindRepositoryRoot(), path)}:{lineInfo.LineNumber} {property}=\"{value}\"");
    }

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
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the JitHub repository root.");
    }
}
