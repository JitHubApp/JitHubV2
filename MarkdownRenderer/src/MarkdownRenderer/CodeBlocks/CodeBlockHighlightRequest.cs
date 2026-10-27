using System.Threading;

namespace MarkdownRenderer.CodeBlocks;

/// <summary>
/// Input passed to an optional syntax-highlighting provider.
/// </summary>
public sealed class CodeBlockHighlightRequest
{
    /// <summary>Initializes a new highlighting request.</summary>
    public CodeBlockHighlightRequest(
        string? language,
        string code,
        CodeBlockThemeVariant themeVariant,
        CancellationToken cancellationToken)
    {
        Language = language;
        Code = code ?? string.Empty;
        ThemeVariant = themeVariant;
        CancellationToken = cancellationToken;
    }

    /// <summary>Normalized language identifier from the fence info string, if any.</summary>
    public string? Language { get; }

    /// <summary>Raw displayed code text, excluding markdown fences.</summary>
    public string Code { get; }

    /// <summary>Resolved renderer theme variant for choosing token colors.</summary>
    public CodeBlockThemeVariant ThemeVariant { get; }

    /// <summary>Cancellation token for abandoning stale highlight work.</summary>
    public CancellationToken CancellationToken { get; }
}
