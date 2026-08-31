using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JitHub.Models;
using JitHub.Services;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed class ThemePaletteCatalogTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void Catalog_HasStableUniqueFamiliesAndKeepsJitHubAsDefault()
    {
        Assert.Equal(20, ThemePaletteCatalog.All.Count);
        Assert.Equal(20, ThemePaletteCatalog.All.Select(static palette => palette.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ThemePaletteIds.JitHub, ThemePaletteCatalog.Default.Id);
        Assert.Equal(ThemePaletteIds.JitHub, ThemePaletteCatalog.Normalize(null));
        Assert.Equal(ThemePaletteIds.JitHub, ThemePaletteCatalog.Normalize("unknown"));
        Assert.Equal(ThemePaletteIds.Windows11, ThemePaletteCatalog.Normalize("win11"));
        Assert.Equal(ThemePaletteIds.VisualStudioCode, ThemePaletteCatalog.Normalize("VS Code"));
    }

    [Fact]
    public void EveryPalette_DefinesTheCompleteSemanticContractForBothAppearances()
    {
        string root = FindRepositoryRoot();
        XDocument defaultDocument = XDocument.Load(ToFilePath(root, ThemePaletteCatalog.Default.ResourceUri!));
        HashSet<string> expected = ReadKeys(FindTheme(defaultDocument, "Light"));

        foreach (ThemePaletteDefinition palette in ThemePaletteCatalog.All)
        {
            if (palette.ResourceUri is null)
            {
                Assert.True(palette.IsGenerated);
                Assert.NotNull(palette.GeneratedLight);
                Assert.NotNull(palette.GeneratedDark);
                Assert.True(expected.SetEquals(palette.GeneratedLight.Colors.Keys), $"{palette.Id} Light changes the semantic token contract.");
                Assert.True(expected.SetEquals(palette.GeneratedDark.Colors.Keys), $"{palette.Id} Dark changes the semantic token contract.");
                continue;
            }

            XDocument document = XDocument.Load(ToFilePath(root, palette.ResourceUri));
            Dictionary<string, XElement> themes = document.Descendants()
                .Where(static element => element.Name.LocalName == "ResourceDictionary")
                .Where(element => element.Attribute(Xaml + "Key") is not null)
                .ToDictionary(
                    element => (string)element.Attribute(Xaml + "Key")!,
                    StringComparer.Ordinal);

            Assert.Equal(["Dark", "Default", "HighContrast", "Light"], themes.Keys.Order(StringComparer.Ordinal));
            HashSet<string> lightKeys = ReadKeys(themes["Light"]);
            HashSet<string> darkKeys = ReadKeys(themes["Dark"]);
            HashSet<string> defaultKeys = ReadKeys(themes["Default"]);
            Assert.True(lightKeys.SetEquals(darkKeys), $"{palette.Id} Dark differs from Light.");
            Assert.True(lightKeys.SetEquals(defaultKeys), $"{palette.Id} Default differs from Light.");
            Assert.Equal(ReadColors(themes["Light"]), ReadColors(themes["Default"]));

            Assert.True(expected.SetEquals(lightKeys), $"{palette.Id} changes the semantic token contract.");

            XElement highContrast = themes["HighContrast"];
            HashSet<string> highContrastKeys = ReadKeys(highContrast);
            if (highContrastKeys.Count == 0)
            {
                string source = highContrast.Descendants()
                    .Single(element => element.Name.LocalName == "ResourceDictionary" && element.Attribute("Source") is not null)
                    .Attribute("Source")!.Value;
                highContrastKeys = ReadKeys(XDocument.Load(ToFilePath(root, source)).Root!);
            }

            Assert.True(lightKeys.SetEquals(highContrastKeys), $"{palette.Id} High Contrast differs from Light.");
        }
    }

    [Fact]
    public void PalettePreviews_AreSourcedFromTheirSemanticTokens()
    {
        string root = FindRepositoryRoot();
        foreach (ThemePaletteDefinition palette in ThemePaletteCatalog.All)
        {
            AssertPreviewMatches(ReadPaletteColors(root, palette, "Light"), palette.Light);
            AssertPreviewMatches(ReadPaletteColors(root, palette, "Dark"), palette.Dark);
        }
    }

    [Fact]
    public void EveryPalette_MeetsTextAndActionContrastContracts()
    {
        string root = FindRepositoryRoot();
        string[] surfaces =
        [
            "AppCanvasColor",
            "AppCanvasRaisedColor",
            "AppSurfaceColor",
            "AppCardColor",
            "AppInputColor",
            "AppPopupSurfaceColor"
        ];
        (string Foreground, string Background)[] actionPairs =
        [
            ("AppAccentForegroundColor", "AppAccentColor"),
            ("AppWarmAccentForegroundColor", "AppWarmAccentColor"),
            ("AppDangerForegroundColor", "AppDangerColor"),
            ("AppSuccessForegroundColor", "AppSuccessColor"),
            ("AppSelectionForegroundColor", "AppSelectionColor"),
            ("AppRowSelectedForegroundColor", "AppRowSelectedColor")
        ];

        foreach (ThemePaletteDefinition palette in ThemePaletteCatalog.All)
        {
            foreach (string appearance in new[] { "Light", "Dark" })
            {
                Dictionary<string, string> colors = ReadPaletteColors(root, palette, appearance);
                foreach (string surface in surfaces)
                {
                    AssertContrast(palette.Id, appearance, "AppInkColor", surface, colors, 4.5);
                    AssertContrast(palette.Id, appearance, "AppInkMutedColor", surface, colors, 4.5);
                }

                foreach ((string foreground, string background) in actionPairs)
                {
                    AssertContrast(palette.Id, appearance, foreground, background, colors, 4.5);
                }
            }
        }
    }

    [Fact]
    public void LiveBrushRefresh_CoversEveryCanonicalPaletteBoundBrush()
    {
        string root = FindRepositoryRoot();
        XDocument brushes = XDocument.Load(Path.Combine(
            root,
            "JitHub.WinUI",
            "Styles",
            "Foundation",
            "Tokens.Brushes.xaml"));
        HashSet<string> solidBrushKeys = brushes.Root!.Elements()
            .Where(element => element.Name.LocalName == "SolidColorBrush")
            .Select(element => (string)element.Attribute(Xaml + "Key")!)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> acrylicBrushKeys = brushes.Root!.Elements()
            .Where(element => element.Name.LocalName == "AcrylicBrush")
            .Select(element => (string)element.Attribute(Xaml + "Key")!)
            .ToHashSet(StringComparer.Ordinal);
        string runtimeSource = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "ThemePaletteRuntime.cs"));
        string solidMappingSource = ReadInitializer(runtimeSource, "SemanticBrushTokenMappings");
        string acrylicMappingSource = ReadInitializer(runtimeSource, "AcrylicBrushTokenMappings");
        HashSet<string> mappedSolidBrushKeys = ReadMappedTokens(solidMappingSource, "Brush");
        HashSet<string> mappedAcrylicBrushKeys = ReadMappedTokens(acrylicMappingSource, "Brush");
        HashSet<string> mappedColorTokenKeys = ReadMappedTokens(
            solidMappingSource + acrylicMappingSource,
            "Color");
        HashSet<string> colorTokenKeys = ReadKeys(FindTheme(
            XDocument.Load(Path.Combine(root, "JitHub.WinUI", "Styles", "Foundation", "Tokens.Colors.xaml")),
            "Light"));

        Assert.True(
            solidBrushKeys.SetEquals(mappedSolidBrushKeys),
            $"Live solid-brush coverage differs. Missing: {string.Join(", ", solidBrushKeys.Except(mappedSolidBrushKeys))}. Extra: {string.Join(", ", mappedSolidBrushKeys.Except(solidBrushKeys))}.");
        Assert.True(
            acrylicBrushKeys.SetEquals(mappedAcrylicBrushKeys),
            $"Live acrylic-brush coverage differs. Missing: {string.Join(", ", acrylicBrushKeys.Except(mappedAcrylicBrushKeys))}. Extra: {string.Join(", ", mappedAcrylicBrushKeys.Except(acrylicBrushKeys))}.");
        Assert.All(mappedColorTokenKeys, tokenKey => Assert.Contains(tokenKey, colorTokenKeys));
    }

    [Fact]
    public void ThemeService_PersistsNormalizedPaletteAndMigratesMissingValuesToJitHub()
    {
        MemorySettingService settings = new();
        ThemeService service = new(settings);

        Assert.Equal(ThemePaletteIds.JitHub, service.GetPalette());

        service.SetPalette("VS Code");

        Assert.Equal(ThemePaletteIds.VisualStudioCode, service.GetPalette());
        Assert.Equal(ThemePaletteIds.VisualStudioCode, settings.Get<string>(ThemeService.PaletteKey));
    }

    private static void AssertPreviewMatches(
        IReadOnlyDictionary<string, string> colors,
        ThemePalettePreview preview)
    {
        Assert.Equal(colors["AppCanvasColor"], preview.Canvas);
        Assert.Equal(colors["AppRailColor"], preview.Rail);
        Assert.Equal(colors["AppCardColor"], preview.Surface);
        Assert.Equal(colors["AppAccentColor"], preview.Accent);
        Assert.Equal(colors["AppInkColor"], preview.Ink);
    }

    private static Dictionary<string, string> ReadPaletteColors(
        string root,
        ThemePaletteDefinition palette,
        string appearance)
    {
        if (palette.ResourceUri is not null)
        {
            XDocument document = XDocument.Load(ToFilePath(root, palette.ResourceUri));
            return ReadColors(FindTheme(document, appearance));
        }

        ThemePaletteTokenSet tokenSet = appearance == "Dark"
            ? Assert.IsType<ThemePaletteTokenSet>(palette.GeneratedDark)
            : Assert.IsType<ThemePaletteTokenSet>(palette.GeneratedLight);
        return new Dictionary<string, string>(tokenSet.Colors, StringComparer.Ordinal);
    }

    private static void AssertContrast(
        string palette,
        string appearance,
        string foreground,
        string background,
        IReadOnlyDictionary<string, string> colors,
        double minimum)
    {
        double ratio = Contrast(colors[foreground], colors[background]);
        Assert.True(
            ratio >= minimum,
            $"{palette} {appearance} {foreground} on {background} is {ratio:F2}:1; expected at least {minimum:F1}:1.");
    }

    private static double Contrast(string foreground, string background)
    {
        double foregroundLuminance = Luminance(foreground);
        double backgroundLuminance = Luminance(background);
        return (Math.Max(foregroundLuminance, backgroundLuminance) + 0.05) /
               (Math.Min(foregroundLuminance, backgroundLuminance) + 0.05);
    }

    private static double Luminance(string hex)
    {
        string value = hex.TrimStart('#');
        if (value.Length == 8)
        {
            value = value[2..];
        }

        double red = Linear(byte.Parse(value.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d);
        double green = Linear(byte.Parse(value.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d);
        double blue = Linear(byte.Parse(value.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255d);
        return (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
    }

    private static double Linear(double channel) =>
        channel <= 0.04045
            ? channel / 12.92
            : Math.Pow((channel + 0.055) / 1.055, 2.4);

    private static XElement FindTheme(XDocument document, string key) =>
        document.Descendants().Single(element =>
            element.Name.LocalName == "ResourceDictionary" &&
            string.Equals((string?)element.Attribute(Xaml + "Key"), key, StringComparison.Ordinal));

    private static HashSet<string> ReadKeys(XElement dictionary) =>
        dictionary.Elements()
            .Select(element => (string?)element.Attribute(Xaml + "Key"))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key!)
            .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> ReadColors(XElement dictionary) =>
        dictionary.Elements()
            .Where(static element => element.Name.LocalName == "Color")
            .ToDictionary(
                element => (string)element.Attribute(Xaml + "Key")!,
                element => element.Value,
                StringComparer.Ordinal);

    private static string ToFilePath(string root, string resourceUri)
    {
        const string prefix = "ms-appx:///";
        Assert.StartsWith(prefix, resourceUri, StringComparison.Ordinal);
        return Path.Combine(
            root,
            "JitHub.WinUI",
            resourceUri[prefix.Length..].Replace('/', Path.DirectorySeparatorChar));
    }

    private static string ReadInitializer(string source, string fieldName)
    {
        int fieldStart = source.IndexOf(fieldName, StringComparison.Ordinal);
        Assert.True(fieldStart >= 0, $"Could not find {fieldName}.");
        int initializerStart = source.IndexOf('[', fieldStart);
        int initializerEnd = source.IndexOf("];", initializerStart, StringComparison.Ordinal);
        Assert.True(initializerStart >= 0 && initializerEnd > initializerStart, $"Could not read {fieldName}.");
        return source[initializerStart..initializerEnd];
    }

    private static HashSet<string> ReadMappedTokens(string source, string suffix) =>
        Regex.Matches(source, "\"(?<token>App[^\"]+)\"", RegexOptions.CultureInvariant)
            .Select(match => match.Groups["token"].Value)
            .Where(token => token.EndsWith(suffix, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

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
