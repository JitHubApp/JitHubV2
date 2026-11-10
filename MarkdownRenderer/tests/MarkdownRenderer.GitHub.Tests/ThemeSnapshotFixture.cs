using Windows.UI;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;
using MarkdownRenderer.Theming.Internal.ResourceDictionaries;

namespace MarkdownRenderer.GitHub.Tests;

/// <summary>
/// Creates theme snapshots and captures resources from a test dictionary
/// graph for the theme snapshot tests.
/// </summary>
internal static class ThemeSnapshotFixture
{
    internal static ThemeSnapshot CreateSnapshot(
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

    internal static Dictionary<string, object> CaptureResources(
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
}
