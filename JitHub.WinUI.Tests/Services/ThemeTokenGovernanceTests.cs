using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Globalization;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ThemeTokenGovernanceTests
{
    private static readonly HashSet<string> VisualBrushProperties =
    [
        "Background",
        "Foreground",
        "BorderBrush",
        "Fill",
        "Stroke",
        "Color"
    ];

    private static readonly string[] AppOwnedImplicitControlTypes =
    [
        "AutoSuggestBox",
        "Button",
        "CalendarDatePicker",
        "CheckBox",
        "ComboBox",
        "ComboBoxItem",
        "DatePicker",
        "DropDownButton",
        "Expander",
        "FlyoutPresenter",
        "HyperlinkButton",
        "InfoBadge",
        "InfoBar",
        "ListView",
        "ListViewItem",
        "MenuFlyoutItem",
        "MenuFlyoutSeparator",
        "MenuFlyoutSubItem",
        "PersonPicture",
        "Pivot",
        "ProgressBar",
        "ProgressRing",
        "RadioButton",
        "RichEditBox",
        "RichTextBlock",
        "Segmented",
        "SegmentedItem",
        "SelectorBar",
        "SelectorBarItem",
        "TextBlock",
        "TextBox",
        "ToggleSwitch",
        "TreeView"
    ];

    [Fact]
    public void EveryThemeDefinesTheSameSemanticTokenSet()
    {
        XDocument colors = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Styles",
            "Foundation",
            "Tokens.Colors.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Dictionary<string, HashSet<string>> themes = colors.Descendants()
            .Where(element => element.Name.LocalName == "ResourceDictionary")
            .Where(element => element.Attribute(x + "Key") is not null)
            .ToDictionary(
                element => (string)element.Attribute(x + "Key")!,
                element => element.Elements()
                    .Select(resource => (string?)resource.Attribute(x + "Key"))
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key!)
                    .ToHashSet(StringComparer.Ordinal),
                StringComparer.Ordinal);

        HashSet<string> expected = themes["Light"];
        Assert.All(new[] { "Default", "Dark", "HighContrast" }, theme =>
            Assert.True(
                expected.SetEquals(themes[theme]),
                $"{theme} token set differs from Light. Missing: {string.Join(", ", expected.Except(themes[theme]))}. Extra: {string.Join(", ", themes[theme].Except(expected))}."));
    }

    [Fact]
    public void AppXamlUsesThemeResourcesInsteadOfLiteralVisualColors()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        string palettePath = Path.Combine(productRoot, "Styles", "Foundation", "Tokens.Colors.xaml");
        string paletteFolder = Path.Combine(productRoot, "Styles", "Foundation", "Palettes");
        List<string> violations = [];

        foreach (string path in Directory.EnumerateFiles(productRoot, "*.xaml", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                     .Where(path => !string.Equals(path, palettePath, StringComparison.OrdinalIgnoreCase))
                     .Where(path => !path.StartsWith(paletteFolder, StringComparison.OrdinalIgnoreCase)))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                foreach (XAttribute attribute in element.Attributes().Where(attribute => VisualBrushProperties.Contains(attribute.Name.LocalName)))
                {
                    AddViolation(path, element, attribute.Value, violations);
                }

                if (element.Name.LocalName == "Setter" &&
                    VisualBrushProperties.Contains((string?)element.Attribute("Property") ?? string.Empty))
                {
                    AddViolation(path, element, (string?)element.Attribute("Value") ?? string.Empty, violations);
                }

                if (element.Name.LocalName == "Color")
                {
                    AddViolation(path, element, element.Value, violations);
                }
            }
        }

        Assert.True(violations.Count == 0, $"Literal visual colors bypass theme tokens:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void AppXamlUsesTypographyAndCornerTokensInsteadOfLiteralMetrics()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        string typographyTokens = Path.Combine(productRoot, "Styles", "Foundation", "Tokens.Typography.xaml");
        string cornerTokens = Path.Combine(productRoot, "Styles", "Foundation", "Tokens.Corners.xaml");
        List<string> violations = [];

        foreach (string path in Directory.EnumerateFiles(productRoot, "*.xaml", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                     .Where(path => !string.Equals(path, typographyTokens, StringComparison.OrdinalIgnoreCase))
                     .Where(path => !string.Equals(path, cornerTokens, StringComparison.OrdinalIgnoreCase)))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement element in document.Descendants())
            {
                foreach (XAttribute attribute in element.Attributes().Where(attribute =>
                             attribute.Name.LocalName == "FontSize" ||
                             attribute.Name.LocalName.EndsWith("CornerRadius", StringComparison.Ordinal)))
                {
                    AddLiteralMetricViolation(path, element, attribute.Name.LocalName, attribute.Value, violations);
                }

                if (element.Name.LocalName == "Setter" &&
                    (string?)element.Attribute("Property") is { } property &&
                    (property == "FontSize" || property.EndsWith("CornerRadius", StringComparison.Ordinal)))
                {
                    AddLiteralMetricViolation(
                        path,
                        element,
                        property,
                        (string?)element.Attribute("Value") ?? string.Empty,
                        violations);
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Literal typography or corner metrics bypass theme tokens:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void RuntimeCreatedUiUsesTypographyAndCornerTokensInsteadOfLiterals()
    {
        string productRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI");
        Regex literalFontSize = new(
            @"\bFontSize\s*=\s*\d+(?:\.\d+)?\b",
            RegexOptions.CultureInvariant);
        Regex literalCornerRadius = new(
            @"\bCornerRadius\s*=\s*new\s+CornerRadius\(\s*\d+(?:\.\d+)?\s*\)",
            RegexOptions.CultureInvariant);
        List<string> violations = [];

        foreach (string path in Directory.EnumerateFiles(productRoot, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)))
        {
            string[] lines = File.ReadAllLines(path);
            for (int index = 0; index < lines.Length; index++)
            {
                if (literalFontSize.IsMatch(lines[index]) || literalCornerRadius.IsMatch(lines[index]))
                {
                    violations.Add($"{Path.GetRelativePath(FindRepositoryRoot(), path)}:{index + 1} -> {lines[index].Trim()}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Runtime-created UI bypasses typography or corner tokens:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void EveryViewInfoBarUsesAnAppOwnedSemanticStyle()
    {
        string root = FindRepositoryRoot();
        string viewsRoot = Path.Combine(root, "JitHub.WinUI", "Views");
        List<string> violations = [];

        foreach (string path in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement infoBar in document.Descendants().Where(element => element.Name.LocalName == "InfoBar"))
            {
                string? style = (string?)infoBar.Attribute("Style");
                if (style is "{StaticResource AppStatusInfoBarStyle}" or "{StaticResource AppErrorInfoBarStyle}")
                {
                    continue;
                }

                IXmlLineInfo lineInfo = infoBar;
                violations.Add($"{Path.GetRelativePath(root, path)}:{lineInfo.LineNumber} -> {style ?? "missing Style"}");
            }
        }

        Assert.True(
            violations.Count == 0,
            $"InfoBars bypass the app control catalog:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void SharedVisualControlsHaveAppOwnedImplicitStyles()
    {
        string stylesRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Styles");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> ownedTypes = Directory.EnumerateFiles(stylesRoot, "*.xaml", SearchOption.AllDirectories)
            .Select(path => XDocument.Load(path))
            .SelectMany(document => document.Descendants())
            .Where(element => element.Name.LocalName == "Style")
            .Where(element => element.Attribute(x + "Key") is null)
            .Select(element => ((string?)element.Attribute("TargetType") ?? string.Empty).Split(':').Last())
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(AppOwnedImplicitControlTypes, type => Assert.Contains(type, ownedTypes));
    }

    [Fact]
    public void ModernControlStylesDoNotDependOnUnpublishedFrameworkResources()
    {
        string repositoryRoot = FindRepositoryRoot();
        string navigation = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "JitHub.WinUI",
            "Styles",
            "Primitives",
            "NavigationPrimitives.xaml"));
        string lists = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "JitHub.WinUI",
            "Styles",
            "Primitives",
            "ListPrimitives.xaml"));

        Assert.DoesNotContain("DefaultSelectorBarStyle", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultSelectorBarItemStyle", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("DefaultTreeViewStyle", lists, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"muxc:SelectorBar\">", navigation, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"muxc:SelectorBarItem\">", navigation, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"TreeView\">", lists, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewStyleReferencesKeepStructureStaticAndThemeOnlyTheirTokens()
    {
        string viewsRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views");
        List<string> violations = [];

        foreach (string path in Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XAttribute style in document.Descendants().Attributes("Style"))
            {
                if (!style.Value.StartsWith("{ThemeResource ", StringComparison.Ordinal))
                {
                    continue;
                }

                IXmlLineInfo lineInfo = style.Parent!;
                violations.Add($"{Path.GetRelativePath(FindRepositoryRoot(), path)}:{lineInfo.LineNumber} -> {style.Value}");
            }
        }

        Assert.True(
            violations.Count == 0,
            $"View structure must use StaticResource styles and let token brushes react to theme changes:{Environment.NewLine}{string.Join(Environment.NewLine, violations)}");
    }

    [Fact]
    public void ReactionStylesConsumeOnlySemanticReactionAndPopupTokens()
    {
        string root = FindRepositoryRoot();
        string buttons = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Styles", "Buttons.xaml"));
        string navigation = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Styles", "Primitives", "NavigationPrimitives.xaml"));
        string spacing = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Styles", "Foundation", "Tokens.Spacing.xaml"));
        string typography = File.ReadAllText(Path.Combine(root, "JitHub.WinUI", "Styles", "Foundation", "Tokens.Typography.xaml"));

        Assert.Contains("AppReactionChipButtonStyle", buttons, StringComparison.Ordinal);
        Assert.Contains("AppReactionPickerButtonStyle", buttons, StringComparison.Ordinal);
        Assert.Contains("AppCommentActionButtonStyle", buttons, StringComparison.Ordinal);
        Assert.Contains("AppReactionChipBackgroundBrush", buttons, StringComparison.Ordinal);
        Assert.Contains("AppReactionChipHoverBrush", buttons, StringComparison.Ordinal);
        Assert.Contains("AppReactionChipPressedBrush", buttons, StringComparison.Ordinal);
        Assert.Contains("AppReactionChipBorderBrush", buttons, StringComparison.Ordinal);
        Assert.Contains("AppReactionChipMinWidth", buttons, StringComparison.Ordinal);
        Assert.Contains("AppReactionPickerButtonSize", buttons, StringComparison.Ordinal);
        Assert.Contains("AppReactionPickerFlyoutPresenterStyle", navigation, StringComparison.Ordinal);
        Assert.Contains("AppPopupSurfaceBrush", navigation, StringComparison.Ordinal);
        Assert.Contains("AppPopupBorderBrush", navigation, StringComparison.Ordinal);
        Assert.Contains("AppReactionPickerMinWidth", spacing, StringComparison.Ordinal);
        Assert.Contains("AppReactionChipPadding", spacing, StringComparison.Ordinal);
        Assert.Contains("AppEmojiFontFamily", typography, StringComparison.Ordinal);
        Assert.Contains("AppReactionPickerEmojiFontSize", typography, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSettingsCardConstrainsItsHeaderIconWithAnAppMetricToken()
    {
        string root = FindRepositoryRoot();
        XDocument card = XDocument.Load(Path.Combine(
            root,
            "JitHub.WinUI",
            "Views",
            "Controls",
            "App",
            "AppSettingsCard.xaml"));

        XElement iconPresenter = card.Descendants()
            .Single(element =>
                element.Name.LocalName == "ContentPresenter" &&
                string.Equals((string?)element.Attribute("Content"), "{Binding HeaderIcon, ElementName=Root}", StringComparison.Ordinal));

        Assert.Equal("{StaticResource AppSpace7}", (string?)iconPresenter.Attribute("Width"));
        Assert.Equal("{StaticResource AppSpace7}", (string?)iconPresenter.Attribute("Height"));
    }

    [Fact]
    public void SharedControlDictionariesAliasTheCanonicalSemanticBrushObjects()
    {
        string root = FindRepositoryRoot();
        string stylesRoot = Path.Combine(root, "JitHub.WinUI", "Styles");
        string canonicalBrushPath = Path.Combine(stylesRoot, "Foundation", "Tokens.Brushes.xaml");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        HashSet<string> canonicalBrushKeys = XDocument.Load(canonicalBrushPath).Root!
            .Elements()
            .Where(element => element.Name.LocalName.EndsWith("Brush", StringComparison.Ordinal))
            .Select(element => (string?)element.Attribute(x + "Key"))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key!)
            .ToHashSet(StringComparer.Ordinal);
        List<string> duplicateBrushes = [];
        List<string> missingAliases = [];

        foreach (string path in Directory.EnumerateFiles(stylesRoot, "*.xaml", SearchOption.AllDirectories)
                     .Where(path => !string.Equals(path, canonicalBrushPath, StringComparison.OrdinalIgnoreCase))
                     .Where(path => !path.Contains(
                         $"{Path.DirectorySeparatorChar}Palettes{Path.DirectorySeparatorChar}",
                         StringComparison.OrdinalIgnoreCase)))
        {
            XDocument document = XDocument.Load(path, LoadOptions.SetLineInfo);
            foreach (XElement brush in document.Descendants().Where(element =>
                         element.Name.LocalName == "SolidColorBrush" &&
                         ((string?)element.Attribute("Color"))?.StartsWith(
                             "{ThemeResource App",
                             StringComparison.Ordinal) == true))
            {
                IXmlLineInfo lineInfo = brush;
                duplicateBrushes.Add($"{Path.GetRelativePath(root, path)}:{lineInfo.LineNumber}");
            }

            foreach (XElement alias in document.Descendants().Where(element =>
                         element.Name.LocalName == "StaticResource" &&
                         ((string?)element.Attribute("ResourceKey"))?.StartsWith("App", StringComparison.Ordinal) == true &&
                         ((string?)element.Attribute("ResourceKey"))?.EndsWith("Brush", StringComparison.Ordinal) == true))
            {
                string resourceKey = (string)alias.Attribute("ResourceKey")!;
                if (!canonicalBrushKeys.Contains(resourceKey))
                {
                    IXmlLineInfo lineInfo = alias;
                    missingAliases.Add($"{Path.GetRelativePath(root, path)}:{lineInfo.LineNumber} -> {resourceKey}");
                }
            }
        }

        Assert.True(
            duplicateBrushes.Count == 0,
            $"Shared styles duplicate palette-bound brushes instead of retaining canonical brush identity:{Environment.NewLine}{string.Join(Environment.NewLine, duplicateBrushes)}");
        Assert.True(
            missingAliases.Count == 0,
            $"Shared styles reference brush aliases outside the canonical token dictionary:{Environment.NewLine}{string.Join(Environment.NewLine, missingAliases)}");
    }

    private static void AddViolation(string path, XElement element, string value, List<string> violations)
    {
        string candidate = value.Trim();
        if (!candidate.Equals("Transparent", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(candidate, "^#[0-9A-Fa-f]{3,8}$", RegexOptions.CultureInvariant))
        {
            return;
        }

        IXmlLineInfo lineInfo = element;
        violations.Add($"{Path.GetRelativePath(FindRepositoryRoot(), path)}:{lineInfo.LineNumber} -> {candidate}");
    }

    private static void AddLiteralMetricViolation(
        string path,
        XElement element,
        string property,
        string value,
        List<string> violations)
    {
        string candidate = value.Trim();
        bool isLiteral = candidate.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .All(part => double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
        if (!isLiteral)
        {
            return;
        }

        IXmlLineInfo lineInfo = element;
        violations.Add(
            $"{Path.GetRelativePath(FindRepositoryRoot(), path)}:{lineInfo.LineNumber} -> {property}=\"{candidate}\"");
    }

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
