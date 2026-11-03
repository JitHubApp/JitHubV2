using System.Threading;
using System.Threading.Tasks;

namespace MarkdownRenderer.SyntaxHighlighting.TextMate;

/// <summary>
/// Supplies TextMate grammar resources and performs tokenization for the lean
/// MarkdownRenderer TextMate integration package.
/// </summary>
/// <remarks>
/// The contract deliberately exposes no TextMateSharp types. Providers may use
/// TextMateSharp, another compatible engine, or application-owned grammars.
/// Implementations must perform expensive work away from the UI thread and
/// observe <see cref="CancellationToken"/> between lines or equivalent units of work.
/// </remarks>
public interface ITextMateGrammarProvider
{
    /// <summary>Revision used to invalidate cached highlighting results.</summary>
    int Revision => 0;

    /// <summary>
    /// Highlights a code block, returns an empty result when the language is not
    /// supported, or returns <see langword="null"/> when work is canceled.
    /// </summary>
    ValueTask<TextMateGrammarHighlightResult?> HighlightAsync(
        TextMateGrammarHighlightRequest request,
        CancellationToken cancellationToken);
}
