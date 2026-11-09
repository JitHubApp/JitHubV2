using System;
using System.Collections.Generic;

namespace JitHub.Services;

public static class PagedRefreshProjectionPolicy
{
    public static T[] Merge<T>(
        IEnumerable<T> incoming,
        IEnumerable<T> published,
        Func<T, string> keySelector,
        PagedDataCompleteness completeness)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(keySelector);

        List<T> result = [];
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        AddUnique(incoming, result, keys, keySelector);

        if (completeness is not PagedDataCompleteness.Complete)
        {
            AddUnique(published, result, keys, keySelector);
        }

        return [.. result];
    }

    public static T[] Merge<T>(
        IEnumerable<T> incoming,
        IEnumerable<T> published,
        Func<T, int, string> keySelector,
        PagedDataCompleteness completeness)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(keySelector);

        List<T> result = [];
        HashSet<string> keys = new(StringComparer.OrdinalIgnoreCase);
        AddUnique(incoming, result, keys, keySelector);

        if (completeness is not PagedDataCompleteness.Complete)
        {
            AddUnique(published, result, keys, keySelector);
        }

        return [.. result];
    }

    private static void AddUnique<T>(
        IEnumerable<T> source,
        ICollection<T> destination,
        ISet<string> keys,
        Func<T, string> keySelector)
    {
        foreach (T item in source)
        {
            string key = keySelector(item);
            if (!string.IsNullOrWhiteSpace(key) && keys.Add(key))
            {
                destination.Add(item);
            }
        }
    }

    private static void AddUnique<T>(
        IEnumerable<T> source,
        ICollection<T> destination,
        ISet<string> keys,
        Func<T, int, string> keySelector)
    {
        int ordinal = 0;
        foreach (T item in source)
        {
            string key = keySelector(item, ordinal++);
            if (!string.IsNullOrWhiteSpace(key) && keys.Add(key))
            {
                destination.Add(item);
            }
        }
    }
}
