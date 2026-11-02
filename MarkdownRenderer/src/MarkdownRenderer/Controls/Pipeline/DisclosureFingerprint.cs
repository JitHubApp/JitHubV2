using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Controls.Pipeline;

/// <summary>
/// An order-independent FNV-1a fingerprint of the disclosure (details)
/// states, so the shared layout metrics cache keys on what is expanded.
/// </summary>
internal static class DisclosureFingerprint
{
    internal static ulong Compute(IReadOnlyDictionary<string, bool> disclosureStates)
    {
        if (disclosureStates.Count == 0)
            return 0;

        const ulong prime = 1099511628211UL;
        var keys = new List<string>(disclosureStates.Keys);
        keys.Sort(StringComparer.Ordinal);
        ulong hash = 14695981039346656037UL;
        foreach (string key in keys)
        {
            hash ^= Fnv1A64(key);
            hash *= prime;
            hash ^= disclosureStates[key] ? 1UL : 0UL;
            hash *= prime;
        }

        return hash;
    }

    private static ulong Fnv1A64(string value)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (var ch in value)
        {
            hash ^= ch;
            hash *= prime;
        }

        return hash;
    }
}
