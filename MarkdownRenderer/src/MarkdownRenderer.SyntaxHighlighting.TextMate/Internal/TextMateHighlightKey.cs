using System;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate.Internal;

/// <summary>
/// Identifies one provider highlight: language, exact code, theme variant and
/// provider revision, compared ordinally with a precomputed hash.
/// </summary>
internal readonly struct TextMateHighlightKey : IEquatable<TextMateHighlightKey>
{
    private readonly int _hashCode;

    public TextMateHighlightKey(
        string languageId,
        string code,
        TextMateGrammarThemeVariant themeVariant,
        int revision)
    {
        LanguageId = languageId;
        Code = code;
        ThemeVariant = themeVariant;
        Revision = revision;
        _hashCode = HashCode.Combine(
            StringComparer.Ordinal.GetHashCode(languageId),
            StringComparer.Ordinal.GetHashCode(code),
            themeVariant,
            revision);
    }

    public string LanguageId { get; }

    public string Code { get; }

    public TextMateGrammarThemeVariant ThemeVariant { get; }

    public int Revision { get; }

    public bool Equals(TextMateHighlightKey other) =>
        _hashCode == other._hashCode &&
        Revision == other.Revision &&
        ThemeVariant == other.ThemeVariant &&
        string.Equals(LanguageId, other.LanguageId, StringComparison.Ordinal) &&
        string.Equals(Code, other.Code, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is TextMateHighlightKey other && Equals(other);

    public override int GetHashCode() => _hashCode;
}
