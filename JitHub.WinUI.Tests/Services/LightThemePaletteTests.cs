using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class LightThemePaletteTests
{
    private static readonly string[] StructuralNeutralKeys =
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
        "AppOverlayColor",
        "AppOutlineColor",
        "AppOutlineStrongColor",
        "AppHairlineColor"
    ];

    private static readonly string[] SemanticSurfaceKeys =
    [
        "AppRailColor",
        "AppCardColor",
        "AppInputColor",
        "AppInputHoverColor",
        "AppRowColor",
        "AppRowHoverColor",
        "AppRowPressedColor",
        "AppRowSelectedColor",
        "AppRowHoverForegroundColor",
        "AppRowPressedForegroundColor",
        "AppRowSelectedForegroundColor",
        "AppSelectionColor",
        "AppSelectionForegroundColor"
    ];

    private static readonly string[] ContentSurfaceKeys =
    [
        "AppCanvasColor",
        "AppRailColor",
        "AppSurfaceColor",
        "AppSurfaceSubtleColor",
        "AppCardColor",
        "AppInputColor",
        "AppInputHoverColor",
        "AppRowHoverColor",
        "AppRowPressedColor",
        "AppRowSelectedColor"
    ];

    [Fact]
    public void LightStructuralPalette_IsNeutralAndCannotRegressToCreamOrTan()
    {
        Dictionary<string, Rgba> light = ReadTheme("Light");

        foreach (string key in StructuralNeutralKeys)
        {
            Rgba color = light[key];
            Assert.Equal(255, color.A);
            Assert.True(
                color.Chroma <= 12,
                $"{key} must remain neutral; channel spread was {color.Chroma} for {color}.");
            Assert.True(
                color.R <= color.B + 2,
                $"{key} has a warm red-over-blue bias associated with cream/tan palettes: {color}.");
        }

        double averageWarmBias = StructuralNeutralKeys
            .Select(key => (double)light[key].R - light[key].B)
            .Average();
        Assert.True(averageWarmBias <= 0, $"Structural palette has aggregate warm bias {averageWarmBias:F2}.");
    }

    [Fact]
    public void LightStructuralPalette_HasAStableMeasuredSurfaceHierarchy()
    {
        Dictionary<string, Rgba> light = ReadTheme("Light");
        double card = RelativeLuminance(light["AppCardColor"]);
        double surface = RelativeLuminance(light["AppSurfaceColor"]);
        double canvas = RelativeLuminance(light["AppCanvasColor"]);
        double rail = RelativeLuminance(light["AppRailColor"]);
        double inset = RelativeLuminance(light["AppCanvasInsetColor"]);
        double inputHover = RelativeLuminance(light["AppInputHoverColor"]);

        Assert.True(card > surface && surface > canvas && canvas > rail && rail > inset);
        Assert.True(card - canvas >= 0.08, "Cards must read clearly above the app canvas.");
        Assert.True(surface - canvas >= 0.035, "General surfaces must remain distinct from the canvas.");
        Assert.True(canvas - rail >= 0.04, "The app rail must remain visibly separate from content.");
        Assert.True(rail - inset >= 0.025, "Inset surfaces must remain the lowest structural layer.");
        Assert.True(card - inputHover >= 0.025, "Hovering an input must create a visible but restrained state.");
    }

    [Fact]
    public void LightTextAndSemanticColors_MeetMeasuredContrastTargets()
    {
        Dictionary<string, Rgba> light = ReadTheme("Light");

        Assert.All(ContentSurfaceKeys, surface => AssertContrast(light, "AppInkColor", surface, 12.0));
        Assert.All(ContentSurfaceKeys, surface => AssertContrast(light, "AppInkMutedColor", surface, 4.5));
        AssertContrast(light, "AppInkSubtleColor", "AppCardColor", 4.5);
        AssertContrast(light, "AppInkSubtleColor", "AppInputColor", 4.5);
        AssertContrast(light, "AppAccentColor", "AppCardColor", 4.5);
        AssertContrast(light, "AppAccentForegroundColor", "AppAccentColor", 4.5);
        AssertContrast(light, "AppAccentForegroundColor", "AppAccentHoverColor", 4.5);
        AssertContrast(light, "AppAccentForegroundColor", "AppAccentPressedColor", 4.5);
        AssertContrast(light, "AppSelectionForegroundColor", "AppSelectionColor", 4.5);
        AssertContrast(light, "AppWarmAccentColor", "AppCardColor", 4.5);
        AssertContrast(light, "AppWarmAccentForegroundColor", "AppWarmAccentColor", 4.5);
        AssertContrast(light, "AppDangerColor", "AppCardColor", 4.5);
        AssertContrast(light, "AppDangerForegroundColor", "AppDangerColor", 4.5);
        AssertContrast(light, "AppSuccessColor", "AppCardColor", 4.5);
        AssertContrast(light, "AppSuccessForegroundColor", "AppSuccessColor", 4.5);
        AssertContrast(light, "AppOutlineStrongColor", "AppCardColor", 3.0);
        AssertContrast(light, "AppOutlineColor", "AppCardColor", 1.4);
        AssertContrast(light, "AppHairlineColor", "AppCardColor", 1.2);
    }

    [Fact]
    public void LightInteractionStates_AreSemanticallyDistinctWithoutBecomingAccentFloods()
    {
        Dictionary<string, Rgba> light = ReadTheme("Light");
        Rgba hover = light["AppRowHoverColor"];
        Rgba pressed = light["AppRowPressedColor"];
        Rgba selected = light["AppRowSelectedColor"];
        Rgba selection = light["AppSelectionColor"];
        Rgba accent = light["AppAccentColor"];

        Assert.Equal(0, light["AppRowColor"].A);
        Assert.True(ColorDistance(hover, pressed) >= 15);
        Assert.True(ColorDistance(hover, selected) >= 18);
        Assert.True(ColorDistance(pressed, selected) >= 6);
        Assert.True(ColorDistance(selected, selection) >= 180);
        Assert.True(ColorDistance(selected, accent) >= 180, "Selected rows should be tinted, not filled with brand green.");
        Assert.True(ColorDistance(light["AppDangerColor"], light["AppSuccessColor"]) >= 100);
        Assert.True(ColorDistance(light["AppWarmAccentColor"], light["AppDangerColor"]) >= 70);
    }

    [Fact]
    public void SemanticSurfaceTokens_AreDefinedForEveryThemeAndHighContrastUsesDistinctSystemRoles()
    {
        Dictionary<string, Rgba> defaults = ReadTheme("Default");
        Dictionary<string, Rgba> light = ReadTheme("Light");
        Assert.Equal(light, defaults);

        XDocument document = LoadColorsDocument();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement dark = FindTheme(document, x, "Dark");
        XElement highContrast = FindTheme(document, x, "HighContrast");

        HashSet<string> darkKeys = ReadKeys(dark, x);
        string[] requiredKeys =
        [
            .. SemanticSurfaceKeys,
            "AppDangerForegroundColor",
            "AppSuccessForegroundColor"
        ];
        Dictionary<string, string> highContrastResources = highContrast.Elements()
            .Where(element => element.Attribute(x + "Key") is not null)
            .ToDictionary(
                element => (string)element.Attribute(x + "Key")!,
                element => (string?)element.Attribute("ResourceKey") ?? string.Empty,
                StringComparer.Ordinal);

        Assert.All(requiredKeys, key => Assert.Contains(key, darkKeys));
        Assert.All(requiredKeys, key => Assert.StartsWith("SystemColor", highContrastResources[key], StringComparison.Ordinal));

        Dictionary<string, string> expectedInteractionRoles = new(StringComparer.Ordinal)
        {
            ["AppCanvasColor"] = "SystemColorWindowColor",
            ["AppInputColor"] = "SystemColorButtonFaceColor",
            ["AppInputHoverColor"] = "SystemColorHighlightColor",
            ["AppRowHoverColor"] = "SystemColorButtonFaceColor",
            ["AppRowPressedColor"] = "SystemColorButtonTextColor",
            ["AppRowSelectedColor"] = "SystemColorHighlightColor",
            ["AppRowHoverForegroundColor"] = "SystemColorButtonTextColor",
            ["AppRowPressedForegroundColor"] = "SystemColorButtonFaceColor",
            ["AppRowSelectedForegroundColor"] = "SystemColorHighlightTextColor"
        };

        Assert.All(expectedInteractionRoles, pair => Assert.Equal(pair.Value, highContrastResources[pair.Key]));
        Assert.Equal(
            3,
            new[]
            {
                highContrastResources["AppRowHoverColor"],
                highContrastResources["AppRowPressedColor"],
                highContrastResources["AppRowSelectedColor"]
            }.Distinct(StringComparer.Ordinal).Count());

        string[] stateSignatures =
        [
            $"{highContrastResources["AppRowHoverColor"]}/{highContrastResources["AppRowHoverForegroundColor"]}",
            $"{highContrastResources["AppRowPressedColor"]}/{highContrastResources["AppRowPressedForegroundColor"]}",
            $"{highContrastResources["AppRowSelectedColor"]}/{highContrastResources["AppRowSelectedForegroundColor"]}/{highContrastResources["AppAccentColor"]}"
        ];
        Assert.Equal(stateSignatures.Length, stateSignatures.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DarkPalette_HasMeasuredSurfaceHierarchyAndDistinctInteractionStates()
    {
        Dictionary<string, Rgba> dark = ReadTheme("Dark");
        double card = RelativeLuminance(dark["AppCardColor"]);
        double surface = RelativeLuminance(dark["AppSurfaceColor"]);
        double canvas = RelativeLuminance(dark["AppCanvasColor"]);
        double rail = RelativeLuminance(dark["AppRailColor"]);
        double inset = RelativeLuminance(dark["AppCanvasInsetColor"]);

        Assert.True(card > surface && surface > canvas && canvas > rail && rail > inset);
        Assert.True(ColorDistance(dark["AppCanvasColor"], dark["AppRailColor"]) >= 6);
        Assert.True(ColorDistance(dark["AppSurfaceColor"], dark["AppCardColor"]) >= 8);
        Assert.True(ColorDistance(dark["AppCardColor"], dark["AppInputColor"]) >= 10);
        Assert.True(ColorDistance(dark["AppInputColor"], dark["AppInputHoverColor"]) >= 15);
        Assert.True(ColorDistance(dark["AppRowHoverColor"], dark["AppRowPressedColor"]) >= 13);
        Assert.True(ColorDistance(dark["AppRowHoverColor"], dark["AppRowSelectedColor"]) >= 17);
        Assert.True(ColorDistance(dark["AppRowPressedColor"], dark["AppRowSelectedColor"]) >= 17);

        AssertContrast(dark, "AppInkColor", "AppCardColor", 12.0);
        AssertContrast(dark, "AppInkMutedColor", "AppCardColor", 7.0);
        AssertContrast(dark, "AppRowHoverForegroundColor", "AppRowHoverColor", 12.0);
        AssertContrast(dark, "AppRowPressedForegroundColor", "AppRowPressedColor", 10.0);
        AssertContrast(dark, "AppRowSelectedForegroundColor", "AppRowSelectedColor", 10.0);
        AssertContrast(dark, "AppAccentForegroundColor", "AppAccentColor", 4.5);
    }

    [Fact]
    public void ReachableSharedChrome_ConsumesSemanticSurfaceRoles()
    {
        string root = FindRepositoryRoot();
        string brushes = Read(root, "JitHub.WinUI", "Styles", "Foundation", "Tokens.Brushes.xaml");
        string interactions = Read(root, "JitHub.WinUI", "Styles", "Foundation", "WinUIResourceBridge.xaml");
        string catalog = Read(root, "JitHub.WinUI", "Styles", "Primitives", "ControlCatalog.xaml");
        string shell = Read(root, "JitHub.WinUI", "Views", "Pages", "ShellPage.xaml");

        Assert.All(
            new[] { "AppRailBrush", "AppCardBrush", "AppInputBrush", "AppInputHoverBrush", "AppRowHoverBrush", "AppRowPressedBrush", "AppRowSelectedBrush", "AppSelectionBrush", "AppSelectionForegroundBrush" },
            key => Assert.Contains($"x:Key=\"{key}\"", brushes, StringComparison.Ordinal));
        Assert.Contains("AppInputColor", interactions, StringComparison.Ordinal);
        Assert.Contains("AppRowHoverColor", interactions, StringComparison.Ordinal);
        Assert.Contains("AppRowPressedColor", interactions, StringComparison.Ordinal);
        Assert.Contains("AppRowSelectedColor", interactions, StringComparison.Ordinal);
        Assert.Contains("AppRowSelectedBrush", catalog, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource AppRailBrush}\"", shell, StringComparison.Ordinal);
        Assert.Contains("Background=\"{ThemeResource AppInputBrush}\"", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalControls_MapEveryStateAndSeverityToSemanticTokens()
    {
        string root = FindRepositoryRoot();
        string buttons = Read(root, "JitHub.WinUI", "Styles", "Buttons.xaml");
        string interactions = Read(root, "JitHub.WinUI", "Styles", "Foundation", "WinUIResourceBridge.xaml");
        string catalog = Read(root, "JitHub.WinUI", "Styles", "Primitives", "ControlCatalog.xaml");

        Assert.Contains("ButtonBackground\" Color=\"{ThemeResource AppInputColor}", interactions, StringComparison.Ordinal);
        Assert.Contains("ButtonBackgroundPointerOver\" Color=\"{ThemeResource AppInputHoverColor}", interactions, StringComparison.Ordinal);
        Assert.Contains("ButtonBackgroundPressed\" Color=\"{ThemeResource AppRowPressedColor}", interactions, StringComparison.Ordinal);
        Assert.Contains("AppDangerForegroundBrush", buttons, StringComparison.Ordinal);

        Assert.Contains("TextControlBackground\" Color=\"{ThemeResource AppInputColor}", interactions, StringComparison.Ordinal);
        Assert.Contains("TextControlBackgroundPointerOver\" Color=\"{ThemeResource AppInputHoverColor}", interactions, StringComparison.Ordinal);
        Assert.Contains("ComboBoxBackgroundPressed\" Color=\"{ThemeResource AppRowPressedColor}", interactions, StringComparison.Ordinal);
        Assert.Contains("ListViewItemBackgroundSelected\" Color=\"{ThemeResource AppRowSelectedColor}", interactions, StringComparison.Ordinal);
        Assert.Contains("MenuFlyoutPresenterBackground\" Color=\"{ThemeResource AppCardColor}", interactions, StringComparison.Ordinal);
        Assert.Contains("PivotHeaderItemSelectedPipeFill\" Color=\"{ThemeResource AppAccentColor}", interactions, StringComparison.Ordinal);
        Assert.Contains("ListViewItemForegroundPointerOver\" Color=\"{ThemeResource AppRowHoverForegroundColor}", interactions, StringComparison.Ordinal);
        Assert.Contains("ListViewItemForegroundPressed\" Color=\"{ThemeResource AppRowPressedForegroundColor}", interactions, StringComparison.Ordinal);
        Assert.Contains("ListViewItemForegroundSelected\" Color=\"{ThemeResource AppRowSelectedForegroundColor}", interactions, StringComparison.Ordinal);

        Assert.Contains("CheckBoxBackgroundChecked\" Color=\"{ThemeResource AppAccentColor}", catalog, StringComparison.Ordinal);
        Assert.Contains("CheckBoxCheckGlyphForegroundChecked\" Color=\"{ThemeResource AppAccentForegroundColor}", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveApplicationResourceGraph_HasOneOwnerPerKeyAndOnePlatformBridge()
    {
        string root = FindRepositoryRoot();
        string appPath = Path.Combine(root, "JitHub.WinUI", "App.xaml");
        XDocument app = XDocument.Load(appPath);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement applicationDictionary = app.Root!
            .Element(presentation + "Application.Resources")!
            .Element(presentation + "ResourceDictionary")!;
        string[] sources = applicationDictionary
            .Element(presentation + "ResourceDictionary.MergedDictionaries")!
            .Elements(presentation + "ResourceDictionary")
            .Select(element => (string?)element.Attribute("Source"))
            .Where(source => source is not null && source.StartsWith("ms-appx:///", StringComparison.Ordinal))
            .Select(source => source!["ms-appx:///".Length..])
            .ToArray();

        Assert.Equal(1, sources.Count(source => source.EndsWith("WinUIResourceBridge.xaml", StringComparison.Ordinal)));
        Assert.DoesNotContain(sources, source => source.EndsWith("InteractionPrimitives.xaml", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, source => source.EndsWith("SurfacePrimitives.xaml", StringComparison.Ordinal));

        List<(string Key, string Owner)> resources = [];
        resources.AddRange(ReadTopLevelKeys(applicationDictionary, x).Select(key => (key, "App.xaml")));
        foreach (string source in sources)
        {
            string path = Path.Combine(root, "JitHub.WinUI", source.Replace('/', Path.DirectorySeparatorChar));
            XElement dictionary = XDocument.Load(path).Root!;
            resources.AddRange(ReadTopLevelKeys(dictionary, x).Select(key => (key, source)));
        }

        string[] duplicates = resources
            .GroupBy(resource => resource.Key, StringComparer.Ordinal)
            .Where(group => group.Select(resource => resource.Owner).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(resource => resource.Owner).Distinct(StringComparer.Ordinal))}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.True(duplicates.Length == 0, $"Duplicate effective resource keys:{Environment.NewLine}{string.Join(Environment.NewLine, duplicates)}");
    }

    [Fact]
    public void ReachablePages_DoNotForkTheLightPaletteWithLocalThemeDictionaries()
    {
        string viewsRoot = Path.Combine(FindRepositoryRoot(), "JitHub.WinUI", "Views");
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] localLightDictionaries = Directory.EnumerateFiles(viewsRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(path => XDocument.Load(path).Descendants().Any(element =>
                element.Name.LocalName == "ResourceDictionary" &&
                string.Equals((string?)element.Attribute(x + "Key"), "Light", StringComparison.Ordinal)))
            .Select(path => Path.GetRelativePath(FindRepositoryRoot(), path))
            .ToArray();

        Assert.Empty(localLightDictionaries);
    }

    private static void AssertContrast(
        IReadOnlyDictionary<string, Rgba> colors,
        string foreground,
        string background,
        double minimum)
    {
        double ratio = ContrastRatio(colors[foreground], colors[background]);
        Assert.True(ratio >= minimum, $"{foreground} on {background} contrast was {ratio:F2}:1; expected at least {minimum:F1}:1.");
    }

    private static double ContrastRatio(Rgba first, Rgba second)
    {
        double firstLuminance = RelativeLuminance(first);
        double secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) /
               (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double RelativeLuminance(Rgba color) =>
        0.2126 * LinearChannel(color.R) +
        0.7152 * LinearChannel(color.G) +
        0.0722 * LinearChannel(color.B);

    private static double LinearChannel(byte value)
    {
        double channel = value / 255d;
        return channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }

    private static double ColorDistance(Rgba first, Rgba second) => Math.Sqrt(
        Math.Pow(first.R - second.R, 2) +
        Math.Pow(first.G - second.G, 2) +
        Math.Pow(first.B - second.B, 2));

    private static Dictionary<string, Rgba> ReadTheme(string themeKey)
    {
        XDocument document = LoadColorsDocument();
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return FindTheme(document, x, themeKey).Elements()
            .Where(element => element.Name.LocalName == "Color")
            .ToDictionary(
                element => (string)element.Attribute(x + "Key")!,
                element => ParseColor(element.Value),
                StringComparer.Ordinal);
    }

    private static HashSet<string> ReadKeys(XElement theme, XNamespace x) => theme.Elements()
        .Select(element => (string?)element.Attribute(x + "Key"))
        .Where(key => !string.IsNullOrWhiteSpace(key))
        .Select(key => key!)
        .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> ReadTopLevelKeys(XElement dictionary, XNamespace x) => dictionary.Elements()
        .Where(element => !element.Name.LocalName.EndsWith(".MergedDictionaries", StringComparison.Ordinal))
        .Where(element => !element.Name.LocalName.EndsWith(".ThemeDictionaries", StringComparison.Ordinal))
        .Select(element => (string?)element.Attribute(x + "Key"))
        .Where(key => !string.IsNullOrWhiteSpace(key))
        .Select(key => key!);

    private static Rgba ParseColor(string value)
    {
        string hex = value.Trim().TrimStart('#');
        if (hex.Length == 6)
        {
            return new Rgba(
                255,
                Convert.ToByte(hex[0..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16));
        }

        if (hex.Length == 8)
        {
            return new Rgba(
                Convert.ToByte(hex[0..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16));
        }

        throw new FormatException($"Unsupported color value '{value}'.");
    }

    private static XDocument LoadColorsDocument() => XDocument.Load(Path.Combine(
        FindRepositoryRoot(),
        "JitHub.WinUI",
        "Styles",
        "Foundation",
        "Tokens.Colors.xaml"));

    private static XElement FindTheme(XDocument document, XNamespace x, string key) => document.Descendants()
        .Single(element =>
            element.Name.LocalName == "ResourceDictionary" &&
            string.Equals((string?)element.Attribute(x + "Key"), key, StringComparison.Ordinal));

    private static string Read(string root, params string[] parts) =>
        File.ReadAllText(Path.Combine([root, .. parts]));

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

    private readonly record struct Rgba(byte A, byte R, byte G, byte B)
    {
        public int Chroma => Math.Max(R, Math.Max(G, B)) - Math.Min(R, Math.Min(G, B));
    }
}
