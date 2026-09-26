using MarkdownRenderer.Document;

namespace MarkdownRenderer.Accessibility;

/// <summary>Creates deterministic UIA identities for virtualized hosted content.</summary>
internal static class HostedElementAutomationIdentity
{
    internal static string Create(string factoryKey, SourceSpan sourceRange, int blockIndex)
    {
        // Do not use string.GetHashCode: its randomized seed would make UIA
        // identities change across processes. FNV-1a is compact, deterministic,
        // and sufficient here because the source range and block ordinal are
        // also part of the identity.
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offsetBasis;
        for (int i = 0; i < factoryKey.Length; i++)
        {
            char value = factoryKey[i];
            hash = (hash ^ (byte)value) * prime;
            hash = (hash ^ (byte)(value >> 8)) * prime;
        }

        return $"MarkdownHosted-{sourceRange.Start:x8}-{sourceRange.Length:x8}-{blockIndex:x8}-{hash:x16}";
    }
}
