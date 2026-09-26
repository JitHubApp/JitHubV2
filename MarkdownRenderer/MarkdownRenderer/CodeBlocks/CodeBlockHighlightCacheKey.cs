namespace MarkdownRenderer.CodeBlocks;

/// <summary>
/// Identifies the visual highlighting applied to a code block. A block retains
/// only this small key after cache eviction, never the provider's full spans.
/// </summary>
internal readonly record struct CodeBlockHighlightCacheKey(
    string? Language,
    ulong CodeHash,
    int CodeLength,
    CodeBlockThemeVariant ThemeVariant,
    int ProviderIdentity,
    int ProviderRevision);
