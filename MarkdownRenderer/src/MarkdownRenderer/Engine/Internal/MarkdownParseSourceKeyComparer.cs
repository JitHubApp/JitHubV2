using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Engine.Internal;

/// <summary>
/// Compares parse-cache keys by their precomputed hash first, then by source
/// identity or ordinal content.
/// </summary>
internal sealed class MarkdownParseSourceKeyComparer : IEqualityComparer<MarkdownParseSourceKey>
{
    internal static MarkdownParseSourceKeyComparer Instance { get; } = new();

    public bool Equals(MarkdownParseSourceKey x, MarkdownParseSourceKey y) =>
        x.OrdinalHashCode == y.OrdinalHashCode &&
        x.DeferOffscreenScenes == y.DeferOffscreenScenes &&
        (ReferenceEquals(x.Source, y.Source) ||
         string.Equals(x.Source, y.Source, StringComparison.Ordinal));

    public int GetHashCode(MarkdownParseSourceKey value) => value.OrdinalHashCode;
}
