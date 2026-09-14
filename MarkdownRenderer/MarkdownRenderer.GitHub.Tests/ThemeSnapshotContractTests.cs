using Microsoft.UI.Xaml;
using Windows.UI;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Gfm.Renderers;
using Xunit;

namespace MarkdownRenderer.GitHub.Tests;

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

        bool found = ThemeResolver.TryResolveFromDictionaryForTesting(
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

        bool found = ThemeResolver.TryResolveFromDictionaryForTesting(
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

        bool found = ThemeResolver.TryResolveFromDictionaryForTesting(
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

        bool found = ThemeResolver.TryResolveFromDictionaryForTesting(
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
            ThemeResolver.GetThemeDictionaryKeysForTesting(isHighContrast, actualTheme));
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

        bool TryResolve(out object value) => ThemeResolver.TryResolveFromDictionaryForTesting(
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

        bool found = ThemeResolver.TryResolveFromDictionaryForTesting(
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
            out object value) => ThemeResolver.TryResolveFromDictionaryForTesting(
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
            => ThemeResolver.TryResolveFromDictionaryForTesting(
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
            => ThemeResolver.TryResolveExplicitFromDictionaryForTesting(
                root,
                "Light",
                resourceKey,
                static (dictionary, theme) => dictionary.Themes.GetValueOrDefault(theme),
                static (dictionary, key) => dictionary.Values.ContainsKey(key),
                static (TestResourceDictionary dictionary, string key, out object result) =>
                    dictionary.TryGetEffectiveValue(key, out result),
                static dictionary => dictionary.Merged.Count,
                static (dictionary, index) => dictionary.Merged[index],
                out value);
    }

    [Fact]
    public void BulkResourceCapture_MatchesPerKeyPrecedenceAndFiltersUnrelatedKeys()
    {
        const string sharedKey = "MarkdownRenderer.Custom.ForegroundBrush";
        const string defaultKey = "MarkdownRenderer.Custom.BackgroundBrush";
        const string mergedKey = "MarkdownRenderer.Custom.FontSize";
        var resources = new TestResourceDictionary();
        resources.Values[sharedKey] = "local";
        resources.Values["Unrelated.Resource"] = "ignored";

        var selectedTheme = new TestResourceDictionary();
        selectedTheme.Values[sharedKey] = "selected-theme";
        selectedTheme.Values[mergedKey] = "selected-theme";
        resources.Themes["Dark"] = selectedTheme;

        var defaultTheme = new TestResourceDictionary();
        defaultTheme.Values[defaultKey] = "default-theme";
        resources.Themes["Default"] = defaultTheme;

        var lowerPrecedenceMerged = new TestResourceDictionary();
        lowerPrecedenceMerged.Values[mergedKey] = "first-merged";
        var higherPrecedenceMerged = new TestResourceDictionary();
        higherPrecedenceMerged.Values[mergedKey] = "last-merged";
        resources.Merged.Add(lowerPrecedenceMerged);
        resources.Merged.Add(higherPrecedenceMerged);

        Dictionary<string, object> captured = CaptureResources(resources);

        Assert.Equal("local", captured[sharedKey]);
        Assert.DoesNotContain(defaultKey, captured.Keys);
        Assert.Equal("last-merged", captured[mergedKey]);
        Assert.DoesNotContain("Unrelated.Resource", captured.Keys);

        var fallbackResources = new TestResourceDictionary();
        fallbackResources.Themes["Default"] = defaultTheme;
        Dictionary<string, object> defaultCaptured = CaptureResources(fallbackResources);
        Assert.Equal("default-theme", defaultCaptured[defaultKey]);
    }

    [Fact]
    public void RelevantResourceKeyCache_AmortizesDiscoveryAndRefreshesWhenCountChanges()
    {
        const string resourceKey = "MarkdownRenderer.Direct.ForegroundBrush";
        var resources = new TestResourceDictionary();
        resources.Values[resourceKey] = "first";
        var cache = CreateRelevantResourceKeyCache();

        IReadOnlyList<string> first = cache.GetRelevantKeys(resources);
        resources.Values[resourceKey] = "replacement";
        IReadOnlyList<string> replacement = cache.GetRelevantKeys(resources);

        Assert.Same(first, replacement);
        Assert.Contains(resourceKey, replacement);
        Assert.Equal(1, resources.KeyEnumerationCount);

        resources.Values["Unrelated"] = "value";
        _ = cache.GetRelevantKeys(resources);
        Assert.Equal(2, resources.KeyEnumerationCount);
    }

    [Fact]
    public void RelevantResourceKeyCache_ExplicitInvalidationDiscoversSameCountKeySubstitution()
    {
        const string originalKey = "MarkdownRenderer.Original.ForegroundBrush";
        const string addedKey = "MarkdownRenderer.Added.ForegroundBrush";
        var resources = new TestResourceDictionary();
        resources.Values[originalKey] = "original";
        resources.Values["Unrelated"] = "value";
        var cache = CreateRelevantResourceKeyCache();
        _ = cache.GetRelevantKeys(resources);

        resources.Values.Remove("Unrelated");
        resources.Values[addedKey] = "added";
        Assert.DoesNotContain(addedKey, cache.GetRelevantKeys(resources));

        cache.Invalidate();
        Assert.Contains(addedKey, cache.GetRelevantKeys(resources));
    }

    [Theory]
    [InlineData("TextControlForegroundFocused")]
    [InlineData("TextControlForeground")]
    [InlineData("TextFillColorPrimaryBrush")]
    [InlineData("TextFillColorPrimary")]
    [InlineData("TextFillColorSecondaryBrush")]
    [InlineData("TextFillColorSecondary")]
    [InlineData("TextControlPlaceholderForeground")]
    [InlineData("AccentTextFillColorPrimaryBrush")]
    [InlineData("AccentTextFillColorPrimary")]
    [InlineData("SystemControlForegroundAccentBrush")]
    [InlineData("AccentFillColorDefaultBrush")]
    [InlineData("ApplicationPageBackgroundThemeBrush")]
    [InlineData("SolidBackgroundFillColorBaseBrush")]
    [InlineData("LayerFillColorDefaultBrush")]
    [InlineData("TextControlSelectionHighlightColor")]
    [InlineData("AccentFillColorSelectedTextBackgroundBrush")]
    [InlineData("SystemControlHighlightAccentBrush")]
    [InlineData("SystemColorHighlightColorBrush")]
    [InlineData("SystemColorHighlightColor")]
    [InlineData("TextOnAccentFillColorSelectedTextBrush")]
    [InlineData("TextOnAccentFillColorSelectedText")]
    [InlineData("SystemColorHighlightTextColorBrush")]
    [InlineData("SystemColorHighlightTextColor")]
    [InlineData("SystemControlFocusVisualPrimaryBrush")]
    [InlineData("FocusVisualPrimaryBrush")]
    public void ScopedPlatformCapture_RecognizesEveryFixedLookup(string resourceKey)
    {
        Assert.True(ThemeResolver.IsScopedPlatformResourceKey(resourceKey));
    }

    [Fact]
    public void ScopedPlatformCapture_RejectsUnrelatedPlatformKey()
    {
        Assert.False(ThemeResolver.IsScopedPlatformResourceKey("ControlCornerRadius"));
    }

    [Fact]
    public void BulkResourceCapture_PreservesNearestScopeAndBoundsSharedCycles()
    {
        const string inheritedKey = "MarkdownRenderer.Custom.FontFamily";
        const string scopedKey = "MarkdownRenderer.Custom.ForegroundBrush";
        var child = new TestResourceDictionary();
        child.Values[scopedKey] = "child";
        var ancestor = new TestResourceDictionary();
        ancestor.Values[scopedKey] = "ancestor";
        ancestor.Values[inheritedKey] = "inherited";
        child.Merged.Add(ancestor);
        ancestor.Merged.Add(child);

        Dictionary<string, object> captured = CaptureResources(child, ancestor);

        Assert.Equal("child", captured[scopedKey]);
        Assert.Equal("inherited", captured[inheritedKey]);
        Assert.Equal(2, captured.Count);
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
        var visited = new HashSet<TestResourceDictionary>(ReferenceEqualityComparer.Instance);

        Assert.False(TryResolveExplicit(child, visited, out _));
        Assert.True(TryResolveExplicit(ancestor, visited, out object resolved));
        Assert.Equal("ancestor", resolved);
        Assert.Equal(resolved, captured[resourceKey]);

        static bool TryResolveExplicit(
            TestResourceDictionary scope,
            HashSet<TestResourceDictionary> visited,
            out object value) => ResourceDictionaryGraphResolver.TryResolveExplicit(
                scope,
                ["Dark", "Default"],
                resourceKey,
                visited,
                static (dictionary, key) => dictionary.Themes.GetValueOrDefault(key),
                static (dictionary, key) => dictionary.Values.ContainsKey(key),
                static (TestResourceDictionary dictionary, string key, out object result) =>
                    dictionary.TryGetEffectiveValue(key, out result),
                static dictionary => dictionary.Merged.Count,
                static (dictionary, index) => dictionary.Merged[index],
                out value);
    }

    [Theory]
    [InlineData(MarkdownResourceKeys.DocumentPadding)]
    [InlineData(MarkdownResourceKeys.SelectionBackgroundBrush)]
    [InlineData(MarkdownResourceKeys.SelectionForegroundBrush)]
    [InlineData("MarkdownRenderer. Document.FontSize")]
    [InlineData("MarkdownRenderer.Document.FontSize")]
    [InlineData("MarkdownRenderer.Bad\u0001Role.FontSize")]
    public void ResourceRoleDiscovery_RejectsGlobalReservedOrMalformedKeys(string resourceKey)
    {
        Assert.False(MarkdownResourceKeys.TryGetStyleRoleName(resourceKey, out _));
    }

    [Fact]
    public void ResourceRoleDiscovery_RejectsOversizedRoleAndAcceptsCanonicalExtensionRole()
    {
        string oversized = $"MarkdownRenderer.{new string('x', 129)}.FontSize";

        Assert.False(MarkdownResourceKeys.TryGetStyleRoleName(oversized, out _));
        Assert.True(MarkdownResourceKeys.TryGetStyleRoleName(
            "MarkdownRenderer.Extension.Widget.FontSize",
            out string roleName));
        Assert.Equal("Extension.Widget", roleName);
    }

    [Theory]
    [InlineData("Document")]
    [InlineData("Selection")]
    [InlineData("FocusVisual")]
    [InlineData("Interaction")]
    [InlineData("Overflow")]
    public void MarkdownStyleRole_RejectsGlobalResourceScopes(string roleName)
    {
        Assert.Throws<ArgumentException>(() => new MarkdownStyleRole(roleName));
    }

    [Theory]
    [InlineData("Body", true)]
    [InlineData("Extension.Widget", true)]
    [InlineData("Document", false)]
    [InlineData(" Extension.Widget ", false)]
    [InlineData("Bad\u0001Role", false)]
    public void ResourceRoleProjection_OnlyAcceptsCanonicalNonReservedNames(
        string elementKey,
        bool expected)
    {
        bool projected = MarkdownStyleRole.TryCreateCanonical(
            elementKey,
            out MarkdownStyleRole role);

        Assert.Equal(expected, projected);
        Assert.Equal(expected ? elementKey : string.Empty, role.Name);
    }

    [Fact]
    public void ResourceRoleProjection_RejectsLongLegacyAliasWithoutThrowing()
    {
        string legacyAlias = MarkdownElementKeys.Class(new string('x', 129));

        Assert.False(MarkdownStyleRole.TryCreateCanonical(legacyAlias, out MarkdownStyleRole role));
        Assert.True(role.IsEmpty);
    }

    [Fact]
    public void LegacyOverrideAlias_RemainsUsableWhenItCannotProjectToAResourceRole()
    {
        string legacyAlias = MarkdownElementKeys.Class(new string('x', 129));
        var overrides = new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal)
        {
            [legacyAlias] = new ElementStyleOverride { FontSize = 19 },
        };
        ThemeSnapshot snapshot = CreateSnapshot(
            new ElementStyle { FontSize = 13 },
            overrides,
            isHighContrast: false,
            MarkdownStyleSheet.Empty);

        Assert.False(MarkdownStyleRole.TryCreateCanonical(legacyAlias, out _));
        ElementStyle resolved = snapshot.GetStyle(
            MarkdownElementKeys.Body,
            contextKeys: null,
            aliasKeys: [legacyAlias]);
        Assert.Equal(19, resolved.FontSize);
    }

    [Theory]
    [InlineData(ElementTheme.Light, ApplicationTheme.Dark)]
    [InlineData(ElementTheme.Dark, ApplicationTheme.Light)]
    public void OppositeApplicationTheme_PerControlSnapshotUsesHostThemeColors(
        ElementTheme hostTheme,
        ApplicationTheme applicationTheme)
    {
        Assert.NotEqual(
            hostTheme == ElementTheme.Dark,
            applicationTheme == ApplicationTheme.Dark);

        ThemeResolver.PlatformThemeColors resolved = ThemeResolver.ResolvePlatformThemeColorsForTesting(
            hostTheme,
            scoped: default);
        ThemeSnapshot snapshot = CreatePlatformColorSnapshot(hostTheme, resolved);

        ThemeResolver.PlatformThemeColorOverrides expected = hostTheme == ElementTheme.Light
            ? LightPlatformColors()
            : DarkPlatformColors();
        Assert.Equal(expected.PrimaryText, snapshot.GetStyle(MarkdownElementKeys.Body).Foreground);
        Assert.Equal(expected.SecondaryText, snapshot.GetStyle(MarkdownElementKeys.Heading6).Foreground);
        Assert.Equal(expected.AccentText, snapshot.GetStyle(MarkdownElementKeys.Link).Foreground);
        Assert.Equal(expected.Surface, snapshot.SurfaceColor);
        Assert.Equal(hostTheme == ElementTheme.Dark, snapshot.IsDark);
    }

    [Theory]
    [InlineData(ElementTheme.Light, ApplicationTheme.Dark)]
    [InlineData(ElementTheme.Dark, ApplicationTheme.Light)]
    public void OppositeApplicationTheme_ScopedPlatformOverridesStillWin(
        ElementTheme hostTheme,
        ApplicationTheme applicationTheme)
    {
        Assert.NotEqual(
            hostTheme == ElementTheme.Dark,
            applicationTheme == ApplicationTheme.Dark);
        var scoped = new ThemeResolver.PlatformThemeColorOverrides(
            PrimaryText: Color.FromArgb(0xFF, 0x11, 0x22, 0x33),
            SecondaryText: Color.FromArgb(0xFF, 0x44, 0x55, 0x66),
            AccentText: Color.FromArgb(0xFF, 0x77, 0x88, 0x99),
            Surface: Color.FromArgb(0xFF, 0xAA, 0xBB, 0xCC));

        ThemeResolver.PlatformThemeColors resolved = ThemeResolver.ResolvePlatformThemeColorsForTesting(
            hostTheme,
            scoped);
        ThemeSnapshot snapshot = CreatePlatformColorSnapshot(hostTheme, resolved);

        Assert.Equal(scoped.PrimaryText, snapshot.GetStyle(MarkdownElementKeys.Body).Foreground);
        Assert.Equal(scoped.SecondaryText, snapshot.GetStyle(MarkdownElementKeys.Heading6).Foreground);
        Assert.Equal(scoped.AccentText, snapshot.GetStyle(MarkdownElementKeys.Link).Foreground);
        Assert.Equal(scoped.Surface, snapshot.SurfaceColor);
    }

    [Fact]
    public void HighContrast_EnforcesColorsAndPreservesConsumerTypographyAndGeometry()
    {
        var mandatory = new ElementStyle
        {
            FontSize = 14,
            Foreground = Color.FromArgb(255, 255, 255, 255),
            Background = Color.FromArgb(255, 0, 0, 0),
            Margin = new Thickness(1),
        };
        var consumerOverride = new ElementStyleOverride
        {
            FontSize = 22,
            Foreground = Color.FromArgb(255, 255, 0, 0),
            Background = Color.FromArgb(255, 0, 255, 0),
            Margin = new Thickness(99),
        };
        var styleSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(MarkdownStyleRole.Body),
                new ElementStyleOverride
                {
                    FontSize = 27,
                    Foreground = Color.FromArgb(255, 0, 0, 255),
                    Padding = new Thickness(7),
                }),
        ]);
        var snapshot = CreateSnapshot(
            mandatory,
            new Dictionary<string, ElementStyleOverride>
            {
                [MarkdownElementKeys.Body] = consumerOverride,
            },
            isHighContrast: true,
            styleSheet);

        ElementStyle actual = snapshot.GetStyle(MarkdownElementKeys.Body);

        Assert.Equal(mandatory.Foreground, actual.Foreground);
        Assert.Equal(mandatory.Background, actual.Background);
        Assert.Equal(consumerOverride.Margin, actual.Margin);
        Assert.Equal(new Thickness(7), actual.Padding);
        Assert.Equal(27, actual.FontSize);
    }

    [Fact]
    public void LanguageAndStateQualifiers_AreAppliedByTheRendererSnapshot()
    {
        var baseStyle = new ElementStyle { FontSize = 13 };
        var styleSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.CodeBlock,
                    language: "csharp",
                    state: "diff"),
                new ElementStyleOverride { FontSize = 27 }),
        ]);
        var snapshot = CreateSnapshot(
            baseStyle,
            new Dictionary<string, ElementStyleOverride>(),
            isHighContrast: false,
            styleSheet);

        ElementStyle matched = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contextKeys: null,
            aliasKeys: null,
            language: "CSHARP",
            state: "diff");
        ElementStyle unmatched = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contextKeys: null,
            aliasKeys: null,
            language: "csharp",
            state: null);

        Assert.Equal(27, matched.FontSize);
        Assert.Equal(13, unmatched.FontSize);
    }

    [Fact]
    public void NestingQualifier_UsesSemanticListDepthNotContextCount()
    {
        var baseStyle = new ElementStyle { FontSize = 13 };
        var styleSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.Body,
                    minimumNestingDepth: 3,
                    maximumNestingDepth: 3),
                new ElementStyleOverride { FontSize = 31 }),
        ]);
        var snapshot = CreateSnapshot(
            baseStyle,
            new Dictionary<string, ElementStyleOverride>(),
            isHighContrast: false,
            styleSheet);

        ElementStyle actual = snapshot.GetStyle(
            MarkdownElementKeys.Body,
            [MarkdownElementKeys.Quote, MarkdownElementKeys.ListDepth(3)],
            aliasKeys: null);

        Assert.Equal(31, actual.FontSize);
    }

    [Fact]
    public void CompiledDispatch_PreservesQualifierAndLegacyOverridePrecedence()
    {
        var overrides = new Dictionary<string, ElementStyleOverride>
        {
            [MarkdownElementKeys.Class("warning")] = new() { CornerRadius = 7 },
            [MarkdownElementKeys.Context(
                MarkdownElementKeys.ListDepth(2),
                MarkdownElementKeys.Class("warning"))] = new() { BorderThickness = 3 },
        };
        var styleSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.CodeBlock,
                    minimumNestingDepth: 1),
                new ElementStyleOverride { FontSize = 17 }),
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.CodeBlock,
                    minimumNestingDepth: 2,
                    maximumNestingDepth: 2,
                    language: "csharp",
                    state: "diff",
                    className: "warning"),
                new ElementStyleOverride { FontSize = 23, BorderThickness = 5 }),
        ]);
        ThemeSnapshot snapshot = CreateSnapshot(
            new ElementStyle { FontSize = 13 },
            overrides,
            isHighContrast: false,
            styleSheet,
            textScaleFactor: 2);

        ElementStyle actual = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            [MarkdownElementKeys.Quote, MarkdownElementKeys.ListDepth(2)],
            [MarkdownElementKeys.Class("warning")],
            language: " CSHARP ",
            state: " diff ");

        Assert.Equal(46, actual.FontSize);
        Assert.Equal(7, actual.CornerRadius);
        Assert.Equal(5, actual.BorderThickness);
    }

    [Fact]
    public void ContextualStyleCache_CopiesMutableQuerySequences()
    {
        var styleSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.CodeBlock,
                    minimumNestingDepth: 2,
                    maximumNestingDepth: 2,
                    className: "warning"),
                new ElementStyleOverride { FontSize = 29 }),
        ]);
        ThemeSnapshot snapshot = CreateSnapshot(
            new ElementStyle { FontSize = 13 },
            new Dictionary<string, ElementStyleOverride>(),
            isHighContrast: false,
            styleSheet);
        var contexts = new List<string> { MarkdownElementKeys.ListDepth(2) };
        var aliases = new List<string> { MarkdownElementKeys.Class("warning") };

        ElementStyle first = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contexts,
            aliases,
            language: null,
            state: null);
        contexts[0] = MarkdownElementKeys.ListDepth(1);
        aliases[0] = MarkdownElementKeys.Class("ordinary");
        ElementStyle changed = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contexts,
            aliases,
            language: null,
            state: null);
        contexts[0] = MarkdownElementKeys.ListDepth(2);
        aliases[0] = MarkdownElementKeys.Class("warning");
        ElementStyle restored = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contexts,
            aliases,
            language: null,
            state: null);

        Assert.Equal(29, first.FontSize);
        Assert.Equal(13, changed.FontSize);
        Assert.Same(first, restored);
    }

    [Fact]
    public void WarmContextualStyleLookup_DoesNotAllocate()
    {
        var styleSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.CodeBlock,
                    minimumNestingDepth: 2,
                    maximumNestingDepth: 2,
                    language: "csharp",
                    state: "diff",
                    className: "warning"),
                new ElementStyleOverride { FontSize = 23 }),
        ]);
        ThemeSnapshot snapshot = CreateSnapshot(
            new ElementStyle { FontSize = 13 },
            new Dictionary<string, ElementStyleOverride>(),
            isHighContrast: false,
            styleSheet,
            textScaleFactor: 2.25);
        string[] contexts = [MarkdownElementKeys.Quote, MarkdownElementKeys.ListDepth(2)];
        string[] aliases = [MarkdownElementKeys.Class("warning")];
        ElementStyle expected = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contexts,
            aliases,
            language: "csharp",
            state: "diff");

        for (int i = 0; i < 1_000; i++)
        {
            _ = snapshot.GetStyle(
                MarkdownElementKeys.CodeBlock,
                contexts,
                aliases,
                language: "csharp",
                state: "diff");
        }

        ElementStyle? observed = null;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            observed = snapshot.GetStyle(
                MarkdownElementKeys.CodeBlock,
                contexts,
                aliases,
                language: "csharp",
                state: "diff");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(expected, observed);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void ContextualStyleCache_CanonicalizesLanguageAndStateQualifiers()
    {
        var styleSheet = new MarkdownStyleSheet(
        [
            new MarkdownStyleRule(
                new MarkdownStyleSelector(
                    MarkdownStyleRole.CodeBlock,
                    language: "csharp",
                    state: "diff"),
                new ElementStyleOverride { FontSize = 23 }),
        ]);
        ThemeSnapshot snapshot = CreateSnapshot(
            new ElementStyle { FontSize = 13 },
            new Dictionary<string, ElementStyleOverride>(),
            isHighContrast: false,
            styleSheet);
        string[] contexts = [MarkdownElementKeys.ListDepth(2)];
        string[] aliases = [MarkdownElementKeys.Class("warning")];

        ElementStyle canonical = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contexts,
            aliases,
            language: "csharp",
            state: "diff");
        ElementStyle equivalent = snapshot.GetStyle(
            MarkdownElementKeys.CodeBlock,
            contexts,
            aliases,
            language: " CSHARP ",
            state: " DIFF ");

        Assert.Same(canonical, equivalent);
        Assert.Equal(23, equivalent.FontSize);
    }

    [Theory]
    [InlineData(1.0, false, 20)]
    [InlineData(1.5, false, 30)]
    [InlineData(2.0, false, 40)]
    [InlineData(2.25, false, 45)]
    [InlineData(1.0, true, 40)]
    [InlineData(2.25, true, 45)]
    public void TaskMarkerSize_TracksTextScaleAndInteractiveMinimum(
        double textScaleFactor,
        bool editable,
        float expected)
    {
        ThemeSnapshot snapshot = CreateSnapshot(
            new ElementStyle(),
            new Dictionary<string, ElementStyleOverride>(),
            isHighContrast: false,
            MarkdownStyleSheet.Empty,
            textScaleFactor,
            minimumInteractiveSize: 40);

        Assert.Equal(expected, TaskListItemRenderer.GetTaskMarkerSize(snapshot, editable));
    }

    [Fact]
    public void TaskMarkerColors_UseTheResolvedSelectionContract()
    {
        Color selectionAccent = Color.FromArgb(0xFF, 0x12, 0x34, 0x56);
        Color selectionForeground = Color.FromArgb(0xFF, 0xFE, 0xDC, 0xBA);
        ThemeSnapshot snapshot = CreateSnapshot(
            new ElementStyle(),
            new Dictionary<string, ElementStyleOverride>(),
            isHighContrast: false,
            MarkdownStyleSheet.Empty,
            selectionHighlightColor: selectionAccent,
            selectionForegroundColor: selectionForeground);

        (Color accent, Color foreground) = TaskListItemRenderer.GetTaskMarkerColors(snapshot);

        Assert.Equal(selectionAccent, accent);
        Assert.Equal(selectionForeground, foreground);
    }

    private static ThemeSnapshot CreateSnapshot(
        ElementStyle style,
        IReadOnlyDictionary<string, ElementStyleOverride> overrides,
        bool isHighContrast,
        MarkdownStyleSheet styleSheet,
        double textScaleFactor = 1,
        double minimumInteractiveSize = 40,
        Color? selectionHighlightColor = null,
        Color? selectionForegroundColor = null)
    {
        var styles = new Dictionary<string, ElementStyle>
        {
            [MarkdownElementKeys.Body] = style,
            [MarkdownElementKeys.CodeBlock] = style,
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            styles,
            overrides,
            transparent,
            selectionHighlightColor ?? transparent,
            selectionForegroundColor ?? transparent,
            transparent,
            isDark: false,
            isHighContrast,
            textScaleFactor,
            styleSheet,
            minimumInteractiveSize);
    }

    private static Dictionary<string, object> CaptureResources(
        params TestResourceDictionary[] scopes)
    {
        var captured = new Dictionary<string, object>(StringComparer.Ordinal);
        var visited = new HashSet<TestResourceDictionary>(ReferenceEqualityComparer.Instance);
        foreach (TestResourceDictionary scope in scopes)
        {
            ResourceDictionaryGraphResolver.CaptureResolvedValues(
                scope,
                ["Dark", "Default"],
                visited,
                static (dictionary, key) => dictionary.Themes.GetValueOrDefault(key),
                static dictionary => dictionary.Values.Keys,
                static (TestResourceDictionary dictionary, string key, out object value) =>
                    dictionary.Values.TryGetValue(key, out value!),
                static dictionary => dictionary.Merged.Count,
                static (dictionary, index) => dictionary.Merged[index],
                static key => key.StartsWith("MarkdownRenderer.", StringComparison.Ordinal),
                captured);
        }

        return captured;
    }

    private static RelevantResourceKeyCache<TestResourceDictionary> CreateRelevantResourceKeyCache()
        => new(
            static dictionary => dictionary.Values.Count,
            static dictionary => dictionary.EnumerateValueKeys(),
            static key => key.StartsWith("MarkdownRenderer.", StringComparison.Ordinal));

    private static ThemeSnapshot CreatePlatformColorSnapshot(
        ElementTheme hostTheme,
        ThemeResolver.PlatformThemeColors colors)
    {
        var styles = new Dictionary<string, ElementStyle>
        {
            [MarkdownElementKeys.Body] = new ElementStyle { Foreground = colors.PrimaryText },
            [MarkdownElementKeys.Heading6] = new ElementStyle { Foreground = colors.SecondaryText },
            [MarkdownElementKeys.Link] = new ElementStyle { Foreground = colors.AccentText },
        };
        Color transparent = Color.FromArgb(0, 0, 0, 0);
        return new ThemeSnapshot(
            styles,
            new Dictionary<string, ElementStyleOverride>(),
            colors.Surface,
            transparent,
            transparent,
            transparent,
            isDark: hostTheme == ElementTheme.Dark,
            isHighContrast: false,
            textScaleFactor: 1,
            MarkdownStyleSheet.Empty);
    }

    private static ThemeResolver.PlatformThemeColorOverrides LightPlatformColors()
        => new(
            PrimaryText: Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A),
            SecondaryText: Color.FromArgb(0xFF, 0x7A, 0x7A, 0x7A),
            AccentText: Color.FromArgb(0xFF, 0x00, 0x5F, 0xB8),
            Surface: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

    private static ThemeResolver.PlatformThemeColorOverrides DarkPlatformColors()
        => new(
            PrimaryText: Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
            SecondaryText: Color.FromArgb(0xFF, 0x9D, 0x9D, 0x9D),
            AccentText: Color.FromArgb(0xFF, 0x60, 0xCD, 0xFF),
            Surface: Color.FromArgb(0xFF, 0x20, 0x20, 0x20));

    private sealed class TestResourceDictionary
    {
        internal Dictionary<string, object> Values { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, object> AmbientValues { get; } = new(StringComparer.Ordinal);
        internal Dictionary<string, TestResourceDictionary> Themes { get; } = new(StringComparer.Ordinal);
        internal List<TestResourceDictionary> Merged { get; } = [];
        internal int KeyEnumerationCount { get; private set; }

        internal IEnumerable<string> EnumerateValueKeys()
        {
            KeyEnumerationCount++;
            return Values.Keys;
        }

        internal bool TryGetEffectiveValue(string key, out object value)
            => Values.TryGetValue(key, out value!) || AmbientValues.TryGetValue(key, out value!);
    }
}
