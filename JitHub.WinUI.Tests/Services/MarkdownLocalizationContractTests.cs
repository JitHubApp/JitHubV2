using System.Text.RegularExpressions;
using System.Xml.Linq;
using JitHub.Services.Markdown;
using Xunit;

namespace JitHub.WinUI.Tests.Services;

public sealed partial class MarkdownLocalizationContractTests
{
    [Fact]
    public void JitHubProvider_CoversEveryStableRendererStringKey()
    {
        string root = FindRepositoryRoot();
        string contracts = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Hosting",
            "MarkdownHostServices.cs"));
        string provider = File.ReadAllText(Path.Combine(
            root,
            "JitHub.WinUI",
            "Services",
            "Markdown",
            "JitHubMarkdownStringProvider.cs"));

        Match[] keys = StableStringKeyPattern().Matches(contracts).Cast<Match>().ToArray();
        Assert.NotEmpty(keys);
        Assert.All(
            keys,
            match => Assert.Contains(
                $"MarkdownStringKeys.{match.Groups[1].Value} =>",
                provider,
                StringComparison.Ordinal));
    }

    [Fact]
    public void RendererStrings_ExistInCanonicalAndPseudoCatalogs()
    {
        string root = FindRepositoryRoot();
        string contracts = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Hosting",
            "MarkdownHostServices.cs"));
        string[] resourceKeys = StableStringKeyPattern()
            .Matches(contracts)
            .Cast<Match>()
            .Select(match => "MarkdownRenderer/" + match.Groups[2].Value.Replace('.', '/'))
            .ToArray();

        HashSet<string> english = LoadCatalog(root, "en-US");
        HashSet<string> pseudo = LoadCatalog(root, "qps-ploc");
        Assert.All(resourceKeys, key => Assert.Contains(key, english));
        Assert.All(resourceKeys, key => Assert.Contains(key, pseudo));
    }

    [Fact]
    public void EveryRealCatalog_HasExactRendererKeySetAndPlaceholderParity()
    {
        string root = FindRepositoryRoot();
        string contracts = File.ReadAllText(Path.Combine(
            root,
            "MarkdownRenderer",
            "MarkdownRenderer",
            "Hosting",
            "MarkdownHostServices.cs"));
        string[] expectedKeys = StableStringKeyPattern()
            .Matches(contracts)
            .Cast<Match>()
            .Select(match => "MarkdownRenderer/" + match.Groups[2].Value.Replace('.', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();

        IReadOnlyDictionary<string, string> english = LoadCatalogValues(root, "en-US");
        Assert.Equal(
            expectedKeys,
            english.Keys
                .Where(static key => key.StartsWith("MarkdownRenderer/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal));

        string stringsRoot = Path.Combine(root, "JitHub.WinUI", "Strings");
        string[] realLocales = Directory
            .EnumerateDirectories(stringsRoot)
            .Select(Path.GetFileName)
            .Where(static locale =>
                !string.IsNullOrWhiteSpace(locale) &&
                !string.Equals(locale, "qps-ploc", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Contains("en-US", realLocales, StringComparer.OrdinalIgnoreCase);
        Assert.NotEmpty(realLocales);

        Assert.All(realLocales, locale =>
        {
            IReadOnlyDictionary<string, string> catalog = LoadCatalogValues(root, locale);
            string[] actualKeys = catalog.Keys
                .Where(static key => key.StartsWith("MarkdownRenderer/", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expectedKeys, actualKeys);
            Assert.All(expectedKeys, key =>
            {
                Assert.False(
                    string.IsNullOrWhiteSpace(catalog[key]),
                    $"{locale} has an empty value for {key}.");
                Assert.Equal(
                    ExtractPlaceholders(english[key]),
                    ExtractPlaceholders(catalog[key]));
            });
        });
    }

    [Fact]
    public void MarkdownViewer_InstallsHostStringProvider()
    {
        string source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "JitHub.WinUI",
            "Views",
            "Controls",
            "Common",
            "MarkdownViewer.xaml.cs"));

        Assert.Contains(
            "_renderer.StringProvider = JitHubMarkdownStringProvider.Instance;",
            source,
            StringComparison.Ordinal);
    }

    private static HashSet<string> LoadCatalog(string root, string language) =>
        LoadCatalogValues(root, language).Keys.ToHashSet(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> LoadCatalogValues(string root, string language) =>
        XDocument.Load(Path.Combine(
                root,
                "JitHub.WinUI",
                "Strings",
                language,
                "Resources.resw"))
            .Root!
            .Elements("data")
            .ToDictionary(
                element => (string)element.Attribute("name")!,
                element => (string?)element.Element("value") ?? string.Empty,
                StringComparer.Ordinal);

    private static string[] ExtractPlaceholders(string value) =>
        CompositePlaceholderPattern()
            .Matches(value)
            .Cast<Match>()
            .Select(static match => match.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "JitHub.WinUI")) &&
                Directory.Exists(Path.Combine(current.FullName, "MarkdownRenderer")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    [GeneratedRegex(
        "public const string (\\w+) = \\\"([^\\\"]+)\\\";",
        RegexOptions.CultureInvariant)]
    private static partial Regex StableStringKeyPattern();

    [GeneratedRegex(
        "(?<!\\{)\\{\\d+(?:,\\s*-?\\d+)?(?::[^{}]+)?\\}(?!\\})",
        RegexOptions.CultureInvariant)]
    private static partial Regex CompositePlaceholderPattern();
}
