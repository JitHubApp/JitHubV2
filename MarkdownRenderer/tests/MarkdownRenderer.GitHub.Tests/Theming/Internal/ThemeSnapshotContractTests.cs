using Microsoft.UI.Xaml;
using MarkdownRenderer.GitHub.Tests.TestSupport;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fakes;
using MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal.ResourceDictionaries;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.TestSupport.Fixtures.ThemeSnapshotFixture;

namespace MarkdownRenderer.GitHub.Tests.Theming.Internal;

/// <summary>
/// Checks theme resource lookup precedence: local values, merged and
/// selected theme dictionaries, the default theme, High Contrast
/// fallback, cyclic merges, and explicit lookups that ignore ambient
/// fallbacks.
/// </summary>
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class ThemeSnapshotContractTests
{
    [Fact]
    public void ResourceLookup_LocalValuePrecedesMergedAndSelectedThemeDictionaries()
    {
        const string resourceKey = "MarkdownRenderer.Custom.Foreground";
        var resources = new TestResourceDictionary();
        resources.Values[resourceKey] = "local";
        var ordinaryMerged = new TestResourceDictionary();
        ordinaryMerged.Values[resourceKey] = "ordinary-merged";
        var selectedTheme = new TestResourceDictionary();
        var selectedThemeMerged = new TestResourceDictionary();
        selectedThemeMerged.Values[resourceKey] = "theme-merged";
        selectedTheme.Merged.Add(selectedThemeMerged);
        resources.Themes["Dark"] = selectedTheme;
        resources.Merged.Add(ordinaryMerged);

        bool found = TryResolveFromDictionaryForTesting(
            resources,
            "Dark",
            resourceKey,
            static (dictionary, key) => dictionary.Themes.GetValueOrDefault(key),
            static (TestResourceDictionary dictionary, string key, out object value) =>
                dictionary.Values.TryGetValue(key, out value!),
            static dictionary => dictionary.Merged.Count,
            static (dictionary, index) => dictionary.Merged[index],
            out object value);

        Assert.True(found);
        Assert.Equal("local", value);
    }

    [Fact]
    public void ResourceLookup_LocalValuePrecedesDefaultThemeDictionary()
    {
        const string resourceKey = "MarkdownRenderer.Extension.ForegroundBrush";
        var resources = new TestResourceDictionary();
        resources.Values[resourceKey] = "generic";
        resources.Themes["Dark"] = new TestResourceDictionary();
        var defaultTheme = new TestResourceDictionary();
        defaultTheme.Values[resourceKey] = "default-theme";
        resources.Themes["Default"] = defaultTheme;

        bool found = TryResolveFromDictionaryForTesting(
            resources,
            "Dark",
            resourceKey,
            static (dictionary, key) => dictionary.Themes.GetValueOrDefault(key),
            static (TestResourceDictionary dictionary, string key, out object value) =>
                dictionary.Values.TryGetValue(key, out value!),
            static dictionary => dictionary.Merged.Count,
            static (dictionary, index) => dictionary.Merged[index],
            out object value);

        Assert.True(found);
        Assert.Equal("generic", value);
    }

    [Fact]
    public void ResourceLookup_DoesNotUseDefaultAsPerKeyFallbackForExistingThemeDictionary()
    {
        const string resourceKey = "MarkdownRenderer.Extension.ForegroundBrush";
        var resources = new TestResourceDictionary();
        resources.Themes["Dark"] = new TestResourceDictionary();
        var defaultTheme = new TestResourceDictionary();
        defaultTheme.Values[resourceKey] = "default-theme";
        resources.Themes["Default"] = defaultTheme;

        bool found = TryResolveFromDictionaryForTesting(
            resources,
            "Dark",
            resourceKey,
            static (dictionary, key) => dictionary.Themes.GetValueOrDefault(key),
            static (TestResourceDictionary dictionary, string key, out object value) =>
                dictionary.Values.TryGetValue(key, out value!),
            static dictionary => dictionary.Merged.Count,
            static (dictionary, index) => dictionary.Merged[index],
            out _);

        Assert.False(found);
    }

    [Fact]
    public void ResourceLookup_UsesDefaultWhenRequestedThemeDictionaryIsAbsent()
    {
        const string resourceKey = "MarkdownRenderer.Extension.ForegroundBrush";
        var resources = new TestResourceDictionary();
        var defaultTheme = new TestResourceDictionary();
        defaultTheme.Values[resourceKey] = "default-theme";
        resources.Themes["Default"] = defaultTheme;

        bool found = TryResolveFromDictionaryForTesting(
            resources,
            "Dark",
            resourceKey,
            static (dictionary, key) => dictionary.Themes.GetValueOrDefault(key),
            static (TestResourceDictionary dictionary, string key, out object value) =>
                dictionary.Values.TryGetValue(key, out value!),
            static dictionary => dictionary.Merged.Count,
            static (dictionary, index) => dictionary.Merged[index],
            out object value);

        Assert.True(found);
        Assert.Equal("default-theme", value);
    }

    [Theory]
    [InlineData(false, ElementTheme.Light, "Light", "Default")]
    [InlineData(false, ElementTheme.Dark, "Dark", "Default")]
    [InlineData(true, ElementTheme.Light, "HighContrast", "Light", "Default")]
    [InlineData(true, ElementTheme.Dark, "HighContrast", "Dark", "Default")]
    public void ThemeDictionarySelection_UsesDocumentedOrderedFallbacks(
        bool isHighContrast,
        ElementTheme actualTheme,
        params string[] expected)
    {
        Assert.Equal(
            expected,
            ThemeResourceScope.GetThemeDictionaryKeys(isHighContrast, actualTheme));
    }

    [Fact]
    public void ResourceLookup_HighContrastFallsBackToActualThemeOnlyWhenDictionaryIsAbsent()
    {
        const string resourceKey = "MarkdownRenderer.Extension.FontSize";
        var resources = new TestResourceDictionary();
        var darkTheme = new TestResourceDictionary();
        darkTheme.Values[resourceKey] = "dark";
        resources.Themes["Dark"] = darkTheme;

        Assert.True(TryResolve(out object fallback));
        Assert.Equal("dark", fallback);

        resources.Themes["HighContrast"] = new TestResourceDictionary();
        Assert.False(TryResolve(out _));

        bool TryResolve(out object value) => TryResolveFromDictionaryForTesting(
            resources,
            ["HighContrast", "Dark", "Default"],
            resourceKey,
            static (dictionary, key) => dictionary.Themes.GetValueOrDefault(key),
            static (TestResourceDictionary dictionary, string key, out object result) =>
                dictionary.Values.TryGetValue(key, out result!),
            static dictionary => dictionary.Merged.Count,
            static (dictionary, index) => dictionary.Merged[index],
            out value);
    }

    [Fact]
    public void ResourceLookup_SelectedThemeStillPrecedesDefaultTheme()
    {
        const string resourceKey = "MarkdownRenderer.Extension.ForegroundBrush";
        var resources = new TestResourceDictionary();
        var selectedTheme = new TestResourceDictionary();
        selectedTheme.Values[resourceKey] = "selected-theme";
        resources.Themes["Dark"] = selectedTheme;
        var defaultTheme = new TestResourceDictionary();
        defaultTheme.Values[resourceKey] = "default-theme";
        resources.Themes["Default"] = defaultTheme;

        bool found = TryResolveFromDictionaryForTesting(
            resources,
            "Dark",
            resourceKey,
            static (dictionary, key) => dictionary.Themes.GetValueOrDefault(key),
            static (TestResourceDictionary dictionary, string key, out object value) =>
                dictionary.Values.TryGetValue(key, out value!),
            static dictionary => dictionary.Merged.Count,
            static (dictionary, index) => dictionary.Merged[index],
            out object value);

        Assert.True(found);
        Assert.Equal("selected-theme", value);
    }

    [Fact]
    public void ResourceLookup_CyclicMergedDictionariesRemainBoundedAndPreserveSiblingLookup()
    {
        const string resourceKey = "MarkdownRenderer.Custom.Foreground";
        var resources = new TestResourceDictionary();
        var resolvedSibling = new TestResourceDictionary();
        resolvedSibling.Values[resourceKey] = "resolved";
        var cyclicBranch = new TestResourceDictionary();
        resources.Merged.Add(resolvedSibling);
        resources.Merged.Add(cyclicBranch);
        cyclicBranch.Merged.Add(resources);

        Assert.True(TryResolve(resources, resourceKey, out object value));
        Assert.Equal("resolved", value);
        Assert.False(TryResolve(resources, "MarkdownRenderer.Missing.Foreground", out _));

        static bool TryResolve(
            TestResourceDictionary root,
            string key,
            out object value) => TryResolveFromDictionaryForTesting(
            root,
            "Light",
            key,
            static (dictionary, theme) => dictionary.Themes.GetValueOrDefault(theme),
            static (TestResourceDictionary dictionary, string candidate, out object result) =>
                dictionary.Values.TryGetValue(candidate, out result!),
            static dictionary => dictionary.Merged.Count,
            static (dictionary, index) => dictionary.Merged[index],
                out value);
    }

    [Fact]
    public void ExplicitResourceLookup_IgnoresAmbientFallbackAndKeepsMergedCustomization()
    {
        const string resourceKey = "TextFillColorPrimaryBrush";
        var resources = new TestResourceDictionary();
        resources.AmbientValues[resourceKey] = "ambient-app-dark";

        Assert.True(TryResolveUnrestricted(resources, out object ambient));
        Assert.Equal("ambient-app-dark", ambient);
        Assert.False(TryResolveExplicit(resources, out _));

        var explicitMerged = new TestResourceDictionary();
        explicitMerged.Values[resourceKey] = "scoped-light";
        resources.Merged.Add(explicitMerged);

        Assert.True(TryResolveExplicit(resources, out object scoped));
        Assert.Equal("scoped-light", scoped);

        static bool TryResolveUnrestricted(TestResourceDictionary root, out object value)
            => TryResolveFromDictionaryForTesting(
                root,
                "Light",
                resourceKey,
                static (dictionary, theme) => dictionary.Themes.GetValueOrDefault(theme),
                static (TestResourceDictionary dictionary, string key, out object result) =>
                    dictionary.TryGetEffectiveValue(key, out result),
                static dictionary => dictionary.Merged.Count,
                static (dictionary, index) => dictionary.Merged[index],
                out value);

        static bool TryResolveExplicit(TestResourceDictionary root, out object value)
            => TryResolveExplicitFromDictionaryForTesting(
                root,
                "Light",
                resourceKey,
                static (dictionary, theme) => dictionary.Themes.GetValueOrDefault(theme),
                static (TestResourceDictionary dictionary, string key, out object result) =>
                    dictionary.TryGetEffectiveValue(key, out result),
                static dictionary => dictionary.Merged.Count,
                static (dictionary, index) => dictionary.Merged[index],
                out value);
    }

    [Fact]
    public void ExplicitLookupAndBulkCapture_IgnoreAmbientChildFallbackInFavorOfAncestorOverride()
    {
        const string resourceKey = "MarkdownRenderer.Custom.ForegroundBrush";
        var child = new TestResourceDictionary();
        child.AmbientValues[resourceKey] = "ambient-application";
        var ancestor = new TestResourceDictionary();
        ancestor.Values[resourceKey] = "ancestor";
        Dictionary<string, object> captured = CaptureResources(child, ancestor);
        Assert.DoesNotContain(resourceKey, CaptureResources(child).Keys);
        Assert.Equal("ancestor", captured[resourceKey]);
    }

    private static bool TryResolveFromDictionaryForTesting(
        TestResourceDictionary resources,
        string themeKey,
        string resourceKey,
        Func<TestResourceDictionary, string, TestResourceDictionary?> getThemeDictionary,
        TryGetGraphResource<TestResourceDictionary> tryGetLocalValue,
        Func<TestResourceDictionary, int> getMergedCount,
        Func<TestResourceDictionary, int, TestResourceDictionary> getMergedAt,
        out object value)
        => TryResolveFromDictionaryForTesting(
            resources,
            themeKey == "Default" ? ["Default"] : [themeKey, "Default"],
            resourceKey,
            getThemeDictionary,
            tryGetLocalValue,
            getMergedCount,
            getMergedAt,
            out value);

    private static bool TryResolveFromDictionaryForTesting(
        TestResourceDictionary resources,
        IReadOnlyList<string> themeKeys,
        string resourceKey,
        Func<TestResourceDictionary, string, TestResourceDictionary?> getThemeDictionary,
        TryGetGraphResource<TestResourceDictionary> tryGetLocalValue,
        Func<TestResourceDictionary, int> getMergedCount,
        Func<TestResourceDictionary, int, TestResourceDictionary> getMergedAt,
        out object value)
        => CaptureSingleValue(
            resources, themeKeys, resourceKey, getThemeDictionary,
            static dictionary => EnumerateEffectiveKeys(dictionary),
            tryGetLocalValue, getMergedCount, getMergedAt, out value);

    private static bool TryResolveExplicitFromDictionaryForTesting(
        TestResourceDictionary resources,
        string themeKey,
        string resourceKey,
        Func<TestResourceDictionary, string, TestResourceDictionary?> getThemeDictionary,
        TryGetGraphResource<TestResourceDictionary> tryGetLocalValue,
        Func<TestResourceDictionary, int> getMergedCount,
        Func<TestResourceDictionary, int, TestResourceDictionary> getMergedAt,
        out object value)
        => CaptureSingleValue(
            resources,
            themeKey == "Default" ? ["Default"] : [themeKey, "Default"],
            resourceKey, getThemeDictionary,
            static dictionary => dictionary.Values.Keys,
            tryGetLocalValue, getMergedCount, getMergedAt, out value);

    private static bool CaptureSingleValue(
        TestResourceDictionary resources,
        IReadOnlyList<string> themeKeys,
        string resourceKey,
        Func<TestResourceDictionary, string, TestResourceDictionary?> getThemeDictionary,
        Func<TestResourceDictionary, IEnumerable<string>> enumerateLocalKeys,
        TryGetGraphResource<TestResourceDictionary> tryGetLocalValue,
        Func<TestResourceDictionary, int> getMergedCount,
        Func<TestResourceDictionary, int, TestResourceDictionary> getMergedAt,
        out object value)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        ResourceDictionaryGraphResolver.CaptureResolvedValues(
            resources, themeKeys,
            new HashSet<TestResourceDictionary>(ReferenceEqualityComparer.Instance),
            getThemeDictionary, enumerateLocalKeys, tryGetLocalValue,
            getMergedCount, getMergedAt,
            key => key == resourceKey,
            values);
        return values.TryGetValue(resourceKey, out value!);
    }

    private static IEnumerable<string> EnumerateEffectiveKeys(TestResourceDictionary dictionary)
    {
        foreach (string key in dictionary.Values.Keys)
            yield return key;
        foreach (string key in dictionary.AmbientValues.Keys)
            yield return key;
    }
}
