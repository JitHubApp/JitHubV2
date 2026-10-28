using System;
using MarkdownRenderer.Theming;

namespace MarkdownRenderer.CodeBlocks;

/// <summary>
/// Identifies the visual highlighting applied to a code block. A block retains
/// only this small key after cache eviction, never the provider's full spans.
/// </summary>
internal readonly struct CodeBlockHighlightCacheKey(
    string? Language,
    ulong CodeHash,
    int CodeLength,
    MarkdownThemeVariant ThemeVariant,
    int ProviderIdentity,
    int ProviderRevision) : IEquatable<CodeBlockHighlightCacheKey>
{
    internal readonly string? Language = Language;
    internal readonly ulong CodeHash = CodeHash;
    internal readonly int CodeLength = CodeLength;
    internal readonly MarkdownThemeVariant ThemeVariant = ThemeVariant;
    internal readonly int ProviderIdentity = ProviderIdentity;
    internal readonly int ProviderRevision = ProviderRevision;

    // This implementation-only key is never printed, deconstructed or cloned.
    // Keep just its ordinal value equality; hashes are process-local cache hints.
    public bool Equals(CodeBlockHighlightCacheKey other) =>
        string.Equals(Language, other.Language, StringComparison.Ordinal) &&
        CodeHash == other.CodeHash && CodeLength == other.CodeLength &&
        ThemeVariant == other.ThemeVariant &&
        ProviderIdentity == other.ProviderIdentity && ProviderRevision == other.ProviderRevision;

    public override bool Equals(object? obj) => obj is CodeBlockHighlightCacheKey other && Equals(other);
    public override int GetHashCode() =>
        HashCode.Combine(Language, CodeHash, CodeLength, ThemeVariant, ProviderIdentity, ProviderRevision);

    public static bool operator ==(CodeBlockHighlightCacheKey left, CodeBlockHighlightCacheKey right) => left.Equals(right);
    public static bool operator !=(CodeBlockHighlightCacheKey left, CodeBlockHighlightCacheKey right) => !left.Equals(right);
}
