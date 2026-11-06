using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.Text;
using MarkdownRenderer.Diagnostics;

namespace MarkdownRenderer.Theming;

/// <summary>
/// Looks up MarkdownRenderer.* and platform resources for one
/// <see cref="ThemeResolver"/>: the host's scoped dictionaries (captured once
/// per snapshot), then application resources by point lookup, with resolved
/// and missing keys memoized for the duration of a snapshot.
/// </summary>
internal sealed class ThemeResourceLookup
{
    private readonly RelevantResourceKeyCache<ResourceDictionary> _relevantResourceKeys;
    private readonly FrameworkElement _host;
    private readonly ResourceDictionary? _applicationResources;
    private readonly bool _isHighContrast;
    private Dictionary<string, object>? _resolvedResourceValues;
    private HashSet<string>? _missingResourceKeys;
    private HashSet<string>? _scopedResourceStyleRoles;
    // Scoped dictionaries are small and explicit. Capture their relevant
    // values once per synchronous snapshot; never enumerate the app dictionary.
    private Dictionary<string, object>? _scopedResourceValues;

    internal ThemeResourceLookup(
        FrameworkElement host,
        ResourceDictionary? applicationResources,
        bool isHighContrast,
        RelevantResourceKeyCache<ResourceDictionary> relevantResourceKeys)
    {
        _relevantResourceKeys = relevantResourceKeys ?? throw new ArgumentNullException(nameof(relevantResourceKeys));
        _host = host;
        _applicationResources = applicationResources;
        _isHighContrast = isHighContrast;
    }

    /// <summary>Starts memoizing lookups and drops any previously captured scoped values.</summary>
    internal void BeginSnapshot()
    {
        _resolvedResourceValues = new Dictionary<string, object>(StringComparer.Ordinal);
        _missingResourceKeys = new HashSet<string>(StringComparer.Ordinal);
        _scopedResourceValues = null;
    }

    /// <summary>Limits the scoped-dictionary capture to resources these style roles can demand.</summary>
    internal void UseScopedResourceStyleRoles(HashSet<string> styleRoles)
        => _scopedResourceStyleRoles = styleRoles;

    /// <summary>Ends the snapshot-only role filter and scoped capture once resources are read.</summary>
    internal void EndSnapshotCapture()
    {
        _scopedResourceStyleRoles = null;
        _scopedResourceValues = null;
    }
    internal Color? ResolveRoleColor(MarkdownStyleRole role, MarkdownStyleProperty property)
        => ResolveColorResource(MarkdownResourceKeys.ForRole(role, property));

    internal string? ResolveRoleFontFamily(MarkdownStyleRole role)
        => ResolveFontFamilyResource(MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.FontFamily));

    internal float? ResolveRoleFloat(MarkdownStyleRole role, MarkdownStyleProperty property)
        => ResolveFloatResource(MarkdownResourceKeys.ForRole(role, property));

    internal Windows.UI.Text.FontWeight? ResolveRoleFontWeight(MarkdownStyleRole role)
        => TryResolveResourceValue(
                MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.FontWeight),
                out var value) && value is Windows.UI.Text.FontWeight fontWeight
            ? fontWeight
            : null;

    internal FontStyle? ResolveRoleFontStyle(MarkdownStyleRole role)
        => TryResolveResourceValue(
                MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.FontStyle),
                out var value) && value is FontStyle fontStyle
            ? fontStyle
            : null;

    internal TextDecorations? ResolveRoleTextDecorations(MarkdownStyleRole role)
        => TryResolveResourceValue(
                MarkdownResourceKeys.ForRole(role, MarkdownStyleProperty.TextDecorations),
                out object value) &&
            ThemeResourceValues.TryExtractTextDecorations(value, out TextDecorations decorations)
                ? decorations
                : null;

    internal Thickness? ResolveRoleThickness(MarkdownStyleRole role, MarkdownStyleProperty property)
        => ResolveThicknessResource(MarkdownResourceKeys.ForRole(role, property));

    internal Color? ResolveColorResource(string key)
        => TryResolveResourceColor(key, out var color) ? color : null;

    internal string? ResolveFontFamilyResource(string key)
    {
        if (!TryResolveResourceValue(key, out var value))
            return null;

        return value switch
        {
            FontFamily family => family.Source,
            string name when !string.IsNullOrWhiteSpace(name) => name.Trim(),
            _ => null,
        };
    }

    internal float? ResolveFloatResource(string key)
    {
        if (!TryResolveResourceValue(key, out var value))
            return null;

        double number = value switch
        {
            double doubleValue => doubleValue,
            float floatValue => floatValue,
            int intValue => intValue,
            _ => double.NaN,
        };
        return double.IsFinite(number) && number >= 0 && number <= float.MaxValue
            ? (float)number
            : null;
    }

    internal Thickness? ResolveThicknessResource(string key)
        => TryResolveResourceValue(key, out var value) && value is Thickness thickness
            ? thickness
            : null;

    private bool TryResolveResourceColor(string resourceKey, out Color color)
    {
        if (TryResolveResourceValue(resourceKey, out var value) &&
            ThemeResourceValues.TryExtractColor(value, out color))
            return true;

        color = default;
        return false;
    }

    /// <summary>
    /// Resolves a platform Fluent token without consulting Application.Resources.
    /// WinUI projects merged platform dictionaries through that scope using the
    /// app's effective palette, which is not reliably described by
    /// Application.RequestedTheme and can therefore disagree with a descendant's
    /// ActualTheme. Element and ancestor dictionaries are scoped to the host and
    /// remain valid customizations. Stable MarkdownRenderer.* resources use the
    /// unrestricted lookup path above and therefore retain app-level support.
    /// </summary>
    internal bool TryResolvePlatformResourceColor(string resourceKey, out Color color)
    {
        if (TryResolveExplicitScopedResourceValue(resourceKey, out object value) &&
            ThemeResourceValues.TryExtractColor(value, out color))
        {
            return true;
        }

        color = default;
        return false;
    }

    private bool TryResolveResourceValue(string resourceKey, out object value)
    {
        if (_resolvedResourceValues is { } resolvedResources &&
            resourceKey.StartsWith(MarkdownResourceKeys.Prefix, StringComparison.Ordinal))
        {
            if (resolvedResources.TryGetValue(resourceKey, out value!))
                return true;
            if (_missingResourceKeys?.Contains(resourceKey) == true)
            {
                value = null!;
                return false;
            }
        }

        try
        {
            if (TryResolveScopedResourceValue(resourceKey, out value) ||
                TryResolveApplicationResourceValue(resourceKey, out value))
            {
                if (resourceKey.StartsWith(MarkdownResourceKeys.Prefix, StringComparison.Ordinal))
                    _resolvedResourceValues?[resourceKey] = value;
                return true;
            }
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ThemeResolver] Resource lookup failed for '{resourceKey}': {ex.Message}");
        }

        value = null!;
        if (resourceKey.StartsWith(MarkdownResourceKeys.Prefix, StringComparison.Ordinal))
            _missingResourceKeys?.Add(resourceKey);
        return false;
    }

    private bool TryResolveScopedResourceValue(string resourceKey, out object value)
    {
        if (_scopedResourceValues is not { } scopedValues)
        {
            scopedValues = new Dictionary<string, object>(StringComparer.Ordinal);
            // An exceptional or reentrant lookup must not retry the graph walk
            // for every style property in this same snapshot.
            _scopedResourceValues = scopedValues;
            var visited = new HashSet<ResourceDictionary>(ReferenceEqualityComparer.Instance);
            IReadOnlyList<string> themeKeys = GetThemeDictionaryKeys();
            Predicate<string> includeResource = _scopedResourceStyleRoles is not null
                ? IncludeScopedResourceForSnapshot
                : ThemeResourceScope.IncludeScopedRendererResource;
            for (DependencyObject? current = _host; current is not null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is FrameworkElement element)
                {
                    ResourceDictionaryGraphResolver.CaptureResolvedValues(
                        element.Resources,
                        themeKeys,
                        visited,
                        static (dictionary, key) =>
                            dictionary.ThemeDictionaries.TryGetValue(key, out object? selected) &&
                            selected is ResourceDictionary selectedDictionary
                                ? selectedDictionary
                                : null,
                        EnumerateRelevantResourceKeys,
                        static (ResourceDictionary dictionary, string key, out object result) =>
                            dictionary.TryGetValue(key, out result),
                        static dictionary => dictionary.MergedDictionaries.Count,
                        static (dictionary, index) => dictionary.MergedDictionaries[index],
                        includeResource,
                        scopedValues);
                }
            }
        }

        return scopedValues.TryGetValue(resourceKey, out value!);
    }

    private bool TryResolveExplicitScopedResourceValue(string resourceKey, out object value)
    {
        if (_resolvedResourceValues is { } resolvedResources &&
            ThemeResourceScope.IsScopedPlatformResourceKey(resourceKey))
        {
            if (resolvedResources.TryGetValue(resourceKey, out value!))
                return true;
            if (_missingResourceKeys?.Contains(resourceKey) == true)
            {
                value = null!;
                return false;
            }
        }

        try
        {
            if (TryResolveScopedResourceValue(resourceKey, out value))
            {
                if (ThemeResourceScope.IsScopedPlatformResourceKey(resourceKey))
                    _resolvedResourceValues?[resourceKey] = value;
                return true;
            }
        }
        catch (Exception ex)
        {
            MarkdownDiagnostics.WriteLine(
                $"[ThemeResolver] Explicit scoped resource lookup failed for '{resourceKey}': {ex.Message}");
        }

        value = null!;
        if (ThemeResourceScope.IsScopedPlatformResourceKey(resourceKey))
            _missingResourceKeys?.Add(resourceKey);
        return false;
    }

    private bool TryResolveApplicationResourceValue(string resourceKey, out object value)
    {
        if (_applicationResources is { } applicationResources)
        {
            // ResourceDictionary performs its own indexed lookup across merged
            // dictionaries and the active application theme. Avoid manually
            // walking and enumerating the app graph for every renderer snapshot.
            return applicationResources.TryGetValue(resourceKey, out value);
        }

        value = null!;
        return false;
    }

    private IReadOnlyList<string> GetThemeDictionaryKeys()
        => ThemeResourceScope.GetThemeDictionaryKeys(_isHighContrast, _host.ActualTheme);

    private bool IncludeScopedResourceForSnapshot(string key)
        => _scopedResourceStyleRoles is { } styleRoles &&
            ThemeResourceScope.IsScopedResourceRequiredForSnapshot(key, styleRoles);

    /// <summary>
    /// Creates the cache of relevant resource keys that one owner's lookups
    /// share across snapshots. Each control owns one, so a control's
    /// invalidation never drops another control's discovery.
    /// </summary>
    internal static RelevantResourceKeyCache<ResourceDictionary> CreateRelevantResourceKeyCache() => new(
        static resources => resources.Keys.Count,
        ThemeResourceValues.EnumerateStringKeys,
        ThemeResourceScope.IncludeScopedRendererResource);

    private IEnumerable<string> EnumerateRelevantResourceKeys(ResourceDictionary resources)
        => _relevantResourceKeys.GetRelevantKeys(resources);

    internal PlatformThemeColorOverrides ResolveScopedPlatformThemeColorOverrides()
    {
        Color? Resolve(params string[] resourceKeys)
        {
            foreach (string resourceKey in resourceKeys)
            {
                if (TryResolveExplicitScopedResourceValue(resourceKey, out object value) &&
                    ThemeResourceValues.TryExtractColor(value, out Color color))
                {
                    return color;
                }
            }

            return null;
        }

        return new PlatformThemeColorOverrides(
            PrimaryText: Resolve(
                "TextControlForegroundFocused",
                "TextControlForeground",
                "TextFillColorPrimaryBrush",
                "TextFillColorPrimary"),
            SecondaryText: Resolve(
                "TextFillColorSecondaryBrush",
                "TextFillColorSecondary",
                "TextControlPlaceholderForeground"),
            AccentText: Resolve(
                "AccentTextFillColorPrimaryBrush",
                "AccentTextFillColorPrimary",
                "SystemControlForegroundAccentBrush",
                "AccentFillColorDefaultBrush"),
            Surface: Resolve(
                "ApplicationPageBackgroundThemeBrush",
                "SolidBackgroundFillColorBaseBrush",
                "LayerFillColorDefaultBrush"));
    }

    internal Color ResolvePlatformBrush(string resourceKey, Color fallback)
    {
        try
        {
            if (TryResolvePlatformResourceColor(resourceKey, out Color color))
                return color;
        }
        catch (Exception ex) { MarkdownDiagnostics.WriteLine($"[ThemeResolver] ResolvePlatformBrush failed for '{resourceKey}': {ex.Message}"); }
        return fallback;
    }
}
