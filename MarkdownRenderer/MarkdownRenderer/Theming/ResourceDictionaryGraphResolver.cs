using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MarkdownRenderer.Theming;

internal delegate bool TryGetGraphResource<TNode>(TNode node, string resourceKey, out object value)
    where TNode : class;

/// <summary>
/// Resolves a resource from a local/merged/theme dictionary graph while visiting
/// each dictionary at most once. The generic graph core keeps the WinUI adapter
/// small and makes cyclic precedence behavior testable without activating XAML.
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

    internal static bool TryResolve<TNode>(
        TNode resources,
        IReadOnlyList<string> themeKeys,
        string resourceKey,
        HashSet<TNode> visited,
        Func<TNode, string, TNode?> getThemeDictionary,
        TryGetGraphResource<TNode> tryGetLocalValue,
        Func<TNode, int> getMergedCount,
        Func<TNode, int, TNode> getMergedAt,
        out object value)
        where TNode : class
        => TryResolveCore(
            resources,
            themeKeys,
            resourceKey,
            visited,
            getThemeDictionary,
            hasExplicitLocalValue: null,
            tryGetLocalValue,
            getMergedCount,
            getMergedAt,
            out value);

    /// <summary>
    /// Resolves only values explicitly declared by each dictionary node. This
    /// prevents projections such as WinUI ResourceDictionary.TryGetValue from
    /// reporting an ambient application fallback as though it belonged to a
    /// control- or ancestor-local dictionary.
    /// </summary>
    internal static bool TryResolveExplicit<TNode>(
        TNode resources,
        IReadOnlyList<string> themeKeys,
        string resourceKey,
        HashSet<TNode> visited,
        Func<TNode, string, TNode?> getThemeDictionary,
        Func<TNode, string, bool> hasExplicitLocalValue,
        TryGetGraphResource<TNode> tryGetLocalValue,
        Func<TNode, int> getMergedCount,
        Func<TNode, int, TNode> getMergedAt,
        out object value)
        where TNode : class
        => TryResolveCore(
            resources,
            themeKeys,
            resourceKey,
            visited,
            getThemeDictionary,
            hasExplicitLocalValue,
            tryGetLocalValue,
            getMergedCount,
            getMergedAt,
            out value);

    private static bool TryResolveCore<TNode>(
        TNode resources,
        IReadOnlyList<string> themeKeys,
        string resourceKey,
        HashSet<TNode> visited,
        Func<TNode, string, TNode?> getThemeDictionary,
        Func<TNode, string, bool>? hasExplicitLocalValue,
        TryGetGraphResource<TNode> tryGetLocalValue,
        Func<TNode, int> getMergedCount,
        Func<TNode, int, TNode> getMergedAt,
        out object value)
        where TNode : class
    {
        if (!visited.Add(resources))
        {
            value = null!;
            return false;
        }

        if ((hasExplicitLocalValue is null || hasExplicitLocalValue(resources, resourceKey)) &&
            tryGetLocalValue(resources, resourceKey, out value))
            return true;

        for (int i = getMergedCount(resources) - 1; i >= 0; i--)
        {
            if (TryResolveCore(
                    getMergedAt(resources, i),
                    themeKeys,
                    resourceKey,
                    visited,
                    getThemeDictionary,
                    hasExplicitLocalValue,
                    tryGetLocalValue,
                    getMergedCount,
                    getMergedAt,
                    out value))
            {
                return true;
            }
        }

        // WinUI selects exactly one theme dictionary for a ResourceDictionary.
        // Default is a dictionary-selection fallback, not a per-key fallback
        // after an existing Light/Dark/HighContrast dictionary misses the key.
        TNode? themeDictionary = SelectThemeDictionary(
            resources,
            themeKeys,
            getThemeDictionary);

        if (themeDictionary is not null &&
            TryResolveCore(
                themeDictionary,
                themeKeys,
                resourceKey,
                visited,
                getThemeDictionary,
                hasExplicitLocalValue,
                tryGetLocalValue,
                getMergedCount,
                getMergedAt,
                out value))
        {
            return true;
        }

        value = null!;
        return false;
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

/// <summary>
/// Caches the small relevant-key projection of dictionary-like nodes without
/// retaining the nodes themselves. Values are deliberately not cached: graph
/// capture re-reads each current value so brush replacement and mutation are
/// reflected on the next snapshot. A key-count change refreshes discovery;
/// callers can invalidate explicitly for a same-count key-set substitution.
/// </summary>
internal sealed class RelevantResourceKeyCache<TNode>
    where TNode : class
{
    private readonly Func<TNode, int> _getKeyCount;
    private readonly Func<TNode, IEnumerable<string>> _enumerateKeys;
    private readonly Predicate<string> _isRelevant;
    private readonly ConditionalWeakTable<TNode, CacheEntry> _entries = new();

    internal RelevantResourceKeyCache(
        Func<TNode, int> getKeyCount,
        Func<TNode, IEnumerable<string>> enumerateKeys,
        Predicate<string> isRelevant)
    {
        _getKeyCount = getKeyCount ?? throw new ArgumentNullException(nameof(getKeyCount));
        _enumerateKeys = enumerateKeys ?? throw new ArgumentNullException(nameof(enumerateKeys));
        _isRelevant = isRelevant ?? throw new ArgumentNullException(nameof(isRelevant));
    }

    internal IReadOnlyList<string> GetRelevantKeys(TNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        CacheEntry entry = _entries.GetValue(node, static _ => new CacheEntry());
        return entry.GetOrRefresh(node, _getKeyCount, _enumerateKeys, _isRelevant);
    }

    internal void Invalidate() => _entries.Clear();

    private sealed class CacheEntry
    {
        private readonly object _gate = new();
        private int _observedKeyCount = -1;
        private string[] _relevantKeys = [];

        internal IReadOnlyList<string> GetOrRefresh(
            TNode node,
            Func<TNode, int> getKeyCount,
            Func<TNode, IEnumerable<string>> enumerateKeys,
            Predicate<string> isRelevant)
        {
            int currentKeyCount = getKeyCount(node);
            lock (_gate)
            {
                if (currentKeyCount == _observedKeyCount)
                    return _relevantKeys;

                var relevantKeys = new List<string>();
                foreach (string key in enumerateKeys(node))
                {
                    if (isRelevant(key))
                        relevantKeys.Add(key);
                }

                _relevantKeys = relevantKeys.ToArray();
                _observedKeyCount = currentKeyCount;
                return _relevantKeys;
            }
        }
    }
}
