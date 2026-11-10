using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MarkdownRenderer.Hosting;
using Xunit;

namespace JitHub.WinUI.Tests.Strings;

/// <summary>
/// Checks the renderer's stable string keys against JitHub's string provider and
/// its .resw catalogs. The keys come from the renderer's public
/// <see cref="MarkdownStringKeys"/> constants and the catalogs are read as data.
/// </summary>
/// <remarks><c>partial</c> only for the generated placeholder regex.</remarks>
public sealed partial class MarkdownLocalizationContractTests
{
    [Fact]
    public void RendererStrings_ExistInCanonicalAndPseudoCatalogs()
    {
        string root = FindRepositoryRoot();
        string[] resourceKeys = StableStringKeys().Select(ToResourceKey).ToArray();

        HashSet<string> english = LoadCatalog(root, "en-US");
        HashSet<string> pseudo = LoadCatalog(root, "qps-ploc");
        Assert.All(resourceKeys, key => Assert.Contains(key, english));
        Assert.All(resourceKeys, key => Assert.Contains(key, pseudo));
    }

    [Fact]
    public void EveryRealCatalog_HasExactRendererKeySetAndPlaceholderParity()
    {
        string root = FindRepositoryRoot();
        string[] expectedKeys = StableStringKeys()
            .Select(ToResourceKey)
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

    private static string[] StableStringKeys()
    {
        string[] keys = typeof(MarkdownStringKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(static field => (string)field.GetRawConstantValue()!)
            .ToArray();
        Assert.NotEmpty(keys);
        return keys;
    }

    private static string ToResourceKey(string key) => "MarkdownRenderer/" + key.Replace('.', '/');

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
        "(?<!\\{)\\{\\d+(?:,\\s*-?\\d+)?(?::[^{}]+)?\\}(?!\\})",
        RegexOptions.CultureInvariant)]
    private static partial Regex CompositePlaceholderPattern();
}
