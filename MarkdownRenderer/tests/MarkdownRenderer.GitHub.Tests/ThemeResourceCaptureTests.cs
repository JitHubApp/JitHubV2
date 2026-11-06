using Windows.UI;
using MarkdownRenderer.Theming;
using Xunit;
using static MarkdownRenderer.GitHub.Tests.ThemeSnapshotFixture;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Checks how theme snapshots capture resources: bulk capture against
/// per-key precedence, built-in role overrides, role-demand scoped
/// capture, the relevant resource key cache and fixed platform lookups.
/// </summary>
[Collection(AllocationSensitiveContractCollection.Name)]
public sealed class ThemeResourceCaptureTests
{
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
    public void BuiltInRoleResourceOverridesRefreshPerSnapshotAndPreferScopedValues()
    {
        const string resourceKey = "MarkdownRenderer.Body.ForegroundBrush";
        const string alertAccentResourceKey = "MarkdownRenderer.AlertWarning.AccentBrush";
        Color appFirst = Color.FromArgb(0xFF, 0x11, 0x22, 0x33);
        Color appSecond = Color.FromArgb(0xFF, 0x44, 0x55, 0x66);
        Color scopedFirst = Color.FromArgb(0xFF, 0x77, 0x88, 0x99);
        Color scopedSecond = Color.FromArgb(0xFF, 0xAA, 0xBB, 0xCC);
        Color alertAppFirst = Color.FromArgb(0xFF, 0x13, 0x57, 0x9B);
        Color alertAppSecond = Color.FromArgb(0xFF, 0x24, 0x68, 0xAC);
        Color alertScopedFirst = Color.FromArgb(0xFF, 0x35, 0x79, 0xBD);
        Color alertScopedSecond = Color.FromArgb(0xFF, 0x46, 0x8A, 0xCE);
        var appResources = new TestResourceDictionary();
        var scopedResources = new TestResourceDictionary();
        appResources.Values[resourceKey] = appFirst;
        scopedResources.Values[resourceKey] = scopedFirst;
        appResources.Values[alertAccentResourceKey] = alertAppFirst;
        scopedResources.Values[alertAccentResourceKey] = alertScopedFirst;

        Assert.Equal(scopedFirst, ResolveBodyForeground());

        appResources.Values[resourceKey] = appSecond;
        Assert.Equal(scopedFirst, ResolveBodyForeground());

        scopedResources.Values[resourceKey] = scopedSecond;
        Assert.Equal(scopedSecond, ResolveBodyForeground());

        scopedResources.Values.Remove(resourceKey);
        Assert.Equal(appSecond, ResolveBodyForeground());

        Assert.Equal(alertScopedFirst, ResolveAlertAccent());
        appResources.Values[alertAccentResourceKey] = alertAppSecond;
        Assert.Equal(alertScopedFirst, ResolveAlertAccent());
        scopedResources.Values[alertAccentResourceKey] = alertScopedSecond;
        Assert.Equal(alertScopedSecond, ResolveAlertAccent());
        scopedResources.Values.Remove(alertAccentResourceKey);
        Assert.Equal(alertAppSecond, ResolveAlertAccent());

        Color ResolveBodyForeground()
        {
            Dictionary<string, object> captured = CaptureResources(scopedResources, appResources);
            var baseStyle = new ElementStyle { Foreground = Color.FromArgb(0xFF, 1, 1, 1) };
            var overrides = new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.Body] = new() { Foreground = (Color)captured[resourceKey] },
            };
            return CreateSnapshot(
                baseStyle,
                overrides,
                isHighContrast: false,
                MarkdownStyleSheet.Empty).GetStyle(MarkdownElementKeys.Body).Foreground;
        }

        Color ResolveAlertAccent()
        {
            Dictionary<string, object> captured = CaptureResources(scopedResources, appResources);
            Color transparent = Color.FromArgb(0, 0, 0, 0);
            var styles = new Dictionary<string, ElementStyle>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.AlertWarning] = new() { AccentBar = Color.FromArgb(0xFF, 0xF5, 0x9E, 0x0B) },
            };
            var overrides = new Dictionary<string, ElementStyleOverride>(StringComparer.Ordinal)
            {
                [MarkdownElementKeys.AlertWarning] = new() { AccentBar = (Color)captured[alertAccentResourceKey] },
            };
            var snapshot = new ThemeSnapshot(
                styles,
                overrides,
                transparent,
                transparent,
                transparent,
                transparent,
                isDark: false,
                isHighContrast: false,
                textScaleFactor: 1);

            return snapshot.GetStyle(MarkdownElementKeys.AlertWarning).AccentBar!.Value;
        }
    }

    [Fact]
    public void ScopedResourceCapture_FollowsRoleDemandAndAlwaysRetainsGlobalThemeInputs()
    {
        string[] elementKeys =
        [
            MarkdownElementKeys.Body,
            MarkdownElementKeys.CodeBlock,
            "Extension.Role",
            " Extension.Role",
            MarkdownElementKeys.Class("align-center"),
        ];

        HashSet<string> demandedRoles = ThemeResolver.GetScopedResourceStyleRoles(
            elementKeys,
            1UL << 6); // Body in ThemeResolver's fixed built-in role order.

        Assert.Equal(
            new[] { ".align-center", "Body", "Extension.Role" },
            demandedRoles.Order(StringComparer.Ordinal));
        Assert.True(ThemeResourceScope.IsScopedResourceRequiredForSnapshot(
            MarkdownResourceKeys.ForRole(MarkdownStyleRole.Body, MarkdownStyleProperty.ForegroundBrush),
            demandedRoles));
        Assert.False(ThemeResourceScope.IsScopedResourceRequiredForSnapshot(
            MarkdownResourceKeys.ForRole(MarkdownStyleRole.CodeBlock, MarkdownStyleProperty.ForegroundBrush),
            demandedRoles));
        Assert.True(ThemeResourceScope.IsScopedResourceRequiredForSnapshot(
            MarkdownResourceKeys.DocumentSurfaceBrush,
            demandedRoles));
        Assert.True(ThemeResourceScope.IsScopedResourceRequiredForSnapshot(
            "TextControlForeground",
            demandedRoles));
        Assert.True(ThemeResourceScope.IsScopedResourceRequiredForSnapshot(
            MarkdownResourceKeys.ForRole(
                MarkdownStyleRole.FromElementKey(MarkdownElementKeys.Class("align-center")),
                MarkdownStyleProperty.ForegroundBrush),
            demandedRoles));
        Assert.False(ThemeResourceScope.IsScopedResourceRequiredForSnapshot(
            "ControlCornerRadius",
            demandedRoles));

        // The demand mask is only a scoped role-value optimization. Global
        // selection, focus, sizing, and surface inputs remain captured for
        // every document and theme, including High Contrast.
        foreach (string globalKey in new[]
        {
            MarkdownResourceKeys.DocumentSurfaceBrush,
            MarkdownResourceKeys.SelectionBackgroundBrush,
            MarkdownResourceKeys.SelectionForegroundBrush,
            MarkdownResourceKeys.FocusVisualBrush,
            MarkdownResourceKeys.DocumentPadding,
            MarkdownResourceKeys.BlockSpacing,
            MarkdownResourceKeys.MinimumInteractiveSize,
            MarkdownResourceKeys.OverflowIndicatorBrush,
        })
        {
            Assert.True(ThemeResourceScope.IsScopedResourceRequiredForSnapshot(globalKey, demandedRoles));
        }

        HashSet<string> fullResolutionRoles = ThemeResolver.GetScopedResourceStyleRoles(
            elementKeys,
            builtInStyleRoleDemandMask: null);
        Assert.Contains("CodeBlock", fullResolutionRoles);
        Assert.Contains("Extension.Role", fullResolutionRoles);
    }

    [Fact]
    public void ScopedResourceCapture_DoesNotReadUndemandedRoleValuesFromDictionaryGraph()
    {
        var resources = new TestResourceDictionary();
        const int extensionRoleCount = 30;
        foreach (MarkdownStyleProperty property in Enum.GetValues<MarkdownStyleProperty>())
        {
            resources.Values[MarkdownResourceKeys.ForRole(MarkdownStyleRole.Body, property)] = property.ToString();
            resources.Values[MarkdownResourceKeys.ForRole(MarkdownStyleRole.CodeBlock, property)] = property.ToString();
            for (int index = 0; index < extensionRoleCount; index++)
            {
                var role = new MarkdownStyleRole($"Extension{index}");
                resources.Values[MarkdownResourceKeys.ForRole(role, property)] = property.ToString();
            }
        }
        resources.Values[MarkdownResourceKeys.DocumentSurfaceBrush] = "surface";
        resources.Values["TextControlForeground"] = "platform-foreground";

        HashSet<string> demandedRoles = ThemeResolver.GetScopedResourceStyleRoles(
            [MarkdownElementKeys.Body, MarkdownElementKeys.CodeBlock, "Extension12"],
            1UL << 6);
        var captured = new Dictionary<string, object>(StringComparer.Ordinal);
        int reads = 0;
        ResourceDictionaryGraphResolver.CaptureResolvedValues(
            resources,
            ["Dark", "Default"],
            new HashSet<TestResourceDictionary>(ReferenceEqualityComparer.Instance),
            static (dictionary, key) => dictionary.Themes.GetValueOrDefault(key),
            static dictionary => dictionary.Values.Keys,
            (TestResourceDictionary dictionary, string key, out object value) =>
            {
                reads++;
                return dictionary.Values.TryGetValue(key, out value!);
            },
            static dictionary => dictionary.Merged.Count,
            static (dictionary, index) => dictionary.Merged[index],
            key => ThemeResourceScope.IsScopedResourceRequiredForSnapshot(key, demandedRoles),
            captured);

        int unfilteredValueCount = extensionRoleCount * Enum.GetValues<MarkdownStyleProperty>().Length +
                                   2 * Enum.GetValues<MarkdownStyleProperty>().Length + 2;
        int expectedReadCount = 2 * Enum.GetValues<MarkdownStyleProperty>().Length + 2;
        Assert.Equal(expectedReadCount, reads);
        Assert.Equal(expectedReadCount, captured.Count);
        Assert.True(reads < unfilteredValueCount / 10,
            $"Expected selective capture to read fewer than one tenth of {unfilteredValueCount} values; read {reads}.");
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

    [Fact]
    public void RelevantResourceKeyCache_RepeatedMembershipChecksEnumerateOnlyOnce()
    {
        const string firstKey = "MarkdownRenderer.First.ForegroundBrush";
        const string secondKey = "MarkdownRenderer.Second.ForegroundBrush";
        var resources = new TestResourceDictionary();
        resources.Values[secondKey] = "second";
        resources.Values["Unrelated"] = "ignored";
        resources.Values[firstKey] = "first";
        var cache = CreateRelevantResourceKeyCache();

        Assert.True(cache.ContainsRelevantKey(resources, firstKey));
        Assert.True(cache.ContainsRelevantKey(resources, secondKey));
        Assert.False(cache.ContainsRelevantKey(resources, "MarkdownRenderer.Missing.ForegroundBrush"));
        Assert.Equal(1, resources.KeyEnumerationCount);
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
        Assert.True(ThemeResourceScope.IsScopedPlatformResourceKey(resourceKey));
    }

    [Fact]
    public void ScopedPlatformCapture_RejectsUnrelatedPlatformKey()
    {
        Assert.False(ThemeResourceScope.IsScopedPlatformResourceKey("ControlCornerRadius"));
        Assert.False(ThemeResourceScope.IsScopedPlatformResourceKey(null!));
        Assert.False(ThemeResourceScope.IsScopedPlatformResourceKey("textControlForeground"));
        Assert.False(ThemeResourceScope.IsScopedPlatformResourceKey("TextControlForegroundSuffix"));
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

    private static RelevantResourceKeyCache<TestResourceDictionary> CreateRelevantResourceKeyCache()
        => new(
            static dictionary => dictionary.Values.Count,
            static dictionary => dictionary.EnumerateValueKeys(),
            static key => key.StartsWith("MarkdownRenderer.", StringComparison.Ordinal));
}
