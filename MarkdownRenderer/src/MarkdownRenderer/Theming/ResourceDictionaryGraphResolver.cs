using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Theming;

/// <summary>
/// Captures explicit resources from a local/merged/theme dictionary graph while
/// visiting each dictionary at most once. The generic graph core keeps the WinUI
/// adapter small and makes cyclic precedence testable without activating XAML.
/// </summary>
internal static class ResourceDictionaryGraphResolver
{
    /// <summary>
    /// Captures the first value for every included key using the same
    /// local, reverse-merged, and selected-theme precedence as the per-key
    /// resolver. The caller supplies the platform-appropriate ordered theme
    /// dictionary candidates; exactly the first existing candidate is searched.
    /// A shared visited set lets callers layer
    /// child, ancestor, and application scopes without rescanning a dictionary
    /// graph for every individual resource key.
    /// </summary>
    internal static void CaptureResolvedValues<TNode>(
        TNode resources,
        IReadOnlyList<string> themeKeys,
        HashSet<TNode> visited,
        Func<TNode, string, TNode?> getThemeDictionary,
        Func<TNode, IEnumerable<string>> enumerateLocalKeys,
        TryGetGraphResource<TNode> tryGetLocalValue,
        Func<TNode, int> getMergedCount,
        Func<TNode, int, TNode> getMergedAt,
        Predicate<string> includeKey,
        IDictionary<string, object> destination)
        where TNode : class
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(themeKeys);
        ArgumentNullException.ThrowIfNull(visited);
        ArgumentNullException.ThrowIfNull(getThemeDictionary);
        ArgumentNullException.ThrowIfNull(enumerateLocalKeys);
        ArgumentNullException.ThrowIfNull(tryGetLocalValue);
        ArgumentNullException.ThrowIfNull(getMergedCount);
        ArgumentNullException.ThrowIfNull(getMergedAt);
        ArgumentNullException.ThrowIfNull(includeKey);
        ArgumentNullException.ThrowIfNull(destination);

        CaptureResolvedValuesCore(
            resources,
            themeKeys,
            visited,
            getThemeDictionary,
            enumerateLocalKeys,
            tryGetLocalValue,
            getMergedCount,
            getMergedAt,
            includeKey,
            destination);
    }

    private static void CaptureResolvedValuesCore<TNode>(
        TNode resources,
        IReadOnlyList<string> themeKeys,
        HashSet<TNode> visited,
        Func<TNode, string, TNode?> getThemeDictionary,
        Func<TNode, IEnumerable<string>> enumerateLocalKeys,
        TryGetGraphResource<TNode> tryGetLocalValue,
        Func<TNode, int> getMergedCount,
        Func<TNode, int, TNode> getMergedAt,
        Predicate<string> includeKey,
        IDictionary<string, object> destination)
        where TNode : class
    {
        if (!visited.Add(resources))
            return;

        foreach (string key in enumerateLocalKeys(resources))
        {
            if (includeKey(key) &&
                !destination.ContainsKey(key) &&
                tryGetLocalValue(resources, key, out object value))
            {
                destination.Add(key, value);
            }
        }

        for (int index = getMergedCount(resources) - 1; index >= 0; index--)
        {
            CaptureResolvedValuesCore(
                getMergedAt(resources, index),
                themeKeys,
                visited,
                getThemeDictionary,
                enumerateLocalKeys,
                tryGetLocalValue,
                getMergedCount,
                getMergedAt,
                includeKey,
                destination);
        }

        TNode? themeDictionary = SelectThemeDictionary(
            resources,
            themeKeys,
            getThemeDictionary);

        if (themeDictionary is not null)
        {
            CaptureResolvedValuesCore(
                themeDictionary,
                themeKeys,
                visited,
                getThemeDictionary,
                enumerateLocalKeys,
                tryGetLocalValue,
                getMergedCount,
                getMergedAt,
                includeKey,
                destination);
        }
    }

    private static TNode? SelectThemeDictionary<TNode>(
        TNode resources,
        IReadOnlyList<string> themeKeys,
        Func<TNode, string, TNode?> getThemeDictionary)
        where TNode : class
    {
        for (int index = 0; index < themeKeys.Count; index++)
        {
            TNode? selected = getThemeDictionary(resources, themeKeys[index]);
            if (selected is not null)
                return selected;
        }

        return null;
    }
}
