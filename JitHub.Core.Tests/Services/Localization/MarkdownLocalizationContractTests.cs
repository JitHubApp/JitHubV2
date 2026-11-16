using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using JitHub.Core.Services.Localization;
using JitHub.Markdown.Localization;
using MarkdownRenderer.Hosting;
using Xunit;

namespace JitHub.Core.Tests.Services.Localization;

/// <summary>
/// Checks the renderer's stable string keys against JitHub's string provider and
/// its .resw catalogs. The keys come from the renderer's public
/// <see cref="MarkdownStringKeys"/> constants and the catalogs are read as data.
/// </summary>
public sealed class MarkdownLocalizationContractTests
{
    // The provider over the app's MRT lookup, as MarkdownRendererConfigurator installs it.
    private static readonly JitHubMarkdownStringProvider Provider = new(new AppResourceText());

    [Fact]
    public void JitHubProvider_ResolvesEveryStableRendererStringKeyFromItsCatalogEntry()
    {
        IReadOnlyDictionary<string, string> english = LoadCatalogValues(FindRepositoryRoot(), "en-US");
        List<string> requested = [];
        using IDisposable lookup = LocalizedResourceText.OverrideResourceLookupFactoryForTests(
            () => (resourceKey, _) =>
            {
                requested.Add(resourceKey);
                return english.TryGetValue(resourceKey, out string? value) ? value : null;
            });

        foreach (string key in StableStringKeys())
        {
            string? value = Provider.GetString(key, CultureInfo.GetCultureInfo("en-US"));

            Assert.False(string.IsNullOrWhiteSpace(value), $"The provider has no string for {key}.");
            Assert.Equal(english[ToResourceKey(key)], value);
        }

        Assert.Equal(StableStringKeys().Select(ToResourceKey).Order(StringComparer.Ordinal), requested.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void JitHubProvider_FallsBackToCanonicalTextWithoutAResourceMap()
    {
        using IDisposable lookup = LocalizedResourceText.OverrideResourceLookupFactoryForTests(static () => static (_, _) => null);

        Assert.All(StableStringKeys(), key =>
            Assert.False(string.IsNullOrWhiteSpace(
                Provider.GetString(key, CultureInfo.InvariantCulture))));
        Assert.Null(Provider.GetString("Not.A.Renderer.Key", CultureInfo.InvariantCulture));
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

    /// <summary>Forwards to the app's resource lookup, like the app's LocalizedMarkdownResourceText.</summary>
    private sealed class AppResourceText : IMarkdownResourceText
    {
        public string GetString(string resourceKey, string fallback, CultureInfo? culture) =>
            LocalizedResourceText.GetString(resourceKey, fallback, culture);
    }
}
