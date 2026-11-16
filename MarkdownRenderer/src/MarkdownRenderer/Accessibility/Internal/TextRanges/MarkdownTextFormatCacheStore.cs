using System;
using System.Threading;
using MarkdownRenderer.Accessibility.Internal.Semantics;
using MarkdownRenderer.Theming;
using MarkdownRenderer.Theming.Internal;

namespace MarkdownRenderer.Accessibility.Internal.TextRanges;

/// <summary>
/// Owns the single format cache associated with an automation peer. Cache hits
/// are lock-free; identity changes rebuild once and publish an immutable value.
/// </summary>
internal sealed class MarkdownTextFormatCacheStore
{
    private readonly object _gate = new();
    private MarkdownTextFormatCache? _cache;
    private int _buildCount;

    internal int BuildCount => Volatile.Read(ref _buildCount);

    internal MarkdownTextFormatCache GetOrCreate(
        MarkdownSemanticDocument document,
        ThemeSnapshot? theme)
    {
        ArgumentNullException.ThrowIfNull(document);

        MarkdownTextFormatCache? cache = Volatile.Read(ref _cache);
        if (cache is not null && cache.Matches(document, theme))
            return cache;

        lock (_gate)
        {
            cache = _cache;
            if (cache is not null && cache.Matches(document, theme))
                return cache;

            cache = MarkdownTextFormatCache.Create(document, theme);
            Interlocked.Increment(ref _buildCount);
            Volatile.Write(ref _cache, cache);
            return cache;
        }
    }

    internal void Invalidate() => Volatile.Write(ref _cache, null);
}
