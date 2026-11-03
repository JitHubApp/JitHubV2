using System;
using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

/// <summary>Input passed to an <see cref="ITextMateGrammarProvider"/>.</summary>
public sealed class TextMateGrammarHighlightRequest
{
    /// <summary>Creates a validated highlighting request.</summary>
    public TextMateGrammarHighlightRequest(
        string languageId,
        string code,
        TextMateGrammarThemeVariant themeVariant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(languageId);
        ArgumentNullException.ThrowIfNull(code);

        LanguageId = languageId;
        Code = code;
        ThemeVariant = themeVariant;
    }

    /// <summary>Normalized TextMate language identifier.</summary>
    public string LanguageId { get; }

    /// <summary>Code to tokenize.</summary>
    public string Code { get; }

    /// <summary>Requested editor theme family.</summary>
    public TextMateGrammarThemeVariant ThemeVariant { get; }
}
