using System;

namespace MarkdownRenderer.Engine.Internal;

/// <summary>
/// Parse-cache key: the source text, its ordinal hash computed once, and the
/// presentation mode, which keeps eager and progressive documents apart.
/// </summary>
internal readonly struct MarkdownParseSourceKey
{
    internal MarkdownParseSourceKey(string source, int ordinalHashCode, bool deferOffscreenScenes)
    {
        Source = source;
        OrdinalHashCode = ordinalHashCode;
        DeferOffscreenScenes = deferOffscreenScenes;
    }

    internal readonly string Source;
    internal readonly int OrdinalHashCode;
    internal readonly bool DeferOffscreenScenes;

    internal static MarkdownParseSourceKey Create(string source, bool deferOffscreenScenes)
        => new(source, StringComparer.Ordinal.GetHashCode(source), deferOffscreenScenes);
}
