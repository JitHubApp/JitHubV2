using System;
using System.Collections.Generic;

namespace MarkdownRenderer.Theming;

/// <summary>The cache key for one contextual style query on a theme snapshot.</summary>
internal readonly struct ThemeStyleCacheKey : IEquatable<ThemeStyleCacheKey>
{
    private readonly int _hashCode;

    private ThemeStyleCacheKey(
        string elementKey,
        IReadOnlyList<string>? contextKeys,
        IReadOnlyList<string>? aliasKeys,
        string? language,
        string? state)
    {
        ElementKey = elementKey;
        ContextKeys = contextKeys;
        AliasKeys = aliasKeys;
        Language = language;
        State = state;
        _hashCode = ComputeHashCode(elementKey, contextKeys, aliasKeys, language, state);
    }

    private readonly string ElementKey;

    private readonly IReadOnlyList<string>? ContextKeys;

    private readonly IReadOnlyList<string>? AliasKeys;

    private readonly string? Language;

    private readonly string? State;

    internal static ThemeStyleCacheKey CreateLookup(
        string elementKey,
        IReadOnlyList<string>? contextKeys,
        IReadOnlyList<string>? aliasKeys,
        string? language,
        string? state)
        => new(elementKey, contextKeys, aliasKeys, language, state);

    internal ThemeStyleCacheKey CreateStableCopy()
        => new(
            ElementKey,
            CopySequence(ContextKeys),
            CopySequence(AliasKeys),
            Language,
            State);

    public bool Equals(ThemeStyleCacheKey other)
        => string.Equals(ElementKey, other.ElementKey, StringComparison.Ordinal) &&
           QualifierEquals(Language, other.Language) &&
           QualifierEquals(State, other.State) &&
           SequenceEqual(ContextKeys, other.ContextKeys) &&
           SequenceEqual(AliasKeys, other.AliasKeys);

    public override bool Equals(object? obj)
        => obj is ThemeStyleCacheKey other && Equals(other);

    public override int GetHashCode() => _hashCode;

    private static IReadOnlyList<string>? CopySequence(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
            return null;

        var copy = new string[values.Count];
        for (int i = 0; i < copy.Length; i++)
            copy[i] = values[i];
        return copy;
    }

    private static bool SequenceEqual(
        IReadOnlyList<string>? left,
        IReadOnlyList<string>? right)
    {
        int leftCount = left?.Count ?? 0;
        int rightCount = right?.Count ?? 0;
        if (leftCount != rightCount)
            return false;

        for (int i = 0; i < leftCount; i++)
        {
            if (!string.Equals(left![i], right![i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static int ComputeHashCode(
        string elementKey,
        IReadOnlyList<string>? contextKeys,
        IReadOnlyList<string>? aliasKeys,
        string? language,
        string? state)
    {
        unchecked
        {
            int hash = StringComparer.Ordinal.GetHashCode(elementKey);
            hash = (hash * 397) ^ GetSequenceHashCode(contextKeys);
            hash = (hash * 397) ^ GetSequenceHashCode(aliasKeys);
            hash = (hash * 397) ^ GetQualifierHashCode(language);
            hash = (hash * 397) ^ GetQualifierHashCode(state);
            return hash;
        }
    }

    private static bool QualifierEquals(string? left, string? right)
        => TrimQualifier(left).Equals(TrimQualifier(right), StringComparison.OrdinalIgnoreCase);

    private static int GetQualifierHashCode(string? value)
    {
        ReadOnlySpan<char> normalized = TrimQualifier(value);
        return normalized.IsEmpty
            ? 0
            : string.GetHashCode(normalized, StringComparison.OrdinalIgnoreCase);
    }

    private static ReadOnlySpan<char> TrimQualifier(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return ReadOnlySpan<char>.Empty;

        ReadOnlySpan<char> span = value.AsSpan();
        int start = 0;
        while (start < span.Length && char.IsWhiteSpace(span[start]))
            start++;

        int end = span.Length;
        while (end > start && char.IsWhiteSpace(span[end - 1]))
            end--;

        return span[start..end];
    }

    private static int GetSequenceHashCode(IReadOnlyList<string>? values)
    {
        int count = values?.Count ?? 0;
        unchecked
        {
            int hash = count;
            for (int i = 0; i < count; i++)
            {
                string? value = values![i];
                hash = (hash * 397) ^ (value is null ? 0 : StringComparer.Ordinal.GetHashCode(value));
            }

            return hash;
        }
    }
}
